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

    [STAThread]
    public static int Main(string[] args)
    {
        DropOwnConsole();
        int? etabsPid = Array.IndexOf(args, "--etabs-pid") is var at and >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var pid) ? pid : null;
        // The CSI program this was started from: named by the plug-in, or told from the process. ETABS when neither says.
        var program = (Array.IndexOf(args, "--program") is var named and >= 0 && named + 1 < args.Length ? CsiProgram.Find(args[named + 1]) : null) ?? CsiProgram.OfProcess(etabsPid);
        if (!program.Ready)
        {
            if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, $"CSI ProMax for {program.Name} is not built yet. It works with ETABS today; {program.Name} is planned.", "CSI ProMax", 0x40);
            return 3;
        }
        var modeler = args.Contains("--modeler");
        var demo = args.Contains("--demo") || modeler;
        if (Array.IndexOf(args, "--test-sap2000") is var sapTest and >= 0)
        {
            var s2k = sapTest + 1 < args.Length ? args[sapTest + 1] : "test-frame.s2k";
            var folder = sapTest + 2 < args.Length ? args[sapTest + 2] : Path.Combine(Path.GetTempPath(), "CsiProMax-sap-test");
            return CsiProMax.Assistant.Sap2000.SapScratchTest.Run(s2k, folder);
        }
        if (args.Contains("--fieldtest"))
        {
            // The field test without the window: prints the report and says where it was saved.
            using var check = new ComEtabs(etabsPid, program);
            // --full: every tool, on scratch models in windows of the programs the test opens itself (20 to 60 minutes).
            var link = demo ? new MemoryEtabs() : CsiProMax.Assistant.Tools.ProgramTools.Live(check);
            var outcome = args.Contains("--full")
                ? FieldTest.Run(link, new FieldTest.Options { Full = true, StartedFrom = "the command line", Progress = step => Console.Error.WriteLine("... " + step) })
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
            if (!args.Contains("--this-version") && Updater.NewestDownloaded(Updater.Current) is { } newer && Environment.ProcessPath is { } dotnet)
            {
                var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = newer };
                info.ArgumentList.Add(Path.Combine(newer, "CsiProMax.dll"));
                foreach (var a in args) info.ArgumentList.Add(a);
                info.ArgumentList.Add("--this-version");
                try { Process.Start(info)?.Dispose(); return 0; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { /* this version runs */ }
            }

            CarryOver();
            // One window per ETABS: started again from ETABS's menu, the open window comes forward.
            var name = "CsiProMax." + (modeler ? "modeler" : demo ? "demo" : etabsPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any");
            using var one = new Mutex(true, name, out var first);
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
            if (!first) { show.Set(); return 0; }

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
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Note("unhandled", e.ExceptionObject as Exception);
            CsiProMax.App.Start(link, etabsPid, demo);
            var window = new CsiProMax.Host.MainWindow();
            // Started from a shortcut, Windows hands the window the shortcut's "minimised" (meant for dotnet's console):
            // the window comes up normal and in front.
            window.ContentRendered += (_, _) =>
            {
                if (window.WindowState == System.Windows.WindowState.Minimized) window.WindowState = System.Windows.WindowState.Normal;
                window.Activate();
            };
            app.MainWindow = window;

            using var life = new CancellationTokenSource();
            var surface = new Thread(() =>
            {
                while (WaitHandle.WaitAny([show, life.Token.WaitHandle]) == 0)
                    window.Dispatcher.BeginInvoke(new Action(window.Surface));
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
            CsiProMax.App.Stop();
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
