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
        yield return ("properties: Material Property Data in full (design, nonlinear, damping, time dependence, notes, rupture) reaches the .e2k, .s2k, .f2k and .cpm (Ahmed 2026-11-26)", () =>
        {
            var link = Frame();
            Run(link, "define_material_data", new
            {
                name = "C40", type = "Concrete", grade = "C40/50",
                values = new { WEIGHTPERVOLUME = 25, E = 35000000, U = 0.2, A = 1e-5, FC = 40000, STRAINATFC = 0.0022, STRAINATULTIMATE = 0.0035, IOCOMPRESSION = 0.002 },
                texts = new { HYSTYPE = "Takeda", SSTYPE = "Mander", TIMEDEPCONCCODE = "CEBFIP90" },
                tables = new Dictionary<string, object>
                {
                    ["General"] = new[] { new { Color = "Yellow", Notes = "Cores and transfer slabs" } },
                    ["Damping Parameters"] = new[] { new { ModalRatio = "0.03", VisMass = "0", VisStiff = "0.001" } },
                    ["Concrete Data"] = new[] { new { LtWtConc = "Yes", LtWtFact = "0.85", IsUserFr = "Yes", UserFr = "4000", TimeType = "CEB-FIP 90", TimeCreep = "Yes", CreepFact = "1" } },
                    ["Time Dependence - CEB-FIP 90"] = new[] { new { CementCoeff = "0.25", RelHumid = "70", BetaSC = "5", ShrinkStart = "3" } },
                    ["User Stress-Strain Curves"] = new[] { new { Point = "1", Strain = "-0.0035", Stress = "-40000" }, new { Point = "2", Strain = "0", Stress = "0" } },
                },
            });
            var c = link.Model.Materials.Single(x => x.Name == "C40");
            var e2k = E2kWriter.Write(link.Model);
            var flat = System.Text.RegularExpressions.Regex.Replace(e2k, " +", " ");
            Check(flat.Contains("HYSTYPE \"Takeda\"") && flat.Contains("STRAINATFC 0.0022") && flat.Contains("IOCOMPRESSION 0.002") && flat.Contains("TIMEDEPCONCCODE \"CEBFIP90\""), "the .e2k has the nonlinear line and the code: " + string.Join(" | ", c.Lines.Select(l => string.Join(" ", l))));
            var back = ViaE2k(link.Model).Materials.Single(x => x.Name == "C40");
            Check(back.Get("SSTYPE") == "Mander" && back.Number("STRAINATULTIMATE") == 0.0035, "the nonlinear values come back from the .e2k");
            var cpm = ModelFile.FromJson(ModelFile.ToJson(link.Model)).Materials.Single(x => x.Name == "C40");
            Check(ProgramTables.Row(cpm, "Damping Parameters")["ModalRatio"] == "0.03" && cpm.Tables!["User Stress-Strain Curves"].Count == 2, "the .cpm keeps the table rows");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("MATERIAL PROPERTIES 06 - DAMPING PARAMETERS") && s2k.Contains("ModalRatio=0.03"), "the .s2k has the damping");
            Check(s2k.Contains("MATERIAL PROPERTIES 05A - TIME DEPENDENCE - CEB-FIP 90") && s2k.Contains("RelHumid=70"), "the .s2k has the time dependence");
            Check(s2k.Contains("MATERIAL PROPERTIES 04 - USER STRESS-STRAIN CURVES") && s2k.Contains("Notes=\"Cores and transfer slabs\""), "the .s2k has the curve and the notes");
            Check(s2k.Contains("SSHysType=Takeda") && s2k.Contains("LtWtConc=Yes") && !s2k.Contains("UserFr"), "SAP2000's concrete row takes the nonlinear and lightweight data, and no user rupture (SAP2000 has none)");
            Check(s2k.Contains("MATERIAL PROPERTIES 09 - ACCEPTANCE CRITERIA") && s2k.Contains("IOComp=0.002"), "the .s2k has the acceptance criteria");
            var f2k = CsiProMax.F2k.F2kWriter.Write(link.Model, new CsiProMax.F2k.F2kOptions()).Text;
            Check(!f2k.Contains("MATERIAL PROPERTIES - DAMPING PARAMETERS") && f2k.Contains("UserFr=4000") && f2k.Contains("LtWtConc=Yes") && f2k.Contains("Color=Yellow"), "the .f2k has the rupture, the lightweight data and the color, in the tables SAFE has");
            var rows = ProgramTables.MaterialRows(link.Model);
            Check(rows.ContainsKey("Material Properties - Damping Parameters") && rows["Material Properties - Damping Parameters"][0]["Material"] == "C40", "ETABS gets its rows under its own table names");
            // Renamed, the rows go with it; a new value keeps the others.
            Run(link, "define_material_data", new { name = "C40B", was = "C40", type = "Concrete", values = new { CPCOMPRESSION = 0.02 } });
            var r = link.Model.Materials.Single(x => x.Name == "C40B");
            Check(r.Number("STRAINATFC") == 0.0022 && r.Number("CPCOMPRESSION") == 0.02 && r.Get("HYSTYPE") == "Takeda" && ProgramTables.Row(r, "Damping Parameters").Count == 3, "renamed: the nonlinear line and the rows are kept");
            Run(link, "define_material_data", new { name = "C40B", was = "C40B", type = "Concrete", values = new { }, tables = new Dictionary<string, object> { ["Damping Parameters"] = Array.Empty<object>() } });
            Check(ProgramTables.Row(link.Model.Materials.Single(x => x.Name == "C40B"), "Damping Parameters").Count == 0, "[] takes the damping off");
            Refused(link, "define_material_data", new { name = "C40B", was = "C40B", type = "Concrete", values = new { }, tables = new Dictionary<string, object> { ["Damping Parameters"] = new[] { new { Nonsense = "1" } } } }, "has no field 'Nonsense'");
            Refused(link, "define_material_data", new { name = "C40B", was = "C40B", type = "Concrete", values = new { }, tables = new Dictionary<string, object> { ["No Such Table"] = new[] { new { A = "1" } } } }, "no material table");
            Refused(link, "define_material_data", new { name = "C40B", was = "C40B", type = "Concrete", values = new { }, texts = new { HYSTYPE = "Wobbly" } }, "HYSTYPE is one of");
            var pd = Run(link, "property_definitions", new { });
            Check(pd["material_tables"]!["Damping Parameters"]!.AsArray().Count == 3 && pd["material_nonlinear"]!["Steel"]!["keys"]!.AsArray().Count > 5, "property_definitions lists the tables and the nonlinear keys");
            Check(pd["materials"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "C40B")!["tables"]!["General"] is not null, "property_definitions gives the material's rows");
        });
        yield return ("properties: Frame Section Property Data's other fields (colour, notes, fillet radius, notional size) are kept, renamed with the section, written to SAP2000, and Show Section Properties gives the shape's properties (Ahmed 2026-11-27)", () =>
        {
            var link = Frame();
            Run(link, "define_material_data", new { name = "S355", type = "Steel", values = new { WEIGHTPERVOLUME = 77, E = 2.1e8, U = 0.3, FY = 355000, FU = 490000 } });
            Run(link, "define_frame_shape", new { name = "IPE400", shape = "Steel I/Wide Flange", material = "S355", dims = new { D = 0.4, B = 0.18, TF = 0.0135, TW = 0.0086 }, extras = new Dictionary<string, string> { ["Color"] = "Cyan", ["Notes"] = "secondary", ["FilletRad"] = "0.021" } });
            var s = link.Model.FrameSections.Single(x => x.Name == "IPE400");
            Check(PropertyDefs.FrameExtras(s)["FilletRad"] == "0.021" && PropertyDefs.FrameExtras(s)["Color"] == "Cyan", "the section keeps its other fields");
            var cpm = ModelFile.FromJson(ModelFile.ToJson(link.Model)).FrameSections.Single(x => x.Name == "IPE400");
            Check(PropertyDefs.FrameExtras(cpm)["Notes"] == "secondary", "the .cpm keeps them");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("Color=Cyan") && s2k.Contains("FilletRadius=0.021") && s2k.Contains("Notes=secondary"), "SAP2000's frame section row takes the colour, fillet radius and notes");
            Run(link, "define_frame_shape", new { name = "IPE400B", was = "IPE400", shape = "Steel I/Wide Flange", material = "S355", dims = new { D = 0.4, B = 0.18, TF = 0.0135, TW = 0.0086 } });
            Check(PropertyDefs.FrameExtras(link.Model.FrameSections.Single(x => x.Name == "IPE400B"))["Color"] == "Cyan", "renamed without extras: they are kept");
            Refused(link, "define_frame_shape", new { name = "IPE400B", was = "IPE400B", shape = "Steel I/Wide Flange", material = "S355", dims = new { D = 0.4, B = 0.18, TF = 0.0135, TW = 0.0086 }, extras = new Dictionary<string, string> { ["NotSizeType"] = "Auto" } }, "has no field 'NotSizeType'");
            var pd = Run(link, "property_definitions", new { });
            Check(pd["frame_sections"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "IPE400B")!["extras"]!["Notes"]!.GetValue<string>() == "secondary", "property_definitions gives them to the form");
            Check(pd["frame_extra_fields"]!["Concrete Rectangular"]!.AsArray().Any(x => x!.GetValue<string>() == "NotSizeType"), "the form is told each shape's fields");
            var props = Run(link, "section_properties", new { shape = "Steel I/Wide Flange", dims = new { D = 0.4, B = 0.18, TF = 0.0135, TW = 0.0086 } });
            Check(Math.Abs(props["A"]!.GetValue<double>() - (2 * 0.18 * 0.0135 + (0.4 - 0.027) * 0.0086)) < 1e-12, "the I's area");
            var rect = Run(link, "section_properties", new { shape = "Concrete Rectangular", dims = new { D = 0.6, B = 0.3 } });
            Check(Math.Abs(rect["I33"]!.GetValue<double>() - 0.3 * 0.216 / 12) < 1e-12 && Math.Abs(rect["Z33"]!.GetValue<double>() - 0.3 * 0.36 / 4) < 1e-12, "the rectangle's I33 and Z33");
        });
        yield return ("properties: Slab, Wall and Shell Section Data's other fields (colour, notes, notional size, a wall's rigid zone, SAP2000's material angle and bending thickness) are kept, written to SAP2000 and read back (Ahmed 2026-11-27)", () =>
        {
            var link = Frame();
            var mat = link.Model.Materials.First(x => x.IsConcrete).Name;
            Run(link, "define_shell_type", new { name = "W300", kind = "wall", thickness = 0.3, material = mat, extras = new Dictionary<string, string> { ["RigidZone"] = "Yes", ["Color"] = "Magenta", ["Notes"] = "core", ["BendThick"] = "0.25" } });
            var w = link.Model.ShellSections.Single(x => x.Name == "W300");
            Check(PropertyDefs.ShellExtras(w)["RigidZone"] == "Yes" && PropertyDefs.ShellTable(w) == "Wall Property Definitions - Specified", "the wall keeps its rigid zone under its table");
            Check(PropertyDefs.ShellExtras(ModelFile.FromJson(ModelFile.ToJson(link.Model)).ShellSections.Single(x => x.Name == "W300"))["Notes"] == "core", "the .cpm keeps them");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("Color=Magenta") && s2k.Contains("BendThick=0.25") && s2k.Contains("Notes=core"), "SAP2000's area section row takes the colour, bending thickness and notes");
            var back = S2kReader.Read(s2k).Model.ShellSections.Single(x => x.Name == "W300");
            Check(PropertyDefs.ShellExtras(back)["BendThick"] == "0.25" && PropertyDefs.ShellExtras(back)["Color"] == "Magenta", "the .s2k gives them back");
            Refused(link, "define_shell_type", new { name = "S1", kind = "slab", thickness = 0.2, material = mat, extras = new Dictionary<string, string> { ["RigidZone"] = "Yes" } }, "has no field 'RigidZone'");
            var pd = Run(link, "property_definitions", new { });
            Check(pd["shell_sections"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "W300")!["extras"]!["Color"]!.GetValue<string>() == "Magenta" && pd["shell_extra_fields"]!["wall"]!.AsArray().Count == 8, "property_definitions gives them to the form");
        });
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
            // ETABS 23 has no auto table for a concrete beam's shear hinge: the two flexural ones go (0.4.55 ETABS run).
            Check(back.Count == 2 && back[0] == new PropertyDefs.HingeDef(0.05, "M3") && back[1].At == 0.95, "the hinges back, in order: " + string.Join(",", back));
            // In ETABS 23's own words, as its .$et has them: ETABS read none of AUTO "M3" RELDIST 0.05.
            var e2k = CsiProMax.E2k.E2kWriter.Write(link.Model);
            Check(e2k.Contains("HINGEDISTRTYPE \"User Defined\"") && e2k.Contains("AUTOHINGETYPE \"ASCE41-17\"  TABLEITEM \"Concrete Beams\"  DOF \"M3\"  STRLIMRATIO 1.1 LOCATIONTYPE \"RelDist\"  RDISTANCE 0.05")
                && e2k.Contains("HYSTYPE \"Isotropic\"") && !e2k.Contains("AUTO  \"") && !e2k.Contains("RELDIST"), "the .e2k's hinges as ETABS writes them");
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

        yield return ("properties: a member's and a shell's own modifiers go in the .e2k as ETABS 23 writes them, PROPMODI33 (0.4.55: ETABS took none)", () =>
        {
            var link = Frame();
            var beam = Beam(link.Model); var at = beam.LastIndexOf('@');
            Run(link, "assign_frame_data", new { objects = new[] { beam }, modifiers = new { I3MOD = 0.35, JMOD = 0.01 } });
            var e2k = CsiProMax.E2k.E2kWriter.Write(link.Model);
            var line = string.Join(" | ", e2k.Split("\r\n").Where(l => l.StartsWith("  LINEASSIGN  " + CsiProMax.E2k.E2kText.Quote(beam[..at]) + "  " + CsiProMax.E2k.E2kText.Quote(beam[(at + 1)..]))));
            line = System.Text.RegularExpressions.Regex.Replace(line, " +", " ");
            Check(line.Contains("PROPMODT 0.01") && line.Contains("PROPMODI33 0.35") && !line.Contains("I3MOD") && !line.Contains("PROPMODA "), "ETABS's names, only those not 1: " + line);
            var back = ViaE2k(link.Model).LineAssigns.Single(a => a.Label == beam[..at] && a.Story == beam[(at + 1)..]);
            var mods = PropertyDefs.Modifiers(back.Extra, PropertyDefs.FrameModifiers);
            Check(mods["I3MOD"] == 0.35 && mods["JMOD"] == 0.01 && mods["AMOD"] == 1, "read back as the model's own names");
            var shell = new CsiProMax.Model.AreaAssign { Label = "F9", Story = "Story1" };
            PropertyDefs.SetModifiers(shell.Extra, PropertyDefs.ShellModifiers, new Dictionary<string, double> { ["M11MOD"] = 0.25, ["WMOD"] = 0.5 });
            var shellLines = CsiProMax.E2k.EtabsModifiers.ToEtabs(shell.Extra, CsiProMax.E2k.EtabsModifiers.Area);
            Check(shellLines.Single().SequenceEqual(new[] { "PROPMODM11", "0.25", "PROPMODW", "0.5" }), "the shell's: " + string.Join(" ", shellLines.SelectMany(x => x)));
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

        yield return ("properties: a concrete tee or L goes to SAP2000 as its General shape with its own properties (0.4.54: SAP2000 skipped Shape=\"Concrete Tee\")", () =>
        {
            var link = Frame();
            Run(link, "define_material_data", new { name = "C30", type = "Concrete", values = new { E = 3.3e7, FC = 30000, WEIGHTPERVOLUME = 25 } });
            foreach (var shape in new[] { "Concrete Tee", "Concrete L" })
                Run(link, "define_frame_shape", new { name = "X-" + shape, shape, material = "C30", dims = new { D = 0.6, B = 0.9, TF = 0.2, TW = 0.3 } });
            var s2k = S2kWriter.Write(link.Model, "a.s2k");
            var sections = CsiProMax.CsiTables.CsiTableText.Parse(s2k.Text)["FRAME SECTION PROPERTIES 01 - GENERAL"];
            foreach (var shape in new[] { "Concrete Tee", "Concrete L" })
            {
                var r = sections.SingleOrDefault(x => x["SectionName"] == "X-" + shape);
                double V(string k) => double.Parse(r![k], System.Globalization.CultureInfo.InvariantCulture);
                Check(r is not null && r["Shape"] == "General", shape + " is written as General, not as a shape SAP2000 skips");
                Check(Math.Abs(V("Area") - (0.9 * 0.2 + 0.3 * 0.4)) < 1e-9 && V("I33") > 0 && V("S33Top") > V("S33Bot"), shape + "'s area and moduli are its own (flange on top)");
                Check(V("t3") == 0.6 && V("t2") == 0.9, shape + "'s depth and width");
            }
            Check(sections.All(r => r["Shape"] is not ("Concrete Tee" or "Concrete L")), "no Concrete Tee or Concrete L shape in the table");
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

        yield return ("properties: Deck Property Data (filled, unfilled, solid slab) is written as ETABS writes $ DECK PROPERTIES, read back, kept apart from the slabs, and refusals leave the model as it was (Ahmed 2026-11-27, every form)", () =>
        {
            var link = Frame();
            var mat = link.Model.Materials[0].Name;
            Run(link, "define_deck", new { name = "Deck1", deck_type = "Filled", slab_material = mat, deck_material = mat, slab_depth = 0.0875, rib_depth = 0.075, rib_width_top = 0.175, rib_width_bottom = 0.125, rib_spacing = 0.3,
                shear_thickness = 0.001, unit_weight = 0.11, stud_diameter = 0.019, stud_height = 0.15, stud_fu = 400000, modifiers = new { M11MOD = 0.5 }, extras = new { Color = "#bfa0ff", Notes = "composite floor" } });
            var e2k = E2kWriter.Write(link.Model);
            Check(e2k.Contains("$ DECK PROPERTIES") && e2k.Contains("PROPTYPE  \"Deck\"") && e2k.Contains("DECKRIBSPACING  0.3") && e2k.Contains("SHEARSTUDFU  400000"), "the deck line as ETABS writes it");
            var back = ViaE2k(link.Model).ShellSections.Single(x => x.Name == "Deck1");
            Check(back.IsDeck && back.Number("DECKSLABDEPTH") == 0.0875 && back.Get("CONCMATERIAL") == mat && PropertyDefs.Modifiers(back.Lines, PropertyDefs.ShellModifiers)["M11MOD"] == 0.5, "the deck reads back");
            Check(PropertyDefs.ShellExtras(link.Model.ShellSections.Single(x => x.Name == "Deck1"))["Notes"] == "composite floor" && PropertyDefs.ShellTable(back) == PropertyDefs.DeckTable, "colour and notes in Deck Property Definitions");
            var pd = Run(link, "property_definitions", new { })["shell_sections"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Deck1")!;
            Check(pd["deck"]!["deck_type"]!.GetValue<string>() == "Filled" && pd["deck"]!["rib_depth"]!.GetValue<double>() == 0.075, "the form sees the deck");
            Check(Run(link, "model_definitions", new { })["shell_sections"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Deck1")!["kind"]!.GetValue<string>() == "deck", "a deck is not a slab in the lists");
            Run(link, "define_deck", new { name = "Solid", deck_type = "Solid Slab", slab_material = mat, slab_depth = 0.15, stud_diameter = 0.019, stud_height = 0.1, stud_fu = 400000 });
            var solid = link.Model.ShellSections.Single(x => x.Name == "Solid");
            Check(solid.Get("DECKTYPE") == "SolidSlab" && solid.Get("DECKMATERIAL") is null && solid.Thickness == 0.15, "a solid slab has no deck");
            Run(link, "define_deck", new { name = "Bare", deck_type = "Unfilled", deck_material = mat, rib_depth = 0.075, rib_width_top = 0.175, rib_width_bottom = 0.125, rib_spacing = 0.3, shear_thickness = 0.001, unit_weight = 0.11 });
            Check(link.Model.ShellSections.Single(x => x.Name == "Bare").Get("CONCMATERIAL") is null, "an unfilled deck has no concrete");
            Run(link, "define_deck", new { name = "Deck2", was = "Deck1", deck_type = "Filled", slab_material = mat, deck_material = mat, slab_depth = 0.1, rib_depth = 0.075, rib_width_top = 0.175, rib_width_bottom = 0.125, rib_spacing = 0.3, shear_thickness = 0.001 });
            Check(link.Model.ShellSections.Exists(x => x.Name == "Deck2") && !link.Model.ShellSections.Exists(x => x.Name == "Deck1"), "a rename");
            Refused(link, "define_deck", new { name = "X", deck_type = "Filled", slab_material = "nope", deck_material = mat, slab_depth = 0.1, rib_depth = 0.075, rib_width_top = 0.175, rib_width_bottom = 0.125, rib_spacing = 0.3, shear_thickness = 0.001 }, "no material");
            Refused(link, "define_deck", new { name = "X", deck_type = "Filled", slab_material = mat, deck_material = mat, slab_depth = 0.1, rib_depth = 0.075, rib_width_top = 0.175, rib_width_bottom = 0.125, rib_spacing = 0.1, shear_thickness = 0.001 }, "rib spacing");
            Refused(link, "define_deck", new { name = "X", deck_type = "Corrugated" }, "deck type");
            Refused(link, "define_deck", new { name = "X", deck_type = "Unfilled", deck_material = mat, rib_depth = 0.075, rib_width_top = 0.175, rib_width_bottom = 0.125, rib_spacing = 0.3, shear_thickness = 0.001, extras = new { Thickness = "1" } }, "no field");
            var slab = link.Model.ShellSections.First(x => !x.IsDeck && !x.IsWall).Name;
            Refused(link, "define_deck", new { name = slab, deck_type = "Solid Slab", slab_material = mat, slab_depth = 0.15 }, "not a deck");
        });

        yield return ("properties: Spring and Link/Support Property Data's other fields (nonlinear options, colour, notes; mass, weight, inertia, line and area factors) are kept by the program's tables, and a link's row follows its type (Ahmed 2026-11-27, every field)", () =>
        {
            var link = Frame();
            Run(link, "define_spring", new { kind = "area", name = "AS1", values = new { U3 = 20000 }, extras = new Dictionary<string, string> { ["NonlinOpt3"] = "Compression Only", ["Color"] = "#ff0000", ["Notes"] = "soil" } });
            var sp = StructureExtras.Get(link.Model, "Spring Property Definitions - Area Springs", "AS1");
            Check(sp["NonlinOpt3"] == "Compression Only" && sp["Notes"] == "soil", "the area spring's row: " + string.Join(",", sp));
            Refused(link, "define_spring", new { kind = "area", name = "AS2", values = new { U3 = 1 }, extras = new Dictionary<string, string> { ["NonlinOpt2"] = "Tension Only" } }, "has no field 'NonlinOpt2'");
            Check(!PropertyDefs.Springs(link.Model).Exists(x => x.Name == "AS2"), "a refused spring is not made");
            Run(link, "define_link", new { name = "L1", type = "GAP", dofs = new { U1 = new { stiffness = 1e6, damping = 0 } }, extras = new Dictionary<string, string> { ["Mass"] = "0.5", ["Notes"] = "bearing" } });
            Check(StructureExtras.Get(link.Model, "Link Property Definitions - Gap", "L1")["Mass"] == "0.5", "the gap link's row");
            Check(StructureExtras.PerDirection("Link Property Definitions - Gap") && !StructureExtras.PerDirection("Link Property Definitions - Linear"), "a nonlinear link's rows go in by direction");
            Run(link, "define_link", new { name = "L1", type = "LINEAR", dofs = new { U1 = new { stiffness = 1e6, damping = 0 } } });
            Check(StructureExtras.Get(link.Model, "Link Property Definitions - Linear", "L1")["Notes"] == "bearing" && StructureExtras.Get(link.Model, "Link Property Definitions - Gap", "L1").Count == 0, "the row moves with the type");
            var pd = Run(link, "property_definitions", new { });
            Check(pd["links"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "L1")!["extras"]!["Mass"]?.GetValue<string>() == "0.5" && pd["springs"]!.AsArray().Any(x => x!["name"]!.GetValue<string>() == "AS1" && x["extras"]!["Color"]?.GetValue<string>() == "#ff0000"), "the forms see them: " + pd["links"]!.ToJsonString() + pd["springs"]!.ToJsonString());
            Run(link, "define_link", new { name = "L1", delete = true });
            Check(StructureExtras.Get(link.Model, "Link Property Definitions - Linear", "L1").Count == 0, "a deleted link leaves no row");
        });
    }
}
