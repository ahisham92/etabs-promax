using System.Text.Json;

namespace CsiProMax.Tools;

/// <summary>
/// One operation the agent can perform in ETABS. Each tool is exposed to Claude as a
/// client tool (name + description + JSON schema). Execute always runs on ETABS's main
/// thread via <see cref="ETABS.EtabsDispatcher"/>.
/// </summary>
public interface IEtabsTool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema "properties" object for the tool input.</summary>
    string PropertiesJson { get; }

    string[] Required { get; }

    /// <summary>True when the tool changes the model. Such calls go through the approval gate.</summary>
    bool ModifiesModel { get; }

    ToolResult Execute(EtabsApp app, ToolArgs args);
}

public sealed record ToolResult(string Text, bool IsError = false, byte[]? PngImage = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static ToolResult Ok(object payload) => new(JsonSerializer.Serialize(payload, Json));
    public static ToolResult Error(string message) => new(message, IsError: true);
}

/// <summary>Typed access to the tool_use input Claude sent.</summary>
public sealed class ToolArgs(IReadOnlyDictionary<string, JsonElement> raw)
{
    public IReadOnlyDictionary<string, JsonElement> Raw => raw;

    public bool Has(string key) =>
        raw.TryGetValue(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public string String(string key) =>
        Has(key) ? (raw[key].ValueKind == JsonValueKind.String ? raw[key].GetString()! : raw[key].GetRawText())
                 : throw new ArgumentException($"Missing required argument '{key}'.");

    public string? OptString(string key) => Has(key) ? String(key) : null;

    public double Double(string key) => Has(key) ? raw[key].GetDouble() : throw new ArgumentException($"Missing '{key}'.");
    public double? OptDouble(string key) => Has(key) ? raw[key].GetDouble() : null;

    public long? OptLong(string key) => Has(key) ? raw[key].GetInt64() : null;

    public bool OptBool(string key, bool fallback = false) => Has(key) ? raw[key].GetBoolean() : fallback;

    public List<string> StringList(string key) =>
        Has(key) ? raw[key].EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];

    public IEnumerable<JsonElement> Array(string key) =>
        Has(key) ? raw[key].EnumerateArray() : [];
}
