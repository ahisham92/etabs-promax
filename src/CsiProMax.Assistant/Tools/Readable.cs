using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// A value as text a person reads in a report or a message: a text as it is, anything else as JSON with its quotes
/// and letters as they are. (The default writer turns every quote inside a text into ", and a tool's answer
/// that was already JSON text came out wrapped a second time: a tester's report was unreadable where it mattered.)
/// </summary>
public static class Readable
{
    // The resolver is given: a JsonNode written with options that no JsonSerializer call has used yet has none, and a
    // value put in with JsonArray.Add<T> (failed.Add("a text")) then threw on writing, when it was the first thing written.
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public static string Text(object? value) => value switch
    {
        null => "null",
        string s => s,
        JsonNode node => node.ToJsonString(Options),
        _ => JsonSerializer.Serialize(value, Options),
    };
}
