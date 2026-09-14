using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Reaches a self-hosted vLLM server through its OpenAI-compatible chat completions API,
/// with a strict JSON schema response format.
/// </summary>
/// <remarks>
/// Sibling to <see cref="OpenAIResponsesModelClient"/> and <see cref="BedrockConverseModelClient"/>:
/// same seam (<see cref="IAnalysisModelClient"/>), same contract, different transport. Added so a
/// tier can name a locally-hosted open-weight model (e.g. Qwen3.6-27B served by vLLM) instead of
/// an OpenAI or Bedrock model, the same way <see cref="RoutingAnalysisModelClient"/> already lets
/// a tier move between OpenAI and Bedrock by configuration alone.
///
/// vLLM's OpenAI-compatible server exposes /v1/chat/completions rather than OpenAI's own
/// /v1/responses endpoint, so the request/response shapes differ from
/// <see cref="OpenAIResponsesModelClient"/> even though both enforce the same strict json_schema
/// response format. This class exists specifically because that shape difference is real, not
/// because the two providers' policies differ -- the taxonomy, confidence floors, and every other
/// decision this pipeline makes stay exactly where <see cref="IAnalysisModelClient"/>'s own
/// remarks say they must: in <see cref="OpenAIContentAnalysisProvider"/>, never here.
///
/// A locally-hosted model may emit a visible "thinking" block before its actual JSON answer
/// (observed with Qwen3.6 during this pipeline's own evaluation -- see
/// scripts/eval/run_scene_verification_eval.py's extract_json_object for the same problem solved
/// for shadow-testing). <see cref="ExtractJsonObject"/> solves it the same way here: find the last
/// complete top-level JSON object in the response text, rather than trusting the whole response
/// as the answer.
/// </remarks>
public sealed class VllmModelClient(
    HttpClient client,
    OpenAIProcessingOptions options,
    ILogger<VllmModelClient> logger) : IAnalysisModelClient
{
    public string ProviderName => "vllm";

    public async Task<AnalysisModelResponse> CompleteJson(
        string model,
        string input,
        string schemaName,
        JsonObject schema,
        CancellationToken cancellationToken)
    {
        // Starts at the configured budget and shrinks on a real, measured overflow (see the
        // 400-handling branch below) rather than a fixed value. A coalesced scene-verification
        // candidate can legitimately span a very wide, unbounded stretch of a real book -- far
        // beyond what this session's own small eval excerpts exercised -- and OpenAI's models
        // this pipeline was originally built against have a 1M-token context that made this a
        // non-issue there. A self-hosted model's much smaller window (here, 65536 tokens) makes
        // it a real one, and no fixed max_tokens value is safe against every candidate a real
        // book can produce.
        var maxTokens = options.VllmMaxTokens;
        // 0 (greedy decoding) for every verification/escalation call (Terra/Sol), which must
        // stay reproducible for their own checkpointing and majority vote across independent
        // calls -- except the first-pass content-analysis call itself, which gets Qwen's own
        // documented non-thinking/instruct sampling temperature instead. Real production
        // evidence this exists to close: disabling Qwen3.6's default reasoning (see
        // BaseAnalysisPromptVersion's own remarks on why that was done) measurably reduced
        // first-pass recall specifically on subtler, more ambiguous sexual-content cues --
        // a real rescan of an already-verified book lost the bulk of its sexual-content
        // events (627 -> 163) even though its transcript had full word timing and nothing
        // else about the pipeline had changed. Qwen's own model card documents that its
        // instruct/non-thinking mode was tuned and evaluated against temperature 0.7, not
        // greedy decoding -- running it at 0 with reasoning off combines two departures from
        // how the model was actually tuned, which a community-documented failure mode
        // describes as producing output that is "not incorrect, just suboptimal in ways that
        // don't surface as errors" (exactly the shape of this regression: no errors, no
        // failures, just quietly fewer real detections). Scoped to only this one call by
        // schemaName rather than raised everywhere: Terra and Sol's own reproducibility, and
        // this pipeline's checkpoint-based cost control, both depend on every other call
        // staying deterministic.
        //
        // Captured as its own named base rather than only assigned to `temperature`: the
        // parse-failure retry branch further down nudges temperature upward from wherever it
        // started, up to a small ceiling above that starting point -- a ceiling that was
        // written assuming every call starts at exactly 0. Without this base, a retry on this
        // one call would clamp back down toward that old absolute ceiling instead of climbing
        // from 0.7, silently undoing this fix on the very calls that already needed help once.
        var baseTemperature = schemaName == "audiochoice_scan_events" ? 0.7 : 0.0;
        var temperature = baseTemperature;

        // Tracks the context-overflow branch's own attempt count and the input-token count
        // it was last told about. Kept separate from the ordinary retry `attempt` counter:
        // this correction is meant to run independent of MaximumRetries (see that branch's
        // own remarks), but "independent of a budget" was never meant to mean "unbounded" --
        // see MaximumContextOverflowRetries below for the real production loop this guards
        // against.
        var contextOverflowAttempts = 0;
        int? previousOverflowInputTokens = null;

        for (var attempt = 0; ; attempt += 1)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "user", ["content"] = input }),
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens,
                // Qwen3.6 reasons by default: it emits a long, invisible "thinking" block
                // before every JSON answer, even for a plain accept/reject call, which is
                // why a single Terra/Sol verification call was measured taking 300-500+
                // seconds in real production use -- an order of magnitude slower than an
                // answer this small should ever need, and the actual reason retries on a
                // failed call were taking so long to exhaust that a whole scan's turnaround
                // became unworkable. This pipeline's own answers are short, structured
                // yes/no-plus-fields decisions with a strict schema already enforcing the
                // shape of the output; the visible reasoning trace it was paying for was
                // never itself returned to a listener or used by any code here. Disabling it
                // per vLLM's own documented Qwen3 mechanism removes that entire block from
                // every call's latency budget, not just the failing ones.
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = schemaName,
                        // Deep-cloned on every attempt: a JsonNode may only ever belong to one
                        // parent, and this loop can rebuild the request body more than once
                        // (the adaptive max_tokens retry below). Reusing the same schema
                        // instance across attempts threw "The node already has a parent" on
                        // the very first retry this fix was meant to enable.
                        ["schema"] = schema.DeepClone(),
                        ["strict"] = true
                    }
                }
            };
            var payload = body.ToJsonString();

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            if (!string.IsNullOrWhiteSpace(options.VllmApiKey))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.VllmApiKey);
            }
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken);
            }
            // Same shape as OpenAIResponsesModelClient's own retry: a client-side timeout or
            // dropped connection throws before any status code exists to inspect, and must not
            // be retried when the caller's own cancellation is what actually fired.
            catch (Exception error) when (
                error is TaskCanceledException or HttpRequestException or IOException &&
                !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= options.MaximumRetries)
                {
                    throw new HttpRequestException(
                        $"{schemaName} did not respond within the configured timeout after " +
                        $"{attempt + 1} attempt(s).", error);
                }

                var timeoutDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(
                    error,
                    "{SchemaName} request to {Model} timed out or failed to connect; retry {Attempt} after {Delay}.",
                    schemaName, model, attempt + 1, timeoutDelay);
                await Task.Delay(timeoutDelay, cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    var root = JsonNode.Parse(responseJson)
                        ?? throw new InvalidOperationException(
                            $"{schemaName} returned invalid JSON.");
                    var content = root["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                        ?? throw new InvalidOperationException(
                            $"{schemaName} response did not contain message content.");
                    var answer = ExtractJsonObject(content);
                    if (answer is null)
                    {
                        // A real production failure this retry exists for: a successful
                        // (HTTP 200) response whose content never contained a complete,
                        // parseable JSON object, observed specifically under real concurrent
                        // load -- the identical batch, sent in isolation with no other
                        // request competing for the GPU at the same time, answered cleanly
                        // and quickly. This is treated as a transient serving hiccup, the
                        // same way a timeout is, rather than evidence the batch's own content
                        // is unanswerable: unlike a genuine context-length overflow (handled
                        // separately below, with its own unlimited-attempts recovery), this
                        // has no measured cause to correct for, so it shares the ordinary
                        // MaximumRetries budget instead of retrying without limit.
                        if (attempt >= options.MaximumRetries)
                        {
                            throw new InvalidOperationException(
                                $"{schemaName} response content did not contain a parseable " +
                                "JSON object after " + (attempt + 1) + " attempt(s) -- the " +
                                "model may have been truncated mid-reasoning; check " +
                                "AudioChoice:OpenAI:VllmMaxTokens. Last response (truncated): " +
                                content[..Math.Min(content.Length, 500)]);
                        }

                        // A real production case this covers, confirmed by direct
                        // observation: at temperature 0 (greedy decoding) the same request,
                        // sent completely unchanged including max_tokens, reproduced the
                        // identical corrupted output on every one of 7 attempts across two
                        // separate scan jobs -- the same single stray non-English token
                        // appeared mid-string at the exact same character position every
                        // time, breaking the JSON string's own closing quote. This ruled out
                        // an earlier theory that nudging max_tokens alone (to shift which
                        // requests share a vLLM batch step) would be enough: greedy decoding
                        // means the token sequence up to the point of failure does not depend
                        // on max_tokens at all, so that nudge changed nothing about the
                        // tokens actually generated and this candidate kept failing at
                        // exactly the same spot regardless. What actually varies the outcome
                        // is temperature itself -- a small positive value breaks the greedy
                        // decode's own determinism, giving the retry a real chance at a
                        // different (and very likely correct) token where greedy decoding
                        // picked a bad one. Kept small and reverted every attempt that
                        // doesn't need it (see the temperature reset above the retry loop)
                        // so this remains a targeted escape from a reproducible bad decode,
                        // not a general loosening of an otherwise-deterministic pipeline.
                        // Clamped relative to baseTemperature, not to an absolute ceiling: a
                        // call already starting above 0 (see baseTemperature's own remarks)
                        // must keep climbing from where it started, never clamp back down
                        // toward a ceiling sized for a base of 0.
                        temperature = Math.Min(
                            baseTemperature + 0.4, temperature + ParseRetryTemperatureStep);
                        maxTokens = Math.Max(
                            MinimumViableMaxTokens, maxTokens - ParseRetryTokenNudge);

                        var parseRetryDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        logger.LogWarning(
                            "{SchemaName} on {Model} returned a successful response with no " +
                            "parseable JSON content; retry {Attempt} after {Delay}, with " +
                            "temperature raised to {Temperature} to escape a reproducible " +
                            "greedy-decode error. Response (truncated): {Content}",
                            schemaName, model, attempt + 1, parseRetryDelay, temperature,
                            content[..Math.Min(content.Length, 500)]);
                        await Task.Delay(parseRetryDelay, cancellationToken);
                        continue;
                    }
                    return new AnalysisModelResponse(
                        answer.ToJsonString(),
                        root["usage"]?["prompt_tokens"]?.GetValue<long>(),
                        root["usage"]?["completion_tokens"]?.GetValue<long>());
                }

                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // A real, measured context-length overflow -- vLLM's own error names the exact
                // input token count it counted. Shrink max_tokens to what is actually left of
                // the model's context window and retry, rather than fail a whole real scan job
                // over a candidate a fixed budget could never have safely covered. Retried
                // whenever it can genuinely help (a positive token budget remains), independent
                // of MaximumRetries -- this is a size correction, not evidence of a transient
                // fault, so it should not compete with that budget for attempts.
                var overflow = TryParseContextLengthOverflow(errorBody);
                if (overflow is { } detected)
                {
                    // vLLM's own "at least N input tokens" figure is not a perfectly stable
                    // count of one unchanged prompt under concurrent load. A real production
                    // rescan, run in complete isolation with no other job in flight, was
                    // directly observed retrying a single call with that number climbing for
                    // several consecutive attempts (49537 -> 49794 -> 50051...) before
                    // genuinely converging; separately, several different concurrent
                    // batches (confirmed distinct by their own separate HTTP response
                    // latencies) were observed independently landing on nearly the same
                    // count purely because their underlying text windows happened to be
                    // similarly sized. An earlier version of this method tried to detect a
                    // non-shrinking retry and give up early on the theory that it could
                    // never converge -- removed, because that check produced real false
                    // positives against candidates that went on to converge normally on a
                    // later attempt, and because every one of those non-shrinking attempts
                    // still computed a perfectly viable revised max_tokens well above
                    // MinimumViableMaxTokens the whole time, so continuing was always safe.
                    // The only thing standing between this and a genuinely unbounded loop is
                    // MaximumContextOverflowRetries below, which fires regardless of which
                    // direction any individual attempt's reported count moved.
                    previousOverflowInputTokens = detected.InputTokens;

                    contextOverflowAttempts += 1;
                    if (contextOverflowAttempts > MaximumContextOverflowRetries)
                    {
                        throw new HttpRequestException(
                            $"{schemaName} failed with HTTP {(int)response.StatusCode}: exceeded " +
                            $"{MaximumContextOverflowRetries} context-overflow retries against " +
                            $"the model's {detected.ContextWindow}-token context window without " +
                            "converging on a viable max_tokens. Stopped rather than retrying " +
                            "without end.");
                    }

                    var revisedMaxTokens = detected.ContextWindow - detected.InputTokens - TokenSafetyMargin;
                    if (revisedMaxTokens > MinimumViableMaxTokens && revisedMaxTokens < maxTokens)
                    {
                        logger.LogWarning(
                            "{SchemaName} on {Model} exceeded its {ContextWindow}-token context " +
                            "window ({InputTokens} input tokens counted); retrying with " +
                            "max_tokens reduced from {PreviousMaxTokens} to {RevisedMaxTokens}.",
                            schemaName, model, detected.ContextWindow, detected.InputTokens,
                            maxTokens, revisedMaxTokens);
                        maxTokens = revisedMaxTokens;
                        continue;
                    }

                    // Input alone leaves no room for a viable answer even at the smallest
                    // reasonable output budget. This candidate cannot be served by this model
                    // at its current context window at all -- failing loudly here, rather than
                    // returning a budget too small to hold a real answer, is what keeps this
                    // from being silently misread as "the model had nothing to say."
                    throw new HttpRequestException(
                        $"{schemaName} failed with HTTP {(int)response.StatusCode}: the " +
                        $"prompt alone uses {detected.InputTokens} of the model's " +
                        $"{detected.ContextWindow}-token context window, leaving no viable " +
                        "room for an answer. This candidate is too large for the local " +
                        "model's context window regardless of max_tokens.");
                }

                // Same retry rule as OpenAIResponsesModelClient: only a rate limit or a server
                // fault is worth repeating.
                if (attempt >= options.MaximumRetries ||
                    (response.StatusCode != HttpStatusCode.TooManyRequests &&
                     (int)response.StatusCode < 500))
                {
                    throw new HttpRequestException(
                        $"{schemaName} failed with HTTP {(int)response.StatusCode}: {errorBody}");
                }

                var delay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(
                    "{SchemaName} retry {Attempt} on {Model} after {Delay}.",
                    schemaName, attempt + 1, model, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Kept below the model's true remaining headroom on purpose: vLLM's own count of "input
    /// tokens" in its error message does not necessarily include every token the chat template
    /// itself adds (role markers, special tokens), so retrying at the exact reported boundary
    /// risks a second, needless overflow on the same candidate.
    /// </summary>
    /// <remarks>
    /// Raised from 256 after directly reproducing a real production failure twice, on two
    /// unrelated books (Fourth Wing, Red Rising), each on a freshly restarted vLLM instance
    /// with zero concurrent load: the exact same request body -- same input text, only
    /// max_tokens differs between attempts -- had its reported "input tokens" figure climb by
    /// a fixed +257 on every single retry (49537 -> 49794 -> 50051 -> ...), exhausting all of
    /// MaximumContextOverflowRetries without ever converging. The input text cannot itself
    /// grow between retries, so that number is not a real measurement drifting upward; it is
    /// this server's own token accounting for one unchanged prompt disagreeing with itself.
    /// This server's own startup log names the likely cause: Qwen3.6 is a hybrid
    /// Mamba/attention architecture, and vLLM logs it padding the mamba page size against the
    /// attention block size at load time. Changing max_tokens changes how many KV-cache blocks
    /// a request reserves, and for a hybrid architecture that can shift how many tokens get
    /// counted against this model specifically -- so the previous 256-token margin's own
    /// correction (shrink max_tokens, retry) was the trigger re-triggering the same drift,
    /// never a fix converging on one. A margin comfortably larger than the worst drift actually
    /// observed (2570 tokens, accumulated over all 10 retries in both real failures) is meant to
    /// let the very first correction absorb that drift outright, rather than needing the
    /// iterative shrink to find a moving target.
    /// </remarks>
    private const int TokenSafetyMargin = 4096;

    /// <summary>
    /// Hard ceiling on the context-overflow branch's own retries, independent of
    /// <see cref="OpenAIProcessingOptions.MaximumRetries"/> by design (see that branch's own
    /// remarks) -- but "independent of that budget" is not the same as "no budget at all".
    /// A genuine size correction against a stable input-token count converges in one or two
    /// attempts; this exists only to stop a candidate whose reported input size will not
    /// stop drifting from consuming a GPU concurrency slot forever.
    /// </summary>
    private const int MaximumContextOverflowRetries = 10;

    /// <summary>
    /// Below this, a revised max_tokens is not worth attempting: this pipeline's own JSON
    /// answers (a handful of fields per candidate) need meaningfully more than a trivial
    /// budget to complete, especially once a locally-hosted reasoning model's visible
    /// thinking block is accounted for.
    /// </summary>
    private const int MinimumViableMaxTokens = 512;

    /// <summary>
    /// How much a parse-failure retry shrinks max_tokens by. Kept as a small secondary
    /// adjustment alongside the temperature increase (see that field's own remarks): on its
    /// own this did not resolve a real reproducible greedy-decode failure, since greedy
    /// decoding's token sequence does not depend on max_tokens up to the point of failure --
    /// but once temperature is no longer exactly 0, a slightly different budget is one more
    /// small variable working in the retry's favor rather than against it.
    /// </summary>
    private const int ParseRetryTokenNudge = 137;

    /// <summary>
    /// How much a parse-failure retry raises temperature by, each attempt, from its normal 0.
    /// </summary>
    /// <remarks>
    /// This is the fix that actually resolves a genuinely reproducible bad decode: a real
    /// production candidate reproduced the identical corrupted output at temperature 0 on 7
    /// consecutive attempts across two separate scan jobs, because greedy decoding is a pure
    /// function of the prompt and model weights alone -- an unchanged request has nothing
    /// left to vary the outcome. Every other field in this pipeline's design deliberately
    /// keeps temperature at 0 for genuine determinism (repeatable checkpoints, comparable
    /// eval runs); this is the one narrow exception, applied only after a parse failure has
    /// already happened, applied by the smallest amount that still changes the sampled token,
    /// and capped low enough that it does not turn this into a materially different model
    /// than the one every other request in this pipeline reaches.
    /// </remarks>
    private const double ParseRetryTemperatureStep = 0.1;

    private static readonly Regex ContextLengthOverflowPattern = new(
        @"maximum context length is (?<window>\d+) tokens\. However, you requested (?<requested>\d+) output tokens and your prompt contains at least (?<input>\d+) input tokens",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal readonly record struct ContextLengthOverflow(int ContextWindow, int InputTokens);

    /// <summary>
    /// Reads vLLM's own context-length-exceeded error message for the real counts it already
    /// computed, rather than estimating them independently on the client side (a second token
    /// count here would not even use the same tokenizer the server measured against).
    /// </summary>
    internal static ContextLengthOverflow? TryParseContextLengthOverflow(string errorBody)
    {
        var match = ContextLengthOverflowPattern.Match(errorBody);
        if (!match.Success) return null;
        return new ContextLengthOverflow(
            int.Parse(match.Groups["window"].Value),
            int.Parse(match.Groups["input"].Value));
    }

    /// <summary>
    /// Finds the last complete top-level JSON object in <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// A locally-hosted reasoning model may prepend a visible "thinking" block -- prose, not
    /// JSON -- before its actual structured answer, even when a strict response_format is
    /// requested. Scanning for balanced braces and keeping only the last complete object found
    /// is the same defense this pipeline's own eval harness already uses for shadow-testing
    /// (scripts/eval/run_scene_verification_eval.py's extract_json_object); ported here so
    /// production behaves the same way as the tool that qualified this model, rather than
    /// trusting the raw response content as-is the way OpenAI's schema-enforced Responses API
    /// safely could.
    /// </remarks>
    internal static JsonNode? ExtractJsonObject(string text)
    {
        var depth = 0;
        var start = -1;
        var candidates = new List<string>();
        for (var index = 0; index < text.Length; index += 1)
        {
            var current = text[index];
            if (current == '{')
            {
                if (depth == 0) start = index;
                depth += 1;
            }
            else if (current == '}' && depth > 0)
            {
                depth -= 1;
                if (depth == 0 && start >= 0)
                {
                    candidates.Add(text[start..(index + 1)]);
                }
            }
        }

        for (var index = candidates.Count - 1; index >= 0; index -= 1)
        {
            try
            {
                return JsonNode.Parse(candidates[index]);
            }
            catch (System.Text.Json.JsonException)
            {
                // Not the answer -- keep looking at earlier candidates.
            }
        }

        return null;
    }
}
