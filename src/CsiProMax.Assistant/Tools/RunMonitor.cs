using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// What CSI ProMax Engine's Run Monitor shows (Ahmed 2026-11-27: "when we run we need to see a progress or something
/// live ... so the user feels okay", and "if the model is having an issue in ETABS ... we are stuck with no way saying
/// what is happening"). Like ETABS's Analysis Monitor: the steps of the job, each with its time; the program's own
/// analysis log as it writes it; the load case it is on; when the program was last heard from; a box it is waiting on.
/// When the job ends badly or with warnings, a Run Report says which step, the program's own words, and what to do.
/// The same for ETABS, SAP2000 and SAFE. Thread-safe: the run writes it, the window reads it.
/// </summary>
public sealed class RunMonitor
{
    /// <summary>The steps of a job, in order, by key and in the words the monitor shows.</summary>
    public static readonly (string Key, string Name)[] Steps =
    [
        ("check", "Check the model for instabilities"),
        ("start", "Start {0} hidden and open the model"),
        ("analysis", "Run the analysis"),
        ("design", "Run the design"),
        ("results", "Read the results and design"),
        ("close", "Close {0} (free its licence)"),
    ];

    public sealed class Step
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        /// <summary>waiting, running, done, warning, failed, skipped.</summary>
        public string State { get; set; } = "waiting";
        public DateTime? Began { get; set; }
        public DateTime? Ended { get; set; }
        public string Detail { get; set; } = "";
    }

    private readonly object _gate = new();
    private readonly List<Step> _steps;
    private readonly List<string> _log = [];
    private readonly List<string> _notices = [];
    private readonly Func<DateTime> _now;

    public string Program { get; }
    public DateTime Began { get; }
    public DateTime? Ended { get; private set; }
    public DateTime LastHeard { get; private set; }
    public string Case { get; private set; } = "";
    public int CaseNumber { get; private set; }
    public int CaseCount { get; set; }
    public string Waiting { get; private set; } = "";
    public bool Cancelled { get; private set; }
    public JsonObject? Report { get; set; }
    public JsonObject? Instabilities { get; set; }
    /// <summary>The program's analysis log being read, and the model file the job ran on (for Open in program).</summary>
    public string LogFile { get; set; } = "";
    public string ModelFile { get; set; } = "";
    private readonly HashSet<string> _casesSeen = new(StringComparer.OrdinalIgnoreCase);

    public RunMonitor(CsiProgram program, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.Now);
        Program = program.Name;
        Began = LastHeard = _now();
        _steps = Steps.Select(s => new Step { Key = s.Key, Name = string.Format(s.Name, program.Name) }).ToList();
    }

    private Step Of(string key) => _steps.First(s => s.Key == key);

    public void Begin(string key, string detail = "")
    {
        lock (_gate) { var s = Of(key); s.State = "running"; s.Began = _now(); s.Ended = null; s.Detail = detail; LastHeard = _now(); }
    }

    /// <summary>Ends a step: done, warning (it finished with something to look at) or failed.</summary>
    public void End(string key, string state = "done", string detail = "")
    {
        lock (_gate)
        {
            var s = Of(key);
            s.State = state; s.Began ??= _now(); s.Ended = _now();
            if (detail.Length > 0) s.Detail = detail;
            LastHeard = _now();
        }
    }

    public void Skip(string key, string why) { lock (_gate) { var s = Of(key); s.State = "skipped"; s.Detail = why; } }

    /// <summary>The step running now (the one a failure belongs to), or null.</summary>
    public Step? Running { get { lock (_gate) return _steps.FirstOrDefault(s => s.State == "running"); } }

    /// <summary>Marks every step still running as failed, and every step not reached as not run.</summary>
    public void Stop(string why, bool cancelled = false)
    {
        lock (_gate)
        {
            Cancelled |= cancelled;
            foreach (var s in _steps.Where(s => s.State == "running")) { s.State = "failed"; s.Ended = _now(); s.Detail = why; }
            foreach (var s in _steps.Where(s => s.State == "waiting" && s.Key != "close")) { s.State = "skipped"; s.Detail = cancelled ? "cancelled" : "not reached"; }
        }
    }

    public void Finish() { lock (_gate) { Ended = _now(); Waiting = ""; foreach (var s in _steps.Where(s => s.State == "waiting")) s.State = "skipped"; } }

    /// <summary>New lines of the program's own log: shown as they come, and the case it is on is taken from them.</summary>
    public void Log(IEnumerable<string> lines)
    {
        lock (_gate)
        {
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;
                _log.Add(line);
                if (_log.Count > 4000) _log.RemoveRange(0, 1000);
                LastHeard = _now();
                if (RunLog.CaseOf(line) is { } c && _casesSeen.Add(c)) { Case = c; CaseNumber = _casesSeen.Count; }
            }
        }
    }

    /// <summary>A box the program put up that CSI ProMax answered itself (said, so nothing happens unseen).</summary>
    public void Notice(string text) { lock (_gate) { _notices.Add(text); LastHeard = _now(); } }

    /// <summary>What the program is waiting on when it has not been heard from: a box no one answered yet ("" when none).</summary>
    public void Wait(string what) { lock (_gate) Waiting = what; }

    public void Heard() { lock (_gate) LastHeard = _now(); }

    public IReadOnlyList<string> LogLines { get { lock (_gate) return _log.ToList(); } }
    public IReadOnlyList<string> Notices { get { lock (_gate) return _notices.ToList(); } }
    public IReadOnlyList<Step> All { get { lock (_gate) return _steps.Select(s => new Step { Key = s.Key, Name = s.Name, State = s.State, Began = s.Began, Ended = s.Ended, Detail = s.Detail }).ToList(); } }

    private static double Seconds(DateTime? a, DateTime? b) => a is { } x && b is { } y ? Math.Round(Math.Max(0, (y - x).TotalSeconds), 1) : 0;

    /// <summary>The monitor for the window, with the last <paramref name="most"/> log lines.</summary>
    public JsonObject Json(int most = 400)
    {
        lock (_gate)
        {
            var now = _now();
            var o = new JsonObject
            {
                ["program"] = Program,
                ["elapsed"] = Seconds(Began, Ended ?? now),
                ["last_heard"] = Seconds(LastHeard, Ended ?? now),
                ["case"] = Case, ["case_number"] = CaseNumber, ["case_count"] = CaseCount,
                ["waiting"] = Waiting, ["cancelled"] = Cancelled, ["log_file"] = LogFile, ["model_file"] = ModelFile,
                ["steps"] = new JsonArray(_steps.Select(s => (JsonNode)new JsonObject
                {
                    ["key"] = s.Key, ["name"] = s.Name, ["state"] = s.State, ["detail"] = s.Detail,
                    ["seconds"] = s.Began is null ? 0 : Seconds(s.Began, s.Ended ?? now),
                }).ToArray()),
                ["log"] = new JsonArray(_log.TakeLast(most).Select(l => (JsonNode)l).ToArray()),
                ["log_total"] = _log.Count,
                ["notices"] = new JsonArray(_notices.Select(l => (JsonNode)l).ToArray()),
            };
            if (Report is not null) o["report"] = Report.DeepClone();
            return o;
        }
    }
}

/// <summary>
/// Reading the program's analysis log (.LOG; SAP2000 also .OUT) while it is written, and saying what its messages mean.
/// ETABS, SAP2000 and SAFE share one solver (SAPFire) and one log wording.
/// </summary>
public static class RunLog
{
    /// <summary>The log the program writes beside the model it runs: name.LOG (any case), else name.OUT.</summary>
    public static string FileOf(string modelFile)
    {
        if (modelFile.Length == 0) return "";
        var dir = Path.GetDirectoryName(modelFile) ?? "";
        var stem = Path.GetFileNameWithoutExtension(modelFile);
        foreach (var ext in new[] { ".LOG", ".log", ".OUT", ".out" })
            if (File.Exists(Path.Combine(dir, stem + ext))) return Path.Combine(dir, stem + ext);
        return Path.Combine(dir, stem + ".LOG");
    }

    /// <summary>
    /// The lines added to <paramref name="file"/> since <paramref name="offset"/> (moved on past them). A file that got
    /// shorter (the program started a new log) is read from its start. Never throws: a log not there yet gives nothing.
    /// </summary>
    public static List<string> Tail(string file, ref long offset, ref string partial)
    {
        var lines = new List<string>();
        try
        {
            if (file.Length == 0 || !File.Exists(file)) return lines;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset) { offset = 0; partial = ""; }
            if (stream.Length == offset) return lines;
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
            var text = partial + reader.ReadToEnd();
            offset = stream.Length;
            var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            partial = parts[^1];
            lines.AddRange(parts[..^1]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    private static readonly Regex CaseLine = new(@"^\s*(?:LOAD\s+)?CASE\s*[:=]\s*""?(?<n>[^""]+?)""?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CaseNamed = new(@"\bFOR\s+(?:LOAD\s+)?CASE\s+""?(?<n>[^""\s]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The load case a log line says the solver is on ("CASE: DEAD", "... FOR CASE MODAL"), or null.</summary>
    public static string? CaseOf(string line)
    {
        var m = CaseLine.Match(line);
        if (!m.Success) m = CaseNamed.Match(line);
        return m.Success ? m.Groups["n"].Value.Trim() : null;
    }

    /// <summary>True when the log says the analysis finished (SAPFire's "A N A L Y S I S   C O M P L E T E").</summary>
    public static bool Complete(IEnumerable<string> lines) => lines.Any(l => Squash(l).Contains("ANALYSISCOMPLETE", StringComparison.Ordinal));

    private static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c) && c != '*').Select(char.ToUpperInvariant).ToArray());

    /// <summary>A kind of message the programs write, what it means in plain words, and what to do.</summary>
    public sealed record Known(string Key, string Level, string[] Words, string Meaning, string Todo);

    /// <summary>The messages ETABS, SAP2000 and SAFE write when a run goes wrong, in their own words (upper case in the log).</summary>
    public static readonly Known[] Messages =
    [
        new("licence", "error", ["LICENSE", "LICENCE"],
            "The program could not get a licence for the run.",
            "Check that a licence is free (no other copy holds it, the licence server is reachable), then run again."),
        new("unstable", "error", ["UNSTABLE", "ILL-CONDITIONED", "ILL CONDITIONED", "STRUCTURE IS UNSTABLE"],
            "The program found the structure unstable or ill-conditioned: some part of it can move without anything resisting.",
            "Look for a missing support, a member pinned at both ends with nothing else holding it, a joint that touches nothing, or a section or material with zero stiffness. The instability check below lists what it found; Show on model selects it."),
        new("mechanism", "error", ["MECHANISM", "NEGATIVE STIFFNESS", "NEGATIVE EIGENVALUE", "NEGATIVE DIAGONAL", "ZERO STIFFNESS"],
            "Part of the model is a mechanism, or a member's stiffness became zero or negative (in a P-Delta or nonlinear case, buckling).",
            "Check releases at both ends of members, joints held only by pins, and very slender members under large axial load. For P-Delta cases, check that the gravity load is not more than the structure can carry."),
        new("accuracy", "warning", ["LOSS OF ACCURACY", "LOST ACCURACY", "ACCURACY LOST", "NUMERICAL PROBLEMS", "NUMERICAL PROBLEM"],
            "The solver lost numerical accuracy at some joints: usually a very stiff member beside a very flexible one, or a near-mechanism.",
            "Look at the joints the log names (Show on model selects them). Check stiffness modifiers far from 1, very short members, rigid links or very large springs."),
        new("mass", "error", ["NO MASS", "ZERO MASS", "MASS IS ZERO", "NO MASS DEFINED"],
            "A modal or response spectrum case found no mass in the model.",
            "Define the mass source (Define > Mass Source) from the dead load and the materials, then run again."),
        new("converge", "error", ["CONVERGENCE NOT ACHIEVED", "DID NOT CONVERGE", "NOT CONVERGED", "FAILED TO CONVERGE", "CONVERGENCE FAILURE"],
            "A nonlinear case did not converge.",
            "Increase the number of steps and iterations of that case, check the hinges and their properties, or run it with smaller load steps."),
        new("modes", "warning", ["MODES FOUND", "FEWER MODES", "NUMBER OF EIGEN MODES FOUND"],
            "The modal case found fewer modes than asked, or not enough mass participation.",
            "Check the modal case (Define > Modal Cases): more modes, or Ritz vectors, and that the mass source has the mass you expect."),
        new("memory", "error", ["OUT OF MEMORY", "INSUFFICIENT MEMORY", "NOT ENOUGH MEMORY", "MEMORY ALLOCATION"],
            "The computer ran out of memory during the run.",
            "Close other programs, or set the solver to run out of core (Analyze > Set Analysis Options), then run again."),
        new("disk", "error", ["DISK FULL", "NOT ENOUGH DISK", "INSUFFICIENT DISK", "NO SPACE"],
            "The disk ran out of space for the analysis files.",
            "Free space on the drive of the run folder, then run again."),
        new("file", "error", ["CANNOT OPEN FILE", "UNABLE TO OPEN", "ERROR OPENING", "ACCESS DENIED", "FILE IS LOCKED", "IN USE BY ANOTHER"],
            "The program could not open or write one of its files.",
            "Close any other program holding the model's files (another copy of the program on the same model), check the folder can be written, then run again."),
        new("aborted", "error", ["ABORT", "TERMINATED", "ANALYSIS NOT COMPLETE", "ANALYSIS INCOMPLETE", "RUN STOPPED", "FATAL"],
            "The analysis stopped before it finished.",
            "Read the program's lines above the stop: the cause is usually named just before it."),
        new("error", "error", ["ERROR"],
            "The program reported an error.",
            "Read the program's line; Open in the program shows the model as it ran, to inspect it."),
        new("warning", "warning", ["WARNING"],
            "The program wrote a warning.",
            "Read the warning; the analysis may still be usable, but check what it names."),
    ];

    /// <summary>The known kind of a log line (the most specific first), or null for an ordinary line.</summary>
    public static Known? Kind(string line)
    {
        var up = line.ToUpperInvariant();
        // A line saying a problem was NOT found is ordinary ("NUMBER OF WARNINGS = 0", "0 ERRORS").
        if (Regex.IsMatch(up, @"(WARNINGS?|ERRORS?)\s*(=|:)\s*0\b") || Regex.IsMatch(up, @"\b0\s+(WARNINGS?|ERRORS?)\b") || up.Contains("NO WARNINGS") || up.Contains("NO ERRORS")
            || up.Contains("TOLERANCE") || up.Contains("EQUILIBRIUM ERROR")) return null;
        return Messages.FirstOrDefault(k => k.Words.Any(w => up.Contains(w, StringComparison.Ordinal)));
    }

    private static readonly Regex JointWord = new(@"\b(?:JOINT|POINT|NODE)S?\s*[:=]?\s*""?(?<n>[A-Za-z0-9_.\-~@]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FrameWord = new(@"\b(?:FRAME|ELEMENT|LINE|MEMBER)S?\s*[:=]?\s*""?(?<n>[A-Za-z0-9_.\-~@]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AreaWord = new(@"\b(?:AREA|SHELL)S?\s*[:=]?\s*""?(?<n>[A-Za-z0-9_.\-~@]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The objects a log line names (joints, frames, areas), for Show on model.</summary>
    public static List<(string Kind, string Name)> Objects(string line)
    {
        var found = new List<(string, string)>();
        void Take(Regex re, string kind)
        {
            foreach (Match m in re.Matches(line))
            {
                var n = m.Groups["n"].Value;
                if (n.Length == 0 || !n.Any(char.IsDigit) || n.Equals("IS", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add((kind, n));
            }
        }
        Take(JointWord, "joint"); Take(FrameWord, "frame"); Take(AreaWord, "shell");
        return found;
    }

    /// <summary>
    /// The trouble the log names: each known kind once, with the program's own lines (up to 6 a kind) and the objects they name.
    /// </summary>
    public static JsonArray Trouble(IReadOnlyList<string> lines)
    {
        var byKind = new Dictionary<string, (Known K, List<string> Lines, List<(string, string)> Objects)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || Kind(line) is not { } k) continue;
            if (!byKind.TryGetValue(k.Key, out var e)) byKind[k.Key] = e = (k, [], []);
            var said = line;
            // The solver's own explanation is often on the lines after the warning ("CHECK THE MODEL CAREFULLY FOR: ...").
            for (var j = i + 1; j < Math.Min(lines.Count, i + 6) && lines[j].Trim() is { Length: > 0 } next && Kind(next) is null && next.TrimStart().StartsWith('-'); j++) said += " " + next.Trim();
            if (e.Lines.Count < 6 && !e.Lines.Contains(said)) e.Lines.Add(said);
            foreach (var o in Objects(line)) if (!e.Objects.Contains(o) && e.Objects.Count < 200) e.Objects.Add(o);
        }
        // A specific kind makes the general "error"/"warning" of the same lines redundant.
        return new JsonArray(byKind.Values
            .OrderBy(e => e.K.Level == "error" ? 0 : 1).ThenBy(e => Array.IndexOf(Messages, e.K))
            .Select(e => (JsonNode)new JsonObject
            {
                ["kind"] = e.K.Key, ["level"] = e.K.Level, ["meaning"] = e.K.Meaning, ["todo"] = e.K.Todo,
                ["said"] = new JsonArray(e.Lines.Select(l => (JsonNode)l).ToArray()),
                ["objects"] = new JsonArray(e.Objects.Select(o => (JsonNode)new JsonObject { ["kind"] = o.Item1, ["name"] = o.Item2 }).ToArray()),
            }).ToArray());
    }

    /// <summary>What a failure message of CSI ProMax itself (not the log) means, in the same shape as <see cref="Trouble"/>.</summary>
    public static JsonObject? Plain(string message)
    {
        if (message.Length == 0) return null;
        if (message.Contains("licen", StringComparison.OrdinalIgnoreCase) || message.Contains("did not start within", StringComparison.OrdinalIgnoreCase))
            return Entry("licence", "error", "The program did not start, or it is waiting on a licence message in its window.", "Press Show program window to see it. Answer a licence message there yourself, then run again.", message);
        if (message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            return Entry("installed", "error", "The program is not installed on this PC.", "Save the model and run it on a PC that has the program, or open its review pack with File > Import > Results.", message);
        if (message.Contains("stopped answering", StringComparison.OrdinalIgnoreCase) || message.Contains("did not close within", StringComparison.OrdinalIgnoreCase) || message.Contains("RPC", StringComparison.Ordinal) || message.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
            return Entry("hung", "error", "The program stopped answering (it may have crashed, or it is held by a box).", "If it is still open, press Show program window to see what it shows; otherwise run again. Open in the program opens the model as it ran.", message);
        if (message.Contains("not analysed", StringComparison.OrdinalIgnoreCase) || message.Contains("did not finish", StringComparison.OrdinalIgnoreCase))
            return Entry("not-finished", "error", "The program answered, but the analysis did not finish.", "Read the program's own lines below: they name the reason. Open in the program shows the model as it ran.", message);
        if (message.Contains("could not be started", StringComparison.OrdinalIgnoreCase) || message.Contains("could not open", StringComparison.OrdinalIgnoreCase) || message.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return Entry("open", "error", "The program could not open the model.", "Read the program's words below. Open in the program opens the same file in the program, where it shows what it does not accept.", message);
        return null;
    }

    private static JsonObject Entry(string kind, string level, string meaning, string todo, string said) => new()
    {
        ["kind"] = kind, ["level"] = level, ["meaning"] = meaning, ["todo"] = todo,
        ["said"] = new JsonArray((JsonNode)said), ["objects"] = new JsonArray(),
    };

    /// <summary>
    /// The design rows that did not pass or carry a message, from the pack's design tables (ETABS, SAP2000 and SAFE all
    /// write a status, an error and a warning field): how many per table, and the first few with their object.
    /// </summary>
    public static JsonArray DesignTrouble(JsonObject? tables)
    {
        var found = new JsonArray();
        if (tables is null) return found;
        foreach (var (name, node) in tables)
        {
            if (node is not JsonObject t || !DesignResults.IsDesignTable(name)) continue;
            var fields = (t["fields"] as JsonArray ?? []).Select(f => f?.GetValue<string>() ?? "").ToList();
            int Col(params string[] names) => fields.FindIndex(f => names.Any(n => f.Equals(n, StringComparison.OrdinalIgnoreCase)));
            var status = Col("Status", "DesignStatus", "PMMStatus", "Result");
            var err = Col("ErrMsg", "ErrorMsg", "Errors", "ErrorMessage");
            var warn = Col("WarnMsg", "WarningMsg", "Warnings", "WarningMessage");
            var obj = Col("Label", "Frame", "Pier", "Spandrel", "Strip", "Point", "Area", "Name", "UniqueName");
            if (status < 0 && err < 0 && warn < 0) continue;
            int fail = 0, errors = 0, warnings = 0;
            var examples = new JsonArray();
            foreach (var r in t["rows"] as JsonArray ?? [])
            {
                if (r is not JsonArray row) continue;
                string V(int i) => i >= 0 && i < row.Count ? row[i]?.ToString() ?? "" : "";
                bool Said(string v) => v.Trim().Length > 0 && !v.Trim().Equals("No Messages", StringComparison.OrdinalIgnoreCase) && !v.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) && v.Trim() != "0";
                var s = V(status);
                var bad = s.Contains("O/S", StringComparison.OrdinalIgnoreCase) || s.Contains("Overstress", StringComparison.OrdinalIgnoreCase) || s.Contains("Fail", StringComparison.OrdinalIgnoreCase) || s.Contains("Error", StringComparison.OrdinalIgnoreCase) || s.Trim() == "NG";
                var e = Said(V(err)); var w = Said(V(warn));
                if (bad) fail++; if (e) errors++; if (w) warnings++;
                if ((bad || e) && examples.Count < 8)
                    examples.Add(new JsonObject { ["object"] = V(obj), ["status"] = s, ["message"] = e ? V(err) : V(warn) });
            }
            if (fail + errors + warnings == 0) continue;
            found.Add(new JsonObject
            {
                ["table"] = name, ["failing"] = fail, ["errors"] = errors, ["warnings"] = warnings, ["examples"] = examples,
                ["todo"] = fail > 0 ? "Members that do not pass: larger sections or more reinforcement, or check their loads. Display > Design shows them in red." : "Read each message: the design was made, but the program has something to say about these members.",
            });
        }
        return found;
    }
}
