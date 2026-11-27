using System.Globalization;
using System.Text.Json.Nodes;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The Engine's ETABS Define > Load Patterns and Define > Load Cases forms, proved in ETABS itself (Ahmed 2026-11-27: every
/// form must work exactly as ETABS's): in the Modeler's part each form's values are set as the form sets them (ETABS 23.2's own
/// definition-table rows, through define_table_rows, the values the form writes by default), the .e2k is written, and in the
/// scratch ETABS each table is read back from ETABS's database and every field must hold the value the form set.
/// A value word ETABS spells another way shows here as a failure, which is how the forms' value words are corrected.
/// </summary>
public static class EtabsFormRoundTrip
{
    /// <summary>The patterns and cases made first (name, add_load_pattern or define_load_case arguments).</summary>
    private static readonly (string Tool, string Json)[] Made =
    [
        ("add_load_pattern", """{"name":"CPM-EQ22","type":"Seismic","self_weight":0}"""),
        ("add_load_pattern", """{"name":"CPM-EQUC","type":"Seismic","self_weight":0}"""),
        ("add_load_pattern", """{"name":"CPM-W16","type":"Wind","self_weight":0}"""),
        ("add_load_pattern", """{"name":"CPM-NOTX","type":"Notional","self_weight":0}"""),
        ("define_function", """{"name":"CPM-RSF","kind":"spectrum","points":[[0,0.5],[0.5,1],[1,0.6],[4,0.15]]}"""),
        ("define_load_case", """{"name":"CPM-LS","type":"Linear Static","loads":[{"pattern":"Dead","factor":1.4}]}"""),
        ("define_load_case", """{"name":"CPM-RSE","type":"Response Spectrum","modal_case":"Modal","modal_combo":"CQC","dir_combo":"SRSS","damping":0.05,"eccentricity":0.05,"loads":[{"accel":"U1","factor":9.80665,"function":"CPM-RSF"}]}"""),
    ];

    /// <summary>(form, table, definition name, rows): what each form writes, as the form writes it.</summary>
    public static (string Form, string Table, string Name, Dictionary<string, string>[] Rows)[] Forms(string top, string bottom) =>
    [
        ("Load Pattern Definitions > ASCE 7-22 Seismic Load Pattern", "Load Pattern Definitions - Auto Seismic - ASCE 7-22", "CPM-EQ22",
            [R(("XDir", "Yes"), ("XDirPlusE", "No"), ("XDirMinusE", "No"), ("YDir", "No"), ("YDirPlusE", "No"), ("YDirMinusE", "No"), ("EccRatio", "0.05"),
               ("TopStory", top), ("BotStory", bottom), ("PeriodType", "Program Calculated"), ("CtAndX", "0.028; 0.8"), ("MethodType", "Method 2"),
               ("Smax", "1"), ("Tmax", "1"), ("Sds", "1.93"), ("Sd1", "1.45"), ("TL", "8"), ("SiteClass", "Default"), ("S1", "0.869"),
               ("R", "8"), ("I", "1"), ("Omega", "3"), ("Cd", "5.5"))]),
        ("Load Pattern Definitions > User Defined Seismic Load Pattern (User Coefficient)", "Load Pattern Definitions - Auto Seismic - User Coefficient", "CPM-EQUC",
            [R(("XDir", "No"), ("XDirPlusE", "No"), ("XDirMinusE", "No"), ("YDir", "Yes"), ("YDirPlusE", "No"), ("YDirMinusE", "No"), ("EccRatio", "0.05"),
               ("TopStory", top), ("BotStory", bottom), ("C", "0.06"), ("K", "1.2"))]),
        ("Load Pattern Definitions > ASCE 7-16 Wind Load Pattern", "Load Pattern Definitions - Auto Wind - ASCE 7-16", "CPM-W16",
            [R(("Exposure", "Diaphragms"), ("TopStory", top), ("BotStory", bottom), ("Parapet", "No"), ("UserCp", "No"),
               ("ASCECase", "Create All Sets"), ("e1", "0.15"), ("e2", "0.15"), ("WindSpeed", "85"), ("ExpType", "B"), ("GrdElevFact", "1"), ("kzt", "1"),
               ("GustFact", "0.85"), ("Kd", "0.85"))]),
        ("Load Pattern Definitions > Auto Notional Load Generation", "Load Pattern Definitions - Auto Notional Loads", "CPM-NOTX",
            [R(("BasePattern", "Dead"), ("LoadRatio", "0.002"), ("LoadDir", "Global X"))]),
        ("Load Case Data (Linear Static)", "Load Case Definitions - Linear Static", "CPM-LS",
            [R(("StiffType", "Preset P-Delta"), ("LoadType", "Load Pattern"), ("LoadName", "Dead"), ("LoadSF", "1.4"))]),
        ("Load Case Data (Response Spectrum)", "Load Case Definitions - Response Spectrum", "CPM-RSE",
            [R(("LoadName", "U1"), ("Function", "CPM-RSF"), ("TransAccSF", "9.80665"), ("Angle", "0"), ("ModalCase", "Modal"), ("ModalCombo", "CQC"),
               ("DirCombo", "SRSS"), ("EccenRatio", "0.05"))]),
        ("Load Case Data (Response Spectrum) > Modal Damping", "Load Case Definitions - Damping - Modal", "CPM-RSE",
            [R(("DampType", "Constant Damping"), ("ConstDamp", "0.04"))]),
    ];

    private static Dictionary<string, string> R(params (string Key, string Value)[] fields) => fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    /// <summary>The top story and the base, as the form fills Top Story and Bottom Story.</summary>
    private static (string Top, string Bottom) Stories(ICsiLink link)
    {
        var s = link.Stories().OrderBy(x => x.Elevation).ToList();
        return (s.Count > 0 ? s[^1].Name : "Story1", s.Count > 0 ? s[0].Name : "Base");
    }

    /// <summary>In the Modeler's part, before the .e2k is written: each form's values set as the form sets them.</summary>
    public static void Define(FieldContext ctx, OfflineEtabs modeler)
    {
        foreach (var (tool, json) in Made)
        {
            var name = JsonNode.Parse(json)!["name"]!.GetValue<string>();
            if (tool == "add_load_pattern" && modeler.LoadPatterns().Any(p => p.Name == name)) continue;
            if (tool == "define_load_case" && modeler.Model.LoadCases.Exists(c => c.Name == name)) continue;
            ctx.Tool(tool, json, null, $"ETABS forms: {name} made for the form round trip");
        }
        var (top, bottom) = Stories(modeler);
        foreach (var (form, table, name, rows) in Forms(top, bottom))
        {
            var json = new JsonObject { ["table"] = table, ["name"] = name, ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject(r.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))).ToArray()) };
            ctx.Tool("define_table_rows", json.ToJsonString(), a => FieldContext.Expect(Int(a["rows"]) == rows.Length,
                $"{table}: {name} holds {a["rows"]} rows, the form set {rows.Length}"), $"ETABS form {form}: {table} set as the form sets it");
        }
    }

    /// <summary>In the scratch ETABS that opened the .e2k: every table read back from ETABS, field by field.</summary>
    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        var (top, bottom) = FieldTest.IsLive(link) ? Stories(link) : ("", "");
        foreach (var group in Forms(top, bottom).GroupBy(f => f.Form))
            ctx.Step($"ETABS form {group.Key}: ETABS holds every value the Engine's form set", () =>
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no ETABS table to read back");
                var tables = link.Tables();
                var wrong = new List<string>();
                foreach (var (_, table, name, rows) in group)
                {
                    TableData data;
                    try
                    {
                        var key = (tables.FirstOrDefault(t => t.Name.Equals(table, StringComparison.OrdinalIgnoreCase)) ?? throw new EtabsException($"ETABS has no table '{table}'")).Key;
                        data = link.ReadTable(key, null, null);
                    }
                    catch (EtabsException ex) { wrong.Add($"{table}: ETABS did not read it back ({ex.Message})"); continue; }
                    var fields = data.Fields.ToArray();
                    var at = Array.FindIndex(fields, f => f.Equals(DefineKey(table), StringComparison.OrdinalIgnoreCase));
                    var mine = data.Rows.Where(r => at >= 0 && at < r.Length && r[at] == name).ToList();
                    if (mine.Count != rows.Length) { wrong.Add($"{table}: ETABS has {mine.Count} rows for {name}, the form set {rows.Length}"); continue; }
                    for (var i = 0; i < rows.Length; i++)
                        foreach (var (field, value) in rows[i])
                        {
                            var f = Array.FindIndex(fields, x => x.Equals(field, StringComparison.OrdinalIgnoreCase));
                            var got = f >= 0 && f < mine[i].Length ? mine[i][f] : null;
                            if (!Same(value, got)) wrong.Add($"{table} {name} row {i + 1} {field}: form {value}, ETABS {got ?? "(no field)"}");
                        }
                }
                FieldContext.Expect(wrong.Count == 0, string.Join("; ", wrong.Take(12)) + (wrong.Count > 12 ? $"; and {wrong.Count - 12} more" : ""));
                return $"{group.Count()} tables, every field the same";
            });
    }

    private static string DefineKey(string table) => CsiProMax.Model.EtabsLoadTables.Fields.TryGetValue(table, out var f) ? f[0].Key : "Name";

    private static bool Same(string want, string? got)
    {
        if (got is null) return false;
        if (double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        return string.Equals(want.Trim(), got.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
