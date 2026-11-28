using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CsiProMax.Assistant;

/// <summary>
/// The processes that hold files open, asked of Windows' Restart Manager (the way Explorer finds "the file is open in").
/// An Engine run's copy was held by the analysis process of an earlier, cancelled run: ETABS runs its solver as a process
/// of its own, and ending ETABS left the solver running and its .Y07 open (Ahmed's real model, 2026-11-28: every later
/// run slow, then "FILE OPERATION ERROR # 232 ... DELETE ... (CSI ProMax run).Y07").
/// </summary>
public static class FileHolders
{
    /// <summary>The ids of the processes that hold any of <paramref name="files"/> open. Empty off Windows or when Windows cannot say.</summary>
    public static List<int> Of(IReadOnlyList<string> files)
    {
        var ids = new List<int>();
        if (!OperatingSystem.IsWindows() || files.Count == 0) return ids;
        var key = new System.Text.StringBuilder(64);
        if (RmStartSession(out var session, 0, key) != 0) return ids;
        try
        {
            if (RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null) != 0) return ids;
            uint needed = 0, count = 0, reasons = 0;
            var answer = RmGetList(session, out needed, ref count, null, ref reasons);
            if (answer == 234 /* ERROR_MORE_DATA */ && needed > 0)
            {
                var found = new RM_PROCESS_INFO[needed];
                count = needed;
                answer = RmGetList(session, out needed, ref count, found, ref reasons);
                if (answer == 0) ids.AddRange(found.Take((int)count).Select(p => p.Process.dwProcessId));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { RmEndSession(session); }
        return ids.Distinct().Where(id => id != Environment.ProcessId).ToList();
    }

    /// <summary>
    /// Ends the processes that hold a run copy's files open (files named with <see cref="ScratchProgram.RunMark"/> only,
    /// so never a process holding the engineer's own files) and says what it ended.
    /// </summary>
    public static List<string> EndHoldersOfCopy(IReadOnlyList<string> copyFiles)
    {
        var said = new List<string>();
        var files = copyFiles.Where(f => Path.GetFileNameWithoutExtension(f).EndsWith(ScratchProgram.RunMark, StringComparison.Ordinal)).ToList();
        foreach (var id in Of(files))
            try
            {
                using var process = Process.GetProcessById(id);
                var name = process.ProcessName;
                process.Kill(entireProcessTree: true);
                said.Add(process.WaitForExit(15000) ? $"{name} (process id {id}), left running by an earlier run and holding its files, was ended" : $"{name} (process id {id}) holds the run's files and was told to end");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return said;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, System.Text.StringBuilder key);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, RM_UNIQUE_PROCESS[]? apps, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, ref uint reasons);
}
