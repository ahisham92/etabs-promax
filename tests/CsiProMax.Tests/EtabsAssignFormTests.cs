using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>
/// ETABS 23.2's own Assign forms in the Engine (Ahmed 2026-11-27: every form identical to ETABS, every option working):
/// the model side behind the forms of Assign > Joint, Joint/Frame/Shell Loads, Copy and Paste Assigns.
/// </summary>
static class EtabsAssignFormTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("EtabsForms", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args)
    {
        var t = CaseTools.All.Concat(PropertyTools.All).Concat(StructureTools.All).Concat(HandTools.All).Single(x => x.Name == tool);
        return (JsonObject)t.Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);
    }

    static StructuralModel ViaE2k(StructuralModel m) => E2kReader.Read(E2kWriter.Write(m));
    static string Joint(StructuralModel m) { var c = m.LineAssigns.First(x => m.Line(x.Label)!.Kind == LineKind.Column); return m.Line(c.Label)!.PointJ + "@" + c.Story; }
    static List<string> Beams(StructuralModel m) => m.LineAssigns.Where(x => m.Line(x.Label)!.Kind == LineKind.Beam).Take(2).Select(a => a.Label + "@" + a.Story).ToList();
    static string Floor(StructuralModel m) { var a = m.AreaAssigns.First(x => m.Area(x.Label)!.Kind == AreaKind.Floor); return a.Label + "@" + a.Story; }

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("ETABS forms: Joint Load Assignment - Force adds, replaces and deletes, and keeps the punching shear size", () =>
        {
            var link = Frame(); var m = link.Model; var j = Joint(m);
            Run(link, "assign_object_loads", new { kind = "joint_force", pattern = "Live", objects = new[] { j }, values = new { fz = -10 } });
            Run(link, "assign_object_loads", new { kind = "joint_force", pattern = "Live", objects = new[] { j }, values = new { fz = -5, fx = 2 }, how = "add" });
            var l = m.PointLoads.Where(x => x.Pattern == "Live").ToList();
            Check(l.Count == 1 && E2kText.Value(l[0].Tokens, "FZ") == "-15" && E2kText.Value(l[0].Tokens, "FX") == "2", "Add to Existing Loads adds the values: " + string.Join(" ", l.SelectMany(x => x.Tokens)));
            Run(link, "assign_object_loads", new { kind = "joint_force", pattern = "Live", objects = new[] { j }, values = new { fz = -3 }, how = "replace" });
            Check(E2kText.Value(m.PointLoads.Single(x => x.Pattern == "Live").Tokens, "FZ") == "-3", "Replace Existing Loads");
            Run(link, "assign_object_loads", new { kind = "joint_force", pattern = "Live", objects = new[] { j }, how = "delete" });
            Check(!m.PointLoads.Any(x => x.Pattern == "Live"), "Delete Existing Loads takes it off");
            Run(link, "assign_joint_data", new { objects = new[] { j }, tables = new Dictionary<string, object> { [AssignTables.JointForceSize] = new[] { new { LoadPattern = "Live", XDim = "12", YDim = "18" }, new { LoadPattern = "Dead", XDim = "6", YDim = "6" } } } });
            var back = ViaE2k(m);
            Check(m.PointAssigns.Single(p => p.Point + "@" + p.Story == j).Tables![AssignTables.JointForceSize].Count == 2, "one size row per load pattern");
            var rows = AssignTables.Rows(m)[AssignTables.JointForceSize];
            Check(rows.Count == 2 && rows.All(r => r.ContainsKey("Story") && r.ContainsKey("Label") && r.ContainsKey("LoadPattern")), "the rows carry Story, Label and the load pattern, for ETABS's table");
        });

        yield return ("ETABS forms: Joint Assignment - Diaphragms takes Disconnect and From Shell Object", () =>
        {
            var link = Frame(); var m = link.Model; var j = Joint(m);
            Run(link, "assign_diaphragm_data", new { objects = new[] { j }, joints = true, diaphragm = "Disconnect" });
            Check(m.PointAssigns.Single(p => p.Point + "@" + p.Story == j).Diaphragm == "DISCONNECT", "Disconnect is kept");
            Check(ViaE2k(m).PointAssigns.Single(p => p.Point + "@" + p.Story == j).Diaphragm == "DISCONNECT", "and read back from the .e2k");
            Run(link, "assign_diaphragm_data", new { objects = new[] { j }, joints = true, diaphragm = "" });
            Check(m.PointAssigns.Single(p => p.Point + "@" + p.Story == j).Diaphragm is null, "From Shell Object takes it off");
        });

        yield return ("ETABS forms: Frame Load Assignment - Distributed in moments, and Paste Assigns copies the loads as they are", () =>
        {
            var link = Frame(); var m = link.Model; var b = Beams(m);
            Run(link, "assign_object_loads", new { kind = "frame_uniform", pattern = "Live", objects = new[] { b[0] }, values = new { value = 2, moments = 1 }, how = "add" });
            Run(link, "assign_object_loads", new { kind = "frame_trapezoid", pattern = "Live", objects = new[] { b[0] }, values = new { start = 0, end = 4, from = 0.25, to = 0.75 }, how = "add" });
            Check(m.LineLoads.Any(x => E2kText.Value(x.Tokens, "TYPE") == "UNIFM"), "moments are UNIFM");
            Run(link, "paste_object_loads", new { from = b[0], objects = new[] { b[1] }, types = new[] { "UNIFF", "UNIFM", "TRAPF", "TRAPM" } });
            var mine = m.LineLoads.Where(x => x.Object + "@" + x.Story == b[1]).Select(x => string.Join(" ", x.Tokens)).OrderBy(x => x).ToList();
            var theirs = m.LineLoads.Where(x => x.Object + "@" + x.Story == b[0]).Select(x => string.Join(" ", x.Tokens)).OrderBy(x => x).ToList();
            Check(mine.SequenceEqual(theirs) && mine.Count == 2, "the second beam has the first one's loads word for word");
            Run(link, "assign_object_loads", new { kind = "frame_uniform", pattern = "Live", objects = new[] { b[1] }, how = "delete", values = new { moments = 1 } });
            Check(!m.LineLoads.Any(x => x.Object + "@" + x.Story == b[1] && E2kText.Value(x.Tokens, "TYPE") == "UNIFM"), "delete takes the moments off");
        });

        yield return ("ETABS forms: Shell Assignment - Uniform Load Set from the file or the Engine's own table, and None", () =>
        {
            var link = Frame(); var m = link.Model; var f = Floor(m);
            m.Passthrough.Add(new RawSection { Heading = LoadSets.Heading, Lines = ["  SHELLUNIFORMLOADSET \"Offices\"  LOADPAT \"Live\"  VALUE 3.5"] });
            (m.DefinitionTables ??= new())["Shell Uniform Load Sets"] = [new() { ["Name"] = "Roof", ["LoadPattern"] = "Live", ["LoadValue"] = "1" }];
            Run(link, "assign_shell_load_set", new { objects = new[] { f }, set = "Offices" });
            Check(m.AreaLoads.Count(x => LoadSets.SetOf(x) == "Offices") == 1, "a set of the file is the AREALOAD line");
            Run(link, "assign_shell_load_set", new { objects = new[] { f }, set = "Roof" });
            var a = m.AreaAssigns.Single(x => x.Label + "@" + x.Story == f);
            Check(!m.AreaLoads.Any(x => LoadSets.SetOf(x) is not null) && a.Tables![AssignTables.AreaLoadSet][0]["LoadSet"] == "Roof", "a set of the Engine's table is the table row, in place of the first");
            Run(link, "assign_shell_load_set", new { objects = new[] { f }, set = "" });
            Check(a.Tables is null || !a.Tables.ContainsKey(AssignTables.AreaLoadSet), "None takes it off");
            try { Run(link, "assign_shell_load_set", new { objects = new[] { f }, set = "Nope" }); throw new Exception("a set that is not there was taken"); }
            catch (ToolInputException) { }
        });

        yield return ("ETABS forms: temperature gradients, non-uniform loads and wind Cp are rows of ETABS's load tables, a row per pattern", () =>
        {
            var link = Frame(); var m = link.Model; var b = Beams(m)[0]; var f = Floor(m);
            Run(link, "assign_frame_data", new { objects = new[] { b }, tables = new Dictionary<string, object> { [AssignTables.FrameTemperature] = new[] { new { LoadPattern = "Dead", TempType = "Temperature Gradient 2-2", Temp = "0", TempGrad2 = "5", TempGrad3 = "0", JointTemp = "No" }, new { LoadPattern = "Live", TempType = "Temperature", Temp = "20", TempGrad2 = "0", TempGrad3 = "0", JointTemp = "Yes" } } } });
            Run(link, "assign_shell_data", new { objects = new[] { f }, tables = new Dictionary<string, object> { ["Area Load Assignments - Non-uniform"] = new[] { new { LoadPattern = "Live", Dir = "Gravity", A = "0", B = "0", C = "1", D = "2", Restriction = "Use All Values" } }, ["Area Load Assignments - Wind Pressure Coefficients"] = new[] { new { LoadPattern = "Dead", Dir = "Windward", Cp = "0.8" } } } });
            var rows = AssignTables.Rows(m);
            Check(rows[AssignTables.FrameTemperature].Count == 2, "two temperature rows");
            Check(rows["Area Load Assignments - Non-uniform"].Single()["D"] == "2", "the non-uniform row");
            Check(ProgramOptions.HasAssignTable(ProgramOptions.Etabs, AssignTables.FrameTemperature) && !ProgramOptions.HasAssignTable(ProgramOptions.Sap2000, AssignTables.FrameTemperature), "ETABS's own, not SAP2000's");
            var json = ModelFile.FromJson(ModelFile.ToJson(m));
            Check(AssignTables.Rows(json)[AssignTables.FrameTemperature].Count == 2, "kept in the model file");
        });
    }
}
