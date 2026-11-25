using System.Text.Json;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Assistant.Tools;

namespace EtabsProMax.Assistant;

/// <summary>
/// Checks the connection to the running ETABS, one API call at a time, and prints what each gave: run EtabsProMax
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

    public static int Run(IEtabsLink etabs, bool changes)
    {
        var steps = Steps(etabs, changes);
        foreach (var s in steps) Console.WriteLine($"{(s.Ok ? "ok   " : "FAIL ")} {s.Name}: {s.Detail}");
        var bad = steps.Count(s => !s.Ok);
        Console.WriteLine(bad == 0 ? "All steps passed." : $"{bad} steps failed.");
        return bad == 0 ? 0 : 1;
    }

    public static List<StepResult> Steps(IEtabsLink etabs, bool changes)
    {
        IsRunning = true;
        try { var r = StepsCore(etabs, changes); Last = (DateTime.Now, r); return r; }
        finally { IsRunning = false; }
    }

    private static List<StepResult> StepsCore(IEtabsLink etabs, bool changes)
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

        if (changes)
        {
            var stories = etabs.Stories();
            double z0 = stories[0].Elevation, z1 = stories[1].Elevation;
            string frame = "", area = "";
            Step("AddFrame", () => frame = etabs.AddFrame(-5, -5, z0, -5, -5, z1, etabs.FrameSections()[0]));
            Step("AddArea", () => area = etabs.AddArea([(-5, -5, z1), (-2, -5, z1), (-2, -2, z1), (-5, -2, z1)], etabs.AreaSections()[0]));
            Step("SetFrameSection", () => { etabs.SetFrameSection(frame, etabs.FrameSections()[^1]); return null; });
            Step("SetAreaSection", () => { etabs.SetAreaSection(area, etabs.AreaSections()[0]); return null; });
            Step("SetRestraint", () => { var p = etabs.Points().First(p => Math.Abs(p.X + 5) < 1e-6 && Math.Abs(p.Y + 5) < 1e-6 && Math.Abs(p.Z - z0) < 1e-6).Name; etabs.SetRestraint(p, [true, true, true, false, false, false]); return etabs.Restraint(p); });
            Step("AddLoadPattern", () => { etabs.AddLoadPattern("EPM-TEST", "Super Dead", 0); return etabs.LoadPatterns(); });
            Step("SetFrameLoad", () => { etabs.SetFrameLoad(frame, "EPM-TEST", 1.5, true); return null; });
            Step("SetAreaLoad", () => { etabs.SetAreaLoad(area, "EPM-TEST", 2.5, true); return null; });
            Step("AddCombo", () => { etabs.AddCombo("EPM-TEST-COMBO", [("Dead", 1.2), ("EPM-TEST", 1.6)]); return etabs.Combos(); });
            Step("Delete", () => { etabs.Delete("frame", frame); etabs.Delete("area", area); return null; });
            Step("Refresh", () => { etabs.Refresh(); return null; });
        }
        return done;
    }
}
