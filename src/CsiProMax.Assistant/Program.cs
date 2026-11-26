using System.Diagnostics;
using System.Runtime.InteropServices;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Update;

namespace CsiProMax.Assistant;

/// <summary>
/// CSI ProMax: Sketch ProMax's panel in a window of its own beside ETABS. Started without an .exe of its own, with the
/// .NET 8 that ETABS 23 itself runs on: dotnet CsiProMax.dll, or from ETABS's Tools menu (the plug-in).
///   --etabs-pid N   the ETABS window this was started from (the plug-in passes it), when several are open
///   --demo          a small model in memory instead of ETABS, to see the window on a PC without ETABS
///   --modeler [file] the Modeler: CSI ProMax's own model with no ETABS, saved as .e2k for licensed ETABS to import
///   --viewer [file] the Viewer in a CSI ProMax window of its own: a review pack (.cpmx) or a Viewer page, or empty
///   --selftest      check the link to ETABS call by call and print the result (--changes also adds and deletes objects)
/// </summary>
public static class Program
{
    /// <summary>The Modeler's model: the file named after --modeler when there is one and it reads, otherwise a new model.</summary>
    private static OfflineEtabs OpenModeler(string[] args)
    {
        var at = Array.IndexOf(args, "--modeler");
        var file = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : null;
        if (file is not null && File.Exists(file))
            try { return new OfflineEtabs(OfflineEtabs.Load(file), file.EndsWith(".e2k", StringComparison.OrdinalIgnoreCase) ? file : ""); }
            catch (Exception ex) when (ex is IOException or CsiProMax.E2k.E2kFormatException or System.Text.Json.JsonException or InvalidDataException) { Note("modeler file", ex); }
        return new OfflineEtabs();
    }

    /// <summary>
    /// The product was called ETABS ProMax until 0.4: its chats, notes and license files under %APPDATA%\EtabsProMax
    /// move to %APPDATA%\CsiProMax once, when the new folder is not there yet.
    /// </summary>
    private static void CarryOver()
    {
        try
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string old = Path.Combine(roaming, "Etabs" + "ProMax"), now = Path.Combine(roaming, "CsiProMax");
            if (Directory.Exists(old) && !Directory.Exists(now)) Directory.Move(old, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the new folder starts empty */ }
    }

    /// <summary>The folder of a newer downloaded version to run instead of this one, or null (also when this one was started as "this version").</summary>
    private static string? Newer(string[] args) => args.Contains("--this-version") ? null : Updater.NewestDownloaded(Updater.Current);

    /// <summary>
    /// How a downloaded version is started in place of this one: the same arguments, told not to look for a newer one
    /// again, where the installed files are (the Tools menus name the plug-in files there) and, when an open window
    /// gives way to it, which process to wait for.
    /// </summary>
    internal static ProcessStartInfo Start(string dotnet, string folder, string[] args, int? afterPid)
    {
        var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, WorkingDirectory = folder };
        info.ArgumentList.Add(Path.Combine(folder, "CsiProMax.dll"));
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--this-version") continue;
            if (args[i] is "--installed-in" or "--after-pid") { i++; continue; }
            info.ArgumentList.Add(args[i]);
        }
        info.ArgumentList.Add("--this-version");
        info.ArgumentList.Add("--installed-in");
        info.ArgumentList.Add(ToolsMenu.InstalledIn ?? AppContext.BaseDirectory);
        if (afterPid is { } pid) { info.ArgumentList.Add("--after-pid"); info.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return info;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        DropOwnConsole();
        NameForTaskbar();
        int? etabsPid = Array.IndexOf(args, "--etabs-pid") is var at and >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var pid) ? pid : null;
        // The CSI program this was started from: named by the plug-in, or told from the process. ETABS when neither says.
        var program = (Array.IndexOf(args, "--program") is var named and >= 0 && named + 1 < args.Length ? CsiProgram.Find(args[named + 1]) : null) ?? CsiProgram.OfProcess(etabsPid);
        // Every text names the program the window works beside (Csi.Name), so it is told before anything else is made.
        Csi.Program = program;
        CsiProMax.App.NoUpdate = args.Contains("--no-update");
        // With the AI chat or without it (AiMode): read before anything is made, since the window is built for it.
        CsiProMax.AiMode.Load(args);
        CsiProMax.App.Reopened = args.Contains("--after-pid");
        // The program version whose Tools menu started this window: its plug-in list may not be written yet (ToolsMenu.State.Running).
        ToolsMenu.OpenedFrom = ToolsMenu.LabelOf(etabsPid);
        if (Array.IndexOf(args, "--installed-in") is var origin and >= 0 && origin + 1 < args.Length) ToolsMenu.InstalledIn = args[origin + 1];
        if (!program.Ready)
        {
            if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, $"CSI ProMax for {program.Name} is not built yet. It works with {CsiProgram.Etabs.Name} today; {program.Name} is planned.", "CSI ProMax", 0x40);
            return 3;
        }
        if (args.Contains("--mta")) ComEtabs.Apartment = ApartmentState.MTA;
        var modeler = args.Contains("--modeler");
        var demo = args.Contains("--demo") || modeler;
        if (Array.IndexOf(args, "--test-sap2000") is var sapTest and >= 0)
        {
            var s2k = sapTest + 1 < args.Length ? args[sapTest + 1] : "test-frame.s2k";
            var folder = sapTest + 2 < args.Length ? args[sapTest + 2] : Path.Combine(Path.GetTempPath(), "CsiProMax-sap-test");
            return CsiProMax.Assistant.Sap2000.SapScratchTest.Run(s2k, folder);
        }
        // The tests from the command line run on the newest version this PC has, like the window: the installed folder
        // keeps the version first installed, and a test of that one reports what is already fixed.
        if ((args.Contains("--fieldtest") || args.Contains("--selftest")) && Newer(args) is { } newerTest && Environment.ProcessPath is { } host)
        {
            try
            {
                using var run = Process.Start(Start(host, newerTest, args, null));
                if (run is not null) { run.WaitForExit(); return run.ExitCode; }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { /* this version runs, and its report says it is old */ }
        }
        if (args.Contains("--fieldtest"))
        {
            // The field test without the window: prints the report and says where it was saved.
            using var check = new ComEtabs(etabsPid, program);
            // --full: every tool, on scratch models in windows of the programs the test opens itself (20 to 60 minutes).
            var link = demo ? new MemoryEtabs() : CsiProMax.Assistant.Tools.ProgramTools.Live(check);
            var outcome = args.Contains("--full")
                ? FieldTest.Run(link, new FieldTest.Options { Full = true, Retest = args.Contains("--retest"), StartedFrom = "the command line", Progress = step => Console.Error.WriteLine("... " + step), Outcome = line => Console.Error.WriteLine(line) })
                : FieldTest.Run(link, args.Contains("--changes"), "the command line");
            Console.WriteLine(outcome.Report);
            Console.WriteLine("Saved: " + outcome.File);
            if (outcome.Zip is not null) Console.WriteLine("To send: " + outcome.Zip);
            return outcome.Failed == 0 ? 0 : 1;
        }
        if (args.Contains("--selftest"))
        {
            using var check = new ComEtabs(etabsPid, program);
            return SelfTest.Run(demo ? new MemoryEtabs() : CsiProMax.Assistant.Tools.ProgramTools.Live(check), args.Contains("--changes"));
        }
        try
        {
            // A newer version the updater downloaded is started instead of this one (once: it is told not to look again).
            if (Newer(args) is { } newer && Environment.ProcessPath is { } dotnet)
            {
                var info = Start(dotnet, newer, args, null);
                info.CreateNoWindow = true;
                try { Process.Start(info)?.Dispose(); return 0; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { /* this version runs */ }
            }
            // Started by an older window that is closing to let this version take over: wait for it to be gone.
            if (Array.IndexOf(args, "--after-pid") is var after and >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out var before))
                try { using var old = Process.GetProcessById(before); old.WaitForExit(20000); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }

            CarryOver();
            // The Viewer on its own (its shortcut, or a pack opened with CSI ProMax): one window, no CSI program, no chat.
            if (args.Contains("--viewer")) return RunViewer(args);
            // One window per ETABS: started again from ETABS's menu, the open window comes forward.
            var name = "CsiProMax." + (modeler ? "modeler" : demo ? "demo" : etabsPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any");
            using var one = new Mutex(true, name, out var first);
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
            using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".shown");
            if (!first && !Handover(one, show, shown, name, args)) return 0;
            Holder.Write(name);

            // Started for an ETABS that is no longer open: say so, instead of a window that closes by itself.
            if (!demo && etabsPid is { } asked && !EtabsIsOpen(asked))
            {
                if (OperatingSystem.IsWindows())
                    MessageBoxW(IntPtr.Zero, $"The {program.Name} window CSI ProMax was started for is closed.\n\nOpen {program.Name} and start CSI ProMax from its Tools menu.", "CSI ProMax", 0x40);
                return 2;
            }
            using var com = new ComEtabs(etabsPid, program);
            ICsiLink link = modeler ? OpenModeler(args) : demo ? new MemoryEtabs() : CsiProMax.Assistant.Tools.ProgramTools.Live(com);
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) => Note("window", e.Exception);
            // The 3D view and the Viewer open in CSI ProMax windows of their own, not in the browser.
            CsiProMax.Host.WebWindow.Install();
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Note("unhandled", e.ExceptionObject as Exception);
            CsiProMax.App.Start(link, etabsPid, demo);
            var window = new CsiProMax.Host.MainWindow();
            // Started from a shortcut, Windows hands the window the shortcut's "minimised" (meant for dotnet's console):
            // the window comes up normal and in front.
            window.ContentRendered += (_, _) =>
            {
                if (window.WindowState == System.Windows.WindowState.Minimized) window.WindowState = CsiProMax.App.Modeler ? System.Windows.WindowState.Maximized : System.Windows.WindowState.Normal;
                window.Activate();
            };
            app.MainWindow = window;

            CsiProMax.App.SwitchTo = folder =>
            {
                if (Environment.ProcessPath is not { } exe) return;
                try
                {
                    var next = Start(exe, folder, args, Environment.ProcessId);
                    next.CreateNoWindow = true;
                    Process.Start(next)?.Dispose();
                    window.Close();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { Note("switch to the newer version", ex); }
            };
            // The AI chat switched on or off in the window: this version starts again with it, read from its file.
            CsiProMax.App.Restart = () =>
            {
                if (Environment.ProcessPath is not { } exe) return;
                try
                {
                    var next = Start(exe, AppContext.BaseDirectory, args.Where(a => a != "--no-ai").ToArray(), Environment.ProcessId);
                    next.CreateNoWindow = true;
                    Process.Start(next)?.Dispose();
                    window.Close();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { Note("start again", ex); }
            };
            // A version downloaded while an older window was busy, or by another window: used as soon as this one is idle.
            window.ContentRendered += (_, _) => CsiProMax.App.SwitchWhenIdle();
            using var life = new CancellationTokenSource();
            var surface = new Thread(() =>
            {
                while (WaitHandle.WaitAny([show, life.Token.WaitHandle]) == 0)
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Opened again from the Tools menu while a newer version is downloaded: this window gives way
                        // to it, unless work is going on (then it comes forward, and switches when the work is done).
                        if (!CsiProMax.App.IsBusy && Updater.NewestDownloaded(Updater.Current) is { } ready && CsiProMax.App.SwitchTo is { } go) go(ready);
                        else
                        {
                            window.Surface();
                            // The Modeler is its 3D view: double-clicked again, that window opens (or comes forward) too.
                            if (CsiProMax.App.Modeler)
                                try { CsiProMax.Host.ModelView.Open(); }
                                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Note("modeler view", ex); }
                        }
                        // Told to the start that asked: it ends, knowing a window answered.
                        try { shown.Set(); } catch (ObjectDisposedException) { }
                    }));
            }) { IsBackground = true, Name = "CsiProMax show" };
            surface.Start();
            // Started from an ETABS window: when that ETABS is closed, this closes too.
            if (etabsPid is { } watched)
                _ = Task.Run(async () =>
                {
                    while (!life.IsCancellationRequested && EtabsIsOpen(watched)) await Task.Delay(3000).ConfigureAwait(false);
                    if (!life.IsCancellationRequested)
                        _ = window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // The chats are saved as they go; the window says why it closes.
                            CsiProMax.ETABS.TaskDialog.Show("CSI ProMax", $"{program.Name} was closed, so CSI ProMax closes too. Your chats are saved: open {program.Name} and start CSI ProMax from its Tools menu to carry on.");
                            window.Close();
                        }));
                });

            app.Run(window);
            life.Cancel();
            // The window is closed: this process ends within seconds, whatever is still winding down, so it never
            // stays behind with no window and keeps the next start of the Modeler (or this program's window) from opening.
            var stopped = Task.Run(CsiProMax.App.Stop);
            if (!stopped.Wait(TimeSpan.FromSeconds(15))) Note("close", new TimeoutException("CSI ProMax was still stopping 15 seconds after its window closed; it ends now."));
            Holder.Clear(name);
            Environment.Exit(0);
            return 0;
        }
        catch (Exception ex)
        {
            // Started without a console, so a failed start would show nothing: say it, and keep it for a bug report.
            Note("start", ex);
            if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, "CSI ProMax could not start.\n\n" + ex.Message + "\n\nThe details are in " + NoteFile, "CSI ProMax", 0x30);
            return 1;
        }
    }

    /// <summary>
    /// The Viewer alone (--viewer [file]): the Viewer page in a CSI ProMax window, with the pack inside when one is
    /// named. Closing the window ends it. With no WebView2 on this PC the page opens in Edge's app window instead.
    /// </summary>
    private static int RunViewer(string[] args)
    {
        var at = Array.IndexOf(args, "--viewer");
        var file = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : null;
        string page;
        try { page = CsiProMax.Host.WebPages.ViewerPageFor(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Note("viewer file", ex);
            if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, $"{Path.GetFileName(file)} could not be opened:\n\n{ex.Message}\n\nThe Viewer opens empty: use File > Open.", "CSI ProMax Viewer", 0x30);
            page = CsiProMax.Host.WebPages.ViewerPageFor(null);
        }
        var address = CsiProMax.Host.WebPages.Address(page);
        if (!CsiProMax.Host.WebWindow.Available())
        {
            CsiProMax.Host.WebPages.OpenOutside(address);
            return 0;
        }
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnLastWindowClose };
        app.DispatcherUnhandledException += (_, e) => Note("viewer window", e.Exception);
        CsiProMax.Host.WebWindow.Install();
        app.Startup += (_, _) => app.MainWindow = CsiProMax.Host.WebWindow.ShowHere(address, CsiProMax.Host.WebPages.Kind.Viewer);
        app.Run();
        return 0;
    }

    /// <summary>
    /// CSI ProMax's own name in the taskbar: its windows are grouped under CSI ProMax with its icon, not under dotnet
    /// (the program that runs CsiProMax.dll), so the Modeler and the Viewer look like the programs they are.
    /// </summary>
    private static void NameForTaskbar()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SetCurrentProcessExplicitAppUserModelID(AppId); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    /// <summary>The taskbar's name for CSI ProMax's windows (Windows' AppUserModelID).</summary>
    internal const string AppId = "CsiProMax.CSIProMax";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>For the windows: an error kept in errors.txt, as the start's own are.</summary>
    internal static void NoteFrom(string where, Exception? ex) => Note(where, ex);

    /// <summary>
    /// Started while another CSI ProMax holds this window's name (the Modeler, the demo, or the window of one CSI
    /// program window). Usually that one is open: it is asked to come forward, and this start ends. But a double-click on
    /// the Modeler once did nothing at all (2026-11-26): the one holding the name had no window (it was closing after an
    /// update, or was left behind). So: a start that gives way to an older version waits for it to end; one that gets
    /// no answer within 8 seconds ends the holder if it has no window, and opens itself. A holder with a window that does
    /// not answer is left alone (it may hold an unsaved model) and the user is told. True: this start takes the name.
    /// </summary>
    internal static bool Handover(Mutex one, EventWaitHandle show, EventWaitHandle shown, string name, string[] args)
    {
        // Started by an older window that gave way to this version: it is closing, so wait for the name.
        if (args.Contains("--after-pid") && Take(one, TimeSpan.FromSeconds(60))) return true;
        show.Set();
        if (shown.WaitOne(TimeSpan.FromSeconds(8))) return false;
        // A version from before 0.4.43 writes no holder file: it is looked for by its command line instead.
        var holder = Holder.Read(name) ?? Holder.Find(name);
        if (holder is { } stale && stale.HasExited is false)
        {
            stale.Refresh();
            if (stale.MainWindowHandle != IntPtr.Zero)
            {
                if (OperatingSystem.IsWindows())
                    MessageBoxW(IntPtr.Zero, "CSI ProMax is open but not answering (it may be busy with a long step).\n\nWait a little and try again, or close it from the taskbar.", "CSI ProMax", 0x40);
                stale.Dispose();
                return false;
            }
            Note("start", new InvalidOperationException($"CSI ProMax (process {stale.Id}) held '{name}' with no window and did not answer; it is ended so this start can open."));
            try { stale.Kill(true); stale.WaitForExit(10000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            stale.Dispose();
        }
        if (Take(one, TimeSpan.FromSeconds(15))) return true;
        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, "CSI ProMax could not open: another copy of it is still closing.\n\nTry again in a minute. The details are in " + NoteFile, "CSI ProMax", 0x30);
        Note("start", new TimeoutException($"'{name}' was still held after the copy holding it was asked to end."));
        return false;
    }

    private static bool Take(Mutex one, TimeSpan wait)
    {
        try { return one.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; } // its holder ended without letting go: the name is this one's now
    }

    /// <summary>Which process holds a window's name: written beside errors.txt, with its start time so a reused process id is never taken for it.</summary>
    internal static class Holder
    {
        internal static string File(string name) => Path.Combine(Path.GetDirectoryName(NoteFile)!, name + ".pid");

        internal static void Write(string name)
        {
            try
            {
                using var me = Process.GetCurrentProcess();
                Directory.CreateDirectory(Path.GetDirectoryName(NoteFile)!);
                System.IO.File.WriteAllText(File(name), Line(me.Id, me.StartTime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        internal static void Clear(string name)
        {
            try
            {
                if (Parse(System.IO.File.Exists(File(name)) ? System.IO.File.ReadAllText(File(name)) : null) is { } held && held.Pid == Environment.ProcessId)
                    System.IO.File.Delete(File(name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        internal static string Line(int pid, DateTime started) => pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + started.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        internal static (int Pid, long Ticks)? Parse(string? text)
        {
            var parts = text?.Trim().Split('|');
            return parts is { Length: 2 } && int.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var ticks) ? (pid, ticks) : null;
        }

        /// <summary>
        /// The dotnet that runs CsiProMax.dll with this name's flag (--modeler, --demo), from Windows' own process list,
        /// for a holder that wrote no file (an older version). Only the Modeler's and the demo's: one per PC.
        /// </summary>
        internal static Process? Find(string name)
        {
            var flag = name switch { "CsiProMax.modeler" => "--modeler", "CsiProMax.demo" => "--demo", _ => null };
            if (flag is null || !OperatingSystem.IsWindows()) return null;
            try
            {
                var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                             "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { $_.CommandLine -like '*CsiProMax.dll*' -and $_.CommandLine -like '*" + flag + "*' -and $_.CommandLine -notlike '*--viewer*' } | ForEach-Object { $_.ProcessId }" })
                    info.ArgumentList.Add(a);
                using var ps = Process.Start(info);
                if (ps is null) return null;
                var output = ps.StandardOutput.ReadToEnd();
                ps.WaitForExit(15000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(line, out var pid) && pid != Environment.ProcessId)
                        try { return Process.GetProcessById(pid); }
                        catch (ArgumentException) { }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
            return null;
        }

        /// <summary>The process that holds <paramref name="name"/>, when it is still the one that wrote the file (same id and start time); otherwise null.</summary>
        internal static Process? Read(string name)
        {
            try
            {
                if (Parse(System.IO.File.Exists(File(name)) ? System.IO.File.ReadAllText(File(name)) : null) is not { } held || held.Pid == Environment.ProcessId) return null;
                var process = Process.GetProcessById(held.Pid);
                if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - held.Ticks) < TimeSpan.TicksPerSecond) return process;
                process.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return null;
        }
    }

    private static bool EtabsIsOpen(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    internal static string NoteFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "errors.txt");

    private static void Note(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NoteFile)!);
            File.AppendAllText(NoteFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] list, uint count);

    /// <summary>
    /// Started from a shortcut or by double-click, dotnet opens a console window of its own that stays in the taskbar.
    /// When this is the only program in that console it is let go, which closes it. A terminal someone started this
    /// from has other programs in it and is left alone.
    /// </summary>
    private static void DropOwnConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { if (GetConsoleProcessList(new uint[4], 4) == 1) FreeConsole(); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }
}
