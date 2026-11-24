using System.Text.Json;
using System.Text.Json.Nodes;
using EtabsProMax.Assistant.Etabs;

namespace EtabsProMax.Assistant.Tools;

/// <summary>When the engineer is asked before a tool runs.</summary>
public enum Ask
{
    /// <summary>It only reads.</summary>
    Never,
    /// <summary>It changes the model: asked only when the engineer set "ask before changes".</summary>
    Changes,
    /// <summary>Always asked: deleting, saving, running the analysis.</summary>
    Always,
}

/// <summary>One thing the assistant can do in ETABS: its name, what Claude is told about it, and the work.</summary>
public sealed record Tool(string Name, string Description, string Schema, Ask Ask, Func<JsonElement, IEtabsLink, JsonNode> Run, Func<JsonElement, string>? Summary = null);

/// <summary>A tool's input was wrong: the message tells Claude how to call it properly.</summary>
public sealed class ToolInputException(string message) : Exception(message);

internal static class Args
{
    public static string Text(this JsonElement a, string name, string fallback = "") =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static string Need(this JsonElement a, string name)
    {
        var value = a.Text(name);
        return value.Length > 0 ? value : throw new ToolInputException($"'{name}' is missing.");
    }

    public static double Number(this JsonElement a, string name, double? fallback = null)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return fallback ?? throw new ToolInputException($"'{name}' is missing: give a number.");
    }

    public static int Whole(this JsonElement a, string name, int fallback) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    public static bool Flag(this JsonElement a, string name, bool fallback = false) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    public static List<string> Texts(this JsonElement a, string name)
    {
        var list = new List<string>();
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return list;
        if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString()!);
        else if (v.ValueKind == JsonValueKind.Array)
            list.AddRange(v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        return list;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            throw new ToolInputException($"'{name}' is missing: give a list.");
        return v.EnumerateArray();
    }

    /// <summary>A point given as [x, y] or {"x":..,"y":..}.</summary>
    public static (double X, double Y) Point(this JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2) return (p[0].GetDouble(), p[1].GetDouble());
        if (p.ValueKind == JsonValueKind.Object) return (p.Number("x"), p.Number("y"));
        throw new ToolInputException("A point is [x, y].");
    }

    public static double Round(double value) => Math.Round(value, 6);
}
