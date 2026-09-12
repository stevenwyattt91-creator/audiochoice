using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

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
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = input }),
            ["temperature"] = 0,
            // vLLM's own generous default max_tokens can leave a long-reasoning model like
            // Qwen3.6 truncated mid-thinking-block, before it ever reaches its actual JSON
            // answer -- see this pipeline's own eval harness, which hit exactly this failure
            // mode at the default token budget. Explicit and generous rather than left unset.
            ["max_tokens"] = options.VllmMaxTokens,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = schemaName,
                    ["schema"] = schema,
                    ["strict"] = true
                }
            }
        };
        var payload = body.ToJsonString();

        for (var attempt = 0; ; attempt += 1)
        {
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
                    var answer = ExtractJsonObject(content)
                        ?? throw new InvalidOperationException(
                            $"{schemaName} response content did not contain a parseable JSON " +
                            "object -- the model may have been truncated mid-reasoning; check " +
                            "AudioChoice:OpenAI:VllmMaxTokens.");
                    return new AnalysisModelResponse(
                        answer.ToJsonString(),
                        root["usage"]?["prompt_tokens"]?.GetValue<long>(),
                        root["usage"]?["completion_tokens"]?.GetValue<long>());
                }

                // Same retry rule as OpenAIResponsesModelClient: only a rate limit or a server
                // fault is worth repeating.
                if (attempt >= options.MaximumRetries ||
                    (response.StatusCode != HttpStatusCode.TooManyRequests &&
                     (int)response.StatusCode < 500))
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"{schemaName} failed with HTTP {(int)response.StatusCode}: {error}");
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
