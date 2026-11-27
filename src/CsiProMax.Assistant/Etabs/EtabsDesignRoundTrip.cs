using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The Engine's ETABS 23.2 Design > Live Load Reduction Factors, Steel Connection Design > View/Revise Preferences, Set
/// Lateral Displacement Targets and Set Time Period Targets (pictures field/reference-forms/etabs/Design/70-99b), proved:
/// in the Modeler's part each is set as its form sets it and read back; the .e2k carries the RLLF line; in the scratch
/// ETABS the targets are read back from ETABS's own "Design Optimization" tables.
/// </summary>
public static class EtabsDesignRoundTrip
{
    /// <summary>In the Modeler's part, before the .e2k is written.</summary>
    public static void Define(FieldContext ctx, OfflineEtabs modeler)
    {
        var m = modeler.Model;
        if (m.Target is "sap2000" or "safe") return;
        ctx.Tool("set_live_load_reduction", """{"method":"Code","code":"ASCE7-10","default_min":true}""",
            _ => FieldContext.Expect(E2kWriter.Write(m).Contains("RLLF  METHOD \"ASCE7-10\"  USEDEFAULTMIN \"YES\"", StringComparison.Ordinal), "the .e2k has no RLLF line as ETABS writes it"),
            "ETABS form Live Load Reduction Factor: Tributary Area (ASCE7-10), as the .e2k's RLLF line"); // ETABS itself
        ctx.Tool("set_live_load_reduction", """{"method":"Stories","default_min":false,"single":0.6,"multiple":0.45,"all_forces":true,"stories":[[1,1],[3,0.8]]}""",
            null, "ETABS form Live Load Reduction By Stories Supported: two rows, kept with the model"); // ETABS itself
        ctx.Tool("set_live_load_reduction", """{"method":"Code","code":"ASCE7-10","default_min":true}""", null, "ETABS form Live Load Reduction Factor: back to the program's default"); // ETABS itself
        ctx.Tool("set_connection_preferences", """{"code":"AISC 360-22","items":[{"group":"Beam-Beam|Partial Depth","item":"Bolt Spacing, S","value":"3.5"},{"group":"Column-Footing","item":"Plate Thickness, T","value":"1.25"}],"schedule":{"mode":"Full Depth","horizontal":12,"partial":32}}""",
            _ =>
            {
                var v = Tools.EtabsDesignTools.ConnectionValues(m);
                FieldContext.Expect(v.GetValueOrDefault("Beam-Beam|Partial Depth|Bolt Spacing, S") == "3.5" && v.GetValueOrDefault("Schedule|Default Mode") == "Full Depth" && v.GetValueOrDefault("|Design Code") == "AISC 360-22",
                    "the preferences did not keep what the form set: " + string.Join("; ", v.Select(kv => kv.Key + "=" + kv.Value)));
            }, "ETABS form Steel Connection Design Preferences, Plate Depth Schedule: kept with the model"); // ETABS itself
        ctx.Tool("design_settings", "{}", a => FieldContext.Expect(a["connection"] is JsonArray { Count: >= 4 } && a["rllf_line"] is JsonArray, "the settings do not read back: " + a.ToJsonString()),
            "ETABS Design menu settings read back"); // ETABS itself
        var stat = m.LoadCases.FirstOrDefault(c => c.Lines.Any(l => l.Count >= 2 && l[0] == "TYPE" && E2kText.Unquote(l[1]).Equals("Linear Static", StringComparison.OrdinalIgnoreCase)))?.Name;
        var modal = m.LoadCases.FirstOrDefault(c => c.Lines.Any(l => l.Count >= 2 && l[0] == "TYPE" && E2kText.Unquote(l[1]).StartsWith("Modal", StringComparison.OrdinalIgnoreCase)))?.Name;
        var top = m.Stories.Count > 0 ? m.Stories[^1].Name : null;
        var joint = top is null ? null : m.LineAssigns.Where(x => x.Story == top).Select(x => m.Lines.Find(l => l.Label == x.Label)).Where(l => l is not null).Select(l => l!.PointI).FirstOrDefault();
        if (stat is not null && top is not null && joint is not null)
            ctx.Tool("define_table_rows", new JsonObject { ["table"] = DefineTables.DisplacementTargets, ["name"] = stat, ["rows"] = new JsonArray(new JsonObject { ["Story"] = top, ["Label"] = joint, ["MaxDispl"] = "0.05", ["Active"] = "Yes" }) }.ToJsonString(),
                null, "ETABS form Lateral Displacement Targets: a target"); // ETABS itself
        if (modal is not null)
            ctx.Tool("define_table_rows", new JsonObject { ["table"] = DefineTables.PeriodTargets, ["name"] = modal, ["rows"] = new JsonArray(new JsonObject { ["ModeNum"] = "1", ["TargetPeriod"] = "0.75", ["Active"] = "Yes" }) }.ToJsonString(),
                null, "ETABS form Time Period Targets: a target"); // ETABS itself
    }

    /// <summary>In the scratch ETABS that opened the .e2k: the targets read back from ETABS's tables.</summary>
    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        foreach (var (form, table, field, value) in new[] { ("Lateral Displacement Targets", DefineTables.DisplacementTargets, "MaxDispl", "0.05"), ("Time Period Targets", DefineTables.PeriodTargets, "TargetPeriod", "0.75") })
            ctx.Step($"ETABS form {form}: ETABS holds the target the Engine's form set", () => // ETABS itself
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no ETABS table to read back"); // ETABS itself
                var t = link.Tables().FirstOrDefault(x => x.Name.Equals(table, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FieldCheckException($"ETABS lists no table '{table}' now: the target did not reach ETABS"); // ETABS itself
                var data = link.ReadTable(t.Key, null, null);
                var f = data.Fields.ToList();
                var i = f.FindIndex(x => x.Replace(" ", "").Equals(field, StringComparison.OrdinalIgnoreCase) || (field == "MaxDispl" && x.StartsWith("Maximum Displacement", StringComparison.OrdinalIgnoreCase)) || (field == "TargetPeriod" && x.StartsWith("Target", StringComparison.OrdinalIgnoreCase)));
                var row = data.Rows.FirstOrDefault(r => i >= 0 && i < r.Length && double.TryParse(r[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d - double.Parse(value, CultureInfo.InvariantCulture)) < 1e-6);
                FieldContext.Expect(row is not null, $"{table} has no row with {field} {value}: fields {string.Join(", ", f)}; {data.Rows.Count} rows");
                return $"{table}: the target is there";
            });
    }
}
