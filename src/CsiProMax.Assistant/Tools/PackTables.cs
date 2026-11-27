using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Which of the program's tables a review pack takes, beyond the usual ones, so that the Viewer can show every result
/// and every design the program shows (Ahmed 2026-11-25: "all results should be seen either results or design whatever
/// the program allows its API or not ... no tiny gap"). The rule is by kind, not by a list of names, so a table a
/// version adds is taken too:
/// <list type="bullet">
/// <item>every table with data that cannot be imported (the program's own "Analysis Results" and "Design" tables:
/// joint, frame, shell, link, spring, pier, spandrel, story, modal, base, hinge, section cut, pushover, buckling...);</item>
/// <item>the design inputs shown beside a design: preferences, overwrites, and the load cases, combinations, patterns,
/// functions and load assignments (temperature loads among them) a reviewer checks results against.</item>
/// </list>
/// When the program does not say whether a table imports (a stand-in model, an older version), the name decides.
/// </summary>
public static class PackTables
{
    /// <summary>Rows a pack takes in all (beyond the usual and design tables), so that a big model's pack still opens.</summary>
    public const int TotalMostRows = int.MaxValue;

    /// <summary>Rows a pack takes of the design and load inputs, apart from the results' total.</summary>
    public const int InputMostRows = int.MaxValue;

    /// <summary>A table of loads assigned to joints, frames or shells (areas, slabs): what Display > Load Assigns draws.</summary>
    public static bool IsLoadTable(string name)
    {
        var n = ResultTools.Norm(name);
        return new[] { "joint", "point", "frame", "line", "beam", "shell", "area", "slab", "wall", "surface", "floor" }.Any(w => n.StartsWith(w + "loads", StringComparison.Ordinal));
    }

    /// <summary>The tables read a second time with every step of a time history, staged or nonlinear case.</summary>
    public static readonly string[] HistoryTables =
    [
        "Base Reactions", "Story Forces", "Story Drifts", "Diaphragm Center Of Mass Displacements", "Story Accelerations",
        "Joint Displacements", "Joint Reactions", "Joint Accelerations - Absolute", "Joint Velocities - Absolute", "Element Forces - Links",
    ];

    /// <summary>The suffix of a table read with every step: "Base Reactions (Step by Step)".</summary>
    public const string StepSuffix = " (Step by Step)";

    private static readonly string[] ResultWords =
    [
        "displacement", "reaction", "drift", "forces", "stress", "acceleration", "velocit", "modal", "period", "participat",
        "basereaction", "storyforce", "stiffness", "centersofmass", "masssummary", "hinge", "sectioncut", "pushover", "buckling",
        "energy", "virtualwork", "deformation", "springforce", "soilpressure", "tributary", "materiallist", "torsional",
        "responsespectrum", "maxoveravg", "diaphragmcenter", "assembledjointmass", "strain", "results", "summary", "ratio",
        "punching", "stripforce", "crack", "influence", "plotfunction", "cablestress", "tendonforce",
    ];

    private static readonly string[] InputWords =
    [
        "preference", "overwrite", "loadcasedefinition", "loadcombinationdefinition", "loadpatterndefinition", "massource",
        "masssource", "functionsresponsespectrum", "functionstimehistory", "loadsdistributed", "loadspoint", "loadsuniform",
        "loadstemperature", "temperature", "loadsstrain", "loadsdeformation", "loadsgravity", "loadsforce", "loadsnonuniform",
        "loadsrotate", "loadsporepressure", "loadssurfacepressure", "hingeoverwrite", "assignmentshinge", "linkpropert", "springpropert",
        "assignmentsspring", "assignmentslink",
        // ETABS 23's load tables: "Frame Loads Assignments - Distributed", "Joint Loads Assignments - Force" (0.4.55).
        "loadsassignments", "loadassignments",
        // SAP2000's names for the same things.
        "springassignment", "hingeassign", "functionresponsespectrum", "functiontimehistory", "functionplotfunction",
        "casemodalhistory", "casedirecthistory", "caseresponsespectrum", "casestatic", "sectioncuts", "sectioncutdefinition",
        "tendonsection", "cablesection",
    ];

    /// <summary>"result", "input" (a design or load input shown beside the results), or null for a table not taken.</summary>
    public static string? Kind(TableName t)
    {
        var n = ResultTools.Norm(t.Name);
        if (n.Length == 0) return null;
        if (DesignResults.IsWallTable(t.Name)) return "result";
        if (IsMeshTable(n)) return "result";
        var input = InputWords.Any(w => n.Contains(w, StringComparison.Ordinal));
        // A table the program cannot import is what it makes of the model ("Frame Assignments - Summary", "Material List
        // by Section Property"): the pack carries it with the inputs, so that what comes back is all the program offers
        // (0.4.73 real-model round trip: the Engine's pack had none of them).
        if (Verdict.IsInputTable(t.Name)) return input || t.Import == 0 ? "input" : null;
        if (DesignResults.IsDesignTable(t.Name)) return "result";
        if (input) return "input";
        if (t.Import == 0) return "result";
        if (t.Import > 0) return null;
        return ResultWords.Any(w => n.Contains(w, StringComparison.Ordinal)) && !n.Contains("assign", StringComparison.Ordinal) && !n.Contains("definition", StringComparison.Ordinal) ? "result" : null;
    }

    /// <summary>
    /// The analysis mesh: the program divides each floor and wall into shell elements and gives their forces at the
    /// element joints ("~123"), most of which are not joints of the model. With these the Viewer draws the shell
    /// contours on the program's own elements, as ETABS, SAP2000 and SAFE draw them (Ahmed 2026-11-26: "exactly the same
    /// way"). ETABS and SAFE: "Objects and Elements - Joints" and "- Shells"; SAP2000: "Objects And Elements - Joints"
    /// and "- Areas".
    /// </summary>
    public static bool IsMeshTable(string normalized) =>
        normalized.StartsWith("objectsandelements", StringComparison.Ordinal)
        && (normalized.EndsWith("joints", StringComparison.Ordinal) || normalized.EndsWith("points", StringComparison.Ordinal) || normalized.EndsWith("shells", StringComparison.Ordinal)
            || normalized.EndsWith("areas", StringComparison.Ordinal) || normalized.EndsWith("slabs", StringComparison.Ordinal) || normalized.EndsWith("walls", StringComparison.Ordinal));

    /// <summary>
    /// A load assignment table ("Shell Loads - Uniform", "Frame Loads Assignments - Distributed", "Joint Loads
    /// Assignments - Force"): read first among the inputs, so that the Viewer's Load Assigns always have them. ETABS 23's
    /// shell loads were left out for size behind the combinations and wind parameters (main 4eabf46 run: Display > Load
    /// Assigns > Shell... greyed out, 25 rows in the model).
    /// </summary>
    public static bool IsLoadAssignment(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(ResultTools.Norm(name), "^(joint|point|frame|line|beam|shell|area|slab|wall|surface|floor|tendon)loads?");

    /// <summary>
    /// The tables to take beyond <paramref name="already"/>, in the order they are read: results first (smaller kinds
    /// before element tables, which are the big ones), then the inputs.
    /// </summary>
    public static IReadOnlyList<TableName> More(IReadOnlyList<TableName> known, IEnumerable<TableName> already)
    {
        var have = new HashSet<string>(already.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
        static int Weight(TableName t) => ResultTools.Norm(t.Name) switch
        {
            var n when n.StartsWith("story", StringComparison.Ordinal) || n.StartsWith("modal", StringComparison.Ordinal) || n.StartsWith("base", StringComparison.Ordinal) || n.StartsWith("diaphragm", StringComparison.Ordinal) => 0,
            var n when n.StartsWith("element", StringComparison.Ordinal) || n.StartsWith("joint", StringComparison.Ordinal) => 2,
            _ => 1,
        };
        return known.Where(t => !t.Empty && !have.Contains(t.Key) && Kind(t) is not null)
            .OrderBy(t => Kind(t) == "input" ? 1 : 0).ThenBy(t => IsLoadAssignment(t.Name) ? 0 : 1).ThenBy(Weight).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(t => t.Key).ToList();
    }

    /// <summary>
    /// Every step of the time history, multi-step static and nonlinear static cases, for the Viewer's plots (Display >
    /// Plot Functions, pushover curves): the history tables are read again with the program's table output set to step
    /// by step, then the output is set back as it was. Best effort: a program whose API has no such setting, or that
    /// refuses it, leaves the pack with the envelopes and says so in a note.
    /// </summary>
    /// <returns>What happened, for the manifest ("step_output") and a note: the 0.4.33 field test could not tell
    /// why a pack had no step tables, so every outcome is now said.</returns>
    /// <summary>The names the programs give the table output options, without Get/Set: SAP2000 and older ETABS, then ETABS 23.</summary>
    internal static readonly string[] OutputOptionNames = ["TableOutputOptionsForDisplay", "OutputOptionsForDisplay"];

    public static JsonObject? AddSteps(ICsiLink e, IReadOnlyList<TableName> known, IReadOnlyList<string> cases, IReadOnlyList<string> combos,
        JsonObject tables, int mostRows, List<string> notes, Func<string, TableData, int, JsonObject> json)
    {
        if (e is MemoryEtabs or OfflineEtabs) return null;
        var status = new JsonObject();
        JsonObject? Say(string outcome) { status["outcome"] = outcome; notes.Add("Time history steps: " + outcome); return status; }
        var wanted = HistoryTables.Select(h => known.FirstOrDefault(t => !t.Empty && ResultTools.Norm(t.Name) == ResultTools.Norm(h))).OfType<TableName>().ToList();
        if (wanted.Count == 0) return Say("none of the history tables (" + string.Join(", ", HistoryTables.Take(4)) + "...) has data.");
        JsonObject? before = null;
        // ETABS 23 names it Get/SetOutputOptionsForDisplay (no "Table"): the 4eabf46 field run had no pushover
        // curve because only the other name was tried.
        var name = OutputOptionNames[0];
        try
        {
            foreach (var n in OutputOptionNames)
            {
                name = n;
                try { before = e.Raw("read the table output options", m => ApiCall.Invoke((object)m, "DatabaseTables.Get" + n)) as JsonObject; break; }
                catch (Exception ex) when (n != OutputOptionNames[^1] && (ex is MissingMethodException || ex is EtabsException && ex.Message.Contains("no method", StringComparison.OrdinalIgnoreCase))) { }
            }
        }
        catch (Exception ex) when (ex is EtabsException or MissingMethodException or ToolInputException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // No program behind the link (a stand-in), or an API without this setting: the envelopes are what there is.
            return Say($"not taken: {e.Program.Name} did not give its table output options ({ex.GetType().Name}: {ex.Message}).");
        }
        if (before?["values"] is not JsonObject old) return Say($"not taken: {e.Program.Name} gave no table output options ({before?.ToJsonString()}).");
        status["options_before"] = old.DeepClone();
        var steps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in old) steps[k] = v?.DeepClone();
        // 2 is "step by step" for each kind of case that has steps.
        var any = false;
        foreach (var k in old.Select(kv => kv.Key).ToList())
            if (k.Contains("History", StringComparison.OrdinalIgnoreCase) || k.Contains("Multistep", StringComparison.OrdinalIgnoreCase) || k.Contains("NonlinearStatic", StringComparison.OrdinalIgnoreCase) || k.Contains("Staged", StringComparison.OrdinalIgnoreCase))
            { steps[k] = 2; any = true; }
        if (!any) return Say("not taken: none of the table output options is for time history, multi-step or nonlinear cases: " + string.Join(", ", old.Select(kv => kv.Key)) + ".");
        try
        {
            var set = e.Raw("set the table output to step by step", m => ApiCall.Invoke((object)m, "DatabaseTables.Set" + name, steps)) as JsonObject;
            // The same tables read again now give every step: what a read session kept of them is not the answer.
            PackSession.Forget(e);
            if (set is null || !ApiCall.Worked(set)) return Say($"not taken: {e.Program.Name} refused step by step table output ({set?["return"]?.ToJsonString()}).");
            var taken = new JsonArray();
            foreach (var t in wanted)
            {
                TableData data;
                // Straight from the program, never Pack.SameModel's reads: those hold the same table as read before the
                // output was set to step by step (envelopes only), so the Viewer's pack of the 0.4.67-test4 SAP2000 run
                // had no step tables and its Static Pushover Curve was greyed out.
                try { data = e.ReadTable(t.Key, cases, combos); }
                catch (EtabsException) { continue; }
                var st = Col(data.Fields, "StepType", "Step Type");
                if (st < 0) continue;
                var rows = data.Rows.Where(r => st < r.Length && r[st].Contains("step", StringComparison.OrdinalIgnoreCase)).ToList();
                if (rows.Count == 0) continue;
                tables[t.Name + StepSuffix] = json(t.Name + StepSuffix, new TableData(data.Fields, rows), mostRows);
                taken.Add((JsonNode)(t.Name + StepSuffix));
            }
            status["tables"] = taken;
            return Say(taken.Count > 0 ? $"taken in {taken.Count} tables ({string.Join(", ", taken.Select(n => n!.GetValue<string>()))})."
                : "the step by step output was set, and no table gave step rows: the model has no time history, multi-step static or nonlinear case that was run.");
        }
        catch (Exception ex) when (ex is EtabsException or MissingMethodException or ToolInputException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return Say($"not taken ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            var back = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in old) back[k] = v?.DeepClone();
            PackSession.Forget(e);
            try { e.Raw("set the table output back", m => ApiCall.Invoke((object)m, "DatabaseTables.Set" + name, back)); }
            catch (Exception ex) when (ex is EtabsException or MissingMethodException or ToolInputException or InvalidOperationException) { notes.Add($"The table output options could not be set back ({ex.Message})."); }
        }
    }

    private static int Col(IReadOnlyList<string> fields, params string[] names)
    {
        for (var i = 0; i < fields.Count; i++)
            if (names.Any(n => string.Equals(ResultTools.Norm(fields[i]), ResultTools.Norm(n), StringComparison.Ordinal))) return i;
        return -1;
    }
}
