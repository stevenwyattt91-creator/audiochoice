using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Reaches a model through OpenAI's Responses API, with a strict JSON schema.
/// </summary>
/// <remarks>
/// This is the transport the scanner has always used, lifted out of
/// <see cref="OpenAIContentAnalysisProvider"/> unchanged: same endpoint, same strict
/// json_schema response format, same retry rule, same way of finding the output text. It was
/// written twice in that file, once for the first pass and once for scene verification, and
/// the two copies had already diverged in what their failures said. There is now one copy.
/// </remarks>
public sealed class OpenAIResponsesModelClient(
    HttpClient client,
    OpenAIProcessingOptions options,
    ILogger<OpenAIResponsesModelClient> logger) : IAnalysisModelClient
{
    public string ProviderName => "openai";

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
            ["input"] = input,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = schemaName,
                    ["strict"] = true,
                    ["schema"] = schema
                }
            }
        };
        var payload = body.ToJsonString();

        for (var attempt = 0; ; attempt += 1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var root = JsonNode.Parse(responseJson)
                    ?? throw new InvalidOperationException(
                        $"{schemaName} returned invalid JSON.");
                return new AnalysisModelResponse(
                    ExtractOutputText(root, schemaName),
                    root["usage"]?["input_tokens"]?.GetValue<long>(),
                    root["usage"]?["output_tokens"]?.GetValue<long>());
            }

            // Only a rate limit or a server fault is worth repeating. A rejected request
            // repeated identically is rejected identically, and paying for that three times
            // only delays the error.
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

    private static string ExtractOutputText(JsonNode root, string schemaName)
    {
        foreach (var output in root["output"]?.AsArray() ?? new JsonArray())
        {
            foreach (var content in output?["content"]?.AsArray() ?? new JsonArray())
            {
                if (content?["type"]?.GetValue<string>() == "output_text" &&
                    content["text"]?.GetValue<string>() is string text)
                {
                    return text;
                }
            }
        }

        throw new InvalidOperationException(
            $"{schemaName} response did not contain output text.");
    }
}
