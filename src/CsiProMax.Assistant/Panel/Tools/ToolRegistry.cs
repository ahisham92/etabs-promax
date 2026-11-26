using CsiProMax.Bugs;

namespace CsiProMax.Tools;

/// <summary>
/// The set of ETABS tools offered to Claude. Add a new capability by implementing
/// <see cref="IEtabsTool"/> and registering it here.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IEtabsTool> _tools;

    public ToolRegistry(IEnumerable<IEtabsTool> tools) =>
        _tools = tools.ToDictionary(t => t.Name);

    private void Add(IEtabsTool tool) => _tools[tool.Name] = tool;

    public IReadOnlyCollection<IEtabsTool> All => _tools.Values;

    /// <summary>A built-in tool, or a learned tool read fresh from disk so tools saved in another ETABS window work too.</summary>
    public IEtabsTool? Find(string name) => _tools.TryGetValue(name, out var tool) ? tool : LearnedTools.Load(name);

    public bool IsBuiltIn(string name) => _tools.ContainsKey(name);

    /// <param name="modeler">True in the Modeler (no ETABS): its file, story, grid and section tools are added.</param>
    /// <param name="program">The CSI program the window works in: its own tools are added (SAFE's slab tools). Left out: ETABS.</param>
    public static ToolRegistry CreateDefault(bool modeler = false, CsiProMax.Assistant.Etabs.CsiProgram? program = null)
    {
        var registry = new ToolRegistry(
    [
        // The chat's own tools: questions, jobs, memory, documents, bugs
        new AskUserTool(),
        new ChatJobsTool(),
        new ChatQuestionsTool(),
        new MemoryNoteTool(),
        new RecallChatsTool(),
        new MakeDocumentTool(),
        new ReadDocumentTextTool(),
        new ReadDrawingTool(),
        new ReadCadDrawingTool(),
        new ExportPackTool(),
        new ReadPackCommentsTool(),
        new CaptureViewImageTool(),
        new ReportBugTool(),
        new ExportChatsAndBugsTool(),
        new GetActivityLogTool(),
        new ListRecipesTool(),
        // Anything the dedicated tools don't cover
        new RunETABSQueryCodeTool(),
        new RunETABSCodeTool(),
    ]);
        // The model: read, build, load, analyse. The shared tools and the program's own, as ProgramTools.Chat gives
        // them: the self test and the field test take them from the same place.
        if (modeler)
            foreach (var tool in CsiProMax.Assistant.Tools.EtabsTools.All.Concat(CsiProMax.Assistant.Tools.ModelerTools.All).Concat(CsiProMax.Assistant.Tools.ProgramTools.Modeler).Concat(CsiProMax.Assistant.Tools.HandTools.All).Concat(CsiProMax.Assistant.Tools.TableTools.All).Concat(CsiProMax.Assistant.Tools.LateralTools.All).Concat(CsiProMax.Assistant.Tools.CaseTools.All).Concat(CsiProMax.Assistant.Tools.ModelTextTools.All).Concat(CsiProMax.Assistant.Tools.PropertyTools.All).Concat(CsiProMax.Assistant.Tools.StructureTools.All).Concat(CsiProMax.Assistant.Tools.ExtraTools.All)) registry.Add(new ModelTool(tool));
        else
            foreach (var tool in CsiProMax.Assistant.Tools.ProgramTools.Chat(program ?? CsiProMax.Assistant.Etabs.CsiProgram.Etabs)) registry.Add(new ModelTool(tool));
        // Recipes replay other tools, so they need the registry itself.
        registry.Add(new SaveRecipeTool(registry));
        registry.Add(new RunRecipeTool(registry));
        registry.Add(new SaveAsToolTool(registry));
        registry.Add(new ListLearnedToolsTool());
        registry.Add(new WorkedCodeTool());
        registry.Add(new RunLearnedToolTool());
        registry.Add(new DeleteLearnedToolTool());
        registry.Add(new RunSelfTestTool(registry));
        registry.Add(new SelfTestStatusTool());
        registry.Add(new RunFieldTestTool());
        return registry;
    }
}

/// <summary>One of the ETABS model tools (EtabsTools), in the shape the bridge, the jobs and the approvals work with.</summary>
public sealed class ModelTool : IEtabsTool
{
    private readonly CsiProMax.Assistant.Tools.Tool _tool;

    public ModelTool(CsiProMax.Assistant.Tools.Tool tool)
    {
        _tool = tool;
        using var schema = System.Text.Json.JsonDocument.Parse(tool.Schema);
        PropertiesJson = schema.RootElement.TryGetProperty("properties", out var p) ? p.GetRawText() : "{}";
        Required = schema.RootElement.TryGetProperty("required", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];
    }

    public string Name => _tool.Name;
    public string Description => _tool.Description;
    public string PropertiesJson { get; }
    public string[] Required { get; }
    public bool ModifiesModel => _tool.Ask != CsiProMax.Assistant.Tools.Ask.Never;

    /// <summary>True for the tools that always ask first (delete, save, run the analysis), whatever the approval level.</summary>
    public bool AlwaysAsks => _tool.Ask == CsiProMax.Assistant.Tools.Ask.Always;

    /// <summary>What the call will do, in a line for the approval card; null when the tool has none.</summary>
    public string? Summary(ToolArgs args)
    {
        try { return _tool.Summary?.Invoke(Json(args)); }
        catch (Exception) { return null; }
    }

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        try
        {
            return new ToolResult(_tool.Run(Json(args), app.Link).ToJsonString());
        }
        catch (CsiProMax.Assistant.Tools.ToolInputException ex) { return ToolResult.Error(ex.Message); }
        catch (CsiProMax.Assistant.Etabs.EtabsException ex) { return ToolResult.Error(ex.Message); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        {
            return ToolResult.Error($"The input could not be read ({ex.Message}). Check it against the tool's description.");
        }
    }

    private static System.Text.Json.JsonElement Json(ToolArgs args) =>
        System.Text.Json.JsonSerializer.SerializeToElement(args.Raw);
}
