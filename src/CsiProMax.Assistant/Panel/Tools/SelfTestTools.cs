using CsiProMax.Assistant;

namespace CsiProMax.Tools;

/// <summary>Checks the link to ETABS one call at a time, reading only, and says which calls worked.</summary>
public sealed class RunSelfTestTool(ToolRegistry registry) : IEtabsTool
{
    public string Name => "run_self_test";
    public string Description =>
        "Check that CSI ProMax can talk to the open ETABS model: runs each reading call once (stories, frames, " +
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

/// <summary>
/// The field test from the chat. The full test (every tool, on scratch models in windows of the programs the test opens
/// itself) runs in the background and is asked about with scope "status"; the quick one reads the open model and answers
/// at once. Either way the report is a file for the user to send back.
/// </summary>
public sealed class RunFieldTestTool : IEtabsTool
{
    public string Name => "run_field_test";
    public string Description =>
        "Run the field test and save its report for the user to send to whoever gave them CSI ProMax. scope \"full\" " +
        "(the default) tests everything: this PC and the install, the open model (reading only), every Modeler tool, then " +
        "a second window of each CSI program on this PC (ETABS, SAP2000, SAFE) opened by the test on its own scratch " +
        "model, where every tool and check is run with each change read back (the analysis and design too), CSI " +
        "ProMax's own features, and each window is closed without saving. It never changes the open model. It takes " +
        "20 to 60 minutes and runs in the background: it answers at once, and scope \"status\" says where it is and, " +
        "when it is done, what failed and which file to send. scope \"quick\" checks this PC, the install and the open " +
        "model in a minute or two. Use it when the user asks for the field test, or is asked to send one.";
    public string PropertiesJson => """
    {
      "scope": {"type": "string", "enum": ["full", "quick", "status"], "description": "full (default): everything, on scratch models, in the background. quick: this PC, the install and the open model, reading only. status: how the field test started last is doing, or what it found."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var scope = (args.OptString("scope") ?? "full").Trim().ToLowerInvariant();
        if (scope == "status") return Status();
        if (scope is not ("full" or "quick")) return ToolResult.Error($"scope is full, quick or status, not '{scope}'.");
        if (FieldTest.IsRunning || SelfTest.IsRunning)
            return ToolResult.Error("A test is already running. Call run_field_test with scope \"status\" to see where it is.");
        if (scope == "quick")
        {
            var outcome = FieldTest.Run(app.Link, changes: false, "the chat");
            return ToolResult.Ok(Found(outcome));
        }
        try
        {
            var programs = FieldTest.Installed(app.Link.Program);
            FieldTest.Start(app.Link, new FieldTest.Options { Full = true, StartedFrom = "the chat" });
            return ToolResult.Ok(new
            {
                status = "started",
                programs = programs.Count == 0 ? "none found on this PC: the tools inside the programs will be listed as not tested" : string.Join(", ", programs.Select(p => p.Name)),
                takes = "20 to 60 minutes",
                report_folder = FieldTest.Folder,
                next = "Tell the user the full field test has started and runs in the background: it opens its own windows of " +
                       "the CSI programs listed, on a test model of its own, and closes them again; they should leave those " +
                       "windows alone and can keep working in this one. Their open model is only read. Call run_field_test with " +
                       "scope \"status\" when they ask how it is going, or after a while, to give them the result and the file to send.",
            });
        }
        catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
    }

    private static ToolResult Status()
    {
        if (FieldTest.Last is not { } run)
            return ToolResult.Ok(new { status = FieldTest.IsRunning ? "running (the quick test)" : "not started in this session", hint = "Call run_field_test to start the full test." });
        if (!run.Task.IsCompleted)
            return ToolResult.Ok(new
            {
                status = "running",
                started = run.Started.ToString("HH:mm"),
                minutes_so_far = (int)(DateTime.Now - run.Started).TotalMinutes,
                steps_so_far = run.StepsSoFar,
                now_at = run.Doing,
                next = "Tell the user where it is, and that it says when it is done.",
            });
        if (run.Task.Exception is { } failed)
            return ToolResult.Error("The field test stopped with an error before it could save its report: " + (failed.InnerException ?? failed).Message);
        return ToolResult.Ok(Found(run.Task.Result));
    }

    private static object Found(FieldTest.Outcome outcome) => new
    {
        status = outcome.Failed == 0 ? "all passed" : "some failed",
        passed = outcome.Passed,
        failed = outcome.Failed,
        not_tested = outcome.NotTested,
        file_to_send = outcome.Send,
        report_file = outcome.File,
        failed_steps = outcome.Steps.Where(s => !s.Ok).Take(40).Select(s => new { part = s.Section, step = s.Name, detail = s.Detail }),
        not_tested_steps = outcome.Steps.Where(s => s.Skipped).Take(40).Select(s => new { step = s.Name, why = s.Detail }),
        next = "Tell the user the counts and what failed, and ask them to send file_to_send to whoever gave them CSI ProMax." +
               (outcome.Zip is not null ? " The report also lists a few things to check by hand (the Tools menu entry, a chat answer, and so on): ask them to try those." : ""),
    };
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
