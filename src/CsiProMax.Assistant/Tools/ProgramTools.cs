using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>The tools a CSI program adds to the shared ones, or puts in place of them (a tool with the same name
/// replaces the shared one). Each program keeps its tools in its own folder.</summary>
public static class ProgramTools
{
    public static IReadOnlyList<Tool> For(CsiProgram program) => program.Key switch
    {
        "sap2000" => Sap2000.SapTools.All,
        _ => [],
    };

    /// <summary>Tools for the Modeler, whatever program it was started for.</summary>
    public static IReadOnlyList<Tool> Modeler => Sap2000.SapModelerTools.All;

    /// <summary>Every program's tools, for the approval rules (which tools always ask).</summary>
    public static IEnumerable<Tool> Every => Sap2000.SapTools.All.Concat(Sap2000.SapModelerTools.All);

    /// <summary>The link to the running program: SAP2000 has its own on top of the shared one.</summary>
    public static ICsiLink Live(ComEtabs com) => com.Program.Key switch
    {
        "sap2000" => new Sap2000.SapLink(com),
        _ => com,
    };
}
