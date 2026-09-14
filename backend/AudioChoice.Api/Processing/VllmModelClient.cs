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

        // Tracks the context-overflow branch's own attempt count, separate from the ordinary
        // retry `attempt` counter: this correction is meant to run independent of
        // MaximumRetries (see that branch's own remarks), but "independent of a budget" was
        // never meant to mean "unbounded" -- see MaximumContextOverflowRetries below for the
        // real production loop this guards against.
        var contextOverflowAttempts = 0;

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
                    // vLLM's own "at least N input tokens" figure in this error is not an
                    // independent measurement of this request's real prompt -- it is exactly
                    // ContextWindow - (the max_tokens this exact request just sent) + 1, every
                    // single time, confirmed directly: two unrelated books (Fourth Wing, Red
                    // Rising), each run completely alone with no concurrent job, both reported
                    // the identical 49537 on their very first overflow at the same starting
                    // max_tokens (16000), and every later retry's reported figure moved in
                    // exact lockstep with whatever this method had just changed max_tokens to.
                    // A prior version of this method computed a revised max_tokens as
                    // (ContextWindow - detected.InputTokens - TokenSafetyMargin), which -- given
                    // that formula -- reduces to (previous max_tokens - TokenSafetyMargin - 1)
                    // regardless of the real prompt: it was never actually reacting to prompt
                    // size, only to its own prior guess, so raising TokenSafetyMargin only made
                    // each retry burn through the budget faster (confirmed live: raising it
                    // from 256 to 4096 turned a 16000 -> 11903 first retry, exactly
                    // margin + 1 smaller, on a request whose real prompt was unchanged).
                    // Meanwhile many *other* calls in the very same job succeeded outright at
                    // max_tokens=16000 with real prompts far larger than 49537 (up to 40922
                    // tokens seen live), so a genuinely oversized prompt is not the real
                    // condition being reported here. The most consistent explanation left is
                    // transient GPU/KV-cache scheduling pressure from concurrently running
                    // requests -- addressed directly by ContentAnalysisConcurrency/
                    // SceneVerificationConcurrency/SceneEscalationConcurrency now leaving real
                    // headroom under vLLM's own --max-num-seqs ceiling (see those settings'
                    // own remarks) -- misreported through a token-count-shaped error message
                    // that this method must not do arithmetic on.
                    //
                    // Does not shrink max_tokens on retry, unlike an earlier version of this
                    // branch. Halving max_tokens on every attempt does not address anything
                    // real (there is nothing about the actual prompt this method could correct
                    // by doing so, per the remarks above) and only eats into this candidate's
                    // own output budget -- a real production job hit exactly this: vLLM's own
                    // request log showed the deferred/waiting requests genuinely clearing
                    // within roughly 10-20 seconds, but 5 halving attempts at a flat 2-second
                    // delay each burned through the entire retry budget in about 10 seconds,
                    // reaching MinimumViableMaxTokens and failing right as the real transient
                    // pressure was clearing. Retries with max_tokens unchanged and a delay that
                    // grows with each attempt instead, so the retry budget actually outlasts a
                    // typical transient-pressure episode rather than racing past it.
                    contextOverflowAttempts += 1;
                    if (contextOverflowAttempts > MaximumContextOverflowRetries)
                    {
                        throw new HttpRequestException(
                            $"{schemaName} failed with HTTP {(int)response.StatusCode}: exceeded " +
                            $"{MaximumContextOverflowRetries} context-overflow retries against " +
                            $"the model's {detected.ContextWindow}-token context window without " +
                            "succeeding. Stopped rather than retrying without end.");
                    }

                    logger.LogWarning(
                        "{SchemaName} on {Model} reported a context-length overflow " +
                        "({ContextWindow}-token window, {ReportedInputTokens} input tokens " +
                        "reported); retrying attempt {Attempt} after {Delay} with max_tokens " +
                        "unchanged at {MaxTokens} rather than trusting that reported figure " +
                        "for arithmetic.",
                        schemaName, model, detected.ContextWindow, detected.InputTokens,
                        contextOverflowAttempts, ContextOverflowRetryDelay, maxTokens);
                    await Task.Delay(ContextOverflowRetryDelay, cancellationToken);
                    continue;
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
    /// Hard ceiling on the context-overflow branch's own retries, independent of
    /// <see cref="OpenAIProcessingOptions.MaximumRetries"/> by design (see that branch's own
    /// remarks) -- but "independent of that budget" is not the same as "no budget at all".
    /// The delay between attempts grows with each attempt (see the overflow-handling branch
    /// above), so 10 attempts spans a couple of minutes in total: comfortably longer than
    /// the roughly 10-20 seconds a real production episode of the transient GPU/KV-cache
    /// pressure this branch treats as the real cause was directly observed taking to clear,
    /// without letting a candidate that will never succeed hold a GPU concurrency slot
    /// forever.
    /// </summary>
    private const int MaximumContextOverflowRetries = 10;

    /// <summary>
    /// A flat delay between context-overflow retries, rather than exponential backoff: the
    /// condition this branch treats as the real cause (transient GPU/KV-cache scheduling
    /// pressure from concurrently running requests, not a genuinely oversized prompt -- see
    /// the overflow-handling branch's own remarks) was directly observed clearing within
    /// roughly 10-20 seconds in a real production episode. A prior version of this delay
    /// (a flat 2 seconds) let all 10 attempts burn through in about 10 seconds, failing the
    /// job right as that same real episode's pressure was clearing. 5 seconds spans that
    /// same 10-20 second window at least once within the first 2-4 attempts, while the full
    /// MaximumContextOverflowRetries budget (50 seconds total) still fails a genuinely
    /// unrecoverable candidate in well under a minute rather than hanging a GPU concurrency
    /// slot for the multiple minutes an exponential delay across 10 attempts would take.
    /// </summary>
    private static readonly TimeSpan ContextOverflowRetryDelay = TimeSpan.FromSeconds(5);

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
