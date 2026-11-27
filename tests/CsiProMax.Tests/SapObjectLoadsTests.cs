using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// SAP2000's Assign > Joint Loads and Frame Loads forms (assign_sap_loads): rows the Modeler holds as its own loads
/// become them, every other row is kept as SAP2000 has it; Add, Replace and Delete Existing Loads work as in SAP2000,
/// and every row goes into the .s2k and comes back from it.
/// </summary>
static class SapObjectLoadsTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Sap()
    {
        var link = new OfflineEtabs(Templates.ConcreteFrame("Sap", [6, 5], [4], 2, 3.2));
        link.Model.Target = "sap2000";
        return link;
    }

    static JsonObject Run(OfflineEtabs link, string tool, object args) =>
        (JsonObject)DefineTools.All.Concat(HandTools.All).Concat(CaseTools.All).Single(x => x.Name == tool)
            .Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static List<Dictionary<string, string>> Rows(string s2k, string table) =>
        CsiProMax.CsiTables.CsiTableText.Parse(s2k).GetValueOrDefault(table) ?? [];

    static string Beam(OfflineEtabs link) { var b = link.Model.LineAssigns.First(x => link.Model.Line(x.Label)?.Kind == LineKind.Beam); return b.Label + "@" + b.Story; }
    static string Joint(OfflineEtabs link) => link.Model.Points[0].Name + "@" + link.Model.Stories[^1].Name;

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("sap2000 load forms: a GLOBAL joint force and a frame distributed load become the Modeler's own loads; Replace and Delete work", () =>
        {
            var link = Sap();
            var pat = link.Model.LoadPatterns[0].Name;
            var (j, b) = (Joint(link), Beam(link));
            var before = (link.Model.PointLoads.Count, link.Model.LineLoads.Count);
            Run(link, "assign_sap_loads", new { table = "Joint Loads - Force", objects = new[] { j }, pattern = pat, rows = new[] { new { CoordSys = "GLOBAL", F1 = "10", F2 = "0", F3 = "-5", M1 = "0", M2 = "0", M3 = "0" } } });
            Check(link.Model.PointLoads.Count == before.Item1 + 1, "the joint force is the Modeler's own load");
            Run(link, "assign_sap_loads", new { table = "Joint Loads - Force", objects = new[] { j }, pattern = pat, option = "add", rows = new[] { new { CoordSys = "GLOBAL", F1 = "2" } } });
            Check(link.Model.PointLoads.Count == before.Item1 + 2, "Add keeps the first");
            Run(link, "assign_sap_loads", new { table = "Joint Loads - Force", objects = new[] { j }, pattern = pat, option = "replace", rows = new[] { new { CoordSys = "GLOBAL", F3 = "-7" } } });
            Check(link.Model.PointLoads.Count == before.Item1 + 1, "Replace takes the others off");
            // A uniform load and a trapezoid segment, as SAP2000's Distributed form gives them.
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Distributed", objects = new[] { b }, pattern = pat, rows = new object[] {
                new { CoordSys = "GLOBAL", Type = "Force", Dir = "Gravity", DistType = "RelDist", RelDistA = "0", RelDistB = "1", FOverLA = "4", FOverLB = "4" },
                new { CoordSys = "Local", Type = "Force", Dir = "2", DistType = "RelDist", RelDistA = "0.25", RelDistB = "0.75", FOverLA = "1", FOverLB = "3" } } });
            Check(link.Model.LineLoads.Count == before.Item2 + 2, "two of the Modeler's own frame loads");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            var dist = Rows(s2k, "FRAME LOADS - DISTRIBUTED");
            Check(dist.Any(r => r["CoordSys"] == "Local" && r["Dir"] == "2" && r["FOverLB"] == "3"), "a local load is CoordSys Local in the .s2k: " + string.Join(" | ", dist.Select(r => string.Join(",", r))));
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Distributed", objects = new[] { b }, pattern = pat, option = "delete" });
            Check(link.Model.LineLoads.Count == before.Item2, "Delete takes them off");
        });

        yield return ("sap2000 load forms: rows the Modeler has no load for are kept and go into the same .s2k table, and come back", () =>
        {
            var link = Sap();
            var pat = link.Model.LoadPatterns[0].Name;
            Run(link, "define_table_rows", new { table = "Joint Pattern Definitions", name = "JP1", rows = new[] { new { Pattern = "JP1" } } });
            var (j, b) = (Joint(link), Beam(link));
            var count = link.Model.LineLoads.Count;
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Distributed", objects = new[] { b }, pattern = pat, rows = new[] { new { CoordSys = "GLOBAL", Type = "Moment", Dir = "Z", DistType = "RelDist", RelDistA = "0", RelDistB = "1", MOverLA = "2", MOverLB = "2" } } });
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Temperature", objects = new[] { b }, pattern = pat, rows = new[] { new { Type = "Gradient3", TempGrad3 = "5", JtPattern = "JP1" } } });
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Strain", objects = new[] { b }, pattern = pat, rows = new[] { new { Component = "Curvature2", Curvature = "0.001" } } });
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Gravity", objects = new[] { b }, pattern = pat, rows = new[] { new { CoordSys = "GLOBAL", MultiplierX = "0", MultiplierY = "0", MultiplierZ = "-1" } } });
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Target Force", objects = new[] { b }, pattern = pat, rows = new[] { new { IncludeP = "Yes", P = "100", RelLocP = "0.5" } } });
            Run(link, "assign_sap_loads", new { table = "Overwrites - Auto Wind Loads - Frame", objects = new[] { b }, rows = new[] { new { LoadByWind = "No", Cf = "1.4" } } });
            Run(link, "assign_sap_loads", new { table = "Frame Fireproofing Assignments", objects = new[] { b }, rows = new[] { new { Type = "Sprayed Auto", Density = "2.3563", Thickness = "0.05", TopFlange = "No", SelfWt = "Yes", GravLoad = "Yes" } } });
            Check(link.Model.LineLoads.Count == count, "none of them is a Modeler load");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(Rows(s2k, "FRAME LOADS - DISTRIBUTED").Any(r => r["Type"] == "Moment" && r["MOverLA"] == "2"), "the moment is in FRAME LOADS - DISTRIBUTED");
            Check(Rows(s2k, "FRAME LOADS - TEMPERATURE").Any(r => r.GetValueOrDefault("JtPattern") == "JP1" && r.GetValueOrDefault("TempGrad3") == "5"), "the gradient by joint pattern");
            Check(Rows(s2k, "FRAME LOADS - STRAIN").Any(r => r["Component"] == "Curvature2"), "the curvature");
            Check(Rows(s2k, "FRAME LOADS - GRAVITY").Count == 1 && Rows(s2k, "FRAME LOADS - TARGET FORCE").Count == 1, "gravity and target force");
            Check(Rows(s2k, "OVERWRITES - AUTO WIND LOADS - FRAME").Single()["Cf"] == "1.4", "the wind overwrite");
            Check(Rows(s2k, "FRAME FIREPROOFING ASSIGNMENTS").Single()["Density"] == "2.3563", "the fireproofing");
            // From the file's tables: the same rows come out again.
            var back = S2kReader.Read(s2k, "a").Model;
            var again = S2kWriter.Write(back, "b.s2k").Text;
            foreach (var t in new[] { "FRAME LOADS - DISTRIBUTED", "FRAME LOADS - TEMPERATURE", "FRAME LOADS - STRAIN", "FRAME LOADS - GRAVITY", "FRAME LOADS - TARGET FORCE", "OVERWRITES - AUTO WIND LOADS - FRAME", "FRAME FIREPROOFING ASSIGNMENTS" })
                Check(Rows(again, t).Count == Rows(s2k, t).Count, $"{t}: {Rows(s2k, t).Count} rows out, {Rows(again, t).Count} after the round trip");
            Check(Rows(again, "FRAME LOADS - DISTRIBUTED").Any(r => r["Type"] == "Moment" && r["MOverLA"] == "2"), "the moment came back");
            // Replace in another pattern leaves these; Delete in this one takes the moment off.
            Run(link, "assign_sap_loads", new { table = "Frame Loads - Distributed", objects = new[] { b }, pattern = pat, option = "delete" });
            Check(!Rows(S2kWriter.Write(link.Model, "c.s2k").Text, "FRAME LOADS - DISTRIBUTED").Any(r => r.GetValueOrDefault("Type") == "Moment"), "Delete took the kept moment off");
        });

        yield return ("sap2000 load forms: area uniform, temperature and strain become the Modeler's own when they can; projected, gradients, curvature and SAP2000-only area loads are kept and come back", () =>
        {
            var link = Sap();
            const string pat = "CPM-AL";
            Run(link, "define_load_pattern", new { name = pat, type = "Other", self_weight = 0 });
            var a = link.Model.AreaAssigns[0]; var area = a.Label + "@" + a.Story;
            var count = link.Model.AreaLoads.Count;
            Run(link, "assign_sap_loads", new { table = "Area Loads - Uniform", objects = new[] { area }, pattern = pat, rows = new[] { new { CoordSys = "GLOBAL", Dir = "Gravity", UnifLoad = "3" } } });
            Check(link.Model.AreaLoads.Count == count + 1, "a gravity uniform load is the Modeler's own");
            Run(link, "assign_sap_loads", new { table = "Area Loads - Uniform", objects = new[] { area }, pattern = pat, option = "add", rows = new[] { new { CoordSys = "GLOBAL", Dir = "Z Projected", UnifLoad = "1" } } });
            Run(link, "assign_sap_loads", new { table = "Area Loads - Temperature", objects = new[] { area }, pattern = pat, rows = new[] { new { Type = "Gradient3", TempGrad3 = "8" } } });
            Run(link, "assign_sap_loads", new { table = "Area Loads - Strain", objects = new[] { area }, pattern = pat, rows = new[] { new { Component = "Curvature12", Curvature = "0.001" } } });
            Run(link, "assign_sap_loads", new { table = "Area Loads - Surface Pressure", objects = new[] { area }, pattern = pat, rows = new[] { new { Face = "Top", Pressure = "4" } } });
            Run(link, "assign_sap_loads", new { table = "Area Loads - Uniform To Frame", objects = new[] { area }, pattern = pat, rows = new[] { new { CoordSys = "GLOBAL", Dir = "Gravity", UnifLoad = "2", DistType = "Two Way" } } });
            Run(link, "assign_sap_loads", new { table = "Area Loads - Wind Pressure Coefficients", objects = new[] { area }, pattern = pat, rows = new[] { new { Windward = "Yes", Cp = "0.8", DistType = "To Joints" } } });
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(Rows(s2k, "AREA LOADS - UNIFORM").Count(r => r["LoadPat"] == pat) == 2 && Rows(s2k, "AREA LOADS - UNIFORM").Any(r => r["Dir"] == "Z Projected"), "both uniform loads, the projected one as SAP2000 has it");
            var back = S2kReader.Read(s2k, "a").Model;
            var again = S2kWriter.Write(back, "b.s2k").Text;
            foreach (var t in new[] { "AREA LOADS - UNIFORM", "AREA LOADS - TEMPERATURE", "AREA LOADS - STRAIN", "AREA LOADS - SURFACE PRESSURE", "AREA LOADS - UNIFORM TO FRAME", "AREA LOADS - WIND PRESSURE COEFFICIENTS" })
                Check(Rows(s2k, t).Count > 0 && Rows(again, t).Count == Rows(s2k, t).Count, $"{t}: {Rows(s2k, t).Count} rows out, {Rows(again, t).Count} after the round trip");
        });

        yield return ("sap2000 load forms: a joint force in a user coordinate system is kept, and a wrong field or pattern is refused with the model unchanged", () =>
        {
            var link = Sap();
            var pat = link.Model.LoadPatterns[0].Name;
            var j = Joint(link);
            Run(link, "assign_sap_loads", new { table = "Joint Loads - Force", objects = new[] { j }, pattern = pat, rows = new[] { new { CoordSys = "CSYS1", F1 = "3" } } });
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(Rows(s2k, "JOINT LOADS - FORCE").Any(r => r["CoordSys"] == "CSYS1" && r["F1"] == "3"), "the CSYS1 force is in the .s2k");
            var json = ModelFile.ToJson(link.Model);
            foreach (var bad in new object[] {
                new { table = "Joint Loads - Force", objects = new[] { j }, pattern = pat, rows = new[] { new { Nope = "1" } } },
                new { table = "Joint Loads - Force", objects = new[] { j }, pattern = "NOPE", rows = new[] { new { F1 = "1" } } },
                new { table = "No Such Table", objects = new[] { j }, pattern = pat, rows = new[] { new { F1 = "1" } } } })
            {
                var refused = false;
                try { Run(link, "assign_sap_loads", bad); } catch (Exception) { refused = true; }
                Check(refused, "refused: " + JsonSerializer.Serialize(bad));
            }
            Check(ModelFile.ToJson(link.Model) == json, "a refusal leaves the model as it was");
        });
    }
}
