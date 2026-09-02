using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Reaches a model through Amazon Bedrock's Converse API.
/// </summary>
/// <remarks>
/// Converse is used rather than each model family's native request shape because it is the
/// same call for every model on Bedrock. That is what makes the eventual move from Nova to a
/// frontier model a change of one configured model identifier rather than a change of code.
/// Cross-region inference profile identifiers work here unchanged: whatever string is
/// configured is passed through, so a model that requires a regional prefix needs no special
/// handling.
///
/// Structured output is imposed with a tool rather than by asking for JSON in the prompt.
/// Bedrock has no equivalent of a strict response schema, but a tool's input schema is
/// enforced, and forcing that one tool means the model must answer in the shape the scanner
/// expects. Asking politely for JSON would have been a regression against what the OpenAI
/// transport already guarantees, and the failure would look like an occasional unparseable
/// answer rather than an obvious break.
/// </remarks>
public sealed class BedrockConverseModelClient(
    IAmazonBedrockRuntime bedrock,
    OpenAIProcessingOptions options,
    ILogger<BedrockConverseModelClient> logger) : IAnalysisModelClient
{
    public string ProviderName => "bedrock";

    public async Task<AnalysisModelResponse> CompleteJson(
        string model,
        string input,
        string schemaName,
        JsonObject schema,
        CancellationToken cancellationToken)
    {
        var request = new ConverseRequest
        {
            ModelId = model,
            Messages =
            [
                new Message
                {
                    Role = ConversationRole.User,
                    Content = [new ContentBlock { Text = input }]
                }
            ],
            ToolConfig = new ToolConfiguration
            {
                Tools =
                [
                    new Tool
                    {
                        ToolSpec = new ToolSpecification
                        {
                            Name = schemaName,
                            Description =
                                "Reports the classified content events for the supplied excerpt.",
                            InputSchema = new ToolInputSchema { Json = BedrockDocuments.ToDocument(schema) }
                        }
                    }
                ],
                // Leaves the model no option but to answer through the tool. Without this a
                // model may reply in prose, which the scanner cannot read.
                ToolChoice = new ToolChoice
                {
                    Tool = new SpecificToolChoice { Name = schemaName }
                }
            }
        };

        var throttles = 0;
        for (var attempt = 0; ; attempt += 1)
        {
            try
            {
                var response = await bedrock.ConverseAsync(request, cancellationToken);
                return new AnalysisModelResponse(
                    ExtractToolInput(response, schemaName),
                    response.Usage?.InputTokens,
                    response.Usage?.OutputTokens);
            }
            catch (ThrottlingException) when (throttles < ThrottleRetryBudget)
            {
                // Throttling is not a fault, it is the account's rate limit doing its job, and
                // it deserves a different response from a fault. It gets its own budget so a
                // busy minute cannot consume the allowance meant for real errors, and a longer,
                // jittered wait so concurrent workers stop marching in step and re-colliding.
                //
                // This is what a twelve-hour book failed on twice. Scene verification made 110
                // Nova Pro calls in quick succession, the general three-retry allowance ran out
                // at window 103, and the whole analysis was discarded seven windows from done.
                throttles += 1;
                var wait = TimeSpan.FromSeconds(
                    Math.Min(Math.Pow(2, throttles), MaximumThrottleWaitSeconds))
                    + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                logger.LogWarning(
                    "{SchemaName} throttled on {Model}; waiting {Wait} (throttle {Count} of {Budget}).",
                    schemaName, model, wait, throttles, ThrottleRetryBudget);
                await Task.Delay(wait, cancellationToken);
            }
            catch (Exception error) when (
                (error is ModelTimeoutException or
                     Amazon.BedrockRuntime.Model.InternalServerException or
                     // "Model produced invalid sequence as part of ToolUse". The request was
                     // accepted; the model produced something unusable while sampling. Asking
                     // again is the correct response and normally works. Left out at first
                     // because it reads like a client error, which cost a book at 91% analysed.
                     ModelErrorException) &&
                attempt < options.MaximumRetries)
            {
                // Never repeat a rejected request: a malformed request repeated is rejected
                // again, and paying three times only delays the error.
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(
                    "{SchemaName} retry {Attempt} on {Model} after {Delay} ({Error}).",
                    schemaName, attempt + 1, model, delay, error.GetType().Name);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// How many times one call may be throttled before giving up, separate from the retry
    /// allowance for faults. Generous because being throttled says nothing is wrong.
    /// </summary>
    private const int ThrottleRetryBudget = 10;

    /// <summary>Longest single wait after being throttled, so a stall stays observable.</summary>
    private const double MaximumThrottleWaitSeconds = 30;

    /// <summary>
    /// Pulls the tool arguments out of the reply, as the JSON the scanner deserializes.
    /// </summary>
    private static string ExtractToolInput(ConverseResponse response, string schemaName)
    {
        var content = response.Output?.Message?.Content ?? [];
        foreach (var block in content)
        {
            if (block.ToolUse is { } use && use.Input is { } arguments)
            {
                return BedrockDocuments.ToJsonNode(arguments)?.ToJsonString()
                    ?? throw new InvalidOperationException(
                        $"{schemaName} returned tool arguments that were not an object.");
            }
        }

        // A model that answered in prose despite being given no choice. Reported with what it
        // said, because the alternative is a null-reference further down that says nothing
        // about which model misbehaved or why.
        var spoken = string.Join(" ", content.Select(block => block.Text).Where(text => text is not null));
        throw new InvalidOperationException(
            $"{schemaName} returned no tool result. Stop reason: {response.StopReason}. " +
            $"Text: {(spoken.Length > 400 ? spoken[..400] : spoken)}");
    }

}
