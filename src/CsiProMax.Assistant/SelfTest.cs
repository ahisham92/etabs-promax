using System.Text.Json;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// Checks the connection to the running ETABS, one API call at a time, and prints what each gave: run CsiProMax
/// --selftest with a model open. With --changes it also adds a column, a slab, a load pattern, loads and a combination
/// to the open model (use a scratch model), to check the calls that change it.
/// </summary>
public static class SelfTest
{
    /// <summary>One checked call: its name, whether it worked, and what it gave or why it failed.</summary>
    public sealed record StepResult(string Name, bool Ok, string Detail);

    /// <summary>The last run in this session, for the self_test_status tool; null before the first.</summary>
    public static (DateTime At, List<StepResult> Steps)? Last { get; private set; }

    public static bool IsRunning { get; private set; }

    public static int Run(ICsiLink etabs, bool changes)
    {
        var steps = Steps(etabs, changes);
        foreach (var s in steps) Console.WriteLine($"{(s.Ok ? "ok   " : "FAIL ")} {s.Name}: {s.Detail}");
        var bad = steps.Count(s => !s.Ok);
        Console.WriteLine(bad == 0 ? "All steps passed." : $"{bad} steps failed.");
        return bad == 0 ? 0 : 1;
    }

    public static List<StepResult> Steps(ICsiLink etabs, bool changes)
    {
        IsRunning = true;
        try { var r = StepsCore(etabs, changes); Last = (DateTime.Now, r); return r; }
        finally { IsRunning = false; }
    }

    private static List<StepResult> StepsCore(ICsiLink etabs, bool changes)
    {
        var done = new List<StepResult>();
        void Step(string name, Func<object?> body)
        {
            try
            {
                var text = JsonSerializer.Serialize(body(), new JsonSerializerOptions { IncludeFields = true });
                done.Add(new StepResult(name, true, text.Length > 300 ? text[..300] + "..." : text));
            }
            catch (Exception ex) { done.Add(new StepResult(name, false, $"{ex.GetType().Name}: {ex.Message}")); }
        }

        Step("Info", () => etabs.Info());
        Step("Stories", () => etabs.Stories());
        Step("Frames", () => etabs.Frames().Take(3));
        Step("Areas", () => etabs.Areas().Take(2));
        Step("Points", () => etabs.Points().Take(3));
        Step("Restraint", () => etabs.Restraint(etabs.Points()[0].Name));
        Step("Selected", () => etabs.Selected().Take(5));
        Step("FrameSections", () => etabs.FrameSections());
        Step("AreaSections", () => etabs.AreaSections());
        Step("Materials", () => etabs.Materials());
        Step("LoadPatterns", () => etabs.LoadPatterns());
        Step("LoadCases", () => etabs.LoadCases());
        Step("Combos", () => etabs.Combos());
        Step("Tables", () => etabs.Tables().Count);
        Step("ReadTable", () => { var t = etabs.ReadTable("Story Definitions", null, null); return new { t.Fields, Rows = t.Rows.Take(2) }; });
        Step("Select", () => { var f = etabs.Frames()[0].Name; etabs.ClearSelection(); etabs.Select("frame", f); var s = etabs.Selected(); etabs.ClearSelection(); return s; });
        Step("Tool get_model_context", () => EtabsTools.All.First(t => t.Name == "get_model_context").Run(JsonDocument.Parse("{}").RootElement, etabs).ToJsonString());
        if (etabs.Program.Key == CsiProgram.Safe.Key)
        {
            // SAFE's own calls and tables: what the SAFE tools lean on.
            var none = JsonDocument.Parse("{}").RootElement;
            Step("SAFE tables", () => etabs.Tables().Where(t => Safe.SafePack.Takes(t.Name) || ResultTools.Norm(t.Name).Contains("strip", StringComparison.Ordinal)).Select(t => $"{t.Name}{(t.Empty ? " (empty)" : "")}").ToList());
            Step("SAFE list_design_strips", () => Safe.SafeTools.All.First(t => t.Name == "list_design_strips").Run(none, etabs).ToJsonString());
            Step("SAFE area spring properties", () => etabs.Raw("list the soil properties", m => { int n = 0; string[] a = []; m.PropAreaSpring.GetNameList(ref n, ref a); return a; }));
            Step("SAFE safe_results_overview", () => Safe.SafeTools.All.First(t => t.Name == "safe_results_overview").Run(none, etabs).ToJsonString());
        }

        var sap = etabs.Program.Key == CsiProgram.Sap2000.Key;
        if (sap)
        {
            // SAP2000's own tools: the reads always, the results when the model has been analysed.
            JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
            string Tool(string name, string json) => Sap2000.SapTools.All.First(t => t.Name == name).Run(J(json), etabs).ToJsonString();
            Step("SAP2000 get_model_context", () => Tool("get_model_context", "{}"));
            Step("SAP2000 query_joints", () => Tool("query_joints", "{\"limit\":3}"));
            Step("SAP2000 table names", () => etabs.Tables().Where(t => Sap2000.SapPack.Takes(t.Name)).Select(t => $"{t.Name}{(t.Empty ? " (empty)" : "")}").ToList());
            if (etabs.Info().Locked)
            {
                Step("SAP2000 joint_displacements", () => Tool("joint_displacements", "{\"limit\":3}"));
                Step("SAP2000 frame_forces", () => Tool("frame_forces", "{\"limit\":3}"));
                Step("SAP2000 base_reactions", () => Tool("base_reactions", "{\"per_joint\":true}"));
                Step("SAP2000 modal_results", () => Tool("modal_results", "{}"));
                Step("SAP2000 check_displacement", () => Tool("check_displacement", "{}"));
                Step("SAP2000 design_ratios", () => Tool("design_ratios", "{\"limit\":5}"));
                Step("SAP2000 check_report", () => Tool("check_report", "{}"));
            }
            else done.Add(new StepResult("SAP2000 results", true, "skipped: the model has not been analysed"));
        }

        if (changes)
        {
            var stories = etabs.Stories();
            double z0 = stories.Count > 1 ? stories[0].Elevation : 0, z1 = stories.Count > 1 ? stories[1].Elevation : 3;
            string frame = "", area = "";
            Step("AddFrame", () => frame = etabs.AddFrame(-5, -5, z0, -5, -5, z1, etabs.FrameSections()[0]));
            Step("AddArea", () => area = etabs.AddArea([(-5, -5, z1), (-2, -5, z1), (-2, -2, z1), (-5, -2, z1)], etabs.AreaSections()[0]));
            Step("SetFrameSection", () => { etabs.SetFrameSection(frame, etabs.FrameSections()[^1]); return null; });
            Step("SetAreaSection", () => { etabs.SetAreaSection(area, etabs.AreaSections()[0]); return null; });
            Step("SetRestraint", () => { var p = etabs.Points().First(p => Math.Abs(p.X + 5) < 1e-6 && Math.Abs(p.Y + 5) < 1e-6 && Math.Abs(p.Z - z0) < 1e-6).Name; etabs.SetRestraint(p, [true, true, true, false, false, false]); return etabs.Restraint(p); });
            Step("AddLoadPattern", () => { etabs.AddLoadPattern("CPM-TEST", "Super Dead", 0); return etabs.LoadPatterns(); });
            Step("SetFrameLoad", () => { etabs.SetFrameLoad(frame, "CPM-TEST", 1.5, true); return null; });
            Step("SetAreaLoad", () => { etabs.SetAreaLoad(area, "CPM-TEST", 2.5, true); return null; });
            Step("AddCombo", () => { etabs.AddCombo("CPM-TEST-COMBO", [(etabs.LoadCases().FirstOrDefault(c => c.Equals("Dead", StringComparison.OrdinalIgnoreCase)) ?? etabs.LoadCases()[0], 1.2), ("CPM-TEST", 1.6)]); return etabs.Combos(); });
            if (sap)
            {
                JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
                string Tool(string name, string json) => Sap2000.SapTools.All.First(t => t.Name == name).Run(J(json), etabs).ToJsonString();
                var mat = etabs.Materials()[0];
                Step("SAP2000 add_material", () => Tool("add_material", "{\"name\":\"CPM-C30\",\"type\":\"concrete\",\"elastic_modulus\":27000000,\"unit_weight\":25,\"fc\":30000}"));
                Step("SAP2000 add_frame_section", () => Tool("add_frame_section", "{\"name\":\"CPM-R300x600\",\"shape\":\"rectangle\",\"material\":\"CPM-C30\",\"depth\":0.6,\"width\":0.3}"));
                Step("SAP2000 add_frame_section I", () => Tool("add_frame_section", $"{{\"name\":\"CPM-I\",\"shape\":\"i_section\",\"material\":\"{mat}\",\"depth\":0.4,\"width\":0.2,\"flange_thickness\":0.015,\"web_thickness\":0.01}}"));
                Step("SAP2000 add_area_section", () => Tool("add_area_section", "{\"name\":\"CPM-S200\",\"material\":\"CPM-C30\",\"thickness\":0.2}"));
                Step("SAP2000 add_joints", () => Tool("add_joints", "{\"points\":[[-9,-9,0],[-9,-9,3]]}"));
                Step("SAP2000 add_frames", () => Tool("add_frames", "{\"section\":\"CPM-R300x600\",\"lines\":[[-9,-9,3,-6,-9,3]]}"));
                Step("SAP2000 set_restraints", () => Tool("set_restraints", "{\"type\":\"pinned\",\"z\":0}"));
                Step("SAP2000 set_joint_loads", () => Tool("set_joint_loads", "{\"joints\":[],\"pattern\":\"CPM-TEST\",\"fz\":-5}"));
                Step("SAP2000 set_member_loads", () => Tool("set_member_loads", $"{{\"frames\":[\"{frame}\"],\"pattern\":\"CPM-TEST\",\"value\":2,\"direction\":\"x\"}}"));
                Step("SAP2000 set_releases", () => Tool("set_releases", $"{{\"frames\":[\"{frame}\"],\"type\":\"pinned\",\"ends\":\"j\"}}"));
            }
            Step("Delete", () => { etabs.Delete("frame", frame); etabs.Delete("area", area); return null; });
            Step("Refresh", () => { etabs.Refresh(); return null; });
        }
        return done;
    }
}
