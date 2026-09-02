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
            catch (Exception error) when (
                (error is ThrottlingException or ModelTimeoutException or
                     Amazon.BedrockRuntime.Model.InternalServerException or
                     // "Model produced invalid sequence as part of ToolUse". The model, not the
                     // request, produced something unusable, and it is sampled rather than
                     // deterministic -- so asking again is the correct response and usually
                     // works. Left out at first because it reads like a client error, which
                     // cost a twelve-hour book at 91% analysed: every batch before it had
                     // succeeded and the job failed on one malformed reply.
                     ModelErrorException) &&
                attempt < options.MaximumRetries)
            {
                // Same rule as the OpenAI transport: repeat a rate limit, a server fault, or a
                // reply the model itself mangled. Never repeat a rejected request -- a
                // malformed request repeated is rejected again.
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(
                    "{SchemaName} retry {Attempt} on {Model} after {Delay} ({Error}).",
                    schemaName, attempt + 1, model, delay, error.GetType().Name);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

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
