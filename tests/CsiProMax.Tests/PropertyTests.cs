using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// The Modeler's property items (PropertyTools, PropertyDefs): materials of every type, every section shape, slab and
/// wall types, modifiers, releases, local axes, insertion points, end offsets and springs. Ahmed: nothing in the model's
/// input may be impossible in the Modeler, "every option every spring".
/// </summary>
static class PropertyTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Props", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args)
    {
        var t = PropertyTools.All.Concat(CaseTools.All).Concat(HandTools.All).First(x => x.Name == tool);
        return (JsonObject)t.Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);
    }

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

    static StructuralModel ViaE2k(StructuralModel m) => E2kReader.Read(E2kWriter.Write(m));
    static string Beam(StructuralModel m, int n = 0) { var a = m.LineAssigns.Where(x => m.Line(x.Label)!.Kind == LineKind.Beam).ElementAt(n); return a.Label + "@" + a.Story; }
    static string Floor(StructuralModel m) { var a = m.AreaAssigns.First(x => m.Area(x.Label)!.Kind == AreaKind.Floor); return a.Label + "@" + a.Story; }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("properties: a steel, a rebar and a tendon material go in with their strengths, and come back from the .e2k and .s2k", () =>
        {
            var link = Frame();
            Run(link, "define_material_data", new { name = "S355", type = "Steel", grade = "S355", values = new { WEIGHTPERVOLUME = 77, E = 2.1e8, U = 0.3, FY = 355000, FU = 510000 } });
            Run(link, "define_material_data", new { name = "B500", type = "Rebar", values = new { WEIGHTPERVOLUME = 77, E = 2e8, FY = 500000, FU = 630000 } });
            Run(link, "define_material_data", new { name = "T1860", type = "Tendon", values = new { E = 1.95e8, FY = 1670000, FU = 1860000 } });
            var back = ViaE2k(link.Model);
            var s = back.Materials.Single(x => x.Name == "S355");
            Check(s.Type == "Steel" && s.Number("FY") == 355000 && s.Number("FYE") == 390500 && s.Number("E") == 2.1e8, "S355 back from the .e2k: " + string.Join(" | ", s.Lines.Select(l => string.Join(" ", l))));
            Check(back.Materials.Single(x => x.Name == "B500").Get("SYMTYPE") == "Uniaxial", "rebar is uniaxial");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("Material=S355") && s2k.Contains("Material=B500"), "the .s2k has the materials");
            Run(link, "define_material_data", new { name = "S355-R", was = "S355", type = "Steel", values = new { FY = 345000, FU = 450000 } });
            Check(link.Model.Materials.Exists(x => x.Name == "S355-R" && x.Number("FY") == 345000 && x.Number("E") == 2.1e8), "renamed, E kept, Fy changed");
            Refused(link, "define_material_data", new { name = "X", type = "Steel", values = new { E = 2e8, FY = 355000, FU = 300000 } }, "Fu at least Fy");
            Refused(link, "define_material_data", new { name = "X", type = "Concrete", values = new { E = 3e7 } }, "f'c");
            Refused(link, "define_material_data", new { name = "X", type = "Wood", values = new { E = 1e7 } }, "no material type");
            Refused(link, "define_material_data", new { name = "X", type = "Steel", values = new { E = 2e8, FY = 3, FU = 4, ZZ = 1 } }, "no value");
        });

        yield return ("properties: every section shape the programs offer goes in, and comes back from the .e2k", () =>
        {
            var link = Frame();
            Run(link, "define_material_data", new { name = "S355", type = "Steel", values = new { E = 2.1e8, FY = 355000, FU = 510000, WEIGHTPERVOLUME = 77 } });
            var conc = link.Model.Materials.First(x => x.IsConcrete).Name;
            var dims = new Dictionary<string, double> { ["D"] = 0.4, ["B"] = 0.2, ["TF"] = 0.015, ["TW"] = 0.01, ["DIS"] = 0.01 };
            foreach (var shape in PropertyDefs.Shapes)
            {
                var d = shape.Dims.Where(k => k is not ("BBOTTOM" or "TFBOTTOM")).ToDictionary(k => k, k => shape.Concrete && k is "TF" or "TW" ? 0.15 : dims[k]);
                if (shape.Concrete && d.ContainsKey("TF")) { d["D"] = 0.6; d["B"] = 1.0; d["TW"] = 0.3; }
                Run(link, "define_frame_shape", new { name = "X-" + shape.Name, shape = shape.Name, material = shape.Concrete ? conc : "S355", dims = d });
            }
            var back = ViaE2k(link.Model);
            foreach (var shape in PropertyDefs.Shapes)
            {
                var s = back.FrameSections.Single(x => x.Name == "X-" + shape.Name);
                Check(s.Shape == shape.Name && s.Number("D") is > 0, shape.Name + " back from the .e2k");
            }
            var s2k = S2kWriter.Write(link.Model, "a.s2k");
            Check(s2k.Text.Contains("SectionName=X-Steel Pipe") && s2k.Text.Contains("SectionName=\"X-Steel Rod\"") || s2k.Text.Contains("SectionName=X-Steel Rod") || s2k.Text.Contains("X-Steel Rod"), "the .s2k has the pipe and the rod");
            Refused(link, "define_frame_shape", new { name = "Y", shape = "Steel I/Wide Flange", material = conc, dims = new { D = 0.4, B = 0.2, TF = 0.01, TW = 0.01 } }, "steel shape");
            Refused(link, "define_frame_shape", new { name = "Y", shape = "Steel I/Wide Flange", material = "S355", dims = new { D = 0.4, B = 0.2, TF = 0.01 } }, "needs TW");
            Refused(link, "define_frame_shape", new { name = "Y", shape = "Steel I/Wide Flange", material = "S355", dims = new { D = 0.4, B = 0.2, TF = 0.3, TW = 0.01 } }, "thicker than the depth");
            Refused(link, "define_frame_shape", new { name = "Y", shape = "Steel Pipe", material = "S355", dims = new { D = 0.4, TW = 0.01, B = 1 } }, "no dimension");
            Refused(link, "define_frame_shape", new { name = "Y", shape = "Steel Zed", material = "S355", dims = new { D = 0.4 } }, "no section shape");
        });

        yield return ("properties: section modifiers, slab types and cracked slab modifiers go in and come back from the .e2k and .s2k", () =>
        {
            var link = Frame();
            var conc = link.Model.Materials.First(x => x.IsConcrete).Name;
            Run(link, "define_frame_shape", new { name = "B-CR", shape = "Concrete Rectangular", material = conc, dims = new { D = 0.6, B = 0.3 }, modifiers = new { JMOD = 0.01, I3MOD = 0.35, I2MOD = 0.35 } });
            Run(link, "define_shell_type", new { name = "S-CR", kind = "slab", slab_type = "Drop", modeling = "ShellThick", thickness = 0.3, material = conc, modifiers = new { M11MOD = 0.25, M22MOD = 0.25 } });
            Run(link, "define_shell_type", new { name = "W-CR", kind = "wall", thickness = 0.25, material = conc, modifiers = new { F11MOD = 0.7, F22MOD = 0.7 } });
            var back = ViaE2k(link.Model);
            var b = PropertyDefs.Modifiers(back.FrameSections.Single(x => x.Name == "B-CR").Lines, PropertyDefs.FrameModifiers);
            Check(b["JMOD"] == 0.01 && b["I3MOD"] == 0.35 && b["AMOD"] == 1, "the beam's modifiers back from the .e2k");
            var s = back.ShellSections.Single(x => x.Name == "S-CR");
            Check(s.Get("SLABTYPE") == "Drop" && s.Get("MODELINGTYPE") == "ShellThick" && PropertyDefs.Modifiers(s.Lines, PropertyDefs.ShellModifiers)["M11MOD"] == 0.25, "the drop panel back");
            Check(back.ShellSections.Single(x => x.Name == "W-CR").IsWall, "the wall back");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("JMod=0.01") && s2k.Contains("M11Mod=0.25"), "the .s2k carries the modifiers");
            Run(link, "define_frame_shape", new { name = "B-CR", was = "B-CR", shape = "Concrete Rectangular", material = conc, dims = new { D = 0.7, B = 0.3 } });
            Check(PropertyDefs.Modifiers(link.Model.FrameSections.Single(x => x.Name == "B-CR").Lines, PropertyDefs.FrameModifiers)["JMOD"] == 0.01, "changing the size keeps the modifiers");
            Refused(link, "define_shell_type", new { name = "Z", kind = "slab", slab_type = "Bubble", thickness = 0.3, material = conc }, "slab type");
            Refused(link, "define_frame_shape", new { name = "B-CR2", shape = "Concrete Rectangular", material = conc, dims = new { D = 0.6, B = 0.3 }, modifiers = new { XMOD = 1 } }, "no modifier");
        });

        yield return ("properties: releases, local axes, insertion point, end offsets and modifiers on a beam go in and come back from the .e2k and .s2k", () =>
        {
            var link = Frame();
            var beam = Beam(link.Model);
            Run(link, "assign_frame_data", new { objects = new[] { beam }, release_i = new[] { "M3", "M2" }, release_j = new[] { "M3" }, angle = 90, cardinal = 8, offset_i = 0.2, offset_j = 0.25, rigid_zone = 0.5, modifiers = new { I3MOD = 0.35 } });
            var back = ViaE2k(link.Model);
            var at = beam.LastIndexOf('@');
            var x = back.LineAssigns.Single(y => y.Label == beam[..at] && y.Story == beam[(at + 1)..]);
            var (i, j) = PropertyDefs.ReleasesOf(x);
            Check(i.SequenceEqual(new[] { "M2", "M3" }) && j.SequenceEqual(new[] { "M3" }), "the releases back: " + PropertyDefs.Value(x.Extra, "RELEASE"));
            Check(PropertyDefs.Value(x.Extra, "RELEASE") == "M2I M3I M3J", "written as ETABS writes them");
            Check(PropertyDefs.Value(x.Extra, "ANG") == "90" && PropertyDefs.Value(x.Extra, "CARDINALPT") == "8" && PropertyDefs.Value(x.Extra, "LENGTHOFFI") == "0.2", "angle, insertion point, offsets back");
            Check(PropertyDefs.Modifiers(x.Extra, PropertyDefs.FrameModifiers)["I3MOD"] == 0.35, "member modifiers back");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            foreach (var t in new[] { "FRAME RELEASE ASSIGNMENTS 1 - GENERAL", "FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL", "FRAME INSERTION POINT ASSIGNMENTS", "FRAME OFFSET ALONG LENGTH ASSIGNMENTS", "FRAME PROPERTY MODIFIERS" })
                Check(s2k.Contains("TABLE:  \"" + t + "\""), "the .s2k has " + t);
            Run(link, "assign_frame_data", new { objects = new[] { beam }, release_i = Array.Empty<string>(), release_j = Array.Empty<string>() });
            Check(PropertyDefs.Value(link.Model.LineAssigns.Single(y => y.Label == beam[..at] && y.Story == beam[(at + 1)..]).Extra, "RELEASE") is null, "[] takes the releases off");
            Refused(link, "assign_frame_data", new { objects = new[] { beam }, release_i = new[] { "T" }, release_j = new[] { "T" } }, "unstable");
            Refused(link, "assign_frame_data", new { objects = new[] { beam }, cardinal = 12 }, "1 to 11");
            Refused(link, "assign_frame_data", new { objects = new[] { beam }, offset_i = 4, offset_j = 4 }, "longer than the member");
            Refused(link, "assign_frame_data", new { objects = new[] { "nothing@Story1" }, angle = 5 }, "There is no frame");
            Refused(link, "assign_frame_data", new { objects = new[] { beam } }, "Give what to assign");
        });

        yield return ("properties: point, line and area springs are defined, assigned, kept by the .e2k, and cannot be deleted while used", () =>
        {
            var link = Frame();
            Run(link, "define_spring", new { kind = "point", name = "PS1", values = new { UX = 1000, UY = 1000, UZ = 5000 } });
            Run(link, "define_spring", new { kind = "line", name = "LS1", values = new { U2 = 200 } });
            Run(link, "define_spring", new { kind = "area", name = "AS1", values = new { U3 = 20000 } });
            var base_ = link.Model.Stories[0].Name;
            var joint = link.Model.PointAssigns.First(p => p.Story == base_).Point + "@" + base_;
            Run(link, "assign_joint_data", new { objects = new[] { joint }, restraint = Array.Empty<string>(), spring = "PS1" });
            Run(link, "assign_frame_data", new { objects = new[] { Beam(link.Model) }, spring = "LS1" });
            Run(link, "assign_shell_data", new { objects = new[] { Floor(link.Model) }, spring = "AS1", modifiers = new { M11MOD = 0.25 } });
            var back = ViaE2k(link.Model);
            var springs = PropertyDefs.Springs(back);
            Check(springs.Count == 3 && springs.Single(s => s.Name == "PS1").Values["UZ"] == 5000, "the springs back from the .e2k");
            Check(PropertyDefs.SpringUsers(back, "point", "PS1").Count() == 1 && PropertyDefs.SpringUsers(back, "area", "AS1").Count() == 1 && PropertyDefs.SpringUsers(back, "line", "LS1").Count() == 1, "the assignments back");
            var at = joint.LastIndexOf('@');
            Check(back.PointAssigns.Single(p => p.Point == joint[..at] && p.Story == base_).Restraint is null, "the joint is freed");
            var s2k = S2kWriter.Write(link.Model, "a.s2k");
            Check(s2k.Text.Contains("TABLE:  \"JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED\"") && s2k.Text.Contains("U3=5000"), "the .s2k has the joint spring");
            Check(s2k.Text.Contains("TABLE:  \"AREA STIFFNESS MODIFIERS\""), "and the area modifiers");
            Refused(link, "define_spring", new { kind = "point", name = "PS1", delete = true }, "assigned to");
            Refused(link, "define_spring", new { kind = "point", name = "PS2", values = new { U1 = 5 } }, "not 'U1'");
            Refused(link, "define_spring", new { kind = "point", name = "PS2", values = new { UX = 0 } }, "above 0");
            Refused(link, "assign_joint_data", new { objects = new[] { joint }, spring = "nope" }, "no point spring");
            Run(link, "assign_joint_data", new { objects = new[] { joint }, spring = "" });
            Run(link, "define_spring", new { kind = "point", name = "PS1", delete = true });
            Check(!PropertyDefs.Springs(link.Model).Exists(s => s.Name == "PS1"), "deleted once no joint uses it");
            var defs = Run(link, "property_definitions", new { objects = new[] { Beam(link.Model), Floor(link.Model), joint } });
            Check(defs["objects"]!.AsArray().Count == 3 && defs["objects"]![1]!["spring"]!.GetValue<string>() == "AS1", "property_definitions reads the objects: " + defs["objects"]!.ToJsonString());
        });

        yield return ("properties: link properties of every type are written as ETABS writes them, and ETABS's own link reads back", () =>
        {
            var sample = E2kReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "samples", "etabs23-default.e2k"));
            var own = PropertyDefs.Links(sample).Single(l => l.Name == "Link1");
            Check(own.Type == "LINEAR" && own.Dofs["U1"].Stiff == 1, "ETABS's Link1: " + own.Type + " " + string.Join(",", own.Dofs.Keys));
            var link = Frame();
            foreach (var type in PropertyDefs.LinkTypes)
                Run(link, "define_link", new { name = "L-" + type, type, dofs = new Dictionary<string, object> { ["U1"] = new { stiffness = 1000, damping = 5 }, ["R3"] = new { stiffness = 20 } } });
            var back = PropertyDefs.Links(ViaE2k(link.Model));
            Check(back.Count(x => x.Name.StartsWith("L-")) == PropertyDefs.LinkTypes.Length, "every type back from the .e2k");
            var l = back.Single(x => x.Name == "L-RUBBER ISOLATOR");
            Check(l.Type == "RUBBER ISOLATOR" && l.Dofs["U1"] == (1000, 5) && l.Dofs["R3"] == (20, 0), "the isolator's directions back");
            Check(E2kWriter.Write(link.Model).Contains("LINKPROP  \"L-LINEAR\"  U1STIFF  1000"), "written as ETABS writes it");
            Run(link, "define_link", new { name = "L-GAP", type = "GAP", dofs = new Dictionary<string, object> { ["U1"] = new { stiffness = 50 } } });
            Check(PropertyDefs.Links(link.Model).Single(x => x.Name == "L-GAP").Dofs.Count == 1, "changing a link replaces its directions");
            Refused(link, "define_link", new { name = "L-X", type = "SPRINGY", dofs = new Dictionary<string, object> { ["U1"] = new { stiffness = 1 } } }, "link type");
            Refused(link, "define_link", new { name = "L-X", dofs = new Dictionary<string, object> { ["U7"] = new { stiffness = 1 } } }, "not 'U7'");
            Refused(link, "define_link", new { name = "L-X" }, "at least one direction");
            Run(link, "define_link", new { name = "L-GAP", delete = true });
            Check(!PropertyDefs.Links(link.Model).Exists(x => x.Name == "L-GAP"), "deleted");
        });

        yield return ("properties: auto hinges go on a frame, come back from the .e2k, are written in the .s2k, and [] takes them off", () =>
        {
            var link = Frame();
            var beam = Beam(link.Model); var at = beam.LastIndexOf('@');
            Run(link, "assign_frame_data", new { objects = new[] { beam }, hinges = new object[] { new { at = 0.95, dof = "m3" }, new { at = 0.05, dof = "M3" }, new { at = 0.5, dof = "V2" } } });
            var back = PropertyDefs.HingesOf(ViaE2k(link.Model), beam[..at], beam[(at + 1)..]);
            Check(back.Count == 3 && back[0] == new PropertyDefs.HingeDef(0.05, "M3") && back[2].At == 0.95, "the hinges back, in order: " + string.Join(",", back));
            var shown = Run(link, "property_definitions", new { objects = new[] { beam } });
            Check(shown["objects"]![0]!["hinges"]!.AsArray().Count == 3 && shown["hinge_dofs"]!.AsArray().Count == PropertyDefs.HingeDofs.Length, "shown in the form");
            var s2k = S2kWriter.Write(link.Model, "a.s2k");
            Check(s2k.Text.Contains("FRAME HINGE ASSIGNS 04 - AUTO FEMA 356 - CONCRETE BEAM") && s2k.Text.Split("DOF=M3").Length == 3, "the two flexural hinges written for SAP2000");
            Check(s2k.Notes.Any(n => n.Contains("V2 hinge")), "the shear hinge named in a note");
            Run(link, "assign_frame_data", new { objects = new[] { beam }, hinges = new object[] { new { at = 0.5, dof = "P-M2-M3" } } });
            Check(PropertyDefs.HingesOf(link.Model, beam[..at], beam[(at + 1)..]).Single().Dof == "P-M2-M3", "a new list replaces the old");
            Refused(link, "assign_frame_data", new { objects = new[] { beam }, hinges = new object[] { new { at = 1.2, dof = "M3" } } }, "0 to 1");
            Refused(link, "assign_frame_data", new { objects = new[] { beam }, hinges = new object[] { new { at = 0.2, dof = "M9" } } }, "not 'M9'");
            Refused(link, "assign_frame_data", new { objects = new[] { beam }, hinges = new object[] { new { at = 0.2, dof = "M3" }, new { at = 0.2, dof = "M3" } } }, "the same");
            Run(link, "assign_frame_data", new { objects = new[] { beam }, hinges = Array.Empty<object>() });
            Check(PropertyDefs.Hinges(link.Model).Count == 0 && !link.Model.Passthrough.Exists(r => r.Heading == PropertyDefs.HingeHeading), "taken off, no empty section left");
        });

        yield return ("properties: the .s2k has none of what SAP2000 26 refused in the 0.4.39 field run", () =>
        {
            var link = Frame();
            var beam = Beam(link.Model);
            Run(link, "define_material_data", new { name = "S355", type = "Steel", values = new { E = 2.1e8, FY = 355000, FU = 510000, WEIGHTPERVOLUME = 77 } });
            Run(link, "define_frame_shape", new { name = "X-Pipe", shape = "Steel Pipe", material = "S355", dims = new { D = 0.4, TW = 0.01 } });
            Run(link, "define_link", new { name = "L-ISO", type = "RUBBER ISOLATOR", dofs = new Dictionary<string, object> { ["U1"] = new { stiffness = 1000 } } });
            Run(link, "define_link", new { name = "L-LIN", type = "LINEAR", dofs = new Dictionary<string, object> { ["U1"] = new { stiffness = 1000 } } });
            Run(link, "assign_frame_data", new { objects = new[] { beam }, hinges = new object[] { new { at = 0.05, dof = "M3" } } });
            link.AddLoadPattern("TEMP", "Temperature", 0);
            Run(link, "assign_object_loads", new { kind = "frame_temperature", pattern = "TEMP", objects = new[] { beam }, values = new { temperature = 25 } });
            Run(link, "define_function", new { name = "TH1", kind = "history", from = "ramp", period = 1, cycles = 2 });
            if (!link.Model.LoadCases.Exists(c => c.Name == "Modal")) Run(link, "define_load_case", new { name = "Modal", type = "Modal - Eigen", max_modes = 12 });
            Run(link, "define_load_case", new { name = "THX", type = "Linear Modal History", modal_case = "Modal", steps = 20, step_size = 0.01, loads = new object[] { new { accel = "U1", factor = 1, function = "TH1" } } });
            var s2k = S2kWriter.Write(link.Model, "a.s2k");
            var tables = CsiProMax.CsiTables.CsiTableText.Parse(s2k.Text);
            Check(tables["FRAME SECTION PROPERTIES 01 - GENERAL"].All(r => r["Shape"] != "Pipe" || double.Parse(r["tw"], System.Globalization.CultureInfo.InvariantCulture) > 0), "a pipe's wall goes in tw (tw cannot be zero)");
            Check(tables["LINK PROPERTY DEFINITIONS 02 - LINEAR"].All(r => r["Link"] != "L-ISO"), "only linear links in 02 - Linear: " + string.Join(",", tables["LINK PROPERTY DEFINITIONS 02 - LINEAR"].Select(r => r["Link"])));
            Check(s2k.Notes.Any(n => n.Contains("L-ISO")), "the isolator's stiffness is named in a note");
            Check(tables["FRAME HINGE ASSIGNS 04 - AUTO FEMA 356 - CONCRETE BEAM"].All(r => !r.ContainsKey("BeyondE")), "no BeyondE value SAP2000 does not know");
            Check(tables["FRAME LOADS - TEMPERATURE"].Single()["Temp"] == "25", "the temperature is in Temp");
            Check(tables["CASE - MODAL HISTORY 1 - GENERAL"].Single() is var th && th["HistoryType"] == "Transient" && !th.ContainsKey("ModalCase") && !th.ContainsKey("Motion"), "the modal history has its HistoryType, and no ModalCase or Motion");
            Check(tables["LOAD CASE DEFINITIONS"].Single(r => r["Case"] == "THX")["ModalCase"] == "Modal", "its modal case is in the case definitions (0.4.40)");
            Check(tables["FRAME HINGE ASSIGNS 00 - HINGE DISTRIBUTION TYPE"].Single()["HgeDistrTyp"] == "Nonlinear Beam Column", "the hinged frame's distribution type");
            Check(tables["GROUPS 1 - DEFINITIONS"].All(r => r.ContainsKey("Color")), "groups have a colour");
        });

        yield return ("properties: the .s2k uses SAP2000 26's own field names (0.4.40 field run): open shapes, modifiers, insertion point", () =>
        {
            var link = Frame();
            Run(link, "define_material_data", new { name = "S355", type = "Steel", values = new { E = 2.1e8, FY = 355000, FU = 510000, WEIGHTPERVOLUME = 77 } });
            foreach (var shape in new[] { "Steel Angle", "Steel Channel", "Steel Tee", "Steel Double Angle", "Steel Double Channel" })
                Run(link, "define_frame_shape", new { name = "X-" + shape, shape, material = "S355", dims = shape.Contains("Double") ? (object)new { D = 0.2, B = 0.1, TF = 0.01, TW = 0.008, DIS = 0.01 } : new { D = 0.2, B = 0.1, TF = 0.01, TW = 0.008 } });
            var beam = Beam(link.Model);
            Run(link, "assign_frame_data", new { objects = new[] { beam }, modifiers = new { I3MOD = 0.35 }, cardinal = 8 });
            var s2k = S2kWriter.Write(link.Model, "a.s2k");
            var t = CsiProMax.CsiTables.CsiTableText.Parse(s2k.Text);
            var sections = t["FRAME SECTION PROPERTIES 01 - GENERAL"];
            foreach (var (shape, sap) in new[] { ("Steel Angle", "Angle"), ("Steel Channel", "Channel"), ("Steel Tee", "Tee"), ("Steel Double Angle", "Double Angle"), ("Steel Double Channel", "Double Channel") })
            {
                var r = sections.SingleOrDefault(x => x["SectionName"] == "X-" + shape);
                Check(r is not null && r["Shape"] == sap && double.Parse(r["Area"], System.Globalization.CultureInfo.InvariantCulture) > 0, shape + " goes across as " + sap);
            }
            var angle = sections.Single(x => x["SectionName"] == "X-Steel Angle");
            double A(string k) => double.Parse(angle[k], System.Globalization.CultureInfo.InvariantCulture);
            Check(Math.Abs(A("Area") - (0.1 * 0.01 + 0.19 * 0.008)) < 1e-9 && A("I23") != 0 && A("S33Top") < A("S33Bot"), "the angle's area, product of inertia and unequal moduli");
            Check(sections.All(r => !r.ContainsKey("S33") && !r.ContainsKey("S22") && r.ContainsKey("S33Top") && r.ContainsKey("S22Right")), "S33Top/S33Bot/S22Left/S22Right, not S33/S22");
            Check(t["FRAME PROPERTY MODIFIERS"].Any(r => r["I33Mod"] == "0.35") && t["FRAME PROPERTY MODIFIERS"].All(r => r.ContainsKey("MassMod") && r.ContainsKey("I22Mod") && !r.ContainsKey("I3Mod")), "frame modifiers by SAP2000's names");
            Check(t.TryGetValue("AREA STIFFNESS MODIFIERS", out var am) is false || am.All(r => r.ContainsKey("MassMod") && !r.ContainsKey("MMod")), "area modifiers by SAP2000's names");
            var ins = t["FRAME INSERTION POINT ASSIGNMENTS"].First(r => r["CardinalPt"] != "10");
            Check(ins.ContainsKey("JtOffsetXI") && ins.ContainsKey("Transform") && !ins.ContainsKey("XI") && !ins.ContainsKey("StiffTransform"), "insertion point by SAP2000's names");
            Check(!s2k.Notes.Any(n => n.Contains("no plain SAP2000 shape")), "no shape left out: " + string.Join("; ", s2k.Notes));
        });

        yield return ("properties: every edit is one undo step in the Modeler window", () =>
        {
            var link = Frame();
            HandModel.Reset();
            JsonObject Do(object request) => HandModel.Handle(link, JsonDocument.Parse(JsonSerializer.Serialize(request)).RootElement);
            var beam = Beam(link.Model);
            Check(Do(new { tool = "assign_frame_data", args = new { objects = new[] { beam }, angle = 45 } })["ok"]!.GetValue<bool>(), "assigned through the window");
            var at = beam.LastIndexOf('@');
            Check(PropertyDefs.Value(link.Model.LineAssigns.Single(y => y.Label == beam[..at] && y.Story == beam[(at + 1)..]).Extra, "ANG") == "45", "the angle is on");
            Do(new { undo = true });
            Check(PropertyDefs.Value(link.Model.LineAssigns.Single(y => y.Label == beam[..at] && y.Story == beam[(at + 1)..]).Extra, "ANG") is null, "undo takes it off");
        });
    }
}
