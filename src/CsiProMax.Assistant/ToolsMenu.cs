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
        /// <summary>It named a file that is no longer there, and names this install now.</summary>
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
    }

    /// <param name="Label">"ETABS 23".</param>
    public sealed record Entry(string Label, CsiProgram Program, int Number, string Ini, State State, string Path)
    {
        /// <summary>True when Tools > CSI ProMax is there the next time the program opens.</summary>
        public bool Listed => State is State.Added or State.Moved or State.Already;

        /// <summary>One line for the engineer.</summary>
        public string Says => State switch
        {
            State.Added => $"{Label}: CSI ProMax was missing from the Tools menu and is added now. Open {Label} again to see it.",
            State.Moved => $"{Label}: the Tools menu named a CSI ProMax file that is gone, and names this install now.",
            State.Already => $"{Label}: CSI ProMax is in the Tools menu.",
            State.Missing => $"{Label}: CSI ProMax is not in the Tools menu, and can be added.",
            State.Open => $"{Label}: CSI ProMax is not in the Tools menu, and {Label} is open, so its plug-in list is left alone. Close {Label}, then open CSI ProMax again (from another program, or the Modeler): it adds itself.",
            State.Others => $"{Label}: CSI ProMax is not in the Tools menu, and {Label} has other plug-ins, so its list is left alone. Add it in {Label}: Tools > Add/Show Plugins, plug-in name {Name(Program)}, menu text {MenuText}, file {Path}.",
            _ => $"{Label}: its settings file was not understood, so it is left alone. Add CSI ProMax in {Label}: Tools > Add/Show Plugins, plug-in name {Name(Program)}, menu text {MenuText}, file {Path}.",
        };
    }

    private static string Name(CsiProgram program) => PlugIns.First(p => p.Program.Key == program.Key).Name;

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
        foreach (var folder in new[] { InstalledIn, AppContext.BaseDirectory })
            if (!string.IsNullOrWhiteSpace(folder) && !IsDownloadedVersion(folder!) && PlugIns.All(p => File.Exists(System.IO.Path.Combine(folder!, p.Dll))))
                return folder!.TrimEnd('\\', '/');
        return null;
    }

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
    public static IReadOnlyList<Entry> Repair(string? installFolder = null, string? settingsRoot = null, Func<CsiProgram, int, bool>? isOpen = null, bool change = true)
    {
        var found = new List<Entry>();
        installFolder ??= InstallFolder();
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
                try { state = Register(ini, plugIn.Name, installFolder is null ? null : path, () => isOpen(plugIn.Program, number), change); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException) { state = State.Unknown; }
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
    internal static State Register(string ini, string name, string? path, Func<bool> open, bool change)
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
        var (state, edited) = Edit(text, name, path ?? name + ".dll", File.Exists);
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
    internal static (State State, string? Text) Edit(string text, string name, string path, Func<string, bool>? exists = null)
    {
        const RegexOptions Any = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Regex.Split(text, "\r?\n").ToList();
        var start = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*\[PlugIn\]\s*$", Any));
        if (start < 0) return (State.Unknown, null);
        var end = lines.FindIndex(start + 1, l => Regex.IsMatch(l, @"^\s*\["));
        if (end < 0) end = lines.Count;

        int countAt = -1, count = -1, oursAt = -1;
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
                if (listed.Success && FileName(listed.Groups[1].Value.Trim('"')).Equals(FileName(path), StringComparison.OrdinalIgnoreCase)) { oursAt = i; oursPath = listed.Groups[1].Value.Trim('"'); }
            }
        }
        if (countAt < 0) return (State.Unknown, null);

        State state;
        if (oursAt >= 0)
        {
            // Listed. It is left as it is, also from another folder, unless the file it names is gone.
            if (oursPath.Equals(path, StringComparison.OrdinalIgnoreCase) || exists is null || exists(oursPath)) return (State.Already, null);
            if (count == 1 && entries.Count == 3) state = State.Moved; else return (State.Others, null);
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

    /// <summary>What a look at the lists found, in lines for a message: the ones that need the engineer first.</summary>
    public static string Summary(IReadOnlyList<Entry> entries) =>
        entries.Count == 0
            ? "No ETABS, SAFE or SAP2000 has kept its settings for this Windows user yet. Open the program once and close it; CSI ProMax adds itself to its Tools menu at its next start." // ETABS itself
            : string.Join("\n", entries.OrderBy(e => e.Listed).ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase).Select(e => e.Says));
}
