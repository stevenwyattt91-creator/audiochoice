using System.Text.Json.Nodes;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Sends one prompt to one model and returns the JSON it produced.
/// </summary>
/// <remarks>
/// This seam exists so the scanner can change model vendors without any of its judgement
/// moving. Everything that decides what a listener has removed from their audiobook -- the
/// taxonomy, the confidence floor, the narrowed violence policy, scene verification and
/// escalation, the plausibility guard, the checkpoint cache -- stays in
/// <see cref="OpenAIContentAnalysisProvider"/>. Only the transport is swapped.
///
/// Written this way rather than as a second content-analysis provider on purpose. A parallel
/// provider would have to restate all of that policy, and the two copies would drift on the
/// first change made to one of them. A wrong classification is close to invisible: a book
/// scans, reports success and quietly filters the wrong thing. So there is exactly one place
/// those decisions live, and this interface is deliberately too small to hold any of them.
///
/// Every call the scanner makes has the same shape: a model name, one prompt, and a JSON
/// schema the answer must satisfy. Three tiers use it -- the high-recall first pass, scene
/// verification, and escalation -- each naming its own model from configuration, which is what
/// lets a tier move between vendors on its own.
/// </remarks>
public interface IAnalysisModelClient
{
    /// <summary>What this transport is, for logs and stored scan provenance.</summary>
    string ProviderName { get; }

    /// <param name="model">The model identifier, taken from configuration by the caller.</param>
    /// <param name="input">The whole prompt, including the transcript excerpt.</param>
    /// <param name="schemaName">A name for the schema, which some vendors require.</param>
    /// <param name="schema">JSON Schema the reply must conform to.</param>
    Task<AnalysisModelResponse> CompleteJson(
        string model,
        string input,
        string schemaName,
        JsonObject schema,
        CancellationToken cancellationToken);
}

/// <summary>
/// The JSON a model returned, and what it cost to get it.
/// </summary>
/// <remarks>
/// Token counts are carried here because this is the only place that sees them, and because
/// nothing in this system currently records what a scan costs. There is no cost field, no
/// token counter and no spend cap anywhere -- only chunk and duration ceilings acting as
/// proxies -- so the question "what does scanning a book cost" cannot be answered from the
/// data, only estimated from an invoice. Reporting usage from the one place every model call
/// passes through makes it answerable per scan, and makes two vendors comparable on the same
/// book rather than on a benchmark.
///
/// Nullable because not every vendor returns usage on every response, and a missing count
/// must read as "not reported" rather than as zero cost.
/// </remarks>
public sealed record AnalysisModelResponse(
    string Json,
    long? InputTokens = null,
    long? OutputTokens = null);
