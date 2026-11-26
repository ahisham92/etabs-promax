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

    internal static void Reset() => Volatile.Write(ref _taken, 0);

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
