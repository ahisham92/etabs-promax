using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.S2k;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>The Modeler's way to SAP2000: the model saved as a SAP2000 text file (.s2k).</summary>
public static class SapModelerTools
{
    public static IReadOnlyList<Tool> All { get; } =
    [
        new("save_model_s2k",
            "Saves the Modeler's model as a SAP2000 text file (.s2k), which licensed SAP2000 opens with File > Import > SAP2000 .s2k Text File. SAP2000 has no stories: members become joints, frames and areas in 3D, stories become Z grid lines and groups, and rigid diaphragms become a diaphragm constraint per story. Default folder: Documents\\CSI ProMax\\Models. The answer says what was left out.",
            """{"type":"object","properties":{"name":{"type":"string","description":"File name without extension. Default: the model's title."},"folder":{"type":"string"}}}""",
            Ask.Always, Save, a => $"Save the model as {(a.Text("name").Length > 0 ? a.Text("name") : "its title")}.s2k for SAP2000"),
    ];

    private static JsonNode Save(JsonElement a, ICsiLink e)
    {
        var offline = e as OfflineEtabs
            ?? throw new ToolInputException("save_model_s2k works in the Modeler. With SAP2000 open, save the model in SAP2000 itself.");
        var name = a.Text("name");
        if (name.Length == 0) name = offline.Model.Title.Length > 0 ? offline.Model.Title : "Untitled";
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '-' : c).ToArray()).Trim();
        var folder = a.Text("folder");
        if (folder.Length == 0) folder = OfflineEtabs.DefaultFolder;
        var path = Path.Combine(folder, name + ".s2k");
        S2kWriter.Result result;
        try
        {
            Directory.CreateDirectory(folder);
            result = S2kWriter.WriteFile(offline.Model, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new EtabsException($"The model could not be saved to {path}: {ex.Message}");
        }
        return new JsonObject
        {
            ["saved"] = path, ["joints"] = result.Joints, ["frames"] = result.Frames, ["areas"] = result.Areas,
            ["left_out"] = new JsonArray(result.Notes.Take(60).Select(n => (JsonNode)n).ToArray()),
            ["next"] = "In SAP2000: File > Import > SAP2000 .s2k Text File, choose this file, then check the model and run the analysis there.",
        };
    }
}
