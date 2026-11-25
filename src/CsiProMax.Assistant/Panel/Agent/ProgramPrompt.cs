using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Agent;

/// <summary>
/// What the chat's Claude is told about the CSI program it works in, on top of the shared prompt. Each program keeps
/// its text in its own folder (Safe/SafePrompt.cs, Sap2000/SapPrompt.cs); ETABS is the shared prompt itself.
/// </summary>
internal static class ProgramPrompt
{
    public static string For(CsiProgram program) => program.Key switch
    {
        "safe" => CsiProMax.Assistant.Safe.SafePrompt.Text,
        "sap2000" => CsiProMax.Assistant.Sap2000.SapPrompt.Text,
        _ => "",
    };

    /// <summary>What the Modeler adds about writing other programs' files.</summary>
    public static string Modeler => CsiProMax.Assistant.Sap2000.SapPrompt.Modeler;

    /// <summary>The shared prompt in the program's own words: "SAFE" where it says "ETABS", for a program that is not ETABS.</summary>
    public static string Reword(string text, CsiProgram program) =>
        program.Key == CsiProgram.Etabs.Key ? text : text.Replace("CSI ETABS", "CSI " + program.Name, StringComparison.Ordinal).Replace("ETABS", program.Name, StringComparison.Ordinal); // ETABS itself
}
