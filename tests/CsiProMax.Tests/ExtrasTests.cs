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
        (JsonObject)ExtraTools.All.Concat(PropertyTools.All).Concat(ModelerTools.All).Concat(DefineTools.All).Single(x => x.Name == tool).Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

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
            // The .e2k's header carries the time it was written: fixed, so the two writes compare (CI flake, 2026-11-26).
            E2kWriter.SavedAt = new DateTime(2026, 11, 26, 12, 0, 0);
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
            var e2kAfter = E2kWriter.Write(link.Model);
            E2kWriter.SavedAt = null;
            Check(e2kAfter == e2kBefore, "the .e2k does not change");
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
            var strips = Tables(plain.Text)["STRIP OBJECT CONNECTIVITY"];
            Check(strips.Count(r => r["Name"] == "MY-A") == 2 && strips.First(r => r["Name"] == "MY-A")["Number Segments"] == "2" && strips.First(r => r["Name"] == "MY-A")["Start Width Right"] == "1.5", "the hand-drawn strip through its three points");
            Check(strips.Where(r => r["Name"] == "CSA1").All(r => r["Layer"] == "B"), "it replaces the grid strip of the same name");
            Check(!Tables(plain.Text).ContainsKey("SPRING PROPERTY DEFINITIONS - AREA SPRINGS"), "no soil without a spring or a subgrade");
            Run(link, "define_spring", new { kind = "area", name = "SOIL-K", values = new { U3 = 20000 } });
            var floor = link.Model.AreaAssigns.First(x => link.Model.Area(x.Label)!.Kind == AreaKind.Floor && x.Story == link.Model.Stories[1].Name);
            Run(link, "assign_shell_data", new { objects = new[] { floor.Label + "@" + floor.Story }, spring = "SOIL-K" });
            var t = Tables(F2kWriter.Write(link.Model, new F2kOptions(Story: floor.Story)).Text);
            Check(t["SPRING PROPERTY DEFINITIONS - AREA SPRINGS"].Single()["Stiffnes U3"] == "20000", "the spring's U3 as the subgrade modulus");
            Check(t["AREA ASSIGNMENTS - AREA SPRINGS"].Single()["Spring Property"] == "SOIL-K", "on that slab only");
            var mat = Tables(F2kWriter.Write(link.Model, new F2kOptions(Story: floor.Story, Subgrade: 5000)).Text);
            Check(mat["SPRING PROPERTY DEFINITIONS - AREA SPRINGS"].Single()["Name"] == "SOIL1" && mat["AREA ASSIGNMENTS - AREA SPRINGS"].Count > 1, "a subgrade given puts one soil under every slab");
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
            foreach (var part in new[] { "function jointsIn(p)", "function jointAt(p, x, y, r)", "function jointInfo(name)", "function rightClick(p, x, y, shift, cx, cy)", "function selFilter()", "function loadRowsOf(o)", "function showJointInTable(name, hit)", "addComment(t, {type: \"j\", kind: \"joint\"", "gotJ.forEach(n => selJ.add(n))", "function loadGrid(loads)", "[\"Filter Selection...\", \"selFilter\"" })
                Check(html.Contains(part), "viewer.html lost " + part);
            var check = ViewerCheck.Script;
            foreach (var step in new[] { "selection: a left-to-right window round a support selects the support joint", "selection: right click on an object opens its information, with a Loads tab holding its loads", "selection: the Filter keeps only the checked kinds of the selection" })
                Check(check.Contains(step), "the field test lost the step " + step);
        });
        yield return ("extras: the results forms match the programs field by field: Deformed Shape, Member Force Diagram for Frames, Shell Forces/Stresses, Joint Reactions (Ahmed 2026-11-26)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "function caseBox(list)", "function caseBoxWire(d, list)", "Case/Combo type", "Multivalued options", "<legend>Contour Options</legend>", "Draw Contours on Objects",
                "<legend>Other Options</legend>", ">Wire Shadow<", "Moment 3-3\"", "Fill Diagram", "Show Values on Diagram", "<legend>Component Type</legend>", "<legend>Output Components</legend>", "<legend>Stress Averaging</legend>" })
                Check(html.Contains(part), "viewer.html lost " + part);
            Check(html.Contains("[[\"OK\", d => applyDeformed(d)], [\"Close\"], [\"Apply\"") && html.Contains("[[\"OK\", d => applyForces(d)], [\"Close\"], [\"Apply\""), "OK, Close, Apply as the programs");
        });
        yield return ("extras: Display Design Info as the programs (Design Output and Design Input lists by their names), the Detailing menu's schedules and the Units button (Ahmed 2026-11-26)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "function designOutputName(fam, s)", "\"P-M Ratio Colors & Values\"", "\"P-M Colors/No Values\"", "\"Column P-M-M Interaction Ratios\"", "\"Rebar Percentage\"", "<legend>Choose Display Type</legend>",
                "Design Output</label>", "Design Input</label>", "function buildDesignInput(fam, field)", "\"Design Sections\"", "\"Live Load Red. Factors\"",
                "data-menu=\"Detailing\"", "function detailingItems()", "\"Beam Schedule...\"", "\"Column Schedule...\"", "function openSchedule(what)", "dialogs.deformed(ms, \"Mode Shape\")", "<button id=\"units\"", "$(\"units\").onclick", "<legend>Consistent Units</legend>" })
                Check(html.Contains(part), "viewer.html lost " + part);
            Check(html.Contains("det.hidden = program === \"sap2000\""), "SAP2000 has no Detailing menu");
        });
        yield return ("extras: the material forms match ETABS 23.2 field by field: Add New Material Property (Region, Material Type, Standard, Grade), Material Property Data with units on the right and a Change... colour, the design, nonlinear, damping and time dependent forms with their own headings (Ahmed 2026-11-27 07:34)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "dialog(\"Add New Material Property\"", "<span>Region</span><select id=\"anR\">", "<span>Standard</span><select id=\"anS\">", "<span>Grade</span><select id=\"anG\">", "[\"Add New Material...\", () => hmMaterialNew(again)]",
                "<select id=\"mT\" disabled>", "<button id=\"mCol\">Change...</button>", "function hmColor(now, done)", ".dlg .gu{display:grid;grid-template-columns:auto 1fr auto", "<legend>Material Name and Type</legend>", "<legend>Design Properties for ${esc(label)} Materials</legend>",
                "<legend>Hysteresis Type And Drucker-Prager Parameters</legend>", "<legend>Stress-Strain Curve Definition Options</legend>", "<legend>Parametric Strain Data</legend>", "<b style=\"text-align:center\">Tension</b><b style=\"text-align:center\">Compression</b>",
                "<button id=\"nPlot\">Show Stress-Strain Plot...</button>", "<button id=\"nConv\">Convert To User Defined</button>", "<legend>Creep Analysis Type</legend>", "<legend>Time Dependence Parameters</legend>", "\"Viscous Proportional Damping\"]",
                "[[\"OK\"], [\"Cancel\"]], {width: 480});" })
                Check(html.Contains(part), "viewer.html lost " + part);
            // ETABS has no Material Grade box in Material Property Data: the grade comes from Add New Material Property.
            Check(!html.Contains("<span>Material Grade</span>"), "Material Property Data shows a Material Grade box ETABS does not have");
        });
        yield return ("extras: the frame section forms match ETABS 23.2 field by field: Frame Properties with its filter and Click to: buttons, Frame Property Shape Type, Import New Properties, Frame Section Property Data with notional size, colour, notes, source, dimensions by ETABS's names with units, fillet radius, mirror, modifiers, rebar, and Show Section Properties (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "dialog(\"Frame Properties\"", "<legend>Filter Properties List</legend>", "[\"Import New Properties...\", () => hmFrameImport(again)]", "[\"Delete Multiple Properties...\"", "dialog(\"Frame Property Shape Type\"",
                "dialog(\"Frame Section Property Import Data\"", "<button id=\"sNot\">Modify/Show Notional Size...</button>", "dialog(\"Notional Size Data\"", "<legend>Section Property Source</legend>", "<button id=\"sProps\">Show Section Properties...</button>",
                "hmRun(\"section_properties\", {shape, dims}, true)", "<button id=\"sMod\" style=\"width:100%\">Modify/Show Modifiers...</button>", "<button id=\"sRebar\" style=\"width:100%\">Modify/Show Rebar...</button>", "\"Steel I/Wide Flange\": {D: \"Total Depth\", B: \"Top Flange Width\"",
                "<span>Fillet Radius</span>", "Mirror About ${f.slice(-1)}-Axis", "dialog(\"Property/Stiffness Modification Factors\"", "function shapeSvg(shape, v)" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });
        yield return ("extras: the slab and wall forms match ETABS 23.2 and SAP2000 26.3 field by field: Slab Properties and Wall Properties lists of their own, Slab Property Data with notional size, modeling type, modifiers, colour, notes, one-way load and rib data, Wall Property Data with the automatic rigid zone, SAP2000's Shell Section Data with type, material angle and membrane/bending thickness (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "kind === \"wall\" ? \"Wall Properties\" : \"Slab Properties\"", "[\"Slab Sections...\", \"m:shellSections\"], [\"Wall Sections...\", \"m:wallSections\"]", "case \"wallSections\": return hmShellSections(\"wall\");",
                "sap ? \"Shell Section Data\" : wall ? \"Wall Property Data\" : \"Slab Property Data\"", "<button id=\"aNot\">Modify/Show Notional Size...</button>", "Include Automatic Rigid Zone Area Over Wall", "Use Special One-Way Load Distribution",
                "<span data-rib>Rib Spacing (Perpendicular to Rib Direction)</span>", "<span data-rib>Stem Width at Top</span>", "<span>Material Angle</span><input id=\"aMA\"", "<span>Bending</span><input id=\"aBT\"", "<legend>Select Section Type To Add</legend>" })
                Check(html.Contains(part), "viewer.html lost " + part);
            Check(!html.Contains("\"Slab or Wall Property Data\""), "ETABS has no Slab or Wall Property Data window: a slab and a wall have forms of their own");
        });
        yield return ("extras: the load forms match ETABS 23.2 and SAP2000 26.3 field by field: Define Load Patterns with each program's words, Load Cases as a table with Show Load Case Tree, Load Case Data's General, sub-type and Initial Condition, Load Combinations with Add Default Design Combos, Mass Sources then Mass Source Data with the move ratios and Mass Options (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "add: \"Add New Load\", mod: \"Modify Load\", lat: \"Modify Lateral Load...\"", "<button id=\"pCopy\">Add Copy of Load Pattern</button>", "<th>Load Case Name</th><th>Load Case Type</th>", "<button data-k=\"tree\">Show Load Case Tree...</button>",
                "Zero Initial Conditions - Start from Unstressed State", "Continue from State at End of Nonlinear Case", "<span>Exclude Objects in this Group</span>", "Add Code-Generated Load Combinations", "Define Combination of Load Case/Combo Results",
                "\"Mass Sources\", \"Mass Sources\"", "<span>Mass Source Name</span><input id=\"mN\"", "Adjust Diaphragm Lateral Mass to Move Mass Centroid by:", "This Ratio of Diaphragm Width in X Direction", "<legend>Mass Options</legend>", "Lump Lateral Mass at Story Levels", "Element Self Mass and Additional Mass" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });
        yield return ("extras: Story Data, Grid System Data, diaphragms, pier and spandrel labels, groups and section cuts match ETABS 23.2 and SAP2000 26.3 field by field (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "<th>Master Story</th><th>Similar To</th><th>Splice Story</th><th>Splice Height (${u.l})</th><th>Story Color</th>", "<button id=\"stColor\">Reset to Default Color</button>",
                "sapG ? \"Define Grid System Data\" : \"Grid System Data\"", "<legend>Story Range Option</legend>", "Display Grid Data as", "Quick Start New Rectangular Grids", "sapG ? \"Bubble Loc\" : \"Bubble Location\"",
                "<legend>Rigidity</legend>", "<button id=\"zRen\">Change Name</button>", "Multi-Story Spandrel", "sapX ? \"Use Group For\" : \"Group Design Usage\"", "[\"Composite\", \"Composite Beam Design\"]", "[\"MassWeight\", \"Mass and Weight\"]",
                "<legend>Define Section Cut By</legend>", "Design (P, V2, V3, T, M2, M3)", "<legend>Section Cut Force Location</legend>", "<legend>Rotation of Section Cut Local Axes</legend>" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });
        yield return ("extras: Link/Support Property Data and the spring forms match ETABS 23.2 and SAP2000 26.3 field by field: Total Mass and Weight, Factors For Line and Area Springs, Directional Properties, Nonlinearity Specification, Display Color and Property Notes (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "<legend>Total Mass and Weight</legend>", "<legend>Factors For Line and Area Springs</legend>", "<legend>Directional Properties</legend>", "<span>Property is Defined for This Length In a Line Spring</span>",
                "<span>Rotational Inertia 1</span>", "<legend>Nonlinearity Specification</legend>", "<legend>Stiffness Options</legend>", "<span>Display Color</span>", "<span>Link/Support Type</span>", "\"Link/Support Property Data\"" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });
        yield return ("extras: Define Functions match ETABS 23.2 and SAP2000 26.3: Choose Function Type to Add, each type's definition form with its own fields, the Function Graph and Convert to User Defined (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "<legend>Choose Function Type to Add</legend>", "\"Modify/Show Spectrum...\" : \"Modify/Show Function...\"", "[\"periodic\", \"User Periodic\"]", "[\"sawtooth\", \"Sawtooth\"]",
                "\"Response Spectrum Function Definition\" : \"Time History Function Definition\"", "<span>Function Damping Ratio</span>", "<legend>Function File</legend>", "<span>Header Lines to Skip</span>", "<span>Prefix Characters per Line to Skip</span>",
                "Values at Equal Intervals of", "<legend>Function Graph</legend>", "<button id=\"fConv\">Convert to User Defined</button>", "[\"ramp_time\", \"Ramp Time\", 1], [\"amplitude\", \"Amplitude\", 1], [\"max_time\", \"Maximum Time\", 2]", "[\"steps_per_cycle\", \"Number of Steps per Cycle\", 16]" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });
        yield return ("extras: Define is each program's own menu in its order and words (Section Properties, Spring Properties, Group Definitions / Groups, Coordinate Systems/Grids in SAP2000), stories and grids from ETABS's Edit menu, strips, tendons and slab rebar under Draw (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "m.Define = hmDefineMenu();", "[\"Edit Stories and Grid Systems...\", \"m:storyGrid\"]", "dialog(\"Edit Story and Grid System Data\"", "[\"Coordinate Systems/Grids...\", \"m:grids\"]",
                "[\"Group Definitions...\", \"m:st:group\"]", "[\"Soil Subgrade Properties...\", \"m:spr:area\"]", "dt(\"Shell Uniform Load Sets...\")", "only([\"etabs\", \"safe\"], [\"Design Strips...\", \"m:ex:strips\"]), [\"Tendons...\", \"m:ex:tendons\"]" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });
        yield return ("extras: Deck Sections and Deck Property Data match ETABS 23.2 field by field (Ahmed 2026-11-27)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "[\"Deck Sections...\", \"m:decks\"]", "listForm(\"Deck Properties\"", "dialog(\"Deck Property Data\"", "\"Slab Depth, tc\"", "\"Rib Width Bottom, wrb\"", "\"Deck Shear Thickness\"", "\"Shear Stud Tensile Strength, Fu\"", "submit(d, \"define_deck\"" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });
        yield return ("extras: the views and toolbars are drawn as ETABS draws them: grey see-through slabs with white edges, thin columns, solid grid, light-blue ground grid with flat bubbles, the axes at the origin, column squares in plan, captions as tabs, the toolbar and strip in the program's order (Ahmed 2026-11-26 22:19)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "const opt = {grid: true, extrude: false,", "floor: \"#737373\"", "ground: [0.85, 0.925, 1]", "const dash = gridsGuessed ? 7 : 0;", "flat: bay * 0.09}", "function drawPlanColumns(p, g, to, T)", "const presetCam = {iso: {yaw: Math.PI / 4, pitch: 0.57}",
                "thin(bufs.edges, 1, [1, 1, 1])", "function buildBars()", "function ddOpen(key, from)", "[\"t3d\", \"Set Default 3D View\", \"v:3d\"]", "[\"zprev\", \"Restore Previous Zoom\", \"zprev\"]", "[\"sall\", \"Select All (Ctrl+A)\", \"selAll\"]", "case \"selPrev\":", "case \"zprev\":",
                "<button class=\"dd\" title=\"Window Position\">", "function ddMenu(btn, items)", ".menu[hidden]{display:none}" })
                Check(html.Contains(part), "viewer.html lost " + part);
            // The four ETABS tools that were greyed out are now real (Ahmed 2026-11-26 22:37: "Add please the tools that we don't have").
            foreach (var part in new[] { "[\"reshape\", \"Reshape Object (click an end or corner, then its new place)\", \"d:reshape\"]", "[\"dqsec\", \"Quick Draw Secondary Beams (click in a bay)\", \"d:qsec\"]", "[\"dcirc\", \"Draw Circular Floor (click the centre, then a point on the edge)\", \"d:circ\"]",
                "[\"sint\", \"Select Using Intersecting Line (drag a line across the objects)\", \"t:iline\"]", "hmRun(\"reshape_object\", {object: r.name, from: r.from, to})", "function lineSelect(p, a, b)", "const circleOutline = (c, r, n)", "[\"Using Intersecting Line\", \"t:iline\"]" })
                Check(html.Contains(part), "viewer.html lost " + part);
            Check(!html.Contains("(not in CSI ProMax yet)\", \"\", 0, 0, 1]"), "a strip button is still greyed out as not in CSI ProMax");
            // The old corner-only axes must not come back: the triad stands at the global origin when it is in view.
            Check(html.Contains("const o = inside ? [at[0], at[1]] : [38, h - 38]"), "the global axes are drawn at the origin");
        });
        yield return ("extras: the Engine is one window laid out as the program: no window menu bar above it (its items in Tools and Help), white views, collapsed Model Explorer, plan dimensions, Ready (Ahmed 2026-11-26 22:11)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "function appMenuLoad()", "fetch(\"app-menu\"", "function drawDims(g, to, T, bub)", "#tools{display:none!important}", "if (!bgSaved) bg = \"white\";", "const model = node(\"model\", \"Model\", null,", ": \"Ready\";", "<option>Global</option>", "[\"run\", \"Run Analysis (F5)\", \"eng:run\"]" })
                Check(html.Contains(part), "viewer.html lost " + part);
            var main = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Panel", "Host", "MainWindow.cs"));
            Check(main.Contains("if (!CsiProMax.App.Modeler) layout.Children.Add(top);") && main.Contains("internal static bool RunAppMenu(string id)"), "the Engine window has no menu bar of its own; its items run from the page");
            var view = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Panel", "Host", "ModelView.cs"));
            Check(view.Contains("case \"app-menu\":") && view.Contains("path == \"app\" ? AppAsync(body)"), "the page reaches the window's items");
        });
        yield return ("extras: right click opens ETABS's Object Information: Object ID, property-grid tabs, a description, OK and Cancel; no browser menu (Ahmed 2026-11-26)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "function infoDialog(spec)", "<legend>Object ID</legend>", "<legend>Object Data</legend>", "class=\"pgdesc\"", "[\"OK\", w => {", "[\"Cancel\"]], {float: true, info: true",
                "[sap ? \"Location\" : \"Geometry\", geo], [\"Assignments\", asg], [\"Loads\", loadGrid(loads)]", "\"Opening\": \"Indicates if the shell object is an opening or not.\"", "\"Polar Inertia (Ix + Iy)\"",
                "async function applyInfo(name, kind, c, L)", "document.addEventListener(\"contextmenu\", e => {", "pickAt(p, x, y, 8)", "if (n === undefined) { n = \"(\"" })
                Check(html.Contains(part), "viewer.html lost " + part);
            Check(!html.Contains("[[\"Show in Tables\", () => showInTable(o)], [\"Close\"]]"), "the old information window's buttons are back");
            Check(ViewerCheck.Script.Contains("const objInfoProblem = d =>"), "the field test no longer checks the window's layout");
            var tools = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Tools", "PropertyTools.cs"));
            Check(tools.Contains("[\"groups\"] = GroupsOf(m, \"AREA\", label, story)") && tools.Contains("[\"opening\"] ="), "property_definitions no longer answers a shell's groups and opening");
        });
        yield return ("extras: CSI ProMax Engine: one program, ETABS's Model Explorer and menus, a run's results shown on the model and cleared by a change (Ahmed 2026-11-26)", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "<span id=\"app\">CSI ProMax Engine</span>", "data-menu=\"Analyze\"", "data-menu=\"Tools\"", "[[\"model\", \"Model\"], [\"display\", \"Display\"], [\"tables\", \"Tables\"], [\"reports\", \"Reports\"]]",
                "node(\"named\", \"Named Output Items\"", "function engineWith(pack, keep)", "async function engineImport(file)", "[\"Results from a Review Pack (.cpmx)...\", \"eng:import\"]", "function jointNamer(t, u)", "<span id=\"vname\"></span>" })
                Check(html.Contains(part), "viewer.html lost " + part);
            Check(ViewerCheck.Script.Contains("the Model Explorer is the program's"), "the field test no longer checks the explorer");
            Check(CsiProMax.Host.WebPages.Title(CsiProMax.Host.WebPages.Kind.ModelerView, "") == "CSI ProMax Engine", "the window is not named CSI ProMax Engine");
            var install = File.ReadAllText(Path.Combine(SourceText.Root(), "install.ps1"));
            Check(install.Contains("'CSI ProMax Engine.lnk'") && !install.Contains("'CSI ProMax Modeler.lnk'"), "the installer does not make the one Engine shortcut");
        });
        yield return ("extras: CSI ProMax Engine works with no AI: Run, the forms and the review pack are CSI ProMax's own code, never the chat's (Ahmed 2026-11-26)", () =>
        {
            var root = SourceText.Root();
            string Src(params string[] parts) => File.ReadAllText(Path.Combine([root, "src", "CsiProMax.Assistant", .. parts]));
            foreach (var (name, text) in new[] { ("EngineRun.cs", Src("Tools", "EngineRun.cs")), ("HandModel.cs", Src("Tools", "HandModel.cs")), ("HandTools.cs", Src("Tools", "HandTools.cs")), ("Pack.cs", Src("Tools", "Pack.cs")) })
                foreach (var chat in new[] { "ChatPane", "ChatEngine", "ChatMcp", "ChatAuto", "AiMode.On", "CsiProMax.Chat." })
                    Check(!text.Contains(chat, StringComparison.Ordinal), $"{name} uses the chat ({chat}); the Engine must work without the AI");
            var engine = Src("Tools", "EngineRun.cs");
            Check(engine.Contains("ProgramTools.Chat(program)", StringComparison.Ordinal), "the Run does not use the same tools the chat runs");
            var view = Src("Panel", "Host", "ModelView.cs");
            Check(view.Contains("path == \"run\" ? RunAsync(body)", StringComparison.Ordinal) && view.Contains("path == \"edit\" ? EditAsync(body)", StringComparison.Ordinal), "the window's Run or forms are not served whatever the AI choice");
            var steps = Src("Field", "AiModeFieldSteps.cs");
            Check(steps.Contains("without the AI chat: the Engine's Run, forms and results are there", StringComparison.Ordinal), "the field test does not check the Engine without the chat");
        });
        yield return ("extras: CSI ProMax Engine's pack lives as the model's results do: written beside the model, removed with its comments kept, replaced while another window reads it (Ahmed 2026-11-26)", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-life-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var oldTries = CsiProMax.Assistant.Packs.PackLife.Tries;
            try
            {
                CsiProMax.Assistant.Packs.PackLife.Tries = 3;
                var link = Frame();
                link.SaveAs(Path.Combine(folder, "Tower.e2k"));
                var (pack, page) = EngineRun.PackFiles(link);
                Check(pack == Path.Combine(folder, "Tower.cpmx") && page == Path.Combine(folder, "Tower viewer.html"), $"the Engine's pack is not beside the model, named as the pack written after a run in the program: {pack} | {page}");
                var comment = new JsonObject { ["id"] = "c1", ["text"] = "check this beam" };
                var built = Pack.Build(link, [], [], [], null, new JsonArray(comment.DeepClone()));
                CsiProMax.Assistant.Packs.PackLife.Write(pack, page, built.Zip, Pack.ViewerHtml(built.Content));
                Check(File.Exists(pack) && File.Exists(page), "the pack and its page were not written");
                // Another window reading the pack (as the Viewer does) while it is replaced.
                using (var reader = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    CsiProMax.Assistant.Packs.PackLife.Write(pack, page, built.Zip, Pack.ViewerHtml(built.Content));
                Check(Pack.ReadComments(pack).Comments.Count == 1, "the pack replaced while it was read is not whole");
                var left = CsiProMax.Assistant.Packs.PackLife.Remove(pack, page);
                Check(left.Count == 0 && !File.Exists(pack) && !File.Exists(page), "the pack was not removed: " + string.Join(", ", left));
                var kept = CsiProMax.Assistant.Packs.PackLife.Comments(pack);
                Check(kept.Count == 1 && kept[0]!["id"]!.GetValue<string>() == "c1" && File.Exists(CsiProMax.Assistant.Packs.PackLife.CommentsFile(pack)), "the comments were lost with the pack");
                // The next pack takes them back, each once (also when the sidecar and the pack both hold one).
                var again = Pack.Build(link, [], [], [], null, CsiProMax.Assistant.Packs.PackLife.Comments(pack));
                CsiProMax.Assistant.Packs.PackLife.Write(pack, page, again.Zip, Pack.ViewerHtml(again.Content));
                Check(CsiProMax.Assistant.Packs.PackLife.Comments(pack).Count == 1 && Pack.ReadComments(pack).Comments.Count == 1, "the comments did not come back once into the next pack");
                Check(CsiProMax.Assistant.Packs.PackLife.Remove(Path.Combine(folder, "none.cpmx"), "").Count == 0, "removing a pack that is not there is not quiet");
                Check(!Directory.EnumerateFiles(folder, "*.writing").Any(), "a half-written file was left behind");
            }
            finally { CsiProMax.Assistant.Packs.PackLife.Tries = oldTries; try { Directory.Delete(folder, true); } catch (IOException) { } }
        });
        yield return ("extras: CSI ProMax Engine's Run form asks each program for its own designs, and finds the selected objects in the program by where they are", () =>
        {
            Check(EngineRun.Kinds["etabs"].Select(k => k.Kind).SequenceEqual(["steel", "concrete", "composite", "shear_wall", "slab"]) && EngineRun.Kinds["sap2000"].Length == 2 && EngineRun.Kinds["safe"].Single().Kind == "slab", "the design kinds per program are not ETABS's, SAP2000's and SAFE's");
            Check(EngineRun.DesignArgs(CsiProgram.Sap2000, ["steel"]) == """{"steel":true,"concrete":false}""", "SAP2000's design arguments: " + EngineRun.DesignArgs(CsiProgram.Sap2000, ["steel"]));
            Check(EngineRun.DesignArgs(CsiProgram.Etabs, ["concrete", "slab"]) == """{"kinds":["concrete","slab"]}""", "ETABS's design arguments: " + EngineRun.DesignArgs(CsiProgram.Etabs, ["concrete", "slab"]));
            var link = Frame();
            var names = link.Frames().Take(2).Select(f => f.Name).Concat(link.Areas().Take(1).Select(a => a.Name)).ToList();
            var at = EngineRun.Places(link, names);
            var (frames, areas) = EngineRun.Find(link, at);
            Check(frames.SequenceEqual(link.Frames().Take(2).Select(f => f.Name)) && areas.Count == Math.Min(1, link.Areas().Count), $"the selected objects were not found by where they are: {string.Join(", ", frames)} | {string.Join(", ", areas)}");
            var refused = EngineRun.Start(link, analyse: false, kinds: [], apiFile: _ => Path.Combine(Path.GetTempPath(), "none.dll"));
            Check(refused["ok"]?.GetValue<bool>() == false && (refused["error"]?.GetValue<string>() ?? "").Contains("Nothing was asked", StringComparison.Ordinal), "a run with nothing ticked was not refused: " + refused.ToJsonString());
            var wrong = EngineRun.Start(link, analyse: true, kinds: ["punching"], apiFile: _ => Path.Combine(Path.GetTempPath(), "none.dll"));
            Check(wrong["ok"]?.GetValue<bool>() == false, "a design kind the program does not have was not refused");
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var marker in new[] { "dialog(\"Run Options\"", "Selected objects only", "\"eng:design\"", "enginePost({unlock: true})", "enginePost({changed: true})" })
                Check(html.Contains(marker, StringComparison.Ordinal), "the page has no " + marker);
            var view = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Panel", "Host", "ModelView.cs"));
            Check(view.Contains("EngineRun.Changed(moved)", StringComparison.Ordinal) && view.Contains("EngineRun.Changed(edited)", StringComparison.Ordinal), "a change to the model does not remove the run's pack");
            var program = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Program.cs"));
            Check(program.Contains("WebPages.RemovedPageFor(file)", StringComparison.Ordinal), "a Viewer window on a removed pack does not say it was removed");
        });
        yield return ("Ahmed 2026-11-27 12:37: a setting (Settings > Runs, and the Run form) keeps the CSI program open with the analysed model after a Run, for now; the next run closes it first", () =>
        {
            Check(new CsiProMax.Settings.AgentSettings().KeepProgramOpenAfterRun, "keeping the program open is not on by default for now");
            Check(CsiProMax.UI.SettingsLayout.Settings.TryGetValue("KeepProgramOpenAfterRun", out var tab) && tab == CsiProMax.UI.SettingsLayout.Runs, "the setting is not on the Runs tab");
            var root = SourceText.Root();
            var html = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Viewer", "viewer.html"));
            Check(html.Contains("id=\"rKeep\"", StringComparison.Ordinal) && html.Contains("keep_open: !!(q(d, \"#rKeep\")", StringComparison.Ordinal), "the Run form has no Keep open choice sent with the run");
            var view = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Panel", "Host", "ModelView.cs"));
            Check(view.Contains("keepOpen: keepOpen", StringComparison.Ordinal) && view.Contains("st.KeepProgramOpenAfterRun = kv", StringComparison.Ordinal), "the Engine does not pass or remember the Keep open choice");
            var run = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Tools", "EngineRun.cs"));
            Check(run.Contains("kept.Reveal()", StringComparison.Ordinal) && run.Contains("CloseKept();", StringComparison.Ordinal), "the run does not leave the program shown, or does not close it before the next run");
            var scratch = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Field", "ScratchProgram.cs"));
            var reveal = scratch[scratch.IndexOf("public bool Reveal()", StringComparison.Ordinal)..];
            Check(reveal[..reveal.IndexOf("public bool Running", StringComparison.Ordinal)].Contains("_watch?.Dispose()", StringComparison.Ordinal), "the program left open would still have its close question answered No");
            var field = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "FieldTest.cs"));
            Check(field.Contains("keepOpen: true), keptOpen: true)", StringComparison.Ordinal) && field.Contains("the last run left open", StringComparison.Ordinal), "the field test does not check Keep open");
            var old = EngineRun.KeepOpenSetting;
            try
            {
                EngineRun.KeepOpenSetting = () => true;
                Check(EngineRun.Status(false)["keep_open"]?.GetValue<bool>() == true, "the Run form is not told the setting");
            }
            finally { EngineRun.KeepOpenSetting = old; }
        });
        yield return ("Ahmed 2026-11-27: Releases/Partial Fixity springs and Insertion Point's mirror and joint offsets, as ETABS's tables and SAP2000's .s2k", () =>
        {
            var link = Frame();
            var b = link.Model.LineAssigns.First(x => link.Model.Line(x.Label)?.Kind == LineKind.Beam);
            var obj = b.Label + "@" + b.Story;
            Refused(link, "assign_frame_data", new { objects = new[] { obj }, partial_fixity = new Dictionary<string, double> { ["M3I"] = 5000 } }, "needs its release");
            Run(link, "assign_frame_data", new { objects = new[] { obj }, release_i = new[] { "M3" }, release_j = new[] { "M3" }, partial_fixity = new Dictionary<string, double> { ["M3I"] = 5000, ["M3J"] = 4000 } });
            Check(PropertyDefs.PartialFixity(b)["M3I"] == 5000 && AssignTables.Row(b, AssignTables.PartialFixity)["M3JSpring"] == "4000", "the springs are not ETABS's table row");
            Refused(link, "assign_frame_data", new { objects = new[] { obj }, partial_fixity = new Dictionary<string, double> { ["X3I"] = 1 } }, "no partial fixity");
            Run(link, "assign_frame_data", new { objects = new[] { obj }, tables = new Dictionary<string, object[]> { [AssignTables.Insertion] = [new Dictionary<string, string> { ["Mirror2"] = "Yes", ["OffsetCSys"] = "Global", ["ZI"] = "0.05" }] } });
            link.Model.Target = "sap2000";
            var text = CsiProMax.S2k.S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(text.Contains("FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY", StringComparison.Ordinal) && text.Contains("PartialFix=Yes", StringComparison.Ordinal) && text.Contains("Mirror2=Yes", StringComparison.Ordinal) && text.Contains("JtOffsetZI=0.05", StringComparison.Ordinal), "the .s2k does not carry them");
            var read = CsiProMax.S2k.S2kReader.Read(text, "Extras").Model;
            var rb = read.LineAssigns.First(x => x.Label == b.Label && x.Story == b.Story);
            Check(PropertyDefs.PartialFixity(rb).GetValueOrDefault("M3J") == 4000 && AssignTables.Row(rb, AssignTables.Insertion).GetValueOrDefault("Mirror2") == "Yes" && AssignTables.Row(rb, AssignTables.Insertion).GetValueOrDefault("ZI") == "0.05", "the .s2k's springs or insertion point did not come back");
            link.Model.Target = "etabs";
            Run(link, "assign_frame_data", new { objects = new[] { obj }, release_j = Array.Empty<string>() });
            Check(!PropertyDefs.PartialFixity(b).ContainsKey("M3J") && PropertyDefs.PartialFixity(b).ContainsKey("M3I"), "taking a release off did not take its spring");
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var marker in new[] { "Frame Partial Fixity Springs", "Mirror about Local 2 Axis", "Frame Joint Offsets to Cardinal Point", "Do not transform frame stiffness for offsets from centroid" })
                Check(html.Contains(marker, StringComparison.Ordinal), "the page has no " + marker);
        });
        yield return ("Ahmed 2026-11-27: the Analyze and Design forms carry the program's own items (ETABS 23.2, SAP2000 26.3): design codes, each code's preferences, active DOF; kept as the program's table rows, the code in the .e2k", () =>
        {
            Check(ProgramForms.Codes("etabs", "steel").Contains("AISC 360-16") && ProgramForms.Codes("etabs", "concrete").Contains("ACI 318-19") && ProgramForms.Codes("etabs", "shear_wall").Contains("ACI 318-19"), "ETABS's design codes: " + string.Join(", ", ProgramForms.Codes("etabs", "steel")));
            Check(ProgramForms.Codes("sap2000", "steel").Contains("AISC 360-16") && ProgramForms.Codes("sap2000", "concrete").Contains("Eurocode 2-2004"), "SAP2000's design codes: " + string.Join(", ", ProgramForms.Codes("sap2000", "concrete")));
            Check(ProgramForms.Fields("etabs", "Steel Frame Design Preferences - AISC 360-16").Exists(f => f.Key == "PatLLF" && f.Label == "Pattern Live Load Factor"), "the steel preferences do not have ETABS's item names");
            Check(ProgramForms.Designs("safe").Single().Kind == "slab" && ProgramForms.AnalysisForms("etabs")[0].Table == "Analysis Options - Active Degrees of Freedom" && ProgramForms.AnalysisForms("sap2000").Any(f => f.Table == "Active Degrees of Freedom"), "the designs and Analyze forms per program");
            Check(CsiProMax.Assistant.Etabs.ModelerTables.KeyOf("Steel Frame Design Preferences - AISC 360-16") == "" && ProgramOptions.HasDefinitionTable("etabs", "Analysis Options - SAPFire Options") && !ProgramOptions.HasDefinitionTable("etabs", "Preferences - Steel Design - AISC 360-16"), "a form's table goes into its own program as one row");
            var link = Frame();
            Check(ProgramForms.Code(link.Model, "steel") == "AISC 360-16", "the template's steel code from the .e2k: " + ProgramForms.Code(link.Model, "steel"));
            var forms = Run(link, "program_forms", new { });
            Check((forms["designs"] as JsonArray)!.Count == ProgramForms.Designs("etabs").Count && (forms["analysis"] as JsonArray)!.Count == ProgramForms.AnalysisForms("etabs").Count, "program_forms lists every design and Analyze form");
            Run(link, "set_program_form", new { design = "steel", code = "Eurocode 3-2005", table = "Steel Frame Design Preferences - Eurocode 3-2005", values = new Dictionary<string, string> { ["Pattern Live Load Factor"] = "0.8" } });
            Check(ProgramForms.Code(link.Model, "steel") == "Eurocode 3-2005", "the code did not change");
            var e2k = CsiProMax.E2k.E2kWriter.Write(link.Model);
            Check(e2k.Contains("STEELPREFERENCE  CODE  \"Eurocode 3-2005\"", StringComparison.Ordinal) && !e2k.Contains("CODE \"AISC 360-16\"  THDESIGN", StringComparison.Ordinal), "the .e2k does not carry the new steel code");
            Check(link.Model.DefinitionTables!["Steel Frame Design Preferences - Eurocode 3-2005"].Single()["PatLLF"] == "0.8", "the preference is not the table's row");
            var one = Run(link, "program_forms", new { design = "steel" });
            Check(((one["items"] as JsonArray)!.First(i => i!["field"]!.GetValue<string>() == "PatLLF"))!["value"]!.GetValue<string>() == "0.8", "the form does not show the value set");
            Run(link, "set_program_form", new { table = "Steel Frame Design Preferences - Eurocode 3-2005", values = new Dictionary<string, string> { ["PatLLF"] = "" } });
            Check(!link.Model.DefinitionTables!.ContainsKey("Steel Frame Design Preferences - Eurocode 3-2005"), "a blank value did not go back to the program's default");
            Refused(link, "set_program_form", new { table = "Steel Frame Design Preferences - AISC 360-16", values = new Dictionary<string, string> { ["Nope"] = "1" } }, "no item");
            Refused(link, "set_program_form", new { design = "steel", code = "AISC 999" }, "no code");
            Refused(link, "set_program_form", new { table = "Preferences - Steel Design - AISC 360-16", values = new Dictionary<string, string> { ["PatLLF"] = "1" } }, "no form");
            var back = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            Check(ProgramForms.Code(back, "steel") == "Eurocode 3-2005", "the code is not kept in the .cpm");
            // SAP2000: the preference tables are its .s2k's own, read back as they were written.
            var sap = Frame();
            sap.Model.Target = "sap2000";
            Run(sap, "set_program_form", new { design = "concrete", code = "ACI 318-19", table = "Preferences - Concrete Design - ACI 318-19", values = new Dictionary<string, string> { ["PatLLF"] = "0.7" } });
            Run(sap, "set_program_form", new { table = "Active Degrees of Freedom", values = new Dictionary<string, string> { ["RZ"] = "No" } });
            var text = CsiProMax.S2k.S2kWriter.Write(sap.Model, "a.s2k").Text;
            Check(text.Contains("TABLE:  \"PREFERENCES - CONCRETE DESIGN - ACI 318-19\"", StringComparison.Ordinal) && text.Contains("TABLE:  \"ACTIVE DEGREES OF FREEDOM\"", StringComparison.Ordinal), "the .s2k has no preference or DOF table");
            var read = CsiProMax.S2k.S2kReader.Read(text, "Extras").Model;
            Check(read.DefinitionTables?.GetValueOrDefault("Preferences - Concrete Design - ACI 318-19")?.Single().GetValueOrDefault("PatLLF") == "0.7" && read.DefinitionTables?.GetValueOrDefault("Active Degrees of Freedom")?.Single().GetValueOrDefault("RZ") == "No", "the .s2k's forms did not come back");
            Check(ProgramForms.Code(sap.Model, "concrete") == "ACI 318-19", "SAP2000's code is not kept");
            var engine = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Tools", "EngineRun.cs"));
            Check(engine.Contains("ctx.Tool(\"design_codes\"", StringComparison.Ordinal), "SAP2000's codes are not set before its design");
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var marker in new[] { "async function hmPrefs(kind, code, changed)", "\"View/Revise Preferences...\"", "Set To Default Values", "Reset To Previous Values", "Fast DOFs", "\"m:pform:\" + i", "Preferences for ${code}" })
                Check(html.Contains(marker, StringComparison.Ordinal), "the page has no " + marker);
        });
        yield return ("Ahmed 2026-11-27: Run asks Analyze only / Analyze and Design / Design only, opens the design types only for a design, and the job counts as closed only when the program's process has ended", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            var run = html[html.IndexOf("async function engineRun(mode)", StringComparison.Ordinal)..html.IndexOf("async function engineStart(ask)", StringComparison.Ordinal)];
            foreach (var marker in new[] { "> Analyze only</label>", "> Analyze and Design</label>", "> Design only</label>", "Select All", "Clear All", "x.disabled = m === \"a\"", "analyse: m !== \"d\"", "Tick at least one design to run." })
                Check(run.Contains(marker, StringComparison.Ordinal), "the Run form has no " + marker);
            var engine = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Tools", "EngineRun.cs"));
            Check(engine.Contains("var ended = Ended(window);", StringComparison.Ordinal) && engine.Contains("[\"closed\"] = _closed", StringComparison.Ordinal) && engine.Contains("did not end after the job", StringComparison.Ordinal), "the Run does not check that the program's process ended");
            Check(EngineRun.Status(false)["closed"] is not null, "the Run's status does not say whether the program closed");
            var field = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "FieldTest.cs"));
            Check(field.Contains("Run Analysis and Design with one chosen design does both in one job, only that design, and closes the program", StringComparison.Ordinal) && field.Contains("status[\"closed\"]?.GetValue<bool>() == true", StringComparison.Ordinal), "the field test does not check Ahmed's Run and that the program closed");
        });
        yield return ("extras: CSI ProMax Engine's Run writes the model for its program beside the model, says plainly when the program is not on this PC, and never touches the model's own file (Ahmed 2026-11-26)", () =>
        {
            var link = Frame();
            var folder = Path.Combine(Path.GetTempPath(), "cpm-engine-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var own = link.SaveAs(Path.Combine(folder, "Tower.e2k"));
                var revision = link.Revision;
                Check(Path.GetDirectoryName(EngineRun.RunFolder(link)) == Path.GetDirectoryName(own) && EngineRun.RunFolder(link).EndsWith(" run", StringComparison.Ordinal), "the run's folder is not beside the model: " + EngineRun.RunFolder(link));
                var written = link.WriteForRun(Path.Combine(folder, "run", "Tower.e2k"));
                Check(File.Exists(written) && written.EndsWith(".e2k", StringComparison.Ordinal), "the ETABS model was not written for the run: " + written);
                Check(link.Path == own && link.Revision == revision && !link.Unsaved, "writing for the run changed the model's own file or revision");
                link.Model.Target = "sap2000";
                var s2k = link.WriteForRun(Path.Combine(folder, "run2", "Tower.e2k"));
                Check(s2k.EndsWith(".s2k", StringComparison.Ordinal) && File.Exists(s2k), "a SAP2000 model is not run from its .s2k: " + s2k);
                var answer = EngineRun.Start(link, false, _ => Path.Combine(folder, "nothing-here.dll"));
                Check(answer["ok"]!.GetValue<bool>() == false && answer["error"]!.GetValue<string>().Contains("SAP2000 is not installed on this PC", StringComparison.Ordinal), "no program on the PC is not said plainly: " + answer.ToJsonString());
                Check(EngineRun.Status(false)["running"]!.GetValue<bool>() == false, "a run that could not start says it is running");
                var view = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Panel", "Host", "ModelView.cs"));
                Check(view.Contains("path == \"run\" ? RunAsync(body)") && view.Contains("EngineRun.Start(offline, design)"), "the Engine window cannot start a run");
                var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
                Check(html.Contains("[\"Run Analysis...\", \"eng:run\", \"F5\"]") && html.Contains("async function engineRun(mode)"), "Analyze > Run Analysis is not in the Engine");
                var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Field", "ScratchProgram.cs"));
                Check(scratch.Contains("_app.Hide();"), "the program is not hidden while it runs");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
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
