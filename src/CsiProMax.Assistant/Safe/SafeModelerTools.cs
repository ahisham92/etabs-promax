using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.F2k;

namespace CsiProMax.Assistant.Safe;

/// <summary>The Modeler's way to SAFE: one floor or foundation of its model written as a SAFE text file (.f2k).</summary>
public static class SafeModelerTools
{
    public static IReadOnlyList<Tool> All { get; } =
    [
        new("save_safe_file",
            "Writes one floor (or the foundation) of the Modeler's model as a SAFE text file (.f2k), for licensed SAFE to import with File > Import > SAFE .F2K file. It takes the slabs, drops and beams on that story, the columns and walls below and above it as supports (their far ends fixed), the point supports there, the uniform loads on those slabs and beams, the load patterns and the combinations. 'subgrade' puts soil (modulus of subgrade reaction, force per length cubed) under every slab, for a mat; 'strips' (default true) makes design strips along the grid lines; strips drawn with define_strip go in either way. Default story: the one with the most slab area. A file of the same name is never overwritten. The Modeler does not analyse or design: SAFE does that after the import.",
            """
            {"type":"object","properties":{
              "story":{"type":"string","description":"The floor to write; \"all\" writes one .f2k for each floor with slabs. Default: the floor with the most slabs."},
              "subgrade":{"type":"number"},
              "compression_only":{"type":"boolean","description":"Soil pushes only. Default true."},
              "strips":{"type":"boolean"},
              "name":{"type":"string","description":"File name without extension. Default: the model's title and the story."},
              "folder":{"type":"string"}}}
            """, Ask.Never, SaveSafe),
    ];

    private static JsonNode SaveSafe(JsonElement a, ICsiLink e)
    {
        if (e is not OfflineEtabs o) throw new ToolInputException("save_safe_file works in the Modeler. In ETABS, use File > Export > Story as SAFE .F2K file."); // ETABS itself
        var options = new F2kOptions(
            Story: a.Text("story") is { Length: > 0 } s && !s.Equals("all", StringComparison.OrdinalIgnoreCase) ? s : null,
            Subgrade: a.TryGetProperty("subgrade", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetDouble() : null,
            CompressionOnly: a.Flag("compression_only", true),
            Strips: a.Flag("strips", true));
        // "all": one file for each floor with slabs (SAFE takes one floor to a file).
        if (a.Text("story").Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<F2kResult> floors;
            try { floors = F2kWriter.WriteEveryFloor(o.Model, options); }
            catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
            var everyFolder = a.Text("folder") is { Length: > 0 } ff ? ff.Trim().Trim('"') : OfflineEtabs.DefaultFolder;
            Directory.CreateDirectory(everyFolder);
            var files = new JsonArray();
            foreach (var floor in floors)
            {
                var stemAll = string.Concat($"{(a.Text("name") is { Length: > 0 } nn ? nn : o.Model.Title.Length > 0 ? o.Model.Title : "Untitled")} {floor.Story}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var file = Path.Combine(everyFolder, stemAll + ".f2k");
                for (var i = 2; File.Exists(file); i++) file = Path.Combine(everyFolder, $"{stemAll} ({i}).f2k");
                File.WriteAllText(file, CsiProMax.CsiTables.EmbeddedModel.Attach(floor.Text, o.Model), new UTF8Encoding(false));
                files.Add(new JsonObject { ["file"] = file, ["story"] = floor.Story, ["slabs"] = floor.Slabs, ["beams"] = floor.Beams, ["columns"] = floor.Columns, ["loads"] = floor.Loads });
            }
            return new JsonObject { ["files"] = files, ["floors"] = floors.Count, ["next"] = "In licensed SAFE: File > Import > SAFE .F2K file, one floor at a time." };
        }
        F2kResult result;
        try { result = F2kWriter.Write(o.Model, options); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        var name = a.Text("name");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ToolInputException("'name' is a file name without a folder. Give the folder in 'folder'.");
        var folder = a.Text("folder") is { Length: > 0 } f ? f.Trim().Trim('"') : OfflineEtabs.DefaultFolder;
        Directory.CreateDirectory(folder);
        var stem = name.Length > 0 ? name : $"{(o.Model.Title.Length > 0 ? o.Model.Title : "Untitled")} {result.Story}";
        stem = string.Concat(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, stem + ".f2k");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}).f2k");
        File.WriteAllText(path, CsiProMax.CsiTables.EmbeddedModel.Attach(result.Text, o.Model), new UTF8Encoding(false));
        return new JsonObject
        {
            ["file"] = path, ["story"] = result.Story,
            ["slabs"] = result.Slabs, ["beams"] = result.Beams, ["columns"] = result.Columns, ["walls"] = result.Walls,
            ["supports"] = result.Supports, ["strips"] = result.Strips, ["loads"] = result.Loads,
            ["notes"] = new JsonArray(result.Notes.Select(n => (JsonNode)n).ToArray()),
            ["next"] = "In licensed SAFE: File > Import > SAFE .F2K file, check the supports and strips, then run the analysis and design there.",
        };
    }
}
