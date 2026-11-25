using System.IO;
using System.IO.Compression;

namespace EtabsProMax.Chat;

/// <summary>
/// Claude Code deletes its own transcripts of conversations after 30 days (…\.claude\projects\…\&lt;session&gt;.jsonl), and
/// with them the whole code of every chat and job. The owner, 19 Nov 2026: "it can hide in the app itself but keep the
/// history and coding in a folder so we can restore it when needed". So every transcript is copied, compressed, into
/// ETABS ProMax's own folder (%APPDATA%\EtabsProMax\history\transcripts\&lt;project&gt;\&lt;session&gt;.jsonl.gz) when ETABS
/// starts and every hour, and a newer version of a file replaces its copy; nothing there is ever deleted. The kept-code
/// library (Tools.WorkedCode) reads these copies too. Never throws; off ETABS's thread.
/// </summary>
internal static class HistoryArchive
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EtabsProMax", "history", "transcripts");

    private static string Source => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// Only ETABS ProMax's own conversations are kept: Claude Code names each project's folder after the folder it ran
    /// in, and the chat's engine runs in ETABS ProMax's own folder. The user's other Claude Code work is not ours to copy.
    /// </summary>
    internal static bool Ours(string root, string file)
    {
        var relative = Relative(root, file);
        var cut = relative.IndexOfAny(new[] { '\\', '/' });
        return (cut < 0 ? relative : relative.Substring(0, cut)).IndexOf("EtabsProMax", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Removes copies an earlier build made of projects that are not ETABS ProMax's (the originals are untouched).</summary>
    private static void DropOthers(string archive)
    {
        try
        {
            if (!Directory.Exists(archive)) return;
            foreach (var folder in Directory.GetDirectories(archive))
                if (Path.GetFileName(folder).IndexOf("EtabsProMax", StringComparison.OrdinalIgnoreCase) < 0)
                    try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static int _started;

    /// <summary>Copies now, then every hour while ETABS runs (once per ETABS session).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            DropOthers(Folder);
            while (true)
            {
                Copy(Source, Folder);
                await Task.Delay(TimeSpan.FromHours(1)).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Copies each transcript under <paramref name="source"/> that is new or newer than its copy into
    /// <paramref name="archive"/>, compressed, keeping the folder below the source. How many were copied.
    /// </summary>
    internal static int Copy(string source, string archive)
    {
        var copied = 0;
        try
        {
            if (!Directory.Exists(source)) return 0;
            foreach (var file in Directory.EnumerateFiles(source, "*.jsonl", SearchOption.AllDirectories))
            {
                if (!Ours(source, file)) continue;
                try
                {
                    var target = Path.Combine(archive, Relative(source, file) + ".gz");
                    var info = new FileInfo(file);
                    if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= info.LastWriteTimeUtc) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temp = target + ".tmp";
                    using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var output = File.Create(temp))
                    using (var zip = new GZipStream(output, CompressionLevel.Optimal))
                        input.CopyTo(zip);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(temp, target);
                    File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: next hour */ }
            }
        }
        catch (Exception) { /* never breaks anything */ }
        return copied;
    }

    /// <summary>
    /// Every transcript there is, once each: Claude Code's own files, and the archived copy of each one Claude Code has
    /// since deleted. Each is read lazily, line by line.
    /// </summary>
    public static IEnumerable<Func<IEnumerable<string>>> AllTranscripts() => All(Source, Folder);

    /// <summary>One session's transcript: Claude Code's own file, else its archived copy; null when neither is there.</summary>
    public static Func<IEnumerable<string>>? Find(string session)
    {
        if (string.IsNullOrEmpty(session)) return null;
        try
        {
            if (Directory.Exists(Source) && Directory.EnumerateFiles(Source, session + ".jsonl", SearchOption.AllDirectories).FirstOrDefault(f => Ours(Source, f)) is { } live)
                return () => ReadShared(live);
            if (Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, session + ".jsonl.gz", SearchOption.AllDirectories).FirstOrDefault() is { } kept)
                return () => ReadZipped(kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static List<Func<IEnumerable<string>>> All(string source, string archive)
    {
        var list = new List<Func<IEnumerable<string>>>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(source))
                foreach (var file in Directory.EnumerateFiles(source, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (!Ours(source, file)) continue;
                    live.Add(Relative(source, file));
                    var path = file;
                    list.Add(() => ReadShared(path));
                }
            if (Directory.Exists(archive))
                foreach (var file in Directory.EnumerateFiles(archive, "*.jsonl.gz", SearchOption.AllDirectories))
                {
                    if (!Ours(archive, file)) continue;
                    var name = Relative(archive, file);
                    if (live.Contains(name.Substring(0, name.Length - 3))) continue;
                    var path = file;
                    list.Add(() => ReadZipped(path));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    private static string Relative(string root, string file) =>
        file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static IEnumerable<string> ReadZipped(string path)
    {
        using var stream = File.OpenRead(path);
        using var zip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(zip);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
