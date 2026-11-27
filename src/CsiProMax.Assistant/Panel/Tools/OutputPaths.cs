using System.IO;
using System.Text.Json;

namespace CsiProMax.Tools;

/// <summary>
/// Checks on file paths Claude passes to the tools, and the calls that are always asked about (see ApprovalPolicy for
/// the rest of the approval rules). No ETABS API here, so the tests check it directly.
/// </summary>
internal static class OutputPaths
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Why a caller-supplied output path is refused, or null when it is fine: network shares (\\server\share),
    /// device paths (\\?\, \\.\, CON, NUL, COM1...) and alternate data streams (file.txt:stream). Local and mapped
    /// drive paths and relative names pass.
    /// </summary>
    public static string? Problem(string? path)
    {
        if (path is null) return null;
        var p = path.Trim().Trim('"');
        if (p.Length == 0) return null;
        if (p.Length >= 2 && p[0] is '\\' or '/' && p[1] is '\\' or '/')
            return "network shares (\\\\server\\...) and device paths (\\\\?\\, \\\\.\\) are not allowed; use a folder on a drive letter";
        var colon = p.IndexOf(':');
        if (colon >= 0 && !(colon == 1 && char.IsLetter(p[0]) && p.IndexOf(':', 2) < 0))
            return "a ':' is only allowed after a drive letter";
        foreach (var segment in p.Split('\\', '/'))
        {
            var name = segment.TrimEnd(' ', '.');
            var dot = name.IndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);
            if (DeviceNames.Contains(name.TrimEnd(' '))) return $"'{segment}' is a Windows device name, not a file or folder";
        }
        return null;
    }

    /// <summary>The path unchanged when it is fine (null stays null); otherwise an ArgumentException saying why.</summary>
    public static string? Checked(string? path) =>
        Problem(path) is { } why ? throw new ArgumentException($"Can't use '{path}': {why}.") : path;

    /// <summary>Tools that run or save code or recipes of tool calls (left out of the chat when its connector is too old to say it is the chat).</summary>
    public static readonly HashSet<string> CodeTools = new(StringComparer.Ordinal)
    {
        "run_etabs_query_code", "run_etabs_code", "save_recipe", "run_recipe",
    };

    /// <summary>
    /// Calls that are asked about in ETABS whatever the settings say and wherever they come from (the chat, the Claude app
    /// or a recipe), and why: a table edit (or a SAP2000 lateral code set through a table), which saves the model first.
    /// </summary>
    public static string? AlwaysAskReason(string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        // A table edit saves the model under its own file name first, so that an edit the program refuses can be undone
        // (SAP2000 26 emptied a model's load patterns on a refused edit). Ahmed, 2026-11-25: "Ask me before each save".
        if (SavesBeforeEdit(tool, args))
        {
            var table = args.TryGetValue("table", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : "a table";
            return $"CSI ProMax asks to edit the table {table}. Before the edit it saves the model under its own file name, so that an edit the program refuses can be undone by opening that file again. If you say no, the model is not saved and the table is not edited.";
        }
        if (SavesBeforeLateral(tool, args))
        {
            var code = args["code"].GetString();
            var pattern = args.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "the pattern";
            return $"CSI ProMax asks to give {pattern} the code {code}. SAP2000's API has no call for this code, so it goes in through the table 'Load Pattern Definitions'. Before that it saves the model under its own file name, so that the model can be opened again as it was if SAP2000 loses or changes any load pattern. If you say no, the model is not saved and the code is not set.";
        }
        return null;
    }

    /// <summary>A table edit that changes the model (add, update, delete): it saves the model first, so it asks at every approval level.</summary>
    public static bool SavesBeforeEdit(string tool, IReadOnlyDictionary<string, JsonElement> args) =>
        tool == "edit_table" && args.TryGetValue("action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() is "add" or "update" or "delete"
        || SavesBeforeLateral(tool, args);

    /// <summary>
    /// set_lateral_load in SAP2000 with a code its API has no Set call for (ASCE 7-22, KDS...): it imports the patterns'
    /// table, so it saves the model first and asks at every approval level, like a table edit. A code with a Set call
    /// and "None" save nothing and are not asked about here.
    /// </summary>
    public static bool SavesBeforeLateral(string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        if (tool != "set_lateral_load" || CsiProMax.Csi.Program.Key != CsiProMax.Assistant.Etabs.CsiProgram.Sap2000.Key) return false;
        if (!args.TryGetValue("code", out var c) || c.ValueKind != JsonValueKind.String || c.GetString() is not { Length: > 0 } code || code.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return false;
        var kind = args.TryGetValue("kind", out var k) && k.ValueKind == JsonValueKind.String && string.Equals(k.GetString(), "wind", StringComparison.OrdinalIgnoreCase) ? CsiProMax.Lateral.LateralCodes.Wind : CsiProMax.Lateral.LateralCodes.Seismic;
        return CsiProMax.Assistant.Tools.SapLateral.Setters(kind, code).Count == 0;
    }

    /// <summary>The path as Windows will resolve it, so the dialog shows where a relative name really lands; unchanged when it can't be resolved.</summary>
    public static string FullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return path; }
    }
}
