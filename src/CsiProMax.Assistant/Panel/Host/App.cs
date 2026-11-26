using CsiProMax.Assistant;
using System.IO;
using System.Windows.Threading;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Bridge;
using CsiProMax.Settings;
using CsiProMax.Tools;
using CsiProMax.Update;

namespace CsiProMax;

/// <summary>
/// CSI ProMax's running parts, started once on the window's thread: the dispatcher that runs tools, the tool list,
/// the bridge with its jobs and approvals, the chat's own connection for Claude Code, and the watch on which model is
/// open in ETABS. It has the shape of Sketch ProMax's App (which Revit starts), so the panel code is the same in both.
/// </summary>
public static class App
{
    public static EtabsDispatcher Dispatcher { get; private set; } = new();

    public static BridgeServer? Bridge { get; private set; }

    /// <summary>ETABS's version as it says it ("23.2.0"), "" before ETABS answered.</summary>
    internal static string ETABSYear { get; private set; } = "";

    /// <summary>ETABS's own main window (not ours), to tell when a dialog or a long operation holds it; zero when unknown.</summary>
    internal static IntPtr MainWindow { get; private set; }

    /// <summary>The window's thread.</summary>
    internal static Dispatcher? Ui { get; private set; }

    /// <summary>The attached ETABS.</summary>
    internal static EtabsApp? Etabs { get; private set; }

    /// <summary>The process id of the ETABS this was started from; null when started by hand (the first ETABS found).</summary>
    internal static int? EtabsPid { get; private set; }

    /// <summary>True in the demo (a small model in memory, no ETABS).</summary>
    internal static bool Demo { get; private set; }

    /// <summary>True in the Modeler: CSI ProMax's own model with no ETABS behind it, saved as ETABS text files.</summary>
    internal static bool Modeler => Etabs?.Link is CsiProMax.Assistant.Etabs.OfflineEtabs;

    /// <summary>What the window and the chat call themselves.</summary>
    internal static string Product => Modeler ? Engine : "CSI ProMax";

    /// <summary>The Modeler and the results Viewer are one program, named so (Ahmed 2026-11-26).</summary>
    internal const string Engine = "CSI ProMax Engine";

    /// <summary>Sketch ProMax's shared-tools mode; never on here.</summary>
    internal static bool Shared => false;

    private static CancellationTokenSource? _life;
    private static bool _started;

    internal static bool IsBusy => Jobs.Running is not null || Dispatcher.HasWork || Chat.ChatPane.IsBusy;

    internal static bool ETABSWorking => Jobs.Running is not null || Dispatcher.HasWork;

    /// <summary>Starts everything. Call on the window's thread, before the window shows.</summary>
    internal static void Start(ICsiLink link, int? etabsPid, bool demo)
    {
        if (_started) return;
        _life = new CancellationTokenSource();
        Ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        EtabsPid = etabsPid;
        Demo = demo;
        Etabs = new EtabsApp(link);
        MainWindow = FindEtabsWindow(etabsPid);
        Dispatcher = new EtabsDispatcher();
        Dispatcher.Initialize(Etabs);
        Host.ModelWatch.Start(link);
        DropForeignHistory();
        var registry = ToolRegistry.CreateDefault(Modeler, link.Program);
        Bridge = new BridgeServer(registry, Dispatcher, ETABSYear);
        Bridge.Start();
        _started = true;
        // The local server carries the 3D view and hand modelling too, so it runs with the AI chat off as well.
        try { ChatMcp.Start(Bridge); } catch (Exception) { /* the chat says what is wrong when a message is sent */ }
        // With the AI chat off (AiMode) nothing of the chat is started: no Claude Code is looked for, offered or run.
        if (AiMode.On)
        {
            try { Chat.ChatPane.Start(Etabs); } catch (Exception) { /* the chat still works, with one conversation */ }
            Chat.Autopilot.ModelTitle = () => Chat.ChatPane.CurrentModelTitle;
        }
        try { CrashGuard.Start(Ui); } catch (Exception) { }
        // This PC's license (Licensing/LicenseState): without a valid key the License window opens at the first idle moment.
        try
        {
            if (!Licensing.LicenseState.Allowed) Dispatcher.Later(() => UI.LicenseWindow.Open());
        }
        catch (Exception) { /* the License button still opens it */ }

        var token = _life.Token;
        _ = Task.Run(() => BackgroundStartAsync(link, token));
        _ = Task.Run(Host.Shortcuts.Ensure);
        // Tools > CSI ProMax kept in every CSI program while CSI ProMax is closed, too (MenuKeeper, keep-plugins.ps1).
        _ = Task.Run(CsiProMax.Assistant.MenuKeeper.Ensure);
        // The team's learned tools: published from here and received from the others, every hour (Tools/TeamLibrary).
        try { if (!Demo) TeamLibrary.Start(token); } catch (Exception) { /* team_library says what is wrong when asked */ }
        RunningCall.Version = Updater.CurrentText;
        RunningCall.Enabled = true;
        _ = Task.Run(() => { foreach (var line in RunningCall.Recover()) ActivityLog.Note("etabs_closed_during", line); });
    }

    /// <summary>The window is closing: end the chat's engine, save, and let go of everything.</summary>
    internal static void Stop()
    {
        if (!_started) return;
        _started = false;
        try { _life?.Cancel(); } catch (ObjectDisposedException) { }
        if (AiMode.On)
        {
            try { Chat.ChatPane.Shutdown(); } catch (Exception) { }
            try { if (Etabs is { } app) Chat.ChatPane.Stop(app); } catch (Exception) { }
        }
        try { ChatMcp.Stop(); } catch (Exception) { }
        try { Bridge?.Stop(); } catch (Exception) { }
        Host.ModelWatch.Stop();
        try { if (Etabs is { } app) Dispatcher.Shutdown(app); } catch (Exception) { }
    }

    /// <summary>
    /// Once per PC: the first test build (25 Nov 2026) built its code library and job recipes from every Claude Code
    /// conversation on the PC, Revit work included. Those two folders are emptied so they are built again from ETABS
    /// ProMax's own chats only (Chat.HistoryArchive.Ours). Nothing of the user's own is touched.
    /// </summary>
    private static void DropForeignHistory()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsiProMax");
            var mark = Path.Combine(root, "own-history-only.txt");
            if (File.Exists(mark)) return;
            foreach (var name in new[] { "worked-code", "job-recipes" })
            {
                var folder = Path.Combine(root, name);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            Directory.CreateDirectory(root);
            File.WriteAllText(mark, DateTime.UtcNow.ToString("s"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* tried again at the next start */ }
    }

    private static IntPtr FindEtabsWindow(int? pid)
    {
        try
        {
            if (pid is { } id)
            {
                using var etabs = System.Diagnostics.Process.GetProcessById(id);
                return etabs.MainWindowHandle;
            }
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Csi.Program.ProcessName))
                using (p)
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return IntPtr.Zero;
    }

    /// <summary>How long after start the network work waits, so the window is up first.</summary>
    internal static readonly TimeSpan BackgroundDelay = TimeSpan.FromSeconds(20);

    /// <summary>How soon after the window opens updates are first looked for.</summary>
    internal static readonly TimeSpan UpdateDelay = TimeSpan.FromSeconds(3);

    private static async Task BackgroundStartAsync(ICsiLink link, CancellationToken stop)
    {
        // ETABS's version, for the bug reports and the system prompt: asked off the window's thread.
        try { ETABSYear = link.Info().Version; } catch (Exception) { }
        // Updates are looked for a moment after the window opens, not after the other background work: a version that
        // is out is downloaded and switched to while the engineer is still reading the first screen.
        var updates = Task.Run(async () =>
        {
            try { await Task.Delay(UpdateDelay, stop).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            await UpdateLoopAsync(stop).ConfigureAwait(false);
        });
        try { await Task.Delay(BackgroundDelay, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        // Tools > CSI ProMax in every CSI program of this user: added where it is missing (a program that had no
        // settings yet, or was open, when CSI ProMax was installed). What it did goes in the activity log.
        try
        {
            foreach (var entry in CsiProMax.Assistant.ToolsMenu.Repair().Where(e => e.State is not CsiProMax.Assistant.ToolsMenu.State.Already))
                ActivityLog.Note("tools_menu", entry.Says);
        }
        catch (Exception) { /* Help > Tools menu says what is wrong when asked */ }
        // The installed plug-in files follow the version that runs, with the programs open (PlugInRefresh). Only a
        // downloaded version does this: a copy unpacked elsewhere to try a version never writes into the installed folder.
        try
        {
            if (CsiProMax.Assistant.ToolsMenu.RunsDownloadedVersion && CsiProMax.Assistant.ToolsMenu.InstallFolder() is { } installed)
            {
                foreach (var line in PlugInRefresh.Run(AppContext.BaseDirectory, installed, CsiProMax.Assistant.ToolsMenu.PlugIns.Select(p => p.Dll)))
                    ActivityLog.Note("plug_in_update", line);
                // And everything else there: the Modeler, the Viewer and the shortcuts start from the installed folder,
                // so one update leaves every part on one version.
                if (PlugInRefresh.Everything(AppContext.BaseDirectory, installed) is { } all) ActivityLog.Note("install_update", all);
            }
        }
        catch (Exception) { /* the loaded plug-in still hands over to the newest copy by itself */ }
        // The owner's changes to keys given out (a shorter date, a cancelled key), kept for offline days.
        try { await Licensing.LicenseState.RefreshChangesAsync(stop).ConfigureAwait(false); } catch (Exception) { }
        if (stop.IsCancellationRequested) return;
        await updates.ConfigureAwait(false);
    }

    /// <summary>How often updates are looked for while the window stays open.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    private static readonly TimeSpan LoopTick = TimeSpan.FromMinutes(5);

    private static string? _offered;

    /// <summary>
    /// How the open window gives way to a downloaded version (Program sets it): the newer version is started beside the
    /// same program window and this one closes. The CSI program itself stays open, like Sketch ProMax in Revit.
    /// </summary>
    internal static Action<string>? SwitchTo { get; set; }

    /// <summary>
    /// Starts this version again in place of this window (Program sets it), beside the same program window: after the
    /// AI chat is switched on or off. The CSI program and the model stay open.
    /// </summary>
    internal static Action? Restart { get; set; }

    /// <summary>True when this window took over from one that was open (an update, or the AI chat switched): it opens nothing by itself again.</summary>
    internal static bool Reopened { get; set; }

    private static int _switching;

    /// <summary>
    /// Switches this window to the newest downloaded version as soon as no work is running (a chat answering, a tool,
    /// a test). An update is not left "downloaded" for someone to close and reopen a window: it is put to use.
    /// </summary>
    internal static void SwitchWhenIdle()
    {
        if (NoUpdate || SwitchTo is null || Updater.NewestDownloaded(Updater.Current) is null) return;
        if (Interlocked.Exchange(ref _switching, 1) == 1) return;
        var stop = _life?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                while (IsBusy || FieldTest.IsRunning || SelfTest.IsRunning) await Task.Delay(1000, stop).ConfigureAwait(false);
                if (Updater.NewestDownloaded(Updater.Current) is { } folder && SwitchTo is { } go && Ui is { } ui)
                    _ = ui.BeginInvoke(new Action(() =>
                    {
                        // Work that started in the last moment keeps this version; the next round tries again.
                        if (IsBusy) { Volatile.Write(ref _switching, 0); return; }
                        go(folder);
                    }));
                else Volatile.Write(ref _switching, 0);
            }
            catch (OperationCanceledException) { Volatile.Write(ref _switching, 0); }
        });
    }

    internal static async Task UpdateLoopAsync(CancellationToken stop)
    {
        var first = true;
        Updater.Said ??= line => { try { ActivityLog.Note("update_check", line); } catch (Exception) { } };
        try { ActivityLog.Note("update_check", $"CSI ProMax {Updater.CurrentText} looks for updates now and every {CheckEvery.TotalMinutes:0} minutes" + (NoUpdate ? " (switched off for this window: --no-update)." : ".")); } catch (Exception) { }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                bool auto;
                try { auto = AgentSettings.Load().AutoUpdate; } catch (Exception) { auto = true; }
                if (first || Updater.IsDue(CheckEvery))
                {
                    await RunUpdateAsync(force: first, interactive: false, stop, install: auto).ConfigureAwait(false);
                    first = false;
                }
                else if (!auto && Updater.Latest is { } latest && latest != _offered && Updater.Staged is null)
                {
                    _offered = latest;
                    UI.UpdateNotice.Offer(new UI.UpdateNotice.Notice(UI.UpdateNotice.Kind.Available, latest, UI.UpdateNotice.TopLines(Updater.NotesFor(latest))));
                }
            }
            catch (Exception ex)
            {
                // A failed check is retried at the next round, and never silent: the activity log says what stopped it.
                first = false;
                try { ActivityLog.Note("update_check", $"{ex.GetType().Name}: {ex.Message}"); } catch (Exception) { }
            }
            // A version this or another window downloaded, and work that was running then: switched to now.
            if (_switching == 0) SwitchWhenIdle();
            try { await Task.Delay(LoopTick, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Check now (Settings, the version button): runs in the background with the update window open from the start.</summary>
    internal static void CheckForUpdatesInteractive()
    {
        if (UpdateProgress.Current is { } running && !running.Snapshot.IsFinal && running.Snapshot.Stage != UpdateStage.Idle)
        {
            running.Surfaced = true;
            UI.UpdateWindow.ShowFor(running);
            return;
        }
        var token = _life?.Token ?? CancellationToken.None;
        _ = Task.Run(() => RunUpdateAsync(force: true, interactive: true, token));
    }

    /// <summary>
    /// Started with --no-update (the window check of a build, tests/window.Tests.ps1): this window neither looks for a
    /// newer version by itself nor switches to one. Without it, a build older than the newest release updated itself
    /// and its window was gone twelve seconds later, which failed the check of a pull request that had done nothing wrong.
    /// </summary>
    public static bool NoUpdate { get; set; }

    private static async Task RunUpdateAsync(bool force, bool interactive, CancellationToken stop, bool install = true)
    {
        if (NoUpdate && !interactive) return;
        var progress = new UpdateProgress { Interactive = interactive };
        UpdateProgress.Current = progress;
        if (interactive) Surface(progress);
        else
            progress.Changed += s =>
            {
                if (s.Stage is UpdateStage.Downloading or UpdateStage.Applying or UpdateStage.RestartNeeded or UpdateStage.Available) progress.Surfaced = true;
                if (s.Stage == UpdateStage.Available && s.Version is { } available)
                {
                    _offered = available;
                    UI.UpdateNotice.Offer(new UI.UpdateNotice.Notice(UI.UpdateNotice.Kind.Available, available,
                        UI.UpdateNotice.TopLines(Updater.NotesFor(available))));
                }
                else if (s.Stage == UpdateStage.RestartNeeded && s.Version is { } installed)
                    UI.UpdateNotice.Offer(new UI.UpdateNotice.Notice(UI.UpdateNotice.Kind.Installed, installed,
                        UI.UpdateNotice.TopLines(Updater.NotesFor(installed)), s.Detail));
            };
        try { await Updater.CheckAsync(force, progress, stop, install).ConfigureAwait(false); }
        finally { await SyncBugLogAsync(stop).ConfigureAwait(false); }
        SwitchWhenIdle();
    }

    private static async Task SyncBugLogAsync(CancellationToken stop)
    {
        try { await Bugs.BugLog.SyncAsync($"CsiProMax/{Updater.CurrentText}", stop).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static void Surface(UpdateProgress progress)
    {
        progress.Surfaced = true;
        UpdateProgress.Current = progress;
        var ui = Ui;
        if (ui is null) return;
        ui.BeginInvoke(new Action(() =>
        {
            try { UI.UpdateWindow.ShowFor(progress); }
            catch (Exception) { }
        }));
    }
}
