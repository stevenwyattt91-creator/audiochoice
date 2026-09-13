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

        for (var attempt = 0; ; attempt += 1)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "user", ["content"] = input }),
                ["temperature"] = 0,
                ["max_tokens"] = maxTokens,
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

                        var parseRetryDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        logger.LogWarning(
                            "{SchemaName} on {Model} returned a successful response with no " +
                            "parseable JSON content; retry {Attempt} after {Delay}. Response " +
                            "(truncated): {Content}",
                            schemaName, model, attempt + 1, parseRetryDelay,
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
    private const int TokenSafetyMargin = 256;

    /// <summary>
    /// Below this, a revised max_tokens is not worth attempting: this pipeline's own JSON
    /// answers (a handful of fields per candidate) need meaningfully more than a trivial
    /// budget to complete, especially once a locally-hosted reasoning model's visible
    /// thinking block is accounted for.
    /// </summary>
    private const int MinimumViableMaxTokens = 512;

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
