using System.Runtime.CompilerServices;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// A picture of the scratch program's windows at the moment a step fails, so the zip shows what the program showed
/// (Ahmed, 2026-11-26: one run must bring everything needed to fix it). Only the test's own scratch window is
/// pictured, never the engineer's screen or another program. A picture that cannot be taken is left out silently:
/// it never fails or holds a test (a window that does not answer is given 5 seconds).
/// </summary>
public static class FailurePictures
{
    public const string FolderName = "Failure pictures";

    /// <summary>The most pictures one run takes: a run where everything fails would otherwise fill the zip.</summary>
    internal const int Most = 40;
    private static int _taken;

    internal static void Reset() { Volatile.Write(ref _taken, 0); lock (Started) Started.Clear(); }

    /// <summary>The work folder of the running field test, so a scratch window can picture itself when a call to it
    /// does not come back (the 0.4.73 run pictured a leftover blank ETABS four times instead of the stuck one).</summary>
    internal static string? WorkFolder { get; set; }

    /// <summary>Every scratch process the test started, oldest first.</summary>
    private static readonly List<int> Started = [];

    /// <summary>A scratch process the test started (the main scratch window, a hidden copy, a real-model copy).</summary>
    internal static void StartedProcess(int processId) { if (processId > 0) lock (Started) { Started.Remove(processId); Started.Add(processId); } }

    /// <summary>A new program's part: processes an earlier part left are not this part's windows.</summary>
    internal static void NewPart(int main) { lock (Started) { Started.Clear(); if (main > 0) Started.Add(main); } }

    /// <summary>The process a failed step's picture is of: the newest test-started process still running, so a
    /// step that worked in its own copy is pictured there, else the main scratch window.</summary>
    internal static int PictureOf(int main, Func<int, bool>? running = null)
    {
        running ??= Running;
        lock (Started)
            for (var i = Started.Count - 1; i >= 0; i--)
                if (running(Started[i])) return Started[i];
        return main;
    }

    private static bool Running(int processId)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(processId); return !process.HasExited; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>The file name for a step's picture: its number, the program, and the step's name made safe.</summary>
    internal static string NameFor(int number, CsiProgram program, string step, int window)
    {
        var safe = new string(step.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
        if (safe.Length > 60) safe = safe[..60].Trim();
        return $"{number:00} {program.Name} {safe}{(window == 0 ? "" : " window " + (window + 1))}.jpg";
    }

    /// <summary>Pictures every window the scratch process shows. Gives the files written (none off Windows, or past <see cref="Most"/>).</summary>
    public static IReadOnlyList<string> Take(string workFolder, CsiProgram program, int processId, string step)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return [];
        var number = Interlocked.Increment(ref _taken);
        if (number > Most) return [];
        var written = new List<string>();
        try
        {
            var work = Task.Run(() => Windows(workFolder, program, processId, step, number, written));
            work.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* no picture */ }
        lock (written) return written.ToList();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Windows(string workFolder, CsiProgram program, int processId, string step, int number, List<string> written)
    {
        var folder = Path.Combine(workFolder, FolderName);
        var index = 0;
        foreach (var (window, _) in ScratchDialogs.Of(processId).Take(4))
        {
            try
            {
                if (IsIconic(window)) continue;
                var picture = CsiProMax.Tools.ScreenGrab.Window(window, 1400);
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, NameFor(number, program, step, index++));
                File.WriteAllBytes(file, picture);
                lock (written) written.Add(file);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next window */ }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
