using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CsiProMax.Chat;

/// <summary>
/// Where Claude Code is looked for on a PC, and what a search found: the file that answered, or every file that was
/// there and why it did not count. The places and the verdicts are kept apart from ClaudeCli (which reads the PC and
/// starts the file), so they are tested without Windows.
/// </summary>
internal static class ClaudeFinder
{
    /// <summary>How long a file gets to answer --version, and how long the one second try gets on a slow PC.</summary>
    internal const int FirstWaitMs = 20_000, SecondWaitMs = 60_000;

    private static readonly Regex VersionText = new(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant);

    /// <summary>The folders a search starts from. Empty ones are left out.</summary>
    /// <param name="Homes">The user's profile folder (and another one when Windows names two).</param>
    /// <param name="Local">%LOCALAPPDATA%.</param>
    /// <param name="Roaming">%APPDATA%.</param>
    /// <param name="ProgramFiles">%ProgramFiles% (and others like it).</param>
    /// <param name="PathFolders">The folders on PATH, as the user has it now.</param>
    /// <param name="Chosen">The file the user pointed at (Settings.AgentSettings.ClaudePath), or "".</param>
    /// <param name="Prefixes">Folders a package manager was told to put programs in (npm's prefix, pnpm, Volta, Bun, Scoop).</param>
    internal sealed record Places(IReadOnlyList<string> Homes, string Local, string Roaming, IReadOnlyList<string> ProgramFiles,
        IReadOnlyList<string> PathFolders, string Chosen = "", IReadOnlyList<string>? Prefixes = null, string User = "");

    /// <summary>Why a file was not used: it is Claude Code but did not work, it is the desktop app, or the chosen file is gone.</summary>
    internal enum LookKind { DidNotWork, DesktopApp, Gone }

    /// <summary>A file that was there (or was chosen), and why it was not used.</summary>
    internal sealed record Look(string Path, string Outcome, LookKind Kind = LookKind.DidNotWork);

    /// <summary>What a search found.</summary>
    /// <param name="Install">The Claude Code that answered, or null.</param>
    /// <param name="Tried">Files that were there but did not count, each with the reason.</param>
    /// <param name="Looked">Every place that was looked at, in order.</param>
    internal sealed record Result(ClaudeCli.Install? Install, IReadOnlyList<Look> Tried, IReadOnlyList<string> Looked, string User, string Home)
    {
        /// <summary>Files that look like Claude Code but did not answer as it does: the card names them instead of "not installed".</summary>
        public IReadOnlyList<Look> Failed => Tried.Where(t => t.Kind == LookKind.DidNotWork).ToList();

        /// <summary>True when the Claude desktop app is on this PC (a different program, which people take for Claude Code).</summary>
        public bool DesktopAppSeen => Tried.Any(t => t.Kind == LookKind.DesktopApp);

        /// <summary>The file the user chose earlier and that is gone now, or null.</summary>
        public string? ChosenGone => Tried.FirstOrDefault(t => t.Kind == LookKind.Gone)?.Path;

        /// <summary>One sentence on where the search looked, for the pane's card.</summary>
        public string Where =>
            $"CSI ProMax looked in {Looked.Count} places for the Windows user {(User.Length > 0 ? User : "of this session")}: " +
            $"{System.IO.Path.Combine(Home, ".local", "bin")}, the folders on PATH, WinGet, npm, and the copy inside the Claude desktop app.";

        /// <summary>Everything, to copy and send to whoever looks after CSI ProMax.</summary>
        public string Details(string product)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{product}: looking for Claude Code");
            sb.AppendLine($"Windows user: {User}");
            sb.AppendLine($"Profile folder: {Home}");
            sb.AppendLine(Install is null ? "Result: no Claude Code that answers was found." : $"Result: {Install.Path} (version {Install.Version})");
            if (Tried.Count > 0)
            {
                sb.AppendLine().AppendLine("Files that were there but were not used:");
                foreach (var look in Tried) sb.AppendLine($"- {look.Path}: {look.Outcome}");
            }
            sb.AppendLine().AppendLine($"Places looked at ({Looked.Count}), in order:");
            foreach (var place in Looked) sb.AppendLine("- " + place);
            return sb.ToString();
        }
    }

    /// <summary>Every place Claude Code is installed to on Windows, the most likely first; the wider guesses come last.</summary>
    internal static IEnumerable<string> Candidates(Places p)
    {
        if (p.Chosen.Trim().Length > 0) yield return p.Chosen.Trim().Trim('"');

        foreach (var home in p.Homes) yield return Join(home, ".local", "bin", "claude.exe");     // Anthropic's installer
        yield return Join(p.Local, "Programs", "claude", "claude.exe");
        yield return Join(p.Local, "Microsoft", "WinGet", "Links", "claude.exe");               // WinGet, for the user
        foreach (var dir in p.PathFolders)
        {
            yield return Join(dir, "claude.exe");
            yield return Join(dir, "claude.cmd");
        }
        yield return Join(p.Roaming, "npm", "claude.cmd");                                      // npm install -g

        // ---- The wider search: installs that never reached PATH, or that PATH lost. ----
        foreach (var pf in p.ProgramFiles) yield return Join(pf, "WinGet", "Links", "claude.exe"); // WinGet, for the PC
        foreach (var packages in new[] { Join(p.Local, "Microsoft", "WinGet", "Packages") }.Concat(p.ProgramFiles.Select(pf => Join(pf, "WinGet", "Packages"))))
            foreach (var package in Folders(packages, "Anthropic.ClaudeCode*"))
            {
                yield return Join(package, "claude.exe");
                foreach (var inner in Folders(package, "*")) yield return Join(inner, "claude.exe");
            }
        foreach (var prefix in p.Prefixes ?? [])
        {
            yield return Join(prefix, "claude.cmd");
            yield return Join(prefix, "claude.exe");
        }
        foreach (var pf in p.ProgramFiles) yield return Join(pf, "nodejs", "claude.cmd");
        foreach (var node in NewestFirst(Folders(Join(p.Roaming, "nvm"), "v*"))) yield return Join(node, "claude.cmd");
        yield return Join(p.Local, "pnpm", "claude.cmd");
        yield return Join(p.Local, "Volta", "bin", "claude.exe");
        yield return Join(p.Local, "Volta", "bin", "claude.cmd");
        yield return Join(p.Local, "Yarn", "bin", "claude.cmd");
        foreach (var home in p.Homes)
        {
            yield return Join(home, ".bun", "bin", "claude.exe");
            yield return Join(home, "scoop", "shims", "claude.exe");
            yield return Join(home, "scoop", "shims", "claude.cmd");
            yield return Join(home, ".claude", "local", "claude.cmd");                          // the old "local" install
            yield return Join(home, ".claude", "local", "node_modules", ".bin", "claude.cmd");
        }
        // Anthropic's installer keeps each version here; claude.exe in .local\bin is a copy of the newest. An update
        // that was interrupted leaves .local\bin without claude.exe, with the last one beside it as claude.exe.old.<time>.
        foreach (var home in p.Homes)
        {
            var versions = Join(home, ".local", "share", "claude", "versions");
            foreach (var version in NewestFirst(Files(versions, "*")).Take(2)) yield return version;
            foreach (var version in NewestFirst(Folders(versions, "*")).Take(2)) yield return Join(version, "claude.exe");
            foreach (var backup in Files(Join(home, ".local", "bin"), "claude.exe.old.*").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).Take(1)) yield return backup;
        }
        // The Claude desktop app keeps a Claude Code of its own for its Code tab: the same program, used last.
        foreach (var data in DesktopData(p))
            foreach (var version in NewestFirst(Folders(Join(data, "claude-code"), "*")))
            {
                yield return Join(version, "claude.exe");
                foreach (var inner in Folders(version, "*")) yield return Join(inner, "claude.exe");
            }
        // The desktop app itself: never started (IsDesktopApp), only named, because people take it for Claude Code.
        yield return Join(p.Local, "AnthropicClaude", "claude.exe");
        yield return Join(p.Local, "Microsoft", "WindowsApps", "claude.exe");
    }

    /// <summary>The folders the Claude desktop app keeps its data in (its usual one, and the ones a Store or MSIX install uses).</summary>
    private static IEnumerable<string> DesktopData(Places p)
    {
        yield return Join(p.Roaming, "Claude");
        yield return Join(p.Local, "Claude");
        yield return Join(p.Local, "Claude-3p");
        yield return Join(p.Roaming, "Claude-3p");
        foreach (var package in Folders(Join(p.Local, "Packages"), "Claude_*"))
        {
            yield return Join(package, "LocalCache", "Roaming", "Claude");
            yield return Join(package, "LocalCache", "Local", "Claude");
            yield return Join(package, "LocalCache", "Local", "Claude-3p");
            yield return Join(package, "LocalCache", "Roaming", "Claude-3p");
        }
    }

    /// <summary>
    /// Looks at every place in turn and returns the first file that answers --version as Claude Code does.
    /// <paramref name="ask"/> starts a file with --version and a time limit, and gives its exit code and output
    /// (ClaudeCli.Run: -1 with no output when it did not answer in time, -1 with the reason when it did not start).
    /// </summary>
    internal static Result Search(Places p, Func<string, int, (int Code, string Output)> ask)
    {
        var tried = new List<Look>();
        var looked = new List<string>();
        var secondTryUsed = false;
        var home = p.Homes.FirstOrDefault() ?? "";
        var chosen = p.Chosen.Trim().Trim('"');
        foreach (var path in Candidates(p).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            looked.Add(path);
            if (!Exists(path))
            {
                if (chosen.Length > 0 && string.Equals(path, chosen, StringComparison.OrdinalIgnoreCase))
                    tried.Add(new Look(path, "the file chosen in CSI ProMax is no longer there", LookKind.Gone));
                continue;
            }
            // The Claude desktop app is also "claude.exe"; it is never started by mistake.
            if (IsDesktopApp(path))
            {
                tried.Add(new Look(path, "is the Claude desktop app, a different program from Claude Code", LookKind.DesktopApp));
                continue;
            }
            var waited = FirstWaitMs;
            var (code, output) = ask(path, waited);
            // A slow PC (a virus scan of a large new file) gets one longer try, once per search.
            if (TimedOut(code, output) && !secondTryUsed)
            {
                secondTryUsed = true;
                waited = SecondWaitMs;
                (code, output) = ask(path, waited);
            }
            var (works, version, why) = Judge(code, output, waited);
            if (works) return new Result(new ClaudeCli.Install(path, version), tried, looked, p.User, home);
            tried.Add(new Look(path, why));
        }
        return new Result(null, tried, looked, p.User, home);
    }

    private static bool TimedOut(int code, string output) => code == -1 && output.Trim().Length == 0;

    /// <summary>Whether an answer to --version is Claude Code's ("2.1.5 (Claude Code)"), and when not, why in plain words.</summary>
    internal static (bool Works, string Version, string Why) Judge(int code, string output, int waitedMs)
    {
        var match = VersionText.Match(output);
        // Exit code 0 with a version, as before; or the words Claude Code with a version, whatever the exit code.
        if (match.Success && (code == 0 || output.IndexOf("Claude Code", StringComparison.OrdinalIgnoreCase) >= 0)) return (true, match.Value, "");
        if (TimedOut(code, output)) return (false, "", $"did not answer within {waitedMs / 1000} seconds");
        var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (first.Length > 160) first = first.Substring(0, 160) + "…";
        if (code == -1) return (false, "", "Windows did not start it: " + first);
        return (false, "", $"answered with exit code {code}" + (first.Length > 0 ? ": " + first : ", and no version"));
    }

    /// <summary>
    /// True for the Claude desktop app's own program, which must never be started as Claude Code: its usual folders
    /// (AnthropicClaude, WindowsApps), or any folder that is an app of its kind (it has resources\app.asar).
    /// </summary>
    internal static bool IsDesktopApp(string path)
    {
        if (path.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        try
        {
            var folder = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "resources", "app.asar"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>The prefix line of an .npmrc file (where npm install -g puts programs), or "" when it has none.</summary>
    internal static string NpmPrefix(string npmrc)
    {
        foreach (var raw in npmrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
            var at = line.IndexOf('=');
            if (at <= 0 || !line.Substring(0, at).Trim().Equals("prefix", StringComparison.OrdinalIgnoreCase)) continue;
            return line.Substring(at + 1).Trim().Trim('"', '\'');
        }
        return "";
    }

    private static string Join(params string[] parts)
    {
        if (parts.Any(string.IsNullOrWhiteSpace)) return "";
        try { return Path.Combine(parts); }
        catch (ArgumentException) { return ""; }
    }

    private static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private static string[] Folders(string parent, string pattern) => List(parent, pattern, folders: true);

    private static string[] Files(string parent, string pattern) => List(parent, pattern, folders: false);

    private static string[] List(string parent, string pattern, bool folders)
    {
        if (parent.Length == 0) return [];
        try
        {
            if (!Directory.Exists(parent)) return [];
            return folders ? Directory.GetDirectories(parent, pattern) : Directory.GetFiles(parent, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }

    /// <summary>Folders or files named by a version (2.1.119, v22.4.0), the newest first.</summary>
    private static IEnumerable<string> NewestFirst(IEnumerable<string> paths) =>
        paths.OrderByDescending(p => VersionOf(Path.GetFileName(p))).ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase);

    private static Version VersionOf(string name) =>
        Regex.Match(name, @"\d+(\.\d+){1,3}") is { Success: true } m && Version.TryParse(m.Value, out var v) ? v : new Version(0, 0);
}
