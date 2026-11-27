using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>
/// Define > Reinforcing Bar Sizes, Frame Section > Modify/Show Rebar and the definitions kept as rows of the program's
/// own tables (DefineTables, DefineTools), and the forms' lists following the model's program (ProgramOptions, Ahmed
/// 2026-11-26: modelling for SAP2000 shows only SAP2000's options, for SAFE only SAFE's).
/// </summary>
static class DefineTablesTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Def", [6, 5], [4], 2, 3.2));

    static JsonObject Run(OfflineEtabs link, string tool, object args) =>
        (JsonObject)DefineTools.All.Concat(PropertyTools.All).Concat(HandTools.All).Concat(CaseTools.All).Single(x => x.Name == tool)
            .Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

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

    static StructuralModel ThroughE2k(StructuralModel m) => E2kReader.Read(E2kWriter.Write(m));

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("define: ETABS's Load Patterns forms keep a code's rows once, set AutoLoad, follow a rename, drop with the pattern, and pass through the .cpm", () =>
        {
            var link = Frame();
            var m = link.Model;
            EtabsTools.All.Single(x => x.Name == "add_load_pattern").Invoke(JsonDocument.Parse("""{"name":"EQX","type":"Seismic","self_weight":0}""").RootElement, link);
            const string asce16 = "Load Pattern Definitions - Auto Seismic - ASCE 7-16", user = "Load Pattern Definitions - Auto Seismic - User Coefficient";
            Run(link, "define_table_rows", new { table = asce16, name = "EQX", rows = new[] { new Dictionary<string, object> { ["XDir"] = "Yes", ["EccRatio"] = "0.05", ["R"] = "7" } } });
            Check(EtabsLoadTables.AutoLoads(m).GetValueOrDefault("EQX") == "ASCE 7-16", "EQX's AutoLoad is ASCE 7-16: " + string.Join(", ", EtabsLoadTables.AutoLoads(m)));
            Run(link, "define_table_rows", new { table = user, name = "EQX", rows = new[] { new Dictionary<string, object> { ["C"] = "0.06", ["K"] = "1" } } });
            Check(m.DefinitionTables?.ContainsKey(asce16) != true, "the other code's rows went when User Coefficient was set");
            Check(EtabsLoadTables.AutoLoads(m)["EQX"] == "User Coefficient", "EQX's AutoLoad is User Coefficient now");
            Refused(link, "define_table_rows", new { table = user, name = "EQX", rows = new[] { new Dictionary<string, object> { ["NoSuchField"] = "1" } } }, "NoSuchField");
            Run(link, "define_table_rows", new { table = "Load Pattern Definitions - Auto Notional Loads", name = "EQX", rows = new[] { new Dictionary<string, object> { ["BasePattern"] = "Dead", ["LoadRatio"] = "0.002", ["LoadDir"] = "Global X" } } });
            var back = ModelFile.FromJson(ModelFile.ToJson(m));
            Check(back.DefinitionTables?[user].Single()["C"] == "0.06", "the code's row is in the .cpm");
            EtabsLoadTables.Rename(m, "pattern", "EQX", "EQX2");
            Check(m.DefinitionTables![user].Single()["Name"] == "EQX2" && m.DefinitionTables["Load Pattern Definitions - Auto Notional Loads"].Single()["LoadPattern"] == "EQX2", "the rows follow the rename");
            EtabsLoadTables.Drop(m, "pattern", "EQX2");
            Check(m.DefinitionTables?.Keys.Any(EtabsLoadTables.Has) != true, "the rows went with the pattern");
            Check(ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, user) && !ProgramOptions.HasDefinitionTable(ProgramOptions.Sap2000, user), "ETABS's tables go to ETABS only");
            Check(EtabsLoadTables.FieldDim(user, "OverEcc") == ModelUnits.Dim.Len && EtabsLoadTables.FieldDim(user, "C") == ModelUnits.Dim.None && EtabsLoadTables.FieldDim("Load Pattern Definitions - Auto Seismic - UBC 97", "Z") == ModelUnits.Dim.None, "a length converts with the units, a coefficient does not");
        });

        yield return ("define: rebar sizes are added, changed and deleted in the .e2k's REBARDEFINITION lines and come back from the file", () =>
        {
            var link = Frame();
            Run(link, "define_rebar_size", new { name = "T16", area = 0.000201, diameter = 0.016 });
            Run(link, "define_rebar_size", new { name = "T20", area = 0.000314, diameter = 0.02 });
            Run(link, "define_rebar_size", new { name = "T16", area = 0.0002, diameter = 0.016 });
            var back = DefineTables.RebarSizes(ThroughE2k(link.Model));
            Check(back.Count(x => x.Name == "T16") == 1 && Math.Abs(back.Single(x => x.Name == "T16").Area - 0.0002) < 1e-9, "T16 changed, once: " + string.Join(", ", back));
            Check(back.Exists(x => x.Name == "T20" && Math.Abs(x.Diameter - 0.02) < 1e-9), "T20 came back");
            Run(link, "define_rebar_size", new { name = "T20", delete = true });
            Check(!DefineTables.RebarSizes(link.Model).Exists(x => x.Name == "T20"), "T20 deleted");
            Refused(link, "define_rebar_size", new { name = "T25", area = 0.0005 }, "diameter");
            Refused(link, "define_rebar_size", new { name = "T99", delete = true }, "no bar size");
            var data = Run(link, "definition_data", new { });
            Check(data["rebar_sizes"]!.AsArray().Any(x => x!["name"]!.GetValue<string>() == "T16"), "definition_data lists T16");
        });

        yield return ("define: Modify/Show Rebar changes a column's and a beam's rebar, keeps it through the .e2k, and refuses a steel section or a wrong pattern", () =>
        {
            var link = Frame();
            var m = link.Model;
            Run(link, "define_frame_rebar", new { section = "C500x500", values = new Dictionary<string, object> { ["PATTERN"] = "R-4-4", ["COVER"] = 0.05, ["DESIGNCHECK"] = "CHECK" } });
            Run(link, "define_frame_rebar", new { section = "B300x600", values = new Dictionary<string, object> { ["ATI"] = 0.0012, ["ABJ"] = 0.0009 } });
            var back = ThroughE2k(m);
            var c = DefineTables.Rebar(back, "C500x500")!;
            Check(c["TYPE"] == "Column" && c["PATTERN"] == "R-4-4" && c["COVER"] == "0.05" && c["DESIGNCHECK"] == "CHECK", "the column's rebar: " + string.Join(" ", c));
            Check(c.ContainsKey("LONGBARMATERIAL") && c.ContainsKey("CONFINEBARSPACING"), "what was not given is kept");
            var b = DefineTables.Rebar(back, "B300x600")!;
            Check(b["TYPE"] == "Beam" && b["ATI"] == "0.0012" && b["ABJ"] == "0.0009", "the beam's rebar: " + string.Join(" ", b));
            var line = m.Passthrough.Single(x => x.Heading == "CONCRETE SECTIONS").Lines.Single(l => l.Contains("\"B300x600\""));
            Check(line.IndexOf("LONGBARMATERIAL", StringComparison.Ordinal) < line.IndexOf("TYPE", StringComparison.Ordinal), "ETABS's order, bar materials first: " + line);
            Refused(link, "define_frame_rebar", new { section = "C500x500", values = new Dictionary<string, object> { ["PATTERN"] = "4 bars" } }, "PATTERN");
            Refused(link, "define_frame_rebar", new { section = "B300x600", values = new Dictionary<string, object> { ["PATTERN"] = "R-3-3" } }, "beam's rebar has no");
            Refused(link, "define_frame_rebar", new { section = "C500x500", values = new Dictionary<string, object> { ["LONGBARMATERIAL"] = "NOPE" } }, "no material");
            PropertyDefs.SetMaterial(m, "S355", null, "Steel", null, new Dictionary<string, double>());
            m.FrameSections.Add(new FrameSection { Name = "HEB300", Lines = [["MATERIAL", "\"S355\"", "SHAPE", "\"Steel I/Wide Flange\"", "D", "0.3", "B", "0.3", "TF", "0.019", "TW", "0.011"]] });
            Refused(link, "define_frame_rebar", new { section = "HEB300", values = new Dictionary<string, object> { ["COVER"] = 0.04 } }, "not a concrete section");
        });

        yield return ("define: a hinge, a generalized displacement and slab layers are kept as table rows by name, in the .cpm, and a wrong field or table is refused", () =>
        {
            var link = Frame();
            Run(link, "define_table_rows", new { table = "Generalized Displacement Definitions", name = "GD1", rows = new[] { new Dictionary<string, object> { ["Type"] = "Translational", ["PointName"] = "1", ["TransU1SF"] = 1 } } });
            Run(link, "define_table_rows", new { table = "hinge definitions - moment m3", name = "H1", rows = new[] { new Dictionary<string, object> { ["IsSymmetric"] = true }, new Dictionary<string, object> { ["IsSymmetric"] = true } } });
            var m = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            Check(m.DefinitionTables!["Hinge Definitions - Moment M3"].Count(r => r["Name"] == "H1" && r["IsSymmetric"] == "Yes") == 2, "two hinge rows, by the table's own name, through the .cpm");
            Check(m.DefinitionTables["Generalized Displacement Definitions"].Single()["TransU1SF"] == "1", "the generalized displacement");
            Run(link, "define_table_rows", new { table = "Hinge Definitions - Moment M3", name = "H1", rows = Array.Empty<object>() });
            Check(!link.Model.DefinitionTables!.ContainsKey("Hinge Definitions - Moment M3"), "rows [] deletes the hinge");
            Refused(link, "define_table_rows", new { table = "Generalized Displacement Definitions", name = "GD2", rows = new[] { new Dictionary<string, object> { ["Nope"] = 1 } } }, "no field");
            Refused(link, "define_table_rows", new { table = "Steel Joists", name = "J", rows = new[] { new Dictionary<string, object> { ["Name"] = "J" } } }, "no definition table");
            var data = Run(link, "definition_data", new { });
            Check(data["tables"]!["Generalized Displacement Definitions"]!["rows"]!.AsArray().Count == 1, "definition_data shows the rows");
        });

        yield return ("define: ETABS 23.2's spring forms keep every option: Elasto-plastic, single joint links at a point (a row each in ETABS's table), springs from a soil profile and a column footing, soil profiles with their layers, and a row given by the names ETABS shows goes in under its field keys (Ahmed 2026-11-27)", () =>
        {
            var link = Frame();
            Run(link, "define_link", new { name = "Link1", type = "LINEAR", dofs = new Dictionary<string, object> { ["U1"] = new { stiffness = 1000, damping = 0 } } });
            Run(link, "define_spring", new { kind = "point", name = "PSprEP", values = new { UZ = 1 }, extras = new Dictionary<string, string> { ["Stiffness Option"] = "User", ["Nonlinearity Specification"] = "Quick", ["NonlinOpt"] = "Elasto-Plastic", ["Compression Stiffness"] = "100", ["Compression Strength"] = "50", ["Tension Stiffness"] = "20", ["Tension Strength"] = "5", ["Color"] = "Gray8Dark" } });
            Run(link, "define_spring", new { kind = "point", name = "PSprLink", values = new { UZ = 0 }, extras = new Dictionary<string, string> { ["Stiffness Option"] = "User", ["Nonlinearity Specification"] = "From Links", ["Link Property"] = "Link1|Link1", ["Link Axial Direction"] = "+Z|-X", ["Link Axis 2 Angle"] = "0|30" } });
            Run(link, "define_table_rows", new { table = "Spring Property Definitions - Soil Profiles", name = "SoilPr1", rows = new[] {
                new Dictionary<string, object> { ["Soil Layer Name"] = "Layer1", ["Elevation of Top of Layer"] = 0, ["Unit Weight"] = 18, ["Shear Modulus"] = 300000, ["Poissons Ratio"] = 0.3, ["Cohesion"] = 20, ["Friction Angle"] = 30, ["Shear Wave Velocity"] = 300, ["Layer Color"] = "Blue", ["Shear Modulus Reduction Factor"] = 1, ["Hysteretic Damping Ratio"] = 0.5 },
                new Dictionary<string, object> { ["Soil Layer Name"] = "Layer2", ["Elevation of Top of Layer"] = -1.5, ["Unit Weight"] = 19, ["Shear Modulus"] = 300000, ["Poissons Ratio"] = 0.3, ["Cohesion"] = 20, ["Friction Angle"] = 30, ["Shear Wave Velocity"] = 300, ["Layer Color"] = "DarkGreen" } } });
            Run(link, "define_table_rows", new { table = "Spring Property Definitions - Isolated Column Footings", name = "ColFoot1", rows = new[] { new Dictionary<string, object> { ["Length"] = 1.5, ["Width"] = 0.9, ["Thickness"] = 0.45, ["Embedment Source"] = "User Defined", ["User Embedment"] = 1.2, ["Color"] = "Blue" } } });
            Run(link, "define_spring", new { kind = "point", name = "PSprSoil", values = new { }, extras = new Dictionary<string, string> { ["Stiffness Option"] = "Soil Profile", ["Soil Profile"] = "SoilPr1", ["Isolated Column Footing"] = "ColFoot1", ["First Period"] = "0.1" } });
            Run(link, "define_spring", new { kind = "area", name = "ASprL", values = new { }, extras = new Dictionary<string, string> { ["Stiffness Option"] = "Link", ["Link Property"] = "Link1" } });
            Refused(link, "define_spring", new { kind = "point", name = "PSprNone", values = new { UZ = 0 } }, "above 0");
            var m = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            var e2k = E2kWriter.Write(link.Model);
            Check(e2k.Contains("\"PSprSoil\"") && e2k.Contains("\"PSprLink\"") && e2k.Contains("\"ASprL\""), "the springs from a soil profile or links are in the .e2k too (their stiffness comes from those)");
            var point = m.DefinitionTables!["Spring Property Definitions - Point Springs"];
            Check(point.Single(r => r["Name"] == "PSprEP") is { } ep && ep["NonlinOpt"] == "Elasto-Plastic" && ep["Tension Strength"] == "5" && ep["Color"] == "Gray8Dark", "the elasto-plastic spring through the .cpm");
            Check(point.Single(r => r["Name"] == "PSprSoil")["Isolated Column Footing"] == "ColFoot1", "the soil spring's footing");
            Check(m.DefinitionTables["Spring Property Definitions - Soil Profiles"].Count(r => r["Name"] == "SoilPr1") == 2, "the soil profile's two layers");
            var rows = ModelerTables.SpringLinkRows(new Dictionary<string, string>(point.Single(r => r["Name"] == "PSprLink"))).ToList();
            Check(rows.Count == 2 && rows[1]["Link Axial Direction"] == "-X" && rows[1]["Link Axis 2 Angle"] == "30" && rows[0]["Link Property"] == "Link1", "a row per single joint link: " + string.Join(" / ", rows.Select(r => string.Join(",", r))));
            Check(ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, DefineTables.SoilProfiles) && ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, DefineTables.ColumnFootings), "the soil profiles and footings go into ETABS");
            var keyed = EditableTables.ByKey([new Dictionary<string, string> { ["Name"] = "P", ["Trans NL Stiffness"] = "1000", ["Notes"] = "x" }], ["Name", "TransK", "Notes"], ["Name", "Trans NL Stiffness", "Notes"]);
            Check(keyed[0].ContainsKey("TransK") && keyed[0]["TransK"] == "1000" && keyed[0].ContainsKey("Name") && !keyed[0].ContainsKey("Trans NL Stiffness"), "a field given by the name ETABS shows goes in under its key");
        });

        yield return ("define: ETABS 23.2's Panel Zone Data keeps each source (auto elastic, spring, ASCE 41-17, user nonlinear with its positive points, link with its own axis) and the user acceptance criteria, in ETABS's two panel zone tables (Ahmed 2026-11-27)", () =>
        {
            var link = Frame();
            Run(link, "define_table_rows", new { table = "Panel Zone Property Definitions", name = "PZone2", rows = new[] { new Dictionary<string, object> { ["Source"] = "Spring", ["ActiveDir"] = "Both", ["OffsetRigid"] = "Yes", ["SpringMajor"] = 1000000, ["SpringMinor"] = 500000, ["AcceptType"] = "None" } } });
            Run(link, "define_table_rows", new { table = "Panel Zone Property Definitions", name = "PZone4", rows = new[] { new Dictionary<string, object> { ["Source"] = "User", ["ActiveDir"] = "Major", ["OffsetRigid"] = "Yes", ["AcceptType"] = "User", ["UserAccType"] = "Moment", ["UserIOMomMa"] = 0.0001, ["UserLSMomMa"] = 0.0001, ["UserCPMomMa"] = 0.0001 } } });
            Run(link, "define_table_rows", new { table = "Panel Zone Property Definitions - User Nonlinear Data", name = "PZone4", rows = new object[] {
                new Dictionary<string, object> { ["Direction"] = "Major", ["Type"] = "Nonlinear", ["AddBC"] = "Yes", ["AddCD"] = "No", ["Point"] = "B", ["Force"] = 1, ["Displ"] = 1, ["UseYldForce"] = "No", ["UseYldDispl"] = "No", ["FDPosForSF"] = 1000, ["FDPosDispSF"] = 0.002, ["BeyondE"] = "Extrapolated", ["HysType"] = "Pivot", ["PivotAlpha1"] = 10 },
                new Dictionary<string, object> { ["Direction"] = "Major", ["Point"] = "BC", ["Force"] = 1, ["Displ"] = 7 }, new Dictionary<string, object> { ["Direction"] = "Major", ["Point"] = "C", ["Force"] = 1, ["Displ"] = 13 },
                new Dictionary<string, object> { ["Direction"] = "Major", ["Point"] = "D", ["Force"] = 1, ["Displ"] = 13 }, new Dictionary<string, object> { ["Direction"] = "Major", ["Point"] = "E", ["Force"] = 1, ["Displ"] = 13 } } });
            Run(link, "define_table_rows", new { table = "Panel Zone Property Definitions", name = "PZone5", rows = new[] { new Dictionary<string, object> { ["Source"] = "Link", ["ActiveDir"] = "Major", ["OffsetRigid"] = "Yes", ["LinkProp"] = "Link1", ["Connect"] = "Beam-Column", ["AxisSource"] = "User Angle", ["AxialDir"] = "+Z", ["Axis2Angle"] = 0, ["AcceptType"] = "None" } } });
            var m = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            var nl = m.DefinitionTables!["Panel Zone Property Definitions - User Nonlinear Data"].Where(r => r["Name"] == "PZone4").ToList();
            Check(nl.Count == 5 && nl[1]["Point"] == "BC" && nl[1]["Displ"] == "7" && nl[0]["HysType"] == "Pivot" && nl[0]["PivotAlpha1"] == "10", "the user nonlinear points through the .cpm");
            var pz = m.DefinitionTables["Panel Zone Property Definitions"];
            Check(pz.Single(r => r["Name"] == "PZone5")["AxisSource"] == "User Angle" && pz.Single(r => r["Name"] == "PZone2")["SpringMinor"] == "500000" && pz.Single(r => r["Name"] == "PZone4")["UserCPMomMa"] == "0.0001", "each source's own fields");
            Check(ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, "Panel Zone Property Definitions - User Nonlinear Data"), "they go into ETABS");
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "dialog(\"Panel Zone Properties\"", "<legend>Defined Panel Zones</legend>", "Auto Nonlinear Properties based on ASCE 41-17 Table 9-7.2 (Steel Only)", "dialog(\"Nonlinear Panel Zone Properties\"", "dialog(\"User Nonlinear Panel Zone Data - \" + name",
                "dialog(\"User-Defined Panel Zone Acceptance Criteria\"", "Use Acceptance Criteria from Link Property", "fs(\"Single Joint Links at Point\"", "dialog(\"Soil Profile Data\"", "dialog(\"Isolated Column Footing Data\"", "fs(\"Choose Soil Profile and Isolated Footing\"", "dialog(\"Inelastic Properties for a Buckling Restrained Brace\"", "Program default inelastic properties, not editable by user", "pr(\"Force Displacement Type\"", "[\"Trilinear\", \"Elastic Perfectly Plastic\"]", "Accumulated Deformation Ratio", "o.TensFy = o.TensFu0" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });

        yield return ("define: ETABS 23.2's composite wall (Composite Shear Wall Data, Wall / Pier or Beam / Spandrel) goes on the wall's own SHELLPROP lines as ETABS writes them, and comes back", () =>
        {
            var link = Frame();
            var conc = link.Model.Materials.First().Name;
            Run(link, "define_shell_type", new { name = "Wall2", kind = "wall", modeling = "Composite", material = conc, thickness = 0.3, composite = new { concrete = conc, steel = conc, length = 3.81, total_thick = 0.3, plate_thick = 0.019, flange_thick = 0.0127, tie_diameter = 0.0159, tie_spacing = 0.3, in_plane = 0.7, out_of_plane = 1, spandrel = true } });
            var e2k = E2kWriter.Write(link.Model);
            Check(System.Text.RegularExpressions.Regex.IsMatch(e2k, "SHELLPROP\\s+\"Wall2\"\\s+PROPTYPE\\s+\"Wall\"\\s+MATERIAL\\s+\"[^\"]+\"\\s+MODELINGTYPE\\s+\"Composite\"\\s+WALLTHICKNESS"), "the first line as ETABS writes it: " + e2k.Split('\n').FirstOrDefault(l => l.Contains("Wall2")));
            Check(System.Text.RegularExpressions.Regex.IsMatch(e2k, "SHELLPROP\\s+\"Wall2\"\\s+CONCMATERIALCOMP .*STEELMATERIALCOMP .*WALLLENGTH .*TOTALTHICK .*PLATETHICK .*FLANGEPLATETHICK .*TIEBARDIAMETER .*TIEBARSPACING .*CSTIFFNESSMODIFIER1 .*CSTIFFNESSMODIFIER2 .*ISPANDREL\\s+\"Yes\""), "the composite line as ETABS writes it");
            var back = ThroughE2k(link.Model).ShellSections.Single(s => s.Name == "Wall2");
            Check(back.Get("MODELINGTYPE") == "Composite" && back.Number("PLATETHICK") is { } p && Math.Abs(p - 0.019) < 1e-9 && back.Number("CSTIFFNESSMODIFIER1") is { } c1 && Math.Abs(c1 - 0.7) < 1e-9 && back.Get("ISPANDREL") == "Yes", "the composite data comes back");
            var pd = Run(link, "property_definitions", new { });
            var w = pd["shell_sections"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "Wall2")!;
            Check(w["composite"]!["spandrel"]!.GetValue<bool>() && Math.Abs(w["composite"]!["tie_spacing"]!.GetValue<double>() - 0.3) < 1e-9, "the form reads it back");
            link.Model.Target = "sap2000";
            Refused(link, "define_shell_type", new { name = "W3", kind = "wall", modeling = "Composite", material = conc, thickness = 0.3, composite = new { concrete = conc, steel = conc, total_thick = 0.3, plate_thick = 0.01, flange_thick = 0.01, tie_diameter = 0.01, tie_spacing = 0.3 } }, "Only an ETABS wall");
            link.Model.Target = "etabs";
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "Modify/Show Composite Wall Data...", "\"In-Plane Stiffness\"", "\"Out-of-Plane Stiffness\"", "modeling: \"Composite\"" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });

        yield return ("define: a Section Designer section keeps its shapes as ETABS 23.2's SDSECTION lines, goes to ETABS as SHAPE \"SD Section\", and an imported one comes back unchanged", () =>
        {
            var link = Frame();
            var conc = link.Model.Materials.First(x => x.IsConcrete).Name;
            var bar = (link.Model.Materials.FirstOrDefault(x => x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase)) ?? link.Model.Materials.First()).Name;
            string Q(string s) => "\"" + s + "\"";
            var sd = new List<string> { "TYPE \"FRAME\" DESIGNTYPE \"CONCRETE\" DESIGNCHECK \"CHECK\" NUMSHAPES 1 ANGLE 90",
                $"SHAPE 1 MATERIAL {Q(conc)} REBARMATERIAL {Q(bar)} SHAPETYPE \"CONCRETE RECTANGULAR\" D 0.6 B 0.6 XC 0 YC 0" };
            for (var i = 1; i <= 4; i++) sd.Add($"SHAPE 1 EDGE {i} EDGEBARSIZE \"#8\" EDGEBARSPACING 0.15 EDGEBARCOVER 0.05");
            for (var i = 1; i <= 4; i++) sd.Add($"SHAPE 1 CORNER {i} CORNERBARSIZE \"#8\"");
            sd.Add("SHAPE 1 TIEBARSIZE \"#4\"");
            Run(link, "define_frame_shape", new { name = "SD1", shape = "SD Section", material = conc, extras = new Dictionary<string, string> { ["DesignType"] = "Concrete Column" }, sd_lines = sd });
            var e2k = E2kWriter.Write(link.Model);
            Check(System.Text.RegularExpressions.Regex.IsMatch(e2k, "FRAMESECTION\\s+\"SD1\"\\s+MATERIAL\\s+\"[^\"]+\"\\s+SHAPE\\s+\"SD Section\""), "the frame section line as ETABS writes it");
            int fs = e2k.IndexOf("$ FRAME SECTIONS"), sds = e2k.IndexOf("$ SECTION DESIGNER SECTIONS");
            Check(fs >= 0 && sds > fs, "the Section Designer lines follow the frame sections");
            Check(System.Text.RegularExpressions.Regex.Matches(e2k, "SDSECTION \"SD1\"").Count == 11, "all 11 lines: " + System.Text.RegularExpressions.Regex.Matches(e2k, "SDSECTION \"SD1\"").Count);
            var back = ThroughE2k(link.Model);
            Check(back.FrameSections.Single(s => s.Name == "SD1").Shape == "SD Section" && SectionDesigner.Lines(back, "SD1").Count == 11, "it reads back");
            var again = E2kWriter.Write(back);
            string Sd(string text) => string.Join("\n", text.Split('\n').Where(l => l.Contains("SDSECTION") || l.Contains("\"SD1\"")).Select(l => l.Trim()));
            Check(Sd(again) == Sd(e2k), "an imported Section Designer section goes back unchanged");
            var pd = Run(link, "property_definitions", new { });
            Check(pd["frame_sections"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "SD1")!["sd_lines"]!.AsArray().Count == 11, "the form gets its lines");
            Run(link, "define_frame_shape", new { name = "SD2", was = "SD1", shape = "SD Section", material = conc });
            Check(SectionDesigner.Lines(link.Model, "SD2").Count == 11 && SectionDesigner.Lines(link.Model, "SD1").Count == 0, "a renamed section keeps its shapes");
            Run(link, "delete_definition", new { kind = "frame_section", name = "SD2" });
            Check(!E2kWriter.Write(link.Model).Contains("SDSECTION"), "a deleted section's shapes go with it");
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[] { "dialog(\"Section Designer Section Property Data\"", "dialog(\"Section Designer\"", "dialog(\"Section Object Data - \" + kind", "\"CONCRETE CIRCLE\": \"Circle\"", "\"STEEL I/WIDE FLANGE\": \"I Section\"", "\"LINE REBAR\": \"LineBar\"", "POLYCORNER ${k + 1} X", "NBARS ${g.nbars || 0} STARTANGLE", "\"Composite Column\": \"COMPOSITECOLUMN\"", "Clear Cover to Tie", "Actual Bar Spacing", "[\"Change Shape to Poly\", () => toPoly()", "dialog(\"Drawing Options\"", "dialog(\"Section Properties\"",
                "Concrete Column Check/Design", "Reinforcement to be Designed", "[\"Rubberband Zoom\"", "[\"Check Section for Overlaps\"", "[\"Get Previous Selection\"", "[\"Moment-Curvature Curve...\"", "SHAPETYPE ${sdQ(s.type)} D ${s.D} B ${s.B} XC ${s.XC} YC ${s.YC}" })
                Check(html.Contains(part), "viewer.html lost " + part);
        });

        yield return ("program: the forms offer only the model's program's options (SAP2000: no ETABS-only assignment tables or slab types; SAFE: concrete shapes, no table rows), and keep what the model has", () =>
        {
            var link = Frame();
            string[] Names(JsonNode n) => n.AsArray().Select(x => x is JsonObject o ? o["shape"]!.GetValue<string>() : x!.GetValue<string>()).ToArray();
            var etabs = Run(link, "property_definitions", new { });
            Check(etabs["assign_tables"]!.AsObject().ContainsKey("Frame Assignments - Floor Cracking"), "ETABS has floor cracking");
            link.Model.Target = "sap2000";
            var sap = Run(link, "property_definitions", new { });
            Check(!sap["assign_tables"]!.AsObject().ContainsKey("Frame Assignments - Floor Cracking") && sap["assign_tables"]!.AsObject().ContainsKey("Frame Assignments - Output Stations"), "SAP2000: output stations but no floor cracking");
            Check(Names(sap["slab_types"]!).Length == 0, "SAP2000 has no slab types");
            Check(!Names(Run(link, "model_definitions", new { })["pattern_types"]!).Contains("Seismic (Drift)"), "SAP2000 has no drift seismic patterns");
            Check(Run(link, "definition_data", new { })["tables"]!.AsObject().All(t => t.Key.EndsWith("Layers") || t.Key.StartsWith("Generalized") || t.Key.StartsWith("Hinges Def") || DefineTables.IsSap(t.Key)), "SAP2000's definition tables");
            link.Model.Target = "safe";
            var safe = Run(link, "property_definitions", new { });
            Check(Names(safe["shapes"]!).All(s => s.StartsWith("Concrete")), "SAFE: concrete shapes only");
            Check(safe["assign_tables"]!.AsObject().Count == 0 && Run(link, "definition_data", new { })["tables"]!.AsObject().All(t => SafeTables.Has(t.Key)), "SAFE: no ETABS table rows (only SAFE's own tables)");
            Check(!Names(safe["modeling_types"]!).Contains("Layered") && !Names(safe["material_types"]!).Contains("Aluminum"), "SAFE: no layered shells or aluminum");
        });
        yield return ("excel: a material's other data, the assignment rows and the definition rows go out to Excel and back with nothing lost; a row for a missing object is refused", () =>
        {
            var link = Frame();
            var m = link.Model;
            var beam = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam);
            AssignTables.Set(beam, new Dictionary<string, List<Dictionary<string, string>>> { ["Frame Assignments - Output Stations"] = [new() { ["StationOpt"] = "Min Stations", ["MinStations"] = "5" }] });
            ProgramTables.SetMaterialTables(m.Materials.First(x => x.IsConcrete), new Dictionary<string, List<Dictionary<string, string>>> { ["Damping Parameters"] = [new() { ["ModalRatio"] = "0.05" }] });
            DefineTables.Set(m, "Hinge Definitions - Moment M3", "H1", [new() { ["IsSymmetric"] = "Yes" }, new() { ["IsSymmetric"] = "No" }]);
            var path = Path.Combine(Path.GetTempPath(), "cpm-extra-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                TableTools.Write(path, CsiProMax.CsiTables.ModelTables.Export(m));
                var tables = TableTools.Read(path);
                Check(tables.Any(t => t.Name == "Frame Assignments - Output Stations") && tables.Any(t => t.Name == "Material Properties - Damping Parameters") && tables.Any(t => t.Name == "Hinge Definitions - Moment M3"), "the three tables are in the workbook: " + string.Join(", ", tables.Select(t => t.Name)));
                var copy = ModelFile.FromJson(ModelFile.ToJson(m));
                copy.LineAssigns.ForEach(a => a.Tables = null); copy.Materials.ForEach(x => x.Tables = null); copy.DefinitionTables = null;
                var r = CsiProMax.CsiTables.ModelTables.Import(copy, tables);
                string Extras(StructuralModel x) => JsonSerializer.Serialize(new { a = x.LineAssigns.Select(y => y.Tables), mt = x.Materials.Select(y => y.Tables), d = x.DefinitionTables });
                Check(Extras(copy) == Extras(m), "the rows came back the same: " + Extras(copy) + " | " + string.Join("; ", r.Notes));
                var bad = tables.Single(t => t.Name == "Frame Assignments - Output Stations");
                bad.Rows[0][1] = "NOPE";
                try { CsiProMax.CsiTables.ModelTables.Import(ModelFile.FromJson(ModelFile.ToJson(m)), tables); throw new Exception("a row for a missing frame was taken"); }
                catch (ArgumentException ex) { Check(ex.Message.Contains("there is no frame NOPE"), ex.Message); }
            }
            finally { File.Delete(path); }
        });
        yield return ("sap2000: generalized displacements and a layered slab's layers go into SAP2000's own tables", () =>
        {
            var link = Frame();
            var m = link.Model;
            var pt = m.Points[0].Name;
            DefineTables.Set(m, "Generalized Displacement Definitions", "GD1", [new() { ["Type"] = "Translational", ["PointName"] = pt + "@Story2", ["TransU1SF"] = "1" }]);
            DefineTables.Set(m, "Generalized Displacement Definitions", "GD2", [new() { ["Type"] = "Rotational", ["PointName"] = "NOPE@Story2", ["RotR3SF"] = "1" }]);
            var slab = m.ShellSections.First(x => !x.IsWall);
            DefineTables.Set(m, "Slab Property Definitions - Layers", slab.Name, [new() { ["LayerName"] = "Top", ["Distance"] = "0.05", ["Thickness"] = "0.1", ["ModelType"] = "Shell", ["Material"] = slab.Material }]);
            var s2k = CsiProMax.S2k.S2kWriter.Write(m, "a.s2k").Text;
            Check(s2k.Contains("GENERALIZED DISPLACEMENT DEFINITIONS 1 - TRANSLATIONAL") && System.Text.RegularExpressions.Regex.IsMatch(s2k, @"GenDispl=GD1\s+Joint=\S+\s+U1SF=1"), "GD1 in SAP2000's table");
            Check(!s2k.Contains("GenDispl=GD2"), "GD2 names no point, so it is left out");
            Check(s2k.Contains("AREA SECTION PROPERTY LAYERS") && s2k.Contains("LayerName=Top") && s2k.Contains("Shell-Layered/Nonlinear"), "the layers and a layered shell");
        });
        yield return ("round trip: a model for ETABS with every kept row goes to a SAP2000 file and back, and comes back the same (ETABS-only items are not lost)", () =>
        {
            var link = Frame();
            var m = link.Model;
            DefineTables.SetRebarSize(m, "T16", 0.000201, 0.016);
            DefineTables.SetRebar(m, "C500x500", new Dictionary<string, string> { ["PATTERN"] = "R-4-4" });
            DefineTables.Set(m, "Hinge Definitions - Moment M3", "H1", [new() { ["IsSymmetric"] = "Yes" }]);
            DefineTables.Set(m, "Walking Vibration Definitions", "W1", [new() { ["Story"] = "Story1", ["X"] = "1" }]);
            var beam = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam);
            AssignTables.Set(beam, new Dictionary<string, List<Dictionary<string, string>>> { ["Frame Assignments - Floor Cracking"] = [new() { ["Consider"] = "Yes" }] });
            ProgramTables.SetMaterialTables(m.Materials.First(x => x.IsConcrete), new Dictionary<string, List<Dictionary<string, string>>> { ["Time Dependence - CEB-FIP 90"] = [new() { ["RelHumid"] = "50" }] });
            var before = ModelFile.ToJson(m);
            m.Target = "sap2000";
            var path = Path.Combine(Path.GetTempPath(), "cpm-trip-" + Guid.NewGuid().ToString("N") + ".s2k");
            try
            {
                CsiProMax.S2k.S2kWriter.WriteFile(m, path);
                var back = CsiProMax.S2k.S2kReader.Read(File.ReadAllText(path), "Trip");
                Check(back.Exact, "the SAP2000 file carries the whole model: " + string.Join(" | ", back.Notes));
                back.Model.Target = "etabs";
                Check(ModelFile.ToJson(back.Model) == before, "back for ETABS it is the same model");
                Check(DefineTables.RebarSizes(back.Model).Exists(x => x.Name == "T16") && back.Model.DefinitionTables!.ContainsKey("Walking Vibration Definitions"), "the bar size and the ETABS-only walking vibration came back");
            }
            finally { File.Delete(path); }
        });
        yield return ("sap2000: a hinge in SAP2000's own hinge tables goes into the .s2k and is read back from its tables; ETABS is never given them", () =>
        {
            var link = Frame();
            var m = link.Model;
            m.Target = "sap2000";
            Run(link, "define_table_rows", new { table = "Hinges Def 01 - Overview", name = "PH1", rows = new[] { new Dictionary<string, object> { ["AssignHinge"] = "No", ["DOFType"] = "M3", ["Behavior"] = "Deformation Controlled" } } });
            Run(link, "define_table_rows", new { table = "Hinges Def 03 - Noninteracting - Deform Control - Force-Deform", name = "PH1", rows = new[] { new Dictionary<string, object> { ["AssignHinge"] = "No", ["DOFType"] = "M3", ["FDPoint"] = "A", ["Force"] = 0, ["Displ"] = 0 }, new Dictionary<string, object> { ["AssignHinge"] = "No", ["DOFType"] = "M3", ["FDPoint"] = "B", ["Force"] = 1, ["Displ"] = 0 } } });
            var data = Run(link, "definition_data", new { });
            Check(data["tables"]!.AsObject().ContainsKey("Hinges Def 01 - Overview") && !data["tables"]!.AsObject().ContainsKey("Hinge Definitions - Moment M3"), "SAP2000 offers its own hinge tables, not ETABS's");
            var text = CsiProMax.S2k.S2kWriter.Write(m, "a.s2k").Text;
            Check(text.Contains("HINGES DEF 01 - OVERVIEW") && System.Text.RegularExpressions.Regex.IsMatch(text, @"HingeName=PH1\s+AssignHinge=No\s+DOFType=M3\s+FDPoint=B"), "the hinge's tables in the .s2k");
            var tablesOnly = string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("$ ")));
            var back = CsiProMax.S2k.S2kReader.Read(tablesOnly, "Hinge");
            Check(!back.Exact && back.Model.DefinitionTables?["Hinges Def 03 - Noninteracting - Deform Control - Force-Deform"].Count(r => r["HingeName"] == "PH1") == 2, "read back from SAP2000's tables: " + string.Join(" | ", back.Notes));
            m.Target = "etabs";
            Check(!ProgramOptions.HasDefinitionTable("etabs", "Hinges Def 01 - Overview"), "ETABS is not given SAP2000's hinge tables");
        });
        yield return ("sap2000: shell edge releases and joint panel zones go into SAP2000's own tables (releases before any edge constraint), from SAP2000's forms or from ETABS's", () =>
        {
            var link = Frame();
            var m = link.Model;
            var slab = m.AreaAssigns.First(a => a.Story == "Story1");
            var slab2 = m.AreaAssigns.First(a => a.Story == "Story2");
            AssignTables.Set(slab, new Dictionary<string, List<Dictionary<string, string>>> { ["Area Edge Release Assignments"] = [new() { ["Edge"] = "2", ["ReleasedDOF"] = "Moment" }] });
            AssignTables.Set(slab2, new Dictionary<string, List<Dictionary<string, string>>> { ["Area Assignments - Edge Releases"] = [new() { ["EdgeNumber"] = "1", ["RBending"] = "Yes", ["RTwisting"] = "Yes", ["SBending"] = "5" }] });
            var p1 = m.PointAssigns.FirstOrDefault() ?? new PointAssign { Point = m.Points[0].Name, Story = "Story1" };
            if (!m.PointAssigns.Contains(p1)) m.PointAssigns.Add(p1);
            AssignTables.Set(p1, new Dictionary<string, List<Dictionary<string, string>>> { ["Joint Panel Zone Assignments"] = [new() { ["PZFrom"] = "Elastic", ["DoublerPl"] = "0.01" }] });
            var p2 = new PointAssign { Point = m.Points[1].Name, Story = "Story2" };
            m.PointAssigns.Add(p2);
            DefineTables.Set(m, "Panel Zone Property Definitions", "PZ1", [new() { ["Source"] = "From Link Property", ["LinkProp"] = "L1", ["Connect"] = "Braces to Other Objects" }]);
            AssignTables.Set(p2, new Dictionary<string, List<Dictionary<string, string>>> { ["Joint Assignments - Panel Zone"] = [new() { ["PanelZone"] = "PZ1" }] });
            var text = CsiProMax.S2k.S2kWriter.Write(m, "a.s2k").Text;
            var er = CsiProMax.S2k.S2kWriter.Name(slab.Label, "Story1");
            Check(System.Text.RegularExpressions.Regex.IsMatch(text, "Area=" + er + @"\s+Edge=2\s+ReleasedDOF=Moment"), "SAP2000's own edge release row");
            Check(System.Text.RegularExpressions.Regex.Matches(text, "Area=" + CsiProMax.S2k.S2kWriter.Name(slab2.Label, "Story2") + @"\s+Edge=1\s+ReleasedDOF=(Moment|Twisting)").Count == 2, "ETABS's edge release, a row per released direction");
            Check(text.Contains("StiffRot=5"), "the spring goes with the moment release");
            Check(System.Text.RegularExpressions.Regex.IsMatch(text, @"PZFrom=Elastic\s+DoublerPl=0.01") && System.Text.RegularExpressions.Regex.IsMatch(text, @"PZFrom=Link\s+PZLink=L1\s+PZConnect=""?Braces to Other Objects"), "both panel zones: " + text[text.IndexOf("JOINT PANEL ZONE")..][..300]);
            Check(!ProgramOptions.HasAssignTable("etabs", "Area Edge Release Assignments") && ProgramOptions.HasAssignTable("sap2000", "Joint Panel Zone Assignments"), "SAP2000's own tables are SAP2000's");
        });
    }
}
