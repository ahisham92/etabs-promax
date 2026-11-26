using System.Globalization;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Calls that older CSI programs do not have, each with the way around it through the program's own editable tables.
/// ETABS 20's API has no SectCut and no ModalEigen.SetNumberModes (tester's PC, 0.4.53, 2026-11-26); its tables take
/// both. The call is tried first; the table only when the call is not there.
/// </summary>
public static class ApiWorkarounds
{
    /// <summary>True for an answer that says this API has no such call (not a refusal of the values given).</summary>
    public static bool IsMissing(Exception ex) =>
        ex is MissingMethodException or MissingMemberException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
        || ex is EtabsException or ToolInputException && (ex.Message.Contains("has no '", StringComparison.Ordinal) || ex.Message.Contains("has no method", StringComparison.Ordinal) || ex.Message.Contains("no such method", StringComparison.OrdinalIgnoreCase));

    /// <summary>The program's editable table whose name has every one of the words (letters and digits, any case), or null.</summary>
    public static TableName? Table(ICsiLink e, params string[] words) =>
        e.Tables().FirstOrDefault(t => words.All(w => Norm(t.Name).Contains(Norm(w), StringComparison.Ordinal)) && !Norm(t.Name).Contains("result", StringComparison.Ordinal));

    private static string Norm(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The modes of a modal case: LoadCases.ModalEigen/ModalRitz.SetNumberModes, else the modal case table.</summary>
    public static string SetNumberModes(ICsiLink e, string api, string name, int max, int min)
    {
        try
        {
            CsiTools.Call(e, $"set the modes of {name}", $"LoadCases.{api}.SetNumberModes", CsiTools.D(("Name", name), ("MaxModes", max), ("MinModes", min)));
            return "through the API";
        }
        catch (Exception ex) when (IsMissing(ex))
        {
            var kind = api == "ModalRitz" ? "Ritz" : "Eigen";
            var table = Table(e, "Modal", kind) ?? throw new EtabsException($"{e.Program.Name}'s API has no SetNumberModes ({ex.Message}), and it lists no table of {kind} modal cases to set them in.");
            EditableTables.Put(e, table.Key, "Name", [new Dictionary<string, string>
            {
                ["Name"] = name, ["MaxModes"] = max.ToString(CultureInfo.InvariantCulture), ["MinModes"] = min.ToString(CultureInfo.InvariantCulture),
            }], $"set the modes of {name}");
            return $"through the table {table.Name} ({e.Program.Name}'s API has no SetNumberModes)";
        }
    }

    /// <summary>A section cut by a group: SectCut.SetByGroup, else the program's section cut table.</summary>
    public static string SectionCutByGroup(ICsiLink e, string name, string group)
    {
        try
        {
            CsiTools.Call(e, "define a section cut by the group", "SectCut.SetByGroup", CsiTools.D(("Name", name), ("GroupName", group), ("MyType", 1)));
            return "through the API";
        }
        catch (Exception ex) when (IsMissing(ex))
        {
            var table = Table(e, "Section Cut", "Definitions") ?? Table(e, "Section Cut")
                ?? throw new EtabsException($"{e.Program.Name}'s API has no SectCut ({ex.Message}), and it lists no section cut table to define one in.");
            EditableTables.Put(e, table.Key, "Name", [new Dictionary<string, string> { ["Name"] = name, ["DefinedBy"] = "Group", ["Group"] = group }], "define a section cut by the group");
            return $"through the table {table.Name} ({e.Program.Name}'s API has no SectCut)";
        }
    }
}
