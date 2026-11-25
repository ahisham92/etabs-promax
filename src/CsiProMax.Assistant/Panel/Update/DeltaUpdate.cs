using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace CsiProMax.Update;

/// <summary>
/// An update that downloads only what changed. Each release publishes files.json (every file of the package with its
/// SHA-256, and which of them are in the "changed" zip: the ones the release before did not have with the same
/// content). The new version's folder is built from the files this PC already has with the right content, and only
/// the changed zip is downloaded, a few megabytes instead of the whole package. When that cannot give every file
/// (a release without the list, a PC several versions behind that lacks a file the changed zip does not hold, a
/// download or a checksum that fails), false is answered and the whole package is downloaded as before.
/// </summary>
internal static class DeltaUpdate
{
    internal sealed record Entry(string Path, string Sha256, long Size, bool Changed);

    /// <param name="Copy">Files this PC has with the right content, and where.</param>
    /// <param name="Download">Files to take from the changed zip.</param>
    /// <param name="Possible">False when a file is needed that the changed zip does not hold.</param>
    internal sealed record Plan(IReadOnlyList<(Entry File, string From)> Copy, IReadOnlyList<Entry> Download, bool Possible);

    public static string ManifestUrl(string repository, string tag) => $"https://github.com/{repository}/releases/download/{tag}/files.json";

    /// <summary>
    /// The changed zip's name. Not "CsiProMax-...": versions up to 0.4.7 take the first release file with that start
    /// as the whole package, and must never take this one.
    /// </summary>
    public static string ChangedName(string version) => $"changed-{version}.zip";

    public static string ChangedUrl(string repository, string tag, string version) => $"https://github.com/{repository}/releases/download/{tag}/{ChangedName(version)}";

    /// <summary>The file list of a release, or null when the text is not one (or names a path outside the package).</summary>
    internal static IReadOnlyList<Entry>? Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Entry>();
            foreach (var f in files.EnumerateArray())
            {
                var path = f.GetProperty("path").GetString() ?? "";
                var sha = f.GetProperty("sha256").GetString() ?? "";
                if (!Safe(path) || sha.Length != 64) return null;
                list.Add(new Entry(path, sha, f.TryGetProperty("size", out var s) ? s.GetInt64() : 0, f.TryGetProperty("changed", out var c) && c.ValueKind == JsonValueKind.True));
            }
            return list.Count > 0 && list.Any(e => e.Path.Equals("CsiProMax.dll", StringComparison.OrdinalIgnoreCase)) ? list : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>A path inside the package: relative, forward slashes, no step up.</summary>
    internal static bool Safe(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");

    /// <summary>What to copy and what to download. <paramref name="have"/> answers the SHA-256 and place of a file this PC has, or null.</summary>
    internal static Plan Make(IReadOnlyList<Entry> files, Func<string, (string Sha256, string From)?> have)
    {
        var copy = new List<(Entry, string)>();
        var download = new List<Entry>();
        var possible = true;
        foreach (var file in files)
        {
            if (have(file.Path) is { } local && local.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) copy.Add((file, local.From));
            else
            {
                download.Add(file);
                if (!file.Changed) possible = false;
            }
        }
        return new Plan(copy, download, possible);
    }

    /// <summary>
    /// Builds <paramref name="target"/> (the new version's folder) from <paramref name="sources"/> and the changed zip.
    /// True when the folder is complete and every file has the listed content; false (and nothing left behind) otherwise.
    /// </summary>
    public static async Task<bool> TryAsync(HttpClient http, string repository, string version, string target, string updatesFolder,
        IReadOnlyList<string> sources, UpdateProgress progress, CancellationToken ct)
    {
        var temp = target + ".delta";
        var zip = Path.Combine(updatesFolder, ChangedName(version));
        try
        {
            string json;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(30));
                using var answer = await http.GetAsync(ManifestUrl(repository, "v" + version), limit.Token).ConfigureAwait(false);
                if (!answer.IsSuccessStatusCode) return false;
                json = await answer.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            if (Read(json) is not { } files) return false;

            var plan = Make(files, path =>
            {
                foreach (var source in sources)
                {
                    var file = Path.Combine(source, path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(file)) return (UpdateResume.Sha256Of(file), file);
                }
                return null;
            });
            if (!plan.Possible) return false;

            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            string Place(Entry e)
            {
                var to = Path.Combine(temp, e.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                return to;
            }
            foreach (var (file, from) in plan.Copy) File.Copy(from, Place(file), true);

            if (plan.Download.Count > 0)
            {
                var size = plan.Download.Sum(e => e.Size);
                progress.Report(UpdateStage.Downloading, $"Downloading only what changed in CSI ProMax {version}: {plan.Download.Count} of {files.Count} files ({UpdateSnapshot.Megabytes(size)} before packing)…", version);
                Directory.CreateDirectory(updatesFolder);
                await UpdateResume.DownloadAsync(http, ChangedUrl(repository, "v" + version, version), zip, progress.ReportBytes, UpdateResume.Stall, ct).ConfigureAwait(false);
                progress.Report(UpdateStage.Unpacking, $"Unpacking CSI ProMax {version}…");
                using var archive = ZipFile.OpenRead(zip);
                foreach (var file in plan.Download)
                {
                    var entry = archive.GetEntry("dist/" + file.Path) ?? throw new InvalidDataException($"The changed files of {version} do not hold {file.Path}.");
                    entry.ExtractToFile(Place(file), true);
                }
            }
            else progress.Report(UpdateStage.Unpacking, $"CSI ProMax {version}: every file is already on this PC, nothing to download.", version);

            // Every file is checked, copied or downloaded: a version folder is only ever a whole, correct package.
            progress.Report(UpdateStage.Verifying, "Checking every file of the new version…");
            foreach (var file in files)
                if (!UpdateResume.Sha256Of(Place(file)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{file.Path} does not have the content the release lists.");

            if (Directory.Exists(target)) UpdateResume.WithRetry(() => Directory.Delete(target, true));
            UpdateResume.WithRetry(() => Directory.Move(temp, target));
            foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories)) Updater.ClearDownloadMark(file);
            UpdateResume.MarkComplete(target, version);
            try { File.Delete(zip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Clean(temp); throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
                                       or OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            // The whole package is downloaded instead; nothing of this try is kept.
            Clean(temp);
            UpdateResume.Discard(zip);
            return false;
        }
    }

    private static void Clean(string temp)
    {
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
