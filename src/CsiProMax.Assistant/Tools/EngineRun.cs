using System.IO;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// CSI ProMax Engine's Analyze > Run (Ahmed 2026-11-26): the model is written for its program, the program is started
/// in a window of its own (never the one the engineer has open), hidden, given the model, analysed (and designed when
/// asked); every result and design table comes back as a review pack beside the model, and the program is closed at
/// once. The program's licence is used only while it runs. The program's own notices (analysis complete, the
/// maintenance reminder, "really close?") are answered as the field test answers them; a licence message is never
/// answered, and the run says plainly that it is waiting on one.
/// </summary>
public static class EngineRun
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = [];
    private static string _phase = "", _program = "", _error = "", _packFile = "";
    private static bool _running, _done;
    private static int _startRevision;
    private static JsonObject? _pack;
    private static OfflineEtabs? _model;

    /// <summary>Starts a run on the Modeler's model. Called on the window's thread (it reads the model); the run goes on in the background.</summary>
    public static JsonObject Start(OfflineEtabs model, bool design, Func<CsiProgram, string>? apiFile = null)
    {
        var program = model.Model.Target switch { "sap2000" => CsiProgram.Sap2000, "safe" => CsiProgram.Safe, _ => CsiProgram.Etabs };
        lock (Gate)
        {
            if (_running) return new JsonObject { ["ok"] = false, ["error"] = $"A run in {_program} is still going ({_phase})." };
            var api = (apiFile ?? (p => EtabsApi.FindApiFile(p)))(program);
            if (!File.Exists(api))
                return new JsonObject { ["ok"] = false, ["error"] = $"{program.Name} is not installed on this PC, so the model cannot be run here. Save it (File > Save) and open the {(program.Key == "etabs" ? ".e2k" : program.Key == "sap2000" ? ".s2k" : ".f2k")} on a PC with {program.Name}, or run it there and open its review pack with File > Import > Results." };
            string file;
            var folder = RunFolder(model);
            try { file = model.WriteForRun(Path.Combine(folder, Safe(model.Model.Title) + ".e2k")); }
            catch (EtabsException ex) { return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
            Lines.Clear(); _error = ""; _pack = null; _packFile = ""; _done = false; _running = true;
            _program = program.Name; _model = model; _startRevision = model.Revision;
            _phase = $"Starting {program.Name} in the background";
            Lines.Add($"The model was written for {program.Name}: {file}");
            _ = Task.Run(() => Work(program, folder, file, design));
            return new JsonObject { ["ok"] = true, ["program"] = program.Name, ["file"] = file };
        }
    }

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

    private static void Work(CsiProgram program, string folder, string file, bool design)
    {
        IScratchWindow? window = null;
        var ctx = new FieldContext(program, folder, _model!);
        // The shared tools (run_analysis, run_design) first, then the program's own: as the field test offers them
        // ("There is no tool named run_analysis here", ETABS run on main 4eabf46).
        ctx.Offer(EtabsTools.All);
        ctx.Offer(ProgramTools.For(program));
        string Failed() => string.Join(" ", ctx.Steps.Where(s => !s.Ok && !s.Skipped).Select(s => s.Name + ": " + s.Detail));
        try
        {
            Say($"Starting {program.Name} in a window of its own, in the background (yours, if open, is not touched)");
            window = ScratchProgram.Start(program, ctx, file);
            if (window is null) { Fail($"{program.Name} could not be started with the model. {Failed()}"); return; }
            if (window is ScratchProgram sp) Say(sp.Hide() ? $"{program.Name} is running hidden" : $"{program.Name} is running (it could not be hidden)");
            ctx.Link = window.Link;
            Say("Running the analysis");
            if (ctx.Tool("run_analysis") is null) { Fail($"The analysis did not run. {Failed()}"); return; }
            if (design)
            {
                Say("Running the design");
                var kinds = program.Key == "safe" ? """{"kinds":["slab"]}""" : "{}";
                if (program.Key == "sap2000") kinds = """{"steel":true,"concrete":true}""";
                if (ctx.Tool("run_design", kinds) is null) lock (Gate) Lines.Add("The design did not run: " + Failed());
            }
            Say("Reading every result and design table");
            var built = Pack.Build(window.Link, [], [], [], null, null, by: "CSI ProMax Engine", instabilities: true);
            var packFile = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ".cpmx");
            try { File.WriteAllBytes(packFile, built.Zip); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (Gate) Lines.Add($"The review pack could not be saved to {packFile}: {ex.Message}"); packFile = ""; }
            lock (Gate) { _pack = built.Content; _packFile = packFile; Lines.Add($"{built.Tables.Count} tables read ({built.Frames} frames, {built.Areas} areas)" + (packFile.Length > 0 ? $"; review pack: {packFile}" : "")); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail($"The run stopped: {ex.Message} {Failed()}"); }
        finally
        {
            if (window is not null)
            {
                Say($"Closing {program.Name}");
                var said = "";
                try { said = window.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { said = ex.Message; }
                try { window.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                lock (Gate) Lines.Add(char.ToUpperInvariant(said[0]) + said[1..] + ".");
            }
            lock (Gate) { _running = false; _done = true; _phase = _error.Length > 0 ? "Stopped" : "Done"; }
        }
    }

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
            };
            if (take && _done && _pack is not null) { o["pack"] = _pack; _pack = null; }
            return o;
        }
    }
}
