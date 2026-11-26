using System.Reflection;

namespace CsiProMaxPlugin;

/// <summary>
/// Lets a fix to the plug-in reach a program that is open, by the normal update (Ahmed, 2026-11-25: an update must
/// never need the programs closed; a tester's SAFE plug-in was still 0.4.5 on 2026-11-26 because the program keeps
/// its plug-in file loaded and only a reinstall with the programs closed replaced it).
/// The program loads the installed plug-in file once and keeps it. So each time Tools > CSI ProMax is picked, and each
/// time a window asks, the installed plug-in looks for a newer copy of the plug-in among the versions the updater
/// downloaded (%LOCALAPPDATA%\CsiProMax\versions\&lt;version&gt;, each checked file by file when it was downloaded), loads
/// it from memory (no file is locked) and lets it serve the pipe and open the window (<see cref="Live"/>). The
/// installed file then only has to change when this hand-over itself changes.
/// </summary>
public static class Newest
{
    /// <summary>The plug-in file every version holds, with the pipe and the launcher in it, whichever program loads it.</summary>
    public const string FileName = "CsiProMaxPlugin.dll";

    private static readonly object Gate = new();
    private static object? _model;
    private static Type? _live;
    private static Version _serving = Own;

    /// <summary>For the tests: the versions folder instead of this PC's.</summary>
    internal static string? VersionsFolder { get; set; }

    private static Version Own => Three(typeof(Newest).Assembly.GetName().Version);

    private static Version Three(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string Versions => VersionsFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "versions");

    /// <summary>
    /// The newest complete downloaded version above <paramref name="above"/> that holds the plug-in file, or null.
    /// Never throws: a folder that cannot be read is as good as none.
    /// </summary>
    internal static (string File, Version Version)? Find(string versionsFolder, Version above)
    {
        try
        {
            if (!Directory.Exists(versionsFolder)) return null;
            (string, Version)? best = null;
            foreach (var folder in Directory.GetDirectories(versionsFolder))
            {
                if (!Version.TryParse(Path.GetFileName(folder), out var named)) continue;
                var version = Three(named);
                var file = Path.Combine(folder, FileName);
                if (version <= above || (best is { } b && version <= b.Item2)) continue;
                if (!File.Exists(Path.Combine(folder, ".complete")) || !File.Exists(file)) continue;
                best = (file, version);
            }
            return best;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Called by the plug-in when Tools > CSI ProMax is picked. True when a newer copy of the plug-in serves the model
    /// and has opened the window; false when the installed plug-in is the newest and does both itself. A failure to
    /// open the window is thrown, as the installed plug-in's own would be.
    /// </summary>
    public static bool Hand(object? model, string pluginFolder)
    {
        if (model is null) return false;
        Type? live;
        lock (Gate)
        {
            _model = model;
            try { Switch(); }
            catch (Exception) { /* the copy could not be loaded: what serves now carries on */ }
            live = _live;
            if (live is null) { ApiBridge.Renewer = Renew; return false; }
            try { Call(live, "Serve", model, (Func<bool>)Renew); }
            catch (Exception) { _live = null; _serving = Own; ApiBridge.Renewer = Renew; return false; }
        }
        Call(live, "Start", pluginFolder);
        return true;
    }

    /// <summary>
    /// Asked by a window (<see cref="ApiWire.Renew"/>): true when a newer copy of the plug-in was found and now serves
    /// the pipe, so the window should connect again. It never throws.
    /// </summary>
    public static bool Renew()
    {
        try
        {
            lock (Gate)
            {
                if (_model is not { } model || !Switch()) return false;
                Call(_live!, "Serve", model, (Func<bool>)Renew);
                return true;
            }
        }
        catch (Exception) { return false; }
    }

    /// <summary>Loads the newest copy above the one that serves, and stops the one that serves. False when there is none newer.</summary>
    private static bool Switch()
    {
        if (Find(Versions, _serving) is not { } pick) return false;
        // From memory: the version folder stays free for the updater to tidy, and a download mark on the file does not matter.
        var live = Assembly.Load(File.ReadAllBytes(pick.File)).GetType("CsiProMaxPlugin.Live", true)!;
        foreach (var name in new[] { "Serve", "Start", "Stop" })
            if (live.GetMethod(name) is null) throw new MissingMethodException("CsiProMaxPlugin.Live", name);
        if (_live is { } old) { try { Call(old, "Stop"); } catch (Exception) { /* it serves on beside the new one */ } }
        else ApiBridge.Stop();
        _live = live;
        _serving = pick.Version;
        return true;
    }

    private static void Call(Type live, string method, params object[] values)
    {
        try { live.GetMethod(method)!.Invoke(null, values); }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new IOException(inner.Message, inner); }
    }

    /// <summary>For the tests: as a program that has just loaded the plug-in.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            if (_live is { } old) { try { Call(old, "Stop"); } catch (Exception) { } }
            ApiBridge.Stop();
            _live = null; _model = null; _serving = Own;
        }
    }
}
