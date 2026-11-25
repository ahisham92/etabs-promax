using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsiProMax.Tools;

/// <summary>
/// Learned tools: when no dedicated tool fits and run_etabs_code solves a request, Claude saves that code as a new
/// named tool with its own parameters. Saved tools are listed to Claude as ordinary tools in every later chat, in every
/// ETABS window, so the add-in grows with use. Stored as JSON in %APPDATA%\CsiProMax\learned-tools, one file per
/// tool. The team shares them through a private GitHub library (<see cref="TeamLibrary"/>, Ahmed 2026-11-25: "codes will
/// be used by all of us"): a tool saved or fixed on one PC is published, and the others' tools arrive here by themselves.
/// Each tool says who saved it last and in which CSI programs it has run (<see cref="LearnedTool.Programs"/>).
/// </summary>
internal static class LearnedTools
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsiProMax", "learned-tools");

    public static readonly Regex ValidName = new("^[a-z][a-z0-9_]{2,47}$");

    public static string PathFor(string name) => PathFor(Folder, name);

    public static string PathFor(string folder, string name) => Path.Combine(folder, name + ".json");

    public static LearnedTool? Load(string name)
    {
        if (!ValidName.IsMatch(name)) return null;
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            // A copied or renamed file must not run under a name other than its own, or replace a built-in tool's name.
            var tool = LearnedTool.FromJson(File.ReadAllText(path));
            return tool.Name == name ? tool : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException) { return null; }
    }

    // Learned tools are plain files anyone can drop into the folder, so the first run of any code asks the user.
    // Approved code is remembered by its SHA-256, so editing a tool asks again.
    private static readonly object ApprovalGate = new();

    // Kept beside the learned-tools folder, not in it: copying that folder to a colleague must not carry approvals,
    // and no learned tool can be saved over it.
    public static string ApprovedFile => Path.Combine(Path.GetDirectoryName(Folder)!, "learned-tools-approved.json");

    public static string Hash(string code)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(code))).Replace("-", "").ToLowerInvariant();
    }

    public static bool IsApproved(string code)
    {
        lock (ApprovalGate) return ReadApproved().Contains(Hash(code));
    }

    public static void MarkApproved(string code)
    {
        lock (ApprovalGate)
        {
            var approved = ReadApproved();
            if (!approved.Add(Hash(code))) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ApprovedFile)!);
                var temp = ApprovedFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(approved.OrderBy(h => h, StringComparer.Ordinal)));
                if (File.Exists(ApprovedFile)) File.Replace(temp, ApprovedFile, null);
                else File.Move(temp, ApprovedFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* asked again next time */ }
        }
    }

    private static HashSet<string> ReadApproved()
    {
        try
        {
            if (File.Exists(ApprovedFile))
                return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ApprovedFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    public static List<LearnedTool> All()
    {
        var list = new List<LearnedTool>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
        {
            var tool = Load(Path.GetFileNameWithoutExtension(file));
            if (tool is not null) list.Add(tool);
        }
        return list.OrderBy(t => t.Name).ToList();
    }

    /// <summary>The saved tools as far as matching a task to them goes; none on any trouble.</summary>
    public static List<WorkedCode.ReadyTool> Ready()
    {
        try { return All().Select(t => new WorkedCode.ReadyTool(t.Name, t.Summary, t.Uses)).ToList(); }
        catch (Exception) { return []; }
    }

    /// <summary>The ready-made tools and kept code that do what <paramref name="what"/> says (bug 129); "" when none.</summary>
    public static string ExistingFor(string what) => WorkedCode.JobHintFor(what, Ready());

    /// <summary>Converts JSON arguments to plain .NET values a snippet can use without System.Text.Json.</summary>
    public static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        _ => null,
    };

    /// <summary>
    /// True when the code opens its own transactions (or an edit scope), so it must run without the one the runner
    /// would open around it: inside an open transaction ETABS refuses "Starting a new transaction is not permitted"
    /// (2026-10-28: two saved join tools failed six times this way).
    /// </summary>
    public static bool OpensOwnTransactions(string code) => OwnTransaction.IsMatch(code);

    private static readonly Regex OwnTransaction = new(@"new\s+(Autodesk\.ETABS\.DB\.)?(Transaction|TransactionGroup)\s*\(|EditScope\b"); // ETABS itself
}

/// <summary>One saved tool. The MCP server reads the same file to list it, so the JSON shape is shared.</summary>
public sealed class LearnedTool : IEtabsTool
{
    public string Name { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string Code { get; private set; } = "";
    public string PropertiesJson { get; private set; } = "{}";
    public string[] Required { get; private set; } = [];
    public bool ModifiesModel { get; private set; }
    /// <summary>The code starts and commits its own transactions; the run is still one Undo step (a group).</summary>
    public bool OwnTransactions { get; private set; }
    public int Uses { get; private set; }
    public DateTime Saved { get; private set; }
    /// <summary>The GitHub login of whoever saved this code ("" when the PC was not signed in to the team library).</summary>
    public string Author { get; private set; } = "";
    /// <summary>
    /// The CSI programs (CsiProgram.Key: etabs, sap2000, safe) this code has run in without an error. The three share
    /// most of their API, so a tool made in ETABS is offered in SAP2000 and SAFE too, with a note until it has run there.
    /// Empty for tools saved before 0.4.16: nothing is said about them.
    /// </summary>
    public string[] Programs { get; private set; } = [];

    public string Description => BaseDescription + Bridge.ConnectorTools.ProgramNote(Programs, Csi.Program.Key);

    /// <summary>The description without the note on programs, which each reader adds in its own program's words (the file is shared by all three).</summary>
    private string BaseDescription => "[Learned tool" + (Author.Length > 0 ? ", by " + Author : "") + "] " + Summary;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        // The bridge asks before calling; this covers every other way in (recipes, for one).
        if (!LearnedTools.IsApproved(Code) && !Bridge.BridgeServer.Approve(this, args.Raw))
            return ToolResult.Error($"The user did not approve running the learned tool '{Name}'. Ask them how to proceed.");
        var values = args.Raw.ToDictionary(p => p.Key, p => LearnedTools.Plain(p.Value));
        // The same time cap as run_etabs_code, so a saved tool can't keep ETABS frozen either.
        var result = ETABSCodeToolBase.RunScript(app, Code, Name.Replace('_', ' '), ModifiesModel, values, OwnTransactions,
            ETABSCodeToolBase.CapSeconds);
        if (!result.IsError) CountUse(app);
        else result = result with
        {
            Text = result.Text + $"\nThis is the learned tool '{Name}'. Fix it: write corrected code with run_etabs_code, " +
                   "and once it works, save it again with save_as_tool under the same name.",
        };
        return result;
    }

    private void CountUse(EtabsApp app)
    {
        try
        {
            if (!LearnedTools.ValidName.IsMatch(Name)) return;
            Uses++;
            // It worked in this program: said in the tool, and published, so the team sees it works here too.
            var program = ProgramOf(app);
            var learnedHere = Programs.Length > 0 && !Programs.Contains(program, StringComparer.OrdinalIgnoreCase);
            if (learnedHere) Programs = [.. Programs, program];
            File.WriteAllText(LearnedTools.PathFor(Name), ToJson());
            if (learnedHere) TeamLibrary.Soon();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the count is only informative */ }
    }

    /// <summary>The program this run went to: the link's own (the field test's scratch window of another program), else the window's.</summary>
    internal static string ProgramOf(EtabsApp? app)
    {
        try { if (app?.Link?.Program is { } p) return p.Key; } catch (Exception) { }
        return Csi.Program.Key;
    }

    /// <summary>This tool with <paramref name="programs"/> in place of its own (the team library merges two PCs' lists).</summary>
    internal LearnedTool WithPrograms(IEnumerable<string> programs)
    {
        var copy = (LearnedTool)MemberwiseClone();
        copy.Programs = programs.Select(p => p.ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return copy;
    }

    /// <summary>This tool as saved at <paramref name="saved"/> (the team library's own check sets the order of two saves).</summary>
    internal LearnedTool WithSaved(DateTime saved)
    {
        var copy = (LearnedTool)MemberwiseClone();
        copy.Saved = saved;
        return copy;
    }

    /// <summary>This tool with the use count of the copy it replaces on this PC (the count is this PC's own).</summary>
    internal LearnedTool WithUses(int uses)
    {
        var copy = (LearnedTool)MemberwiseClone();
        copy.Uses = uses;
        return copy;
    }

    public static LearnedTool Create(string name, string summary, string code, string propertiesJson, string[] required,
        bool modifiesModel, int uses, bool? ownTransactions = null, string author = "", string[]? programs = null) => new()
    {
        Name = name, Summary = summary, Code = code, PropertiesJson = propertiesJson, Required = required,
        ModifiesModel = modifiesModel, Uses = uses, Saved = DateTime.Now,
        OwnTransactions = ownTransactions ?? LearnedTools.OpensOwnTransactions(code),
        Author = author ?? "",
        Programs = (programs ?? []).Select(p => p.ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray(),
    };

    /// <summary>
    /// What the team library holds for this tool: everything but this PC's own use count and the description (which
    /// is made from the summary in the words of the program that reads it). The same tool always gives the same text,
    /// on any PC and in any time zone, so its hash says whether it changed.
    /// </summary>
    public string SharedJson()
    {
        using var props = JsonDocument.Parse(PropertiesJson);
        return JsonSerializer.Serialize(new
        {
            name = Name,
            summary = Summary,
            modifiesModel = ModifiesModel,
            ownTransactions = OwnTransactions,
            inputSchema = new { type = "object", properties = props.RootElement, required = Required },
            code = Code,
            author = Author,
            programs = Programs,
            saved = SavedUtc,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>When it was saved, in UTC to the second: the same text on every PC.</summary>
    internal string SavedUtc => (Saved.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(Saved, DateTimeKind.Utc) : Saved)
        .ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public string ToJson()
    {
        using var props = JsonDocument.Parse(PropertiesJson);
        return JsonSerializer.Serialize(new
        {
            name = Name,
            description = BaseDescription + (ModifiesModel ? " (Changes the model.)" : ""),
            summary = Summary,
            modifiesModel = ModifiesModel,
            ownTransactions = OwnTransactions,
            inputSchema = new { type = "object", properties = props.RootElement, required = Required },
            code = Code,
            author = Author,
            programs = Programs,
            uses = Uses,
            saved = SavedUtc,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static LearnedTool FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var schema = r.GetProperty("inputSchema");
        var name = r.GetProperty("name").GetString() ?? "";
        if (!LearnedTools.ValidName.IsMatch(name)) throw new FormatException($"'{name}' is not a valid learned tool name.");
        return new LearnedTool
        {
            Name = name,
            Summary = r.GetProperty("summary").GetString() ?? "",
            Code = r.GetProperty("code").GetString()!,
            ModifiesModel = r.GetProperty("modifiesModel").GetBoolean(),
            // Tools saved before 0.35.43 have no flag: tell from the code.
            OwnTransactions = r.TryGetProperty("ownTransactions", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? o.GetBoolean() : LearnedTools.OpensOwnTransactions(r.GetProperty("code").GetString() ?? ""),
            PropertiesJson = schema.TryGetProperty("properties", out var p) ? p.GetRawText() : "{}",
            Required = schema.TryGetProperty("required", out var q) && q.ValueKind == JsonValueKind.Array
                ? q.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
            Uses = r.TryGetProperty("uses", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0,
            Saved = r.TryGetProperty("saved", out var s) && s.ValueKind == JsonValueKind.String && s.TryGetDateTime(out var d) ? d : DateTime.MinValue,
            Author = r.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString() ?? "" : "",
            Programs = r.TryGetProperty("programs", out var pr) && pr.ValueKind == JsonValueKind.Array
                ? pr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.ToLowerInvariant())
                    .Where(x => x.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : [],
        };
    }
}

public sealed class SaveAsToolTool : IEtabsTool
{
    public string Name => "save_as_tool";
    public string Description =>
        $"Save working {Csi.Name} API code as a new reusable tool, so next time the request has a dedicated tool. Use it after " +
        "run_etabs_code or run_etabs_query_code solved something no existing tool covers and that is likely to come up " +
        "again: generalise the code so whatever changes between uses (names, levels, sizes, ids) comes from 'args', a " +
        "dictionary of the tool's inputs (args[\"width_mm\"] is a long, double, string, bool, List<object> or " +
        "Dictionary<string, object>; use Convert.ToDouble etc.). Saving under an existing learned tool's name replaces it, " +
        "which is how a learned tool is fixed or improved. The code is compiled before saving.";
    public string PropertiesJson => $$$"""
    {
      "name": {"type": "string", "description": "snake_case, 3 to 48 characters, verb first, e.g. renumber_parking_spaces"},
      "description": {"type": "string", "description": "What the tool does and when to use it, written for Claude choosing tools in a later chat. Mention units."},
      "code": {"type": "string", "description": "C# method body, as for run_etabs_code, reading its inputs from args. Must end with a return statement."},
      "parameters": {"type": "object", "description": "JSON Schema 'properties' for the inputs, e.g. {\"level\": {\"type\": \"string\", \"description\": \"Level name\"}}"},
      "required": {"type": "array", "items": {"type": "string"}},
      "changes_model": {"type": "boolean", "description": "True when the code changes the model. It then runs behind the same approval as run_etabs_code. {{{Csi.Name}}} has no undo."}
    }
    """;
    public string[] Required => ["name", "description", "code", "changes_model"];
    public bool ModifiesModel => false;

    private readonly ToolRegistry _registry;
    public SaveAsToolTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var name = args.String("name").Trim();
        if (!LearnedTools.ValidName.IsMatch(name))
            return ToolResult.Error("The name must be snake_case: lowercase letters, digits and underscores, 3 to 48 characters, starting with a letter.");
        if (_registry.IsBuiltIn(name))
            return ToolResult.Error($"'{name}' is a built-in tool. Choose another name.");

        var code = args.String("code");
        var (run, errors) = ScriptCompiler.Compile(code);
        if (run is null) return ToolResult.Error("The code did not compile, so it was not saved:\n" + errors);

        var properties = args.Has("parameters") ? args.Raw["parameters"] : default;
        if (properties.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            return ToolResult.Error("'parameters' must be a JSON Schema properties object.");
        var propertiesJson = properties.ValueKind == JsonValueKind.Object ? properties.GetRawText() : "{}";
        var names = properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Select(p => p.Name).ToHashSet() : [];
        var required = args.StringList("required");
        var missing = required.Where(r => !names.Contains(r)).ToList();
        if (missing.Count > 0) return ToolResult.Error($"Required inputs not described in 'parameters': {string.Join(", ", missing)}");

        var previous = LearnedTools.Load(name);
        // The programs it is known to run in: this one (its code just worked here), plus the earlier list when the code
        // is the same (only the description or inputs changed).
        var here = LearnedTool.ProgramOf(app);
        var programs = previous is not null && previous.Code == code ? previous.Programs.Append(here) : [here];
        var tool = LearnedTool.Create(name, args.String("description"), code, propertiesJson, required.ToArray(),
            args.OptBool("changes_model"), previous?.Uses ?? 0, args.Has("own_transactions") ? args.OptBool("own_transactions") : (bool?)null,
            TeamLibrary.Login, programs.ToArray());
        Directory.CreateDirectory(LearnedTools.Folder);
        File.WriteAllText(LearnedTools.PathFor(name), tool.ToJson());
        var shared = TeamLibrary.Shares(name);
        if (shared) TeamLibrary.Soon();
        return ToolResult.Ok(new
        {
            saved = name,
            replaced = previous is not null,
            file = LearnedTools.PathFor(name),
            team = shared ? $"Published to the team library ({TeamLibrary.Repo}) within a minute, so the team has it too." : null,
            note = "The tool is available now and in every later chat. Tell the user in one line that you saved it" + (shared ? " and that the team gets it too." : "."),
        });
    }
}

public sealed class ListLearnedToolsTool : IEtabsTool
{
    public string Name => "list_learned_tools";
    public string Description =>
        "List the tools Claude has learned (saved with save_as_tool): name, description, inputs, how often each was used, " +
        "and optionally the code. Use it when the user asks what has been learned, before editing one, or when a learned " +
        "tool does not appear in the tool list yet (call it through run_learned_tool).";
    public string PropertiesJson => """
    {
      "include_code": {"type": "boolean"},
      "name": {"type": "string", "description": "Only this tool"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var withCode = args.OptBool("include_code");
        var only = args.OptString("name");
        var tools = LearnedTools.All().Where(t => only is null || t.Name == only).Select(t => new
        {
            name = t.Name,
            description = t.Summary,
            changesModel = t.ModifiesModel,
            inputs = JsonDocument.Parse(t.PropertiesJson).RootElement,
            required = t.Required,
            uses = t.Uses,
            saved = t.Saved,
            by = t.Author.Length > 0 ? t.Author : null,
            ranIn = t.Programs.Length > 0 ? t.Programs : null,
            code = withCode ? t.Code : null,
        }).ToList();
        return ToolResult.Ok(new { tools, folder = LearnedTools.Folder, team = TeamLibrary.StatusLine() });
    }
}

public sealed class RunLearnedToolTool : IEtabsTool
{
    public string Name => "run_learned_tool";
    public string Description =>
        "Run a learned tool by name with its inputs. Normally learned tools appear as their own tools; use this when one " +
        "was saved in this chat but the Claude app has not refreshed its tool list yet.";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "arguments": {"type": "object", "description": "The tool's inputs"}
    }
    """;
    public string[] Required => ["name"];
    // Approval for model changes is asked by the bridge, which resolves this call to the learned tool itself.
    public bool ModifiesModel => false;

    /// <summary>
    /// The chat's calls never get here: the bridge runs the learned tool itself, with its own approval and transaction.
    /// A call from anywhere else (the field test, a recipe) runs a learned tool that only reads; one that changes the
    /// model is sent back to the chat, where it is approved. Before 0.4.8 every such call said there was no such tool.
    /// </summary>
    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var name = args.String("name");
        if (LearnedTools.Load(name) is not { } learned) return ToolResult.Error($"No learned tool named '{name}'. Call list_learned_tools.");
        if (learned.ModifiesModel)
            return ToolResult.Error($"The learned tool '{name}' changes the model, so it runs from the chat, where the change is approved first.");
        var inner = args.Raw.TryGetValue("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>();
        return learned.Execute(app, new ToolArgs(inner));
    }
}

public sealed class DeleteLearnedToolTool : IEtabsTool
{
    public string Name => "delete_learned_tool";
    public string Description => "Delete a learned tool the user no longer wants. Only learned tools can be deleted, never built-in ones. " +
        "A tool shared through the team library is taken out of the library too (its history stays on GitHub), so ask first when someone else made it.";
    public string PropertiesJson => """{"name": {"type": "string"}}""";
    public string[] Required => ["name"];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var name = args.String("name");
        if (LearnedTools.Load(name) is null) return ToolResult.Error($"No learned tool named '{name}'. Call list_learned_tools.");
        File.Delete(LearnedTools.PathFor(name));
        var shared = TeamLibrary.Shares(name);
        if (shared) TeamLibrary.Soon();
        return ToolResult.Ok(new { deleted = name, team = shared ? "Taken out of the team library at the next sync, within a minute." : null });
    }
}
