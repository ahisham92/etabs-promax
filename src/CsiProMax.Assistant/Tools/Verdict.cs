using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The one rule for every check of every program: a check says "pass" or "fail" only from result rows the program
/// produced by its own analysis (and design). A model that has not been analysed answers "not available", whatever
/// its tables hold, and a table of what the engineer entered (overwrites, preferences, definitions, assignments,
/// the design load combinations) is never read as a result.
///
/// It exists because a SAFE model that had never been analysed was once reported as "slab design: pass": the design
/// load combination table had rows, and rows were taken for results. Every check starts with <see cref="NotAnalysed"/>,
/// every search for a result table leaves out <see cref="IsInputTable"/>, and the tests hold every program's report to
/// it (no check may pass or fail on a model that is not analysed).
/// </summary>
public static class Verdict
{
    public const string Pass = "pass", Fail = "fail", Reported = "reported", NotAvailable = "not available";

    /// <summary>The only words a check's status may be, in every program. The Viewer colours by them.</summary>
    public static readonly IReadOnlyList<string> Words = [Pass, Fail, Reported, NotAvailable];

    private static readonly string[] Inputs =
        ["overwrite", "preference", "definition", "assignment", "loadcombination", "connectivity", "option", "propert"];

    /// <summary>
    /// True for a table that holds what the engineer entered or chose, by its name: it has rows before any analysis,
    /// so it says nothing about results.
    /// </summary>
    public static bool IsInputTable(string name)
    {
        var n = ResultTools.Norm(name);
        return Inputs.Any(word => n.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// The answer of a check on a model that has not been analysed: "not available", and why. Null when the model has
    /// been analysed (the program locks it then), so the check goes on to read its table.
    /// </summary>
    public static JsonObject? NotAnalysed(ICsiLink e, string what)
    {
        if (e.Info().Locked) return null;
        var program = e is OfflineEtabs ? CsiProgram.Etabs.Name : e.Program.Name;
        return new JsonObject
        {
            ["status"] = NotAvailable,
            ["analysis_run"] = false,
            ["note"] = $"{program}'s analysis has not run on this model (the model is not locked), so there are no results and {what} could not be checked. Run the analysis (and the design, for design checks) in {program} first.",
        };
    }

    /// <summary>Pass or fail from what was judged; "not available" when nothing was.</summary>
    public static string From(int judged, int failing) => judged <= 0 ? NotAvailable : failing > 0 ? Fail : Pass;
}
