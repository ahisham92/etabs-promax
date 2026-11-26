using System.Diagnostics;
using System.Reflection;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Safe;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

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

    /// <summary>Puts back the program's plug-in lists the scratch window emptied. Returns the settings files put back.</summary>
    IReadOnlyList<string> PutBackPlugIns() => [];
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

    /// <summary>
    /// Answers each "analysis complete" notice of the process the way a person does: by its own OK button, and only
    /// when the program lets that button be pressed. Returns their titles.
    /// </summary>
    /// <remarks>
    /// Never by closing the window (its X): the analysis windows of ETABS and SAP2000 carry the same title while the
    /// program still works, and closing one made ETABS ask "Program is active. Do you really want to close the
    /// program?" in the middle of its analysis, and left SAP2000 with no analysis window ("Invoke or BeginInvoke cannot
    /// be called on a control until the window handle has been created") in the second full field test of 0.4.9
    /// (2026-11-25). A notice with no OK button to press yet is left alone and looked at again a second later.
    /// </remarks>
    public static List<string> CloseNotices(int processId, CsiProgram? program = null)
    {
        var closed = new List<string>();
        foreach (var (window, title) in Of(processId))
        {
            if (MayClose(title) && Button(window, "OK") is { } ok && IsWindowEnabled(ok) && PostMessageW(ok, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero)) closed.Add(title);
            else if (program is not null && IsProgramBox(title, program) && Button(window, "OK") is { } fine && IsWindowEnabled(fine)
                     && LongWords(window).FirstOrDefault(IsFileRefused) is { } said && PostMessageW(fine, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                closed.Add($"{title}: {said.Trim()} (OK)");
            else if (IsMaintenanceReminder(title, Words(window))) closed.AddRange(AnswerMaintenanceReminder([processId]));
            else if (IsStoryPicker(title) && Button(window, "OK") is { } go && IsWindowEnabled(go))
            {
                // All stories first, then OK: the two presses reach the window in this order.
                if (Button(window, "Select All") is { } all && IsWindowEnabled(all)) PostMessageW(all, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                if (PostMessageW(go, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero)) closed.Add(title + " (all stories, OK)");
            }
            else if (IsImportLog(title) && Button(window, "Done") is { } done && IsWindowEnabled(done))
            {
                // Its errors are read before Done is pressed, so the report says what the import did not take.
                var errors = ImportErrors(LongWords(window));
                if (PostMessageW(done, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                    closed.Add(title + " (Done" + (errors.Count > 0 ? "; its errors: " + string.Join(" | ", errors) : "") + ")");
            }
        }
        return closed;
    }

    /// <summary>
    /// True for the story picker ETABS 23 opens when the slab design is started through the API ("Select Stories for
    /// Slab Design", seen 2026-11-25: run_design waited on it for ten minutes). In the test's own scratch window the
    /// test answers it: all stories, OK.
    /// </summary>
    /// <summary>A message box of the program itself: titled with its name alone ("SAFE").</summary>
    public static bool IsProgramBox(string title, CsiProgram program) => string.Equals(title.Trim(), program.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The program's word that it could not read a file it was given ("Error decompressing file. May not be a valid
    /// ETABS .EDB file.", SAFE 20 given an .f2k, 2026-11-26). In the test's own scratch window it is answered OK, so
    /// the call comes back and the step can say why. Never a licence message.
    /// </summary>
    public static bool IsFileRefused(string text) =>
        text.Contains("Error decompressing file", StringComparison.OrdinalIgnoreCase) && !text.Contains("licen", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SAP2000's "Maintenance Expiring" reminder ("License Available. Session Created. Maintenance expiring on
    /// 5-Jul-2019."), shown at every start on Ahmed's PC. It says a licence WAS given, so the test answers it itself
    /// (Ahmed, 2026-11-26: "find a way that this maintenance expiring not to wait for me please"): "Do not show this
    /// message until next expiration period", then OK. Any other licence message (none free, an error) is never this.
    /// </summary>
    public static bool IsMaintenanceReminder(string title, IEnumerable<string> words)
    {
        var all = string.Join(" ", words);
        return title.Trim().Equals("Maintenance Expiring", StringComparison.OrdinalIgnoreCase)
            && all.Contains("License Available", StringComparison.OrdinalIgnoreCase) && all.Contains("Maintenance expiring on", StringComparison.OrdinalIgnoreCase)
            && !all.Contains("in use", StringComparison.OrdinalIgnoreCase) && !all.Contains("not available", StringComparison.OrdinalIgnoreCase)
            && !all.Contains("Status ", StringComparison.OrdinalIgnoreCase) && !all.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The choice the test makes on the reminder: not again until the next expiration period.</summary>
    public static bool IsNotUntilNextPeriod(string label) =>
        label.Replace("&", "").Contains("until next expiration period", StringComparison.OrdinalIgnoreCase);

    /// <summary>Answers SAP2000's maintenance reminder in the given processes (the test's own): returns what it answered.</summary>
    public static List<string> AnswerMaintenanceReminder(IEnumerable<int> processIds)
    {
        var answered = new List<string>();
        foreach (var id in processIds)
            foreach (var (window, title) in Of(id))
            {
                if (!IsMaintenanceReminder(title, Words(window)) || Button(window, "OK") is not { } ok || !IsWindowEnabled(ok)) continue;
                IntPtr? later = null;
                EnumChildWindows(window, (child, _) =>
                {
                    var label = new System.Text.StringBuilder(200);
                    if (GetWindowTextW(child, label, label.Capacity) > 0 && IsNotUntilNextPeriod(label.ToString()) && IsWindowVisible(child)) { later = child; return false; }
                    return true;
                }, IntPtr.Zero);
                if (later is { } choice) PostMessageW(choice, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                if (PostMessageW(ok, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                    answered.Add(title.Trim() + (later is null ? " (OK)" : " (not until the next expiration period, OK)"));
            }
        return answered;
    }

    public static bool IsStoryPicker(string title) => title.TrimStart().StartsWith("Select Stories for Slab Design", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the import log SAP2000 shows when it opens a .s2k it had something to say about ("Access Database Import
    /// Log", with Done). Seen on Ahmed's PC on 2026-11-26: the 0.4.36 run opened the Modeler's cpm-field-test.s2k (1 error,
    /// 13 warnings) and the log waited all night for a person. In the test's own scratch window the test reads its errors
    /// and presses Done.
    /// </summary>
    public static bool IsImportLog(string title)
    {
        var t = title.Trim();
        return t.EndsWith("Import Log", StringComparison.OrdinalIgnoreCase) && (t.Contains("Database", StringComparison.OrdinalIgnoreCase) || t.Contains("Import Log", StringComparison.OrdinalIgnoreCase))
            && !t.Contains("licen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The error lines of an import log's text (not its counts), at most ten.</summary>
    public static List<string> ImportErrors(IEnumerable<string> texts) =>
        texts.SelectMany(t => t.Split('\n')).Select(l => l.Trim())
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Number of errors", StringComparison.OrdinalIgnoreCase)
                && !l.StartsWith("Import Control for Errors", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+\s+errors?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !l.Equals("Error", StringComparison.OrdinalIgnoreCase) && !l.Equals("Error or Warning", StringComparison.OrdinalIgnoreCase))
            .Distinct().Take(10).ToList();

    /// <summary>The whole texts of a window's child controls, read with WM_GETTEXT so a long log comes whole.</summary>
    private static List<string> LongWords(IntPtr window)
    {
        var words = new List<string>();
        if (!OperatingSystem.IsWindows()) return words;
        EnumChildWindows(window, (child, _) =>
        {
            var text = new System.Text.StringBuilder(200000);
            if (SendMessageTimeoutW(child, 0x000D /* WM_GETTEXT */, new IntPtr(text.Capacity), text, 2 /* SMTO_ABORTIFHUNG */, 2000, out _) != IntPtr.Zero && text.Length > 0)
                words.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return words;
    }

    /// <summary>The label of a button as a person reads it: without the "&amp;" of its access key.</summary>
    public static bool IsButton(string label, string text) => string.Equals(label.Replace("&", "").Trim(), text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The window's own visible button with this text, or null.</summary>
    private static IntPtr? Button(IntPtr window, string text)
    {
        IntPtr? found = null;
        if (!OperatingSystem.IsWindows() || window == IntPtr.Zero) return null;
        EnumChildWindows(window, (child, _) =>
        {
            var label = new System.Text.StringBuilder(64);
            if (GetWindowTextW(child, label, label.Capacity) > 0 && IsButton(label.ToString(), text) && IsWindowVisible(child))
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// True for the one question the test answers itself: the program asking whether it may really close ("Program is
    /// active. Do you really want to close the program?"), which it shows when the test closes its own scratch window
    /// while the program is still busy. Told by the question's own words; a licence message is never this.
    /// </summary>
    public static bool IsCloseQuestion(IEnumerable<string> words) =>
        words.Any(w => w.Contains("Program is active", StringComparison.OrdinalIgnoreCase) && w.Contains("close the program", StringComparison.OrdinalIgnoreCase));

    /// <summary>The words on a window: the texts of its child controls.</summary>
    public static List<string> Words(IntPtr window)
    {
        var words = new List<string>();
        if (!OperatingSystem.IsWindows()) return words;
        EnumChildWindows(window, (child, _) =>
        {
            var text = new System.Text.StringBuilder(600);
            if (GetWindowTextW(child, text, text.Capacity) > 0) words.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return words;
    }

    /// <summary>
    /// Answers the "really close the program?" question of the process, as its own buttons do: Yes while the test is
    /// closing its own scratch window, No at any other time (the test goes on in that window). Returns how many were answered.
    /// </summary>
    public static int AnswerCloseQuestion(int processId, bool yes)
    {
        var answered = 0;
        foreach (var (window, _) in Of(processId))
            if (IsCloseQuestion(Words(window)) && PostMessageW(window, 0x0111 /* WM_COMMAND */, new IntPtr(CloseAnswer(yes)), IntPtr.Zero)) answered++;
        return answered;
    }

    /// <summary>The button the test presses on the close question: Yes (6) only while it is closing the window itself, else No (7).</summary>
    public static int CloseAnswer(bool closing) => closing ? 6 : 7;

    /// <summary>What Windows says of a window: " (cannot be clicked: another window of the program holds it)", " (not answering)", or nothing.</summary>
    public static string State(IntPtr window)
    {
        if (!OperatingSystem.IsWindows()) return "";
        var said = new List<string>();
        if (!IsWindowEnabled(window)) said.Add("cannot be clicked: the program has it disabled");
        if (IsHungAppWindow(window)) said.Add("not answering");
        return said.Count == 0 ? "" : " (" + string.Join(", ", said) + ")";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
    private delegate bool Each(IntPtr window, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(Each each, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, Each each, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, System.Text.StringBuilder text, int most);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr w, System.Text.StringBuilder l, uint flags, uint timeout, out IntPtr result);
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

    /// <summary>How long closing the scratch window may take before the test gives up on it.</summary>
    public static TimeSpan CloseTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long opening the model file, or saving the scratch model, may take.</summary>
    public static TimeSpan FileTimeout { get; set; } = TimeSpan.FromMinutes(3);

    private readonly CsiProgram _program;
    private ComEtabs? _com;
    private dynamic? _app;
    /// <summary>The pipe to a .NET Framework program's scratch window (Windows PowerShell holds it), else null.</summary>
    private ApiPipe? _pipe;
    /// <summary>The window stopped answering: its thread is still waiting on it, so nothing more can be asked of it.</summary>
    private bool _left;
    private bool _closed;
    private Dictionary<string, string> _plugIns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings files whose plug-in list the scratch window had emptied, and that were put back.</summary>
    public IReadOnlyList<string> PlugInListsPutBack { get; private set; } = [];

    public IReadOnlyList<string> PutBackPlugIns()
    {
        if (_plugIns.Count == 0) return PlugInListsPutBack;
        var back = KeptPlugIns.PutBack(_plugIns);
        if (back.Count > 0) PlugInListsPutBack = PlugInListsPutBack.Concat(back).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return PlugInListsPutBack;
    }

    /// <summary>The test is closing its own window: only then is the program's "really close?" question answered.</summary>
    private volatile bool _closing;
    private volatile bool _closeAnswered;
    /// <summary>How often the program asked whether to really close while the test was still working in it, and the test answered No.</summary>
    private int _keptOpen;
    public int KeptOpen => _keptOpen;

    private ScratchProgram(CsiProgram program) => _program = program;

    /// <summary>When this test asked the program to start: a process older than that is never the test's.</summary>
    private DateTime _startedAt;
    private Timer? _watch;
    private readonly List<string> _noticesClosed = [];

    /// <summary>The titles of the windows the scratch program has open now, each with whether it can be clicked and whether it answers.</summary>
    /// A window other than the program's main one also gives its words (what a message box says), so a box that held the
    /// scratch window (ETABS 20 opening a checkpoint, 0.4.53) can be answered by the test next time.
    public IReadOnlyList<string> OpenWindows() =>
        ScratchDialogs.Of(ProcessId).Select(w => w.Title + ScratchDialogs.State(w.Window) + Said(w.Window)).ToList();

    private static string Said(IntPtr window)
    {
        try
        {
            var words = string.Join(" / ", ScratchDialogs.Words(window).Where(x => x.Trim().Length > 0).Take(8));
            return words.Length == 0 ? "" : " saying: " + (words.Length > 300 ? words[..300] + "..." : words);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

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

        // SAFE 20 and 21 (on the .NET Framework) open only their own .fdb through the API: given the Modeler's .f2k,
        // SAFE 20 showed "Error decompressing file. May not be a valid ETABS .EDB file." and waited for a person
        // (tester's PC, 0.4.47, 2026-11-26). Such a program starts a blank model instead, and the test builds the
        // Modeler's model in it through the API (F2kRebuild), so the editing tools are still tried (Ahmed, 2026-11-26).
        var build = !OpensThroughApi(_program, modelFile, EtabsApi.RunsOnFramework(ApiFile));
        if (build && !BuildsThroughApi(_program, modelFile))
        {
            ctx.Fail($"start a second {name} window", $"{name} on this PC ({Path.GetFileName(Path.GetDirectoryName(ApiFile)) ?? name}) opens only its own {ModelExtension(_program)} files through its API, " +
                $"and the Modeler writes {Path.GetExtension(modelFile)}. To check the Modeler's file by hand: in {name}, File > Import, pick {Path.GetFileName(modelFile)} from the zip. The test has no way yet to build this file through the API.");
            return false;
        }

        // The program's plug-in lists as they are before the scratch window: put back when it has closed (KeptPlugIns).
        _plugIns = KeptPlugIns.Take(_program);
        var before = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).ToHashSet();
        _startedAt = DateTime.Now;
        var com = _com = new ComEtabs(_program, () =>
        {
            var ns = EtabsApi.NamespaceOf(ApiFile);
            dynamic started;
            if (EtabsApi.RunsOnFramework(ApiFile))
            {
                // ETABS 21, SAP2000 25, SAFE 21 and older: their Helper starts the program through .NET Remoting, which
                // .NET 8 does not have ("gave no application object" for SAFE 20, ETABS 20 and SAP2000 24, 2026-11-26).
                // Windows PowerShell starts it with the same Helper, and the calls go over the plug-in's pipe.
                try { _pipe = ApiPipe.ToPowerShell(_program, ApiFile, "start", null); }
                catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new EtabsException($"{name} could not be started ({inner.GetType().Name}: {inner.Message})."); }
                started = _pipe.Root();
            }
            else
            {
                var helper = Activator.CreateInstance(api.GetType(ns + ".Helper", true)!)!;
                object app;
                try
                {
                    app = api.GetType(ns + ".cHelper", true)!.GetMethod("CreateObjectProgID")!.Invoke(helper, [_program.ProgId])
                        ?? throw new EtabsException($"{name} gave no application object.");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new EtabsException($"{name} could not be started ({inner.GetType().Name}: {inner.Message})."); }
                started = new EtabsApi(app, api.GetType(ns + ".cOAPI", true)!);
            }
            _app = started;
            var answer = Convert.ToInt32(started.ApplicationStart());
            if (answer != 0) throw new EtabsException($"{name} refused to start (it answered {answer}).");
            return started.SapModel;
        });
        if (ctx.Step($"start a second {name} window", () =>
        {
            // While it starts, the maintenance reminder of a new process of this program (the test's own) is answered.
            using var starting = new Timer(_ =>
            {
                try
                {
                    var fresh = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
                    var said = ScratchDialogs.AnswerMaintenanceReminder(fresh);
                    if (said.Count > 0) lock (_noticesClosed) _noticesClosed.AddRange(said);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next look tries again */ }
            }, null, 1000, 1000);
            Ask(StartTimeout, "start", _ => null,
                $"{name} did not start within {StartTimeout.TotalMinutes:0} minutes. A licence or start-up message may be waiting in its window: answer it yourself, close that window, and run the field test again.");
            var mine = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
            ProcessId = mine.Count == 1 ? mine[0] : 0;
            // From here to the window's end, an "analysis complete" notice of this process is closed as it appears.
            if (ProcessId != 0) _watch = new Timer(_ => { try { lock (_noticesClosed) _noticesClosed.AddRange(ScratchDialogs.CloseNotices(ProcessId, _program)); var closing = _closing; if (ScratchDialogs.AnswerCloseQuestion(ProcessId, closing) > 0) { if (closing) _closeAnswered = true; else Interlocked.Increment(ref _keptOpen); } } catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next look tries again */ } }, null, 1000, 1000);
            return mine.Count == 1 ? $"{name} process {ProcessId}, through {Path.GetFileName(ApiFile)}"
                : $"{name} started through {Path.GetFileName(ApiFile)}; its process could not be told apart ({mine.Count} new {name} processes)";
        }) is null) return false;

        // A name of this run's own: two tests on one PC (it happened) must never take each other's window for theirs.
        var target = Path.Combine(ctx.Folder, ScratchName() + ModelExtension(_program));
        var text = build ? File.ReadAllText(modelFile) : "";
        if (build)
        {
            if (ctx.Step($"start a blank model in it ({name} on this PC opens only {ModelExtension(_program)} files through its API, so the Modeler's {Path.GetExtension(modelFile)} is built in it instead)", () =>
            {
                var units = F2kRebuild.UnitsOf(text);
                Ask(FileTimeout, "start a blank model", m =>
                {
                    object model = m;
                    ApiCall.Must(model, "InitializeNewModel", CsiTools.D(("Units", units)), "start a new model");
                    return ApiCall.First(model, ["File.NewBlank", "File.NewGridOnly"], CsiTools.D(("NumberStorys", 1), ("TypicalStoryHeight", 3), ("BottomStoryHeight", 3), ("NumberLinesX", 2), ("NumberLinesY", 2), ("SpacingX", 6), ("SpacingY", 6)), "start a blank model");
                }, $"{name} did not finish starting a blank model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window.");
                return $"blank, in {ComEtabs.UnitNames[units - 1]}";
            }) is null) return false;
        }
        else
        if (ctx.Step($"open {Path.GetFileName(modelFile)} in it", () =>
        {
            var answer = Convert.ToInt32(Ask(FileTimeout, "open the file", m => m.File.OpenFile(Path.GetFullPath(modelFile)),
                $"{name} did not finish opening the file within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            string[] refused;
            lock (_noticesClosed) refused = _noticesClosed.Where(n => ScratchDialogs.IsFileRefused(n)).ToArray();
            if (answer != 0 || refused.Length > 0)
                throw new EtabsException($"{name} could not open the file the Modeler wrote (it answered {answer})." + (refused.Length > 0 ? $" It said: {string.Join(" | ", refused)}; the test pressed OK." : ""));
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

        // The Modeler's table data (material damping, notes, time dependence, ...) goes into ETABS through its tables, as
        // open_model does for a Modeler .e2k, and the model is saved again.
        if (!build && _program.Key == CsiProgram.Etabs.Key && ModelerTables.ModelerFileBeside(modelFile) is { } cpm
            && ModelFile.Load(cpm).Materials.Any(x => x.Tables is not null)
            && ctx.Step("put the Modeler's material data that the .e2k has no keyword for in through ETABS's tables", () => // ETABS itself
            {
                var (done, refused) = ModelerTables.Apply(Link, cpm);
                var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                    $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
                FieldContext.Expect(refused.Count == 0, "refused: " + string.Join(" | ", refused));
                return string.Join("; ", done);
            }) is null && _left) return false;

        // The Modeler's model, built in the blank model through the API, and saved: what the program's part then checks.
        if (build && ctx.Step($"build the Modeler's model from {Path.GetFileName(modelFile)} in it through the API", () =>
        {
            var built = F2kRebuild.Build(Link, text);
            ctx.Facts["safe.built"] = 1;
            var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            if (answer != 0) throw new EtabsException($"{name} could not save the scratch model after building it (it answered {answer}).");
            return built;
        }) is null) return false;

        // SAFE's API reads design strips and has no call to draw them: they go in through its strip table. A strip
        // table SAFE does not take fails this step only; the rest of SAFE's part still runs on the model built above.
        if (build && ctx.Step($"put the Modeler's design strips in through {name}'s table '{F2kRebuild.StripTable}' (its API cannot draw strips)", () =>
        {
            var strips = F2kRebuild.BuildStrips(Link, text);
            ctx.Facts["safe.strips_built"] = strips;
            var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            if (answer != 0) throw new EtabsException($"{name} could not save the scratch model after the strips (it answered {answer}).");
            return $"{name} lists {strips} design strips";
        }) is null && _left) return false;

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

    /// <summary>The scratch model's name: "cpm-field-test-" and six characters no other run has.</summary>
    public static string ScratchName() => "cpm-field-test-" + Guid.NewGuid().ToString("N")[..6];

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

    /// <summary>
    /// The same model by its name: the program may write the folder another way than Windows gave it, and it may name
    /// its working copy beside the model instead of the model (ETABS 23 answers "name.$et" while "name.edb" is open),
    /// so the file type is left out. The scratch name is this run's alone (<see cref="ScratchName"/>).
    /// </summary>
    internal static bool SameName(string a, string b) =>
        string.Equals(Path.GetFileNameWithoutExtension(a.Replace('\\', '/').Split('/').Last()), Path.GetFileNameWithoutExtension(b.Replace('\\', '/').Split('/').Last()), StringComparison.OrdinalIgnoreCase)
        && a.Trim().Length > 0;

    /// <summary>The program's own model file type.</summary>
    /// <summary>
    /// Whether the program can open <paramref name="file"/> through its API's File.OpenFile. Its own model always;
    /// a text file (.e2k, .s2k, .f2k) too, except in SAFE on the .NET Framework (SAFE 20 and 21), which reads every
    /// file as a packed .fdb and stops on a message box.
    /// </summary>
    public static bool OpensThroughApi(CsiProgram program, string file, bool onFramework) =>
        !(program.Key == CsiProgram.Safe.Key && onFramework && !string.Equals(Path.GetExtension(file), ModelExtension(program), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the test can build <paramref name="file"/>'s model through the API in a blank model of a program that
    /// cannot open it (<see cref="OpensThroughApi"/>): a SAFE text file (.f2k), by <see cref="F2kRebuild"/>.
    /// </summary>
    public static bool BuildsThroughApi(CsiProgram program, string file) =>
        program.Key == CsiProgram.Safe.Key && string.Equals(Path.GetExtension(file), ".f2k", StringComparison.OrdinalIgnoreCase);

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
        if (_left) return Said($"the scratch {name} window stopped answering, so it was left as it is; close it yourself without saving");
        _closing = true;
        try
        {
            // Three minutes: in the 0.4.28 full test SAP2000 26 took over a minute to close its scratch window after the
            // whole test (the test had given up and named its process, which had ended by the time anyone looked).
            Ask(CloseTimeout, "close the window", _ => { _app.ApplicationExit(false); return null; },
                $"the scratch {name} window did not close within {CloseTimeout.TotalMinutes:0} minutes; close it yourself without saving");
            _closed = true;
            // The program said yes to closing: its process must go too, or it keeps a licence with no window to show for it.
            if (!Gone(TimeSpan.FromSeconds(30))) return Said($"the scratch {name} window was told to close, and its process did not end; end it yourself");
            int notices;
            List<string> titles;
            lock (_noticesClosed) { notices = _noticesClosed.Count; titles = [.. _noticesClosed.GroupBy(t => t).Select(g => g.Count() == 1 ? g.Key : $"{g.Key}, {g.Count()} times")]; }
            return $"the scratch {name} window was closed without saving" + (_closeAnswered ? " (it asked whether to really close, and the test answered Yes)" : "") + (_keptOpen > 0 ? $" (earlier it asked {_keptOpen} time{(_keptOpen == 1 ? "" : "s")} whether to close while the test was working in it, and the test answered No)" : "") + (notices > 0 ? $" (windows of it the test answered itself along the way: {string.Join("; ", titles)})" : "");
        }
        // Whatever the program or the link throws (a window that is no longer there, a call that faulted on another
        // thread): closing says what happened and never stops the test, whose report must still be written.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var why = (ex is AggregateException { InnerException: { } inner } ? inner : ex).Message;
            return Said(_left ? why : $"the scratch {name} window could not be closed ({why}); close it yourself without saving");
        }
    }

    /// <summary>What happened, with what became of a process that stayed: ended by the test, or left for the engineer.</summary>
    private string Said(string what)
    {
        var after = StillRunning();
        if (!after.Contains("the test ended it", StringComparison.Ordinal)) return what + after;
        _closed = true;
        return $"the scratch {_program.Name} window stopped answering" + after;
    }

    /// <summary>True when the scratch process has ended (or could not be told apart, so nothing is known of it).</summary>
    private bool Gone(TimeSpan wait)
    {
        if (ProcessId == 0) return true;
        try { using var process = Process.GetProcessById(ProcessId); return process.WaitForExit((int)wait.TotalMilliseconds); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    /// <summary>
    /// A scratch process that is still there after its window was left or told to close: the test ends it (Ahmed's
    /// choice, 2026-11-25: "end only its own test copy"), so no licence stays held by a program with no window. Only
    /// the process this object started, and only when <see cref="MayEnd"/> says it can be no one else's. Otherwise
    /// the engineer is told its process id and how to end it.
    /// </summary>
    private string StillRunning()
    {
        if (Gone(TimeSpan.Zero)) return "";
        var hasWindow = false;
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            var titles = ScratchDialogs.Of(ProcessId).Select(w => w.Title).ToList();
            hasWindow = titles.Count > 0;
            if (string.Equals(process.ProcessName, _program.ProcessName, StringComparison.OrdinalIgnoreCase) && MayEnd(process.StartTime, _startedAt, titles))
            {
                process.Kill();
                if (process.WaitForExit(15000))
                    return $". Its process (process id {ProcessId}) no longer answered, so the test ended it: this scratch {_program.Name} was closed without saving and holds no licence now";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* said below */ }
        return Gone(TimeSpan.Zero) ? "" : LeftRunning(_program.Name, _program.ProcessName, ProcessId, hasWindow);
    }

    /// <summary>
    /// True when a process can only be the test's own scratch program: it started after the test asked for it, and
    /// every window it shows is the scratch model's (or it shows none). A window that names anything else keeps it alive.
    /// </summary>
    public static bool MayEnd(DateTime processStarted, DateTime testAsked, IEnumerable<string> titles) =>
        testAsked != default && processStarted >= testAsked.AddSeconds(-2)
        && titles.All(t => t.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the windows of a process show it is a test copy an earlier run left open: one of them names a scratch
    /// model ("cpm-field-test-" and its six characters), and every other one is a window the test answers itself (the
    /// analysis notice, the story picker, the import log). A window that names anything else keeps the process alive.
    /// </summary>
    public static bool IsLeftCopy(IEnumerable<string> titles)
    {
        var all = titles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return all.Any(t => t.Contains("cpm-field-test-", StringComparison.OrdinalIgnoreCase))
            && all.All(t => t.Contains("cpm-field-test-", StringComparison.OrdinalIgnoreCase) || ScratchDialogs.MayClose(t) || ScratchDialogs.IsStoryPicker(t) || ScratchDialogs.IsImportLog(t));
    }

    /// <summary>
    /// Ends the test copies an earlier run left open (a run that gave up on a window, as the 0.4.10 run did on ETABS 23
    /// with the story picker showing): they hold a licence, and they keep CSI ProMax out of that program's Tools menu
    /// ("ETABS 23 is open"). Only a process <see cref="IsLeftCopy"/> names; it is ended without saving (Ahmed's choice,
    /// 2026-11-25: the test may end "only its own test copy"). Returns one line for each.
    /// </summary>
    public static List<string> EndLeftCopies()
    {
        var said = new List<string>();
        if (!OperatingSystem.IsWindows()) return said;
        foreach (var program in CsiProgram.All)
        {
            Process[] running;
            try { running = Process.GetProcessesByName(program.ProcessName); }
            catch (InvalidOperationException) { continue; }
            foreach (var process in running)
                using (process)
                    try
                    {
                        var titles = ScratchDialogs.Of(process.Id).Select(w => w.Title).ToList();
                        if (!IsLeftCopy(titles)) continue;
                        var name = titles.First(t => t.Contains("cpm-field-test-", StringComparison.OrdinalIgnoreCase));
                        process.Kill();
                        said.Add(process.WaitForExit(15000) ? $"'{name}' (process id {process.Id}) was closed without saving" : $"'{name}' (process id {process.Id}) was told to end and is still there");
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* not ours to end, or already gone */ }
        }
        return said;
    }

    public static string LeftRunning(string name, string processName, int processId, bool hasWindow) =>
        $". Its process is still running ({processName}.exe, process id {processId}, {(hasWindow ? "with a window" : "with no window on the screen")}) and holds a {name} licence until it ends: " +
        $"{(hasWindow ? "close that window without saving, or " : "")}end the {processName}.exe with process id {processId} in Task Manager (Details tab). It is the test's own scratch model; nothing of yours is in it";

    /// <summary>
    /// Takes the program's window off the screen (its API's Hide), for CSI ProMax Engine's runs, which use the program
    /// as a solver in the background (Ahmed 2026-11-26). False when the program does not hide; the run goes on either way.
    /// </summary>
    public bool Hide()
    {
        if (_app is null || _closed || _left) return false;
        try
        {
            Ask(TimeSpan.FromSeconds(30), "hide the window", _ => { _app.Hide(); return null; }, $"the {_program.Name} window did not hide within 30 seconds");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    public void Dispose()
    {
        // Letting go never throws: the report is written after it.
        try { if (_app is not null && !_closed && !_left) Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { _watch?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { PutBackPlugIns(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { _com?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { _pipe?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}

/// <summary>
/// A scratch window must leave the program's plug-in lists as it found them. ETABS 23 and SAP2000 26, started through
/// the API, rewrite their settings file with an empty [PlugIn] section (NumberPlugIns=0): the field test of 0.4.14 on
/// Ahmed's PC emptied SAP2000 26's list as its scratch window started (19:29:42), and a scratch ETABS 23 closed with
/// ApplicationExit emptied ETABS 23's (19:18:05), so Tools &gt; CSI ProMax was gone from both. The lists are read before
/// the window starts and put back, the [PlugIn] section only, once it has closed.
/// </summary>
public static class KeptPlugIns
{
    /// <summary>Each settings file of the program (every version on the PC) with its [PlugIn] section as it is now.</summary>
    public static Dictionary<string, string> Take(CsiProgram program, string? settingsRoot = null)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = settingsRoot ?? ToolsMenu.SettingsRoot;
        try
        {
            if (!Directory.Exists(root)) return kept;
            foreach (var folder in Directory.GetDirectories(root, program.Name + " *"))
                foreach (var ini in Directory.GetFiles(folder, "*.ini"))
                    if (FieldTest.PlugInSection(File.ReadAllText(ini, System.Text.Encoding.Latin1)) is { } section)
                        kept[ini] = section;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return kept;
    }

    /// <summary>Puts back each [PlugIn] section that changed since <see cref="Take"/>. Returns the files put back.</summary>
    public static List<string> PutBack(IReadOnlyDictionary<string, string> kept)
    {
        var back = new List<string>();
        foreach (var (ini, section) in kept)
            try
            {
                if (!File.Exists(ini)) continue;
                var text = File.ReadAllText(ini, System.Text.Encoding.Latin1);
                if (FieldTest.PlugInSection(text) is not { } now || now == section) continue;
                if (Replace(text, section) is not { } fixedText) continue;
                File.WriteAllText(ini, fixedText, System.Text.Encoding.Latin1);
                back.Add(ini);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return back;
    }

    /// <summary>The settings text with its [PlugIn] section replaced by <paramref name="section"/>, in the file's own line ends.</summary>
    internal static string? Replace(string text, string section)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(l => l.Trim().Equals("[PlugIn]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        // The section ends with its last line of text: blank lines after it stay where they were.
        while (end - 1 > start && lines[end - 1].Trim().Length == 0) end--;
        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, section.Split('\n'));
        var joined = string.Join("\n", lines);
        return crlf ? joined.Replace("\n", "\r\n") : joined;
    }
}
