using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Reaches a hosted model through DeepInfra's OpenAI-compatible chat completions API, with a
/// strict JSON schema response format.
/// </summary>
/// <remarks>
/// Sibling to <see cref="OpenAIResponsesModelClient"/>, <see cref="BedrockConverseModelClient"/>,
/// and <see cref="VllmModelClient"/>: same seam (<see cref="IAnalysisModelClient"/>), same
/// contract, different transport. Added so a tier can name a model DeepInfra hosts (for
/// example, the same Qwen family this pipeline already validated for scene verification and
/// escalation, at a fraction of a self-hosted GPU's standing cost) instead of OpenAI, Bedrock,
/// or a local vLLM server.
///
/// Unlike <see cref="VllmModelClient"/>, this does not need to work around a locally-hosted
/// model's own serving quirks (a visible "thinking" block, vLLM-side context-length
/// misreporting under queue pressure) -- those are specific to that self-hosted deployment, not
/// a general property of every OpenAI-compatible endpoint. This client is deliberately simpler:
/// one request, the ordinary HTTP-status-based retry every other client here already uses, and
/// nothing else.
/// </remarks>
public sealed class DeepInfraModelClient(
    HttpClient client,
    OpenAIProcessingOptions options,
    ILogger<DeepInfraModelClient> logger) : IAnalysisModelClient
{
    public string ProviderName => "deepinfra";

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
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = input }),
            ["temperature"] = schemaName == "audiochoice_scan_events" ? 0.7 : 0.0,
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
            using var request = new HttpRequestMessage(HttpMethod.Post, "openai/chat/completions");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", options.DeepInfraApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken);
            }
            // Same shape as every other client here: a client-side timeout or dropped
            // connection throws before any status code exists to inspect, and must not be
            // retried when the caller's own cancellation is what actually fired.
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
                    return new AnalysisModelResponse(
                        content,
                        root["usage"]?["prompt_tokens"]?.GetValue<long>(),
                        root["usage"]?["completion_tokens"]?.GetValue<long>());
                }

                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Only a rate limit or a server fault is worth repeating -- the same retry
                // rule every other client here uses.
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
}
