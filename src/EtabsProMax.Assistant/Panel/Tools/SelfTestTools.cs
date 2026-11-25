using EtabsProMax.Assistant;

namespace EtabsProMax.Tools;

/// <summary>Checks the link to ETABS one call at a time, reading only, and says which calls worked.</summary>
public sealed class RunSelfTestTool(ToolRegistry registry) : IEtabsTool
{
    public string Name => "run_self_test";
    public string Description =>
        "Check that ETABS ProMax can talk to the open ETABS model: runs each reading call once (stories, frames, " +
        "areas, points, sections, materials, load patterns, cases, combinations, tables, selection) and reports " +
        "which worked. Changes nothing in the model. Use it when something seems broken, or when the user asks " +
        "whether everything works.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        if (SelfTest.IsRunning) return ToolResult.Error("A self test is already running.");
        var steps = SelfTest.Steps(app.Link, changes: false);
        return ToolResult.Ok(SelfTestStatusTool.Report(steps, DateTime.Now, registry.All.Count));
    }
}

/// <summary>What the last self test of this session found, without running it again.</summary>
public sealed class SelfTestStatusTool : IEtabsTool
{
    public string Name => "self_test_status";
    public string Description => "The result of the last self test run in this session, without running it again.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args) =>
        SelfTest.IsRunning ? ToolResult.Ok(new { status = "running" })
        : SelfTest.Last is { } last ? ToolResult.Ok(Report(last.Steps, last.At, null))
        : ToolResult.Ok(new { status = "never run", hint = "Call run_self_test." });

    internal static object Report(List<SelfTest.StepResult> steps, DateTime at, int? tools) => new
    {
        status = steps.All(s => s.Ok) ? "all passed" : "some failed",
        ran_at = at.ToString("yyyy-MM-dd HH:mm"),
        passed = steps.Count(s => s.Ok),
        failed = steps.Count(s => !s.Ok),
        tools_registered = tools,
        steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Ok ? null : s.Detail }),
    };
}
