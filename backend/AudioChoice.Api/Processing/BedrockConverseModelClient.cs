using System.Text.Json;
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
        var noToolResultAttempts = 0;
        var bogusOverflowAttempts = 0;
        for (var attempt = 0; ; attempt += 1)
        {
            ConverseResponse response;
            try
            {
                response = await bedrock.ConverseAsync(request, cancellationToken);
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
                continue;
            }
            catch (Amazon.BedrockRuntime.Model.ValidationException error) when (
                IsBogusContextOverflow(error) &&
                bogusOverflowAttempts < MaximumBogusOverflowRetries)
            {
                // Confirmed genuinely non-deterministic, not content-dependent: on the same
                // Carl's Doomsday Scenario run, one candidate (126 segments, ~11,112
                // characters -- the largest of the four real cache-misses that run) failed
                // this exact "32769 input tokens" claim twice and then succeeded on its
                // third attempt, while a smaller candidate (73 segments, ~6,495 characters)
                // failed it on all of MaximumRetries' 3 attempts and failed the job. A
                // request whose real size does not change between attempts cannot
                // deterministically overflow on one attempt and fit on the next; what
                // varies is the bogus count, not the prompt. MaximumRetries' budget (3) is
                // shared with genuine faults (timeouts, malformed tool calls) that should
                // stay capped tightly, so this gets its own, more generous budget instead
                // of just raising MaximumRetries for every other fault type too. Capped
                // backoff (not unbounded exponential) because nothing about this fault
                // benefits from waiting longer between attempts -- it is not a rate limit.
                bogusOverflowAttempts += 1;
                var delay = TimeSpan.FromSeconds(
                    Math.Min(Math.Pow(2, bogusOverflowAttempts), MaximumBogusOverflowWaitSeconds));
                logger.LogWarning(
                    "{SchemaName} on {Model} hit the bogus 32769-input-token overflow " +
                    "(attempt {Attempt} of {Budget}); retrying after {Delay}.",
                    schemaName, model, bogusOverflowAttempts, MaximumBogusOverflowRetries, delay);
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            catch (Exception error) when (
                IsRetryableFault(error) && attempt < options.MaximumRetries)
            {
                // Never repeat a rejected request: a malformed request repeated is rejected
                // again, and paying three times only delays the error.
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(
                    "{SchemaName} retry {Attempt} on {Model} after {Delay} ({Error}).",
                    schemaName, attempt + 1, model, delay, error.GetType().Name);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            try
            {
                return new AnalysisModelResponse(
                    ExtractToolInput(response, schemaName, schema),
                    response.Usage?.InputTokens,
                    response.Usage?.OutputTokens);
            }
            catch (NoToolResultException error)
            {
                // A real, repeatedly observed Bedrock Converse behavior, not unique to one
                // model: a forced tool_choice is not always honored, and the model answers in
                // plain prose instead -- stopReason "end_turn", not "tool_use" -- even though
                // the request gave it no other option. Retried first rather than accepted
                // immediately, the same as a malformed-output fault above: a second
                // independent call asking the identical question is cheap next to the cost of
                // treating a genuine miss as a confirmed empty result.
                noToolResultAttempts += 1;
                if (noToolResultAttempts <= MaximumNoToolResultRetries)
                {
                    logger.LogWarning(
                        "{SchemaName} on {Model} answered in prose instead of using the " +
                        "forced tool (attempt {Attempt} of {Budget}); retrying. Text: {Text}",
                        schemaName, model, noToolResultAttempts, MaximumNoToolResultRetries,
                        error.SpokenText);
                    continue;
                }

                // Retries exhausted. Only ever falls back to an empty result, and only for
                // the one schema (audiochoice_scan_events) where an empty array is itself a
                // real, meaningful answer this pipeline already treats as "nothing found in
                // this batch" -- never for scene verification/escalation, where a candidate
                // the caller is waiting on a decision for is not safely assumed clean just
                // because the model talked around the question instead of answering it, and
                // where ResolveMajorityVote's own missing-vote handling already has a
                // different, deliberate meaning (a vote that does not count toward either
                // side) that this must not be confused with. Logged at Error, not Warning,
                // because this is the one path where real sexual-content judgment is being
                // silently assumed rather than actually rendered, and that needs to stay
                // visible rather than blend into ordinary retry noise.
                if (schemaName == "audiochoice_scan_events")
                {
                    logger.LogError(
                        "{SchemaName} on {Model} never used the forced tool after {Budget} " +
                        "retries; treating this batch as reporting no events. This batch's " +
                        "real content was never actually judged by a structured answer -- " +
                        "only by prose this pipeline cannot act on. Text: {Text}",
                        schemaName, model, MaximumNoToolResultRetries, error.SpokenText);
                    return new AnalysisModelResponse("""{"events":[]}""", null, null);
                }

                throw new InvalidOperationException(
                    $"{schemaName} never used the forced tool on {model} after " +
                    $"{MaximumNoToolResultRetries} retries. This schema has no safe empty " +
                    $"fallback -- a candidate this caller is waiting on a real decision for " +
                    $"cannot be silently treated as anything. Stop reason: {error.StopReason}. " +
                    $"Text: {error.SpokenText}");
            }
        }
    }

    /// <summary>
    /// How many additional times a call that answered in prose instead of using the forced
    /// tool, or used it but left out a required field (see
    /// <see cref="FindMissingRequiredField"/>), may be retried before <see cref="CompleteJson"/>
    /// gives up on getting a real structured answer.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OpenAIProcessingOptions.MaximumRetries"/>, which exists for
    /// outright faults (a timeout, a malformed tool call) -- this is a different kind of
    /// failure (the call succeeded; the model simply chose not to answer through the tool it
    /// was forced to use, or answered through it incompletely).
    ///
    /// Raised from 2 to 4 after direct evidence on Butcher's Masquerade that this is a real,
    /// frequent Qwen3-32B-on-Bedrock behavior, not an occasional fluke: 147 occurrences across
    /// one book's scan (prose answers and missing required fields combined), the large
    /// majority clearing within the original 2-attempt budget, and exactly one candidate that
    /// did not and failed the whole job at candidate 199 of roughly 199 -- a missing
    /// needsEscalation field, not even the same field as an earlier run's missing
    /// candidateKey. This is not "fixed" by raising the budget; the model's own forced-tool
    /// reliability under this schema's complexity is a genuine, repeatedly-confirmed
    /// limitation worth weighing directly in whether this model is viable for production, not
    /// something this retry is meant to make invisible. Raised only modestly, and not to the
    /// bogus-overflow budget's scale (8, see MaximumBogusOverflowRetries): unlike that
    /// confirmed-noise case, a model that skips or incompletes a forced tool call is not
    /// guaranteed to eventually comply no matter how many times it is asked, so a very large
    /// budget here would mostly just delay an honest failure rather than fix anything.
    /// </remarks>
    private const int MaximumNoToolResultRetries = 4;

    /// <summary>
    /// How many times one call may be throttled before giving up, separate from the retry
    /// allowance for faults. Generous because being throttled says nothing is wrong.
    /// </summary>
    private const int ThrottleRetryBudget = 10;

    /// <summary>Longest single wait after being throttled, so a stall stays observable.</summary>
    private const double MaximumThrottleWaitSeconds = 30;

    /// <summary>
    /// How many times to retry the confirmed-bogus "32769 input tokens" overflow (see
    /// <see cref="IsBogusContextOverflow"/>) before giving up on this one candidate. Set well
    /// above <see cref="OpenAIProcessingOptions.MaximumRetries"/>'s own default (3): on the
    /// real run that proved this is noise, the candidate that eventually succeeded needed 3
    /// attempts, and the one that exhausted a 6-attempt budget and still failed suggests 3 is
    /// not always enough. This is deliberately its own budget rather than a change to
    /// MaximumRetries, which stays tight for genuine faults (timeouts, malformed tool calls)
    /// that retrying many times would otherwise mask.
    /// </summary>
    private const int MaximumBogusOverflowRetries = 8;

    /// <summary>Longest single wait between bogus-overflow retries. Capped, not unbounded
    /// exponential, because nothing about this fault benefits from a long wait -- it is not
    /// a rate limit, so there is no reason to slow down the later attempts.</summary>
    private const double MaximumBogusOverflowWaitSeconds = 15;

    /// <summary>
    /// Pulls the tool arguments out of the reply, as the JSON the scanner deserializes.
    /// </summary>
    /// <summary>
    /// Returns the array a model encoded as a JSON string, or null if it did not.
    /// </summary>
    /// <remarks>
    /// Only accepted when the text parses cleanly as an array. Anything less certain is left
    /// alone: reinterpreting a reply we cannot read confidently would be inventing an answer,
    /// and these replies decide what is removed from somebody's audiobook.
    /// </remarks>
    private static JsonArray? Unwrap(JsonNode value)
    {
        var text = value switch
        {
            JsonValue single when single.TryGetValue(out string? direct) => direct,
            JsonArray array when array.Count == 1 && array[0] is JsonValue inner &&
                inner.TryGetValue(out string? nested) => nested,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonArray; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Nudges a reply into the shape its schema declared, where a model bends it.
    /// </summary>
    /// <remarks>
    /// A tool schema is enforced loosely enough that Nova will sometimes return a single object
    /// where an array of one was asked for. Deserialization then fails on the whole batch --
    /// which cost a book at 94%, after every model call had already been paid for, on a reply
    /// that carried the right content in a marginally wrong wrapper.
    ///
    /// Only that one shape is corrected, and only where the schema says array. Anything else is
    /// left to fail loudly: quietly coercing a reply into the expected type is how a wrong
    /// answer gets accepted as a right one, and this pipeline decides what a listener hears.
    /// </remarks>
    private static void Conform(JsonNode answer, JsonObject? schema)
    {
        if (answer is not JsonObject obj || schema?["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var property in properties)
        {
            var declared = property.Value as JsonObject;
            // A schema type may be a union, as profanityWord's ["string","null"] is, so this
            // cannot assume a plain string. Reading it as one threw and failed the whole job.
            var type = declared?["type"] is JsonValue declaredType &&
                declaredType.TryGetValue(out string? single) ? single : null;
            var value = obj[property.Key];
            if (value is null) continue;

            if (type == "array")
            {
                // An array whose only element is a string that is itself the JSON array. Nova
                // does this: {"candidates":["[{\"candidateKey\":...}]"]}. Every field lookup
                // then fails, and the error names a byte offset inside text that looks like the
                // right answer -- which is why this took three rebuilds to see rather than guess.
                if (Unwrap(value) is JsonArray unwrapped)
                {
                    obj[property.Key] = unwrapped;
                    value = unwrapped;
                }
                // A lone object where an array of one was asked for.
                else if (value is not JsonArray)
                {
                    obj[property.Key] = new JsonArray(value.DeepClone());
                }
                foreach (var item in obj[property.Key]!.AsArray())
                {
                    if (item is not null) Conform(item, declared?["items"] as JsonObject);
                }
                continue;
            }

            if (type == "object") { Conform(value, declared); continue; }

            // A scalar sent as a quoted string. Only converted when the text is unambiguously
            // the declared type -- "true" for a boolean, a parseable number for a number.
            // Anything else is left alone to fail, because guessing at a value here decides
            // what a listener hears.
            if (value is not JsonValue scalar || !scalar.TryGetValue(out string? text)) continue;
            if (type == "boolean" && bool.TryParse(text, out var flag))
            {
                obj[property.Key] = JsonValue.Create(flag);
            }
            else if ((type == "number" || type == "integer") &&
                double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                obj[property.Key] = JsonValue.Create(number);
            }
        }
    }


    /// <summary>
    /// Whether a fault is one worth asking again rather than failing the whole batch outright.
    /// </summary>
    /// <remarks>
    /// <see cref="ModelTimeoutException"/>, <see cref="Amazon.BedrockRuntime.Model.InternalServerException"/>,
    /// and <see cref="ModelErrorException"/> ("Model produced invalid sequence as part of
    /// ToolUse" -- the request was accepted, the model produced something unusable while
    /// sampling, and asking again normally works) are retried unconditionally.
    ///
    /// The bogus context-overflow case (see <see cref="IsBogusContextOverflow"/>) is handled
    /// separately above with its own, more generous retry budget, not folded in here.
    /// </remarks>
    private static bool IsRetryableFault(Exception error) =>
        error is ModelTimeoutException or
            Amazon.BedrockRuntime.Model.InternalServerException or
            ModelErrorException;

    /// <summary>
    /// Whether a <see cref="Amazon.BedrockRuntime.Model.ValidationException"/> is the one
    /// specific, confirmed-non-deterministic bogus overflow this book has been failing on,
    /// rather than a real one.
    /// </summary>
    /// <remarks>
    /// A real production failure on Carl's Doomsday Scenario claimed "your prompt contains at
    /// least 32769 input tokens" immediately after a genuinely small, successful call (the
    /// preceding request logged in=4991), on a candidate whose own real segment count and
    /// character length were nowhere near large enough to produce that figure. The exact same
    /// number recurred across three separate books and pipeline stages after two independent
    /// rounds of shrinking every known real prompt-size knob
    /// (MaximumSegmentsPerAnalysisRequest, MaximumContextExpansionSeconds) failed to change it
    /// at all. Decisive proof this is noise and not a real, content-dependent limit: on one
    /// run, a 126-segment/~11,112-character candidate failed this exact claim twice and then
    /// succeeded on a third, identical retry, while a smaller 73-segment/~6,495-character
    /// candidate failed it on every attempt within the general retry budget and failed the
    /// job -- a request whose real size does not change between attempts cannot
    /// deterministically overflow on one attempt and fit on the next. Matched narrowly on
    /// this exact claimed count, not on the error type alone, so a real, differently-sized
    /// overflow elsewhere still fails loudly instead of being silently retried into eventually
    /// exhausting the budget and failing anyway, just slower.
    /// </remarks>
    private static bool IsBogusContextOverflow(Amazon.BedrockRuntime.Model.ValidationException error) =>
        error.Message.Contains("at least 32769 input tokens");

    /// <summary>
    /// Finds the first schema-required field that is missing or explicitly null anywhere in
    /// an answer, recursing into nested objects and array items the same way
    /// <see cref="Conform"/> does. Returns null when every required field the schema actually
    /// declares is present with a real value.
    /// </summary>
    /// <remarks>
    /// Deliberately only checks presence/null-ness, not type or content -- a wrong type is
    /// already a separate, louder failure elsewhere (deserialization), and this exists purely
    /// to catch the one gap a forced tool_choice does not close: a field the schema marks
    /// required can still come back null.
    /// </remarks>
    private static string? FindMissingRequiredField(JsonNode answer, JsonObject? schema)
    {
        if (answer is not JsonObject obj || schema is null) return null;

        if (schema["required"] is JsonArray required)
        {
            foreach (var requiredField in required)
            {
                if (requiredField is not JsonValue nameValue ||
                    !nameValue.TryGetValue(out string? name))
                {
                    continue;
                }
                if (obj[name] is null)
                {
                    return name;
                }
            }
        }

        if (schema["properties"] is not JsonObject properties) return null;
        foreach (var property in properties)
        {
            var value = obj[property.Key];
            if (value is null) continue;
            var declared = property.Value as JsonObject;
            var type = declared?["type"] is JsonValue declaredType &&
                declaredType.TryGetValue(out string? single) ? single : null;

            if (type == "object" && FindMissingRequiredField(value, declared) is { } nested)
            {
                return $"{property.Key}.{nested}";
            }

            if (type == "array" && value is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is not null &&
                        FindMissingRequiredField(item, declared?["items"] as JsonObject) is { } itemField)
                    {
                        return $"{property.Key}[].{itemField}";
                    }
                }
            }
        }

        return null;
    }

    private static string ExtractToolInput(
        ConverseResponse response, string schemaName, JsonObject schema)
    {
        var content = response.Output?.Message?.Content ?? [];
        foreach (var block in content)
        {
            if (block.ToolUse is { } use && use.Input is { } arguments)
            {
                var answer = BedrockDocuments.ToJsonNode(arguments)
                    ?? throw new InvalidOperationException(
                        $"{schemaName} returned tool arguments that were not an object.");
                Conform(answer, schema);
                var missingField = FindMissingRequiredField(answer, schema);
                if (missingField is not null)
                {
                    // A real production failure on Butcher's Masquerade: Bedrock's forced
                    // tool_choice is enforced loosely enough to guarantee the right field
                    // names and types exist in the schema, but not that the model actually
                    // filled every field the schema marks required -- one of 251 real Terra
                    // answers came back a syntactically valid tool call with a null
                    // candidateKey, which crashed a plain dictionary lookup four stages later
                    // with no indication of which call produced it. Treated as the same class
                    // of failure as NoToolResultException (the tool was technically used, but
                    // the answer still cannot be trusted or acted on) rather than a generic
                    // fault, so CompleteJson's retry loop asks again instead of either
                    // crashing deep in unrelated code or silently treating a null key as if
                    // it were a real one.
                    var answerJson = answer.ToJsonString();
                    throw new NoToolResultException(
                        schemaName, $"missing required field '{missingField}'",
                        answerJson.Length > 400 ? answerJson[..400] : answerJson);
                }
                return answer.ToJsonString();
            }
        }

        // A model that answered in prose despite being given no choice. Reported with what it
        // said, because the alternative is a null-reference further down that says nothing
        // about which model misbehaved or why. A distinct exception type rather than
        // InvalidOperationException directly, so CompleteJson's retry loop can recognize and
        // react to this specific failure (see MaximumNoToolResultRetries's own remarks)
        // instead of treating it the same as a genuine fault.
        var spoken = string.Join(" ", content.Select(block => block.Text).Where(text => text is not null));
        throw new NoToolResultException(
            schemaName, response.StopReason?.ToString() ?? "unknown",
            spoken.Length > 400 ? spoken[..400] : spoken);
    }

}

/// <summary>
/// A Bedrock model answered in plain prose instead of using the tool
/// <see cref="BedrockConverseModelClient"/> forced it to use. See
/// <see cref="BedrockConverseModelClient.MaximumNoToolResultRetries"/>'s own remarks for why
/// this is handled as its own retryable case rather than an ordinary fault.
/// </summary>
internal sealed class NoToolResultException(string schemaName, string stopReason, string spokenText)
    : Exception($"{schemaName} returned no tool result. Stop reason: {stopReason}. Text: {spokenText}")
{
    public string StopReason { get; } = stopReason;
    public string SpokenText { get; } = spokenText;
}
