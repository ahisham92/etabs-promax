using System.IO;
using System.Text.Json;

namespace CsiProMax.Tools;

/// <summary>
/// Where the team's learned tools are kept: a private GitHub repository with one file per tool, tools/&lt;name&gt;.json.
/// The real one is <see cref="GitHubLibrary"/>; the tests and the field test use one in memory.
/// </summary>
internal interface ILibraryRemote
{
    /// <summary>Every tool in the library: its name and the id of its file's content (GitHub's blob SHA).</summary>
    Task<Dictionary<string, string>> ListAsync(CancellationToken ct);
    Task<string> ReadAsync(string name, string sha, CancellationToken ct);
    /// <summary>Writes a tool; <paramref name="sha"/> is the content it replaces (null for a new one). Gives the new content's id.</summary>
    Task<string> WriteAsync(string name, string json, string? sha, string message, CancellationToken ct);
    Task DeleteAsync(string name, string sha, string message, CancellationToken ct);
}

/// <summary>The library refused a write because it changed meanwhile: the next round reads it again.</summary>
internal sealed class LibraryChangedException(string message) : Exception(message);

/// <summary>
/// One round of keeping this PC's learned tools and the team library the same. No ETABS and no network here: the tests
/// and the field test run it on folders and a library in memory.
///
/// What is remembered between rounds (the state file) is, per tool, the library's content id and this PC's hash of it
/// at the last round. With that each side tells "changed since" from "never had it", so:
/// <list type="bullet">
/// <item>a tool saved or fixed here is published; one saved or fixed by a teammate arrives;</item>
/// <item>deleted here (and unchanged in the library): taken out of the library; deleted from the library by a teammate
/// (and unchanged here): taken off this PC. A tool changed on the other side meanwhile is kept, never lost;</item>
/// <item>changed on both sides: the same code with different program lists is merged; otherwise the newer save wins and
/// the other is kept in the backups folder (and in the library's GitHub history), never lost;</item>
/// <item>a tool from the library that is not valid (unreadable, a wrong or built-in name, too long, or code that does not
/// compile here) is not taken, and is said in the report until it is fixed. A valid one still asks before its first run
/// on each PC (approvals are by the code's hash and never travel with it).</item>
/// </list>
/// The field test's own tools (<see cref="Local"/>) never travel.
/// </summary>
internal static class TeamSync
{
    /// <summary>The longest tool the library takes, in characters of its file.</summary>
    public const int MaxFile = 64 * 1024;

    public sealed class Report
    {
        public List<string> Published { get; } = [];
        public List<string> Received { get; } = [];
        public List<string> RemovedHere { get; } = [];
        public List<string> RemovedThere { get; } = [];
        public List<string> Conflicts { get; } = [];
        public Dictionary<string, string> Rejected { get; } = new(StringComparer.Ordinal);
        public List<string> Errors { get; } = [];
        public int InLibrary { get; set; }

        public bool Changed => Published.Count + Received.Count + RemovedHere.Count + RemovedThere.Count > 0;

        public string Summary()
        {
            var parts = new List<string>();
            if (Published.Count > 0) parts.Add($"published {string.Join(", ", Published)}");
            if (Received.Count > 0) parts.Add($"received {string.Join(", ", Received)}");
            if (RemovedHere.Count > 0) parts.Add($"removed here (taken out of the library) {string.Join(", ", RemovedHere)}");
            if (RemovedThere.Count > 0) parts.Add($"taken out of the library {string.Join(", ", RemovedThere)}");
            if (Conflicts.Count > 0) parts.Add($"changed on two PCs, the newer kept: {string.Join("; ", Conflicts)}");
            if (Rejected.Count > 0) parts.Add("not taken: " + string.Join("; ", Rejected.Select(r => $"{r.Key} ({r.Value})")));
            if (Errors.Count > 0) parts.Add("trouble: " + string.Join("; ", Errors));
            return parts.Count == 0 ? $"up to date, {InLibrary} tools in the library" : string.Join(". ", parts) + $". {InLibrary} tools in the library.";
        }
    }

    internal sealed class State
    {
        public string Repo { get; set; } = "";
        public Dictionary<string, Seen> Tools { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Library content ids not taken (name: id), so the same bad file is not read again every round.</summary>
        public Dictionary<string, string> Rejected { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Why each was not taken, for the report.</summary>
        public Dictionary<string, string> RejectedWhy { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class Seen
    {
        public string Remote { get; set; } = "";
        public string Local { get; set; } = "";
    }

    /// <summary>Tools that stay on this PC: the field test's own.</summary>
    public static bool Local(string name) => name.StartsWith("cpm_field_test", StringComparison.Ordinal);

    public static string HashOf(LearnedTool tool) => LearnedTools.Hash(tool.SharedJson());

    public static State ReadState(string file, string repo)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<State>(File.ReadAllText(file)) is { } s
                && string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase))
            {
                s.Tools = new Dictionary<string, Seen>(s.Tools ?? [], StringComparer.Ordinal);
                s.Rejected = new Dictionary<string, string>(s.Rejected ?? [], StringComparer.Ordinal);
                s.RejectedWhy = new Dictionary<string, string>(s.RejectedWhy ?? [], StringComparer.Ordinal);
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // Another library (or none yet): nothing is known, so nothing is taken as deleted.
        return new State { Repo = repo };
    }

    private static void WriteState(string file, State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }

    /// <param name="folder">This PC's learned-tools folder.</param>
    /// <param name="stateFile">What the last round saw (kept beside the folder, never in it).</param>
    /// <param name="backups">Where a tool that loses to a newer one is kept.</param>
    /// <param name="validate">Why a tool from the library is not taken, or null when it is fine (the name is free, the code compiles).</param>
    /// <param name="who">Who publishes, for the library's history.</param>
    public static async Task<Report> RunAsync(ILibraryRemote remote, string repo, string folder, string stateFile, string backups,
        Func<LearnedTool, string?> validate, string who, CancellationToken ct)
    {
        var report = new Report();
        var state = ReadState(stateFile, repo);
        var there = await remote.ListAsync(ct).ConfigureAwait(false);
        there = there.Where(p => !Local(p.Key) && LearnedTools.ValidName.IsMatch(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var here = new Dictionary<string, LearnedTool>(StringComparer.Ordinal);
        if (Directory.Exists(folder))
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (Local(name) || !LearnedTools.ValidName.IsMatch(name)) continue;
                try
                {
                    var tool = LearnedTool.FromJson(File.ReadAllText(file));
                    if (tool.Name == name) here[name] = tool;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                               or InvalidOperationException or FormatException) { /* not a tool: left alone */ }
            }

        foreach (var name in here.Keys.Union(there.Keys).Union(state.Tools.Keys).OrderBy(n => n, StringComparer.Ordinal).ToList())
        {
            ct.ThrowIfCancellationRequested();
            try { await OneAsync(name).ConfigureAwait(false); }
            catch (LibraryChangedException ex) { report.Errors.Add($"{name}: {ex.Message}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException
                                           or FormatException or KeyNotFoundException or InvalidOperationException)
            { report.Errors.Add($"{name}: {ex.Message}"); }
        }
        // Library files that went away: nothing more to say about them.
        foreach (var gone in state.Rejected.Keys.Where(k => !there.ContainsKey(k)).ToList()) { state.Rejected.Remove(gone); state.RejectedWhy.Remove(gone); }
        foreach (var (name, why) in state.RejectedWhy) report.Rejected[name] = why;
        report.InLibrary = there.Count;
        WriteState(stateFile, state);
        return report;

        async Task OneAsync(string name)
        {
            here.TryGetValue(name, out var mine);
            there.TryGetValue(name, out var theirSha);
            state.Tools.TryGetValue(name, out var seen);
            var myHash = mine is null ? null : HashOf(mine);
            var changedHere = mine is not null && (seen is null || seen.Local != myHash);
            var changedThere = theirSha is not null && (seen is null || seen.Remote != theirSha);

            if (mine is null && theirSha is null) { state.Tools.Remove(name); return; }

            if (mine is not null && theirSha is null)
            {
                if (seen is not null && !changedHere)
                {
                    // A teammate took it out of the library, and it was not changed here since: it goes from this PC too.
                    Backup(mine, "removed from the library");
                    File.Delete(LearnedTools.PathFor(folder, name));
                    state.Tools.Remove(name);
                    report.RemovedThere.Add(name);
                    return;
                }
                await PublishAsync(name, mine, myHash!, null).ConfigureAwait(false);
                return;
            }

            if (mine is null)
            {
                if (seen is not null && !changedThere)
                {
                    // Deleted here, and nobody changed it meanwhile: it leaves the library too.
                    await remote.DeleteAsync(name, theirSha!, $"{who}: removed {name}", ct).ConfigureAwait(false);
                    state.Tools.Remove(name);
                    report.RemovedHere.Add(name);
                    return;
                }
                await ReceiveAsync(name, theirSha!, null).ConfigureAwait(false);
                return;
            }

            if (!changedHere && !changedThere) return;
            if (changedHere && !changedThere) { await PublishAsync(name, mine, myHash!, theirSha).ConfigureAwait(false); return; }
            if (!changedHere) { await ReceiveAsync(name, theirSha!, mine).ConfigureAwait(false); return; }

            // Changed on both sides (or both had it before this PC ever synced).
            if (state.Rejected.TryGetValue(name, out var bad) && bad == theirSha) { await PublishAsync(name, mine, myHash!, theirSha).ConfigureAwait(false); return; }
            var theirs = Parse(await remote.ReadAsync(name, theirSha!, ct).ConfigureAwait(false), name);
            if (theirs is null || validate(theirs) is not null) { await PublishAsync(name, mine, myHash!, theirSha).ConfigureAwait(false); return; }
            if (HashOf(theirs) == myHash) { Remember(name, theirSha!, myHash!); return; }
            if (SameWork(theirs, mine))
            {
                // The same code; only the programs it has run in (or the wording) differ: both lists, published once.
                var merged = (theirs.Saved >= mine.Saved ? theirs : mine).WithPrograms(theirs.Programs.Union(mine.Programs)).WithUses(mine.Uses);
                File.WriteAllText(LearnedTools.PathFor(folder, name), merged.ToJson());
                await PublishAsync(name, merged, HashOf(merged), theirSha).ConfigureAwait(false);
                return;
            }
            var mineNewer = mine.Saved > theirs.Saved;
            report.Conflicts.Add(mineNewer ? $"{name}: this PC's newer version kept, {Who(theirs)}'s older one is in the backups"
                                           : $"{name}: {Who(theirs)}'s newer version taken, this PC's older one is in the backups");
            if (mineNewer)
            {
                Backup(theirs, "older than this PC's");
                await PublishAsync(name, mine, myHash!, theirSha).ConfigureAwait(false);
            }
            else
            {
                Backup(mine, "older than " + Who(theirs) + "'s");
                Take(name, theirs, theirSha!, mine);
            }
        }

        async Task PublishAsync(string name, LearnedTool tool, string hash, string? replacing)
        {
            var json = tool.SharedJson();
            if (json.Length > MaxFile) { report.Errors.Add($"{name}: too long to publish ({json.Length / 1024} KB; the library takes {MaxFile / 1024} KB)"); return; }
            var verb = replacing is null ? "added" : "updated";
            var sha = await remote.WriteAsync(name, json, replacing, $"{who}: {verb} {name}", ct).ConfigureAwait(false);
            Remember(name, sha, hash);
            state.Rejected.Remove(name);
            state.RejectedWhy.Remove(name);
            report.Published.Add(name);
        }

        async Task ReceiveAsync(string name, string sha, LearnedTool? mine)
        {
            if (state.Rejected.TryGetValue(name, out var bad) && bad == sha) return;
            var text = await remote.ReadAsync(name, sha, ct).ConfigureAwait(false);
            var theirs = text.Length > MaxFile ? null : Parse(text, name);
            var why = text.Length > MaxFile ? $"longer than {MaxFile / 1024} KB"
                : theirs is null ? "not a learned tool CSI ProMax can read, or saved under another name"
                : validate(theirs);
            if (why is not null)
            {
                state.Rejected[name] = sha;
                state.RejectedWhy[name] = why;
                return;
            }
            state.Rejected.Remove(name);
            state.RejectedWhy.Remove(name);
            Take(name, theirs!, sha, mine);
        }

        void Take(string name, LearnedTool theirs, string sha, LearnedTool? mine)
        {
            var kept = theirs.WithUses(mine?.Uses ?? 0);
            Directory.CreateDirectory(folder);
            File.WriteAllText(LearnedTools.PathFor(folder, name), kept.ToJson());
            Remember(name, sha, HashOf(kept));
            report.Received.Add(name);
        }

        void Remember(string name, string remoteSha, string localHash) => state.Tools[name] = new Seen { Remote = remoteSha, Local = localHash };

        void Backup(LearnedTool tool, string why)
        {
            try
            {
                Directory.CreateDirectory(backups);
                var file = Path.Combine(backups, $"{tool.Name} {DateTime.Now:yyyy-MM-dd HHmmss}.json");
                File.WriteAllText(file, tool.ToJson());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report.Errors.Add($"{tool.Name}: could not keep a backup ({why}): {ex.Message}"); }
        }
    }

    private static string Who(LearnedTool tool) => tool.Author.Length > 0 ? tool.Author : "a teammate";

    /// <summary>The same tool apart from the programs it ran in, its author or the time it was saved.</summary>
    private static bool SameWork(LearnedTool a, LearnedTool b) =>
        a.Code == b.Code && a.Summary == b.Summary && Norm(a.PropertiesJson) == Norm(b.PropertiesJson) && a.ModifiesModel == b.ModifiesModel
        && a.Required.SequenceEqual(b.Required);

    private static string Norm(string json)
    {
        try { using var d = JsonDocument.Parse(json); return JsonSerializer.Serialize(d.RootElement); }
        catch (JsonException) { return json; }
    }

    private static LearnedTool? Parse(string json, string name)
    {
        try
        {
            var tool = LearnedTool.FromJson(json);
            return tool.Name == name ? tool : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>
/// The sync's rules, checked on two made-up PCs and a library in memory, in <paramref name="root"/>: the unit tests and
/// the field test (on every PC that runs it) use this same check. Gives what it checked; throws saying what is wrong.
/// Never touches this PC's own learned tools or the team's real library.
/// </summary>
internal static class TeamSyncCheck
{
    public static string Run(string root, Func<LearnedTool, string?> validate)
    {
        var library = new MemoryLibrary();
        var a = new Pc(Path.Combine(root, "pc-a"), "member_a", library, validate);
        var b = new Pc(Path.Combine(root, "pc-b"), "member_b", library, validate);
        var t0 = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        void Expect(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }

        // 1. Saved on A: published; arrives on B; a second round changes nothing (the same tool gives the same text).
        a.Save(Tool("count_team_frames", "int n = 0; string[] names = null; Ok(sap.FrameObj.GetNameList(ref n, ref names), \"list the frames\"); return n;", "etabs", "member_a", t0));
        var r = a.Sync();
        Expect(r.Published.SequenceEqual(["count_team_frames"]) && library.Files.ContainsKey("count_team_frames"), "a tool saved on one PC was not published: " + r.Summary());
        r = b.Sync();
        Expect(r.Received.SequenceEqual(["count_team_frames"]) && b.Has("count_team_frames"), "a teammate's tool did not arrive: " + r.Summary());
        Expect(b.Get("count_team_frames")!.Author == "member_a", "the tool that arrived does not say who made it");
        r = b.Sync();
        var again = a.Sync();
        Expect(!r.Changed && !again.Changed, $"a second round changed something although nothing did: {r.Summary()} / {again.Summary()}");

        // 2. This PC's use count stays here: using a tool publishes nothing.
        b.Save(b.Get("count_team_frames")!.WithUses(7));
        Expect(!b.Sync().Changed, "a use count was published");

        // 3. Fixed on B: published, and A gets the fix.
        b.Save(Tool("count_team_frames", "int n = 0; string[] names = null; Ok(sap.FrameObj.GetNameList(ref n, ref names), \"list the frames\"); return new { frames = n };", "etabs", "member_b", t0.AddMinutes(5)));
        Expect(b.Sync().Published.Contains("count_team_frames"), "a fix was not published");
        r = a.Sync();
        Expect(r.Received.Contains("count_team_frames") && a.Get("count_team_frames")!.Code.Contains("frames = n", StringComparison.Ordinal), "a teammate's fix did not arrive: " + r.Summary());

        // 4. Changed on both PCs: the newer save wins on both, and the older is kept in the backups.
        a.Save(Tool("count_team_frames", "return \"change by A\";", "etabs", "member_a", t0.AddMinutes(10)));
        b.Save(Tool("count_team_frames", "return \"change by B\";", "etabs", "member_b", t0.AddMinutes(20)));
        a.Sync();
        r = b.Sync();
        Expect(r.Conflicts.Count == 1 && r.Published.Contains("count_team_frames"), "a change made on two PCs was not settled for the newer one: " + r.Summary());
        Expect(Directory.Exists(b.Backups) && Directory.GetFiles(b.Backups).Any(f => File.ReadAllText(f).Contains("change by A", StringComparison.Ordinal)), "the older change was not kept in the backups");
        a.Sync();
        Expect(a.Get("count_team_frames")!.Code.Contains("change by B", StringComparison.Ordinal), "the PC with the older change did not take the newer one");

        // 5. The same code run in two more programs on two PCs: both lists kept, so the team sees where it works.
        a.Save(a.Get("count_team_frames")!.WithPrograms(["etabs", "sap2000"]));
        b.Save(b.Get("count_team_frames")!.WithPrograms(["etabs", "safe"]));
        a.Sync();
        b.Sync();
        a.Sync();
        foreach (var pc in new[] { a, b })
            Expect(pc.Get("count_team_frames")!.Programs.SequenceEqual(["etabs", "safe", "sap2000"]), $"the programs it ran in were not merged on {pc.Who}: {string.Join(", ", pc.Get("count_team_frames")!.Programs)}");

        // 6. Bad code in the library is not taken, says why, and is not read again until it changes.
        library.Files["broken_team_tool"] = Tool("broken_team_tool", "return thisNameIsNotDefined;", "etabs", "member_b", t0).SharedJson();
        library.Files["garbled_team_tool"] = "{ not a tool";
        r = a.Sync();
        Expect(!a.Has("broken_team_tool") && r.Rejected.ContainsKey("broken_team_tool"), "code that does not compile was taken from the library: " + r.Summary());
        Expect(!a.Has("garbled_team_tool") && r.Rejected.ContainsKey("garbled_team_tool"), "an unreadable library file was taken: " + r.Summary());
        Expect(a.Sync().Rejected.ContainsKey("broken_team_tool"), "a tool not taken is no longer said in the next round");

        // 7. The field test's own tools never travel.
        a.Save(Tool("cpm_field_test_tool", "return 42;", "etabs", "member_a", t0));
        a.Sync();
        Expect(!library.Files.ContainsKey("cpm_field_test_tool"), "the field test's own tool was published");

        // 8. Deleted on A: out of the library, then off B (kept in B's backups).
        File.Delete(LearnedTools.PathFor(a.Folder, "count_team_frames"));
        r = a.Sync();
        Expect(r.RemovedHere.Contains("count_team_frames") && !library.Files.ContainsKey("count_team_frames"), "a tool deleted on one PC stayed in the library: " + r.Summary());
        r = b.Sync();
        Expect(r.RemovedThere.Contains("count_team_frames") && !b.Has("count_team_frames"), "a tool taken out of the library stayed on a teammate's PC: " + r.Summary());

        // 9. A PC that never synced and has no tools takes nothing as deleted.
        var c = new Pc(Path.Combine(root, "pc-c"), "member_c", library, validate);
        library.Files["fresh_team_tool"] = Tool("fresh_team_tool", "return 1;", "sap2000", "member_b", t0).SharedJson();
        r = c.Sync();
        Expect(r.Received.Contains("fresh_team_tool") && library.Files.ContainsKey("fresh_team_tool"), "a new PC did not get the library's tools: " + r.Summary());

        return $"9 rules held on two made-up PCs and a library in memory ({library.History.Count} library changes): publish, receive, " +
               "fix, both-changed (newer kept, older backed up), programs merged, bad code refused, the test's own tools kept here, delete both ways, a new PC";
    }

    private static LearnedTool Tool(string name, string code, string program, string author, DateTime saved) =>
        LearnedTool.Create(name, "The team library's own check. Safe to delete.", code, "{}", [], false, 0, null, author, [program]).WithSaved(saved);

    private sealed class Pc(string root, string who, MemoryLibrary library, Func<LearnedTool, string?> validate)
    {
        public string Who => who;
        public string Folder { get; } = Path.Combine(root, "learned-tools");
        public string Backups { get; } = Path.Combine(root, "backups");
        private string State => Path.Combine(root, "state.json");

        public void Save(LearnedTool tool)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(LearnedTools.PathFor(Folder, tool.Name), tool.ToJson());
        }

        public bool Has(string name) => File.Exists(LearnedTools.PathFor(Folder, name));
        public LearnedTool? Get(string name) => Has(name) ? LearnedTool.FromJson(File.ReadAllText(LearnedTools.PathFor(Folder, name))) : null;

        public TeamSync.Report Sync() =>
            TeamSync.RunAsync(library, "team/library", Folder, State, Backups, validate, who, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>A library in memory: for the tests and the field test, which must never write to the team's real library.</summary>
internal sealed class MemoryLibrary : ILibraryRemote
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public List<string> History { get; } = [];

    public static string ShaOf(string text) => LearnedTools.Hash(text)[..40];

    public Task<Dictionary<string, string>> ListAsync(CancellationToken ct) =>
        Task.FromResult(Files.ToDictionary(f => f.Key, f => ShaOf(f.Value), StringComparer.Ordinal));

    public Task<string> ReadAsync(string name, string sha, CancellationToken ct) =>
        Files.TryGetValue(name, out var text) && ShaOf(text) == sha ? Task.FromResult(text) : throw new LibraryChangedException("it changed meanwhile");

    public Task<string> WriteAsync(string name, string json, string? sha, string message, CancellationToken ct)
    {
        Files.TryGetValue(name, out var now);
        if ((now is null) != (sha is null) || (now is not null && ShaOf(now) != sha)) throw new LibraryChangedException("it changed meanwhile");
        Files[name] = json;
        History.Add(message);
        return Task.FromResult(ShaOf(json));
    }

    public Task DeleteAsync(string name, string sha, string message, CancellationToken ct)
    {
        if (!Files.TryGetValue(name, out var now) || ShaOf(now) != sha) throw new LibraryChangedException("it changed meanwhile");
        Files.Remove(name);
        History.Add(message);
        return Task.CompletedTask;
    }
}
