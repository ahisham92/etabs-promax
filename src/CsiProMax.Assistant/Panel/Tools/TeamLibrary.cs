using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CsiProMax.Tools;

/// <summary>
/// The team's shared learned tools (Ahmed, 25 Nov 2026: "I want things of my library to be published to github so
/// other users can take advantage of that ... it will be used by 3 members", publishing and getting them "through the
/// app very smoothly ... every hour or 2"). Sketch ProMax keeps its learned tools on one PC; CSI ProMax shares them.
/// <list type="bullet">
/// <item><b>Where:</b> a private repository of its own, <see cref="DefaultRepo"/>, one file per tool (tools/&lt;name&gt;.json).
/// Not the CSI ProMax repository, which is public: the team's code stays private.</item>
/// <item><b>Set up by itself:</b> the owner's CSI ProMax creates the library the first time it finds none and invites
/// everyone who can write to the CSI ProMax repository; a member's CSI ProMax accepts that invitation by itself.</item>
/// <item><b>Sign-in, with no program of ours:</b> the GitHub sign-in already on the PC: GitHub CLI's (gh auth token) or
/// Git's credential manager (git credential fill, which Git for Windows and so Claude Code have), else a token in
/// CSIPROMAX_GITHUB_TOKEN. Nothing is ever shipped as an .exe.</item>
/// <item><b>When:</b> a minute after the window opens, every hour after, and a few seconds after a tool is saved,
/// fixed or deleted. One PC syncs one window at a time (a lock file); a field test is never interrupted by it.</item>
/// <item><b>Conflicts and bad code:</b> <see cref="TeamSync"/>.</item>
/// </list>
/// Settings (all optional) in %APPDATA%\CsiProMax\team-library.json: {"enabled": true, "repo": "owner/name"}.
/// </summary>
internal static class TeamLibrary
{
    public const string DefaultRepo = "ahisham92/csi-promax-library";

    /// <summary>Whose repository's writers are the team: everyone who can write to CSI ProMax's own repository.</summary>
    public const string TeamRepo = "ahisham92/etabs-promax";

    public static readonly TimeSpan Every = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan SoonAfter = TimeSpan.FromSeconds(8);

    private static string Roaming => Path.GetDirectoryName(LearnedTools.Folder)!;
    public static string SettingsFile => Path.Combine(Roaming, "team-library.json");
    public static string StateFile => Path.Combine(Roaming, "team-library-state.json");
    public static string Backups => Path.Combine(Roaming, "team-library-backups");
    private static string LockFile => Path.Combine(Roaming, "team-library.lock");

    private sealed class Settings
    {
        public bool Enabled { get; set; } = true;
        public string Repo { get; set; } = DefaultRepo;
    }

    private static Settings Read()
    {
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } s)
            {
                if (string.IsNullOrWhiteSpace(s.Repo) || s.Repo.Count(c => c == '/') != 1) s.Repo = DefaultRepo;
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new Settings();
    }

    public static bool Enabled => Read().Enabled;
    public static string Repo => Read().Repo.Trim();

    /// <summary>Whether this tool travels to the team (the library is on, and it is not one of the field test's own).</summary>
    public static bool Shares(string name) => Enabled && !TeamSync.Local(name);

    // ---- What the last round said, for the tool and the field test ---------------------------------------------------

    private static readonly object Gate = new();
    private static string _login = "";
    private static string _last = "not synced yet in this window";
    private static DateTime? _lastAt;
    private static bool _lastOk;
    private static string _signIn = "";

    /// <summary>The GitHub login this PC syncs as ("" before the first round), written into the tools saved here.</summary>
    public static string Login
    {
        get
        {
            lock (Gate) if (_login.Length > 0) return _login;
            try { return File.Exists(LoginFile) ? File.ReadAllText(LoginFile).Trim() : ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        }
    }

    private static string LoginFile => Path.Combine(Roaming, "team-library-login.txt");

    public static string StatusLine()
    {
        lock (Gate)
        {
            if (!Enabled) return "The team library is switched off in team-library.json.";
            var when = _lastAt is { } at ? $" ({at:HH:mm})" : "";
            return $"Team library {Repo}{(Login.Length > 0 ? ", signed in as " + Login : "")}: {_last}{when}. Synced every hour.";
        }
    }

    public static object Status() => new
    {
        library = Repo,
        enabled = Enabled,
        signedInAs = Login.Length > 0 ? Login : null,
        signIn = _signIn.Length > 0 ? _signIn : null,
        lastSync = _lastAt,
        lastSyncWorked = _lastAt is null ? (bool?)null : _lastOk,
        last = _last,
        every = "hour, and a few seconds after a learned tool is saved, fixed or deleted",
        backups = Backups,
    };

    private static void Said(bool ok, string what)
    {
        lock (Gate) { _lastOk = ok; _last = what; _lastAt = DateTime.Now; }
        try { ActivityLog.Note("team_library", what); } catch (Exception) { }
    }

    // ---- When -------------------------------------------------------------------------------------------------------

    private static int _started;
    private static CancellationToken _stop;
    private static readonly SemaphoreSlim One = new(1, 1);
    private static CancellationTokenSource? _soon;

    /// <summary>Starts the rounds: a minute after the window opens, then every hour. Called once per window.</summary>
    public static void Start(CancellationToken stop)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _stop = stop;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstAfter, stop).ConfigureAwait(false);
                while (!stop.IsCancellationRequested)
                {
                    await SyncAsync(interactive: false, stop).ConfigureAwait(false);
                    await Task.Delay(Every, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>A tool was saved, fixed or deleted: a round a few seconds later (several saves in a row make one round).</summary>
    public static void Soon()
    {
        if (!Enabled || Volatile.Read(ref _started) == 0) return;
        var next = new CancellationTokenSource();
        Interlocked.Exchange(ref _soon, next)?.Cancel();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(next.Token, _stop).Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SoonAfter, stop).ConfigureAwait(false);
                await SyncAsync(interactive: false, _stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>One round now. Never throws: what happened is in the answer and in <see cref="StatusLine"/>.</summary>
    public static async Task<(bool Ok, string Said, TeamSync.Report? Report)> SyncAsync(bool interactive, CancellationToken ct)
    {
        if (!Enabled) return (false, "the team library is switched off in " + SettingsFile, null);
        if (CsiProMax.Assistant.FieldTest.IsRunning && !interactive) return (true, "left for after the field test", null);
        if (!await One.WaitAsync(interactive ? TimeSpan.FromMinutes(2) : TimeSpan.Zero, ct).ConfigureAwait(false))
            return (true, "a round is already running", null);
        FileStream? held = null;
        try
        {
            // Another CSI ProMax window on this PC (ETABS and SAP2000 open together) shares the folder: one round at a time.
            try
            {
                Directory.CreateDirectory(Roaming);
                held = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { return (true, "another CSI ProMax window on this PC is syncing", null); }

            var repo = Repo;
            var (token, from) = await GitHubSignIn.TokenAsync(interactive, ct).ConfigureAwait(false);
            lock (Gate) _signIn = from;
            if (token is null)
            {
                var why = "no GitHub sign-in on this PC. " + GitHubSignIn.HowToSignIn;
                Said(false, why);
                return (false, why, null);
            }
            using var github = new GitHubLibrary(repo, token);
            var login = await github.LoginAsync(ct).ConfigureAwait(false);
            lock (Gate) _login = login;
            try { File.WriteAllText(LoginFile, login); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var ready = await github.EnsureAsync(login, ct).ConfigureAwait(false);
            if (ready is not null) { Said(false, ready); return (false, ready, null); }
            var registry = BuiltInNames.Value;
            var report = await TeamSync.RunAsync(github, repo, LearnedTools.Folder, StateFile, Backups,
                tool => Check(tool, registry), login, ct).ConfigureAwait(false);
            var text = report.Summary();
            Said(report.Errors.Count == 0, text);
            return (report.Errors.Count == 0, text, report);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException)
        {
            var why = ex is OperationCanceledException ? "GitHub did not answer in time" : ex.Message;
            Said(false, why);
            return (false, why, null);
        }
        finally
        {
            held?.Dispose();
            One.Release();
        }
    }

    private static readonly Lazy<HashSet<string>> BuiltInNames = new(() =>
    {
        try { return ToolRegistry.CreateDefault().All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal); }
        catch (Exception) { return []; }
    });

    /// <summary>Why a tool from the library is not taken here, or null: its name is free and its code compiles here.</summary>
    internal static string? Check(LearnedTool tool, IReadOnlySet<string> builtIn)
    {
        if (builtIn.Contains(tool.Name)) return "it has the name of a built-in tool";
        if (tool.Code.Length > WorkedCode.MaxCode) return $"its code is longer than {WorkedCode.MaxCode} characters";
        var (run, errors) = ScriptCompiler.Compile(tool.Code);
        if (run is null) return "its code does not compile here: " + (errors.Length > 200 ? errors[..200] + "..." : errors);
        try { using var _ = JsonDocument.Parse(tool.PropertiesJson); }
        catch (JsonException) { return "its inputs are not valid JSON"; }
        return null;
    }

    /// <summary>
    /// The field test's check against the real library: a file is written in field-test/ (never tools/, so no PC takes
    /// it as a tool), read back and taken away again. Throws <see cref="CsiProMax.Assistant.FieldSkipException"/> when
    /// this PC has no GitHub sign-in, or the library is not made yet and this PC can't make it.
    /// </summary>
    internal static async Task<string> RoundTripAsync(CancellationToken ct)
    {
        if (!Enabled) throw new CsiProMax.Assistant.FieldSkipException("the team library is switched off in " + SettingsFile);
        var (token, from) = await GitHubSignIn.TokenAsync(false, ct).ConfigureAwait(false);
        if (token is null) throw new CsiProMax.Assistant.FieldSkipException("there is no GitHub sign-in on this PC, so the team library can't be reached. " + GitHubSignIn.HowToSignIn);
        using var github = new GitHubLibrary(Repo, token);
        var login = await github.LoginAsync(ct).ConfigureAwait(false);
        var ready = await github.EnsureAsync(login, ct).ConfigureAwait(false);
        if (ready is not null) throw new CsiProMax.Assistant.FieldSkipException(ready);
        var path = $"field-test/{login}-{Environment.MachineName.ToLowerInvariant()}.txt";
        var text = $"CPM field test, {DateTime.UtcNow:O}. Safe to delete.";
        var old = await github.ShaOfAsync(path, ct).ConfigureAwait(false);
        var sha = await github.PutAsync(path, text, old, "CPM field test: write", ct).ConfigureAwait(false);
        var back = await github.BlobAsync(sha, ct).ConfigureAwait(false);
        if (back != text) throw new CsiProMax.Assistant.FieldCheckException("what was read back from the library is not what was written");
        await github.RemoveAsync(path, sha, "CPM field test: remove", ct).ConfigureAwait(false);
        var tools = (await github.ListAsync(ct).ConfigureAwait(false)).Count;
        return $"{Repo}, signed in as {login} ({from}): wrote, read back and removed a test file; {tools} tools in the library";
    }
}

/// <summary>
/// The GitHub sign-in already on this PC, so CSI ProMax needs no sign-in of its own and ships no program. In order: a
/// token in the environment, GitHub CLI's, Git's credential manager's (asked without any window, except when the user
/// asks to sign in, when Git's own GitHub sign-in window may open once).
/// </summary>
internal static class GitHubSignIn
{
    public const string HowToSignIn =
        "Ask the chat \"sign in to the team library\" (Git's own GitHub sign-in opens once), or sign in with GitHub CLI (gh auth login).";

    private static string? _cached;
    private static string _cachedFrom = "";

    public static async Task<(string? Token, string From)> TokenAsync(bool interactive, CancellationToken ct)
    {
        if (!interactive && _cached is not null) return (_cached, _cachedFrom);
        foreach (var name in new[] { "CSIPROMAX_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } t) return Keep(t.Trim(), name);
        if (await RunAsync("gh", "auth token --hostname github.com", null, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) is { Length: > 0 } gh
            && !gh.Contains(' ')) return Keep(gh.Trim(), "GitHub CLI");
        // In the background nothing may pop up: no credential manager window, no password box (an empty GIT_ASKPASS
        // and SSH_ASKPASS make Git skip its own), no terminal prompt.
        var env = interactive ? null : new Dictionary<string, string>
        {
            ["GCM_INTERACTIVE"] = "never", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_ASKPASS"] = "", ["SSH_ASKPASS"] = "",
        };
        var answer = await RunAsync(Git(), "credential fill", "protocol=https\nhost=github.com\n\n", env,
            interactive ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var password = answer?.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..];
        if (password is { Length: > 0 })
        {
            // Told to Git as working, so the credential manager keeps a sign-in it just made.
            if (interactive) _ = await RunAsync(Git(), "credential approve", answer!.Trim() + "\n\n", null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return Keep(password, "Git's credential manager");
        }
        return (null, "");
    }

    /// <summary>Git: on the PATH, else where Git for Windows (which Claude Code needs) puts it.</summary>
    private static string Git()
    {
        if (!OperatingSystem.IsWindows()) return "git";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            try { if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim('"'), "git.exe"))) return Path.Combine(dir.Trim('"'), "git.exe"); }
            catch (ArgumentException) { }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            var at = Path.Combine(Environment.GetFolderPath(root), root == Environment.SpecialFolder.ProgramFiles ? "Git" : @"Programs\Git", "cmd", "git.exe");
            if (File.Exists(at)) return at;
        }
        return "git";
    }

    /// <summary>GitHub refused the token: it is asked for again next time.</summary>
    public static void Forget() => _cached = null;

    private static (string?, string) Keep(string token, string from)
    {
        (_cached, _cachedFrom) = (token, from);
        return (token, from);
    }

    /// <summary>A program's output, or null when it is not on this PC, fails or takes too long. No window is shown.</summary>
    private static async Task<string?> RunAsync(string program, string arguments, string? input, IDictionary<string, string>? env, TimeSpan limit, CancellationToken ct)
    {
        var info = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        if (env is not null) foreach (var (k, v) in env) info.Environment[k] = v;
        try
        {
            using var child = Chat.ChildProcess.Start(info);
            if (input is not null && child.StandardInput is { } stdin) { await stdin.WriteAsync(input).ConfigureAwait(false); stdin.Close(); }
            var output = child.StandardOutput!.ReadToEndAsync(ct);
            _ = child.StandardError!.ReadToEndAsync(ct);
            using var limitCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limitCt.CancelAfter(limit);
            try { await child.Process.WaitForExitAsync(limitCt.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { child.Process.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            return child.Process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException) { return null; }
    }
}

/// <summary>The team library on GitHub, through GitHub's REST API with the PC's own sign-in.</summary>
internal sealed class GitHubLibrary : ILibraryRemote, IDisposable
{
    private const string Api = "https://api.github.com";
    private const string Branch = "main";
    private readonly HttpClient _http;
    private readonly string _repo;

    public GitHubLibrary(string repo, string token)
    {
        _repo = repo;
        var handler = new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CsiProMax-TeamLibrary");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose() => _http.Dispose();

    private string Owner => _repo.Split('/')[0];
    private string Name => _repo.Split('/')[1];

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            GitHubSignIn.Forget();
            throw new InvalidOperationException("GitHub refused this PC's sign-in (it may have expired). " + GitHubSignIn.HowToSignIn);
        }
        JsonElement parsed = default;
        if (text.Length > 0)
            try { using var d = JsonDocument.Parse(text); parsed = d.RootElement.Clone(); }
            catch (JsonException) { }
        return (response.StatusCode, parsed);
    }

    private static string Message(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

    private static Exception Failed(string what, HttpStatusCode status, JsonElement body) =>
        new InvalidOperationException($"GitHub answered {(int)status} when {what}{(Message(body) is { Length: > 0 } m ? ": " + m : "")}.");

    public async Task<string> LoginAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/user", null, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) throw Failed("asking who is signed in", status, body);
        return body.GetProperty("login").GetString() ?? "";
    }

    /// <summary>
    /// Makes sure the library is there and this PC can reach it: the owner's PC creates it (private) and invites the
    /// team; a member's PC accepts its invitation. Null when ready, else what it waits for, in the user's words.
    /// </summary>
    public async Task<string?> EnsureAsync(string login, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/repos/{_repo}", null, ct).ConfigureAwait(false);
        var mine = string.Equals(login, Owner, StringComparison.OrdinalIgnoreCase);
        if (status == HttpStatusCode.NotFound && !mine && await AcceptInvitationAsync(ct).ConfigureAwait(false))
            (status, body) = await SendAsync(HttpMethod.Get, $"/repos/{_repo}", null, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound && mine)
        {
            var (made, why) = await SendAsync(HttpMethod.Post, "/user/repos", new
            {
                name = Name,
                @private = true,
                auto_init = true,
                description = "CSI ProMax team library: learned tools shared by the team, kept in sync by CSI ProMax. One file per tool in tools/.",
            }, ct).ConfigureAwait(false);
            if (made is not (HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity)) throw Failed("creating the team library", made, why);
            (status, body) = await SendAsync(HttpMethod.Get, $"/repos/{_repo}", null, ct).ConfigureAwait(false);
        }
        if (status == HttpStatusCode.NotFound)
            return mine ? $"the team library {_repo} could not be made"
                : $"waiting for {Owner} to open CSI ProMax once: it makes the team library {_repo} and invites you, and this PC then accepts by itself";
        if (status != HttpStatusCode.OK) throw Failed("reading the team library", status, body);
        if (mine) await InviteTeamAsync(ct).ConfigureAwait(false);
        return null;
    }

    private async Task<bool> AcceptInvitationAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/user/repository_invitations?per_page=100", null, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || body.ValueKind != JsonValueKind.Array) return false;
        foreach (var invite in body.EnumerateArray())
            if (string.Equals(invite.GetProperty("repository").GetProperty("full_name").GetString(), _repo, StringComparison.OrdinalIgnoreCase))
            {
                var (accepted, _) = await SendAsync(HttpMethod.Patch, $"/user/repository_invitations/{invite.GetProperty("id").GetInt64()}", null, ct).ConfigureAwait(false);
                return accepted == HttpStatusCode.NoContent;
            }
        return false;
    }

    /// <summary>Everyone who can write to CSI ProMax's repository can write to the library: invited when not yet.</summary>
    private async Task InviteTeamAsync(CancellationToken ct)
    {
        var team = await LoginsAsync($"/repos/{TeamLibrary.TeamRepo}/collaborators?per_page=100", "login", ct).ConfigureAwait(false);
        if (team.Count == 0) return;
        var have = await LoginsAsync($"/repos/{_repo}/collaborators?per_page=100", "login", ct).ConfigureAwait(false);
        var asked = await LoginsAsync($"/repos/{_repo}/invitations?per_page=100", "invitee", ct).ConfigureAwait(false);
        foreach (var login in team.Where(l => !have.Contains(l) && !asked.Contains(l)))
            await SendAsync(HttpMethod.Put, $"/repos/{_repo}/collaborators/{login}", new { permission = "push" }, ct).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> LoginsAsync(string path, string field, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (status != HttpStatusCode.OK || body.ValueKind != JsonValueKind.Array) return set;
        foreach (var item in body.EnumerateArray())
        {
            var who = field == "login" ? item : item.TryGetProperty(field, out var i) ? i : default;
            if (who.ValueKind == JsonValueKind.Object && who.TryGetProperty("login", out var l) && l.GetString() is { } s) set.Add(s);
        }
        return set;
    }

    public async Task<Dictionary<string, string>> ListAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/repos/{_repo}/git/trees/{Branch}?recursive=1", null, ct).ConfigureAwait(false);
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return tools; // empty
        if (status != HttpStatusCode.OK) throw Failed("listing the team library", status, body);
        foreach (var entry in body.GetProperty("tree").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString() ?? "";
            if (entry.GetProperty("type").GetString() == "blob" && path.StartsWith("tools/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)
                && path.IndexOf('/', 6) < 0)
                tools[path[6..^5]] = entry.GetProperty("sha").GetString() ?? "";
        }
        return tools;
    }

    public Task<string> ReadAsync(string name, string sha, CancellationToken ct) => BlobAsync(sha, ct);

    public async Task<string> BlobAsync(string sha, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/repos/{_repo}/git/blobs/{sha}", null, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) throw Failed("reading from the team library", status, body);
        return Encoding.UTF8.GetString(Convert.FromBase64String((body.GetProperty("content").GetString() ?? "").Replace("\n", "")));
    }

    public Task<string> WriteAsync(string name, string json, string? sha, string message, CancellationToken ct) =>
        PutAsync($"tools/{name}.json", json, sha, message, ct);

    public Task DeleteAsync(string name, string sha, string message, CancellationToken ct) =>
        RemoveAsync($"tools/{name}.json", sha, message, ct);

    public async Task<string?> ShaOfAsync(string path, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/repos/{_repo}/contents/{path}?ref={Branch}", null, ct).ConfigureAwait(false);
        return status == HttpStatusCode.OK ? body.GetProperty("sha").GetString() : null;
    }

    public async Task<string> PutAsync(string path, string text, string? sha, string message, CancellationToken ct)
    {
        var payload = new Dictionary<string, string> { ["message"] = message, ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), ["branch"] = Branch };
        if (sha is not null) payload["sha"] = sha;
        var (status, body) = await SendAsync(HttpMethod.Put, $"/repos/{_repo}/contents/{path}", payload, ct).ConfigureAwait(false);
        if (status is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity) throw new LibraryChangedException("changed in the library meanwhile; tried again at the next sync");
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created)) throw Failed("writing to the team library", status, body);
        return body.GetProperty("content").GetProperty("sha").GetString() ?? "";
    }

    public async Task RemoveAsync(string path, string sha, string message, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Delete, $"/repos/{_repo}/contents/{path}", new { message, sha, branch = Branch }, ct).ConfigureAwait(false);
        if (status is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity) throw new LibraryChangedException("changed in the library meanwhile; tried again at the next sync");
        if (status is not (HttpStatusCode.OK or HttpStatusCode.NotFound)) throw Failed("removing from the team library", status, body);
    }
}

/// <summary>The chat's way to the team library: how it stands, a sync now, and the one-time sign-in.</summary>
public sealed class TeamLibraryTool : IEtabsTool
{
    public string Name => "team_library";
    public string Description =>
        "The team library: learned tools are shared with the team through a private GitHub repository and kept in sync by " +
        "themselves (every hour, and seconds after a tool is saved, fixed or deleted). action 'status' (default): who this " +
        "PC is signed in as, the last sync and what it did, tools not taken and why. 'sync': sync now (when the user asks " +
        "for the team's latest tools, or to publish one at once). 'sign_in': when the status says there is no GitHub " +
        "sign-in: opens Git's own GitHub sign-in once, then syncs.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["status", "sync", "sign_in"]}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var action = args.OptString("action") ?? "status";
        if (action == "status") return ToolResult.Ok(new { status = TeamLibrary.Status(), line = TeamLibrary.StatusLine() });
        if (action is not ("sync" or "sign_in")) return ToolResult.Error("action is 'status', 'sync' or 'sign_in'.");
        if (action == "sign_in") GitHubSignIn.Forget();
        // This runs on the program's own thread: it waits a little for the round, never minutes (a sign-in in the
        // browser takes as long as the user takes), and the round goes on in the background.
        var round = Task.Run(async () =>
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(action == "sign_in" ? 5 : 2));
            return await TeamLibrary.SyncAsync(interactive: true, limit.Token).ConfigureAwait(false);
        });
        if (!round.Wait(action == "sign_in" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(25)))
            return ToolResult.Ok(new
            {
                running = true,
                said = action == "sign_in"
                    ? "Git's GitHub sign-in is open (it may be behind this window). Once the user has signed in there, the sync runs by itself."
                    : "Still syncing in the background.",
                note = "Call team_library with action 'status' in a minute to see what it did.",
            });
        var (ok, said, report) = round.Result;
        var result = new
        {
            worked = ok,
            said,
            published = report?.Published,
            received = report?.Received,
            notTaken = report?.Rejected,
            status = TeamLibrary.Status(),
            note = report is { Received.Count: > 0 } ? "Received tools are in your tool list from the next message; call them now with run_learned_tool." : null,
        };
        return ok ? ToolResult.Ok(result) : ToolResult.Error(JsonSerializer.Serialize(result));
    }
}
