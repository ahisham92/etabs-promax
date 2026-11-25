using System.Text;
using System.Text.RegularExpressions;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test: what a person runs on a PC that has a licensed CSI program, so the checks that cannot be made
/// without one are made there. It runs the self test's steps on the open model (reading only, unless changes are asked
/// for on a scratch model), adds what this PC and this install are like, and saves one report file to send back
/// (Help > Field test, the run_field_test tool, or CsiProMax --fieldtest).
/// </summary>
public static class FieldTest
{
    public sealed record Outcome(string File, IReadOnlyList<SelfTest.StepResult> Steps, string Report)
    {
        public int Failed => Steps.Count(s => !s.Ok);

        /// <summary>The result in a few lines, for a dialog: the counts, what did not work, and where the report is.</summary>
        public string Summary()
        {
            var bad = Steps.Where(s => !s.Ok).ToList();
            var text = bad.Count == 0
                ? $"Field test: all {Steps.Count} checks passed."
                : $"Field test: {Steps.Count - bad.Count} of {Steps.Count} checks passed. Not working:\n" +
                  string.Join("\n", bad.Take(12).Select(s => $"- {s.Name}: {Short(s.Detail, 160)}")) + (bad.Count > 12 ? $"\n- and {bad.Count - 12} more, in the report" : "");
            return text + "\n\nThe report is saved here. Please send this file to whoever gave you CSI ProMax:\n" + File;
        }
    }

    /// <summary>Documents\CSI ProMax\Bugs\Field tests: beside the bug reports, and taken by Export chats and bugs.</summary>
    public static string Folder => Path.Combine(global::CsiProMax.Bugs.BugLog.Root, "Field tests");

    /// <param name="startedFrom">Where the person started it: "the Help menu", "the chat", "the command line".</param>
    /// <param name="folder">Where the report goes. Left out: <see cref="Folder"/>.</param>
    /// <param name="settingsRoot">For tests: stands in for %LOCALAPPDATA%\Computers and Structures.</param>
    public static Outcome Run(ICsiLink link, bool changes, string startedFrom, string? folder = null, DateTime? at = null, string? settingsRoot = null)
    {
        var now = at ?? DateTime.Now;
        var steps = SelfTest.Steps(link, changes);
        var report = Render(link, steps, changes, startedFrom, now, PlugInLists(settingsRoot));
        var target = folder ?? Folder;
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, $"Field test {link.Program.Name} {now:yyyy-MM-dd HHmm}.md");
        if (File.Exists(file)) file = Path.Combine(target, $"Field test {link.Program.Name} {now:yyyy-MM-dd HHmmss}.md");
        File.WriteAllText(file, report, new UTF8Encoding(false));
        return new Outcome(file, steps, report);
    }

    internal static string Render(ICsiLink link, IReadOnlyList<SelfTest.StepResult> steps, bool changes, string startedFrom, DateTime now,
        IReadOnlyList<(string File, string Section)> plugIns)
    {
        var bad = steps.Where(s => !s.Ok).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# CSI ProMax field test");
        sb.AppendLine();
        sb.AppendLine("Please send this file to whoever gave you CSI ProMax. It holds the versions on this PC, the plug-in lists of the");
        sb.AppendLine("CSI programs, and small samples of the open model (names, counts and the first rows of result tables). Read it");
        sb.AppendLine("before you send it.");
        sb.AppendLine();
        sb.AppendLine(bad.Count == 0 ? $"- Result: all {steps.Count} checks passed" : $"- Result: {steps.Count - bad.Count} of {steps.Count} checks passed, {bad.Count} did not");
        sb.AppendLine($"- Run: {now:yyyy-MM-dd HH:mm}, started from {startedFrom}");
        sb.AppendLine(changes
            ? "- Changes to the model: a test column, a slab, the load pattern CPM-TEST, loads and the combination CPM-TEST-COMBO were added; the column and the slab were deleted again"
            : "- Changes to the model: none (reading only)");
        sb.AppendLine($"- CSI ProMax: {Update.Updater.CurrentText}, in {AppContext.BaseDirectory}");
        sb.AppendLine("- " + ProgramLine(link));
        // Only the plug-in passes --etabs-pid, so this says whether the Tools menu entry works on this PC.
        sb.AppendLine(Environment.GetCommandLineArgs().Contains("--etabs-pid")
            ? "- Window: opened from the program's Tools menu (the plug-in started it)"
            : "- Window: opened on its own, not from the program's Tools menu");
        sb.AppendLine($"- This PC: {Environment.OSVersion}, .NET {Environment.Version}, started with {Environment.ProcessPath}");
        var beside = new[] { "CsiProMaxPlugin.dll", "CsiProMaxSafePlugin.dll", "CsiProMaxSapPlugin.dll" };
        sb.AppendLine("- Plug-in files beside CsiProMax.dll: " + string.Join(", ", beside.Select(f => $"{f} {(File.Exists(Path.Combine(AppContext.BaseDirectory, f)) ? "yes" : "NO")}")));
        sb.AppendLine();

        if (bad.Count > 0)
        {
            sb.AppendLine("## Not working");
            sb.AppendLine();
            foreach (var s in bad) sb.AppendLine($"- {s.Name}: {Short(s.Detail, 400)}");
            sb.AppendLine();
        }

        sb.AppendLine("## Plug-in lists of the CSI programs");
        sb.AppendLine();
        if (plugIns.Count == 0) sb.AppendLine("None was found for this Windows user.").AppendLine();
        foreach (var (file, section) in plugIns)
        {
            sb.AppendLine($"### {file}");
            sb.AppendLine();
            Fence(sb, section, "ini");
        }

        sb.AppendLine("## Every check");
        sb.AppendLine();
        foreach (var s in steps)
        {
            sb.AppendLine($"### {(s.Ok ? "ok" : "FAIL")}: {s.Name}");
            sb.AppendLine();
            Fence(sb, s.Full ?? s.Detail, s.Ok ? "json" : "");
        }
        return HideModelFolder(sb.ToString(), link);
    }

    /// <summary>
    /// The report without the folder the model is in, wherever a check's answer names it: the report is sent on, and a
    /// folder can name a client or a person. The file's own name stays.
    /// </summary>
    private static string HideModelFolder(string report, ICsiLink link)
    {
        string file;
        try { file = link.Info().File; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return report; }
        var cut = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        if (cut <= 0) return report;
        // An answer inside an answer is escaped again, so the folder's parts may be one, two or four back-slashes apart.
        // A model in the root of a drive has no folder worth hiding, and "C:" alone is in other paths too.
        var parts = file[..cut].Split('\\', '/').Where(p => p.Length > 0).Select(Regex.Escape).ToList();
        return parts.Count < 2 ? report : Regex.Replace(report, string.Join(@"[\\/]+", parts), "(model folder)", RegexOptions.IgnoreCase);
    }

    private static string ProgramLine(ICsiLink link)
    {
        try
        {
            var info = link.Info();
            var file = info.File.Replace('\\', '/').Split('/').Last();
            return $"Program: {link.Program.Name} {info.Version}, units {info.Force}, {info.Length}, model \"{(file.Length > 0 ? file : "not saved yet")}\", analysed: {(info.Locked ? "yes" : "no")}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"Program: {link.Program.Name} (the model could not be read: {ex.Message})"; }
    }

    private static void Fence(StringBuilder sb, string text, string kind)
    {
        // A fence longer than any run of back-ticks in the text, so the text cannot end it.
        var ticks = new string('`', Math.Max(3, Regex.Matches(text, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max() + 1));
        sb.AppendLine(ticks + kind);
        sb.AppendLine(text);
        sb.AppendLine(ticks);
        sb.AppendLine();
    }

    private static string Short(string text, int most) => text.Length > most ? text[..most] + "..." : text;

    /// <summary>
    /// The [PlugIn] part of each CSI program's settings file for this Windows user, as it is written there: it shows
    /// whether CSI ProMax is listed, and how the program itself writes a list (the one-line install writes the same).
    /// </summary>
    internal static IReadOnlyList<(string File, string Section)> PlugInLists(string? root = null)
    {
        var found = new List<(string, string)>();
        try
        {
            root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Computers and Structures");
            if (!Directory.Exists(root)) return found;
            foreach (var folder in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(folder);
                if (!Regex.IsMatch(name, @"^(ETABS|SAFE|SAP2000|CSiBridge) \d+$", RegexOptions.IgnoreCase)) continue;
                foreach (var ini in Directory.GetFiles(folder, "*.ini").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    if (PlugInSection(File.ReadAllText(ini)) is { } section) found.Add(($"{name}\\{Path.GetFileName(ini)}", section));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what was read so far is reported */ }
        return found;
    }

    /// <summary>The lines from "[PlugIn]" to the next section of a settings file, or null when it has none.</summary>
    internal static string? PlugInSection(string ini)
    {
        var lines = ini.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim().Equals("[PlugIn]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Length;
        return string.Join("\n", lines[start..end]).TrimEnd();
    }
}
