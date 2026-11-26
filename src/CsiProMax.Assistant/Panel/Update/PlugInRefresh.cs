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
