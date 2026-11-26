using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Update;

namespace CsiProMax.Assistant.Packs;

/// <summary>
/// The review packs kept up to date by themselves, with no window: dotnet CsiProMax.dll --packs. One for each Windows
/// user (a named mutex), started at sign-in by the Tools menu keeper (keep-plugins.ps1) and by the CSI ProMax window.
/// Every few seconds it looks for open ETABS, SAP2000 and SAFE windows and, for each, follows <see cref="AutoPack"/>'s
/// rule: after an analysis or a design the pack beside the model is written again. The newer programs (on .NET 8) are
/// reached through their API from here; the older ones (ETABS 21, SAP2000 25, SAFE 21 and before) through the
/// plug-in's pipe, once Tools > CSI ProMax was opened in them, or through Windows PowerShell.
///   --packs          keep doing it until Windows signs out (or packs-off.txt is put in %LOCALAPPDATA%\CsiProMax)
///   --packs --once   one look at every open program now, and say what was done (from a command line)
/// What it does goes in %LOCALAPPDATA%\CsiProMax\packs.log, and what it watches now in packs.json.
/// </summary>
public static class PackWatcher
{
    public const string MutexName = "Local\\CsiProMax.Packs";

    internal static string StateRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax");
    internal static string LogFile => Path.Combine(StateRoot, "packs.log");
    internal static string StatusFile => Path.Combine(StateRoot, "packs.json");

    /// <summary>The file that turns it off: while it is there, nothing is watched and no watcher starts.</summary>
    internal static string OffFile => Path.Combine(StateRoot, "packs-off.txt");

    /// <summary>What the Tools menu keeper reads to start the watcher at sign-in: "on|dotnet|folder of CsiProMax.dll".</summary>
    internal static string OnFile => Path.Combine(StateRoot, "packs.txt");

    public static bool Off => File.Exists(OffFile);

    /// <summary>How often each program is looked at.</summary>
    public static TimeSpan Every { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly object LogLock = new();

    internal static void Log(string line)
    {
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(StateRoot);
                var kept = File.Exists(LogFile) ? File.ReadAllLines(LogFile).TakeLast(499).ToList() : [];
                kept.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
                File.WriteAllLines(LogFile, kept);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The CSI program windows open now: process id and program. Only windows the engineer sees (one started hidden through the API is a test's).</summary>
    internal static IReadOnlyList<(int Pid, CsiProgram Program)> OpenPrograms()
    {
        var found = new List<(int, CsiProgram)>();
        foreach (var program in CsiProgram.All)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(program.ProcessName); }
            catch (InvalidOperationException) { continue; }
            foreach (var p in processes)
                using (p)
                {
                    try { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)) found.Add((p.Id, program)); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
        }
        return found;
    }

    /// <summary>One program window being followed.</summary>
    private sealed class Follow(int pid, CsiProgram program) : IDisposable
    {
        public int Pid { get; } = pid;
        /// <summary>This window, among every window on the same model (<see cref="AutoPack.LockedElsewhere"/>).</summary>
        public string Key => Program.Key + ":" + Pid.ToString(CultureInfo.InvariantCulture);
        public CsiProgram Program { get; } = program;
        private ComEtabs? _com;
        private ICsiLink? _link;
        public PackLook? Before;
        public DateTime NotBefore = DateTime.MinValue;
        public string? Problem;
        public string? LastPack;

        public ICsiLink Link()
        {
            if (_link is not null) return _link;
            _com = new ComEtabs(Pid, Program) { CallLimit = TimeSpan.FromMinutes(5) };
            return _link = CsiProMax.Assistant.Tools.ProgramTools.Live(_com);
        }

        /// <summary>A link that failed is made again at the next look (the program may have been busy, or its pipe gone).</summary>
        public void Drop()
        {
            try { _com?.Dispose(); } catch (Exception) { }
            _com = null; _link = null;
        }

        public void Dispose() => Drop();
    }

    /// <summary>One look at one program; problems are logged once each and the next look waits a little.</summary>
    private static void LookAt(Follow f)
    {
        if (DateTime.UtcNow < f.NotBefore) return;
        // An analysis that is still writing its log: the program is busy, so it is not asked anything now.
        if (f.Before is { } b && AutoPack.RunStamp(b.File) is { } run && DateTime.UtcNow - run < AutoPack.Settle) return;
        try
        {
            var done = AutoPack.Once(f.Link(), ref f.Before, window: f.Key);
            if (f.Problem is not null) Log($"{f.Program.Name} (process {f.Pid}) answers again.");
            f.Problem = null;
            if (done is not null) { f.LastPack = done; Log(done); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var why = ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;
            if (why != f.Problem) Log($"{f.Program.Name} (process {f.Pid}): {why}");
            f.Problem = why;
            f.Drop();
            f.NotBefore = DateTime.UtcNow.AddMinutes(1);
        }
    }

    public static int Run(string[] args)
    {
        if (args.Contains("--once")) return OnceNow();
        if (Off) return 0;
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first) return 0;
        Log($"Watching {string.Join(", ", CsiProgram.All.Select(p => p.Name))} for runs and designs (CSI ProMax {Updater.Current}, process {Environment.ProcessId}).");
        var follows = new Dictionary<int, Follow>();
        var workers = new Dictionary<int, Task>();
        var started = DateTime.UtcNow;
        var nextUpdate = DateTime.UtcNow.AddMinutes(2);
        var nextCheck = DateTime.UtcNow.AddMinutes(5);
        try
        {
            while (!Off)
            {
                var open = OpenPrograms();
                foreach (var (pid, program) in open)
                    if (!follows.ContainsKey(pid))
                    {
                        follows[pid] = new Follow(pid, program);
                        Log($"{program.Name} is open (process {pid}).");
                    }
                foreach (var gone in follows.Keys.Where(k => open.All(o => o.Pid != k)).ToList())
                {
                    Log($"{follows[gone].Program.Name} (process {gone}) is closed.");
                    if (!workers.TryGetValue(gone, out var w) || w.IsCompleted) { AutoPack.Forget(follows[gone].Key); follows[gone].Dispose(); follows.Remove(gone); workers.Remove(gone); }
                }
                // Each program on its own: a long pack in ETABS does not hold SAP2000's back.
                foreach (var f in follows.Values)
                    if (!workers.TryGetValue(f.Pid, out var w) || w.IsCompleted) workers[f.Pid] = Task.Run(() => LookAt(f));
                Status(started, follows.Values);
                // A newer version downloaded (by this watcher, the Modeler, the Viewer or the window): it takes over.
                if (DateTime.UtcNow > nextCheck)
                {
                    nextCheck = DateTime.UtcNow.AddHours(1);
                    _ = Task.Run(async () => { try { await Updater.CheckAsync(force: false).ConfigureAwait(false); } catch (Exception ex) { Log("The update check did not run: " + ex.Message); } });
                }
                if (DateTime.UtcNow > nextUpdate && workers.Values.All(w => w.IsCompleted))
                {
                    nextUpdate = DateTime.UtcNow.AddMinutes(10);
                    if (Updater.NewestDownloaded(Updater.Current) is { } newer && Environment.ProcessPath is { } dotnet)
                        try
                        {
                            var info = Program.Start(dotnet, newer, ["--packs"], Environment.ProcessId);
                            info.CreateNoWindow = true;
                            mutex.ReleaseMutex();
                            Process.Start(info)?.Dispose();
                            Log($"CSI ProMax {Path.GetFileName(newer)} takes over watching.");
                            return 0;
                        }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ApplicationException) { Log("The newer version could not take over: " + ex.Message); }
                }
                Thread.Sleep(Every);
            }
            Log("packs-off.txt is there: watching stops.");
            return 0;
        }
        finally
        {
            foreach (var f in follows.Values) f.Dispose();
        }
    }

    /// <summary>--packs --once: every open program looked at now, the pack written where the rule says, and each said.</summary>
    private static int OnceNow()
    {
        var open = OpenPrograms();
        if (open.Count == 0) { Console.WriteLine($"No {string.Join(", ", CsiProgram.All.Select(p => p.Name))} window is open."); return 0; }
        var failed = 0;
        foreach (var (pid, program) in open)
        {
            using var f = new Follow(pid, program);
            try
            {
                var look = AutoPack.Look(f.Link());
                PackLook? before = null;
                var made = AutoPack.Saved(look.File) && look.Locked ? AutoPack.MadeOf(AutoPack.PackFile(look.File)) : null;
                var why = AutoPack.Decide(before, look, made, DateTime.UtcNow);
                Console.WriteLine($"{program.Name} (process {pid}): {(look.File.Length == 0 ? "a model never saved" : look.File)}, " +
                    (look.Locked ? "with results" : "no results") + (look.Design.Length > 0 ? ", with design results" : "") +
                    (look.RunStamp is { } r ? $", analysis log {r.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : ", no analysis log beside it"));
                if (why is null) { Console.WriteLine(made is null ? "  Nothing to pack." : "  The pack beside it is up to date."); continue; }
                var done = AutoPack.Once(f.Link(), ref before, fieldTestModels: true);
                Console.WriteLine("  " + (done ?? "Nothing to pack."));
                if (done is not null) Log(done);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                Console.WriteLine($"{program.Name} (process {pid}): {(ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message)}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    private static DateTime _lastStatus;

    private static void Status(DateTime started, IEnumerable<Follow> follows)
    {
        if (DateTime.UtcNow - _lastStatus < TimeSpan.FromSeconds(30)) return;
        _lastStatus = DateTime.UtcNow;
        try
        {
            var status = new JsonObject
            {
                ["version"] = Updater.Current.ToString(), ["pid"] = Environment.ProcessId,
                ["started"] = started.ToString("o", CultureInfo.InvariantCulture), ["seen"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["programs"] = new JsonArray(follows.Select(f => (JsonNode)new JsonObject
                {
                    ["program"] = f.Program.Name, ["pid"] = f.Pid, ["model"] = f.Before?.File, ["results"] = f.Before?.Locked,
                    ["problem"] = f.Problem, ["last_pack"] = f.LastPack,
                }).ToArray()),
            };
            Directory.CreateDirectory(StateRoot);
            File.WriteAllText(StatusFile, status.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether a watcher runs now for this Windows user.</summary>
    public static bool Running()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { if (Mutex.TryOpenExisting(MutexName, out var m)) { m.Dispose(); return true; } }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return true; }
        return false;
    }

    /// <summary>
    /// Starts the watcher from this version's folder when none runs, and tells the Tools menu keeper to start it at
    /// every sign-in. On by default; packs-off.txt turns it off. Called by the CSI ProMax window when it opens.
    /// </summary>
    public static void EnsureRunning()
    {
        if (!OperatingSystem.IsWindows() || Off || Environment.ProcessPath is not { } dotnet) return;
        try
        {
            var folder = InstalledFolder();
            Directory.CreateDirectory(StateRoot);
            var line = "on|" + dotnet + "|" + folder;
            if (!File.Exists(OnFile) || File.ReadAllText(OnFile).Trim() != line) File.WriteAllText(OnFile, line);
            if (Running()) return;
            var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CsiProMax.dll"));
            info.ArgumentList.Add("--packs");
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log("The watcher could not be started: " + ex.Message);
        }
    }

    /// <summary>The installed folder (the keeper starts the watcher from there; it then runs the newest downloaded version itself).</summary>
    private static string InstalledFolder() => ToolsMenu.InstalledIn ?? AppContext.BaseDirectory.TrimEnd('\\', '/');
}
