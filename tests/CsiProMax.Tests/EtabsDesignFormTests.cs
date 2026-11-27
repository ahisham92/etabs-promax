using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>
/// ETABS 23.2's Design > Live Load Reduction Factors, Steel Connection Design preferences and the design targets in the
/// Engine (pictures field/reference-forms/etabs/Design/70-99b): kept with the model, the RLLF line as ETABS writes it.
/// </summary>
static class EtabsDesignFormTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("EtabsDesign", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args)
    {
        var t = EtabsDesignTools.All.Concat(DefineTools.All).Single(x => x.Name == tool);
        return (JsonObject)t.Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);
    }

    static bool Refused(ICsiLink link, string tool, object args) { try { Run(link, tool, args); return false; } catch (ToolInputException) { return true; } }

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("ETABS Design: live load reduction by code writes ETABS's RLLF line; the other methods are kept with the model", () =>
        {
            var link = Frame(); var m = link.Model;
            Run(link, "set_live_load_reduction", new { method = "Code", code = "ASCE7-05", default_min = false, single = 0.6, multiple = 0.45 });
            var e2k = E2kWriter.Write(m);
            Check(e2k.Contains("  RLLF  METHOD \"ASCE7-05\"  USEDEFAULTMIN \"NO\"", StringComparison.Ordinal), "the RLLF line: " + string.Join(" | ", m.Controls));
            Check(m.Controls.Count(l => l.TrimStart().StartsWith("RLLF", StringComparison.Ordinal)) == 1, "one RLLF line");
            Check(E2kWriter.Write(E2kReader.Read(e2k)).Contains("RLLF  METHOD \"ASCE7-05\"", StringComparison.Ordinal), "the line reads back");
            Run(link, "set_live_load_reduction", new { method = "Curves", curves = new[] { new { dl_ll = 1.0, points = new[] { new[] { 9.29, 1.0 }, new[] { 37.16, 0.6 } } } } });
            var back = Run(link, "design_settings", new { });
            var rows = (JsonArray)back["live_load_reduction"]!;
            Check(rows.Count == 3 && rows[0]!["Method"]!.GetValue<string>() == "Curves", "the curve is kept: " + rows.ToJsonString());
            Check(m.Controls.Any(l => l.Contains("ASCE7-05", StringComparison.Ordinal)), "a method with no ETABS words yet leaves the line as it was");
            // The .cpm keeps it.
            var again = ModelFile.FromJson(ModelFile.ToJson(m));
            Check(again.Kept?.GetValueOrDefault(EtabsDesignTools.LlrfKept)?.Count == 3, "the .cpm keeps the live load reduction");
            Check(Refused(link, "set_live_load_reduction", new { method = "Code", code = "ASCE7-22" }), "a code the list does not have is refused");
            Check(Refused(link, "set_live_load_reduction", new { method = "Stories" }), "By Stories Supported with no rows is refused");
            Check(Refused(link, "set_live_load_reduction", new { method = "User", single = 1.5 }), "a minimum factor above 1 is refused");
        });

        yield return ("ETABS Design: steel connection preferences, plate schedule and design combinations are kept; blank puts the default back", () =>
        {
            var link = Frame(); var m = link.Model;
            Run(link, "set_connection_preferences", new { code = "AISC 360-16", items = new[] { new { group = "Beam-Beam|Partial Depth", item = "Bolt Spacing, S", value = "3.5" }, new { group = "Column-Footing", item = "Plate Thickness, T", value = "1.25" } }, schedule = new { mode = "Full Depth", horizontal = 12, partial = 32 } });
            var v = EtabsDesignTools.ConnectionValues(m);
            Check(v["|Design Code"] == "AISC 360-16" && v["Beam-Beam|Partial Depth|Bolt Spacing, S"] == "3.5" && v["Schedule|Partial"] == "32", "kept: " + string.Join("; ", v));
            Run(link, "set_connection_preferences", new { items = new[] { new { group = "Beam-Beam|Partial Depth", item = "Bolt Spacing, S", value = "" } } });
            Check(!EtabsDesignTools.ConnectionValues(m).ContainsKey("Beam-Beam|Partial Depth|Bolt Spacing, S"), "blank puts the default back");
            Check(Refused(link, "set_connection_preferences", new { code = "AISC 360-05" }), "a code Steel Connection Design does not have is refused");
            Check(Refused(link, "set_connection_preferences", new { schedule = new { horizontal = 30, partial = 10 } }), "a partial plate depth below the horizontal one is refused");
            Check(Refused(link, "set_connection_preferences", new { items = new[] { new { group = "Beams", item = "x", value = "1" } } }), "an unknown tab is refused");
        });

        yield return ("ETABS Design: lateral displacement and time period targets are rows of ETABS's Design Optimization tables, by case", () =>
        {
            var link = Frame(); var m = link.Model;
            Run(link, "define_table_rows", new { table = DefineTables.DisplacementTargets, name = "Dead", rows = new[] { new { Story = "Story3", Label = "1", MaxDispl = "0.04", Active = "Yes" }, new { Story = "Story2", Label = "1", MaxDispl = "0.03", Active = "Yes" } } });
            Run(link, "define_table_rows", new { table = DefineTables.PeriodTargets, name = "Modal", rows = new[] { new { ModeNum = "1", TargetPeriod = "0.8", Active = "No" } } });
            var back = Run(link, "design_settings", new { });
            Check(((JsonArray)back["displacement_targets"]!).Count == 2 && ((JsonArray)back["period_targets"]!).Count == 1, "the targets: " + back.ToJsonString());
            Check(((JsonArray)back["displacement_targets"]!)[0]!["StaticCase"]!.GetValue<string>() == "Dead", "each target names its case");
            Run(link, "define_table_rows", new { table = DefineTables.DisplacementTargets, name = "Dead", rows = Array.Empty<object>() });
            Check(m.DefinitionTables?.ContainsKey(DefineTables.DisplacementTargets) != true, "deleted");
            Check(ModelerTables.KeyOf(DefineTables.PeriodTargets) == "ModalCase|ModeNum", "ETABS's table is put in by case and mode");
        });
    }
}
