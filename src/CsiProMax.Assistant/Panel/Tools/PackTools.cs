using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Tools;

/// <summary>
/// Makes the review pack of the open model: one .cpmx file (model, ETABS's result and design tables, the checks) and
/// the Viewer page with the pack inside it, which a reviewer without ETABS opens in any browser. Reads ETABS only.
/// </summary>
public sealed class ExportPackTool : IEtabsTool
{
    public string Name => "export_pack";

    public string Description =>
        "Make a review pack of the open model for someone who has no ETABS: the model's geometry and sections, ETABS's " +
        "own result tables (modes, base reactions, story drifts, displacements, story forces and stiffness, design " +
        "results of every kind: steel, concrete, composite, shear wall pier and spandrel, slab and punching) and the check report. It writes two files into the model's own folder, beside the model file: the " +
        "pack (.cpmx) and a Viewer page (.html) with the pack inside it (Documents\\CSI ProMax\\Made in chat only when the " +
        "model has not been saved yet or its folder cannot be written to). The reviewer opens the page in a browser, with " +
        "nothing to install: a 3D view, deformed shapes, design ratios in colour, the tables and the checks, and they " +
        "write comments and send a comments file back (read it with read_pack_comments). Run the analysis first if the " +
        "results should be in it. Give 'tables' for more ETABS tables by name (from list_tables), 'cases' and " +
        "'combinations' to limit the results, and 'drift_limit' for the drift verdict. Not a model change.";

    public string PropertiesJson => """
    {
      "name": {"type": "string", "description": "The file name without extension. Default: the model's name."},
      "tables": {"type": "array", "items": {"type": "string"}, "description": "More ETABS tables to take, by name."},
      "cases": {"type": "array", "items": {"type": "string"}},
      "combinations": {"type": "array", "items": {"type": "string"}},
      "drift_limit": {"type": "number", "description": "Allowed drift ratio for the check report, e.g. 0.002."},
      "design_limit": {"type": "number", "description": "Design ratio limit (default 1.0)."},
      "folder": {"type": "string", "description": "Another folder for the files. Leave it out: they go beside the model."},
      "design_files": {"type": "array", "items": {"type": "string"}, "description": "Table files exported from the program (File > Export > tables to Excel .xlsx, or to a text file) whose design tables fill what the program's own tables lack, for example shear wall pier and spandrel design. Files beside the model named after it are taken without asking."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args) => Make(app.Link, args, MakeDocumentTool.DefaultFolder);

    /// <summary>
    /// Where the pack goes when no folder was asked for: the model's own folder, beside the model file, so it is found
    /// where the engineer already works (ETABS, SAP2000 and SAFE alike). A model that was never saved has no folder,
    /// and a folder may be read-only (a share, an archive): then <paramref name="fallback"/>, with the reason to tell.
    /// </summary>
    internal static (string Folder, string? Why) BesideModel(CsiProMax.Assistant.Etabs.ICsiLink link, string fallback)
    {
        try { return BesideModel(link.Info().File, fallback); }
        catch (CsiProMax.Assistant.Etabs.EtabsException ex) { return (fallback, $"the model's file could not be read ({ex.Message})"); }
    }

    /// <param name="file">The model's file as the program gives it: a full path, or "" or a bare name for a model never saved.</param>
    internal static (string Folder, string? Why) BesideModel(string? file, string fallback)
    {
        file = (file ?? "").Trim().Trim('"');
        string? folder = null;
        try { if (file.Length > 0 && Path.IsPathRooted(file)) folder = Path.GetDirectoryName(file); }
        catch (ArgumentException) { }
        if (string.IsNullOrEmpty(folder)) return (fallback, "the model has not been saved yet, so it has no folder of its own");
        if (!Directory.Exists(folder)) return (fallback, $"the model's folder is not there ({folder})");
        // The only sure way to know a folder takes files: put one in it and take it out again.
        var probe = Path.Combine(folder, ".csi-promax-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return (folder, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return (fallback, $"the model's folder cannot be written to ({folder}: {ex.Message.TrimEnd('.')})");
        }
    }

    /// <param name="fallbackFolder">Where the files go when the model has no folder of its own that can be written to.</param>
    internal static ToolResult Make(CsiProMax.Assistant.Etabs.ICsiLink link, ToolArgs args, string fallbackFolder)
    {
        string folder;
        string? elsewhere = null;
        try
        {
            if (OutputPaths.Checked(args.OptString("folder")) is { Length: > 0 } asked) folder = asked.Trim().Trim('"');
            else (folder, elsewhere) = BesideModel(link, fallbackFolder);
        }
        catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }
        try
        {
            var limits = new JsonObject();
            if (args.OptDouble("drift_limit") is { } drift) limits["drift_limit"] = drift;
            if (args.OptDouble("design_limit") is { } design) limits["design_limit"] = design;
            var cases = args.StringList("cases");
            var combos = args.StringList("combinations");
            if (cases.Count > 0) limits["cases"] = new JsonArray(cases.Select(c => (JsonNode)c).ToArray());
            if (combos.Count > 0) limits["combinations"] = new JsonArray(combos.Select(c => (JsonNode)c).ToArray());
            var checks = ProgramTools.Report(JsonDocument.Parse(limits.ToJsonString()).RootElement, link).AsObject();
            var built = Pack.Build(link, args.StringList("tables"), cases, combos, checks, null, designFiles: args.StringList("design_files"));
            var model = built.Content["manifest"]!["model"]!.GetValue<string>();
            var name = OfficeDocs.SafeName((args.OptString("name") ?? "").Trim() is { Length: > 0 } n ? n : model);
            Directory.CreateDirectory(folder);
            var pack = OfficeDocs.FreePath(folder, name, ".cpmx");
            File.WriteAllBytes(pack, built.Zip);
            var page = OfficeDocs.FreePath(folder, name + " viewer", ".html");
            File.WriteAllText(page, Pack.ViewerHtml(built.Content), new UTF8Encoding(false));
            return ToolResult.Ok(new
            {
                files = new[] { page, pack },
                viewer = page, pack, folder,
                beside_model = elsewhere is null && !args.Has("folder"),
                not_beside_model = elsewhere is null ? null : $"The files are in {folder}, not beside the model: {elsewhere}. Tell the user.",
                frames = built.Frames, areas = built.Areas, tables = built.Tables,
                checks = checks["summary"],
                design = built.Content["manifest"]!["design"],
                size_kb = Math.Max(1, (int)Math.Round(new FileInfo(page).Length / 1024.0)),
                notes = built.Notes,
                shown = "The chat shows both files as cards. Say they are in the model's own folder (or where 'folder' says), and tell the user to send the viewer page (.html): it opens in any browser with the pack inside. The .cpmx is the same pack for the Viewer's Open button.",
            });
        }
        catch (Exception ex) when (ex is CsiProMax.Assistant.Etabs.EtabsException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ToolResult.Error("The pack could not be made: " + ex.Message);
        }
    }
}

/// <summary>Reads the comments a reviewer wrote in the Viewer, from the comments file or the pack they sent back.</summary>
public sealed class ReadPackCommentsTool : IEtabsTool
{
    public string Name => "read_pack_comments";

    public string Description =>
        "Read the review comments a reviewer wrote in the CSI ProMax Viewer, from the comments file they saved (.json) " +
        "or a pack (.cpmx). Each comment has who wrote it, when, the text, and the member it is on (kind, name, story) " +
        "when there is one. Then answer them one by one: select the member with select_objects, look at the model and " +
        "the results, and say what you found or changed. Not a model change.";

    public string PropertiesJson => """{"path": {"type": "string", "description": "The comments file or pack's full path."}}""";
    public string[] Required => ["path"];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args) => Read(args.OptString("path") ?? "");

    internal static ToolResult Read(string path)
    {
        path = path.Trim().Trim('"');
        if (!File.Exists(path)) return ToolResult.Error($"There is no file at {path}.");
        try
        {
            var (model, comments) = Pack.ReadComments(path);
            return new ToolResult(new JsonObject { ["model"] = model, ["count"] = comments.Count, ["comments"] = comments }.ToJsonString());
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return ToolResult.Error("The comments could not be read: " + ex.Message);
        }
    }
}
