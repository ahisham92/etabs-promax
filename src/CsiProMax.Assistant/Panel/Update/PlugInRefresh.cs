using System.IO;
using System.Reflection;

namespace CsiProMax.Update;

/// <summary>
/// Brings the installed plug-in files (Documents\CSI ProMax\CsiProMax*Plugin.dll, the ones the programs' Tools menus
/// name) up to the version that runs, with the programs open. The updater never wrote there, so a tester's SAFE plug-in
/// was still 0.4.5 under a 0.4.40 window (2026-11-26), and only a reinstall with the programs closed replaced it.
/// A program keeps its plug-in file loaded, and Windows lets a loaded file be renamed though not overwritten: the old
/// file is moved aside (name.old-...), the new one is copied to the old name, and the program takes it at its next
/// start. Until then the loaded plug-in hands its work to the newest downloaded copy by itself (CsiProMaxPlugin.Newest),
/// when it is new enough to know how. Nothing else in the installed folder is touched.
/// </summary>
internal static class PlugInRefresh
{
    /// <summary>What was done, one line per plug-in file that was replaced or could not be; empty when all were current.</summary>
    public static IReadOnlyList<string> Run(string from, string into, IEnumerable<string> files, Func<string, Version>? versionOf = null)
    {
        var said = new List<string>();
        if (string.Equals(Path.GetFullPath(from).TrimEnd('\\', '/'), Path.GetFullPath(into).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return said;
        foreach (var stale in SafeFiles(into, "*.old-*")) Try(() => File.Delete(stale));
        foreach (var name in files)
        {
            string source = Path.Combine(from, name), target = Path.Combine(into, name);
            try
            {
                if (!File.Exists(source) || !File.Exists(target)) continue;
                Version mine = (versionOf ?? VersionOf)(source), theirs = (versionOf ?? VersionOf)(target);
                if (mine <= theirs) continue;
                try { File.Copy(source, target, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A program has it loaded: aside, then the new one under the old name.
                    var aside = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
                    File.Move(target, aside);
                    try { File.Copy(source, target, false); }
                    catch { Try(() => File.Move(aside, target)); throw; }
                }
                Updater.ClearDownloadMark(target);
                said.Add($"{name}: {theirs} replaced by {mine}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
            {
                said.Add($"{name} could not be replaced ({ex.Message}).");
            }
        }
        return said;
    }

    /// <summary>The files copied last: once they are in place the installed copy is the new version, so everything it needs is there before them.</summary>
    private static readonly string[] Last = ["CsiProMax.deps.json", "CsiProMax.runtimeconfig.json", "CsiProMax.dll"];

    /// <summary>
    /// Brings the whole installed folder to the version that runs (Ahmed, 2026-11-26: one update must bring every part
    /// to the same version, "not just the one in etabs or safe": the chat window, the plug-ins, the Modeler, the Viewer
    /// and the shortcuts all start from this folder). Only files whose content differs are written, a file in use is
    /// moved aside as a plug-in is, CsiProMax.dll goes last, and an installed copy that is newer is left alone. Files
    /// the installed folder has and the version does not are kept.
    /// </summary>
    /// <returns>One line saying what was done, or null when the installed folder was already this version, or newer.</returns>
    public static string? Everything(string from, string into, Func<string, Version>? versionOf = null)
    {
        if (string.Equals(Path.GetFullPath(from).TrimEnd('\\', '/'), Path.GetFullPath(into).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
        string mine = Path.Combine(from, "CsiProMax.dll"), theirs = Path.Combine(into, "CsiProMax.dll");
        if (!File.Exists(mine) || !File.Exists(theirs)) return null;
        Version running, installed;
        try { running = (versionOf ?? VersionOf)(mine); installed = (versionOf ?? VersionOf)(theirs); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) { return $"The installed copy's version could not be read ({ex.Message})."; }
        if (running < installed) return null;
        foreach (var stale in SafeFiles(into, "*.old-*")) Try(() => File.Delete(stale));
        var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(from, f))
            .Where(r => !r.Equals(UpdateResume.CompleteMark, StringComparison.OrdinalIgnoreCase) && !r.Contains(".old-", StringComparison.Ordinal))
            .OrderBy(r => Array.FindIndex(Last, l => l.Equals(r, StringComparison.OrdinalIgnoreCase))).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        int written = 0;
        var failed = new List<string>();
        foreach (var relative in files)
        {
            string source = Path.Combine(from, relative), target = Path.Combine(into, relative);
            try
            {
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length && UpdateResume.Sha256Of(target) == UpdateResume.Sha256Of(source)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(source, target, true); }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(target))
                {
                    var aside = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
                    File.Move(target, aside);
                    try { File.Copy(source, target, false); }
                    catch { Try(() => File.Move(aside, target)); throw; }
                }
                Updater.ClearDownloadMark(target);
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{relative} ({ex.Message})");
                // The new CsiProMax.dll must not land on files that are not all there: the old one stays and hands over as before.
                if (!Last.Contains(relative, StringComparer.OrdinalIgnoreCase)) { failed.Add("so CsiProMax.dll was left as it is"); break; }
            }
        }
        if (written == 0 && failed.Count == 0) return null;
        return failed.Count == 0
            ? $"The installed copy in {into} is now {Three(running)} (was {Three(installed)}): {written} files written."
            : $"The installed copy in {into} could not be brought to {Three(running)} completely ({written} files written): {string.Join("; ", failed)}.";
    }

    private static string Three(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    /// <summary>
    /// Every part's version on this PC, and whether they are one: this run, the installed CsiProMax.dll (which the
    /// Modeler, the Viewer and the shortcuts start), each installed plug-in file, and the newest downloaded version.
    /// </summary>
    public static (bool Same, string Text) Aligned(string? running, string? installFolder, string? newestDownloaded, IEnumerable<string> plugIns, Func<string, Version>? versionOf = null)
    {
        var parts = new List<(string Part, string Version)>();
        if (running is not null) parts.Add(("this run", running));
        if (newestDownloaded is not null) parts.Add(("newest downloaded", newestDownloaded));
        if (installFolder is not null)
            foreach (var name in new[] { "CsiProMax.dll" }.Concat(plugIns))
            {
                var file = Path.Combine(installFolder, name);
                string version;
                try { version = File.Exists(file) ? Three((versionOf ?? VersionOf)(file)) : "missing"; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) { version = "unreadable"; }
                parts.Add(("installed " + name, version));
            }
        var text = string.Join(", ", parts.Select(p => $"{p.Part} {p.Version}"));
        return (parts.Select(p => p.Version).Distinct().Count() == 1, text);
    }

    private static Version VersionOf(string file) => AssemblyName.GetAssemblyName(file).Version ?? new Version(0, 0);

    private static string[] SafeFiles(string folder, string pattern)
    {
        try { return Directory.GetFiles(folder, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static void Try(Action step)
    {
        try { step(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
