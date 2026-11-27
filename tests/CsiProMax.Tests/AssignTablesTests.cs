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
        yield return ("sap2000: SAP2000's own Assign tables (joint local axes, frame skew and gravity load, area auto mesh) stay on their objects, go into the .s2k and come back; ETABS is never given them", () =>
        {
            var link = Frame();
            var m = link.Model;
            m.Target = "sap2000";
            var beam = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam && a.Story == "Story1");
            var slab = m.AreaAssigns.First(a => a.Story == "Story1");
            var point = m.PointAssigns.FirstOrDefault(p => p.Story == "Story1")?.Point ?? m.Points[0].Name;
            var pattern = m.LoadPatterns[0].Name;
            Run(link, "assign_frame_data", new { objects = new[] { beam.Label + "@Story1" }, tables = new Dictionary<string, object>
            {
                ["Frame End Skew Angle Assignments"] = new[] { new { SkewI = "10", SkewJ = "-5" } },
                ["Frame Loads - Gravity"] = new[] { new { LoadPat = pattern, CoordSys = "GLOBAL", MultiplierX = "0", MultiplierY = "0", MultiplierZ = "-1" } },
            } });
            Run(link, "assign_shell_data", new { objects = new[] { slab.Label + "@Story1" }, tables = new Dictionary<string, object> { ["Area Auto Mesh Assignments"] = new[] { new { MeshType = "Number", Number1 = "4", Number2 = "3" } } } });
            Run(link, "assign_joint_data", new { objects = new[] { point + "@Story1" }, tables = new Dictionary<string, object> { ["Joint Local Axes Assignments 1 - Typical"] = new[] { new { AngleA = "30", AngleB = "0", AngleC = "0", AdvanceAxes = "No" } } } });
            var text = S2kWriter.Write(m, "own.s2k").Text;
            foreach (var t in new[] { "FRAME END SKEW ANGLE ASSIGNMENTS", "FRAME LOADS - GRAVITY", "AREA AUTO MESH ASSIGNMENTS", "JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL" })
                Check(text.Contains("TABLE:  \"" + t + "\""), "the .s2k has " + t);
            Check(System.Text.RegularExpressions.Regex.IsMatch(text, @"Frame=\S+\s+SkewI=10\s+SkewJ=-5"), "the skew row names the frame first");
            var back = S2kReader.Read(text, "Own").Model;
            var b2 = back.LineAssigns.First(a => a.Label == beam.Label && a.Story == beam.Story);
            Check(AssignTables.Row(b2, "Frame End Skew Angle Assignments").GetValueOrDefault("SkewJ") == "-5", "the skew came back on the beam");
            Check(AssignTables.Row(b2, "Frame Loads - Gravity").GetValueOrDefault("MultiplierZ") == "-1", "the gravity load came back on the beam");
            Check(AssignTables.Row(back.AreaAssigns.First(a => a.Label == slab.Label && a.Story == slab.Story), "Area Auto Mesh Assignments").GetValueOrDefault("Number1") == "4", "the auto mesh came back on the slab");
            Check(back.PointAssigns.Exists(p => AssignTables.Row(p, "Joint Local Axes Assignments 1 - Typical").GetValueOrDefault("AngleA") == "30"), "the joint axes came back");
            Check(!(back.Kept ?? []).Keys.Any(k => k.Contains("SKEW", StringComparison.OrdinalIgnoreCase)), "not kept apart from the frame");
            Check(!ProgramOptions.HasAssignTable("etabs", "Frame End Skew Angle Assignments") && ProgramOptions.HasAssignTable("sap2000", "Frame Loads - Gravity"), "SAP2000's own, not ETABS's");
        });
        yield return ("sap2000: Assign > Frame forms' rows (advanced axes, material temperature, hinge distribution and user hinges, offsets) round-trip once each, and Reverse Connectivity keeps the ends' releases in place", () =>
        {
            var link = Frame();
            var m = link.Model;
            m.Target = "sap2000";
            var beam = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam && a.Story == "Story1");
            Refused(link, "assign_frame_data", new { objects = new[] { beam.Label + "@Story1" }, reverse = "global" }, "more than one story");
            m.LineAssigns.RemoveAll(a => a.Label == beam.Label && a.Story != "Story1");
            var b = beam.Label + "@Story1";
            Run(link, "assign_frame_data", new { objects = new[] { b }, offset_i = 0.2, offset_j = 0.3, rigid_zone = 0.5, tables = new Dictionary<string, object>
            {
                ["Frame Local Axes Assignments 2 - Advanced"] = new[] { new { LocalPlane = "12", PlOption1 = "Coord Dir", PlCoordSys = "GLOBAL", CoordDir1 = "+Y", CoordDir2 = "+X" } },
                ["Frame Material Temperatures"] = new[] { new { Temp = "35" } },
                ["Frame Offset Along Length Assignments"] = new[] { new { Type = "User", LengthI = "0.2", LengthJ = "0.3", RigidFactor = "0.5" } },
                ["Frame Hinge Assigns 00 - Hinge Distribution Type"] = new[] { new { HgeDistrTyp = "User Defined", IsHgeLenRel = "No", HgeLenRel = "0", HgeLenAbs = "0" } },
                ["Frame Hinge Assigns 02 - User Defined Properties"] = new[] { new { HgeDistrTyp = "User Defined", AssignProp = "FH1", SaveFibResp = "Yes", DistType = "RelDist", RelDist = "0.25", AbsDist = "0", LenOWType = "None" } },
                ["Frame Hinge Assigns 17 - Auto ASCE 41-17 - Concrete Beam"] = new[] { new { HgeDistrTyp = "User Defined", DOF = "M3", VFrom = "Case", VCase = "DEAD", Conforming = "Yes", RatioFrom = "Design", BeyondE = "Drops", SaveFibResp = "Yes", DistType = "RelDist", RelDist = "0.1" } },
            } });
            var text = S2kWriter.Write(m, "frame.s2k").Text;
            int Rows(string table) { var at = text.IndexOf("TABLE:  \"" + table + "\"", StringComparison.Ordinal); if (at < 0) return 0; var end = text.IndexOf("TABLE:", at + 8, StringComparison.Ordinal); return text[at..(end < 0 ? text.Length : end)].Split('\n').Count(l => l.TrimStart().StartsWith("Frame=" + "", StringComparison.Ordinal)); }
            Check(Rows("FRAME OFFSET ALONG LENGTH ASSIGNMENTS") >= 1 && System.Text.RegularExpressions.Regex.Matches(text, @"TABLE:  ""FRAME OFFSET ALONG LENGTH ASSIGNMENTS""").Count == 1, "one offsets table");
            Check(System.Text.RegularExpressions.Regex.IsMatch(text, @"AdvanceAxes=Yes"), "the typical axes row says advanced");
            foreach (var t in new[] { "FRAME MATERIAL TEMPERATURES", "FRAME HINGE ASSIGNS 00 - HINGE DISTRIBUTION TYPE", "FRAME HINGE ASSIGNS 02 - USER DEFINED PROPERTIES", "FRAME LOCAL AXES ASSIGNMENTS 2 - ADVANCED" })
                Check(System.Text.RegularExpressions.Regex.Matches(text, "TABLE:  \"" + System.Text.RegularExpressions.Regex.Escape(t) + "\"").Count == 1, "the .s2k has " + t + " once");
            var back = S2kReader.Read(text, "Frame").Model;
            var b2 = back.LineAssigns.First(a => a.Label == beam.Label && a.Story == beam.Story);
            Check(AssignTables.Row(b2, "Frame Material Temperatures").GetValueOrDefault("Temp") == "35", "the material temperature came back");
            Check(AssignTables.Row(b2, "Frame Hinge Assigns 02 - User Defined Properties").GetValueOrDefault("RelDist") == "0.25", "the user hinge came back");
            Check(AssignTables.Row(b2, "Frame Hinge Assigns 17 - Auto ASCE 41-17 - Concrete Beam").GetValueOrDefault("VCase") == "DEAD", "the ASCE 41-17 auto hinge came back");
            Check(System.Text.RegularExpressions.Regex.Matches(text, @"TABLE:  ""FRAME HINGE ASSIGNS 17 - AUTO ASCE 41-17 - CONCRETE BEAM""").Count == 1, "one ASCE 41-17 concrete beam table");
            Check(AssignTables.Row(b2, "Frame Local Axes Assignments 2 - Advanced").GetValueOrDefault("CoordDir1") == "+Y", "the advanced axes came back");
            Run(link, "assign_frame_data", new { objects = new[] { b }, release_i = new[] { "M3" }, release_j = Array.Empty<string>() });
            var (pi, pj) = (m.Line(beam.Label)!.PointI, m.Line(beam.Label)!.PointJ);
            Run(link, "assign_frame_data", new { objects = new[] { b }, reverse = "global" });
            var x = link.Model.LineAssigns.First(a => a.Label == beam.Label && a.Story == "Story1");
            Check(link.Model.Line(beam.Label)!.PointI == pj && link.Model.Line(beam.Label)!.PointJ == pi, "the ends swapped");
            var (ri, rj) = PropertyDefs.ReleasesOf(x);
            Check(ri.Count == 0 && rj.SequenceEqual(new[] { "M3" }), "the M3 release stayed at the same joint: now end J");
            Check(PropertyDefs.Value(x.Extra, "LENGTHOFFI") == "0.3" && PropertyDefs.Value(x.Extra, "LENGTHOFFJ") == "0.2", "the offsets stayed at their joints");
        });
        yield return ("sap2000 Assign > Area forms: every form's rows (SapFormRoundTrip.AreaAssigns) go into the .s2k as SAP2000's own tables and come back on the area; Reverse Local 3 reverses the corners", () =>
        {
            var link = Frame();
            var m = link.Model;
            m.Target = "sap2000";
            var slab = m.AreaAssigns.First(a => a.Story == "Story1");
            var mat = m.Materials.First(x => x.IsConcrete).Name;
            var obj = slab.Label + "@Story1";
            foreach (var (form, _, args, _) in CsiProMax.Assistant.Sap2000.SapFormRoundTrip.AreaAssigns)
            {
                var json = JsonNode.Parse(args.Replace("{mat}", mat))!.AsObject();
                json["objects"] = new JsonArray(obj);
                PropertyTools.All.Single(x => x.Name == "assign_shell_data").Invoke(JsonDocument.Parse(json.ToJsonString()).RootElement, link);
            }
            var text = S2kWriter.Write(m, "area.s2k").Text;
            var name = S2kWriter.Name(slab.Label, slab.Story);
            var tables = CsiProMax.CsiTables.CsiTableText.Parse(text);
            foreach (var (form, table, _, row) in CsiProMax.Assistant.Sap2000.SapFormRoundTrip.AreaAssigns)
            {
                var rows = tables.FirstOrDefault(t => t.Key.Equals(table, StringComparison.OrdinalIgnoreCase)).Value ?? [];
                var mine = rows.Where(r => r.GetValueOrDefault("Area") == name).ToList();
                Check(mine.Count > 0, $"{form}: the .s2k has no {table} row for {name}");
                Check(mine.Exists(r => row.All(kv => double.TryParse(kv.Value.Replace("{mat}", mat), System.Globalization.CultureInfo.InvariantCulture, out var want) && double.TryParse(r.GetValueOrDefault(kv.Key), System.Globalization.CultureInfo.InvariantCulture, out var got) ? Math.Abs(want - got) < 1e-9 : r.GetValueOrDefault(kv.Key) == kv.Value.Replace("{mat}", mat))),
                    $"{form}: {table} for {name} is {string.Join(" | ", mine.Select(r => string.Join(" ", r.Select(kv => kv.Key + "=" + kv.Value))))}");
            }
            var back = S2kReader.Read(text, "Area").Model;
            var a2 = back.AreaAssigns.First(a => a.Label == slab.Label && a.Story == slab.Story);
            Check(AssignTables.Row(a2, "Area Overwrites - Thickness").GetValueOrDefault("Thick3") == "0.35", "the thickness overwrite came back");
            Check(AssignTables.Row(a2, "Area Assignments - Material Overwrites").GetValueOrDefault("Material") == mat, "the material overwrite came back");
            Check(AssignTables.Row(a2, "Area Assignments - Additional Mass").GetValueOrDefault("AddedMass") == "0.03", "the area mass came back");
            Check(AssignTables.Row(a2, "Area Assignments - Local Axes").GetValueOrDefault("Angle") == "30", "the local axis angle came back");
            Check(Math.Abs(PropertyDefs.Modifiers(a2.Extra, PropertyDefs.ShellModifiers)["M11MOD"] - 0.25) < 1e-9, "the stiffness modifiers came back");
            Check(AssignTables.Row(a2, "Area Material Temperatures").GetValueOrDefault("Temp") == "20", "the material temperature came back");
            // Reverse Local 3: corner 1 stays, the others run the other way.
            var before = m.Area(slab.Label)!.PointNames.ToList();
            var others = m.AreaAssigns.Where(a => a.Label == slab.Label).Select(a => a.Label + "@" + a.Story).ToArray();
            PropertyTools.All.Single(x => x.Name == "reverse_objects").Invoke(JsonDocument.Parse(JsonSerializer.Serialize(new { objects = others })).RootElement, link);
            var after = m.Area(slab.Label)!.PointNames;
            Check(after[0] == before[0] && after[1] == before[^1] && after[^1] == before[1], "Reverse Local 3 runs the corners the other way: " + string.Join(",", after));
            if (others.Length > 1) Refused(link, "reverse_objects", new { objects = new[] { obj } }, "select it on each");
        });
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
            Check(!s2k.Text.Contains("Cable=", StringComparison.Ordinal), "SAP2000 26.3's frame added mass has no Cable field (0.4.57 full test)");
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

        yield return ("assign tables: ETABS-only assignments survive the model going to SAP2000 and back to ETABS, in the model and its .cpm", () =>
        {
            var link = Frame();
            var m = link.Model;
            var beam = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam && a.Story == "Story1");
            var slab = m.AreaAssigns.First(a => a.Story == "Story1");
            var etabsOnly = new Dictionary<string, object>
            {
                ["Frame Assignments - Column Splice Overwrites"] = new[] { new { SpliceOpt = "Splice at Height", SpliceHt = "1.2" } },
                ["Frame Assignments - Floor Cracking"] = new[] { new { Consider = "Yes" } },
                ["Frame Assignments - Moment Frame Beam Connection Type"] = new[] { new { Type = "Pinned" } },
                // On a line (a null line in ETABS), not an area (ETABS 23.2, the J export).
                ["Area Assignments - Slab Line Releases"] = new[] { new { ShearL = "Yes" } },
            };
            Run(link, "assign_frame_data", new { objects = new[] { beam.Label + "@Story1" }, tables = etabsOnly });
            Run(link, "assign_shell_data", new { objects = new[] { slab.Label + "@Story1" }, tables = new Dictionary<string, object> { ["Area Assignments - Slab Rib Locations"] = new[] { new { X = "1", Y = "2" } } } });
            string Rows(StructuralModel x) => JsonSerializer.Serialize(AssignTables.Rows(x).OrderBy(t => t.Key, StringComparer.Ordinal).ToDictionary(t => t.Key, t => t.Value));
            var before = Rows(m);
            var program = ModelerTools.All.Single(x => x.Name == "set_model_program");
            program.Invoke(JsonDocument.Parse("""{"program":"sap2000"}""").RootElement, link);
            Check(link.Model.Target == "sap2000", "the model is for SAP2000 now");
            S2kWriter.Write(link.Model, "a.s2k");
            var viaFile = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            Check(Rows(viaFile) == before, "the SAP2000 model's .cpm keeps the ETABS-only rows");
            program.Invoke(JsonDocument.Parse("""{"program":"etabs"}""").RootElement, link);
            Check(link.Model.Target is null or "etabs", "back to ETABS: " + link.Model.Target);
            Check(Rows(link.Model) == before, "back in ETABS the rows are all there, as they were");
            Check(AssignTables.Rows(link.Model)["Frame Assignments - Column Splice Overwrites"].Single()["SpliceHt"] == "1.2", "the splice height is intact");
        });
    }
}
