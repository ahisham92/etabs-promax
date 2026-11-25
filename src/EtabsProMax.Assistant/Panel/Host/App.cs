using System.IO;
using System.Windows.Threading;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Bridge;
using EtabsProMax.Settings;
using EtabsProMax.Tools;
using EtabsProMax.Update;

namespace EtabsProMax;

/// <summary>
/// ETABS ProMax's running parts, started once on the window's thread: the dispatcher that runs tools, the tool list,
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

    /// <summary>Sketch ProMax's shared-tools mode; never on here.</summary>
    internal static bool Shared => false;

    private static CancellationTokenSource? _life;
    private static bool _started;

    internal static bool IsBusy => Jobs.Running is not null || Dispatcher.HasWork || Chat.ChatPane.IsBusy;

    internal static bool ETABSWorking => Jobs.Running is not null || Dispatcher.HasWork;

    /// <summary>Starts everything. Call on the window's thread, before the window shows.</summary>
    internal static void Start(IEtabsLink link, int? etabsPid, bool demo)
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
        var registry = ToolRegistry.CreateDefault();
        Bridge = new BridgeServer(registry, Dispatcher, ETABSYear);
        Bridge.Start();
        _started = true;
        try { ChatMcp.Start(Bridge); } catch (Exception) { /* the chat says what is wrong when a message is sent */ }
        try { Chat.ChatPane.Start(Etabs); } catch (Exception) { /* the chat still works, with one conversation */ }
        Chat.Autopilot.ModelTitle = () => Chat.ChatPane.CurrentModelTitle;
        try { CrashGuard.Start(Ui); } catch (Exception) { }
        // This PC's license (Licensing/LicenseState): without a valid key the License window opens at the first idle moment.
        try
        {
            if (!Licensing.LicenseState.Allowed) Dispatcher.Later(() => UI.LicenseWindow.Open());
        }
        catch (Exception) { /* the License button still opens it */ }

        var token = _life.Token;
        _ = Task.Run(() => BackgroundStartAsync(link, token));
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
        try { Chat.ChatPane.Shutdown(); } catch (Exception) { }
        try { if (Etabs is { } app) Chat.ChatPane.Stop(app); } catch (Exception) { }
        try { ChatMcp.Stop(); } catch (Exception) { }
        try { Bridge?.Stop(); } catch (Exception) { }
        Host.ModelWatch.Stop();
        try { if (Etabs is { } app) Dispatcher.Shutdown(app); } catch (Exception) { }
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
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("ETABS"))
                using (p)
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return IntPtr.Zero;
    }

    /// <summary>How long after start the network work waits, so the window is up first.</summary>
    internal static readonly TimeSpan BackgroundDelay = TimeSpan.FromSeconds(20);

    private static async Task BackgroundStartAsync(IEtabsLink link, CancellationToken stop)
    {
        // ETABS's version, for the bug reports and the system prompt: asked off the window's thread.
        try { ETABSYear = link.Info().Version; } catch (Exception) { }
        try { await Task.Delay(BackgroundDelay, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        // The owner's changes to keys given out (a shorter date, a cancelled key), kept for offline days.
        try { await Licensing.LicenseState.RefreshChangesAsync(stop).ConfigureAwait(false); } catch (Exception) { }
        if (stop.IsCancellationRequested) return;
        await UpdateLoopAsync(stop).ConfigureAwait(false);
    }

    /// <summary>How often updates are looked for while the window stays open.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    private static readonly TimeSpan LoopTick = TimeSpan.FromMinutes(5);

    private static string? _offered;

    internal static async Task UpdateLoopAsync(CancellationToken stop)
    {
        var first = true;
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
            catch (Exception) { first = false; /* a failed check is retried at the next round */ }
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

    private static async Task RunUpdateAsync(bool force, bool interactive, CancellationToken stop, bool install = true)
    {
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
    }

    private static async Task SyncBugLogAsync(CancellationToken stop)
    {
        try { await Bugs.BugLog.SyncAsync($"EtabsProMax/{Updater.CurrentText}", stop).ConfigureAwait(false); }
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
