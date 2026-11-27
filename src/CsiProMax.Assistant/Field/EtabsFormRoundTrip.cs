using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant;

/// <summary>
/// The Engine's ETABS forms, proved in ETABS itself (Ahmed 2026-11-27: every form must work exactly as ETABS's): in the
/// Modeler's part each form's values are set as the form sets them (ETABS's own table rows, through define_table_rows),
/// the .e2k is written, and in the scratch ETABS each table is read back from ETABS's database and every field must hold
/// the value the form set. A form thread adds its tables to <see cref="Forms"/>.
/// </summary>
public static class EtabsFormRoundTrip
{
    private const string L = "Link Property Definitions - ";

    // The link's own fields on its first row, as Link Property Data writes them.
    private static (string, string)[] Own(string notes) =>
        [("Mass", "0.01"), ("Weight", "0.2"), ("RotInert1", "0"), ("RotInert2", "0"), ("RotInert3", "0"), ("DefLength", "1"), ("DefArea", "1"), ("PDM2I", "0.25"), ("PDM2J", "0.25"), ("PDM3I", "0"), ("PDM3J", "0"), ("StiffDFact", "1"), ("Notes", notes)];
    private static readonly (string, string)[] Nl = [("StiffLOpt", "Effective from Zero"), ("StiffDOpt", "Initial")];

    /// <summary>(form, table, definition name, rows): what each form writes, as the form writes it.</summary>
    public static readonly (string Form, string Table, string Name, Dictionary<string, string>[] Rows)[] Forms =
    [
        ("Link Property Data (Linear, coupled)", L + "Linear", "CPM-LLIN",
            [R([.. Own("linear"), ("U1Status", "Active"), ("U2Status", "Active"), ("U3Status", "Inactive"), ("R1Status", "Inactive"), ("R2Status", "Inactive"), ("R3Status", "Fixed"),
                ("StiffU1", "1000"), ("StiffU1U2", "30"), ("StiffU2", "500"), ("DampU1", "2"), ("DampU2", "0"), ("DJ2", "0.5")])]),
        ("Link Acceptance Criteria Definition (Force)", L + "Acceptance Criteria - Force", "CPM-LLIN",
            [R(("Symmetric", "No"), ("F1", "Yes"), ("F2", "No"), ("F3", "No"), ("M1", "No"), ("M2", "No"), ("M3", "No"), ("IOPosF1", "10"), ("LSPosF1", "20"), ("CPPosF1", "30"), ("IONegF1", "11"), ("LSNegF1", "21"), ("CPNegF1", "31"))]),
        ("Link Property Data (MultiLinear Elastic)", L + "Multilinear - Elastic", "CPM-LMLE",
            [R([.. Own("mle"), .. Nl, ("Direction", "U1"), ("Fixed", "No"), ("TransKE", "100"), ("TransCE", "0"), ("TransPoint", "1"), ("Displ", "-10"), ("Force", "-1")]),
             R(("Direction", "U1"), ("TransPoint", "2"), ("Displ", "-1"), ("Force", "-1")), R(("Direction", "U1"), ("TransPoint", "3"), ("Displ", "0"), ("Force", "0")),
             R(("Direction", "U1"), ("TransPoint", "4"), ("Displ", "1"), ("Force", "1")), R(("Direction", "U1"), ("TransPoint", "5"), ("Displ", "10"), ("Force", "1"))]),
        ("Link Property Data (Damper - Exponential, fixed length)", L + "Damper - Exponential", "CPM-LDMP",
            [R([.. Own("damper"), .. Nl, ("Direction", "U1"), ("Fixed", "No"), ("TransKE", "0"), ("TransCE", "0"), ("TransK", "500"), ("TransC", "10"), ("TransCExp", "0.5"), ("FLDamper", "Yes"), ("FLDLength", "4"), ("FDLModulus", "4176000"), ("FDLArea", "0.007")])]),
        ("Link Property Data (Gap)", L + "Gap", "CPM-LGAP",
            [R([.. Own("gap"), .. Nl, ("Direction", "U1"), ("Fixed", "No"), ("TransKE", "0"), ("TransCE", "0"), ("TransK", "1000"), ("TransOpen", "0.04")])]),
        ("Link Property Data (Hook)", L + "Hook", "CPM-LHOOK",
            [R([.. Own("hook"), .. Nl, ("Direction", "U1"), ("Fixed", "No"), ("TransKE", "0"), ("TransCE", "0"), ("TransK", "1000"), ("TransOpen", "0.04")])]),
        ("Link Property Data (Plastic (Wen))", L + "Plastic Wen", "CPM-LWEN",
            [R([.. Own("wen"), .. Nl, ("Direction", "U1"), ("Fixed", "No"), ("TransKE", "0"), ("TransCE", "0"), ("TransK", "1000"), ("TransYield", "50"), ("TransRatio", "0.05"), ("TransExp", "2")])]),
        ("Link Property Data (Rubber Isolator)", L + "Isolator - Rubber", "CPM-LRUB",
            [R([.. Own("rubber"), .. Nl, ("Direction", "U1"), ("Fixed", "No"), ("TransKE", "10000"), ("TransCE", "0")]),
             R(("Direction", "U2"), ("Fixed", "No"), ("TransKE", "50"), ("TransCE", "0"), ("DJ", "0"), ("TransK", "100"), ("TransYield", "10"), ("TransRatio", "0.1"))]),
        ("Link Property Data (Friction Isolator)", L + "Isolator - Friction", "CPM-LFRI",
            [R([.. Own("friction"), .. Nl, ("Direction", "U1"), ("Fixed", "No"), ("TransKE", "10000"), ("TransCE", "0"), ("TransK", "10000"), ("TransC", "0")]),
             R(("Direction", "U2"), ("Fixed", "No"), ("TransKE", "50"), ("TransCE", "0"), ("DJ", "0"), ("TransK", "1000"), ("Slow", "0.03"), ("Fast", "0.06"), ("Rate", "1"), ("Radius", "6"))]),
    ];

    private static Dictionary<string, string> R(params (string Key, string Value)[] fields) => fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    /// <summary>In the Modeler's part, before the .e2k is written: each form's values set as the form sets them.</summary>
    public static void Define(FieldContext ctx)
    {
        foreach (var (form, table, name, rows) in Forms)
        {
            var json = new JsonObject { ["table"] = table, ["name"] = name, ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject(r.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))).ToArray()) };
            ctx.Tool("define_table_rows", json.ToJsonString(), a => FieldContext.Expect(Int(a["rows"]) == rows.Length,
                $"{table}: {name} holds {a["rows"]} rows, the form set {rows.Length}"), $"ETABS form {form}: {table} set as the form sets it"); // ETABS itself
        }
    }

    /// <summary>In the scratch ETABS that opened the .e2k: every table read back from ETABS, field by field.</summary>
    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        if (link.Program.Key != CsiProgram.Etabs.Key) return;
        foreach (var group in Forms.GroupBy(f => f.Form))
            ctx.Step($"ETABS form {group.Key}: ETABS holds every value the Engine's form set", () => // ETABS itself
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no ETABS table to read back"); // ETABS itself
                var tables = link.Tables();
                var wrong = new List<string>();
                foreach (var (_, table, name, rows) in group)
                {
                    var t = tables.FirstOrDefault(x => x.Name.Equals(table, StringComparison.OrdinalIgnoreCase));
                    if (t is null) { wrong.Add($"ETABS has no table '{table}' (it lists only tables with data: the link did not go in)"); continue; } // ETABS itself
                    TableData data;
                    try { data = link.ReadTable(t.Key, null, null); }
                    catch (EtabsException ex) { wrong.Add($"{table}: ETABS did not read it back ({ex.Message})"); continue; } // ETABS itself
                    var fields = data.Fields.ToArray();
                    var key = Array.FindIndex(fields, f => f.Equals("Name", StringComparison.OrdinalIgnoreCase));
                    var mine = data.Rows.Where(r => key >= 0 && key < r.Length && r[key] == name).ToList();
                    if (mine.Count != rows.Length) { wrong.Add($"{table}: ETABS has {mine.Count} rows for {name}, the form set {rows.Length}"); continue; } // ETABS itself
                    for (var i = 0; i < rows.Length; i++)
                        foreach (var (field, value) in rows[i])
                        {
                            var at = Array.FindIndex(fields, f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
                            var got = at >= 0 && at < mine[i].Length ? mine[i][at] : null;
                            if (!Same(value, got)) wrong.Add($"{table} {name} row {i + 1} {field}: form {value}, ETABS {got ?? "(no field)"}"); // ETABS itself
                        }
                }
                FieldContext.Expect(wrong.Count == 0, string.Join("; ", wrong.Take(12)) + (wrong.Count > 12 ? $"; and {wrong.Count - 12} more" : ""));
                return $"{group.Count()} table{(group.Count() == 1 ? "" : "s")}, every field the same";
            });
    }

    private static bool Same(string want, string? got)
    {
        if (got is null) return false;
        if (double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        // ETABS shortens some words in its tables (pictured: "Effective from ...", "Initial").
        return string.Equals(want.Trim(), got.Trim(), StringComparison.OrdinalIgnoreCase) || got.Trim().StartsWith(want.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
