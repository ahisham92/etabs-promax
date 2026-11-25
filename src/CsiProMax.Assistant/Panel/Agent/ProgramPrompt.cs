using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Agent;

/// <summary>What the chat's Claude is told for the CSI program it works beside, on top of the shared notes. Each
/// program keeps its own text in its own folder; ETABS's is the shared notes themselves.</summary>
internal static class ProgramPrompt
{
    public static string For(CsiProgram program) => program.Key switch
    {
        "sap2000" => CsiProMax.Assistant.Sap2000.SapPrompt.Text,
        _ => "",
    } + (CsiProMax.App.Modeler ? CsiProMax.Assistant.Sap2000.SapPrompt.Modeler : "");

    /// <summary>The program the window was started for: SAP2000 or SAFE when the plug-in said so, otherwise ETABS.</summary>
    public static CsiProgram Current => CsiProMax.App.Etabs?.Link.Program ?? CsiProgram.Etabs;
}
