using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace CsiProMax.Update;

/// <summary>
/// Keeps CSI ProMax up to date without reinstalling, and without restarting ETABS.
///
/// It asks GitHub for the latest release at ETABS start and hourly (see App.UpdateLoopAsync). With automatic updates off
/// it only says a newer one is out (<see cref="Latest"/>, UI/UpdateNotice). Otherwise, when a newer one exists it downloads the zip to
/// %LOCALAPPDATA%\CsiProMax\updates, unpacks it into %LOCALAPPDATA%\CsiProMax\versions\&lt;version&gt;, and:
///   - tells the loader (CsiProMax.Loader, which ETABS loads) that a newer core is ready. The loader switches to it
///     the next time ETABS is idle with no job running: the old core stops, the new one starts. Nothing is overwritten;
///     the old DLLs stay loaded, unused, until ETABS closes.
///   - puts the new loader in place of the installed one (Addins\&lt;year&gt;\CsiProMax\CsiProMax.Loader.dll), only when
///     it changed. CsiProMax.addin keeps naming that one path, so ETABS's "Always Load" for it holds (AddinFiles).
///   - replaces the desktop-app connector (CsiProMax.Mcp.exe). Windows lets a running exe be renamed, so
///     the old one is moved aside and the new one copied in; the Claude app uses it after its next restart. The tool
///     list itself reaches the Claude app straight away: the new core rewrites tools.json, which the connector watches.
/// Settings live in %APPDATA%\CsiProMax and are never touched.
/// </summary>
public static class Updater
{
    public const string Repository = "ahisham92/etabs-promax";

    private static readonly string LocalRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax");

    private static string UpdatesFolder => Path.Combine(LocalRoot, "updates");
    internal static string VersionsFolder => Path.Combine(LocalRoot, "versions");
    private static string StateFile => Path.Combine(LocalRoot, "update-state.json");

    /// <summary>Where a check that could not run says why (the window's activity log), so it can be read on the PC afterwards.</summary>
    public static Action<string>? Said { get; set; }

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}" + (Current.Revision > 0 ? $"-test{Current.Revision}" : "");

    /// <summary>
    /// Sketch ProMax switches versions inside a running Revit. CSI ProMax is its own window: a new version is used the
    /// next time the window is opened (Program picks the newest downloaded version), so nothing is switched live.
    /// </summary>
    public static bool CanSwitchLive => false;

    /// <summary>Version downloaded and ready for the next ETABS start, if any.</summary>
    public static string? Staged => LoadState().Staged is { } s && Parse(s) > Current ? s : null;

    /// <summary>
    /// The newest release the last successful check saw, when it is newer than this version; null otherwise. The state
    /// file is shared by every ETABS window, and another window may run a newer version: a check that window made says
    /// "up to date" for it, not for this one, so the badge compares this version with what was seen.
    /// </summary>
    public static string? Latest => LoadState().Latest is { } l && Parse(l) > Current ? l : null;

    /// <summary>The release notes the last check read for <paramref name="version"/>, or null.</summary>
    public static string? NotesFor(string version) => LoadState() is { Latest: { } l, Notes: { } n } && Parse(l) == Parse(version) ? n : null;

    /// <summary>When updates were last looked for, and why that look failed (null when it worked), for the Update button's badge.</summary>
    public static (DateTime? LastCheck, string? Error) LastCheckState()
    {
        var state = LoadState();
        return (state.LastCheck, state.LastError);
    }

    /// <summary>
    /// True when the last check (by any ETABS window) is at least <paramref name="every"/> old by the wall clock, or
    /// there was none, or the clock went back. The update loop asks every few minutes rather than sleeping an hour: a
    /// timer stands still while Windows sleeps, so after a night asleep an hourly timer still had most of its hour to go.
    /// </summary>
    public static bool IsDue(TimeSpan every) => UI.UpdateBadge.CheckDue(LoadState().LastCheck, every, DateTime.UtcNow);

    /// <summary>A one-line description for the Status button and for Claude.</summary>
    public static string Describe()
    {
        var state = LoadState();
        if (Latest is { } latest && Staged is null)
            return $"This window runs CSI ProMax {CurrentText}. {latest} is out and not installed yet: click Update to install it; {Csi.Name} stays open.";
        if (Staged is { } staged)
            return $"This window runs CSI ProMax {CurrentText}. {staged} is downloaded and not in use yet: the window switches to it by itself as soon as no work is running ({Csi.Name} stays open).";
        if (state.LastError is { } error)
            return $"CSI ProMax {CurrentText}. The last update check failed: {error}";
        return state.LastCheck is { } last
            ? $"CSI ProMax {CurrentText} is up to date (checked {last.ToLocalTime():g})."
            : $"CSI ProMax {CurrentText}.";
    }

    /// <summary>
    /// Checks for and stages an update, reporting each stage to <paramref name="progress"/>. When the loader runs this core
    /// it is then asked to switch to the new version (the switch itself happens on ETABS's thread, later). Returns the
    /// newly staged version, or null when there is nothing new. With <paramref name="install"/> false (automatic updates
    /// off) a newer release is only recorded and reported as <see cref="UpdateStage.Available"/>; nothing is downloaded.
    /// </summary>
    /// <summary>1 while this core runs <see cref="CheckAsync"/> (a second call, such as a click during the automatic check, waits for nothing).</summary>
    private static int _running;
    private static volatile UpdateProgress? _runningProgress;

    /// <summary>Mirrors the run already going into <paramref name="mine"/> until it ends.</summary>
    private static void Follow(UpdateProgress running, UpdateProgress mine)
    {
        Action<UpdateSnapshot>? copy = null;
        copy = s =>
        {
            if (s.Bytes > 0) mine.ReportBytes(s.Bytes, s.Total);
            mine.Report(s.Stage, s.Detail, s.Version);
            if (s.IsFinal) running.Changed -= copy;
        };
        running.Changed += copy;
        copy(running.Snapshot);
    }

    public static async Task<string?> CheckAsync(bool force, UpdateProgress? progress = null, CancellationToken ct = default, bool install = true)
    {
        progress ??= new UpdateProgress();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, progress.Cancellation.Token);
        ct = linked.Token;
        // Already updating in this window (the automatic check at start or on the hour): it carries on by itself.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            // The click shows the background run's progress instead of saying another window is updating.
            if (_runningProgress is { } running && !ReferenceEquals(running, progress)) Follow(running, progress);
            else progress.Report(UpdateStage.Applying, $"Already updating in the background. It switches over by itself when {Csi.Name} is idle.");
            return null;
        }
        _runningProgress = progress;
        // Only one ETABS window updates at a time. A lock file, because a mutex can't be released after an await.
        FileStream? gate;
        try
        {
            Directory.CreateDirectory(LocalRoot);
            gate = new FileStream(Path.Combine(LocalRoot, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runningProgress = null;
            Interlocked.Exchange(ref _running, 0);
            progress.Report(UpdateStage.Failed, $"Another open {Csi.Name} is updating right now. This one follows it by itself when it is done.");
            Said?.Invoke($"The check for updates did not run: update.lock is held by another CSI ProMax ({ex.Message}).");
            return null;
        }

        try
        {
            CleanUp();
            var state = LoadState();
            if (!force && state.LastCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(1))
            {
                progress.Report(UpdateStage.Idle);
                return null;
            }

            progress.Report(UpdateStage.Checking, "Asking GitHub for the latest release…");
            var release = await LatestReleaseAsync(ct).ConfigureAwait(false);
            state.LastCheck = DateTime.UtcNow;
            state.LastError = null;
            state.Latest = release?.Version;
            state.Notes = release?.Notes is { Length: > 4000 } full ? full.Substring(0, 4000) : release?.Notes;
            if (release is null || Parse(release.Version) <= Current)
            {
                SaveState(state);
                progress.Report(UpdateStage.UpToDate, $"CSI ProMax {CurrentText} is the latest version.", CurrentText);
                return null;
            }
            var stagedFolder = Path.Combine(VersionsFolder, release.Version);
            var downloaded = release.Version == state.Staged && Directory.Exists(stagedFolder);
            if (!install && !downloaded)
            {
                SaveState(state);
                progress.Report(UpdateStage.Available, $"CSI ProMax {release.Version} is available. Automatic updates are off, so nothing was downloaded.", release.Version);
                return null;
            }
            if (downloaded)
            {
                // Already downloaded (by this or another ETABS window). Make sure its loader is in place (nothing is written
                // when it is), and switch this window over too.
                SaveState(state);
                Apply(stagedFolder, release.Version, progress);
                return null;
            }

            // What was found is kept even if the download then fails: the badge still offers the new version.
            SaveState(state);
            var folder = await DownloadAndUnpackAsync(release, progress, ct).ConfigureAwait(false);
            progress.Report(UpdateStage.Applying, "Installing…");
            state.Staged = release.Version;
            SaveState(state);
            Apply(folder, release.Version, progress);
            return release.Version;
        }
        catch (OperationCanceledException) when (progress.Cancellation.IsCancellationRequested)
        {
            progress.Report(UpdateStage.Cancelled, "The update was cancelled. Nothing changed; it is offered again at the next check.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or JsonException or InvalidDataException or OperationCanceledException or InvalidOperationException
                                       or System.Xml.XmlException or TimeoutException)
        {
            var state = LoadState();
            state.LastCheck = DateTime.UtcNow;
            state.LastError = ex is OperationCanceledException ? "the download took too long" : ex.Message;
            SaveState(state);
            progress.Report(UpdateStage.Failed, $"The update didn't finish: {state.LastError}. Nothing changed. " +
                                                "Try again (or the next automatic check) continues from where it stopped.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else: never silent. On one PC a window ran for an hour with no check written and no error to
            // read (2026-11-26); whatever stops a check is now kept and said, and the next round tries again.
            try
            {
                var state = LoadState();
                state.LastCheck = DateTime.UtcNow;
                state.LastError = $"{ex.GetType().Name}: {ex.Message}";
                SaveState(state);
            }
            catch (Exception) { /* the note below still says it */ }
            Said?.Invoke($"The check for updates failed ({ex.GetType().Name}: {ex.Message}).");
            progress.Report(UpdateStage.Failed, $"The update didn't finish: {ex.Message}. Nothing changed; the next automatic check tries again.");
            return null;
        }
        finally
        {
            gate.Dispose();
            _runningProgress = null;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>A downloaded version is switched to by the open window itself as soon as no work is running (App.SwitchWhenIdle).</summary>
    private static void Apply(string versionFolder, string version, UpdateProgress progress) =>
        progress.Report(UpdateStage.RestartNeeded,
            $"CSI ProMax {version} is downloaded. This window closes and opens again on {version} by itself as soon as no work is running; " +
            $"{Csi.Name} stays open and your model is not touched. The window's title shows the version that is running.", version);

    /// <summary>
    /// The folder of the newest complete downloaded version that is newer than <paramref name="running"/>, or null: what
    /// Program starts instead of itself, so an update is used without anyone copying files into the installed folder.
    /// </summary>
    internal static string? NewestDownloaded(Version running, string? versionsFolder = null)
    {
        try
        {
            var root = versionsFolder ?? VersionsFolder;
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root)
                .Select(f => (Folder: f, Version: Version.TryParse(Path.GetFileName(f), out var v) ? Normalize(v) : null))
                .Where(f => f.Version is not null && f.Version > running && UpdateResume.IsComplete(f.Folder) && File.Exists(Path.Combine(f.Folder, "CsiProMax.dll")))
                .OrderByDescending(f => f.Version).Select(f => f.Folder).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>For the tests: what <see cref="Behind()"/> answers, instead of this PC's own update state.</summary>
    internal static Func<string?>? BehindProbe;

    /// <summary>
    /// The one rule for "is this the newest CSI ProMax": null when it is, otherwise a line that says which version runs,
    /// which newer one is known and how to get onto it. Every test and report asks here, so a run on old code says so
    /// itself (a tester once sent two reports from an old version while the window offered the new one).
    /// </summary>
    public static string? Behind()
    {
        if (BehindProbe is { } probe) return probe();
        var state = LoadState();
        return Behind(Current, state.Latest, state.Staged, NewestDownloaded(Current) is { } folder ? Path.GetFileName(folder) : null);
    }

    internal static string? Behind(Version running, string? latest, string? staged, string? downloaded)
    {
        var mine = $"{running.Major}.{running.Minor}.{running.Build}";
        var ready = new[] { staged, downloaded }.Where(v => v is not null && Parse(v) > running).OrderByDescending(v => Parse(v!)).FirstOrDefault();
        var newest = new[] { latest, ready }.Where(v => v is not null && Parse(v) > running).OrderByDescending(v => Parse(v!)).FirstOrDefault();
        if (newest is null) return null;
        return ready is not null && Parse(ready) >= Parse(newest)
            ? $"This is CSI ProMax {mine}, an old version: {ready} is downloaded on this PC and not in use yet. The window switches to it by itself when no work is running (or close the CSI ProMax window and open it again from the Tools menu); its title then says {ready}. Then run this again. What an old version reports is already fixed or changed."
            : $"This is CSI ProMax {mine}, an old version: {newest} is out. Update first (Settings > Updates > Check for updates, or close the CSI program and run the install line again), open CSI ProMax again, and run this again. What an old version reports is already fixed or changed.";
    }

    internal sealed record Release(string Version, string ZipUrl, string? Sha256, string? Notes = null);

    private static HttpClient CreateClient()
    {
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CsiProMax/{CurrentText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// The latest release: GitHub's API first, with a short time limit; on any failure of it (an error status, a refused or
    /// timed-out connection, an unreadable answer) the github.com release page, whose package has a fixed address there.
    /// </summary>
    internal static async Task<Release?> LatestReleaseAsync(CancellationToken ct)
    {
        var (release, _) = await ReleaseSource.ApiThenWebAsync(TestBuilds() ? NewestWithTestBuildsAsync : LatestReleaseFromApiAsync, LatestReleaseFromWebAsync, ReleaseSource.ApiTimeout, ct).ConfigureAwait(false);
        return release;
    }

    private static async Task<ReleaseSource.Answer<Release>> LatestReleaseFromApiAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return ReleaseSource.Found<Release>(null); // no release published yet
        // GitHub's API allows 60 anonymous requests an hour per network address, which an office network shares.
        // The release web page has no such limit, so fall back to it rather than fail.
        if (!response.IsSuccessStatusCode) return ReleaseSource.Fallback<Release>($"GitHub's API answered {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;
        var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return ReleaseSource.Found<Release>(null);
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return ReleaseSource.Found<Release>(null);
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                // The whole package, by its exact name: a release has other zips too (the changed files).
                if (!name.Equals($"CsiProMax-{version}.zip", StringComparison.OrdinalIgnoreCase)) continue;
                // GitHub publishes a "sha256:..." digest for each release file; verify it when present.
                var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7) : null;
                return ReleaseSource.Found(new Release(version, asset.GetProperty("browser_download_url").GetString()!, sha, notes));
            }
        // A newer release whose package isn't listed (yet): not "up to date". Its package has the usual name; when it is
        // not there the download fails, the error is shown and the next check tries again.
        if (!Version.TryParse(version, out _)) return ReleaseSource.Found<Release>(null);
        return ReleaseSource.Found(new Release(version, ReleaseSource.DownloadUrl(Repository, tag, version), null, notes));
    }

    /// <summary>
    /// The latest release without the API: github.com/.../releases/latest redirects to the release's tag, and the
    /// package name follows from the version. There is no published digest this way, so the zip's contents are checked instead.
    /// </summary>
    private static async Task<Release?> LatestReleaseFromWebAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CsiProMax/{CurrentText}");
        using var response = await http.GetAsync($"https://github.com/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (ReleaseSource.TagFromRedirect(response.Headers.Location?.ToString()) is not { } tag)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"GitHub did not say which release is the latest ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _)) throw new InvalidDataException($"The latest release tag '{tag}' is not a version number.");
        return new Release(version, ReleaseSource.DownloadUrl(Repository, tag, version), null);
    }

    /// <summary>The Assembly path of every installed CsiProMax.addin, for all ETABS versions.</summary>
    private static bool IsUnder(string path, string folder)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// Downloads and unpacks <paramref name="release"/> into &lt;root&gt;\versions\&lt;version&gt; (root: %LOCALAPPDATA%\CsiProMax,
    /// or TritoETABS's own folder for its copy). Returns that folder. An earlier try that stopped part way is continued, not
    /// started again (<see cref="UpdateResume"/>): a folder already unpacked is used as it is, a complete download is only
    /// checked, a partial one continues from where it stopped, and unpacking skips files already written.
    /// </summary>
    internal static async Task<string> DownloadAndUnpackAsync(Release release, UpdateProgress progress, CancellationToken ct, string? root = null)
    {
        var updatesFolder = root is null ? UpdatesFolder : Path.Combine(root, "updates");
        var versionsFolder = root is null ? VersionsFolder : Path.Combine(root, "versions");
        var target = Path.Combine(versionsFolder, release.Version);
        if (UpdateResume.IsComplete(target))
        {
            progress.Report(UpdateStage.Unpacking, $"CSI ProMax {release.Version} was already downloaded and unpacked by an earlier try.", release.Version);
            return target;
        }
        Directory.CreateDirectory(updatesFolder);
        // Only what changed, when the release says what that is and this PC has the rest (DeltaUpdate); else the whole package.
        using (var http = CreateClient())
            if (await DeltaUpdate.TryAsync(http, Repository, release.Version, target, updatesFolder, DeltaSources(), progress, ct).ConfigureAwait(false))
                return target;
        var zip = Path.Combine(updatesFolder, $"CsiProMax-{release.Version}.zip");

        // A complete download from an earlier try is checked rather than fetched again; a damaged one is fetched afresh.
        string? sha = null;
        if (File.Exists(zip))
        {
            progress.Report(UpdateStage.Verifying, "Checking the download an earlier try finished…", release.Version);
            try { sha = Verify(zip, release); }
            catch (InvalidDataException) { UpdateResume.Discard(zip); }
        }
        if (sha is null)
        {
            var have = UpdateResume.Downloaded(zip);
            progress.Report(UpdateStage.Downloading, have > 0
                ? $"Continuing the download of CSI ProMax {release.Version} from {UpdateSnapshot.Megabytes(have)}…"
                : $"Downloading CSI ProMax {release.Version}…", release.Version);
            using (var http = CreateClient())
                await UpdateResume.DownloadAsync(http, release.ZipUrl, zip, progress.ReportBytes, UpdateResume.Stall, ct).ConfigureAwait(false);
            progress.Report(UpdateStage.Verifying, "Checking the download is complete and undamaged…");
            try { sha = Verify(zip, release); }
            catch (InvalidDataException)
            {
                // Damaged: the next try downloads it afresh rather than continuing it.
                UpdateResume.Discard(zip);
                throw;
            }
        }
        // From here on the update completes: cancelling half an unpack would only leave a mess.
        ClearDownloadMark(zip);
        var temp = target + ".partial";
        var kept = 0;
        progress.Report(UpdateStage.Unpacking, $"Unpacking CSI ProMax {release.Version}…");
        try { kept = UpdateResume.Extract(zip, temp, sha); }
        catch (InvalidDataException)
        {
            // The zip itself is unreadable (a check without a published digest can't see every fault): fetch it afresh next time.
            UpdateResume.Discard(zip);
            throw;
        }
        if (kept > 0) progress.Report(UpdateStage.Unpacking, $"Unpacking CSI ProMax {release.Version} (continuing: {kept} files were already unpacked)…");
        var dist = Directory.GetDirectories(temp, "dist", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new InvalidDataException("The update package has no dist folder.");
        if (Directory.Exists(target)) UpdateResume.WithRetry(() => Directory.Delete(target, true));
        UpdateResume.WithRetry(() => Directory.Move(dist, target));
        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories)) ClearDownloadMark(file);
        UpdateResume.MarkComplete(target, release.Version);
        TryDelete(() => Directory.Delete(temp, true));
        TryDelete(() => File.Delete(zip));
        return target;
    }

    /// <summary>Where this PC has CSI ProMax files an update can reuse: the running version first, then the installed folder.</summary>
    private static IReadOnlyList<string> DeltaSources() =>
        new[] { AppContext.BaseDirectory, Assistant.ToolsMenu.InstalledIn }.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)).Select(f => f!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Builds &lt;versions&gt;\&lt;version&gt; from the installed files that are unchanged and downloads only the rest
    /// (<see cref="DeltaUpdate"/>). False when that isn't possible (an older release without a file list, a failed
    /// download, a file that doesn't match its checksum): the whole zip is downloaded instead.
    /// </summary>
    private static string Verify(string zip, Release release)
    {
        var actual = UpdateResume.Sha256Of(zip);
        if (release.Sha256 is { } expected)
        {
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update is damaged (checksum mismatch). The next try downloads it again.");
            return actual;
        }
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        if (!names.Any(n => n.Contains("dist/") && n.EndsWith("/CsiProMax.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The downloaded update is incomplete (no CsiProMax.dll). The next try downloads it again.");
        return actual;
    }

    private static void CleanUp()
    {
        if (!Directory.Exists(VersionsFolder)) return;
        var running = Path.GetDirectoryName(typeof(Updater).Assembly.Location) ?? "";
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CurrentText };
        var saved = LoadState();
        if (saved.Staged is { } staged) keep.Add(staged);
        // What a try of the newest release has done so far (unpacked, or partly), so the next try continues it.
        var latest = saved.Latest is { } l && Parse(l) > Current ? l : null;
        if (latest is not null) keep.Add(latest);
        if (latest is not null) keep.Add(latest + ".partial");
        // Downloads (whole or partial) of any other version are no longer wanted.
        if (Directory.Exists(UpdatesFolder))
            foreach (var file in Directory.GetFiles(UpdatesFolder, "CsiProMax-*").Concat(Directory.GetFiles(UpdatesFolder, "changed-*")))
            {
                var name = Path.GetFileName(file);
                if (latest is not null && name.StartsWith($"CsiProMax-{latest}.zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (latest is not null && name.StartsWith(DeltaUpdate.ChangedName(latest), StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(() => File.Delete(file));
            }

        foreach (var folder in Directory.GetDirectories(VersionsFolder))
        {
            var name = Path.GetFileName(folder);
            if (name.EndsWith(".del", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(() => Directory.Delete(folder, true));
                continue;
            }
            if (keep.Contains(name)) continue;
            if (IsUnder(running, folder)) continue;
            // Renaming fails while ETABS has a DLL in it loaded, so a folder in use is never half deleted.
            var doomed = folder + ".del";
            try
            {
                if (Directory.Exists(doomed)) Directory.Delete(doomed, true);
                Directory.Move(folder, doomed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            TryDelete(() => Directory.Delete(doomed, true));
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileNative(string path);

    /// <summary>
    /// Removes the "downloaded from the internet" mark (the Zone.Identifier alternate data stream) so Windows and
    /// .NET Framework (ETABS 2024) don't block the files. HttpClient and ZipFile don't normally add it, but security
    /// software or a proxy can. The Win32 call is used because .NET Framework rejects "file:stream" paths.
    /// </summary>
    internal static void ClearDownloadMark(string path)
    {
        try { DeleteFileNative(path + ":Zone.Identifier"); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static Version Parse(string text) => Normalize(Version.TryParse(text, out var v) ? v : null);

    /// <summary>
    /// Major.minor.build; a test build (tag v0.4.67-test5, built with assembly version 0.4.67.5) keeps its number as the
    /// fourth part, so test5 is newer than test4 and than the plain 0.4.67, and older than 0.4.68.
    /// </summary>
    private static Version Normalize(Version? v) => v is null ? new Version(0, 0, 0)
        : v.Revision > 0 ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), v.Revision)
        : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>A release tag as the version this updater compares and names its folder by: v0.4.66 → 0.4.66, v0.4.67-test5 → 0.4.67.5; null for any other tag.</summary>
    internal static string? TagVersion(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag ?? "", @"^[vV]?(\d+\.\d+\.\d+)(?:-test(\d+))?$");
        if (!m.Success) return null;
        return m.Groups[2].Success ? m.Groups[1].Value + "." + int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : m.Groups[1].Value;
    }

    /// <summary>
    /// Settings > Updates > "Get test builds" (AgentSettings.TestBuilds): this PC also takes the test builds (pre-releases
    /// tagged v0.4.67-testN) that the field test PC tries before a version is released to everyone. Off everywhere else.
    /// </summary>
    internal static bool TestBuilds()
    {
        try { return CsiProMax.Settings.AgentSettings.Load().TestBuilds; }
        catch (Exception) { return false; }
    }

    /// <summary>The newest release or test build, from the list of releases: for a PC with <see cref="TestBuilds"/> on.</summary>
    private static async Task<ReleaseSource.Answer<Release>> NewestWithTestBuildsAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return await LatestReleaseFromApiAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return ReleaseSource.Found(PickNewest(doc.RootElement));
    }

    /// <summary>Of a GitHub list of releases, the newest (by <see cref="TagVersion"/>) that is not a draft and has its package.</summary>
    internal static Release? PickNewest(JsonElement releases)
    {
        Release? best = null;
        if (releases.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (TagVersion(tag) is not { } version) continue;
            if (best is not null && Parse(version) <= Parse(best.Version)) continue;
            var name = tag.TrimStart('v', 'V');
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!(asset.GetProperty("name").GetString() ?? "").Equals($"CsiProMax-{name}.zip", StringComparison.OrdinalIgnoreCase)) continue;
                var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7) : null;
                var notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                best = new Release(version, asset.GetProperty("browser_download_url").GetString()!, sha, notes);
                break;
            }
        }
        return best;
    }

    private sealed class State
    {
        public DateTime? LastCheck { get; set; }
        public string? Staged { get; set; }
        public string? LastError { get; set; }
        public string? Latest { get; set; }
        public string? Notes { get; set; }
    }

    private static State LoadState()
    {
        try
        {
            if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    private static void SaveState(State state)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch (IOException) { }
    }
}
