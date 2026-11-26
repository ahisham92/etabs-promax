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
            Check(Run(link, "definition_data", new { })["tables"]!.AsObject().All(t => t.Key.EndsWith("Layers") || t.Key.StartsWith("Generalized")), "SAP2000's definition tables");
            link.Model.Target = "safe";
            var safe = Run(link, "property_definitions", new { });
            Check(Names(safe["shapes"]!).All(s => s.StartsWith("Concrete")), "SAFE: concrete shapes only");
            Check(safe["assign_tables"]!.AsObject().Count == 0 && Run(link, "definition_data", new { })["tables"]!.AsObject().Count == 0, "SAFE: no ETABS table rows");
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
    }
}
