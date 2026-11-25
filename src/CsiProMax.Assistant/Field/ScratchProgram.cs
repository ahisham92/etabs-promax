using System.Diagnostics;
using System.Reflection;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>The field test's own window of a CSI program on its own scratch model. Tests stand in for it with a model in memory.</summary>
public interface IScratchWindow : IDisposable
{
    /// <summary>The link to the scratch window, and to no other.</summary>
    ICsiLink Link { get; }

    /// <summary>The scratch model's own file, in the test's folder.</summary>
    string ModelPath { get; }

    /// <summary>Closes the window without saving, and says in a sentence what happened.</summary>
    string Close();

    /// <summary>The titles of the windows the scratch program has open now (a message waiting for an answer is one of them).</summary>
    IReadOnlyList<string> OpenWindows() => [];
}

/// <summary>
/// The windows a scratch program puts up by itself. SAP2000 shows "Analysis Complete - (model)" after an analysis and
/// waits for OK, so run_analysis never came back in the first field test on a real SAP2000. That one window, in the
/// test's own scratch process, is closed for it. Nothing else is: a licence notice or any other message is the
/// engineer's to read and answer, and the report names it.
/// </summary>
public static class ScratchDialogs
{
    /// <summary>True for a window the test may close itself: only the notice that an analysis has finished.</summary>
    public static bool MayClose(string title) => title.TrimStart().StartsWith("Analysis Complete", StringComparison.OrdinalIgnoreCase);

    /// <summary>The visible titled windows of one process.</summary>
    public static List<(IntPtr Window, string Title)> Of(int processId)
    {
        var found = new List<(IntPtr, string)>();
        if (processId == 0 || !OperatingSystem.IsWindows()) return found;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(window)) return true;
            var text = new System.Text.StringBuilder(300);
            if (GetWindowTextW(window, text, text.Capacity) > 0) found.Add((window, text.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Asks each "analysis complete" notice of the process to close, as its own X button does. Returns their titles.</summary>
    public static List<string> CloseNotices(int processId)
    {
        var closed = new List<string>();
        foreach (var (window, title) in Of(processId))
            if (MayClose(title) && PostMessageW(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero)) closed.Add(title);
        return closed;
    }

    private delegate bool Each(IntPtr window, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(Each each, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, System.Text.StringBuilder text, int most);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
}

/// <summary>
/// A window of the CSI program that the field test starts for itself, licensed as usual, on a scratch model: never a
/// window the engineer has open. It opens the model file the Modeler wrote, saves it as a scratch model in the test's
/// folder, hands out a link to exactly that window, and closes the window without saving when the test is done.
/// Every call to the window is made on the link's one thread.
/// </summary>
public sealed class ScratchProgram : IScratchWindow
{
    /// <summary>How long the program may take to start before the test gives up on it (a licence message may be waiting).</summary>
    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>How long opening the model file, or saving the scratch model, may take.</summary>
    public static TimeSpan FileTimeout { get; set; } = TimeSpan.FromMinutes(3);

    private readonly CsiProgram _program;
    private ComEtabs? _com;
    private dynamic? _app;
    /// <summary>The window stopped answering: its thread is still waiting on it, so nothing more can be asked of it.</summary>
    private bool _left;
    private bool _closed;

    private ScratchProgram(CsiProgram program) => _program = program;

    private Timer? _watch;
    private readonly List<string> _noticesClosed = [];

    /// <summary>The titles of the windows the scratch program has open now, the main one left out.</summary>
    public IReadOnlyList<string> OpenWindows() =>
        ScratchDialogs.Of(ProcessId).Select(w => w.Title).Where(t => !t.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase) || ScratchDialogs.MayClose(t)).ToList();

    public ICsiLink Link { get; private set; } = null!;

    /// <summary>The scratch window's process, 0 when it could not be told apart.</summary>
    public int ProcessId { get; private set; }

    public string ModelPath { get; private set; } = "";

    /// <summary>The API file the program was reached through.</summary>
    public string ApiFile { get; private set; } = "";

    /// <summary>
    /// Starts the program, opens <paramref name="modelFile"/> and saves it as the scratch model. Each stage is a step
    /// in the report. Null when a stage failed: then nothing else is tried, because a change must never reach a model
    /// that is not the test's.
    /// </summary>
    public static IScratchWindow? Start(CsiProgram program, FieldContext ctx, string modelFile)
    {
        var scratch = new ScratchProgram(program);
        var ok = false;
        try
        {
            ok = scratch.Open(ctx, modelFile);
            return ok ? scratch : null;
        }
        finally
        {
            if (!ok)
            {
                // Dispose closes the window when the step could not (the part was given up on meanwhile).
                try { if (scratch._app is not null) CloseStep(ctx, scratch, program); }
                finally { scratch.Dispose(); }
            }
        }
    }

    /// <summary>Closes a scratch window as a step of the report: a window that had to be left open is a failure the tester must see.</summary>
    public static void CloseStep(FieldContext ctx, IScratchWindow window, CsiProgram program) =>
        ctx.Step($"close the scratch {program.Name} window without saving", () =>
        {
            var said = window.Close();
            FieldContext.Expect(said.Contains("closed without saving", StringComparison.Ordinal) && !said.Contains("yourself", StringComparison.Ordinal), said);
            return said;
        });

    private bool Open(FieldContext ctx, string modelFile)
    {
        var name = _program.Name;
        Assembly? loaded = null;
        if (ctx.Step($"find {name}'s API file", () =>
        {
            ApiFile = EtabsApi.FindApiFile(_program);
            FieldContext.Expect(File.Exists(ApiFile), $"{name} is not installed on this PC, or its API file was not found ({ApiFile}).");
            loaded = Assembly.LoadFrom(ApiFile);
            return $"{ApiFile}: {loaded.FullName}";
        }) is null || loaded is not { } api) return false;

        var before = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).ToHashSet();
        var com = _com = new ComEtabs(_program, () =>
        {
            var ns = EtabsApi.NamespaceOf(ApiFile);
            var helper = Activator.CreateInstance(api.GetType(ns + ".Helper", true)!)!;
            object app;
            try
            {
                app = api.GetType(ns + ".cHelper", true)!.GetMethod("CreateObjectProgID")!.Invoke(helper, [_program.ProgId])
                    ?? throw new EtabsException($"{name} gave no application object.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new EtabsException($"{name} could not be started ({inner.GetType().Name}: {inner.Message})."); }
            dynamic started = new EtabsApi(app, api.GetType(ns + ".cOAPI", true)!);
            _app = started;
            var answer = Convert.ToInt32(started.ApplicationStart());
            if (answer != 0) throw new EtabsException($"{name} refused to start (it answered {answer}).");
            return started.SapModel;
        });
        if (ctx.Step($"start a second {name} window", () =>
        {
            Ask(StartTimeout, "start", _ => null,
                $"{name} did not start within {StartTimeout.TotalMinutes:0} minutes. A licence or start-up message may be waiting in its window: answer it yourself, close that window, and run the field test again.");
            var mine = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
            ProcessId = mine.Count == 1 ? mine[0] : 0;
            // From here to the window's end, an "analysis complete" notice of this process is closed as it appears.
            if (ProcessId != 0) _watch = new Timer(_ => { try { lock (_noticesClosed) _noticesClosed.AddRange(ScratchDialogs.CloseNotices(ProcessId)); } catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next look tries again */ } }, null, 1000, 1000);
            return mine.Count == 1 ? $"{name} process {ProcessId}, through {Path.GetFileName(ApiFile)}"
                : $"{name} started through {Path.GetFileName(ApiFile)}; its process could not be told apart ({mine.Count} new {name} processes)";
        }) is null) return false;

        var target = Path.Combine(ctx.Folder, "cpm-field-test" + ModelExtension(_program));
        if (ctx.Step($"open {Path.GetFileName(modelFile)} in it", () =>
        {
            var answer = Convert.ToInt32(Ask(FileTimeout, "open the file", m => m.File.OpenFile(Path.GetFullPath(modelFile)),
                $"{name} did not finish opening the file within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            if (answer != 0) throw new EtabsException($"{name} could not open the file the Modeler wrote (it answered {answer}).");
            return "opened";
        }) is null) return false;

        if (ctx.Step("save it as the scratch model", () =>
        {
            var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            if (answer != 0) throw new EtabsException($"{name} could not save the scratch model (it answered {answer}).");
            return target;
        }) is null) return false;
        ModelPath = target;

        // What the link reads is checked against the scratch file before any change is made through it.
        if (ctx.Step("the link reads the scratch model, and no other", () =>
        {
            var link = ProgramTools.Live(com);
            var file = link.Info().File;
            FieldContext.Expect(SameName(file, target), $"the window that answered has '{Path.GetFileName(file)}' open, not the scratch model, so the test stops here.");
            Link = link;
            return Path.GetFileName(file);
        }) is null) return false;

        // The way the Tools menu reaches a window: by its process id. Checked here, not relied on.
        if (ProcessId != 0)
            ctx.Step($"reach the scratch window by its process id, as the Tools menu does", () =>
            {
                using var byProcess = new ComEtabs(ProcessId, _program);
                var file = byProcess.Info().File;
                FieldContext.Expect(SameName(file, target), $"process {ProcessId} answered with '{Path.GetFileName(file)}', not the scratch model.");
                return $"process {ProcessId} answers with {Path.GetFileName(file)}";
            });
        else ctx.Skip("reach the scratch window by its process id, as the Tools menu does", "its process could not be told apart from the other windows of the program");
        return true;
    }

    /// <summary>One call to the scratch window that may never come back (a message waiting in it): then it is left alone.</summary>
    private object? Ask(TimeSpan most, string what, Func<dynamic, object?> work, string late)
    {
        if (_left || _com is not { } com) throw new EtabsException($"The scratch {_program.Name} window stopped answering earlier.");
        var call = Task.Run(() => com.Raw(what, work));
        try
        {
            if (call.Wait(most)) return call.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw(); }
        _left = true;
        throw new EtabsException(late);
    }

    /// <summary>The same file by its name: the program may write the folder another way than Windows gave it.</summary>
    private static bool SameName(string a, string b) =>
        string.Equals(a.Replace('\\', '/').Split('/').Last(), b.Replace('\\', '/').Split('/').Last(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The program's own model file type.</summary>
    public static string ModelExtension(CsiProgram program) => program.Key switch
    {
        "sap2000" => ".sdb",
        "safe" => ".fdb",
        _ => ".edb",
    };

    /// <summary>Closes the scratch window without saving. The engineer's own windows are not this object's to close.</summary>
    public string Close()
    {
        var name = _program.Name;
        if (_app is null) return "it was not started";
        if (_closed) return $"the scratch {name} window was already closed";
        if (_left) return $"the scratch {name} window stopped answering, so it was left as it is; close it yourself without saving";
        try
        {
            Ask(TimeSpan.FromMinutes(1), "close the window", _ => { _app.ApplicationExit(false); return null; },
                $"the scratch {name} window did not close within a minute; close it yourself without saving");
            _closed = true;
            int notices;
            lock (_noticesClosed) notices = _noticesClosed.Count;
            return $"the scratch {name} window was closed without saving" + (notices > 0 ? $" ({notices} 'analysis complete' notice{(notices == 1 ? "" : "s")} of it closed along the way)" : "");
        }
        catch (EtabsException ex) { return _left ? ex.Message : $"the scratch {name} window could not be closed ({ex.Message}); close it yourself without saving"; }
    }

    public void Dispose()
    {
        if (_app is not null && !_closed && !_left) Close();
        _watch?.Dispose();
        _com?.Dispose();
    }
}
