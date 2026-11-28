using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using CsiProMax.Model;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Checks.Stability;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// CSI ProMax Engine's Analyze > Run (Ahmed 2026-11-26): the model is written for its program, the program is started
/// in a window of its own (never the one the engineer has open), hidden, given the model, analysed and designed as the
/// Run form asks; every result and design table comes back as a review pack beside the model. The program's own notices
/// (analysis complete, the maintenance reminder, "really close?") are answered as the field test answers them; a
/// licence message is never answered, and the run says plainly that it is waiting on one.
///
/// Each job closes the program as soon as it is done (Ahmed 2026-11-26 18:59): the licence is used only while it
/// runs. The Run form asks for the analysis, the design of the kinds ticked, or both. The analysed model the program
/// saved is kept in the run folder: a design asked on a model that has not changed since is run on that saved model,
/// opened as it is with its results, so the analysis is not run again; a changed model is analysed first.
/// </summary>
public static class EngineRun
{
    /// <summary>The design kinds each program can run from the Run form, in its own words.</summary>
    public static readonly IReadOnlyDictionary<string, (string Kind, string Name)[]> Kinds = new Dictionary<string, (string Kind, string Name)[]>
    {
        ["etabs"] = [("steel", "Steel frames"), ("concrete", "Concrete frames"), ("composite", "Composite beams"), ("shear_wall", "Shear walls (piers and spandrels)"), ("slab", "Concrete slabs")],
        ["sap2000"] = [("steel", "Steel frames"), ("concrete", "Concrete frames")],
        ["safe"] = [("slab", "Slabs, strips and punching")],
    };

    private static readonly object Gate = new();
    private static readonly List<string> Lines = [];
    private static string _phase = "", _program = "", _error = "", _packFile = "";
    private static bool _running, _done;
    private static int _startRevision;
    private static JsonObject? _pack;
    // What the last run's results lost against the program (only when CheckLoss); null when not checked.
    private static List<string>? _lost;

    /// <summary>The field test sets this: after each run, what the results lost is checked against the program before it closes.</summary>
    public static bool CheckLoss { get; set; }

    /// <summary>The field test's measuring while the program still holds the run's results (after the results step); its answer is in Status's "probe".</summary>
    public static Func<ICsiLink, JsonObject>? WhileOpen { get; set; }
    private static JsonObject? _probe;
    private static OfflineEtabs? _model;

    // The last analysis: the model it was of, its revision, and the program's own file holding its results.
    private static OfflineEtabs? _analysedModel;
    private static int _analysedRevision = -1;
    private static string _analysedFile = "", _analysedProgram = "";
    private static int _starts;
    /// <summary>Whether the last job's program was seen to end (its process gone), so its licence is free.</summary>
    private static bool _closed, _keptOpen;
    /// <summary>The program left open after the last run (Settings &gt; Runs, keep it open), closed before the next one.</summary>
    private static IScratchWindow? _kept;
    private static string _keptName = "";

    // The Run Monitor of the job going on (or the last one), its program window, and whether Cancel was pressed.
    private static RunMonitor? _mon;
    private static IScratchWindow? _window;
    private static bool _cancel;

    /// <summary>Settings &gt; Runs: whether a Run leaves the program open with the analysed model (Ahmed 2026-11-27, for now).</summary>
    public static Func<bool> KeepOpenSetting { get; set; } = () => { try { return CsiProMax.Settings.AgentSettings.Load().KeepProgramOpenAfterRun; } catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } };

    // The pack written by the last run: of which model, at which revision (removed when the model moves on).
    private static OfflineEtabs? _packModel;
    private static int _packRevision = -1;
    private static string _packPage = "";

    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Thread Worker = MakeWorker();

    private static Thread MakeWorker()
    {
        var t = new Thread(() => { foreach (var job in Queue.GetConsumingEnumerable()) { try { job(); } catch (OutOfMemoryException) { CsiProMax.Assistant.OutOfMemory.Free(); } catch (Exception) { } } }) { IsBackground = true, Name = "CSI ProMax Engine run" };
        try { t.SetApartmentState(ApartmentState.STA); } catch (PlatformNotSupportedException) { }
        t.Start();
        return t;
    }

    private static void Post(Action job) { _ = Worker; Queue.Add(job); }

    /// <summary>A run of the analysis (when <paramref name="design"/>, the design of every kind the program has too).</summary>
    public static JsonObject Start(OfflineEtabs model, bool design, Func<CsiProgram, string>? apiFile = null)
    {
        var program = ProgramOf(model);
        return Start(model, analyse: true, design ? Kinds[program.Key].Select(k => k.Kind).ToList() : [], apiFile);
    }

    /// <summary>
    /// Starts a run on the Modeler's model. Called on the window's thread (it reads the model); the run goes on in the
    /// background. <paramref name="analyse"/>: run the analysis (skipped when the program holds this model unchanged
    /// and analysed). <paramref name="kinds"/>: the designs to run after it. <paramref name="stopOnErrors"/>: the Run
    /// form's "stop before the run when the check finds errors": the instability check runs first either way, and with
    /// this a model it finds errors in is not given to the program at all (no licence used); the Run Report lists them,
    /// and Run anyway runs it.
    /// </summary>
    public static JsonObject Start(OfflineEtabs model, bool analyse, IReadOnlyList<string> kinds, Func<CsiProgram, string>? apiFile = null, IReadOnlyList<string>? selected = null, bool? keepOpen = null, bool stopOnErrors = false)
    {
        var program = ProgramOf(model);
        var unknown = kinds.Where(k => !Kinds[program.Key].Any(x => x.Kind == k)).ToList();
        if (unknown.Count > 0) return new JsonObject { ["ok"] = false, ["error"] = $"{program.Name} has no {string.Join(", ", unknown)} design." };
        if (!analyse && kinds.Count == 0) return new JsonObject { ["ok"] = false, ["error"] = "Nothing was asked: tick the analysis or a design." };
        lock (Gate)
        {
            if (_running) return new JsonObject { ["ok"] = false, ["error"] = $"A run in {_program} is still going ({_phase})." };
            var api = (apiFile ?? (p => EtabsApi.FindApiFile(p)))(program);
            if (!File.Exists(api))
                return new JsonObject { ["ok"] = false, ["error"] = $"{program.Name} is not installed on this PC, so the model cannot be run here. Save it (File > Save) and open the {(program.Key == "etabs" ? ".e2k" : program.Key == "sap2000" ? ".s2k" : ".f2k")} on a PC with {program.Name}, or run it there and open its review pack with File > Import > Results." };
            var keep = keepOpen ?? KeepOpenSetting();
            var current = AnalysedNow(model, program);
            string file = "";
            var folder = RunFolder(model);
            if (!current)
                try { file = model.WriteForRun(Path.Combine(folder, RunName(model) + ".e2k")); }
                catch (EtabsException ex) { return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
            Lines.Clear(); _error = ""; _pack = null; _lost = null; _probe = null; _done = false; _running = true; _closed = false; _keptOpen = false; _cancel = false; _window = null; _seenNotices = 0;
            _mon = new RunMonitor(program) { CaseCount = CasesOf(model), SourceFile = model.Path };
            // The model as the instability check reads it, taken here on the window's thread; checked in the background.
            AnalysisModel? check = null;
            try { check = FromStructural.Read(model.Model, program.Key); } catch (Exception ex) when (ex is not OutOfMemoryException) { _mon.Skip("check", "the model could not be read for the check: " + ex.Message); }
            _program = program.Name; _model = model; _startRevision = model.Revision;
            _phase = $"Starting {program.Name} in the background";
            if (file.Length > 0) Lines.Add($"The model was written for {program.Name}: {file}");
            // A design on its own still needs the analysis behind it: a model not analysed as it is now is analysed first.
            var mustAnalyse = !current;
            var analysed = _analysedFile;
            var only = kinds.Count > 0 && selected is { Count: > 0 } ? Places(model, selected) : null;
            if (only is not null) Lines.Add($"The design is for the {selected!.Count} selected objects only.");
            if (file.Length > 0) _mon.ModelFile = file;
            Post(() => Work(program, folder, mustAnalyse ? file : analysed, mustAnalyse, analyse, kinds, only, keep, check, stopOnErrors));
            return new JsonObject { ["ok"] = true, ["program"] = program.Name, ["file"] = file, ["analyses"] = mustAnalyse, ["keep_open"] = keep };
        }
    }

    /// <summary>Whether the program's saved analysis is of this model as it is now (same model, same revision, file still there).</summary>
    private static int CasesOf(OfflineEtabs model) { try { return model.Model.LoadCases.Count; } catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; } }

    private static bool AnalysedNow(OfflineEtabs model, CsiProgram program) =>
        ReferenceEquals(_analysedModel, model) && _analysedRevision == model.Revision && _analysedProgram == program.Key && File.Exists(_analysedFile);

    private static CsiProgram ProgramOf(OfflineEtabs model) => model.Model.Target switch { "sap2000" => CsiProgram.Sap2000, "safe" => CsiProgram.Safe, _ => CsiProgram.Etabs };

    /// <summary>Where a run's files go: a folder beside the model's own file, named after it (Ahmed 2026-11-25: packs in the model's folder).</summary>
    internal static string RunFolder(OfflineEtabs model)
    {
        var name = Safe(model.Model.Title);
        var root = model.Path.Length > 0 ? Path.GetDirectoryName(model.Path)! : OfflineEtabs.DefaultFolder;
        return Path.Combine(root, name + " run");
    }

    /// <summary>
    /// The name the run's file is written under: the model file's own name, and the model's title only for a model never
    /// saved. A Verification example (HK CP-2013 Ex002.EDB) carries the title of another ("Eurocode 2-2004 CO-FR-002"),
    /// so its run was written under that name (0.4.70 full test, 2026-11-27).
    /// </summary>
    internal static string RunName(OfflineEtabs model) =>
        model.Path.Length > 0 && Path.GetFileNameWithoutExtension(model.Path.Replace('\\', '/')) is { Length: > 0 } own ? Safe(own) : Safe(model.Model.Title);

    private static string Safe(string title)
    {
        var clean = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : "Untitled";
    }

    private static void Say(string phase) { lock (Gate) { _phase = phase; Lines.Add(phase + "..."); } }

    /// <summary>The arguments of run_design for the kinds asked, in each program's own tool.</summary>
    internal static string DesignArgs(CsiProgram program, IReadOnlyList<string> kinds) => program.Key == "sap2000"
        ? JsonSerializer.Serialize(new { steel = kinds.Contains("steel"), concrete = kinds.Contains("concrete") })
        : JsonSerializer.Serialize(new { kinds });

    /// <summary>Where the selected objects are (frames by their ends, areas by their centre), to find them in the program, whose names may differ.</summary>
    internal static Shapes Places(OfflineEtabs model, IReadOnlyList<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var frames = model.Frames().Where(f => wanted.Contains(f.Name)).Select(f => (f.X1, f.Y1, f.Z1, f.X2, f.Y2, f.Z2)).ToList();
        var areas = model.Areas().Where(a => wanted.Contains(a.Name) && a.Corners.Count > 0).Select(a => Centre(a.Corners)).ToList();
        return new Shapes(frames, areas);
    }

    internal sealed record Shapes(List<(double X1, double Y1, double Z1, double X2, double Y2, double Z2)> Frames, List<(double X, double Y, double Z)> Areas);

    private static (double X, double Y, double Z) Centre(IReadOnlyList<(double X, double Y, double Z)> c) => (c.Average(p => p.X), c.Average(p => p.Y), c.Average(p => p.Z));

    /// <summary>The program's names of the frames and areas at the places given (to 1 mm in metres, or the same share of the model's units).</summary>
    internal static (List<string> Frames, List<string> Areas) Find(ICsiLink link, Shapes at, double tolerance = 0.002)
    {
        bool Near(double a, double b) => Math.Abs(a - b) <= tolerance;
        bool Same((double, double, double) p, (double, double, double) q) => Near(p.Item1, q.Item1) && Near(p.Item2, q.Item2) && Near(p.Item3, q.Item3);
        var frames = link.Frames().Where(f => at.Frames.Any(w =>
            (Same((f.X1, f.Y1, f.Z1), (w.X1, w.Y1, w.Z1)) && Same((f.X2, f.Y2, f.Z2), (w.X2, w.Y2, w.Z2))) ||
            (Same((f.X1, f.Y1, f.Z1), (w.X2, w.Y2, w.Z2)) && Same((f.X2, f.Y2, f.Z2), (w.X1, w.Y1, w.Z1))))).Select(f => f.Name).ToList();
        var areas = link.Areas().Where(a => a.Corners.Count > 0 && at.Areas.Any(w => Same(Centre(a.Corners), w))).Select(a => a.Name).ToList();
        return (frames, areas);
    }

    private static void Work(CsiProgram program, string folder, string file, bool mustAnalyse, bool analyseAsked, IReadOnlyList<string> kinds, Shapes? only, bool keepOpen, AnalysisModel? check, bool stopOnErrors)
    {
        IScratchWindow? window = null;
        Packs.PackHelpers? helpers = null;
        var mon = _mon!;
        var ctx = new FieldContext(program, folder, _model!);
        ctx.Offer(ProgramTools.Chat(program));
        // The monitor's eyes while the job runs: the program's analysis log as it is written, the boxes the run
        // answered, and a box nobody answered when the program goes quiet. Every second, on a thread of its own, so a
        // program busy in a long call is still watched.
        var tail = new LogTail();
        using var watch = new Timer(_ => Watch(mon, tail), null, 1000, 1000);
        try
        {
            // The program the last run left open is closed first: it holds a licence and has the run's files open.
            CloseKept();
            if (!Check(mon, program, check, stopOnErrors)) return;
            if (Cancelled()) return;
            // The analysed file can be gone by the time the job starts (0.4.57 full test: SAP2000 was given
            // cpm-field-test-3795ee.sdb, which no longer existed, and waited on its "Open Model File" box). The model is
            // then written again and analysed first, never handed to the program as a missing file.
            if (!mustAnalyse && !File.Exists(file))
            {
                try { file = _model!.WriteForRun(Path.Combine(folder, RunName(_model) + ".e2k")); }
                catch (EtabsException ex) { Fail($"The analysed {program.Name} model file is gone and the model could not be written again: {ex.Message}"); return; }
                mustAnalyse = true;
                Say($"The analysed model file is gone, so {program.Name} analyses the model again first");
            }
            mon.ModelFile = file;
            mon.Begin("start", Path.GetFileName(file));
            Say(mustAnalyse
                ? $"Starting {program.Name} in a window of its own, in the background (yours, if open, is not touched)"
                : $"Starting {program.Name} on the model it analysed ({Path.GetFileName(file)}), in the background");
            // The program's copy is named after the engineer's own file (Ahmed 2026-11-28: "why is it not the same file
            // name"), with ScratchProgram.RunMark so its windows are still told apart from any of the engineer's.
            var copyName = mustAnalyse ? ScratchProgram.RunCopyName(RunName(_model!)) : null;
            if (copyName is not null) ClearCopy(folder, copyName);
            window = ScratchProgram.Start(program, ctx, file, asIs: !mustAnalyse, name: copyName);
            if (window is null)
            {
                // A file the program rejected is the headline, in its own words (Ahmed's real model, 2026-11-27: the report
                // led with "the model must be saved first" while ETABS had said "May not be a valid ETABS 23.2.0 text file").
                var open = ctx.Steps.FirstOrDefault(s => !s.Ok && !s.Skipped && s.Name.StartsWith("open ", StringComparison.Ordinal));
                Fail(open is not null ? $"{program.Name} could not open the model: {open.Detail}" : $"{program.Name} could not be started with the model. {Failed(ctx)}");
                return;
            }
            lock (Gate) { _starts++; _window = window; }
            if (window.ModelPath.Length > 0) mon.ModelFile = window.ModelPath;
            tail.File = RunLog.FileOf(mon.ModelFile); mon.LogFile = tail.File;
            if (window is ScratchProgram sp) Say(sp.Hide() ? $"{program.Name} is running hidden" : $"{program.Name} is running (it could not be hidden)");
            mon.End("start", "done", $"{program.Name} opened {Path.GetFileName(mon.ModelFile)}");
            if (Cancelled()) return;
            ctx.Link = window.Link;
            if (!mustAnalyse && !Locked(window.Link))
            {
                // The saved analysis did not come back with the file: it is run again rather than designing without one.
                lock (Gate) Lines.Add($"{program.Name} opened the analysed model without its results, so the analysis runs again.");
                mustAnalyse = true;
            }
            if (mustAnalyse && SlabTables.Any(_model!.Model))
            {
                // What the .e2k cannot carry (slab line releases, slab rebar) goes in through the program's tables first.
                Say($"Putting the slab line releases and slab rebar into {program.Name} through its tables");
                foreach (var line in SlabTables.PutInto(window.Link, _model!.Model)) lock (Gate) Lines.Add(line);
            }
            if (mustAnalyse && RunCases.NotRun(_model!.Model) is { Count: > 0 } notRun)
            {
                // Analyze > Set Load Cases to Run: the cases set not to run are left out of this run.
                Say($"Setting {notRun.Count} load case{(notRun.Count == 1 ? "" : "s")} not to run");
                var set = ctx.Tool("set_run_cases", JsonSerializer.Serialize(new { all = true, not_run = notRun.ToArray() }));
                lock (Gate) Lines.Add(set is null
                    ? $"{program.Name} would not take the cases set not to run, so every case runs. {Failed(ctx)}"
                    : $"Not run, as Set Load Cases to Run says: {string.Join(", ", notRun)}.");
            }
            if (mustAnalyse)
            {
                mon.Begin("analysis", mon.CaseCount > 0 ? $"{mon.CaseCount} load cases" : "");
                // A new run writes a new log: what an earlier run left is not this run's.
                tail.Restart();
                Say("Running the analysis");
                if (ctx.Tool("run_analysis") is null)
                {
                    tail.Read(mon);
                    Fail($"The analysis did not run. {Failed(ctx)}");
                    return;
                }
                tail.Read(mon);
                var trouble = RunLog.Trouble(mon.LogLines);
                mon.End("analysis", trouble.Any(t => t?["level"]?.GetValue<string>() is "error" or "warning") ? "warning" : "done",
                    trouble.Count > 0 ? string.Join("; ", trouble.Select(t => t?["meaning"]?.GetValue<string>())) : RunLog.Complete(mon.LogLines) ? "analysis complete" : "");
                var saved = window is ScratchProgram s2 ? s2.ModelPath : "";
                string before;
                lock (Gate) { before = _analysedFile; _analysedModel = _model; _analysedRevision = _startRevision; _analysedFile = saved; _analysedProgram = program.Key; }
                Forget(before, saved);
            }
            else
            {
                var why = analyseAsked ? $"The model has not changed since its analysis in {program.Name}, so it is not analysed again." : $"The analysis in {program.Name} is kept; only the design runs.";
                lock (Gate) Lines.Add(why);
                mon.Skip("analysis", why);
            }
            if (Cancelled()) return;
            // The helper windows for reading the results start now, in the background, while a design runs.
            // As many as the model's size needs: none for a small model (Ahmed 2026-11-27).
            var need = 0;
            try { need = Math.Min(Packs.PackHelpers.Wanted, Packs.PackHelpers.ForSize(_model!.Frames().Count, _model.Areas().Count, _model.Model.LoadCases.Count + _model.Model.Combos.Count)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { need = 0; }
            if (need > 0 && window.ModelPath.Length > 0 && File.Exists(window.ModelPath))
            {
                helpers = Packs.PackHelpers.Open(program, window.ModelPath, need);
                lock (Gate) Lines.Add($"The model is large: {helpers.Asked} more hidden {program.Name} window{(helpers.Asked == 1 ? "" : "s")} on copies of it will read results beside this one.");
            }
            if (kinds.Count > 0)
            {
                mon.Begin("design", string.Join(", ", kinds.Select(k => Kinds[program.Key].First(x => x.Kind == k).Name)));
                if (only is not null)
                {
                    // The selected objects are selected in the program, as an engineer would before Start Design/Check.
                    var (frames, areas) = Find(window.Link, only);
                    ctx.Tool("select_objects", JsonSerializer.Serialize(new { frames, areas }));
                    lock (Gate) Lines.Add($"Selected in {program.Name} for the design: {frames.Count} of {only.Frames.Count} frames, {areas.Count} of {only.Areas.Count} areas.");
                }
                // SAP2000's .s2k has no place for the design codes chosen in the Modeler (Design > ... > View/Revise
                // Preferences): they are set in the program before the design. ETABS and SAFE read them from their file.
                if (program.Key == CsiProgram.Sap2000.Key)
                {
                    var codes = kinds.Select(k => (k, c: ProgramForms.Code(_model!.Model, k))).Where(x => x.c.Length > 0).ToDictionary(x => x.k, x => x.c);
                    if (codes.Count > 0 && ctx.Tool("design_codes", JsonSerializer.Serialize(new { set = codes })) is not null)
                        lock (Gate) Lines.Add("Design codes set: " + string.Join(", ", codes.Select(c => $"{c.Key} {c.Value}")) + ".");
                }
                Say("Running the design: " + string.Join(", ", kinds.Select(k => Kinds[program.Key].First(x => x.Kind == k).Name)) + (only is not null ? " (selected objects)" : ""));
                if (ctx.Tool("run_design", DesignArgs(program, kinds)) is null)
                {
                    var why = Failed(ctx);
                    lock (Gate) Lines.Add("The design did not run: " + why);
                    mon.End("design", "failed", why);
                }
                else mon.End("design");
            }
            else mon.Skip("design", "no design was asked");
            if (Cancelled()) return;
            mon.Begin("results");
            Say("Reading every result and design table");
            // The pack beside the model (the same file every run, as the pack written after a run in the program), with
            // the comments a reviewer made on the last one carried in.
            var (packFile, pageFile) = PackFiles(_model!);
            var comments = Packs.PackLife.Comments(packFile);
            // One read session for the pack and, in the field test, the check after it of what the pack lost.
            using var session = PackSession.Start(window.Link);
            Pack.Built built;
            // Up to two more hidden windows of the program on copies of the analysed model read result tables beside this
            // one (Ahmed 2026-11-27: three windows on one licence). They were started right after the analysis.
            try { built = Pack.Build(window.Link, [], [], [], null, comments, by: "CSI ProMax Engine", instabilities: true, helpers: helpers); }
            finally { if (helpers is not null) { Say($"Closing the {helpers.Asked} helper window{(helpers.Asked == 1 ? "" : "s")}"); helpers.Dispose(); helpers = null; } }
            try { Packs.PackLife.Write(packFile, pageFile, built.Zip, built.Content); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (Gate) Lines.Add($"The review pack could not be saved to {packFile}: {ex.Message}"); packFile = ""; }
            lock (Gate)
            {
                _pack = built.Content; _packFile = packFile; _packPage = packFile.Length > 0 ? pageFile : ""; _packModel = _model; _packRevision = _startRevision;
                Lines.Add($"{built.Tables.Count} tables read ({built.Frames} frames, {built.Areas} areas)" + (packFile.Length > 0 ? $"; review pack: {packFile}" + (comments.Count > 0 ? $" ({comments.Count} comments kept)" : "") : ""));
            }
            var design = RunLog.DesignTrouble(built.Content["tables"] as JsonObject);
            mon.End("results", design.Count > 0 ? "warning" : "done", $"{built.Tables.Count} tables" + (design.Count > 0 ? "; the design has members to look at" : ""));
            // Ahmed 2026-11-27: the results come back complete. The field test checks it against the program, still open,
            // after the results step's time is taken (the check reads every table the program lists, empty ones too).
            if (CheckLoss)
            {
                List<string> lost;
                try { lost = Pack.Missing(window.Link, built.Content, [], []); }
                catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { lost = ["the check could not be made: " + ex.Message]; }
                lock (Gate) _lost = lost;
            }
            if (WhileOpen is { } probe)
            {
                JsonObject found;
                try { found = probe(window.Link); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { found = new JsonObject { ["error"] = ex.Message }; }
                lock (Gate) _probe = found;
            }
        }
        catch (OutOfMemoryException)
        {
            // 0.4.68 full test: a large model's results ran CSI ProMax out of memory on this thread, and the whole program
            // went down with it. The results are let go, the run is a failure that says so, and the program is closed below.
            lock (Gate) _pack = null;
            CsiProMax.Assistant.OutOfMemory.Free();
            Fail($"The results were too large for the memory CSI ProMax has: the run stopped while reading them ({Etabs.ComEtabs.LastRead}). {program.Name} is closed and its licence freed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            tail.Read(mon);
            Fail(_cancel ? "Cancelled." : $"The run stopped: {ex.Message} {Failed(ctx)}");
        }
        finally
        {
            // Helper windows a stopped run left are closed first.
            if (helpers is not null) try { helpers.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            // Closed at once, whatever happened: the licence is used only while the job runs. Unless Settings > Runs
            // says to keep it open (Ahmed 2026-11-27, for now): it is then shown with the analysed model, the engineer's
            // to look at, and closed before the next run. A cancelled or stopped run is always closed.
            if (window is ScratchProgram kept && keepOpen && !_cancel && kept.Reveal())
            {
                try { kept.PutBackPlugIns(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                lock (Gate)
                {
                    _kept = kept; _keptName = program.Name; _keptOpen = true; _closed = false;
                    Lines.Add($"{program.Name} was left open with the analysed model (Settings > Runs: keep the program open after a run). It holds a licence until you close it; the next run closes it first.");
                }
                mon.Skip("close", $"{program.Name} was left open with the analysed model (Settings > Runs)");
            }
            else if (window is not null)
            {
                mon.Begin("close");
                Say($"Closing {program.Name}");
                var said = "";
                try { said = window.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { said = ex.Message; }
                try { window.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                // Closing is a must (Ahmed 2026-11-27): the licence is free only once the program's process has ended,
                // so that is checked, never assumed from the window saying yes.
                var ended = Ended(window);
                lock (Gate)
                {
                    _closed = ended;
                    var why = said.Length > 0 ? " " + char.ToUpperInvariant(said[0]) + said[1..] + "." : "";
                    Lines.Add(ended ? $"{program.Name} was closed; its licence is free." + why : $"{program.Name} did not end after the job, so its licence may still be held." + why);
                    if (!ended && _error.Length == 0) _error = $"{program.Name} did not end after the job.{why}";
                }
                mon.End("close", ended ? "done" : "failed", ended ? "the licence is free" : said);
            }
            else { lock (Gate) _closed = true; mon.Skip("close", "the program was not started"); }
            watch.Dispose();
            tail.Read(mon);
            Notices(mon, window);
            lock (Gate) { _window = null; }
            mon.Report = Report(mon, program);
            mon.Finish();
            lock (Gate) { _running = false; _done = true; _phase = _error.Length > 0 ? (_cancel ? "Cancelled" : "Stopped") : "Done"; }
        }
    }

    /// <summary>
    /// The instability check before the program is started (the pre-run checker, Analyze > Check Model for
    /// Instabilities): what it finds is in the monitor and the Run Report. False when the run must stop here (errors
    /// found and the Run form asked to stop on them): the program is then never started, so no licence is used.
    /// </summary>
    private static bool Check(RunMonitor mon, CsiProgram program, AnalysisModel? check, bool stopOnErrors)
    {
        if (check is null) return true;
        mon.Begin("check");
        Say("Checking the model for instabilities");
        try
        {
            var report = StabilityCheck.Run(check, new StabilityCheck.Options(MostFindings: 200));
            mon.Instabilities = StabilityTools.Json(report, program, modeler: true);
            var summary = mon.Instabilities["summary"]?.GetValue<string>() ?? "";
            lock (Gate) Lines.Add(summary);
            if (report.Errors > 0 && stopOnErrors)
            {
                mon.End("check", "failed", $"{report.Errors} error(s), {report.Warnings} warning(s)");
                Fail($"The instability check found {report.Errors} error(s) that would stop the analysis or make it unstable, so {program.Name} was not started (no licence was used). Fix them (Show on model), or press Run anyway.");
                return false;
            }
            mon.End("check", report.Errors + report.Warnings > 0 ? "warning" : "done", report.Errors + report.Warnings > 0 ? $"{report.Errors} error(s), {report.Warnings} warning(s)" : "no problems found");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { mon.End("check", "warning", "the check could not be made: " + ex.Message); }
        return true;
    }

    /// <summary>True when Cancel was pressed: the job stops here (the program, if started, is ended in the finally).</summary>
    private static bool Cancelled()
    {
        if (!_cancel) return false;
        Fail("Cancelled.");
        return true;
    }

    /// <summary>The program's analysis log read as it grows.</summary>
    private sealed class LogTail
    {
        private readonly object _gate = new();
        private long _offset;
        private string _partial = "";
        public string File { get; set; } = "";

        /// <summary>From the end of what is there now: the log an earlier run wrote is not this one's.</summary>
        public void Restart()
        {
            lock (_gate)
            {
                _partial = "";
                try { _offset = File.Length > 0 && System.IO.File.Exists(File) ? new FileInfo(File).Length : 0; } catch (IOException) { _offset = 0; }
            }
        }

        public void Read(RunMonitor mon)
        {
            List<string> lines;
            lock (_gate)
            {
                if (File.Length == 0) return;
                // The program may start a log of another case of the name (ETABS writes MODEL.LOG; SAP2000 MODEL.LOG or .OUT).
                if (!System.IO.File.Exists(File) && RunLog.FileOf(mon.ModelFile) is { Length: > 0 } other && System.IO.File.Exists(other)) { File = other; mon.LogFile = other; _offset = 0; }
                lines = RunLog.Tail(File, ref _offset, ref _partial);
            }
            if (lines.Count > 0) mon.Log(lines);
        }
    }

    private static int _seenNotices;

    /// <summary>One look of the monitor: new log lines, the boxes answered, and a box the program waits on.</summary>
    private static int _watching;

    private static void Watch(RunMonitor mon, LogTail tail)
    {
        // One look at a time: a look held by a slow window must not pile up behind the timer.
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        try
        {
            tail.Read(mon);
            IScratchWindow? window;
            lock (Gate) window = _window;
            Notices(mon, window);
            if (window is not ScratchProgram sp) { mon.Wait(""); return; }
            // What the program's own run window shows (ETABS's "Analyzing" window and its log box), and the windows it
            // has open: the Engine's run window shows them live (Ahmed 2026-11-28: "it is important to see what is happening").
            if (sp.RunWindow() is { } screen) mon.Screen(screen.Title, screen.Lines);
            var shown = sp.OpenWindows();
            mon.Windows(shown.Count == 0 ? "" : string.Join(" | ", shown));
            var quiet = (DateTime.Now - mon.LastHeard).TotalSeconds;
            if (quiet < 30) { mon.Wait(""); return; }
            // Quiet for half a minute: what the program shows is said, so a box nobody answered is never a silent wait.
            var open = sp.OpenWindows();
            mon.Wait(open.Count == 0 ? "" : open.Any(t => t.Contains("licen", StringComparison.OrdinalIgnoreCase))
                ? $"{mon.Program} is showing a licence message: " + string.Join(" | ", open) + ". Press Show program window and answer it there."
                : $"{mon.Program} has written nothing for {quiet:0} s. Its open windows: " + string.Join(" | ", open) + ". If one waits for an answer, press Show program window.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next look tries again */ }
        finally { Volatile.Write(ref _watching, 0); }
    }

    private static void Notices(RunMonitor mon, IScratchWindow? window)
    {
        if (window is not ScratchProgram sp) return;
        var all = sp.Answered();
        lock (Gate)
        {
            if (all.Count < _seenNotices) _seenNotices = 0;
            foreach (var n in all.Skip(_seenNotices)) { mon.Notice(n); Lines.Add($"{mon.Program} showed a box, answered by the run: {n}"); }
            _seenNotices = all.Count;
        }
    }

    /// <summary>
    /// The Run Report: whether the job finished, the step it stopped at, what the program said (its log, its boxes) in
    /// plain words with what to do, the instability check's findings, the design's members that do not pass, and the
    /// files to open. Made for every job, so a run that finished with warnings says so too.
    /// </summary>
    internal static JsonObject Report(RunMonitor mon, CsiProgram program)
    {
        string error;
        lock (Gate) error = _error;
        var steps = mon.All;
        var failed = steps.FirstOrDefault(s => s.State == "failed");
        var trouble = RunLog.Trouble(mon.LogLines);
        if (!mon.Cancelled && RunLog.Plain(error) is { } plain && !trouble.Any(t => t?["kind"]?.GetValue<string>() == plain["kind"]?.GetValue<string>())) trouble.Insert(0, plain);
        JsonArray design;
        lock (Gate) design = RunLog.DesignTrouble((_pack ?? new JsonObject())["tables"] as JsonObject);
        var inst = mon.Instabilities;
        var instErrors = inst?["errors"]?.GetValue<int>() ?? 0;
        var instWarnings = inst?["warnings"]?.GetValue<int>() ?? 0;
        var ok = error.Length == 0;
        var warn = trouble.Count > 0 || design.Count > 0 || instErrors + instWarnings > 0 || mon.Notices.Count > 0;
        var headline = mon.Cancelled ? $"Cancelled. {program.Name} was ended without saving; your model is as it was."
            : !ok ? $"The run stopped at \"{failed?.Name ?? "the run"}\"."
            : warn ? $"The run finished in {program.Name}, with things to look at below."
            : $"The run finished in {program.Name} with no warnings.";
        var report = new JsonObject
        {
            ["ok"] = ok, ["cancelled"] = mon.Cancelled, ["headline"] = headline, ["error"] = error,
            ["step"] = failed?.Name ?? "", ["program"] = program.Name,
            ["trouble"] = trouble, ["design"] = design, ["notices"] = new JsonArray(mon.Notices.Select(n => (JsonNode)n).ToArray()),
            ["log_file"] = mon.LogFile, ["model_file"] = mon.ModelFile,
            ["can_open"] = mon.ModelFile.Length > 0 && File.Exists(mon.ModelFile),
            ["log_tail"] = new JsonArray(mon.LogLines.TakeLast(60).Select(l => (JsonNode)l).ToArray()),
        };
        if (inst is not null && instErrors + instWarnings > 0) report["instabilities"] = inst.DeepClone();
        return report;
    }

    /// <summary>The Run Monitor's Cancel: the job stops, and the program it started is ended at once (its licence freed).</summary>
    public static JsonObject Cancel()
    {
        IScratchWindow? window;
        RunMonitor? mon;
        lock (Gate)
        {
            if (!_running) return new JsonObject { ["ok"] = false, ["error"] = "No run is going." };
            _cancel = true; window = _window; mon = _mon;
        }
        mon?.Stop("cancelled", cancelled: true);
        var said = window is ScratchProgram sp ? sp.Kill() : $"{mon?.Program ?? "The program"} is not open yet; the run stops as soon as it answers, and it is ended then";
        lock (Gate) Lines.Add("Cancel pressed: " + said + ".");
        return new JsonObject { ["ok"] = true, ["said"] = said };
    }

    /// <summary>The Run Monitor's Show program window: the hidden program comes on the screen, to read and answer a box there.</summary>
    public static JsonObject ShowProgram()
    {
        IScratchWindow? window;
        lock (Gate) window = _window;
        if (window is not ScratchProgram sp) return new JsonObject { ["ok"] = false, ["error"] = "The program of the run is not open." };
        var shown = sp.Show();
        return shown ? new JsonObject { ["ok"] = true, ["said"] = $"{_program} is on the screen. Answer what it shows there; the run carries on (and closes it after the job)." }
            : new JsonObject { ["ok"] = false, ["error"] = $"{_program} did not come on the screen: it may be held by a box. Look for its window on the taskbar." };
    }

    /// <summary>
    /// The Run Report's Open in the program: a copy of the model as the job ran it (its results files too) opens in the
    /// real program, visible, to inspect what it says. A copy, so nothing done there changes the run's own files.
    /// </summary>
    public static JsonObject OpenInProgram(Func<CsiProgram, string>? apiFile = null)
    {
        RunMonitor? mon;
        lock (Gate) { if (_running) return new JsonObject { ["ok"] = false, ["error"] = "The run is still going: wait for it, or Cancel it first." }; mon = _mon; }
        if (mon is null || mon.ModelFile.Length == 0 || !File.Exists(mon.ModelFile)) return new JsonObject { ["ok"] = false, ["error"] = "The run left no model file to open." };
        var program = CsiProgram.All.FirstOrDefault(p => p.Name == mon.Program) ?? CsiProgram.Etabs;
        var api = (apiFile ?? (p => EtabsApi.FindApiFile(p)))(program);
        var exe = Path.Combine(Path.GetDirectoryName(api) ?? "", program.ProcessName + ".exe");
        if (!File.Exists(exe)) return new JsonObject { ["ok"] = false, ["error"] = $"{program.Name} was not found on this PC ({exe})." };
        string copy;
        try { copy = CopyForInspection(mon.ModelFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new JsonObject { ["ok"] = false, ["error"] = "The model could not be copied to open: " + ex.Message }; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "\"" + copy + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(copy)! }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new JsonObject { ["ok"] = false, ["error"] = $"{program.Name} could not be started: {ex.Message}" }; }
        return new JsonObject { ["ok"] = true, ["file"] = copy, ["said"] = $"{program.Name} is opening a copy of the model as it ran ({copy}). It is yours to look at; the run's own files are not touched." };
    }

    /// <summary>The model file and its sidecar files (same name, any extension: results, log) copied into "inspect HHmmss" beside them.</summary>
    internal static string CopyForInspection(string modelFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(modelFile))!;
        var stem = Path.GetFileNameWithoutExtension(modelFile);
        var to = Path.Combine(dir, "inspect " + DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        return Path.Combine(to, Path.GetFileName(modelFile));
    }

    /// <summary>The Run Report's Open run folder: Windows Explorer on the run's folder, the model file picked.</summary>
    public static JsonObject OpenFolder()
    {
        RunMonitor? mon;
        lock (Gate) mon = _mon;
        var file = mon?.ModelFile ?? "";
        if (file.Length == 0) return new JsonObject { ["ok"] = false, ["error"] = "No run yet." };
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", File.Exists(file) ? $"/select,\"{file}\"" : $"\"{Path.GetDirectoryName(file)}\"") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>Closes the program the last run left open, if it is still there, and says so.</summary>
    private static void CloseKept()
    {
        IScratchWindow? kept; string name;
        lock (Gate) { kept = _kept; name = _keptName; _kept = null; }
        if (kept is null) return;
        if (kept is ScratchProgram sp && !sp.Running) { try { kept.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { } return; }
        Say($"Closing the {name} the last run left open");
        try { kept.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { kept.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        if (!Ended(kept)) lock (Gate) Lines.Add($"The {name} the last run left open did not end; close it yourself without saving.");
    }

    /// <summary>True when the job's program process has ended (waited for up to a minute after it was told to close).</summary>
    internal static bool Ended(IScratchWindow window) => window is not ScratchProgram sp || sp.Ended(TimeSpan.FromMinutes(1));

    /// <summary>The files of an earlier analysis this one replaces (the program's model and its result files, one name), in the run folder only.</summary>
    private static void Forget(string before, string now)
    {
        if (before.Length == 0 || string.Equals(before, now, StringComparison.OrdinalIgnoreCase)) return;
        var stem = Path.GetFileNameWithoutExtension(before);
        if (!(stem.StartsWith("cpm-field-test-", StringComparison.Ordinal) || stem.EndsWith(ScratchProgram.RunMark, StringComparison.Ordinal)) || Path.GetDirectoryName(before) is not { } dir || !Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
            try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The files an earlier run left under the copy's name (its model and its analysis files), in the run folder only:
    /// the new copy is saved over them, and an old analysis must never be taken for this one's.
    /// </summary>
    internal static void ClearCopy(string folder, string copyName)
    {
        if (!copyName.EndsWith(ScratchProgram.RunMark, StringComparison.Ordinal) || !Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder, copyName + ".*"))
            if (string.Equals(Path.GetFileNameWithoutExtension(f), copyName, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The pack and its Viewer page: beside the model's own file, named after it; in the run folder for a model never saved.</summary>
    internal static (string Pack, string Page) PackFiles(OfflineEtabs model) => model.Path.Length > 0
        ? (Packs.AutoPack.PackFile(model.Path), Packs.AutoPack.PageFile(model.Path))
        : (Path.Combine(RunFolder(model), Safe(model.Model.Title) + ".cpmx"), Path.Combine(RunFolder(model), Safe(model.Model.Title) + " viewer.html"));

    /// <summary>
    /// The model changed after its run (a hand edit, the chat, Undo): it has no results any more, so its pack is removed at
    /// once, as the program deletes its results when its model is unlocked. Nothing happens when the pack is of this
    /// model as it is. Said in the run's lines; what could not be removed (held open elsewhere) is said too.
    /// </summary>
    public static JsonObject Changed(OfflineEtabs model)
    {
        lock (Gate)
        {
            if (_running || !ReferenceEquals(_packModel, model) || _packRevision == model.Revision || _packFile.Length == 0) return new JsonObject { ["removed"] = "" };
        }
        return Unlock(model, "the model changed");
    }

    /// <summary>Analyze > Unlock Model: the results are removed (the pack and page beside the model, comments kept), and the next design analyses again.</summary>
    public static JsonObject Unlock(OfflineEtabs model, string why = "the model was unlocked")
    {
        string pack, page;
        lock (Gate)
        {
            if (_running) return new JsonObject { ["ok"] = false, ["error"] = $"A run in {_program} is still going ({_phase})." };
            (pack, page) = _packFile.Length > 0 && ReferenceEquals(_packModel, model) ? (_packFile, _packPage) : PackFiles(model);
            _packFile = ""; _packPage = ""; _packModel = null; _packRevision = -1;
            if (ReferenceEquals(_analysedModel, model)) { _analysedModel = null; _analysedRevision = -1; }
        }
        var had = File.Exists(pack);
        var left = Packs.PackLife.Remove(pack, page);
        var said = !had ? "" : left.Count == 0 ? $"The results were removed ({why}): {Path.GetFileName(pack)} is gone; its comments are kept in {Path.GetFileName(Packs.PackLife.CommentsFile(pack))} when it had any."
            : $"The results were removed ({why}), but {string.Join(", ", left)} could not be.";
        if (said.Length > 0) lock (Gate) Lines.Add(said);
        return new JsonObject { ["ok"] = true, ["removed"] = had && left.Count == 0 ? pack : "", ["left"] = new JsonArray(left.Select(l => (JsonNode)l).ToArray()), ["said"] = said };
    }

    private static bool Locked(ICsiLink link)
    {
        try { return link.Info().Locked; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static string Failed(FieldContext ctx) => string.Join(" ", ctx.Steps.Where(s => !s.Ok && !s.Skipped).Select(s => s.Name + ": " + s.Detail));

    private static void Fail(string why)
    {
        // A licence message is the engineer's to answer: said plainly, never pressed.
        var licence = why.Contains("licen", StringComparison.OrdinalIgnoreCase) || why.Contains("did not start within", StringComparison.OrdinalIgnoreCase);
        lock (Gate) { _error = why + (licence ? " If a licence message is showing in the program's window, answer it there, close that window and run again." : ""); Lines.Add(_error); }
        _mon?.Stop(why, cancelled: _cancel);
    }

    /// <summary>What the page shows while it waits, and the results once the run is done (taken once).</summary>
    public static JsonObject Status(bool take)
    {
        lock (Gate)
        {
            var o = new JsonObject
            {
                ["running"] = _running, ["done"] = _done, ["program"] = _program, ["phase"] = _phase, ["error"] = _error,
                ["lines"] = new JsonArray(Lines.Select(l => (JsonNode)l).ToArray()), ["pack_file"] = _packFile,
                ["probe"] = _probe?.DeepClone(),
                ["lost"] = _lost is null ? null : new JsonArray(_lost.Select(l => (JsonNode)l).ToArray()),
                ["model_changed"] = _model is not null && _model.Revision != _startRevision,
                // Whether the last analysis is of the model as it is now: a design then needs no new analysis.
                ["analysed"] = _analysedModel is not null && _analysedRevision == _analysedModel.Revision && File.Exists(_analysedFile),
                ["starts"] = _starts,
                ["analysed_file"] = _analysedFile,
                ["closed"] = _closed,
                ["kept_open"] = _keptOpen,
                ["monitor"] = _mon?.Json(),
                ["keep_open"] = KeepOpenSetting(),
                ["kinds"] = new JsonObject(Kinds.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonArray(p.Value.Select(k => (JsonNode)new JsonObject { ["kind"] = k.Kind, ["name"] = k.Name }).ToArray())))),
            };
            if (take && _done && _pack is not null) { o["pack"] = Pack.ForWindow(_pack); _pack = null; }
            return o;
        }
    }
}
