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
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Text(object? value) => value switch
    {
        null => "null",
        string s => s,
        JsonNode node => node.ToJsonString(Options),
        _ => JsonSerializer.Serialize(value, Options),
    };
}
