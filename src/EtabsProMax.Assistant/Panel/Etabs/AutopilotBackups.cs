using System.IO;

namespace EtabsProMax.ETABS;

/// <summary>
/// Autopilot's safety net, and the only one ETABS allows (its API has no undo): each model it changes is copied into a
/// Backup folder beside it ("Backup\Tower autopilot backup 2026-11-25 0310.EDB") before its first change, once per run.
/// The copy is of the model as last saved: ETABS ProMax never saves the engineer's model by itself.
/// </summary>
internal static class AutopilotBackups
{
    public static string? Ensure(EtabsDoc doc)
    {
        try
        {
            if (doc.PathName.Length == 0 || !Chat.AutopilotShare.Claim(doc.PathName, DateTime.UtcNow)) return null;
            var copy = Copy(doc.PathName);
            Chat.Autopilot.Log(Chat.AutopilotEntry.Note, copy.Length > 0 ? $"Backup of \"{doc.Title}\" (as last saved) kept before its first change: {copy}"
                : $"No backup of \"{doc.Title}\" could be kept before its first change.");
            return copy;
        }
        catch (Exception) { return null; /* the change goes ahead; the log says no backup when it can */ }
    }

    /// <summary>Copies the model's file into a Backup folder beside it; the copy's path, or "" when it couldn't.</summary>
    public static string Copy(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var folder = Path.Combine(Path.GetDirectoryName(path) ?? "", "Backup");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " autopilot backup "
                + DateTime.Now.ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(path));
            File.Copy(path, copy, overwrite: true);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }
}
