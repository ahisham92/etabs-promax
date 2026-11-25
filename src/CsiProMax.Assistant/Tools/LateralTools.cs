using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Lateral;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Seismic and wind loads by any code, in every program: the same two tools in the Modeler and in a live program.
/// In the Modeler the code's method computes the story forces (<see cref="LateralLoads"/>). In ETABS and SAP2000 each
/// auto lateral code the program lists has its own editable table ("Auto Seismic - ASCE 7-16", "Auto Wind - Eurocode
/// 1 2005"...), so every code it has is reached through its table, whether its API has a call for it or not.
/// </summary>
public static class LateralTools
{
    public static IReadOnlyList<Tool> All { get; } =
    [
        new("lateral_codes",
            "The seismic and wind codes this model can take, with each code's inputs: in the Modeler the methods it computes (user coefficient, user loads, user spectrum and pressure, ASCE 7, IBC, UBC, Eurocode 8 and 1, ECP 201, NBCC, IS 1893 and 875); in a live program every auto lateral code it lists, with the fields of its table. Give 'code' for one code's inputs, and 'pattern' for what that pattern has now.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["seismic","wind"],"description":"Default seismic."},"code":{"type":"string"},"pattern":{"type":"string"}}}""",
            Ask.Never, Codes),
        new("set_lateral_load",
            "Makes a load pattern a seismic or wind load by a code, with the code's inputs (keys from lateral_codes; one left out takes its default): made as a Seismic or Wind pattern when it is not there. In the Modeler the story forces are computed and applied at once, and again after each change to the model; the answer gives them. In a live program the program computes them when the analysis runs. code 'None' takes the code load off the pattern.",
            """
            {"type":"object","required":["name","code"],"properties":{
              "name":{"type":"string"},"kind":{"type":"string","enum":["seismic","wind"],"description":"Default seismic."},
              "code":{"type":"string","description":"As lateral_codes names it, e.g. ASCE 7-16, Eurocode 8 2004, User Coefficient. None: no code load."},
              "values":{"type":"object","description":"The code's inputs by key, e.g. {\"Dir\":\"X+Ecc\",\"R\":\"8\",\"Ss\":\"1.2\"}.","additionalProperties":{"type":["string","number"]}}}}
            """,
            Ask.Changes, Set, a => $"Set {a.Text("name")} to {a.Text("code")} {a.Text("kind", "seismic")}"),
    ];

    private static string Kind(JsonElement a) => a.Text("kind", "seismic").Equals("wind", StringComparison.OrdinalIgnoreCase) ? LateralCodes.Wind : LateralCodes.Seismic;

    private static Dictionary<string, string> Values(JsonElement a)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Object) return values;
        foreach (var p in v.EnumerateObject())
            values[p.Name] = p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString() ?? "", JsonValueKind.Number => p.Value.GetRawText(), JsonValueKind.True => "Yes", JsonValueKind.False => "No", _ => p.Value.GetRawText() };
        return values;
    }

    // ---- the codes ----

    private static JsonNode Codes(JsonElement a, ICsiLink e)
    {
        var kind = Kind(a);
        if (e is OfflineEtabs offline) return OfflineCodes(offline.Model, kind, a.Text("code"), a.Text("pattern"));
        var tables = CodeTables(e, kind);
        var code = a.Text("code");
        if (code.Length == 0)
            return new JsonObject { ["kind"] = kind, ["program"] = e.Program.Name, ["codes"] = new JsonArray(tables.Select(t => (JsonNode)t.Code).ToArray()), ["count"] = tables.Count };
        var table = Table(e, tables, kind, code);
        var result = (JsonObject)e.Raw($"read the inputs of {table.Code}", m =>
        {
            int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
            m.DatabaseTables.GetAllFieldsInTable(table.Key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
            keys ??= []; names ??= []; about ??= []; units ??= []; importable ??= [];
            return new JsonObject
            {
                ["kind"] = kind, ["code"] = table.Code, ["table"] = table.Name,
                ["inputs"] = new JsonArray(Enumerable.Range(0, Math.Min(n, keys.Length)).Where(i => i >= importable.Length || importable[i])
                    .Select(i => (JsonNode)new JsonObject { ["key"] = keys[i], ["name"] = i < names.Length ? names[i] : keys[i], ["units"] = i < units.Length ? units[i] : "", ["about"] = i < about.Length ? about[i] : "" }).ToArray()),
            };
        })!;
        if (a.Text("pattern") is { Length: > 0 } pattern && EditableTables.Read(e, table.Key, KeyField(e, table.Key), pattern) is { } row)
            result["pattern"] = new JsonObject(row.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        return result;
    }

    public static JsonObject OfflineCodes(StructuralModel m, string kind, string code = "", string pattern = "")
    {
        JsonObject Describe(LateralCode c) => new()
        {
            ["code"] = c.Name, ["method"] = c.About, ["programs"] = c.Programs,
            ["inputs"] = new JsonArray(c.Params.Select(p => (JsonNode)new JsonObject
            {
                ["key"] = p.Key, ["name"] = p.Label, ["default"] = p.Default, ["type"] = p.Kind.ToString().ToLowerInvariant(),
                ["choices"] = p.Kind == ParamKind.Story ? new JsonArray(m.Stories.Select(s => (JsonNode)s.Name).ToArray()) : p.Choices is null ? null : new JsonArray(p.Choices.Select(x => (JsonNode)x).ToArray()),
                ["about"] = p.Help.Length > 0 ? p.Help : null,
            }).ToArray()),
        };
        if (code.Length == 0)
        {
            var list = new JsonObject { ["kind"] = kind, ["codes"] = new JsonArray(LateralCodes.Of(kind).Select(c => (JsonNode)Describe(c)).ToArray()) };
            if (pattern.Length > 0 && m.LoadPatterns.Find(p => p.Name == pattern) is { } lp) list["pattern"] = Lateral(lp);
            return list;
        }
        var found = LateralCodes.Find(kind, code) ?? throw new ToolInputException($"There is no {kind.ToLowerInvariant()} code '{code}'. The codes: {string.Join(", ", LateralCodes.Of(kind).Select(c => c.Name))}.");
        var one = Describe(found);
        one["kind"] = kind;
        if (pattern.Length > 0 && m.LoadPatterns.Find(p => p.Name == pattern) is { } lp2) one["pattern"] = Lateral(lp2);
        return one;
    }

    /// <summary>A pattern's code load and what was computed from it, for the answer and the Define form.</summary>
    public static JsonNode? Lateral(LoadPattern p) => p.Auto is not { } d ? null : new JsonObject
    {
        ["pattern"] = p.Name, ["kind"] = d.Kind, ["code"] = d.Code,
        ["values"] = new JsonObject(d.Values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["results"] = new JsonObject(d.Results.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["story_forces"] = new JsonArray(d.Forces.Select(f => (JsonNode)new JsonObject { ["story"] = f.Story, ["elevation"] = f.Elevation, ["weight"] = f.Weight, ["fx"] = f.Fx, ["fy"] = f.Fy, ["mz"] = f.Mz }).ToArray()),
        ["error"] = d.Error,
    };

    // ---- setting one ----

    private static JsonNode Set(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name").Trim();
        var kind = Kind(a);
        var code = a.Need("code").Trim();
        var values = Values(a);
        return e is OfflineEtabs offline ? SetOffline(offline, name, kind, code, values) : SetLive(e, name, kind, code, values);
    }

    private static JsonNode SetOffline(OfflineEtabs link, string name, string kind, string code, Dictionary<string, string> values)
    {
        var none = code.Equals("None", StringComparison.OrdinalIgnoreCase);
        var have = link.Model.LoadPatterns.Find(p => p.Name == name);
        if (have is not null && !none && !have.Type.Equals(kind, StringComparison.OrdinalIgnoreCase))
            throw new ToolInputException($"The load pattern {name} is a {have.Type} pattern: a {kind.ToLowerInvariant()} code load needs a {kind} pattern. Give another name, or change its type first.");
        if (none)
        {
            if (have?.Auto is null) throw new ToolInputException($"{name} has no code load to take off.");
            return link.Edit(m =>
            {
                m.LoadPatterns.First(p => p.Name == name).Auto = null;
                m.PointLoads.RemoveAll(l => l.Pattern == name);
                return (JsonNode)new JsonObject { ["pattern"] = name, ["code"] = "None", ["done"] = $"{name} has no code load now; its story forces are taken off." };
            });
        }
        var def = new LateralDef { Kind = kind, Code = code, Values = values };
        try { LateralLoads.Check(link.Model, def); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        return link.Edit(m =>
        {
            var p = m.LoadPatterns.Find(x => x.Name == name) ?? m.AddLoadPattern(name, kind, 0);
            p.Auto = def;
            LateralLoads.Refresh(m);
            var answer = (JsonObject)Lateral(p)!;
            answer["note"] = p.Auto.Error is null
                ? "The story forces are the pattern's joint loads, at each story's mass center with the eccentricity; they are worked out again after each change to the model. The .e2k carries them as joint loads."
                : "The code load is kept, but its forces could not be worked out yet: " + p.Auto.Error;
            return (JsonNode)answer;
        });
    }

    // ---- a live program: its own table for the code ----

    private sealed record CodeTable(string Code, string Key, string Name);

    private static List<CodeTable> CodeTables(ICsiLink e, string kind)
    {
        var marker = kind == LateralCodes.Wind ? "Auto Wind - " : "Auto Seismic - ";
        return e.Tables().Select(t => (t, at: t.Name.IndexOf(marker, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.at >= 0).Select(x => new CodeTable(x.t.Name[(x.at + marker.Length)..].Trim(), x.t.Key, x.t.Name))
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static CodeTable Table(ICsiLink e, List<CodeTable> tables, string kind, string code) =>
        tables.FirstOrDefault(t => t.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? tables.FirstOrDefault(t => Norm(t.Code) == Norm(code))
        ?? throw new ToolInputException(tables.Count == 0
            ? $"{e.Program.Name} lists no auto {kind.ToLowerInvariant()} tables, so a code load cannot be set here. Give the story forces as joint loads instead."
            : $"{e.Program.Name} has no {kind.ToLowerInvariant()} code '{code}'. Its codes: {string.Join(", ", tables.Select(t => t.Code))}.");

    /// <summary>The field that names the pattern in a table: Name in ETABS, LoadPat in SAP2000.</summary>
    private static string KeyField(ICsiLink e, string key) => (string)e.Raw("read a table's fields", m =>
    {
        int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
        m.DatabaseTables.GetAllFieldsInTable(key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
        keys ??= [];
        return keys.FirstOrDefault(k => k.Equals("Name", StringComparison.OrdinalIgnoreCase)) ?? keys.FirstOrDefault(k => k.Equals("LoadPat", StringComparison.OrdinalIgnoreCase))
            ?? keys.FirstOrDefault(k => k.Contains("Pat", StringComparison.OrdinalIgnoreCase)) ?? (keys.Length > 0 ? keys[0] : "Name");
    })!;

    private static JsonNode SetLive(ICsiLink e, string name, string kind, string code, Dictionary<string, string> values)
    {
        var have = e.LoadPatterns().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (have is null) e.AddLoadPattern(name, kind, 0);
        else if (!have.Type.Equals(kind, StringComparison.OrdinalIgnoreCase))
            throw new ToolInputException($"The load pattern {name} is a {have.Type} pattern: a {kind.ToLowerInvariant()} code load needs a {kind} pattern. Give another name.");
        var patterns = e.Tables().FirstOrDefault(t => t.Name.Equals("Load Pattern Definitions", StringComparison.OrdinalIgnoreCase))
            ?? throw new EtabsException($"{e.Program.Name} has no table 'Load Pattern Definitions' to give {name} its code.");
        var patternKey = KeyField(e, patterns.Key);
        if (code.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            EditableTables.Put(e, patterns.Key, patternKey, [EditableTables.Row((patternKey, name), ("AutoLoad", "None"))], $"take the code load off {name}");
            return new JsonObject { ["pattern"] = name, ["code"] = "None", ["done"] = $"{name} has no auto {kind.ToLowerInvariant()} load now." };
        }
        var table = Table(e, CodeTables(e, kind), kind, code);
        var key = KeyField(e, table.Key);
        // The pattern takes the code first, which makes its row in the code's table with the program's defaults;
        // then the given inputs go into that row, and the row's other fields keep the defaults.
        EditableTables.Put(e, patterns.Key, patternKey, [EditableTables.Row((patternKey, name), ("AutoLoad", table.Code))], $"make {name} a {table.Code} load");
        if (values.Count > 0)
        {
            var row = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase) { [key] = name };
            EditableTables.Put(e, table.Key, key, [row], $"give {name} its {table.Code} inputs");
        }
        var back = EditableTables.Read(e, table.Key, key, name)
            ?? throw new EtabsException($"{e.Program.Name} took {table.Code} for {name}, but its table '{table.Name}' has no row for {name} when read back.");
        foreach (var (field, value) in values)
            if (back.TryGetValue(field, out var got) && !Same(got, value))
                throw new EtabsException($"{e.Program.Name} kept {field} = '{got}' for {name}, not '{value}': check the value against lateral_codes for {table.Code}.");
        return new JsonObject
        {
            ["pattern"] = name, ["kind"] = kind, ["code"] = table.Code, ["table"] = table.Name,
            ["read_back"] = new JsonObject(back.Where(kv => kv.Key is not ("GUID")).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["note"] = $"{e.Program.Name} computes the story forces when the analysis runs. They show in its Load Patterns > Modify Lateral Load, and in the table '{table.Name}'.",
        };
    }

    private static bool Same(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return double.TryParse(a, System.Globalization.NumberStyles.Float, inv, out var x) && double.TryParse(b, System.Globalization.NumberStyles.Float, inv, out var y)
            && Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Abs(y));
    }
}
