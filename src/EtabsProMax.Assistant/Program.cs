using System.Diagnostics;
using System.Runtime.InteropServices;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Update;

namespace EtabsProMax.Assistant;

/// <summary>
/// ETABS ProMax: Sketch ProMax's panel in a window of its own beside ETABS. Started without an .exe of its own, with the
/// .NET 8 that ETABS 23 itself runs on: dotnet EtabsProMax.dll, or from ETABS's Tools menu (the plug-in).
///   --etabs-pid N   the ETABS window this was started from (the plug-in passes it), when several are open
///   --demo          a small model in memory instead of ETABS, to see the window on a PC without ETABS
///   --selftest      check the link to ETABS call by call and print the result (--changes also adds and deletes objects)
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int? etabsPid = Array.IndexOf(args, "--etabs-pid") is var at and >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var pid) ? pid : null;
        var demo = args.Contains("--demo");
        if (args.Contains("--selftest"))
        {
            using var check = new ComEtabs(etabsPid);
            return SelfTest.Run(demo ? new MemoryEtabs() : check, args.Contains("--changes"));
        }
        try
        {
            // A newer version the updater downloaded is started instead of this one (once: it is told not to look again).
            if (!args.Contains("--this-version") && Updater.NewestDownloaded(Updater.Current) is { } newer && Environment.ProcessPath is { } dotnet)
            {
                var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = newer };
                info.ArgumentList.Add(Path.Combine(newer, "EtabsProMax.dll"));
                foreach (var a in args) info.ArgumentList.Add(a);
                info.ArgumentList.Add("--this-version");
                try { Process.Start(info)?.Dispose(); return 0; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { /* this version runs */ }
            }

            // One window per ETABS: started again from ETABS's menu, the open window comes forward.
            var name = "EtabsProMax." + (demo ? "demo" : etabsPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any");
            using var one = new Mutex(true, name, out var first);
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
            if (!first) { show.Set(); return 0; }

            // Started for an ETABS that is no longer open: say so, instead of a window that closes by itself.
            if (!demo && etabsPid is { } asked && !EtabsIsOpen(asked))
            {
                if (OperatingSystem.IsWindows())
                    MessageBoxW(IntPtr.Zero, "The ETABS window ETABS ProMax was started for is closed.\n\nOpen ETABS and start ETABS ProMax from its Tools menu.", "ETABS ProMax", 0x40);
                return 2;
            }
            using var com = new ComEtabs(etabsPid);
            IEtabsLink link = demo ? new MemoryEtabs() : com;
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) => Note("window", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Note("unhandled", e.ExceptionObject as Exception);
            EtabsProMax.App.Start(link, etabsPid, demo);
            var window = new EtabsProMax.Host.MainWindow();
            app.MainWindow = window;

            using var life = new CancellationTokenSource();
            var surface = new Thread(() =>
            {
                while (WaitHandle.WaitAny([show, life.Token.WaitHandle]) == 0)
                    window.Dispatcher.BeginInvoke(new Action(window.Surface));
            }) { IsBackground = true, Name = "EtabsProMax show" };
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
                            EtabsProMax.ETABS.TaskDialog.Show("ETABS ProMax", "ETABS was closed, so ETABS ProMax closes too. Your chats are saved: open ETABS and start ETABS ProMax from its Tools menu to carry on.");
                            window.Close();
                        }));
                });

            app.Run(window);
            life.Cancel();
            EtabsProMax.App.Stop();
            return 0;
        }
        catch (Exception ex)
        {
            // Started without a console, so a failed start would show nothing: say it, and keep it for a bug report.
            Note("start", ex);
            if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, "ETABS ProMax could not start.\n\n" + ex.Message + "\n\nThe details are in " + NoteFile, "ETABS ProMax", 0x30);
            return 1;
        }
    }

    private static bool EtabsIsOpen(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    internal static string NoteFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtabsProMax", "errors.txt");

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
}
