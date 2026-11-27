using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// The ETABS Define > Functions forms in the field test (Ahmed 2026-11-27: every form identical to ETABS, every value
/// read back): the Modeler defines one function of several codes and types, each with its ETABS form's own default
/// parameters (from the pictures of ETABS 23.2's forms), ETABS opens the .e2k and takes the table rows
/// (ModelerTables), and the scratch part reads each table back from ETABS, field by field.
/// </summary>
public static class EtabsFunctionRoundTrip
{
    internal static readonly (string Name, string Kind, string Table, Dictionary<string, string> Values, double[][] Points)[] Functions =
    [
        ("CPM-ERS-ASCE716", "spectrum", "Functions - Response Spectrum - ASCE7-16", new() { ["Ss"] = "2.29", ["S1"] = "0.869", ["TL"] = "8", ["SiteClass"] = "B", ["Fa"] = "0.9", ["Fv"] = "0.8", ["SDS"] = "1.374", ["SD1"] = "0.4635" }, [[0, 0.5496], [0.0675, 1.374], [0.3373, 1.374], [1, 0.4635], [8, 0.0579]]),
        ("CPM-ERS-UBC97", "spectrum", "Functions - Response Spectrum - UBC 97", new() { ["Ca"] = "0.4", ["Cv"] = "0.4" }, [[0, 0.4], [0.08, 1], [0.4, 1], [0.6, 0.6667], [0.8, 0.5], [1, 0.4], [1.2, 0.3333]]),
        ("CPM-ETH-SINE", "history", "Functions - Time History - Sine", new() { ["Period"] = "1", ["StepsPerCyc"] = "4", ["NumCycles"] = "2", ["Amplitude"] = "1" }, [[0, 0], [0.25, 1], [0.5, 0], [0.75, -1], [1, 0], [1.25, 1], [1.5, 0], [1.75, -1], [2, 0]]),
        ("CPM-ETH-RAMP", "history", "Functions - Time History - Ramp", new() { ["RampTime"] = "1", ["Amplitude"] = "1", ["MaxTime"] = "20" }, [[0, 0], [1, 1], [20, 1]]),
        ("CPM-ETH-TRI", "history", "Functions - Time History - Triangular", new() { ["Period"] = "1", ["NumCycles"] = "2", ["Amplitude"] = "1" }, [[0, 0], [0.25, 1], [0.75, -1], [1, 0], [1.25, 1], [1.75, -1], [2, 0]]),
        ("CPM-ETH-PER", "history", "Functions - Time History - User Periodic", new() { ["NumCycles"] = "3" }, [[0, 0], [0.5, 1], [1, 0]]),
    ];

    /// <summary>In the Modeler's part: the functions, written into the model the programs open.</summary>
    public static void Define(FieldContext ctx)
    {
        foreach (var (name, kind, table, values, points) in Functions)
            ctx.Tool("define_function", new JsonObject
            {
                ["name"] = name, ["kind"] = kind, ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(p[0], p[1])).ToArray()), ["etabs_table"] = table,
                ["etabs_values"] = new JsonObject(values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            }.ToJsonString(), null, $"tool define_function, ETABS's {table.Split(" - ").Last()} {(kind == "spectrum" ? "response spectrum" : "time history")} form");
    }

    /// <summary>In ETABS's scratch part: each function in ETABS's own table of its code or type, every parameter as the Engine wrote it.</summary>
    public static void Check(FieldContext ctx, ICsiLink link)
    {
        foreach (var (name, kind, table, values, points) in Functions)
            ctx.Step($"ETABS read the Engine's {table.Replace("Functions - ", "")} function, every field", () =>
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no table to read back");
                TableData t;
                try { t = link.ReadTable(table, null, null); }
                catch (EtabsException ex) { throw new FieldCheckException($"ETABS did not give its table \"{table}\" ({ex.Message})"); }
                var col = t.Fields.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.OrdinalIgnoreCase);
                FieldContext.Expect(col.ContainsKey("Name"), $"ETABS's \"{table}\" has no Name field: {string.Join(", ", t.Fields)}");
                // ETABS names the function on its first row; the rows after it, up to the next name, are its points.
                var at = t.Rows.ToList().FindIndex(r => r[col["Name"]] == name);
                FieldContext.Expect(at >= 0, $"ETABS has no {name} in \"{table}\" (it has {string.Join(", ", t.Rows.Select(r => r[col["Name"]]).Where(n => n.Length > 0).Distinct())})");
                var rows = t.Rows.Skip(at).TakeWhile((r, i) => i == 0 || r[col["Name"]].Length == 0 || r[col["Name"]] == name).ToList();
                static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase)
                    || double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) <= 1e-3 * Math.Max(1, Math.Abs(x));
                var off = values.Where(kv => !col.TryGetValue(kv.Key, out var c) || !Same(rows[0][c] ?? "", kv.Value)).Select(kv => $"{kv.Key}: Engine {kv.Value}, ETABS {(col.TryGetValue(kv.Key, out var c2) ? rows[0][c2] : "(no field)")}").ToList();
                FieldContext.Expect(off.Count == 0, $"{name}: " + string.Join("; ", off));
                if (table.EndsWith("User Periodic", StringComparison.OrdinalIgnoreCase))
                    FieldContext.Expect(rows.Count == points.Length, $"{name}: the Engine wrote {points.Length} points of one cycle, ETABS has {rows.Count}");
                return $"{rows.Count} rows; " + string.Join(", ", values.Select(kv => kv.Key + "=" + rows[0][col[kv.Key]]));
            });
    }
}
