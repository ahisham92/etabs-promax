using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// Assign > Frame, Shell and Joint items the .e2k has no keyword for, kept as rows of ETABS's own assignment tables
/// (AssignTables, Ahmed 2026-11-26: every button): set by the assign tools, shown by property_definitions, kept in the
/// .cpm and by Replicate, written to SAP2000's tables where it has them, and refused when the table or field is not ETABS's.
/// </summary>
static class AssignTablesTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Asg", [6, 5], [4], 2, 3.2));

    static JsonObject Run(OfflineEtabs link, string tool, object args) =>
        (JsonObject)PropertyTools.All.Concat(EditTools.All).Single(x => x.Name == tool).Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static void Refused(OfflineEtabs link, string tool, object args, string says)
    {
        var before = ModelFile.ToJson(link.Model);
        try { Run(link, tool, args); }
        catch (ToolInputException ex)
        {
            Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), $"{tool}: '{says}' expected in: {ex.Message}");
            Check(ModelFile.ToJson(link.Model) == before, $"{tool}: a refusal changed the model");
            return;
        }
        throw new Exception($"{tool} took {JsonSerializer.Serialize(args)}");
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("assign tables: added mass, output stations, T/C limits, edge releases and an opening reach the model, the .cpm, Replicate and SAP2000's tables", () =>
        {
            var link = Frame();
            var m = link.Model;
            var beam = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam && a.Story == "Story1");
            var slab = m.AreaAssigns.First(a => a.Story == "Story1");
            var point = m.PointAssigns.FirstOrDefault(p => p.Story == "Story1")?.Point ?? m.Points[0].Name;
            var b = beam.Label + "@Story1";
            Run(link, "assign_frame_data", new { objects = new[] { b }, tables = new Dictionary<string, object>
            {
                ["Frame Assignments - Additional Mass"] = new[] { new { AddedMass = "0.25" } },
                ["Frame Assignments - Output Stations"] = new[] { new { StationOpt = "Min Stations", MinStations = "9" } },
                ["Frame Assignments - Tension and Compression Limits"] = new[] { new Dictionary<string, object> { ["IncludeTens"] = true, ["TensLimit"] = "0" } },
            } });
            Check(AssignTables.Row(beam, "Frame Assignments - Output Stations")["MinStations"] == "9", "the output stations are kept");
            Check(AssignTables.Row(beam, "Frame Assignments - Tension and Compression Limits")["IncludeTens"] == "Yes", "true is kept as Yes");
            Run(link, "assign_shell_data", new { objects = new[] { slab.Label + "@Story1" }, tables = new Dictionary<string, object>
            {
                ["Area Assignments - Additional Mass"] = new[] { new { AddedMass = "0.1" } },
                ["Area Assignments - Edge Releases"] = new[] { new { EdgeNumber = "1", RBending = "Yes" }, new { EdgeNumber = "3", RBending = "Yes" } },
            } });
            Run(link, "assign_joint_data", new { objects = new[] { point + "@Story1" }, tables = new Dictionary<string, object> { ["Joint Assignments - Additional Mass"] = new[] { new { MassXY = "2", MassZ = "0" } } } });
            var pd = (JsonObject)PropertyTools.All.Single(x => x.Name == "property_definitions").Invoke(JsonDocument.Parse(JsonSerializer.Serialize(new { objects = new[] { b, slab.Label + "@Story1", point + "@Story1" } })).RootElement, link);
            var objs = pd["objects"]!.AsArray();
            Check(objs[0]!["tables"]!["Frame Assignments - Additional Mass"]![0]!["AddedMass"]!.GetValue<string>() == "0.25", "property_definitions shows the frame's rows");
            Check(objs[1]!["tables"]!["Area Assignments - Edge Releases"]!.AsArray().Count == 2, "two edge release rows");
            Check(objs[2]!["tables"]!["Joint Assignments - Additional Mass"] is not null, "the joint's mass");
            Check(pd["assign_tables"]!["Frame Assignments - Output Stations"]!["fields"]!.AsArray().Count == 3, "the catalogue lists the fields");
            var cpm = ModelFile.FromJson(ModelFile.ToJson(m));
            Check(AssignTables.Row(cpm.AreaAssigns.Single(a => a.Label == slab.Label && a.Story == "Story1"), "Area Assignments - Additional Mass")["AddedMass"] == "0.1", "kept in the .cpm");
            var rows = AssignTables.Rows(m);
            Check(rows["Frame Assignments - Additional Mass"].Single()["Label"] == beam.Label && rows["Frame Assignments - Additional Mass"].Single()["Story"] == "Story1", "rows carry Story and Label for ETABS");
            var copy = ((JsonObject)Run(link, "replicate_objects", new { objects = new[] { b }, mode = "linear", dy = 20.0 }))["made"]![0]!.GetValue<string>().Split('@')[0];
            Check(AssignTables.Row(m.LineAssigns.Single(a => a.Label == copy && a.Story == "Story1"), "Frame Assignments - Additional Mass")["AddedMass"] == "0.25", "a copy keeps the rows");
            var s2k = S2kWriter.Write(m, "a.s2k");
            foreach (var t in new[] { "FRAME ADDED MASS ASSIGNMENTS", "FRAME TENSION AND COMPRESSION LIMITS", "AREA ADDED MASS ASSIGNMENTS", "JOINT ADDED MASS ASSIGNMENTS" })
                Check(s2k.Text.Contains("TABLE:  \"" + t + "\"", StringComparison.Ordinal), $"the .s2k has {t}");
            Check(!s2k.Notes.Any(n => n.Contains("Assignments", StringComparison.Ordinal) || n.Contains("assign it in SAP2000", StringComparison.Ordinal) && n.Contains("Output", StringComparison.Ordinal)), "no note asks for the assignments by hand: " + string.Join(" | ", s2k.Notes));
            // An opening, by the .e2k's own keyword.
            Run(link, "assign_shell_data", new { objects = new[] { slab.Label + "@Story1" }, opening = true });
            Check(System.Text.RegularExpressions.Regex.IsMatch(E2kWriter.Write(m), "AREAASSIGN\\s+\"" + slab.Label + "\"\\s+\"Story1\".*OPENING\\s+\"Yes\""), "the .e2k says OPENING \"Yes\"");
            // Taking a table off.
            Run(link, "assign_frame_data", new { objects = new[] { b }, tables = new Dictionary<string, object> { ["Frame Assignments - Output Stations"] = Array.Empty<object>() } });
            Check(!beam.Tables!.ContainsKey("Frame Assignments - Output Stations"), "[] takes the table off");
        });

        yield return ("assign tables: a table that is not ETABS's, a field it lacks, the wrong kind or two rows where one goes are refused", () =>
        {
            var link = Frame();
            var m = link.Model;
            var b = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam).Label + "@Story1";
            var s = m.AreaAssigns.First(a => a.Story == "Story1").Label + "@Story1";
            Refused(link, "assign_frame_data", new { objects = new[] { b }, tables = new Dictionary<string, object> { ["Frame Assignments - Nonsense"] = new[] { new { A = "1" } } } }, "no frame assignment table");
            Refused(link, "assign_frame_data", new { objects = new[] { b }, tables = new Dictionary<string, object> { ["Frame Assignments - Additional Mass"] = new[] { new { Mass = "1" } } } }, "no field 'Mass'");
            Refused(link, "assign_frame_data", new { objects = new[] { b }, tables = new Dictionary<string, object> { ["Area Assignments - Additional Mass"] = new[] { new { AddedMass = "1" } } } }, "no frame assignment table");
            Refused(link, "assign_shell_data", new { objects = new[] { s }, tables = new Dictionary<string, object> { ["Area Assignments - Additional Mass"] = new[] { new { AddedMass = "1" }, new { AddedMass = "2" } } } }, "one row");
        });
    }
}
