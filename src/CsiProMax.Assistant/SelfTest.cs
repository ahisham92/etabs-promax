using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// Checks the connection to the running CSI program (ETABS, SAFE or SAP2000), one API call at a time, and prints what
/// each gave: run CsiProMax --selftest with a model open. With --changes it also adds a column, a slab, a load
/// pattern, loads and a combination to the open model (use a scratch model), to check the calls that change it.
///
/// It runs in every program, so nothing here may assume ETABS: a table is taken from the program's own table list,
/// never by a name written here; a tool is the one the chat is given in this program (ProgramTools.Chat); a step that
/// belongs to one program is named after that program; and what it touches in the window (the selection) is put back.
/// </summary>
public static class SelfTest
{
    /// <summary>
    /// One checked call: its name, whether it worked, and what it gave or why it failed. Detail is cut short for a
    /// dialog; Full is the whole answer when it was longer, for the field test's report.
    /// </summary>
    public sealed record StepResult(string Name, bool Ok, string Detail, string? Full = null);

    /// <summary>The last run in this session, for the self_test_status tool; null before the first.</summary>
    public static (DateTime At, List<StepResult> Steps)? Last { get; private set; }

    public static bool IsRunning { get; private set; }

    /// <summary>The most of one answer the field test's report keeps.</summary>
    private const int MostFull = 20000;

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
        var program = etabs.Program.Name;
        var none = JsonDocument.Parse("{}").RootElement;
        void Step(string name, Func<object?> body)
        {
            try
            {
                var text = Readable.Text(body());
                done.Add(new StepResult(name, true, text.Length > 300 ? text[..300] + "..." : text,
                    text.Length <= 300 ? null : text.Length > MostFull ? text[..MostFull] + "..." : text));
            }
            catch (Exception ex) { done.Add(new StepResult(name, false, $"{ex.GetType().Name}: {ex.Message}")); }
        }

        // The model's file name without its folder: the report is sent on, and a folder can name a client or a person.
        Step("Info", () => { var info = etabs.Info(); return info with { File = info.File.Replace('\\', '/').Split('/').Last() }; });
        Step("Stories", () => etabs.Stories());
        Step("Frames", () => etabs.Frames().Take(3));
        Step("Areas", () => etabs.Areas().Take(2));
        Step("Points", () => etabs.Points().Take(3));
        // A model with nothing in it yet has no point to read and no frame to select: said so, not counted as broken.
        void NotHere(string name, string what) => done.Add(new StepResult(name, true, $"skipped: the open model has no {what} yet, so there was nothing to try this on"));
        bool Any(Func<int> count) { try { return count() > 0; } catch (Exception ex) when (ex is not OutOfMemoryException) { return true; } }
        if (Any(() => etabs.Points().Count)) Step("Restraint", () => etabs.Restraint(etabs.Points()[0].Name));
        else NotHere("Restraint", "points");
        Step("Selected", () => etabs.Selected().Take(5));
        Step("FrameSections", () => etabs.FrameSections());
        Step("AreaSections", () => etabs.AreaSections());
        Step("Materials", () => etabs.Materials());
        Step("LoadPatterns", () => etabs.LoadPatterns());
        Step("LoadCases", () => etabs.LoadCases());
        Step("Combos", () => etabs.Combos());
        Step("Tables", () => etabs.Tables().Count);
        // A table the program itself lists as having data, by the key it lists it under. Each program has its own
        // tables (SAFE has no 'Story Definitions'), so no table name is written here.
        TableName? readable = null;
        try { readable = etabs.Tables().FirstOrDefault(t => !t.Empty); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* the Tables step above says why */ }
        if (readable is { } table) Step("ReadTable", () => { var t = etabs.ReadTable(table.Key, null, null); return new { table = table.Name, t.Fields, Rows = t.Rows.Take(2) }; });
        else done.Add(new StepResult("ReadTable", true, $"skipped: {program} lists no table with data in this model, so there was none to read"));
        if (Any(() => etabs.Frames().Count)) Step("Select", () => SelectAndPutBack(etabs));
        else NotHere("Select", "frames");
        // The tool the chat is given in this program, not ETABS's when the program has its own.
        Step("Tool get_model_context", () =>
        {
            var answer = ProgramTools.Chat(etabs.Program).First(t => t.Name == "get_model_context").Run(none, etabs);
            var named = answer["program"]?.GetValue<string>();
            if (named != program) throw new InvalidOperationException($"the model context says the program is '{named ?? "(not said)"}', and this is {program}");
            return answer;
        });
        // The one rule for checks (Verdict), held on the engineer's own model: nothing passes or fails without an analysis.
        Step($"{program} checks give no verdict without an analysis", () =>
        {
            // An analysed model's checks are run further down, where they answer from its results.
            if (etabs.Info().Locked) return (object)"the model has been analysed, so its checks answer from its results (see the result steps)";
            var report = ProgramTools.Report(none, etabs);
            var lines = report["summary"] is JsonArray list ? list.Select(n => n?.GetValue<string>() ?? "").ToList() : [];
            var verdicts = lines.Where(l => !l.EndsWith(": " + Verdict.NotAvailable, StringComparison.Ordinal)).ToList();
            if (verdicts.Count > 0) throw new InvalidOperationException($"the model has not been analysed, and {verdicts.Count} of {lines.Count} checks answered as if it had: " + string.Join("; ", verdicts));
            return new { analysed = false, summary = lines };
        });
        if (etabs.Program.Key == CsiProgram.Etabs.Key)
        {
            // ETABS's result tables and checks. The table and field names the checks look for were written from
            // ETABS's documentation, so the field test says what a real ETABS calls them: each table the checks read
            // with its fields and first rows, then every check's own answer.
            JsonNode Result(string name) => ResultTools.All.First(t => t.Name == name).Run(none, etabs);
            Step($"{program} result and design tables", () => etabs.Tables().Where(t => ResultTools.IsResultTable(t.Name) || ResultTools.IsDesignSummary(t.Name)).Select(t => $"{t.Name}{(t.Empty ? " (empty)" : "")}").ToList());
            if (etabs.Info().Locked)
            {
                foreach (var names in ResultTools.CheckedTables) Step($"{program} table {names[0]}", () => ResultTools.Sample(etabs, names));
                foreach (var check in new[] { "results_overview", "check_modal", "check_drift", "check_torsion", "check_story_irregularity", "design_ratios", "check_report" })
                    Step($"{program} {check}", () => Result(check));
            }
            else done.Add(new StepResult($"{program} results", true, $"skipped: the model has not been analysed. Run the analysis (and the design) in {program}, then run this again for the result checks."));
            // The review pack from this model, with one load case so a large model stays quick. Nothing is written to disk.
            Step($"{program} review pack", () =>
            {
                var pack = Pack.Build(etabs, [], etabs.LoadCases().Take(1).ToList(), [], null, null, mostRows: 2000);
                return new { bytes = pack.Zip.Length, frames = pack.Frames, areas = pack.Areas, tables = pack.Tables, notes = pack.Notes };
            });
        }
        if (etabs.Program.Key == CsiProgram.Safe.Key)
        {
            // SAFE's own calls and tables: what the SAFE tools lean on.
            Step("SAFE tables", () => etabs.Tables().Where(t => Safe.SafePack.Takes(t.Name) || ResultTools.Norm(t.Name).Contains("strip", StringComparison.Ordinal)).Select(t => $"{t.Name}{(t.Empty ? " (empty)" : "")}").ToList());
            Step("SAFE list_design_strips", () => Safe.SafeTools.All.First(t => t.Name == "list_design_strips").Run(none, etabs));
            Step("SAFE area spring properties", () => etabs.Raw("list the soil properties", m => { int n = 0; string[] a = []; m.PropAreaSpring.GetNameList(ref n, ref a); return a; }));
            Step("SAFE safe_results_overview", () => Safe.SafeTools.All.First(t => t.Name == "safe_results_overview").Run(none, etabs));
            Step("SAFE API file", () => EtabsApi.FindApiFile(CsiProgram.Safe));
            Step("SAFE slab properties", () => etabs.Raw("read the slab properties", m =>
            {
                int n = 0; string[] names = [];
                m.PropArea.GetNameList(ref n, ref names);
                return names.Take(n).Take(10).Select(name =>
                {
                    int type = 0, shell = 0, color = 0; string mat = "", notes = "", guid = ""; double t = 0;
                    // SAFE answers 0 for a slab property. Anything else (a wall property, say) has no slab data: said so,
                    // not shown as a slab with thickness 0.
                    try
                    {
                        var answer = Convert.ToInt32(m.PropArea.GetSlab(name, ref type, ref shell, ref mat, ref t, ref color, ref notes, ref guid));
                        return answer == 0 ? $"{name}: type {type}, shell {shell}, {mat}, {t}" : $"{name}: not a slab property (SAFE answered {answer})";
                    }
                    catch (Exception ex) { return $"{name}: {ex.Message}"; }
                }).ToList();
            }));
            Step("SAFE safe_check_report", () => Safe.SafeTools.Report(none, etabs)["summary"]);
        }

        var sap = etabs.Program.Key == CsiProgram.Sap2000.Key;
        if (sap)
        {
            // SAP2000's own tools: the reads always, the results when the model has been analysed.
            JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
            JsonNode Tool(string name, string json) => Sap2000.SapTools.All.First(t => t.Name == name).Run(J(json), etabs);
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

    /// <summary>
    /// Selects one frame, reads the selection back, and puts the engineer's own selection back as it was: the test
    /// reads the open model, and the selection is part of what the engineer sees in it.
    /// </summary>
    internal static object SelectAndPutBack(ICsiLink link)
    {
        var before = link.Selected();
        var frame = link.Frames()[0].Name;
        IReadOnlyList<(string Kind, string Name)> read;
        try
        {
            link.ClearSelection();
            link.Select("frame", frame);
            read = link.Selected();
        }
        finally
        {
            link.ClearSelection();
            foreach (var (kind, name) in before)
                if (kind is "frame" or "area" or "point")
                    try { link.Select(kind, name); }
                    catch (EtabsException) { /* an object that is gone meanwhile */ }
        }
        if (read.Count != 1 || read[0] != ("frame", frame))
            throw new InvalidOperationException($"frame {frame} was selected; the selection read back as: {string.Join(", ", read.Select(s => s.Kind + " " + s.Name))}");
        return new { selected = $"frame {frame}", read_back = true, selection_put_back = $"{before.Count(s => s.Kind is "frame" or "area" or "point")} of {before.Count} objects that were selected before" };
    }
}
