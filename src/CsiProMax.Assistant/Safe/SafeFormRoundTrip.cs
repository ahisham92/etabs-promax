using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant.Safe;

/// <summary>
/// The Engine's SAFE forms, proved in SAFE itself (Ahmed 2026-11-27: every SAFE form identical to SAFE's and working as
/// SAFE's): in the Modeler's part, values are set as the SAFE forms set them (rows of SAFE's own tables, "SAFE | ...",
/// through define_table_rows), the .f2k is written with them, and in the scratch SAFE each table is read back from SAFE's
/// database and every field must hold the value the form set. A SAFE form batch adds its tables to <see cref="Forms"/>.
/// Fields are SAFE's .f2k field names; SAFE's API may give a field by its key (SafeTables.All has both), so either matches.
/// </summary>
public static class SafeFormRoundTrip
{
    /// <summary>The concrete the slab property form picks: the first concrete of the Modeler's model.</summary>
    private const string Concrete = "{concrete}";

    /// <summary>(form, SAFE table, definition name or "" for a one-row options table, rows by SAFE's field names).</summary>
    public static readonly (string Form, string Table, string Name, Dictionary<string, string>[] Rows)[] Forms =
    [
        ("Define > Load Patterns", "Load Pattern Definitions", "CPM-SF-LIVE", [R(("Is Auto Load", "No"), ("Type", "Live"), ("Self Weight Multiplier", "0"))]),
        ("Define > Load Combinations", "Load Combination Definitions", "CPM-SF-COMB",
            [R(("Type", "Linear Add"), ("Is Auto", "No"), ("Load Name", "Dead"), ("SF", "1.2")), R(("Type", "Linear Add"), ("Is Auto", "No"), ("Load Name", "Live"), ("SF", "1.6"))]),
        ("Define > Groups", "Group Definitions", "CPM-SF-GRP", [R(("Color", "Red"), ("Steel Design?", "No"), ("Concrete Design?", "Yes"), ("Composite Design?", "No"))]),
        ("Define > Section Properties > Reinforcing Bar Sizes", "Reinforcing Bar Sizes", "CPM-SF-B22", [R(("Diameter", "0.022"), ("Area", "0.00038"))]),
        ("Define > Section Properties > Slab Properties", "Slab Property Definitions", "CPM-SF-SLAB",
            [R(("Modeling Type", "Shell-Thin"), ("Property Type", "Slab"), ("Material", Concrete), ("Slab Thickness", "0.23"), ("One Way Load Distribution?", "No"),
                ("f11 Modifier", "0.8"), ("f22 Modifier", "0.8"), ("f12 Modifier", "1"), ("m11 Modifier", "0.5"), ("m22 Modifier", "0.5"), ("m12 Modifier", "0.5"),
                ("v13 Modifier", "1"), ("v23 Modifier", "1"), ("Mass Modifier", "1"), ("Weight Modifier", "1"))]),
        ("Design > Concrete Slab Design > View/Revise Preferences", "Concrete Slab Design Preferences - ACI 318-19", "", [R(("CoverTop", "0.035"), ("CoverBot", "0.04"))]),
        ("Analyze > Cracking Analysis Options", "Analysis Options - Cracking Analysis Options", "", [R(("Reinforcement Source", "User and Designed"), ("Minimum Tension Ratio", "0.0018"), ("Minimum Compression Ratio", "0"))]),
    ];

    private static Dictionary<string, string> R(params (string Key, string Value)[] fields) => fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    /// <summary>The concrete the forms use: the Modeler's first concrete material.</summary>
    private static string ConcreteOf(FieldContext ctx) =>
        (ctx.Link as OfflineEtabs)?.Model.Materials.FirstOrDefault(m => m.IsConcrete)?.Name ?? "4000Psi";

    private static Dictionary<string, string> Fill(Dictionary<string, string> row, string concrete) =>
        row.ToDictionary(kv => kv.Key, kv => kv.Value == Concrete ? concrete : kv.Value, StringComparer.Ordinal);

    /// <summary>The rows each form set, with the Modeler's concrete in place (Define keeps it for Check).</summary>
    private static string concreteUsed = "";

    /// <summary>In the Modeler's part, before the .f2k is written: each SAFE form's values set as the form sets them.</summary>
    public static void Define(FieldContext ctx)
    {
        concreteUsed = ConcreteOf(ctx);
        foreach (var (form, table, name, rows) in Forms)
        {
            var full = SafeTables.Prefix + table;
            var json = new JsonObject { ["table"] = full, ["name"] = name, ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject(Fill(r, concreteUsed).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))).ToArray()) };
            ctx.Tool("define_table_rows", json.ToJsonString(), a => FieldContext.Expect(Int(a["rows"]) == rows.Length,
                $"{table}: {(name.Length > 0 ? name : "the options row")} holds {a["rows"]} rows, the form set {rows.Length}"), $"SAFE form {form}: SAFE's {table} set as the form sets it");
        }
    }

    /// <summary>In the scratch SAFE that took the .f2k: every table read back from SAFE, field by field.</summary>
    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        foreach (var group in Forms.GroupBy(f => f.Form))
            ctx.Step($"SAFE form {group.Key}: SAFE holds every value the Engine's form set", () =>
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no SAFE table to read back");
                var wrong = new List<string>();
                foreach (var (_, table, name, rows) in group)
                {
                    TableData data;
                    try { data = link.ReadTable(table, null, null); }
                    catch (EtabsException ex) { wrong.Add($"{table}: SAFE did not read it back ({ex.Message})"); continue; }
                    var fields = data.Fields.ToArray();
                    int Col(string label)
                    {
                        var key = SafeTables.All.TryGetValue(table, out var all) ? all.FirstOrDefault(f => f.Label == label).Key : null;
                        static string N(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                        var at = Array.FindIndex(fields, f => f.Equals(label, StringComparison.OrdinalIgnoreCase) || key is not null && f.Equals(key, StringComparison.OrdinalIgnoreCase));
                        return at >= 0 ? at : Array.FindIndex(fields, f => N(f) == N(label));
                    }
                    var nameAt = name.Length == 0 ? -1 : Col("Name");
                    if (name.Length > 0 && nameAt < 0) { wrong.Add($"{table}: SAFE's table has no Name field ({string.Join(", ", fields)})"); continue; }
                    var mine = data.Rows.Where(r => name.Length == 0 || nameAt < r.Length && r[nameAt] == name).ToList();
                    if (name.Length == 0 ? mine.Count == 0 : mine.Count != rows.Length) { wrong.Add($"{table}: SAFE has {mine.Count} rows for {(name.Length > 0 ? name : "its options")}, the form set {rows.Length}"); continue; }
                    for (var i = 0; i < rows.Length; i++)
                        foreach (var (field, value) in Fill(rows[i], concreteUsed))
                        {
                            var at = Col(field);
                            var got = at >= 0 && at < mine[i].Length ? mine[i][at] : null;
                            if (!Same(value, got)) wrong.Add($"{table} {(name.Length > 0 ? name : "")} row {i + 1} {field}: form {value}, SAFE {got ?? "(no field: " + string.Join(", ", fields) + ")"}");
                        }
                }
                FieldContext.Expect(wrong.Count == 0, string.Join("; ", wrong.Take(12)) + (wrong.Count > 12 ? $"; and {wrong.Count - 12} more" : ""));
                return $"{group.Count()} tables, every field the same";
            });
    }

    private static bool Same(string want, string? got)
    {
        if (got is null) return false;
        if (double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) <= 1e-4 * Math.Max(1e-9, Math.Max(Math.Abs(a), Math.Abs(b))) || Math.Abs(a - b) < 1e-12;
        return string.Equals(want.Trim(), got.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
