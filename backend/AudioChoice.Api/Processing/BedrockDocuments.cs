using System.Text.Json.Nodes;
using Amazon.Runtime.Documents;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Converts between JSON and the AWS SDK's Document type.
/// </summary>
/// <remarks>
/// Bedrock takes a tool's input schema, and returns the tool's arguments, as Document rather
/// than as JSON. So the scanner's schema has to survive a round trip through a different
/// representation, and this is the one step where it could quietly change shape: an enum
/// flattened to a plain string, a bound dropped, a number arriving as text. None of that would
/// fail loudly. The model would answer in a shape slightly off from what the taxonomy expects,
/// the unknown labels would be logged and discarded, and the book would scan successfully with
/// less filtered than it should have.
///
/// Separated from the transport so it can be tested on the real taxonomy rather than only
/// exercised by a live model call.
/// </remarks>
public static class BedrockDocuments
{
    /// <summary>Converts JSON, such as a JSON Schema, into a Document.</summary>
    public static Document ToDocument(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return new Document();
            case JsonObject obj:
                var members = new Dictionary<string, Document>();
                foreach (var pair in obj) members[pair.Key] = ToDocument(pair.Value);
                return new Document(members);
            case JsonArray array:
                return new Document(array.Select(ToDocument).ToList());
            case JsonValue value:
                // Order matters. A bool reads as a number in some representations, and an
                // integer that is tested as a double first comes back as 1 rather than 1.0 --
                // harmless for a bound, wrong for a value the schema calls an integer.
                if (value.TryGetValue(out bool flag)) return new Document(flag);
                if (value.TryGetValue(out int whole)) return new Document(whole);
                if (value.TryGetValue(out long large)) return new Document(large);
                if (value.TryGetValue(out double real)) return new Document(real);
                if (value.TryGetValue(out string? text)) return new Document(text);
                return new Document(value.ToJsonString());
            default:
                return new Document(node.ToJsonString());
        }
    }

    /// <summary>Converts a Document, such as a model's tool arguments, back into JSON.</summary>
    public static JsonNode? ToJsonNode(Document document)
    {
        if (document.IsDictionary())
        {
            var obj = new JsonObject();
            foreach (var pair in document.AsDictionary()) obj[pair.Key] = ToJsonNode(pair.Value);
            return obj;
        }
        if (document.IsList())
        {
            return new JsonArray(document.AsList().Select(ToJsonNode).ToArray());
        }
        if (document.IsBool()) return JsonValue.Create(document.AsBool());
        if (document.IsInt()) return JsonValue.Create(document.AsInt());
        if (document.IsLong()) return JsonValue.Create(document.AsLong());
        if (document.IsDouble()) return JsonValue.Create(document.AsDouble());
        if (document.IsString()) return JsonValue.Create(document.AsString());
        return null;
    }
}
