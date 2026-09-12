using System.Text.Json.Nodes;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Sends each call to whichever service hosts the model it names.
/// </summary>
/// <remarks>
/// The scanner's three tiers already name their models in configuration. This makes those names
/// decide the vendor too, so a single tier can sit somewhere else without a second set of
/// settings to keep in step -- and without the provider being a whole-pipeline choice, which is
/// what it was until now.
///
/// That matters because the tiers are not equally well served. Nova classifies and judges
/// violence well and cheaply, and it does the bulk of the work: the first pass is around 350
/// calls a book against a dozen for escalation. But the sexual-scene stages return replies this
/// pipeline cannot read reliably, so those stay on OpenAI until a frontier model is reachable on
/// Bedrock. Splitting by tier keeps roughly 95% of the volume on credits while the part that has
/// to be right stays on the service that gets it right.
///
/// When a frontier model does become available, moving those tiers is changing two model names.
/// No code, no redeploy of anything but configuration.
///
/// A third route -- a self-hosted vLLM server -- was added the same way: a tier names a local
/// model identifier (see <see cref="IsVllmModel"/>) and this router sends it there instead,
/// without OpenAI or Bedrock ever being asked. This is what lets the pipeline run with no
/// OpenAI dependency at all when every tier names a local model, while leaving the OpenAI and
/// Bedrock paths completely intact for rollback -- switching back is changing the three model
/// names in configuration, not removing code.
/// </remarks>
public sealed class RoutingAnalysisModelClient(
    IAnalysisModelClient? bedrock,
    IAnalysisModelClient? openAI,
    IAnalysisModelClient? vllm,
    ILogger<RoutingAnalysisModelClient> logger) : IAnalysisModelClient
{
    public string ProviderName => "routed";

    /// <summary>
    /// Whether a model name belongs to OpenAI rather than Bedrock or a local vLLM server.
    /// </summary>
    /// <remarks>
    /// Read from the name because the name is already the only thing distinguishing the tiers.
    /// Bedrock's own identifiers are unambiguous -- "amazon.nova-lite-v1:0",
    /// "us.amazon.nova-2-lite-v1:0" -- so anything that is not one of those, and looks like an
    /// OpenAI model, goes to OpenAI. A name matching neither is refused rather than guessed at,
    /// because sending a book's analysis to the wrong service silently is worse than not starting.
    /// </remarks>
    internal static bool IsOpenAIModel(string model) =>
        model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase);

    internal static bool IsBedrockModel(string model) =>
        model.Contains("amazon.", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("anthropic.", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("meta.", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("mistral.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a model name belongs to the self-hosted vLLM server rather than OpenAI or
    /// Bedrock.
    /// </summary>
    /// <remarks>
    /// vLLM model identifiers are whatever <c>--served-model-name</c> the server was started
    /// with (see deploy/lambda/vllm-eval/docker-compose.yml), so there is no vendor-assigned
    /// naming convention to key on the way "gpt" or "amazon." are. Matched on "qwen" and
    /// "vllm" as the two names this pipeline actually uses today; add a case here rather than
    /// widening either check if a genuinely different local model name is ever configured, so
    /// an unrecognized name still fails loudly instead of being silently misrouted.
    /// </remarks>
    internal static bool IsVllmModel(string model) =>
        model.Contains("qwen", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("vllm", StringComparison.OrdinalIgnoreCase);

    public Task<AnalysisModelResponse> CompleteJson(
        string model,
        string input,
        string schemaName,
        JsonObject schema,
        CancellationToken cancellationToken)
    {
        IAnalysisModelClient chosen;
        if (IsOpenAIModel(model))
        {
            chosen = openAI ?? throw new InvalidOperationException(
                $"Model '{model}' names an OpenAI model, but no OpenAI client was configured.");
        }
        else if (IsBedrockModel(model))
        {
            chosen = bedrock ?? throw new InvalidOperationException(
                $"Model '{model}' names a Bedrock model, but no Bedrock client was configured.");
        }
        else if (IsVllmModel(model))
        {
            chosen = vllm ?? throw new InvalidOperationException(
                $"Model '{model}' names a local vLLM model, but no vLLM client was configured. " +
                "Set AudioChoice:OpenAI:VllmEndpoint or check AnalysisProvider.");
        }
        else
        {
            // Deliberately fatal. A misspelled model name that quietly fell through to one
            // service would classify a whole book against the wrong provider, and the result
            // would look ordinary.
            throw new InvalidOperationException(
                $"Cannot tell which service hosts the model '{model}'. Bedrock names contain a " +
                "provider prefix such as 'amazon.'; OpenAI names begin with 'gpt'; a local " +
                "vLLM model name contains 'qwen' or 'vllm'.");
        }

        logger.LogDebug("{SchemaName} routed to {Provider} for {Model}.",
            schemaName, chosen.ProviderName, model);
        return chosen.CompleteJson(model, input, schemaName, schema, cancellationToken);
    }
}
