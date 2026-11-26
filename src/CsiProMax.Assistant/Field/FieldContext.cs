using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>One thing the field test did: the part of the test it belongs to, whether it worked, and the answer.</summary>
/// <param name="Detail">Short, for a dialog or a list.</param>
/// <param name="Full">The whole answer when it was longer, for the report.</param>
/// <param name="Skipped">Left out on purpose; Detail says why. It counts as not tested, not as a failure.</param>
/// <param name="Seconds">How long it took, for the steps that take a while (starting the program, the analysis).</param>
public sealed record FieldStep(string Section, string Name, bool Ok, string Detail, string? Full = null, bool Skipped = false, double Seconds = 0);

/// <summary>A check inside a field test step did not hold: the message says what was expected and what was found.</summary>
public sealed class FieldCheckException(string message) : Exception(message);

/// <summary>
/// Inside a field test step: the check could not be made here (the way the test reads something back did not work),
/// which says nothing about the tool under test. The step is listed as not tested, with the reason.
/// </summary>
public sealed class FieldSkipException(string why) : Exception(why);

/// <summary>
/// What the steps of a field test work with: the model they act on, the folder for the files they make, and the list
/// they add their results to. Each program has its own steps (Field/EtabsFieldSteps, Safe/SafeFieldSteps,
/// Sap2000/SapFieldSteps); they all record through this, so the report and the count of what was not tested are made
/// in one place.
///
/// The rule that makes the test complete: every tool that is offered (<see cref="Offer"/>) must be run at least once
/// through <see cref="Tool"/> or <see cref="Refused"/>, and <see cref="Coverage"/> fails naming the ones that were not.
/// </summary>
public sealed class FieldContext(CsiProgram program, string folder, ICsiLink link)
{
    private const int MostShort = 300, MostFull = 20000;

    private readonly Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _run = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly AsyncLocal<int> _stage = new();
    private int _stageNow;
    private string _section = "";

    /// <summary>The CSI program under test now.</summary>
    /// <summary>
    /// Where a file error came from (its first frames in CSI ProMax): .NET's "Access to the path is denied." names no
    /// path when the denied call was a replace or a delete (0.4.40 field test, the team library's check).
    /// </summary>
    internal static string Where(Exception ex)
    {
        if (ex is not (IOException or UnauthorizedAccessException)) return "";
        var frames = new System.Diagnostics.StackTrace(ex, false).GetFrames()
            .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.Namespace?.StartsWith("CsiProMax", StringComparison.Ordinal) == true)
            .Select(m => $"{m!.DeclaringType!.Name}.{m.Name}").Distinct().Take(3).ToList();
        var first = new System.Diagnostics.StackTrace(ex, false).GetFrames().FirstOrDefault()?.GetMethod();
        return frames.Count == 0 && first is null ? "" : $" (at {string.Join(" < ", frames.Count > 0 ? frames : [$"{first!.DeclaringType?.Name}.{first.Name}"])}{(first is not null && frames.Count > 0 ? $"; in {first.DeclaringType?.Name}.{first.Name}" : "")})";
    }

    public CsiProgram Program { get; set; } = program;

    /// <summary>Where the test puts the files it makes (the scratch model, packs, documents). Made by the runner.</summary>
    public string Folder { get; } = folder;

    /// <summary>
    /// The model the steps act on now: the Modeler's model while the scratch model is built, then the scratch model in
    /// the test's own window of the program. Never the engineer's open model while changes are made.
    /// </summary>
    public ICsiLink Link { get; set; } = link;

    /// <summary>The file the Modeler wrote for each program, by the program's key: .e2k for etabs, .s2k for sap2000, .f2k for safe.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file the Modeler wrote for the program under test ("" when it wrote none), which the program then opens.</summary>
    public string ModelFile
    {
        get => Files.GetValueOrDefault(Program.Key, "");
        set => Files[Program.Key] = value;
    }

    /// <summary>Numbers one part of the test leaves for a later part, by name ("sap2000.joints": what the .s2k was written with).</summary>
    public Dictionary<string, double> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the Modeler's model held when it was written, to compare with what the program read in.</summary>
    public (int Stories, int Frames, int Areas) Written { get; set; }

    public List<FieldStep> Steps { get; } = [];

    /// <summary>Told each step's name as it starts, so a long run shows where it is. It may throw to stop the run.</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>Starts a part of the report; the steps that follow belong to it.</summary>
    public void Section(string title) => _section = title;

    /// <summary>
    /// Runs one check and records it: ok with what the body gave, or FAIL with why it threw. Gives the body's value
    /// back (the default when it failed), so later steps can build on it.
    /// </summary>
    public T? Step<T>(string name, Func<T> body)
    {
        StillWanted();
        if (_gone is { } why && Watched is not null)
        {
            // The window stopped answering earlier in this part: what is left is not tested, said once per step with
            // the step it stopped at, instead of failing each with the same message.
            Skip(name, why);
            return default;
        }
        lock (_gate) { _doing = name; _moved = DateTime.UtcNow; }
        Progress?.Invoke(name);
        var refusedBefore = CsiProMax.Assistant.Etabs.EditableTables.Refusals;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var value = body();
            Add(name, true, Text(value), clock.Elapsed.TotalSeconds);
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch (FieldSkipException ex)
        {
            // Left out without a reason that holds: a gap in the test or a bug, so a failure (SkipAudit).
            if (!MayLeaveOut(ex.Message)) { Add(name, false, SkipAudit.Failure(ex.Message), clock.Elapsed.TotalSeconds); return default; }
            var left = new FieldStep(_section, name, true, "skipped: " + ex.Message, null, true);
            lock (_gate) Steps.Add(left);
            Live(left);
            return default;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var real = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            // A table import the program refused in this step: what was sent and its whole log, so the report alone says why.
            var import = CsiProMax.Assistant.Etabs.EditableTables.Refusals != refusedBefore ? " | Import sent and its whole log: " + CsiProMax.Assistant.Etabs.EditableTables.LastRefused : "";
            Add(name, false, $"{real.GetType().Name}: {real.Message}{Where(real)}{import}", clock.Elapsed.TotalSeconds);
            StoppedAnswering(name);
            return default;
        }
    }

    /// <summary>
    /// The program window this part of the test works in (set by the runner for a scratch window's own steps, null
    /// otherwise). When a step fails and that window no longer gives its model, the steps left in the part are
    /// recorded as not tested: one hang is one failure in the report, not fifty.
    /// </summary>
    /// <summary>
    /// What must run while the scratch model still has its results: the Viewer's check. A program's steps call
    /// <see cref="WithResults"/> before they unlock the model (ETABS unlocks it at the end of its part, and the Viewer
    /// then had no result to show: every result button was greyed out in the 0.4.11 run). Run once.
    /// </summary>
    public Action? WhileAnalysed { get; set; }

    /// <summary>Runs <see cref="WhileAnalysed"/> if it has not run yet.</summary>
    public void WithResults()
    {
        var work = WhileAnalysed;
        WhileAnalysed = null;
        work?.Invoke();
    }

    public ICsiLink? Watched
    {
        get => _watched;
        set { _watched = value; _gone = null; }
    }

    private ICsiLink? _watched;
    private string? _gone;

    private void StoppedAnswering(string at)
    {
        if (_watched is not { } window || _gone is not null) return;
        try { window.Info(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var real = ex is AggregateException { InnerException: { } inner } ? inner : ex;
            _gone = $"the program's window stopped answering at the step '{at}' ({real.Message}), so this step was not run";
        }
    }

    /// <summary>
    /// Runs a part of the test that talks to a program which may stop answering (a message waiting in its window), and
    /// gives up on it after <paramref name="most"/>: the part is recorded as failed at the step it was on, the steps it
    /// would still record are dropped, and the test goes on so the report is always written. False when it gave up.
    /// </summary>
    public bool Within(TimeSpan most, string what, Action part)
    {
        var mine = Interlocked.Increment(ref _stageNow);
        var section = _section;
        var work = Task.Run(() => { _stage.Value = mine; part(); });
        lock (_gate) _moved = DateTime.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool done;
        var quiet = false;
        try
        {
            // Given up on at the part's limit, or sooner when no step has started or ended for Quiet: one call to the
            // program that never comes back (with nothing on its screen) is not waited on for the whole limit.
            while (!(done = work.Wait(TimeSpan.FromMilliseconds(Math.Clamp((most - clock.Elapsed).TotalMilliseconds, 1, 5000)))))
            {
                if (clock.Elapsed >= most) break;
                DateTime moved;
                lock (_gate) moved = _moved;
                if (DateTime.UtcNow - moved >= Quiet) { quiet = true; break; }
            }
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException stop) { throw new OperationCanceledException(stop.Message); }
        catch (AggregateException ex)
        {
            _section = section;
            Add(what, false, $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            return true;
        }
        if (done) return true;
        string last;
        lock (_gate) { last = _doing; _givenUp.Add(mine); }
        _section = section;
        Add(what, false, (quiet ? $"Nothing moved in it for {Span(Quiet)} (one call to the program did not come back), so it was given up on." : $"It did not come back within {Span(most)} and was given up on.") +
            $" It was at: {(last.Length > 0 ? last : "its start")}. A message may be waiting in the program's window: look at it, answer it, and run the field test again.");
        return false;
    }

    private static string Span(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minutes" : $"{Math.Max(1, t.TotalSeconds):0} seconds";

    /// <summary>Stops a part that was given up on (<see cref="Within"/>) the next time it comes back to record something.</summary>
    private void StillWanted()
    {
        bool gone;
        lock (_gate) gone = _givenUp.Contains(_stage.Value);
        if (gone) throw new OperationCanceledException("This part of the field test was given up on.");
    }

    private readonly HashSet<int> _givenUp = [];
    private string _doing = "";
    private DateTime _moved = DateTime.UtcNow;

    /// <summary>
    /// How long a part may go without a step starting or ending before it is given up on (<see cref="Within"/>). The
    /// slowest single step of a scratch model (its analysis or design) takes a minute or two.
    /// </summary>
    public TimeSpan Quiet { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The same for a check that gives nothing back. True when it worked.</summary>
    public bool Step(string name, Action body) => Step(name, () => { body(); return "done"; }) is not null;

    /// <summary>
    /// Whether a step may be listed as not tested for this reason (SkipAudit). In a program's scratch part run with no
    /// program behind it (the tests' stand-in for ETABS, SAP2000 or SAFE) every such step is one the missing program
    /// would have answered, so it may: that is "the program is not here".
    /// </summary>
    private bool MayLeaveOut(string why) =>
        SkipAudit.Allowed(SkipAudit.Of(why)) || (_section.Contains("scratch model", StringComparison.OrdinalIgnoreCase) && !FieldTest.IsLive(Link));

    /// <summary>Records something left out on purpose, with the reason. It is listed as not tested.</summary>
    public void Skip(string name, string why)
    {
        StillWanted();
        if (!MayLeaveOut(why)) { Add(name, false, SkipAudit.Failure(why)); return; }
        var skipped = new FieldStep(_section, name, true, "skipped: " + why, null, true);
        lock (_gate) Steps.Add(skipped);
        Live(skipped);
    }

    /// <summary>
    /// Records a step the self test made (the open model's part): its answer as it gave it, and "skipped: ..." as not
    /// tested.
    /// </summary>
    public void Record(SelfTest.StepResult step)
    {
        StillWanted();
        var skipped = step.Ok && step.Detail.StartsWith("skipped:", StringComparison.OrdinalIgnoreCase);
        var recorded = new FieldStep(_section, step.Name, step.Ok, step.Detail, step.Full, skipped);
        lock (_gate) Steps.Add(recorded);
        Live(recorded);
    }

    /// <summary>Records something found that is neither a pass nor a failure: a fact for the report.</summary>
    public void Note(string name, string what) => Add(name, true, what);

    /// <summary>Records a failure found outside a step.</summary>
    public void Fail(string name, string why) => Add(name, false, why);

    /// <summary>Inside a step: the check that must hold. The message says what was expected and what was found.</summary>
    public static void Expect(bool condition, string what)
    {
        if (!condition) throw new FieldCheckException(what);
    }

    /// <summary>
    /// The tools this part of the test must cover. A later tool with the same name replaces an earlier one, as in the
    /// chat (a program's own get_model_context replaces the shared one).
    /// </summary>
    public void Offer(IEnumerable<Tool> tools)
    {
        foreach (var tool in tools) _tools[tool.Name] = tool;
    }

    /// <summary>Forgets the offered tools and which were run: the next part of the test has its own set.</summary>
    public void ClearTools()
    {
        _tools.Clear();
        _run.Clear();
    }

    /// <summary>
    /// Runs a tool by name on <see cref="Link"/> with these arguments, as the chat would, and records it as a step
    /// named "tool name" (or <paramref name="step"/>). Gives the tool's answer, or null when it failed.
    /// <paramref name="check"/> looks at the answer and throws (<see cref="Expect"/>) when it is not what it should be.
    /// </summary>
    public JsonNode? Tool(string name, string json = "{}", Action<JsonNode>? check = null, string? step = null) =>
        Step(step ?? "tool " + name, () =>
        {
            var answer = Call(name, json);
            check?.Invoke(answer);
            return answer;
        });

    /// <summary>
    /// Runs a tool that must say no here (the analysis in the Modeler, say) and records it as ok when it refuses with a
    /// message that holds <paramref name="saying"/>, and as a failure when it goes through or refuses in other words.
    /// </summary>
    public void Refused(string name, string json, string saying, string? step = null) =>
        Step(step ?? $"tool {name} says no", () =>
        {
            try { Call(name, json); }
            catch (Exception ex) when (ex is ToolInputException or EtabsException)
            {
                Expect(ex.Message.Contains(saying, StringComparison.OrdinalIgnoreCase), $"it refused, but not with '{saying}': {ex.Message}");
                return "refused as it should: " + ex.Message;
            }
            throw new FieldCheckException("it should have refused, and went through");
        });

    /// <summary>Runs an offered tool without recording a step, for a step that looks at the outcome itself. It counts as run.</summary>
    public JsonNode Call(string name, string json)
    {
        // A part that was given up on never makes another call: the link may be another model's by now.
        StillWanted();
        if (!_tools.TryGetValue(name, out var tool)) throw new FieldCheckException($"There is no tool named {name} here.");
        _run.Add(name);
        using var document = JsonDocument.Parse(json);
        JsonNode answer;
        try { answer = tool.Invoke(document.RootElement.Clone(), Link); }
        catch (Exception ex) when (ex is ToolInputException or EtabsException && NamesAnotherProgram(ex.Message) is { } other)
        {
            throw new FieldCheckException($"{name} answered in {other}'s words, and this is {Link.Program.Name}: {ex.Message}");
        }
        if (WordsOf(answer).Select(w => (Text: w, Other: NamesAnotherProgram(w))).FirstOrDefault(w => w.Other is not null) is { Other: { } named } wrong)
            throw new FieldCheckException($"{name} answered in {named}'s words, and this is {Link.Program.Name}: {wrong.Text}");
        return answer;
    }

    /// <summary>
    /// The program a text of ours names when it is not the one the link works in: "ETABS" in an answer given in SAFE
    /// or SAP2000. Only in a live program (the Modeler writes ETABS's files and says so), and never for ETABS itself,
    /// whose tools may name the other programs' files.
    /// </summary>
    internal string? NamesAnotherProgram(string text)
    {
        if (Link is OfflineEtabs || Link.Program.Key == CsiProgram.Etabs.Key) return null;
        return System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + CsiProgram.Etabs.Name + @"\b") ? CsiProgram.Etabs.Name : null;
    }

    /// <summary>
    /// The sentences of ours in a tool's answer: the texts under note, next, warning, why, message and error, at any
    /// depth. Names from the model (a file, a section, a table) are not ours and are not looked at.
    /// </summary>
    internal static IEnumerable<string> WordsOf(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && key is "note" or "next" or "warning" or "why" or "message" or "error") yield return text!;
                    else foreach (var inner in WordsOf(value)) yield return inner;
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var inner in WordsOf(item)) yield return inner;
                break;
        }
    }

    /// <summary>Counts a tool as covered that was exercised another way (the runner itself, say).</summary>
    public void Covered(string name) => _run.Add(name);

    /// <summary>The offered tools no step has run yet.</summary>
    public IReadOnlyList<string> NotRun => _tools.Keys.Where(k => !_run.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>The closing check of a part: every offered tool was run. It fails with the names of the ones that were not.</summary>
    public void Coverage(string name) =>
        Step(name, () =>
        {
            var missing = NotRun;
            Expect(missing.Count == 0, $"{missing.Count} of {_tools.Count} tools were not run by the field test: {string.Join(", ", missing)}");
            return $"all {_tools.Count} tools were run";
        });

    private void Add(string name, bool ok, string text, double seconds = 0)
    {
        var step = new FieldStep(_section, name, ok, text.Length > MostShort ? text[..MostShort] + "..." : text,
            text.Length <= MostShort ? null : text.Length > MostFull ? text[..MostFull] + "..." : text, false, Math.Round(seconds, 1));
        lock (_gate) { Steps.Add(step); _moved = DateTime.UtcNow; }
        Live(step);
    }

    /// <summary>The file in the work folder each step's outcome is added to as it happens, so a run can be followed (and fixed) before its report exists.</summary>
    public const string LiveFile = "results so far.txt";

    /// <summary>
    /// Says a step's outcome as it happens: to whoever follows the run (the console shows "ok" or "FAIL: why"), and
    /// to <see cref="LiveFile"/> in the work folder. A full run takes up to an hour, and its failures were only known
    /// when the report was written.
    /// </summary>
    private void Live(FieldStep step)
    {
        var line = LiveLine(step);
        try { Outcome?.Invoke(line); } catch (Exception ex) when (ex is not OutOfMemoryException) { /* following the run never stops it */ }
        try { Directory.CreateDirectory(Folder); lock (_gate) File.AppendAllText(Path.Combine(Folder, LiveFile), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    /// <summary>Called with each step's outcome line as it is recorded.</summary>
    public Action<string>? Outcome { get; set; }

    public static string LiveLine(FieldStep step)
    {
        var detail = step.Detail.ReplaceLineEndings(" ");
        return step.Skipped ? $"not tested  [{step.Section}] {step.Name}: {detail}"
            : step.Ok ? $"ok          [{step.Section}] {step.Name}"
            : $"FAIL        [{step.Section}] {step.Name}: {detail}";
    }

    private static string Text(object? value) => Readable.Text(value);
}
