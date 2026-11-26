using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.F2k;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>What the .e2k has no place for: SAP2000's joint constraints and moving loads, SAFE's own strips and soil (ExtraTools).</summary>
static class ExtrasTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Extras", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args) =>
        (JsonObject)ExtraTools.All.Concat(PropertyTools.All).Concat(ModelerTools.All).Single(x => x.Name == tool).Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

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

    static Dictionary<string, List<Dictionary<string, string>>> Tables(string text) => CsiTableText.Parse(text);

    static List<LineAssign> Beams(StructuralModel m) => m.LineAssigns.Where(x => m.Line(x.Label)!.Kind == LineKind.Beam).ToList();

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("extras: a Modeler model is made for one program, chosen at New Model; it is kept in the .cpm, and saving writes that program's file", () =>
        {
            var link = new OfflineEtabs();
            Run(link, "new_model", new { title = "P", program = "sap2000", template = "concrete_frame", x_spans = new[] { 6.0 }, y_spans = new[] { 5.0 }, stories = 2, story_height = 3.0 });
            Check(link.Model.Target == "sap2000", "made for SAP2000");
            Check(ModelFile.FromJson(ModelFile.ToJson(link.Model)).Target == "sap2000", "kept in the .cpm");
            var folder = Path.Combine(Path.GetTempPath(), "cpm-target-" + Guid.NewGuid().ToString("N"));
            try
            {
                var saved = Run(link, "save_model_as", new { name = "P", folder });
                Check(saved["program"]!.GetValue<string>() == "SAP2000" && File.Exists(Path.Combine(folder, "P.s2k")) && saved["file"]!.GetValue<string>().EndsWith(".s2k"), "saving writes the .s2k: " + saved.ToJsonString());
                Check(File.Exists(Path.Combine(folder, "P.cpm")), "and the .cpm");
                Check(OfflineEtabs.Load(Path.Combine(folder, "P.cpm")).Target == "sap2000", "the .cpm opens as a SAP2000 model again");
                var changed = Run(link, "set_model_program", new { program = "safe" });
                Check(changed["program"]!.GetValue<string>() == "SAFE" && changed["was"]!.GetValue<string>() == "SAP2000" && link.Model.Target == "safe", "changed to SAFE");
                Run(link, "save_model_as", new { name = "P2", folder });
                Check(File.Exists(Path.Combine(folder, "P2.f2k")), "a SAFE model saves its .f2k");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
            Refused(link, "set_model_program", new { program = "revit" }, "one of");
            var etabs = new OfflineEtabs();
            Run(etabs, "new_model", new { title = "E" });
            Check(etabs.Model.Target == "etabs", "ETABS when none is given");
        });

        yield return ("extras: joint constraints of every type go into the .s2k with their joints, beside the floors' diaphragms, and stay out of the .e2k", () =>
        {
            var link = Frame();
            var e2kBefore = E2kWriter.Write(link.Model);
            foreach (var type in ExtrasDefs.ConstraintTypes)
                Run(link, "define_constraint", new { name = "C-" + type, type, tolerance = 0.01 });
            Check(link.Model.Extras!.Constraints.Single(c => c.Type == "Local").Dofs.SequenceEqual(ExtrasDefs.LocalDofs), "a local constraint ties U1..R3 by default");
            Check(link.Model.Extras.Constraints.Single(c => c.Type == "Plate").Axis == "Z", "a plate is about Z by default");
            var b = Beams(link.Model)[0];
            var line = link.Model.Line(b.Label)!;
            Run(link, "assign_joint_constraint", new { objects = new[] { line.PointI + "@" + b.Story, line.PointJ + "@" + b.Story.ToLowerInvariant() }, constraint = "C-Body" });
            Check(link.Model.Extras.Constraints.Single(c => c.Name == "C-Body").Joints.SequenceEqual([line.PointI + "@" + b.Story, line.PointJ + "@" + b.Story]), "the joints, by the story's own name");
            var t = Tables(S2kWriter.Write(link.Model, "a.s2k").Text);
            Check(t["CONSTRAINT DEFINITIONS - BODY"].Single()["UX"] == "Yes", "the body constraint");
            Check(t["CONSTRAINT DEFINITIONS - WELD"].Single()["Tolerance"] == "0.01", "the weld's tolerance");
            Check(t["CONSTRAINT DEFINITIONS - LOCAL"].Single().ContainsKey("U1") && !t["CONSTRAINT DEFINITIONS - LOCAL"].Single().ContainsKey("CoordSys"), "the local constraint in local axes");
            Check(t["JOINT CONSTRAINT ASSIGNMENTS"].Count(r => r["Constraint"] == "C-Body") == 2, "its two joints");
            Check(t["CONSTRAINT DEFINITIONS - DIAPHRAGM"].Count >= 2, "the floors' diaphragms and the one defined, in one table");
            Check(E2kWriter.Write(link.Model) == e2kBefore, "the .e2k does not change");
            var back = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            Check(back.Extras!.Constraints.Count == ExtrasDefs.ConstraintTypes.Length, "kept in the .cpm");
            Refused(link, "define_constraint", new { name = "C-X", type = "Glue" }, "constraint type");
            Refused(link, "define_constraint", new { name = "C-X", type = "Weld" }, "tolerance");
            Refused(link, "define_constraint", new { name = "C-X", type = "Equal", dofs = new[] { "U1" } }, "not 'U1'");
            Refused(link, "assign_joint_constraint", new { objects = new[] { "NOPE@" + b.Story }, constraint = "C-Body" }, "no point");
            Refused(link, "assign_joint_constraint", new { objects = new[] { line.PointI + "@" + b.Story }, constraint = "C-None" }, "no constraint");
            Run(link, "assign_joint_constraint", new { objects = new[] { line.PointI + "@" + b.Story }, constraint = "" });
            Check(link.Model.Extras.Constraints.Single(c => c.Name == "C-Body").Joints.Count == 1, "\"\" takes a joint out");
            foreach (var type in ExtrasDefs.ConstraintTypes) Run(link, "define_constraint", new { name = "C-" + type, delete = true });
            Check(link.Model.Extras is null, "nothing left: no extras in the file");
        });

        yield return ("extras: lanes, vehicles, vehicle classes and a moving load case go into the .s2k, and what is used cannot be deleted", () =>
        {
            var link = Frame();
            var beams = Beams(link.Model).Where(x => x.Story == Beams(link.Model)[^1].Story).Take(2).Select(x => x.Label + "@" + x.Story).ToArray();
            Run(link, "define_lane", new { name = "L1", frames = beams, offset = 0.5, width = 3 });
            Run(link, "define_vehicle", new { name = "HL", standard = "hl-93k" });
            Run(link, "define_vehicle", new { name = "TRK", axles = new object[] { new { load = 35 }, new { load = 145, spacing = 4.3 } }, uniform = 9.3 });
            Run(link, "define_vehicle_class", new { name = "VC", vehicles = new object[] { new { vehicle = "HL" }, new { vehicle = "TRK", scale = 1.25 } } });
            Run(link, "define_moving_case", new { name = "MOVE", assignments = new object[] { new { @class = "VC", lanes = new[] { "L1" } } } });
            var t = Tables(S2kWriter.Write(link.Model, "a.s2k").Text);
            Check(t["LANE DEFINITION DATA"].Count == 2 && t["LANE DEFINITION DATA"][0]["Width"] == "3", "the lane along its two frames");
            Check(t["VEHICLES 1 - STANDARD VEHICLES"].Single(r => r["VehName"] == "HL")["Type"] == "HL-93K", "the standard truck by its own name");
            Check(t["VEHICLES 2 - GENERAL VEHICLES 1 - GENERAL"].Single()["NumInter"] == "1", "the general truck, one axle behind the first");
            var loads = t["VEHICLES 3 - GENERAL VEHICLES 2 - LOADS"];
            Check(loads.Count == 3 && loads[0]["LoadType"] == "Leading Load" && loads[1]["InterMinD"] == "4.3" && loads[1]["InterAxle"] == "145" && loads[2]["LoadType"] == "Trailing Load", "the general truck's axles");
            Check(t["VEHICLES 4 - VEHICLE CLASSES"].Count == 2, "the class's two vehicles");
            Check(t["LOAD CASE DEFINITIONS"].Single(r => r["Case"] == "MOVE")["Type"] == "LinMoving", "the moving load case");
            Check(t["CASE - MOVING LOAD 1 - LANE ASSIGNMENTS"].Single() is var asg && asg["MaxLoaded"] == "1" && asg["NumLanes"] == "1" && !asg.ContainsKey("Lane"), "its lane assignment");
            Check(t["CASE - MOVING LOAD 2 - LANES LOADED"].Single()["Lane"] == "L1", "the lane it loads");
            Refused(link, "define_vehicle", new { name = "X" }, "axles");
            Refused(link, "define_vehicle", new { name = "X", standard = "Lorry" }, "standard vehicle");
            Refused(link, "define_lane", new { name = "X", frames = new[] { "NOPE@NOPE" }, width = 3 }, "no frame");
            Refused(link, "define_vehicle_class", new { name = "X", vehicles = new object[] { new { vehicle = "NOPE" } } }, "no vehicle");
            Refused(link, "define_moving_case", new { name = "X", assignments = new object[] { new { @class = "VC", lanes = new[] { "L1" }, min_loaded = 2 } } }, "cannot be");
            Refused(link, "define_moving_case", new { name = "Dead", assignments = new object[] { new { @class = "VC", lanes = new[] { "L1" } } } }, "already a load case");
            Refused(link, "define_lane", new { name = "L1", delete = true }, "used by MOVE");
            Refused(link, "define_vehicle", new { name = "TRK", delete = true }, "used by VC");
            Run(link, "define_moving_case", new { name = "MOVE", delete = true });
            Run(link, "define_vehicle_class", new { name = "VC", delete = true });
            Run(link, "define_lane", new { name = "L1", delete = true });
            var defs = Run(link, "extra_definitions", new { });
            Check(defs["vehicles"]!.AsArray().Count == 2 && defs["lanes"]!.AsArray().Count == 0, "listed: " + defs.ToJsonString());
        });

        yield return ("extras: a hand-drawn strip and each slab's area spring as soil go into the SAFE file", () =>
        {
            var link = Frame();
            Run(link, "define_strip", new { name = "MY-A", layer = "A", points = new[] { new[] { 0.0, 2.5 }, new[] { 6.0, 2.5 }, new[] { 11.0, 3.0 } }, width_left = 1, width_right = 1.5 });
            Run(link, "define_strip", new { name = "CSA1", layer = "B", points = new[] { new[] { 1.0, 0.0 }, new[] { 1.0, 4.0 } }, width_left = 0.5, width_right = 0.5 });
            var plain = F2kWriter.Write(link.Model, new F2kOptions(Story: link.Model.Stories[1].Name));
            var strips = Tables(plain.Text)["OBJECT GEOMETRY - DESIGN STRIPS"];
            Check(strips.Count(r => r["Strip"] == "MY-A") == 3 && strips.First(r => r["Strip"] == "MY-A")["WBRight"] == "1.5", "the hand-drawn strip through its three points");
            Check(strips.Where(r => r["Strip"] == "CSA1").All(r => r["Layer"] == "B"), "it replaces the grid strip of the same name");
            Check(!Tables(plain.Text).ContainsKey("SOIL PROPERTIES"), "no soil without a spring or a subgrade");
            Run(link, "define_spring", new { kind = "area", name = "SOIL-K", values = new { U3 = 20000 } });
            var floor = link.Model.AreaAssigns.First(x => link.Model.Area(x.Label)!.Kind == AreaKind.Floor && x.Story == link.Model.Stories[1].Name);
            Run(link, "assign_shell_data", new { objects = new[] { floor.Label + "@" + floor.Story }, spring = "SOIL-K" });
            var t = Tables(F2kWriter.Write(link.Model, new F2kOptions(Story: floor.Story)).Text);
            Check(t["SOIL PROPERTIES"].Single()["Subgrade"] == "20000", "the spring's U3 as the subgrade modulus");
            Check(t["SOIL PROPERTY ASSIGNMENTS"].Single()["SoilProp"] == "SOIL-K", "on that slab only");
            var mat = Tables(F2kWriter.Write(link.Model, new F2kOptions(Story: floor.Story, Subgrade: 5000)).Text);
            Check(mat["SOIL PROPERTIES"].Single()["Soil"] == "SOIL1" && mat["SOIL PROPERTY ASSIGNMENTS"].Count > 1, "a subgrade given puts one soil under every slab");
            Refused(link, "define_strip", new { name = "X", points = new[] { new[] { 0.0, 0.0 } }, width_left = 1 }, "two points");
            Refused(link, "define_strip", new { name = "X", layer = "C", points = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 } }, width_left = 1 }, "strip layer");
            Refused(link, "define_strip", new { name = "X", points = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 } } }, "a width");
        });

        yield return ("extras: the Modeler gives its loads as the programs' load tables, so Properties and the information window show them (Ahmed 2026-11-26)", () =>
        {
            var link = Frame();
            var frame = link.Frames().First().Name;
            link.SetFrameLoad(frame, "Dead", 5, false);
            var point = link.Points().First();
            var (label, story) = (point.Name.Split('@')[0], point.Name.Split('@')[1]);
            link.Model.PointLoads.Add(new PointLoad { Object = label, Story = story, Tokens = ["TYPE", "\"FORCE\"", "LC", "\"Dead\"", "FZ", "-80"] });
            var names = link.Tables();
            foreach (var t in new[] { "Frame Loads - Distributed", "Joint Loads - Force" })
                Check(names.Any(n => n.Name == t && !n.Empty), t + " is listed with rows: " + string.Join(", ", names.Select(n => n.Name)));
            var dist = link.ReadTable("Frame Loads - Distributed", null, null);
            int U(string f) => Array.IndexOf(dist.Fields.ToArray(), f);
            Check(dist.Rows.Any(r => r[U("UniqueName")] == frame && r[U("LoadPattern")] == "Dead" && r[U("FOverLA")] == "5" && r[U("Direction")] == "Gravity"), "the frame's load is a row named as the Modeler names the frame");
            var joint = link.ReadTable("Joint Loads - Force", null, null);
            Check(joint.Rows.Any(r => r[Array.IndexOf(joint.Fields.ToArray(), "UniqueName")] == point.Name && r[Array.IndexOf(joint.Fields.ToArray(), "FZ")] == "-80"), "the joint load is a row named as the Modeler names the joint");
            var tables = (JsonObject)Pack.Build(link, ["Grid Definitions", .. OfflineEtabs.LoadTables], [], [], null, null).Content["tables"]!;
            Check(tables.ContainsKey("Frame Loads - Distributed") && tables.ContainsKey("Joint Loads - Force"), "the Modeler page's pack carries them: " + string.Join(", ", tables.Select(t => t.Key)));
            foreach (var file in new[] { "src/CsiProMax.Assistant/Panel/Host/ModelView.cs", "src/CsiProMax.Assistant/Field/ModelerCheck.cs" })
                Check(File.ReadAllText(Path.Combine(SourceText.Root(), file)).Contains(".. OfflineEtabs.LoadTables]"), file + " does not give the Modeler page its load tables");
        });
        yield return ("extras: the Viewer and Modeler select joints and supports, open information on a right click, and filter the selection (Ahmed 2026-11-26)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "function jointsIn(p)", "function jointAt(p, x, y, r)", "function jointInfo(name)", "function rightClick(p, x, y, shift, cx, cy)", "function selFilter()", "function loadRowsOf(o)", "gotJ.forEach(n => selJ.add(n))", "data-page=\"lds\">Loads (", "[\"filter\", \"Filter the selection" })
                Check(html.Contains(part), "viewer.html lost " + part);
            var check = ViewerCheck.Script;
            foreach (var step in new[] { "selection: a left-to-right window round a support selects the support joint", "selection: right click on an object opens its information, with a Loads tab holding its loads", "selection: the Filter keeps only the checked kinds of the selection" })
                Check(check.Contains(step), "the field test lost the step " + step);
        });
        yield return ("extras: Edit > Model Text keeps them, and Undo in the Modeler window takes one back", () =>
        {
            var link = Frame();
            HandModel.Reset();
            JsonObject Do(object request) => HandModel.Handle(link, JsonDocument.Parse(JsonSerializer.Serialize(request)).RootElement);
            Check(Do(new { tool = "define_constraint", args = new { name = "C1", type = "Rod" } })["ok"]!.GetValue<bool>(), "defined through the window");
            Do(new { undo = true });
            Check(link.Model.Extras is null || link.Model.Extras.Constraints.Count == 0, "Undo took it back");
            Run(link, "define_constraint", new { name = "C2", type = "Beam" });
            var text = (JsonObject)ModelTextTools.All.Single(x => x.Name == "model_text").Run(JsonDocument.Parse("""{"section":"LOAD PATTERNS"}""").RootElement, link);
            ModelTextTools.All.Single(x => x.Name == "edit_model_text").Run(JsonDocument.Parse(JsonSerializer.Serialize(new { section = "LOAD PATTERNS", text = text["text"]!.GetValue<string>() })).RootElement, link);
            Check(link.Model.Extras?.Constraints.Single().Name == "C2", "Model Text kept the constraint");
        });
    }
}
