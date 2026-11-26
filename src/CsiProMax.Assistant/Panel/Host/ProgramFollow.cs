using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Host;

/// <summary>
/// The floating CSI ProMax window follows the CSI program the engineer is working in, whichever program's Tools menu
/// it was opened from (Ahmed, 2026-11-26: opened in SAFE, then he went to ETABS, and the window still worked on SAFE; a
/// tester's field test, started from SAFE's window, was named SAFE while he worked in ETABS 20).
///
/// A window is made for one program window (its link, its tools, its chats by model, its Tools-menu name), so it
/// follows the way it takes an update: when another ETABS, SAFE or SAP2000 window has been in front for a moment and
/// nothing is running here, this window starts again for that program in the same place and closes. It never follows
/// while a chat, a job or a test is working, never a test's own scratch copy, never a program that has its own
/// CSI ProMax window open, and the Modeler and the Viewer (which are not tied to a program) never follow.
/// When the program it works beside is closed and another is open, it goes to that one instead of closing.
/// </summary>
internal static class ProgramFollow
{
    /// <summary>The window in front: its process, and the title of that process's main window.</summary>
    internal sealed record Front(int Pid, string ProcessName, string Title);

    /// <summary>How long another program must stay in front, with nothing running here, before the window follows it.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The CSI program the window in front belongs to, when this window may follow it: another window than the one it
    /// works beside (another program when it works beside none in particular), and not a field test's scratch copy.
    /// </summary>
    internal static CsiProgram? Followable(Front front, int? currentPid, CsiProgram current)
    {
        if (front.Pid == currentPid) return null;
        if (front.Title.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase)) return null;
        var program = CsiProgram.All.FirstOrDefault(p => p.Ready && string.Equals(p.ProcessName, front.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (program is null) return null;
        return currentPid is null && program.Key == current.Key ? null : program;
    }

    /// <summary>
    /// Told the window in front again and again; answers where to go. The program to follow is the CSI program window
    /// the engineer touched last: it stays the one to follow while this window, or any other application, is in front,
    /// and is dropped when the program this window works beside comes in front again. The window goes there once that
    /// program has been the last one touched for <see cref="Settle"/> with nothing running here.
    /// </summary>
    internal sealed class Tracker
    {
        private (int Pid, CsiProgram Program)? _to;
        private DateTime _since;

        public (int Pid, CsiProgram Program)? Tick(Front? front, int? currentPid, CsiProgram current, bool busy, DateTime now)
        {
            if (front is not null)
            {
                var own = front.Pid == currentPid || (currentPid is null && string.Equals(front.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (own) _to = null;
                else if (Followable(front, currentPid, current) is { } program && _to?.Pid != front.Pid) { _to = (front.Pid, program); _since = now; }
            }
            if (_to is null) return null;
            // Work going on here keeps the window where it is; the wait starts again when the work ends.
            if (busy) { _since = now; return null; }
            return now - _since >= Settle ? _to : null;
        }

        /// <summary>The program to follow is gone, or has its own window: it is forgotten.</summary>
        public void Forget() => _to = null;
    }

    /// <summary>The start arguments for the same window beside another program window.</summary>
    internal static string[] Args(string[] args, int pid, CsiProgram program)
    {
        var next = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--etabs-pid" or "--program") { i++; continue; }
            next.Add(args[i]);
        }
        next.AddRange(["--etabs-pid", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--program", program.Key]);
        return [.. next];
    }

    /// <summary>The name a CSI ProMax window holds for the program window it works beside (Program.Main).</summary>
    internal static string WindowName(int pid) => "CsiProMax." + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when a CSI ProMax window is already open for this program window: it is left to that one.</summary>
    internal static bool HasWindow(int pid)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { if (Mutex.TryOpenExisting(WindowName(pid), out var held)) { held.Dispose(); return true; } }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return true; }
        return false;
    }

    /// <summary>The window in front now; null when there is none or it cannot be read.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Front? ReadFront()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            return new Front((int)pid, process.ProcessName, process.MainWindowTitle ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Another CSI program window that is open, for when the one this window worked beside is closed: one with a main
    /// window, that is not a test's scratch copy and has no CSI ProMax window of its own. Null when there is none.
    /// </summary>
    internal static (int Pid, CsiProgram Program)? Another(int gonePid)
    {
        foreach (var program in CsiProgram.All.Where(p => p.Ready))
        {
            Process[] running;
            try { running = Process.GetProcessesByName(program.ProcessName); }
            catch (InvalidOperationException) { continue; }
            try
            {
                foreach (var process in running)
                {
                    try
                    {
                        if (process.Id == gonePid || process.HasExited || process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                        if (Followable(new Front(process.Id, process.ProcessName, process.MainWindowTitle ?? ""), gonePid, program) is null) continue;
                        if (HasWindow(process.Id)) continue;
                        return (process.Id, program);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* it is closing */ }
                }
            }
            finally { foreach (var process in running) process.Dispose(); }
        }
        return null;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
