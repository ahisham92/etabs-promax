using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The tools, checks and pack tables a CSI program adds to the shared ones. Each program keeps its own in its own
/// folder (Safe/, Sap2000/); this is the one place that picks them by the program's key.
/// </summary>
public static class ProgramTools
{
    /// <summary>The program's own tools on a live model (none for ETABS: its tools are the shared ones).</summary>
    public static IReadOnlyList<Tool> For(CsiProgram program) => program.Key switch
    {
        "safe" => Safe.SafeTools.All,
        "sap2000" => Sap2000.SapTools.All,
        _ => [],
    };

    /// <summary>Tools the Modeler adds for writing other programs' files. Always offered in the Modeler.</summary>
    public static IReadOnlyList<Tool> Modeler { get; } = [.. Safe.SafeModelerTools.All, .. Sap2000.SapModelerTools.All];

    /// <summary>Every program's own tools, for lists that must know them all (which tools always ask first).</summary>
    public static IEnumerable<Tool> Every => CsiProgram.All.SelectMany(For).Concat(Modeler);

    /// <summary>The check report a review pack carries: the program's own checks, or the shared ETABS ones.</summary>
    public static JsonNode Report(JsonElement a, ICsiLink e) => e.Program.Key switch
    {
        "safe" => Safe.SafeTools.Report(a, e),
        "sap2000" => Sap2000.SapTools.Report(a, e),
        _ => ResultTools.Report(a, e),
    };

    /// <summary>True for a result or design table of this program that a review pack takes when it has data.</summary>
    public static bool PackTable(CsiProgram program, string table) => program.Key switch
    {
        "safe" => Safe.SafePack.Takes(table),
        "sap2000" => Sap2000.SapPack.Takes(table),
        _ => false,
    };

    /// <summary>The link to the running program: SAP2000 has its own on top of the shared one (no stories).</summary>
    public static ICsiLink Live(ComEtabs com) => com.Program.Key switch
    {
        "sap2000" => new Sap2000.SapLink(com),
        _ => com,
    };
}
