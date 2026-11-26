using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test: what a person runs on a PC that has licensed CSI programs, so that what cannot be checked without
/// them is checked there, and one report comes back that says what works and what does not.
///
/// The quick test reads the open model (the self test's steps and the result checks) and checks this PC and the
/// install. The full test adds every tool: it builds a model in the Modeler with every Modeler tool, opens a second
/// window of each CSI program on this PC on that model (a scratch model in the test's own folder), runs every tool and
/// check of that program there with each change read back, runs CSI ProMax's own features, and closes each window
/// without saving. The engineer's open model is only ever read.
///
/// Help > Field test, the run_field_test tool ("run the field test" in the chat), or CsiProMax --fieldtest [--full].
/// </summary>
public static class FieldTest
{
    /// <summary>The report's part for the open model.</summary>
    public const string OpenSection = "The open model (reading only)";

    /// <summary>The report's part for the scratch windows when none could be opened.</summary>
    public const string ScratchSection = "The CSI programs, on scratch models";

    /// <summary>How the field test is run.</summary>
    public sealed record Options
    {
        /// <summary>True: every tool, on scratch models (the full test). False: the open model, reading only.</summary>
        public bool Full { get; init; }

        /// <summary>The quick test only: also adds test objects to the open model (a scratch model). The full test never changes the open model.</summary>
        public bool Changes { get; init; }

        /// <summary>Where the person started it: "the Help menu", "the chat", "the command line".</summary>
        public string StartedFrom { get; init; } = "the chat";

        /// <summary>Where the report goes. Left out: <see cref="FieldTest.Folder"/>.</summary>
        public string? Folder { get; init; }

        /// <summary>The full test's own folder (the scratch models, the files it makes). Left out: one in %LOCALAPPDATA%\CsiProMax\Field test.</summary>
        public string? WorkFolder { get; init; }

        public DateTime? At { get; init; }

        /// <summary>For tests: stands in for %LOCALAPPDATA%\Computers and Structures.</summary>
        public string? SettingsRoot { get; init; }

        /// <summary>The folder CsiProMax.dll is in. Left out: this program's own.</summary>
        public string? InstallFolder { get; init; }

        /// <summary>The programs to open a scratch window of. Left out: every CSI program on this PC, the open one first.</summary>
        public IReadOnlyList<CsiProgram>? Programs { get; init; }

        /// <summary>
        /// One program only (Ahmed, 2026-11-26: "test sap etabs safe one by one"): this PC, the open model and that program's
        /// scratch window with CSI ProMax's own features in it. The Modeler's part still runs: it writes the scratch model that
        /// program opens. The other programs are left out.
        /// </summary>
        public CsiProgram? OnlyProgram { get; init; }

        /// <summary>Told the test's own folder once it is known (its "results so far.txt" grows as the test goes).</summary>
        public Action<string>? OnWorkFolder { get; init; }

        /// <summary>Opens a scratch window (for tests, a stand-in): null when it could not, with its steps in the report.</summary>
        public Func<CsiProgram, FieldContext, string, IScratchWindow?> StartScratch { get; init; } = ScratchProgram.Start;
        /// <summary>Ends the test copies an earlier run left open, and says which. For tests: a stand-in.</summary>
        public Func<List<string>> EndLeftCopies { get; init; } = ScratchProgram.EndLeftCopies;
        /// <summary>
        /// Only what is new, changed or failed: a program's part, or CSI ProMax's own features, is left out when all of
        /// it passed on this PC and its code is unchanged since (see <see cref="FieldRetest"/>). With <see cref="Full"/>.
        /// </summary>
        public bool Retest { get; init; }
        /// <summary>Where each part's last result is kept. Left out: this PC's own file, and nothing for a stand-in run.</summary>
        public string? ResultsFile { get; init; }
        /// <summary>For tests: the value that stands for a part's code. Left out: from the source hashes in this build.</summary>
        public Func<string, string>? Print { get; init; }
        /// <summary>For the tests: the file that says a full test is running, instead of this PC's own.</summary>
        public string? LockFile { get; init; }

        /// <summary>For tests: stands in for starting CSI ProMax a second time (true: with the AI chat). Left out: the real start, in the full test on a real PC.</summary>
        public Func<bool, WindowFieldSteps.Seen>? OpenWindow { get; init; }

        /// <summary>True when a CSI program is behind the scratch windows, so code is run against its API (tests: false).</summary>
        public bool LiveScratch { get; init; } = true;

        /// <summary>Told each step's name as it starts.</summary>
        public Action<string>? Progress { get; init; }
        /// <summary>Called with each step's outcome as it is recorded: "ok ...", "FAIL ...: why", "not tested ...: why".</summary>
        public Action<string>? Outcome { get; init; }

        /// <summary>The most one program's part may take before the test gives up on it and goes on.</summary>
        /// <summary>Open the Viewer in a browser without a window and check its screen, for each program's scratch model.</summary>
        public bool Viewer { get; init; } = true;
        public TimeSpan ScratchLimit { get; init; } = TimeSpan.FromMinutes(45);
    }

    /// <summary>What a field test found, and the files it saved.</summary>
    /// <param name="File">The report.</param>
    /// <param name="Zip">The full test's zip beside the report: the report and the small files the test made. The file to send.</param>
    /// <param name="WorkFolder">The full test's own folder, which can be deleted afterwards.</param>
    public sealed record Outcome(string File, IReadOnlyList<FieldStep> Steps, string Report, string? Zip = null, string? WorkFolder = null)
    {
        public int Failed => Steps.Count(s => !s.Ok);
        public int NotTested => Steps.Count(s => s.Skipped);
        public int Passed => Steps.Count(s => s.Ok && !s.Skipped);

        /// <summary>The file the person sends back: the zip when there is one, else the report.</summary>
        public string Send => Zip ?? File;

        /// <summary>The result in a few lines, for a dialog: the counts, what did not work, and where the file to send is.</summary>
        public string Summary()
        {
            var bad = Steps.Where(s => !s.Ok).ToList();
            var counts = Counts(Passed, Failed, NotTested);
            var text = bad.Count == 0
                ? $"Field test: {counts}."
                : $"Field test: {counts}. Not working:\n" +
                  string.Join("\n", bad.Take(12).Select(s => $"- {s.Name}: {Short(s.Detail, 160)}")) + (bad.Count > 12 ? $"\n- and {bad.Count - 12} more, in the report" : "");
            return text + "\n\nThe report is saved here. Please send this file to whoever gave you CSI ProMax:\n" + Send;
        }
    }

    /// <summary>Documents\CSI ProMax\Bugs\Field tests: beside the bug reports, and taken by Export chats and bugs.</summary>
    public static string Folder => Path.Combine(global::CsiProMax.Bugs.BugLog.Root, "Field tests");

    private static int _running;

    /// <summary>True while a field test runs (one at a time).</summary>
    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The quick test, as before: the open model (reading only unless <paramref name="changes"/>), this PC and the install.</summary>
    public static Outcome Run(ICsiLink link, bool changes, string startedFrom, string? folder = null, DateTime? at = null, string? settingsRoot = null) =>
        Run(link, new Options { Changes = changes, StartedFrom = startedFrom, Folder = folder, At = at, SettingsRoot = settingsRoot });

    public static Outcome Run(ICsiLink link, Options o)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("A field test is already running.");
        FileStream? only = null;
        try
        {
            // One full test at a time on a PC, whoever starts it: two at once opened windows of the same program
            // seconds apart and worked in each other's (Ahmed's PC, 2026-11-25).
            if (o.Full) only = OnlyOne(o.LockFile);
            return RunCore(link, o);
        }
        finally { only?.Dispose(); Volatile.Write(ref _running, 0); }
    }

    /// <summary>
    /// What a report is named for: what the test covered, never the window it was started from. The full test covers
    /// every installed program ("all programs"), or the one it was asked for; the quick test reads the open model, so
    /// it is named for that model's program. (A tester's full test, started from SAFE's window, tested ETABS 20 and
    /// was named SAFE: 2026-11-26.)
    /// </summary>
    internal static string ReportName(CsiProgram opened, Options o) => !o.Full ? opened.Name : o.OnlyProgram is { } only ? only.Name : "all programs";

    /// <summary>True while a full field test runs on this PC, in this window or any other process (it holds the lock file).</summary>
    public static bool RunningOnThisPc(string? file = null)
    {
        if (IsRunning) return true;
        file ??= DefaultLockFile;
        if (!File.Exists(file)) return false;
        try { using var free = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The file a full field test holds open while it runs, so a second one on the same PC is refused.</summary>
    public static string DefaultLockFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "field-test.lock");

    internal static FileStream OnlyOne(string? file)
    {
        file ??= DefaultLockFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Another full field test is already running on this PC (from another CSI ProMax window, or from a command line). " +
                                                "Two at once would work in each other's test windows, so this one was not started. Wait for the other to end, then run it again.");
        }
    }

    /// <summary>A field test that runs in the background (the full one takes a while): what it is at, and its outcome when done.</summary>
    public sealed class Running
    {
        internal Running(bool full, string startedFrom, bool retest = false, CsiProgram? only = null)
        {
            Full = full; StartedFrom = startedFrom; Retest = retest; Only = only;
            Expected = ExpectedSteps(full, retest, only: only);
        }
        /// <summary>The one program it tests, or null for all of them.</summary>
        public CsiProgram? Only { get; }
        /// <summary>The live file the test appends each step's outcome to, as it goes ("results so far.txt").</summary>
        public string? LiveFile { get; internal set; }
        public bool Full { get; }
        public bool Retest { get; }
        public string StartedFrom { get; }
        public DateTime Started { get; } = DateTime.Now;
        public Task<Outcome> Task { get; internal set; } = null!;
        private volatile string _doing = "starting";
        private volatile string _part = "";
        private int _steps, _ok, _failed, _notTested;
        public string Doing => _doing;
        public int StepsSoFar => Volatile.Read(ref _steps);
        /// <summary>The part the last recorded step belongs to ("SAP2000 on a scratch model"...).</summary>
        public string Part => _part;
        /// <summary>Steps recorded so far, by outcome (Ahmed, 2026-11-26: the field test "needs urgently to show progress").</summary>
        public int Ok => Volatile.Read(ref _ok);
        public int Failed => Volatile.Read(ref _failed);
        public int NotTested => Volatile.Read(ref _notTested);
        public int Done => Ok + Failed + NotTested;
        /// <summary>About how many steps this kind of run had last time on this PC; 0 when it never ran here.</summary>
        public int Expected { get; }
        internal void Saw(string step) { _doing = step; Interlocked.Increment(ref _steps); }
        /// <summary>Counts one outcome line ("ok          [part] step", "FAIL        [part] ...", "not tested  [part] ...").</summary>
        internal void Counted(string line)
        {
            if (line.StartsWith("ok", StringComparison.Ordinal)) Interlocked.Increment(ref _ok);
            else if (line.StartsWith("FAIL", StringComparison.Ordinal)) Interlocked.Increment(ref _failed);
            else if (line.StartsWith("not tested", StringComparison.Ordinal)) Interlocked.Increment(ref _notTested);
            var open = line.IndexOf('[');
            var close = open < 0 ? -1 : line.IndexOf(']', open + 1);
            if (close > open + 1) _part = line[(open + 1)..close];
        }
    }

    /// <summary>A running test's progress as lines (the progress window and the chat both show them).</summary>
    public sealed record ProgressLines(string Part, string Step, double? Percent, string Count, string Tally, string Time)
    {
        public static ProgressLines Of(string part, string doing, int done, int expected, int ok, int failed, int notTested, TimeSpan elapsed, bool finished)
        {
            // Never 100% before it is done: the last run's count is only an estimate.
            double? percent = finished ? 100 : expected > 0 ? Math.Min(99, 100.0 * done / expected) : null;
            var count = expected > 0 ? $"Step {done} of about {expected}" : $"Step {done} (the first run of this kind on this PC, so the total is not known yet)";
            var tally = $"Passed {ok}   Failed {failed}   Not tested {notTested}";
            var time = $"Running for {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            return new ProgressLines(part.Length > 0 ? part : "Starting", "Now: " + doing, percent, count, tally, time);
        }

        public static ProgressLines Of(Running run) =>
            Of(run.Part, run.Doing, run.Done, run.Expected, run.Ok, run.Failed, run.NotTested, DateTime.Now - run.Started, run.Task?.IsCompleted == true);
    }

    /// <summary>The file with how many steps the last full run and the last retest had on this PC.</summary>
    private static string ExpectedFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "Field test", "steps.txt");

    private static string ExpectedKey(bool full, bool retest, CsiProgram? only = null) => (!full ? "quick" : retest ? "retest" : "full") + (only is null ? "" : "-" + only.Key);

    internal static int ExpectedSteps(bool full, bool retest, string? file = null, CsiProgram? only = null)
    {
        try
        {
            foreach (var line in File.ReadAllLines(file ?? ExpectedFile))
                if (line.Split('=') is [var k, var v] && k.Trim() == ExpectedKey(full, retest, only) && int.TryParse(v.Trim(), out var n) && n > 0) return n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }
        return 0;
    }

    internal static void SaveExpected(bool full, bool retest, int steps, string? file = null, CsiProgram? only = null)
    {
        try
        {
            var path = file ?? ExpectedFile;
            var lines = File.Exists(path) ? File.ReadAllLines(path).Where(l => !l.StartsWith(ExpectedKey(full, retest, only) + "=", StringComparison.Ordinal)).ToList() : [];
            lines.Add($"{ExpectedKey(full, retest, only)}={steps}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }
    }

    /// <summary>The last field test started in the background in this session (the chat and the Help menu both see it).</summary>
    public static Running? Last { get; private set; }

    /// <summary>Starts a field test on its own thread and returns at once. Refused while one runs.</summary>
    public static Running Start(ICsiLink link, Options o)
    {
        if (IsRunning || Last is { Task.IsCompleted: false }) throw new InvalidOperationException("A field test is already running.");
        var run = new Running(o.Full, o.StartedFrom, o.Retest, o.OnlyProgram);
        var mine = o with
        {
            Progress = step => { run.Saw(step); o.Progress?.Invoke(step); },
            Outcome = line => { run.Counted(line); o.Outcome?.Invoke(line); },
            OnWorkFolder = folder => { run.LiveFile = Path.Combine(folder, FieldContext.LiveFile); o.OnWorkFolder?.Invoke(folder); },
        };
        run.Task = System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            var outcome = Run(link, mine);
            if (o.ResultsFile is null) SaveExpected(o.Full, o.Retest, outcome.Steps.Count, only: o.OnlyProgram);
            return outcome;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Last = run;
        return run;
    }

    private static Outcome RunCore(ICsiLink link, Options o)
    {
        var now = o.At ?? DateTime.Now;
        // The report's folder is fixed first: the test points the bug folder at its own while it checks the bug report.
        var target = o.Folder ?? Folder;
        var work = o.WorkFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "Field test", $"{link.Program.Name} {now:yyyy-MM-dd HHmmss}");
        if (o.Full) Directory.CreateDirectory(work);
        var ctx = new FieldContext(link.Program, work, link) { Progress = o.Progress, Outcome = o.Outcome };
        try { o.OnWorkFolder?.Invoke(work); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        // Test copies an earlier run left open go first: they would count as the program being open below.
        var left = new List<string>();
        if (o.Full && o.SettingsRoot is null)
            try { left = o.EndLeftCopies(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { left.Add($"could not be looked for ({ex.Message})"); }
        // Tools > CSI ProMax in each program: put right where it may be, as at every start, then read as it stands.
        IReadOnlyList<ToolsMenu.Entry> menus = [];
        try { menus = ToolsMenu.Repair(o.InstallFolder, o.SettingsRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the lists below still show what is there */ }
        var plugIns = PlugInLists(o.SettingsRoot);

        PcFieldSteps.Run(ctx, o.InstallFolder ?? AppContext.BaseDirectory, plugIns, o.Full ? [target, work] : [target], menus);
        // With the AI chat and without it (install-no-ai.ps1): both ways, whichever this PC has.
        AiModeFieldSteps.Run(ctx);
        // The Modeler and the Viewer in CSI ProMax windows of their own, never a browser tab.
        WebWindowFieldSteps.Run(ctx);
        // The Modeler from its desktop shortcut: it opens, and a second double-click is answered (2026-11-26).
        ModelerStartFieldSteps.Run(ctx, o.Full && (o.LiveScratch && o.SettingsRoot is null));
        // The real window, opened a second time on the demo model, with the chat and without it (the full test on a real PC).
        if (o.Full && (o.OpenWindow is not null || (o.LiveScratch && o.SettingsRoot is null))) WindowFieldSteps.Run(ctx, o.OpenWindow);
        if (left.Count > 0) ctx.Step("test copies an earlier run left open are closed", () => string.Join("; ", left));
        // The chat's defaults (Pitso and his workers, Simple chat), the autopilot's rules and the chat from the phone (2026-11-26).
        ChatFieldSteps.Run(ctx);

        ctx.Section(OpenSection);
        o.Progress?.Invoke("reading the open model");
        // A model that cannot be read at all is one failure that says so, not one for every read that follows.
        string? unread = null;
        try { link.Info(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { unread = $"{ex.GetType().Name}: {ex.Message}"; }
        if (unread is null) foreach (var step in SelfTest.Steps(link, o.Changes && !o.Full)) ctx.Record(step);
        else
        {
            ctx.Fail("read the open model", unread + " Nothing of the open model could be read. Open a model in the program, start CSI ProMax from the program's Tools menu, and run the field test again.");
            ctx.Skip("the checks of the open model", "the open model could not be read (see above)");
        }

        var ranParts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // part -> its section in the report
        if (o.Full)
        {
            // Whatever a part throws, the report is written: a run that ends without one tells nobody what it found.
            try { Full(ctx, link, o, ranParts); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var real = ex is AggregateException { InnerException: { } inner } ? inner : ex;
                try { ctx.Fail("the full test stopped before its end", $"{real.GetType().Name}: {real.Message} The steps after this one were not run. Where: {Where(real)}"); }
                catch (OperationCanceledException) { }
            }
        }

        // The scratch programs rewrite their settings file when they close: CSI ProMax must still be in each Tools menu.
        if (o.Full)
        {
            try
            {
                var after = ToolsMenu.Repair(o.InstallFolder, o.SettingsRoot);
                var lost = after.Where(e => e.State is ToolsMenu.State.Added or ToolsMenu.State.Moved).Select(e => e.Label).ToList();
                var missing = after.Where(e => !e.Listed).ToList();
                ctx.Section(PcFieldSteps.Section);
                if (after.Count > 0)
                    ctx.Step("Tools > CSI ProMax is still in each program after the test", () =>
                    {
                        FieldContext.Expect(missing.Count == 0, string.Join(" ", missing.Select(e => e.Says)));
                        return lost.Count == 0 ? "unchanged: " + string.Join(", ", after.Select(e => e.Label))
                            : $"the test's own scratch window emptied the plug-in list of {string.Join(", ", lost)} when it closed, and CSI ProMax was added again";
                    });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { /* the first check of the lists stands */ }
        }

        var steps = ctx.Steps.ToList();
        // Each part that ran keeps its result on this PC, for the next test of "only what changed or failed".
        if (o.Full && ResultsFile(o) is { } results)
            FieldRetest.Save(ranParts.ToDictionary(r => r.Key, r => FieldRetest.Of(steps.Where(s => s.Section == r.Value), Update.Updater.CurrentText, PrintOf(o, r.Key))), results);
        var report = Render(link, steps, o, now, plugIns, o.Full ? work : null);
        Directory.CreateDirectory(target);
        var named = ReportName(link.Program, o);
        var file = Path.Combine(target, $"Field test {named} {now:yyyy-MM-dd HHmm}.md");
        if (File.Exists(file)) file = Path.Combine(target, $"Field test {named} {now:yyyy-MM-dd HHmmss}.md");
        File.WriteAllText(file, report, new UTF8Encoding(false));
        return new Outcome(file, steps, report, o.Full ? Pack(file, report, work) : null, o.Full ? work : null);
    }

    /// <summary>The full test's parts after the open model: the Modeler, a scratch window of each program, CSI ProMax's own features.</summary>
    private static string? ResultsFile(Options o) => o.ResultsFile ?? (o.LiveScratch ? FieldRetest.File : null);

    private static string PrintOf(Options o, string part) => o.Print is { } print ? print(part) : FieldRetest.Print(part);

    private static void Full(FieldContext ctx, ICsiLink link, Options o, Dictionary<string, string> ran)
    {
        ModelerFieldSteps.Run(ctx); // it writes the file each program's scratch window opens, one program alone too
        var last = o.Retest && ResultsFile(o) is { } kept ? FieldRetest.Load(kept) : [];
        (bool Run, string Why) Choice(string part) => o.Retest ? FieldRetest.Decide(last.GetValueOrDefault(part), PrintOf(o, part)) : (true, "");

        var programs = o.OnlyProgram is { } only ? [only] : o.Programs ?? Installed(link.Program);
        if (programs.Count == 0)
        {
            ctx.Section(ScratchSection);
            ctx.Skip("a second window of a CSI program, on a scratch model",
                "no ETABS, SAP2000 or SAFE was found on this PC, so no tool was run inside a program. Run the field test on a PC that has them."); // ETABS itself
        }
        var panelDone = false;
        var panel = Choice(FieldRetest.Panel);
        if (!panel.Run)
        {
            panelDone = true;
            ctx.Section(PanelFieldSteps.Section);
            FieldRetest.LeftOut(ctx, "CSI ProMax's own features", last[FieldRetest.Panel], panel.Why);
        }
        // CSI ProMax's own features need one scratch window: when no program's part is due, the first program's runs for them.
        var due = programs.Where(p => Choice(p.Key).Run).Select(p => p.Key).ToHashSet();
        if (panel.Run && due.Count == 0 && programs.Count > 0) due.Add(programs[0].Key);
        foreach (var program in programs)
        {
            ctx.Section(FieldParts.ScratchSection(program));
            if (!due.Contains(program.Key))
            {
                FieldRetest.LeftOut(ctx, $"{program.Name} on a scratch model", last[program.Key], Choice(program.Key).Why);
                continue;
            }
            ran[program.Key] = FieldParts.ScratchSection(program);
            var file = ctx.Files.GetValueOrDefault(program.Key, "");
            if (file.Length == 0 || !File.Exists(file))
            {
                ctx.Fail($"open {program.Name} on a scratch model", $"the Modeler did not write the file {program.Name} opens (see the Modeler's part), so no {program.Name} window was opened");
                continue;
            }
            IScratchWindow? window = null;
            var finished = ctx.Within(o.ScratchLimit, $"{program.Name} on a scratch model", () =>
            {
                ctx.Program = program;
                window = o.StartScratch(program, ctx, file);
                if (window is null) return;
                ctx.Link = window.Link;
                ctx.Watched = window.Link;
                // The Viewer's own screen on this program's model: one check, the same for every program. It runs while
                // the model has its results (the program's steps say when), or after them if they never did.
                ctx.WhileAnalysed = o.Viewer ? () => { var at = ctx.Watched; ctx.Watched = null; try { ViewerCheck.FieldSteps(ctx, window.Link, program.Name); } finally { ctx.Watched = at; } } : null;
                try { ScratchSteps(program)(ctx); }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                {
                    // One call the steps did not expect to fail: said once, with what was not reached because of it.
                    ctx.Section(FieldParts.ScratchSection(program));
                    var left = ctx.NotRun;
                    ctx.Fail($"{program.Name}'s part stopped before its end",
                        $"{ex.GetType().Name}: {ex.Message} Tools not run because of it: {(left.Count == 0 ? "none" : string.Join(", ", left))}. Where: {Where(ex)}");
                }
                finally { ctx.Watched = null; }
                // The Viewer's own screen on this program's model: one check, the same for every program.
                ctx.Section(FieldParts.ScratchSection(program));
                ctx.WithResults();
                // Learned tools are shared by the three programs (and the team): one made elsewhere must run here too.
                PanelFieldSteps.InProgram(ctx, window.Link, program, o.LiveScratch);
                if (panelDone) return;
                // CSI ProMax's own features need a window that still answers: one that stopped (a call that never came
                // back) is left out, and the next program's window, or the open model, takes that part instead.
                try { window.Link.Info(); }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                {
                    ctx.Skip($"CSI ProMax's own features on the scratch {program.Name} window", $"that window no longer answers ({(ex is AggregateException { InnerException: { } inner } ? inner : ex).Message}), so they run on the next window that does");
                    return;
                }
                panelDone = true;
                ran[FieldRetest.Panel] = PanelFieldSteps.Section;
                // CSI ProMax's own tools change the model too (code): only while this window still has the test's model.
                var mine = ctx.Step($"the scratch {program.Name} window still has the scratch model open", () =>
                {
                    // By its name without the file type: a save may write the program's other type beside it.
                    var now = Path.GetFileName(window.Link.Info().File.Replace('\\', '/'));
                    FieldContext.Expect(string.Equals(Path.GetFileNameWithoutExtension(now), Path.GetFileNameWithoutExtension(window.ModelPath), StringComparison.OrdinalIgnoreCase),
                        $"it has '{now}' open, not '{Path.GetFileName(window.ModelPath)}', so nothing more is changed through it");
                    return now;
                }) is not null;
                PanelFieldSteps.Run(ctx, window.Link, scratch: mine, live: o.LiveScratch);
            });
            ctx.Watched = null;
            // ctx.Link stays on the scratch window: a part that was given up on must never reach the engineer's model.
            ctx.Section(FieldParts.ScratchSection(program));
            // Given up on: the windows waiting in the scratch program are named, so the engineer knows which to answer.
            if (!finished && window is { } stuck)
            {
                IReadOnlyList<string> waiting;
                try { waiting = stuck.OpenWindows(); } catch (Exception ex) when (ex is not OutOfMemoryException) { waiting = []; }
                if (waiting.Count > 0)
                    ctx.Fail($"windows waiting in the scratch {program.Name}", "These were open in it when the test gave up: " + string.Join("; ", waiting.Select(t => $"'{t}'")) + ". Answer them yourself, close that window without saving, and run the field test again.");
            }
            if (window is { } open)
            {
                // Closing and letting go never stop the test: the next program and the report still follow.
                try
                {
                    ScratchProgram.CloseStep(ctx, open, program);
                    // A window that stopped answering is left as it is: letting go of it could wait for ever too.
                    if (finished) open.Dispose();
                    // Tools > CSI ProMax stays where it was: a scratch window started through the API empties the plug-in list.
                    ctx.Step($"{program.Name}'s plug-in lists are as they were before the scratch window", () =>
                    {
                        var back = open.PutBackPlugIns();
                        return back.Count == 0 ? "unchanged" : "the scratch window had emptied them; put back: " + string.Join(", ", back.Select(Path.GetFileName));
                    });
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    var real = ex is AggregateException { InnerException: { } inner } ? inner : ex;
                    ctx.Fail($"let go of the scratch {program.Name} window", $"{real.GetType().Name}: {real.Message}");
                }
            }
        }
        if (!panelDone)
        {
            ran[FieldRetest.Panel] = PanelFieldSteps.Section;
            PanelFieldSteps.Run(ctx, link, scratch: false, live: IsLive(link));
        }
    }

    /// <summary>The first calls of where an exception came from, for the report: enough to find the step.</summary>
    private static string Where(Exception ex) =>
        string.Join(" < ", (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4).Select(l => l.Length > 160 ? l[..160] : l));

    private static Action<FieldContext> ScratchSteps(CsiProgram program) => program.Key switch
    {
        "sap2000" => Sap2000.SapFieldSteps.Scratch,
        "safe" => Safe.SafeFieldSteps.Scratch,
        _ => EtabsFieldSteps.Scratch,
    };

    /// <summary>The CSI programs installed on this PC, <paramref name="first"/> first.</summary>
    internal static IReadOnlyList<CsiProgram> Installed(CsiProgram first) =>
        CsiProgram.All.Where(p =>
            {
                try { return File.Exists(EtabsApi.FindApiFile(p)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
            })
            .OrderBy(p => p.Key == first.Key ? 0 : 1).ToList();

    /// <summary>True when a CSI program is behind the link (not the Modeler, not a model in memory).</summary>
    internal static bool IsLive(ICsiLink link) => link switch
    {
        OfflineEtabs or MemoryEtabs => false,
        Sap2000.SapLink sap => IsLive(sap.Inner),
        _ => true,
    };

    private static string Counts(int passed, int failed, int notTested) =>
        (failed == 0 ? $"all {passed} checks passed" : $"{passed} checks passed, {failed} did not") +
        (notTested > 0 ? $", {notTested} not tested" : "");

    /// <summary>What no test can do for the person: they look at these themselves.</summary>
    internal static readonly string[] ByHand =
    [
        "Tools > CSI ProMax is in the menu of each CSI program (ETABS, SAP2000, SAFE), and opens this window on that program's model.", // ETABS itself
        "A message in the chat gets an answer from Claude Code.",
        "A question the chat asks shows in its Questions tab, and your answer reaches the chat.",
        "Help > Report a bug: the picture of the screen is in the report.",
        "The review pack opens in its viewer page (the .html beside the .cpmx) in your browser.",
        "CSI ProMax > Check for updates finds the newest version.",
    ];

    internal static string Render(ICsiLink link, IReadOnlyList<FieldStep> steps, Options o, DateTime now,
        IReadOnlyList<(string File, string Section)> plugIns, string? work)
    {
        var bad = steps.Where(s => !s.Ok).ToList();
        var skipped = steps.Where(s => s.Skipped).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# CSI ProMax field test");
        sb.AppendLine();
        if (o.Full)
        {
            sb.AppendLine("Please send the zip beside this report (the same name, ending in .zip) to whoever gave you CSI ProMax. The zip");
            sb.AppendLine("holds this report and the small files the test made on its own test model. This report holds the versions on");
            sb.AppendLine("this PC, the plug-in lists of the CSI programs, and small samples of the open model (names, counts and the first");
            sb.AppendLine("rows of result tables). Read it before you send it.");
        }
        else
        {
            sb.AppendLine("Please send this file to whoever gave you CSI ProMax. It holds the versions on this PC, the plug-in lists of the");
            sb.AppendLine("CSI programs, and small samples of the open model (names, counts and the first rows of result tables). Read it");
            sb.AppendLine("before you send it.");
        }
        sb.AppendLine();
        sb.AppendLine($"- Result: {Counts(steps.Count(s => s.Ok && !s.Skipped), bad.Count, skipped.Count)}");
        sb.AppendLine(o.Full
            ? "- Kind: the full test (every tool: the Modeler, a second window of each CSI program on a scratch model, CSI ProMax's own features)"
            : "- Kind: the quick test (this PC, the install and the open model)");
        sb.AppendLine($"- Run: {now:yyyy-MM-dd HH:mm}, started from {o.StartedFrom}");
        if (o.Full && o.Retest) sb.AppendLine("- Kind of run: only what is new, changed or failed. A part marked \"not run again\" passed on this PC before and its code is unchanged since.");
        sb.AppendLine(o.Full
            ? "- Changes to the model: none to the open model (reading only). Every change was made on the test's own scratch models, in program windows the test opened and closed itself"
            : o.Changes
                ? "- Changes to the model: a test column, a slab, the load pattern CPM-TEST, loads and the combination CPM-TEST-COMBO were added; the column and the slab were deleted again"
                : "- Changes to the model: none (reading only)");
        sb.AppendLine($"- CSI ProMax: {Update.Updater.CurrentText}, in {o.InstallFolder ?? AppContext.BaseDirectory}");
        if (Update.Updater.Behind() is { } behind) sb.AppendLine("- NOT THE NEWEST VERSION: " + behind);
        sb.AppendLine("- " + ProgramLine(link));
        // Only the plug-in passes --etabs-pid, so this says whether the Tools menu entry works on this PC.
        sb.AppendLine(Environment.GetCommandLineArgs().Contains("--etabs-pid")
            ? "- Window: opened from the program's Tools menu (the plug-in started it)"
            : "- Window: opened on its own, not from the program's Tools menu");
        sb.AppendLine($"- This PC: {Environment.OSVersion}, .NET {Environment.Version}, started with {Environment.ProcessPath}");
        if (work is not null) sb.AppendLine($"- The test's own files (scratch models, the files it made): {work}. The zip beside this report holds the small ones; that folder can be deleted.");
        sb.AppendLine();

        // What failed, then what was left out, each under the part of the test it belongs to (a tool has the same name in every program).
        void Listed(string title, IReadOnlyList<FieldStep> which)
        {
            if (which.Count == 0) return;
            sb.AppendLine("## " + title);
            sb.AppendLine();
            foreach (var part in which.GroupBy(s => s.Section))
            {
                if (part.Key.Length > 0) sb.AppendLine(part.Key + ":").AppendLine();
                foreach (var s in part) sb.AppendLine($"- {s.Name}: [{SkipAudit.Words(SkipAudit.Of(s.Detail))}] {Short(Regex.Replace(s.Detail, "^skipped: ", ""), 400)}");
                sb.AppendLine();
            }
        }
        Listed("Not working", bad);
        Listed("Not tested", skipped);
        if (o.Full)
        {
            sb.AppendLine("## Check by hand");
            sb.AppendLine();
            sb.AppendLine("No test can do these for you. Please try each one and write in your message which did not work:");
            sb.AppendLine();
            foreach (var line in ByHand) sb.AppendLine("- " + line);
            sb.AppendLine();
        }

        sb.AppendLine("## Plug-in lists of the CSI programs");
        sb.AppendLine();
        if (plugIns.Count == 0) sb.AppendLine("None was found for this Windows user.").AppendLine();
        foreach (var (file, section) in plugIns)
        {
            sb.AppendLine($"### {file}");
            sb.AppendLine();
            Fence(sb, section, "ini");
        }

        sb.AppendLine("## Every check");
        sb.AppendLine();
        sb.AppendLine("Each part of the test, in the order it ran. Each check has what it found, or why it failed.");
        sb.AppendLine();
        foreach (var part in steps.GroupBy(s => s.Section))
        {
            if (part.Key.Length > 0)
            {
                sb.AppendLine($"## {part.Key}");
                sb.AppendLine();
            }
            foreach (var s in part)
            {
                var took = s.Seconds >= 1 ? $" ({s.Seconds:0} s)" : "";
                sb.AppendLine($"### {(s.Skipped ? "not tested" : s.Ok ? "ok" : "FAIL")}: {s.Name}{took}");
                sb.AppendLine();
                Fence(sb, s.Full ?? s.Detail, s.Ok && !s.Skipped ? "json" : "");
            }
        }
        var text = HideFolder(sb.ToString(), ModelFolder(link), "(model folder)");
        return HideFolder(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
    }

    /// <summary>The folder the open model is in, "" when it has none.</summary>
    private static string ModelFolder(ICsiLink link)
    {
        string file;
        try { file = link.Info().File; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
        var cut = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        return cut <= 0 ? "" : file[..cut];
    }

    /// <summary>
    /// The report without a folder, wherever a check's answer names it: the report is sent on, and a folder can name a
    /// client or a person (the model's folder, the Windows user's own). File names stay.
    /// </summary>
    private static string HideFolder(string report, string folder, string instead)
    {
        // An answer inside an answer is escaped again, so the folder's parts may be one, two or four back-slashes apart.
        // A folder in the root of a drive has nothing worth hiding, and "C:" alone is in other paths too.
        var parts = folder.Split('\\', '/').Where(p => p.Length > 0).Select(Regex.Escape).ToList();
        return parts.Count < 2 ? report : Regex.Replace(report, string.Join(@"[\\/]+", parts), instead.Replace("$", "$$"), RegexOptions.IgnoreCase);
    }

    /// <summary>File kinds the zip takes from the test's folder: what was written and read, never the programs' model files.</summary>
    private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".log", ".out", ".json", ".csv", ".e2k", ".$et", ".s2k", ".$2k", ".f2k", ".$fk", ".cpm", ".cpmx",
        ".xlsx", ".docx", ".pptx", ".pdf", ".dxf", ".dwg", ".png",
    };

    private const long MostFile = 3 * 1024 * 1024, MostZip = 25 * 1024 * 1024;

    /// <summary>
    /// The zip to send: the report, and the small files the test wrote and read back (the files the Modeler wrote for
    /// each program, the packs, the documents, the programs' messages). Not the scratch models themselves, nothing over
    /// 3 MB, nothing from the made-up bug report. Null when it could not be written (then the report is sent alone).
    /// </summary>
    internal static string? Pack(string reportFile, string report, string work)
    {
        var zip = Path.ChangeExtension(reportFile, ".zip");
        try
        {
            if (File.Exists(zip)) File.Delete(zip);
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(archive.CreateEntry(Path.GetFileName(reportFile)).Open(), new UTF8Encoding(false))) writer.Write(report);
            long total = 0;
            if (!Directory.Exists(work)) return zip;
            foreach (var path in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var inside = Path.GetRelativePath(work, path).Replace('\\', '/');
                if (inside.StartsWith("Bugs/", StringComparison.OrdinalIgnoreCase) || !Small.Contains(Path.GetExtension(path))) continue;
                var size = new FileInfo(path).Length;
                if (size > MostFile || total + size > MostZip) continue;
                archive.CreateEntryFromFile(path, "files/" + inside, CompressionLevel.Optimal);
                total += size;
            }
            return zip;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    private static string ProgramLine(ICsiLink link)
    {
        try
        {
            var info = link.Info();
            var file = info.File.Replace('\\', '/').Split('/').Last();
            return $"Program: {link.Program.Name} {info.Version}, units {info.Force}, {info.Length}, model \"{(file.Length > 0 ? file : "not saved yet")}\", analysed: {(info.Locked ? "yes" : "no")}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"Program: {link.Program.Name} (the model could not be read: {ex.Message})"; }
    }

    private static void Fence(StringBuilder sb, string text, string kind)
    {
        // A fence longer than any run of back-ticks in the text, so the text cannot end it.
        var ticks = new string('`', Math.Max(3, Regex.Matches(text, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max() + 1));
        sb.AppendLine(ticks + kind);
        sb.AppendLine(text);
        sb.AppendLine(ticks);
        sb.AppendLine();
    }

    private static string Short(string text, int most) => text.Length > most ? text[..most] + "..." : text;

    /// <summary>
    /// The [PlugIn] part of each CSI program's settings file for this Windows user, as it is written there: it shows
    /// whether CSI ProMax is listed, and how the program itself writes a list (the one-line install writes the same).
    /// </summary>
    internal static IReadOnlyList<(string File, string Section)> PlugInLists(string? root = null)
    {
        var found = new List<(string, string)>();
        try
        {
            root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Computers and Structures");
            if (!Directory.Exists(root)) return found;
            foreach (var folder in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(folder);
                if (!Regex.IsMatch(name, @"^(ETABS|SAFE|SAP2000|CSiBridge) \d+$", RegexOptions.IgnoreCase)) continue; // ETABS itself
                foreach (var ini in Directory.GetFiles(folder, "*.ini").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    if (PlugInSection(File.ReadAllText(ini)) is { } section) found.Add(($"{name}\\{Path.GetFileName(ini)}", section));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what was read so far is reported */ }
        return found;
    }

    /// <summary>The lines from "[PlugIn]" to the next section of a settings file, or null when it has none.</summary>
    internal static string? PlugInSection(string ini)
    {
        var lines = ini.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim().Equals("[PlugIn]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Length;
        return string.Join("\n", lines[start..end]).TrimEnd();
    }
}
