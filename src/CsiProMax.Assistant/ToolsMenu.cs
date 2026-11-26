using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// "CSI ProMax" in the Tools menu of each ETABS, SAFE and SAP2000 of this Windows user. The one-line install adds it
/// once; this keeps it there. A program that had never been opened when CSI ProMax was installed (it has no settings
/// file yet), or that was open then, is left without the entry, and nobody runs the install line a second time: a
/// tester's ETABS 23 had no entry while four other programs on the same PC had one. So every start of CSI ProMax
/// looks at the lists again and adds itself where it is missing, under the install line's own rule: only when that
/// program is closed, only when it has no other plug-in, and a copy of its settings file is kept beside it first.
/// It never starts, closes or licenses a program, and never touches a model.
/// </summary>
public static class ToolsMenu
{
    public const string MenuText = "CSI ProMax";

    /// <summary>The plug-in each program loads, and the oldest version of the program it is offered to.</summary>
    public sealed record PlugIn(CsiProgram Program, string Name, string Dll, int Oldest);

    public static readonly IReadOnlyList<PlugIn> PlugIns =
    [
        new(CsiProgram.Etabs, "CsiProMaxPlugin", "CsiProMaxPlugin.dll", 18),
        new(CsiProgram.Safe, "CsiProMaxSafePlugin", "CsiProMaxSafePlugin.dll", 20),
        new(CsiProgram.Sap2000, "CsiProMaxSapPlugin", "CsiProMaxSapPlugin.dll", 20),
    ];

    public enum State
    {
        /// <summary>It was missing and is added now.</summary>
        Added,
        /// <summary>It named a file that is no longer there, or was listed more than once, and names this install now, once.</summary>
        Moved,
        /// <summary>It is listed.</summary>
        Already,
        /// <summary>It is missing and could be added (asked for without changing anything).</summary>
        Missing,
        /// <summary>It is missing, and the program is open: its list is left alone until it is closed.</summary>
        Open,
        /// <summary>It is missing, and the program has other plug-ins: how several are written is not known, so the list is left alone.</summary>
        Others,
        /// <summary>The settings file was not understood, or could not be written.</summary>
        Unknown,
        /// <summary>
        /// The settings file does not list it, but this very window was started from that program's Tools menu, so the
        /// program has it: the program writes its list when it closes (SAFE 20, 2026-11-26: NumberPlugIns=0 while
        /// SAFE 20's Tools > CSI ProMax had opened the window that ran the test).
        /// </summary>
        Running,
    }

    /// <param name="Label">"ETABS 23".</param>
    public sealed record Entry(string Label, CsiProgram Program, int Number, string Ini, State State, string Path)
    {
        /// <summary>True when Tools > CSI ProMax is there the next time the program opens.</summary>
        public bool Listed => State is State.Added or State.Moved or State.Already or State.Running;

        /// <summary>One line for the engineer.</summary>
        public string Says => State switch
        {
            State.Added => $"{Label}: CSI ProMax was missing from the Tools menu and is added now. Open {Label} again to see it.",
            State.Moved => $"{Label}: the Tools menu named a CSI ProMax file that is gone or a copy outside the installed folder, or named CSI ProMax twice, and names this install now, once.",
            State.Already => $"{Label}: CSI ProMax is in the Tools menu.",
            State.Missing => $"{Label}: CSI ProMax is not in the Tools menu, and can be added.",
            State.Running => $"{Label}: CSI ProMax is in the Tools menu: this window was opened from it. {Label} writes its plug-in list to its settings file when it closes, so the file does not list it yet; if it is still missing after {Label} closes, CSI ProMax adds it then.",
            State.Open => $"{Label}: CSI ProMax is not in the Tools menu, and {Label} is open, so its plug-in list is left alone. Close {Label}, then open CSI ProMax again (from another program, or the Modeler): it adds itself.",
            State.Others => $"{Label}: CSI ProMax is not in the Tools menu, and {Label} has other plug-ins, so its list is left alone. Add it in {Label}: Tools > Add/Show Plugins, plug-in name {Name(Program)}, menu text {MenuText}, file {Path}.",
            _ => $"{Label}: its settings file was not understood, so it is left alone. Add CSI ProMax in {Label}: Tools > Add/Show Plugins, plug-in name {Name(Program)}, menu text {MenuText}, file {Path}.",
        };
    }

    private static string Name(CsiProgram program) => PlugIns.First(p => p.Program.Key == program.Key).Name;

    /// <summary>
    /// The program version this window was started from by its Tools menu ("SAFE 20"), told by Program from the
    /// plug-in's process; null when the window was not started from a program.
    /// </summary>
    public static string? OpenedFrom { get; set; }

    /// <summary>"SAFE 20" for a process started from C:\Program Files\Computers and Structures\SAFE 20\SAFE.exe; null when it can't be told.</summary>
    public static string? LabelOf(int? pid)
    {
        if (pid is not { } id) return null;
        try
        {
            using var process = Process.GetProcessById(id);
            return LabelOfFolder(System.IO.Path.GetDirectoryName(process.MainModule?.FileName ?? ""));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    /// <summary>"SAFE 20" for a program folder named "SAFE 20" (any case); null for any other folder.</summary>
    public static string? LabelOfFolder(string? folder)
    {
        var match = Regex.Match((folder ?? "").Replace('\\', '/').TrimEnd('/').Split('/')[^1], @"^(ETABS|SAFE|SAP2000) (\d+)$", RegexOptions.IgnoreCase); // ETABS itself
        return match.Success ? $"{CsiProgram.All.First(p => p.Name.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).Name} {match.Groups[2].Value}" : null;
    }

    /// <summary>Where the programs keep their settings for this Windows user.</summary>
    public static string SettingsRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Computers and Structures");

    /// <summary>The folder the plug-in files are started from, told by Program when a downloaded version runs instead of the installed one.</summary>
    public static string? InstalledIn { get; set; }

    /// <summary>
    /// The folder whose plug-in files the Tools menus should name: the installed folder (Documents\CSI ProMax), not the
    /// folder of a downloaded version that runs in its place. Null when it cannot be told.
    /// </summary>
    public static string? InstallFolder()
    {
        foreach (var folder in new[] { DefaultInstall(), InstalledIn, AppContext.BaseDirectory })
            if (!string.IsNullOrWhiteSpace(folder) && !IsDownloadedVersion(folder!) && PlugIns.All(p => File.Exists(System.IO.Path.Combine(folder!, p.Dll))))
                return folder!.TrimEnd('\\', '/');
        return null;
    }

    /// <summary>
    /// Documents\CSI ProMax, where the install line puts CSI ProMax, when the plug-in files are there. It comes before
    /// the folder CSI ProMax happens to run from: a copy unpacked elsewhere to try a version (as the field test runs
    /// are) once wrote its own folder into the Tools menus, so Tools > CSI ProMax started that copy (2026-11-25).
    /// </summary>
    public static string? DefaultInstall()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) return null;
            var folder = System.IO.Path.Combine(documents, "CSI ProMax");
            return PlugIns.All(p => File.Exists(System.IO.Path.Combine(folder, p.Dll))) ? folder : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether this window runs a version the updater downloaded, not the installed files or a copy unpacked to try.</summary>
    public static bool RunsDownloadedVersion => IsDownloadedVersion(AppContext.BaseDirectory);

    private static bool IsDownloadedVersion(string folder)
    {
        try { return System.IO.Path.GetFullPath(folder).StartsWith(System.IO.Path.GetFullPath(Update.Updater.VersionsFolder), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    /// <summary>
    /// Looks at every program's plug-in list and adds CSI ProMax where it is missing and may be added. Run at every
    /// start (in the background), from Help, and by the field test.
    /// </summary>
    /// <param name="installFolder">The folder of the plug-in files. Left out: <see cref="InstallFolder"/>.</param>
    /// <param name="settingsRoot">For tests: a folder that stands in for <see cref="SettingsRoot"/>.</param>
    /// <param name="isOpen">For tests: whether a version of a program is open. Left out: asked of Windows.</param>
    /// <param name="change">False: say what would be done, and change nothing.</param>
    /// <param name="prefer">An entry that names another folder's copy is made to name this folder. Left out: only when this folder is Documents\CSI ProMax.</param>
    public static IReadOnlyList<Entry> Repair(string? installFolder = null, string? settingsRoot = null, Func<CsiProgram, int, bool>? isOpen = null, bool change = true, bool? prefer = null)
    {
        var found = new List<Entry>();
        installFolder ??= InstallFolder();
        var mine = prefer ?? (installFolder is not null && DefaultInstall() is { } usual && string.Equals(usual.TrimEnd('\\', '/'), installFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        var root = settingsRoot ?? SettingsRoot;
        isOpen ??= IsOpen;
        string[] folders;
        try { folders = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }
        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var match = Regex.Match(System.IO.Path.GetFileName(folder), @"^(ETABS|SAFE|SAP2000) (\d+)$", RegexOptions.IgnoreCase); // ETABS itself
            if (!match.Success) continue;
            var plugIn = PlugIns.First(p => p.Program.Name.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            var number = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (number < plugIn.Oldest || number > 99) continue;
            string[] files;
            try { files = Directory.GetFiles(folder, plugIn.Program.Name + "*.ini"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var ini in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var label = $"{plugIn.Program.Name} {number}";
                var path = installFolder is null ? plugIn.Dll : System.IO.Path.Combine(installFolder, plugIn.Dll);
                State state;
                try { state = Register(ini, plugIn.Name, installFolder is null ? null : path, () => isOpen(plugIn.Program, number), change, mine); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException) { state = State.Unknown; }
                // Missing from the file of the very program this window was started from: the program has it.
                if (state is State.Open or State.Missing or State.Others or State.Unknown && string.Equals(label, OpenedFrom, StringComparison.OrdinalIgnoreCase)) state = State.Running;
                found.Add(new Entry(label, plugIn.Program, number, ini, state, path));
            }
        }
        return found;
    }

    /// <summary>
    /// One settings file: adds the plug-in when it is missing and may be added, keeping the file's encoding and line
    /// ends, and a copy of the file as it was beside it (once). <paramref name="path"/> null: the install folder is
    /// not known, so nothing can be added, and the answer only says whether it is listed.
    /// </summary>
    internal static State Register(string ini, string name, string? path, Func<bool> open, bool change, bool prefer = false)
    {
        var bytes = File.ReadAllBytes(ini);
        Encoding encoding;
        var skip = 0;
        var plain = false;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = new UTF8Encoding(false); skip = 3; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, false); skip = 2; }
        else { encoding = Encoding.Latin1; plain = true; }   // no mark: every byte is kept as it is
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        // The plug-in's file name is enough to see whether it is listed; the folder matters only for adding it.
        var (state, edited) = Edit(text, name, path ?? name + ".dll", File.Exists, prefer && path is not null);
        if (edited is null) return state;
        if (path is null) return State.Missing;
        // A folder name with letters outside plain ASCII cannot be written safely into a file with no encoding mark.
        if (plain && path.Any(c => c < 0x20 || c > 0x7E)) return State.Unknown;
        if (!change) return State.Missing;
        if (open()) return State.Open;
        var backup = ini + ".before-csi-promax";
        if (!File.Exists(backup)) File.Copy(ini, backup);
        var body = encoding.GetBytes(edited);
        var all = new byte[skip + body.Length];
        Array.Copy(bytes, 0, all, 0, skip);
        Array.Copy(body, 0, all, skip, body.Length);
        File.WriteAllBytes(ini, all);
        return state;
    }

    /// <summary>
    /// The [PlugIn] part of a settings file with CSI ProMax in it: text in, text out, the same rule as install.ps1.
    /// A program with no plug-in has "[PlugIn]" and " NumberPlugIns=0"; with one it has NumberPlugIns=1 and the lines
    /// PlugInName, PlugInMenuText and PlugInPath. How several plug-ins are written is not known, so a list that holds
    /// another plug-in is never changed. The text is null when nothing is to be written.
    /// </summary>
    /// <param name="exists">Whether a listed plug-in file is there: an entry of ours that names a file that is gone is put right.</param>
    /// <param name="prefer">An entry of ours that names a copy in another folder is made to name <paramref name="path"/>, though that copy is there.</param>
    internal static (State State, string? Text) Edit(string text, string name, string path, Func<string, bool>? exists = null, bool prefer = false)
    {
        const RegexOptions Any = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Regex.Split(text, "\r?\n").ToList();
        var start = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*\[PlugIn\]\s*$", Any));
        if (start < 0) return (State.Unknown, null);
        var end = lines.FindIndex(start + 1, l => Regex.IsMatch(l, @"^\s*\["));
        if (end < 0) end = lines.Count;

        int countAt = -1, count = -1, oursAt = -1, paths = 0, ours = 0;
        string indent = " ", oursPath = "";
        var entries = new List<int>();
        for (var i = start + 1; i < end; i++)
        {
            var number = Regex.Match(lines[i], @"^(\s*)NumberPlugIns\s*=\s*(\d+)\s*$", Any);
            if (number.Success)
            {
                if (countAt >= 0) return (State.Unknown, null);
                countAt = i; indent = number.Groups[1].Value; count = int.Parse(number.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (Regex.IsMatch(lines[i], @"^\s*PlugIn\w*\s*=", Any))
            {
                entries.Add(i);
                var listed = Regex.Match(lines[i], @"^\s*PlugInPath\w*\s*=\s*(.*?)\s*$", Any);
                if (listed.Success) paths++;
                if (listed.Success && FileName(listed.Groups[1].Value.Trim('"')).Equals(FileName(path), StringComparison.OrdinalIgnoreCase)) { oursAt = i; oursPath = listed.Groups[1].Value.Trim('"'); ours++; }
            }
        }
        if (countAt < 0) return (State.Unknown, null);

        State state;
        if (oursAt >= 0)
        {
            // Listed once: left as it is, also from another folder, unless the file it names is gone. Listed more than
            // once (added by hand a second time, so the menu has two entries and one may be an old copy): made one.
            if (ours == 1 && (oursPath.Equals(path, StringComparison.OrdinalIgnoreCase) || !prefer && (exists is null || exists(oursPath)))) return (State.Already, null);
            if (paths == ours) state = State.Moved; else return (State.Others, null);
        }
        else if (count == 0 && entries.Count == 0) state = State.Added;
        else return (State.Others, null);

        var kept = Enumerable.Range(start + 1, end - start - 1).Where(i => i != countAt && !entries.Contains(i)).Select(i => lines[i]);
        var all = lines.Take(start)
            .Concat([lines[start], indent + "NumberPlugIns=1", indent + "PlugInName=" + name, indent + "PlugInMenuText=" + MenuText, indent + "PlugInPath=" + path])
            .Concat(kept).Concat(lines.Skip(end));
        return (state, string.Join(newline, all));
    }

    private static string FileName(string path) => path.Replace('\\', '/').Split('/').Last();

    /// <summary>
    /// True when this version of the program is open, or may be: a program rewrites its settings file when it closes,
    /// so the file of an open program is never changed. The version is told from the folder the program runs from
    /// (...\ETABS 23\ETABS.exe); a window whose folder cannot be read counts as open.
    /// </summary>
    private static bool IsOpen(CsiProgram program, int number)
    {
        Process[] running;
        try { running = Process.GetProcessesByName(program.ProcessName); }
        catch (InvalidOperationException) { return true; }
        try
        {
            foreach (var process in running)
            {
                // A process left behind with no window (the program hung on closing, hours ago) never writes its
                // settings again, and its user cannot find it to close it: it does not count as the program being open.
                if (LeftBehind(Window(process), Started(process), DateTime.Now)) continue;
                string file;
                try { file = process.MainModule?.FileName ?? ""; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
                var version = Regex.Match(file, $@"[\\/]{Regex.Escape(program.Name)} ?(\d+)[\\/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!version.Success || int.Parse(version.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == number) return true;
            }
            return false;
        }
        finally { foreach (var process in running) process.Dispose(); }
    }

    /// <summary>
    /// True for a process of the program that is not the program being open: it has no window and started more than
    /// ten minutes ago (a program that is starting has no window for some seconds, and counts as open). A process whose
    /// window or start time cannot be read counts as open.
    /// </summary>
    internal static bool LeftBehind(bool? hasWindow, DateTime? started, DateTime now) =>
        hasWindow == false && started is { } at && now - at > TimeSpan.FromMinutes(10);

    /// <summary>True when the process shows a window of its own: a main window with a title.</summary>
    internal static bool HasWindow(bool handle, string? title) => handle && !string.IsNullOrWhiteSpace(title);

    private static bool? Window(Process process)
    {
        // A window a person can see has a title. SAP2000 26 left behind keeps a hidden helper window with none
        // ("Internet Explorer_Hidden", size 0), which Windows still names as its main window (seen 2026-11-25).
        try { return HasWindow(process.MainWindowHandle != IntPtr.Zero, process.MainWindowTitle); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static DateTime? Started(Process process)
    {
        try { return process.StartTime; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }

    /// <summary>What a look at the lists found, in lines for a message: the ones that need the engineer first.</summary>
    public static string Summary(IReadOnlyList<Entry> entries) =>
        entries.Count == 0
            ? "No ETABS, SAFE or SAP2000 has kept its settings for this Windows user yet. Open the program once and close it; CSI ProMax adds itself to its Tools menu at its next start." // ETABS itself
            : string.Join("\n", entries.OrderBy(e => e.Listed).ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase).Select(e => e.Says));
}
