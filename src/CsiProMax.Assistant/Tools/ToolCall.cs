using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// How every tool is called (the chat, the Modeler window, the field test): its input is checked against its own schema
/// first, so input of the wrong shape is refused in words naming the field, never with the JSON reader's own message;
/// and in the Modeler a call that changes the model and fails part way puts the model back as it was, so a refusal never
/// leaves half an edit (the robustness run, ModelFuzz, found add_stories adding stories before it refused a name).
/// </summary>
public static class ToolCall
{
    public static JsonNode Invoke(this Tool tool, JsonElement args, ICsiLink link)
    {
        ToolSchema.Check(tool.Schema, args);
        if (link is not OfflineEtabs o || tool.Ask == Ask.Never) return tool.Run(args, link);
        var before = ModelFile.ToJson(o.Model);
        var unsaved = o.Unsaved;
        var revision = o.Revision;
        try { return tool.Run(args, link); }
        catch
        {
            if (o.Revision != revision || ModelFile.ToJson(o.Model) != before) o.Rollback(ModelFile.FromJson(before), unsaved);
            throw;
        }
    }
}

/// <summary>The shape a tool's schema asks for: types of the given fields (null counts as not given) and the required ones.</summary>
public static class ToolSchema
{
    public static void Check(string schema, JsonElement args)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(schema).RootElement; }
        catch (JsonException) { return; }
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        if (args.ValueKind != JsonValueKind.Object) throw new ToolInputException("The input is an object of named values ({...}).");
        Walk(root, args, "");
    }

    private static void Walk(JsonElement s, JsonElement v, string path)
    {
        if (v.ValueKind == JsonValueKind.Null) return;
        if (s.ValueKind != JsonValueKind.Object) return;
        if (s.TryGetProperty("type", out var t))
        {
            var types = t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [t.GetString() ?? ""];
            if (!types.Any(x => Fits(x, v)))
                throw new ToolInputException($"'{(path.Length > 0 ? path : "the input")}' is {Kind(v)}; it should be {string.Join(" or ", types.Select(Name))}.");
        }
        if (v.ValueKind == JsonValueKind.Object)
        {
            if (s.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                foreach (var r in req.EnumerateArray().Select(x => x.GetString()).OfType<string>())
                    if (!v.TryGetProperty(r, out var given) || given.ValueKind == JsonValueKind.Null)
                        throw new ToolInputException($"'{Join(path, r)}' is missing.");
            if (s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject())
                    if (v.TryGetProperty(p.Name, out var given)) Walk(p.Value, given, Join(path, p.Name));
        }
        else if (v.ValueKind == JsonValueKind.Array && s.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var x in v.EnumerateArray())
            {
                // An empty entry in a list is never meant (a field left out of an entry is another thing).
                if (x.ValueKind == JsonValueKind.Null && items.ValueKind == JsonValueKind.Object && items.TryGetProperty("type", out _))
                    throw new ToolInputException($"'{path}[{i}]' is empty.");
                Walk(items, x, $"{path}[{i++}]");
            }
        }
    }

    private static string Join(string path, string name) => path.Length > 0 ? path + "." + name : name;

    private static bool Fits(string type, JsonElement v) => type switch
    {
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => v.ValueKind == JsonValueKind.Array,
        "object" => v.ValueKind == JsonValueKind.Object,
        _ => true,
    };

    private static string Name(string type) => type switch { "string" => "a text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false", "array" => "a list", "object" => "an object {...}", _ => type };

    private static string Kind(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => "a text", JsonValueKind.Number => "a number", JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Array => "a list", JsonValueKind.Object => "an object", _ => "empty",
    };
}
