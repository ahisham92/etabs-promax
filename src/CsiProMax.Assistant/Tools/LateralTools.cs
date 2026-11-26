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

    /// <summary>
    /// Saves the model under its own file name before an import of SAP2000's patterns' table, and gives that file. A model
    /// with no file of its own is refused: if the import lost a pattern there would be nothing to open again.
    /// </summary>
    private static string SaveFirst(ICsiLink e, string pattern, string code)
    {
        var program = e.Program.Name;
        return (string)e.Raw($"save the model before giving {pattern} the code {code}", m =>
        {
            string file;
            try { file = Convert.ToString(m.GetModelFilename(true)) ?? ""; } catch (Exception) { file = ""; }
            if (!new[] { ".sdb", ".edb", ".fdb" }.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                throw new EtabsException($"{program}'s API has no call for {code}, so it goes in through the table 'Load Pattern Definitions', and the model is saved first so that it can be opened again if {program} loses a pattern. This model has no file of its own yet: save it (File > Save As), then ask again.");
            if (Convert.ToInt32(m.File.Save(file)) != 0)
                throw new EtabsException($"{program} refused to save the model as {System.IO.Path.GetFileName(file)} before the import, so {pattern} was not given {code}: nothing was changed.");
            return file;
        })!;
    }

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

    /// <summary>A code's table. Its name and its key can name the code differently (ETABS 23: "KDS 2019" in the name,
    /// "KDS 41 17 00:2019" in the key, 0.4.34 field test); the key's is the program's own, so it goes into AutoLoad.</summary>
    private sealed record CodeTable(string Code, string Key, string Name, string KeyCode);

    private static List<CodeTable> CodeTables(ICsiLink e, string kind)
    {
        var marker = kind == LateralCodes.Wind ? "Auto Wind - " : "Auto Seismic - ";
        // "Loads to Diaphragms" and "Loads to Stories" are ETABS's worked-out auto loads, read only, not codes (0.4.55
        // ETABS run: every field "importable False", so their rows were refused).
        return e.Tables().Select(t => (t, at: t.Name.IndexOf(marker, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.at >= 0 && x.t.Import != 0 && !x.t.Name.EndsWith("Loads to Diaphragms", StringComparison.OrdinalIgnoreCase) && !x.t.Name.EndsWith("Loads to Stories", StringComparison.OrdinalIgnoreCase)).Select(x => new CodeTable(x.t.Name[(x.at + marker.Length)..].Trim(), x.t.Key, x.t.Name,
                x.t.Key.IndexOf(marker, StringComparison.OrdinalIgnoreCase) is var k and >= 0 ? x.t.Key[(k + marker.Length)..].Trim() : x.t.Name[(x.at + marker.Length)..].Trim()))
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static CodeTable Table(ICsiLink e, List<CodeTable> tables, string kind, string code) =>
        tables.FirstOrDefault(t => t.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? tables.FirstOrDefault(t => t.KeyCode.Equals(code, StringComparison.OrdinalIgnoreCase))
        ?? tables.FirstOrDefault(t => Norm(t.Code) == Norm(code)) ?? tables.FirstOrDefault(t => Norm(t.KeyCode) == Norm(code))
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
        // SAP2000 26 refuses every import of "Load Pattern Definitions" for a model with loads and is left with almost no
        // load patterns (0.4.22 and 0.4.34 field tests: 9 patterns, then 1), so its patterns' table is never written here.
        var sap = e.Program.Key == CsiProgram.Sap2000.Key;
        if (code.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            if (sap)
            {
                // SAP2000's own call (0.4.40 field test: its patterns' table cannot take the None).
                SapLateral.SetNone(e, name, kind);
                return new JsonObject { ["pattern"] = name, ["code"] = "None", ["set_by"] = $"LoadPatterns.Auto{(kind == LateralCodes.Wind ? "Wind" : "Seismic")}.SetNone", ["done"] = $"{name} has no auto {kind.ToLowerInvariant()} load now." };
            }
            EditableTables.Put(e, patterns.Key, patternKey, [EditableTables.Row((patternKey, name), ("AutoLoad", "None"))], $"take the code load off {name}");
            return new JsonObject { ["pattern"] = name, ["code"] = "None", ["done"] = $"{name} has no auto {kind.ToLowerInvariant()} load now." };
        }
        var table = Table(e, CodeTables(e, kind), kind, code);
        var key = KeyField(e, table.Key);
        // The pattern takes the code first, which makes its row in the code's table with the program's defaults;
        // then the given inputs go into that row, and the row's other fields keep the defaults.
        // ETABS 23 takes the AutoLoad but makes no row in the code's table from it (0.4.34 field test: every code read back
        // empty, except where inputs were given): the row is always written to the code's table too, with the inputs given.
        if (!sap)
            EditableTables.Put(e, patterns.Key, patternKey, [EditableTables.Row((patternKey, name), ("AutoLoad", table.KeyCode))], $"make {name} a {table.Code} load");
        // SAP2000 26 takes a code only by its own Set call: a code's table takes no row for a pattern that does not have
        // the code yet (0.4.40 field test: 0 errors and nothing kept, for 70 codes). The call sets plain starting values;
        // the inputs given then go into the row it made.
        string? setBy = null;
        if (sap)
        {
            if (SapLateral.SetCode(e, name, kind, table.Code, values) is { } made)
            {
                setBy = made.Method;
                var read = (made.Back?.DeepClone() as JsonObject) ?? (EditableTables.Read(e, table.Key, key, name) is { } r ? new JsonObject(r.Where(kv => kv.Key is not "GUID").Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))) : null)
                    ?? throw new EtabsException($"{e.Program.Name} took {table.Code} for {name} (by {setBy}), but neither its Get call nor its table '{table.Name}' gives it back.");
                return new JsonObject
                {
                    ["pattern"] = name, ["kind"] = kind, ["code"] = table.Code, ["table"] = table.Name,
                    ["set_by"] = "LoadPatterns.Auto" + (kind == LateralCodes.Wind ? "Wind." : "Seismic.") + setBy,
                    ["read_back"] = read,
                    ["note"] = $"{e.Program.Name} computes the story forces when the analysis runs. They show in its Load Patterns > Modify Lateral Load, and in the table '{table.Name}'.",
                };
            }
            // No Set call for this code (ASCE 7-22, KDS, AASHTO...): the pattern's row and the code's row go in together.
            // That import is the one that lost patterns in the 0.4.22 and 0.4.34 field tests: the model is saved under its
            // own file name first (the engineer is always asked before, OutputPaths.SavesBeforeLateral) and opened again
            // from that file when SAP2000 lost or changed any pattern or code, as edit_table does.
            var saved = SaveFirst(e, name, table.Code);
            var wasLocked = e.Info().Locked;
            string log;
            string? second = null;
            var what = $"give {name} the {kind.ToLowerInvariant()} code {table.Code}";
            try
            {
                // The other patterns keep their codes, those given through the API too, by a blank AutoLoad (see PutAutoLoad).
                log = EditableTables.PutAutoLoad(e, name, kind == LateralCodes.Wind ? "Wind" : "Quake", table.KeyCode, table.Key, values, what);
            }
            catch (EtabsException ex)
            {
                var reopened = (bool)e.Raw("open the model again as it was saved", m => { try { return Convert.ToInt32(m.File.OpenFile(saved)) == 0; } catch (Exception) { return false; } })!;
                try { e.Refresh(); } catch (Exception rex) when (rex is EtabsException or InvalidOperationException) { /* the next call reads it */ }
                // A SAP2000 session that opened an .s2k takes no table import: the same import, in a second SAP2000 (SapFreshProcess).
                if (reopened && SapFreshProcess.IsSessionRefusal(e.Program, ex.Message))
                {
                    try
                    {
                        log = SapFreshProcess.Apply(e, saved, fresh => EditableTables.PutAutoLoad(fresh, name, kind == LateralCodes.Wind ? "Wind" : "Quake", table.KeyCode, table.Key, values, what));
                        second = SapFreshProcess.Said(saved);
                    }
                    catch (EtabsException ex2)
                    {
                        throw new EtabsException($"{ex.Message} The model was opened again as it was saved just before ({System.IO.Path.GetFileName(saved)}). Making the change in a second {e.Program.Name} did not work either: {ex2.Message}");
                    }
                }
                else throw new EtabsException(reopened
                    ? $"{ex.Message} The model was opened again as it was saved just before ({System.IO.Path.GetFileName(saved)}), so nothing of the import is left in it and every pattern is as it was."
                    : $"{ex.Message} Opening {System.IO.Path.GetFileName(saved)} again was refused: open it yourself (File > Open), without saving the model first.");
            }
            var got = EditableTables.Read(e, table.Key, key, name)
                ?? throw new EtabsException($"{e.Program.Name} took {name}'s rows for {table.Code}, but its table '{table.Name}' has no row for {name} when read back. Its import log: {log}");
            foreach (var (field, value) in values)
                if (got.TryGetValue(field, out var now) && !Same(now, value))
                    throw new EtabsException($"{e.Program.Name} kept {field} = '{now}' for {name}, not '{value}': check the value against lateral_codes for {table.Code}. Its import log: {log}");
            return new JsonObject
            {
                ["pattern"] = name, ["kind"] = kind, ["code"] = table.Code, ["table"] = table.Name,
                ["set_by"] = $"the tables 'Load Pattern Definitions' and '{table.Name}', in one import ({e.Program.Name}'s API has no call for this code)",
                ["read_back"] = new JsonObject(got.Where(kv => kv.Key is not "GUID").Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["note"] = $"{e.Program.Name} filled in its own defaults for the inputs not given. It computes the story forces when the analysis runs.",
                ["saved_before_edit"] = $"The model was saved as {System.IO.Path.GetFileName(saved)} just before the import, so that it could be opened again if {e.Program.Name} lost a pattern. Tell the engineer that the file was saved.",
                ["unlocked"] = wasLocked ? $"The model was locked (it held analysis results): it was unlocked for the import, so the results are deleted. Run the analysis again." : null,
                ["second_sap2000"] = second,
            };
        }
        else
        {
            var row = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase) { [key] = name };
            // ETABS 23's User Loads row needs its load set ("Field name: LOAD SET NUMBER ... record skipped" with the
            // name alone, 0.4.55 ETABS run): one set, at the mass center, unless given.
            if (table.Code.Equals("User Loads", StringComparison.OrdinalIgnoreCase))
                foreach (var (f, v) in new[] { ("NumberSets", "1"), ("SetNumber", "1"), ("LoadAtCM", "Yes") })
                    row.TryAdd(f, v);
            EditableTables.Put(e, table.Key, key, [row], $"give {name} its {table.Code} inputs");
        }
        var back = EditableTables.Read(e, table.Key, key, name)
            ?? throw new EtabsException($"{e.Program.Name} took {table.Code} for {name}, but its table '{table.Name}' has no row for {name} when read back.");
        foreach (var (field, value) in values)
            if (back.TryGetValue(field, out var got) && !Same(got, value))
                throw new EtabsException($"{e.Program.Name} kept {field} = '{got}' for {name}, not '{value}': check the value against lateral_codes for {table.Code}.");
        return new JsonObject
        {
            ["pattern"] = name, ["kind"] = kind, ["code"] = table.Code, ["table"] = table.Name, ["set_by"] = setBy is null ? "the table" : "LoadPatterns.Auto" + (kind == LateralCodes.Wind ? "Wind." : "Seismic.") + setBy,
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
