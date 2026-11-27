using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using CsiProMax.Model;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

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
    private static OfflineEtabs? _model;

    // The last analysis: the model it was of, its revision, and the program's own file holding its results.
    private static OfflineEtabs? _analysedModel;
    private static int _analysedRevision = -1;
    private static string _analysedFile = "", _analysedProgram = "";
    private static int _starts;
    /// <summary>Whether the last job's program was seen to end (its process gone), so its licence is free.</summary>
    private static bool _closed;

    // The pack written by the last run: of which model, at which revision (removed when the model moves on).
    private static OfflineEtabs? _packModel;
    private static int _packRevision = -1;
    private static string _packPage = "";

    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Thread Worker = MakeWorker();

    private static Thread MakeWorker()
    {
        var t = new Thread(() => { foreach (var job in Queue.GetConsumingEnumerable()) { try { job(); } catch (Exception ex) when (ex is not OutOfMemoryException) { } } }) { IsBackground = true, Name = "CSI ProMax Engine run" };
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
    /// and analysed). <paramref name="kinds"/>: the designs to run after it.
    /// </summary>
    public static JsonObject Start(OfflineEtabs model, bool analyse, IReadOnlyList<string> kinds, Func<CsiProgram, string>? apiFile = null, IReadOnlyList<string>? selected = null)
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
            var current = AnalysedNow(model, program);
            string file = "";
            var folder = RunFolder(model);
            if (!current)
                try { file = model.WriteForRun(Path.Combine(folder, Safe(model.Model.Title) + ".e2k")); }
                catch (EtabsException ex) { return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
            Lines.Clear(); _error = ""; _pack = null; _done = false; _running = true; _closed = false;
            _program = program.Name; _model = model; _startRevision = model.Revision;
            _phase = $"Starting {program.Name} in the background";
            if (file.Length > 0) Lines.Add($"The model was written for {program.Name}: {file}");
            // A design on its own still needs the analysis behind it: a model not analysed as it is now is analysed first.
            var mustAnalyse = !current;
            var analysed = _analysedFile;
            var only = kinds.Count > 0 && selected is { Count: > 0 } ? Places(model, selected) : null;
            if (only is not null) Lines.Add($"The design is for the {selected!.Count} selected objects only.");
            Post(() => Work(program, folder, mustAnalyse ? file : analysed, mustAnalyse, analyse, kinds, only));
            return new JsonObject { ["ok"] = true, ["program"] = program.Name, ["file"] = file, ["analyses"] = mustAnalyse };
        }
    }

    /// <summary>Whether the program's saved analysis is of this model as it is now (same model, same revision, file still there).</summary>
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

    private static void Work(CsiProgram program, string folder, string file, bool mustAnalyse, bool analyseAsked, IReadOnlyList<string> kinds, Shapes? only)
    {
        IScratchWindow? window = null;
        var ctx = new FieldContext(program, folder, _model!);
        ctx.Offer(ProgramTools.Chat(program));
        try
        {
            // The analysed file can be gone by the time the job starts (0.4.57 full test: SAP2000 was given
            // cpm-field-test-3795ee.sdb, which no longer existed, and waited on its "Open Model File" box). The model is
            // then written again and analysed first, never handed to the program as a missing file.
            if (!mustAnalyse && !File.Exists(file))
            {
                try { file = _model!.WriteForRun(Path.Combine(folder, Safe(_model.Model.Title) + ".e2k")); }
                catch (EtabsException ex) { Fail($"The analysed {program.Name} model file is gone and the model could not be written again: {ex.Message}"); return; }
                mustAnalyse = true;
                Say($"The analysed model file is gone, so {program.Name} analyses the model again first");
            }
            Say(mustAnalyse
                ? $"Starting {program.Name} in a window of its own, in the background (yours, if open, is not touched)"
                : $"Starting {program.Name} on the model it analysed ({Path.GetFileName(file)}), in the background");
            window = ScratchProgram.Start(program, ctx, file, asIs: !mustAnalyse);
            if (window is null) { Fail($"{program.Name} could not be started with the model. {Failed(ctx)}"); return; }
            lock (Gate) _starts++;
            if (window is ScratchProgram sp) Say(sp.Hide() ? $"{program.Name} is running hidden" : $"{program.Name} is running (it could not be hidden)");
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
            if (mustAnalyse)
            {
                Say("Running the analysis");
                if (ctx.Tool("run_analysis") is null) { Fail($"The analysis did not run. {Failed(ctx)}"); return; }
                var saved = window is ScratchProgram s2 ? s2.ModelPath : "";
                string before;
                lock (Gate) { before = _analysedFile; _analysedModel = _model; _analysedRevision = _startRevision; _analysedFile = saved; _analysedProgram = program.Key; }
                Forget(before, saved);
            }
            else lock (Gate) Lines.Add(analyseAsked ? $"The model has not changed since its analysis in {program.Name}, so it is not analysed again." : $"The analysis in {program.Name} is kept; only the design runs.");
            if (kinds.Count > 0)
            {
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
                if (ctx.Tool("run_design", DesignArgs(program, kinds)) is null) lock (Gate) Lines.Add("The design did not run: " + Failed(ctx));
            }
            Say("Reading every result and design table");
            // The pack beside the model (the same file every run, as the pack written after a run in the program), with
            // the comments a reviewer made on the last one carried in.
            var (packFile, pageFile) = PackFiles(_model!);
            var comments = Packs.PackLife.Comments(packFile);
            var built = Pack.Build(window.Link, [], [], [], null, comments, by: "CSI ProMax Engine", instabilities: true);
            try { Packs.PackLife.Write(packFile, pageFile, built.Zip, Pack.ViewerHtml(built.Content)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (Gate) Lines.Add($"The review pack could not be saved to {packFile}: {ex.Message}"); packFile = ""; }
            lock (Gate)
            {
                _pack = built.Content; _packFile = packFile; _packPage = packFile.Length > 0 ? pageFile : ""; _packModel = _model; _packRevision = _startRevision;
                Lines.Add($"{built.Tables.Count} tables read ({built.Frames} frames, {built.Areas} areas)" + (packFile.Length > 0 ? $"; review pack: {packFile}" + (comments.Count > 0 ? $" ({comments.Count} comments kept)" : "") : ""));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail($"The run stopped: {ex.Message} {Failed(ctx)}"); }
        finally
        {
            // Closed at once, whatever happened: the licence is used only while the job runs.
            if (window is not null)
            {
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
            }
            lock (Gate) { _running = false; _done = true; _phase = _error.Length > 0 ? "Stopped" : "Done"; }
        }
    }

    /// <summary>True when the job's program process has ended (waited for up to a minute after it was told to close).</summary>
    internal static bool Ended(IScratchWindow window) => window is not ScratchProgram sp || sp.Ended(TimeSpan.FromMinutes(1));

    /// <summary>The files of an earlier analysis this one replaces (the program's model and its result files, one name), in the run folder only.</summary>
    private static void Forget(string before, string now)
    {
        if (before.Length == 0 || string.Equals(before, now, StringComparison.OrdinalIgnoreCase)) return;
        var stem = Path.GetFileNameWithoutExtension(before);
        if (!stem.StartsWith("cpm-field-test-", StringComparison.Ordinal) || Path.GetDirectoryName(before) is not { } dir || !Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
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
                ["model_changed"] = _model is not null && _model.Revision != _startRevision,
                // Whether the last analysis is of the model as it is now: a design then needs no new analysis.
                ["analysed"] = _analysedModel is not null && _analysedRevision == _analysedModel.Revision && File.Exists(_analysedFile),
                ["starts"] = _starts,
                ["closed"] = _closed,
                ["kinds"] = new JsonObject(Kinds.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonArray(p.Value.Select(k => (JsonNode)new JsonObject { ["kind"] = k.Kind, ["name"] = k.Name }).ToArray())))),
            };
            if (take && _done && _pack is not null) { o["pack"] = _pack; _pack = null; }
            return o;
        }
    }
}
