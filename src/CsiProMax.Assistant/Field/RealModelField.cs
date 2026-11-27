using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// Real models, in and out with nothing lost (Ahmed 2026-11-27: "it should be nothing lost whenever we get into etabs or
/// out or sap or safe it should be exactly the same", and the results must match the original run). For each model
/// file found on the PC, a copy only: the program opens the copy as it is (A), and CSI ProMax reads the same copy, writes
/// it back and runs it through the Engine (B). Every model table the program has is compared, A against B, row by row and
/// field by field; then the analysis and design results the Engine brought back are compared with the same tables of A.
/// Any difference fails, naming the table, the row and the field.
/// </summary>
public static class RealModelField
{
    /// <summary>The models each program's part takes at most, newest first (each is opened, analysed and designed twice).</summary>
    public static int MostModels { get; set; } = 3;

    /// <summary>The program's own example models taken as well, one per kind of example (design code, analysis type).</summary>
    public static int MostExamples { get; set; } = 4;

    /// <summary>A list of model files to take as well, one full path a line (Ahmed's own choice of models).</summary>
    public static string ListFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "real-models.txt");

    /// <summary>The text model files of a program (what CSI ProMax opens): ETABS .e2k, SAP2000 .s2k, SAFE .f2k.</summary>
    public static string[] TextKinds(CsiProgram program) => program.Key switch
    {
        "sap2000" => [".s2k", ".$2k"],
        "safe" => [".f2k", ".$fk"],
        _ => [".e2k", ".$et"],
    };

    /// <summary>The program's own model file: ETABS .edb, SAP2000 .sdb, SAFE .fdb.</summary>
    public static string ModelKind(CsiProgram program) => program.Key switch { "sap2000" => ".sdb", "safe" => ".fdb", _ => ".edb" };

    /// <summary>A program's own model file CSI ProMax can be given as text: ETABS exports its .e2k through its API
    /// (File.ExportFile, TextFile); SAP2000 keeps a .$2k beside a model it saved, and any .sdb's tables are read as its .s2k.</summary>
    public static bool Takes(CsiProgram program, string file) =>
        TextKinds(program).Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
        || Path.GetExtension(file).Equals(ModelKind(program), StringComparison.OrdinalIgnoreCase) && (program.Key == CsiProgram.Etabs.Key || program.Key == CsiProgram.Sap2000.Key || TwinOf(program, file) is not null);

    private static string? TwinOf(CsiProgram program, string file) =>
        TextKinds(program).Select(k => Path.ChangeExtension(file, k)).FirstOrDefault(File.Exists);

    /// <summary>The program's own examples (its Verification folder), the smallest first: the model kinds a PC's
    /// own models may not have (Ahmed 2026-11-27: "tested for all types of models").</summary>
    public static List<string> Examples(CsiProgram program, int most)
    {
        try
        {
            var api = EtabsApi.FindApiFile(program);
            var folder = Path.Combine(Path.GetDirectoryName(api) ?? "", "Verification");
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => Takes(program, f))
                .Select(f => new FileInfo(f)).GroupBy(f => Path.GetFileName(Path.GetDirectoryName(f.FullName)) ?? "")
                .Select(g => g.OrderBy(f => f.Length).First()).OrderBy(f => f.Length).Take(most).Select(f => f.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EtabsException) { return []; }
    }

    /// <summary>Folders and files that are CSI ProMax's or the tests' own, never a real model.</summary>
    public static bool IsOurs(string path) =>
        new[] { "CSI ProMax", "CsiProMax", "etabs-promax", "Field test", "pack copies", "\\cpm-", "/cpm-", " run\\", " run/" }.Any(w => path.Contains(w, StringComparison.OrdinalIgnoreCase))
        || Path.GetFileName(path).StartsWith("cpm-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The real models to take: the list file's, then the newest text models in Documents, Downloads and Desktop.</summary>
    public static List<string> Find(CsiProgram program, IEnumerable<string>? roots = null, string? listFile = null)
    {
        var found = new List<string>();
        try
        {
            if (File.Exists(listFile ?? ListFile))
                found.AddRange(File.ReadAllLines(listFile ?? ListFile).Select(l => l.Trim().Trim('"')).Where(l => l.Length > 0 && File.Exists(l) && Takes(program, l)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots ??= [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)];
        var files = new List<FileInfo>();
        foreach (var root in roots.Where(Directory.Exists)) Walk(root, 0);
        void Walk(string dir, int depth)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (Takes(program, f) && !IsOurs(f)) files.Add(new FileInfo(f));
                if (depth < 3) foreach (var d in Directory.EnumerateDirectories(dir)) if (!IsOurs(d + "\\")) Walk(d, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // A model and its own text twin are one model: the text is taken.
        found.AddRange(files.OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName)
            .Where(f => !Path.GetExtension(f).Equals(ModelKind(program), StringComparison.OrdinalIgnoreCase) || TwinOf(program, f) is null));
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Take(MostModels).ToList();
    }

    public static void Run(FieldContext ctx, CsiProgram program)
    {
        var models = Find(program).Concat(Examples(program, MostExamples)).ToList();
        if (models.Count == 0)
        {
            // Not a reason to leave the check out: the test's own scratch model, written as the program writes it, stands in.
            ctx.Note($"real models in {program.Name}", $"no {string.Join("/", TextKinds(program))} model of yours was found in Documents, Downloads or Desktop, or in {ListFile}; the scratch model's round trip covers it");
            return;
        }
        foreach (var file in models) One(ctx, program, file);
    }

    /// <summary>One real model: a copy opened as it is, and the same copy through CSI ProMax and the Engine, compared.</summary>
    public static void One(FieldContext ctx, CsiProgram program, string original)
    {
        var name = Path.GetFileNameWithoutExtension(original);
        var folder = Path.Combine(ctx.Folder, "real models", Safe(name));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(original));
        // SAP2000's text twin goes with its model.
        if (TwinOf(program, original) is { } twinOriginal && !TextKinds(program).Contains(Path.GetExtension(original), StringComparer.OrdinalIgnoreCase))
            try { File.Copy(twinOriginal, Path.Combine(folder, Path.GetFileName(twinOriginal)), overwrite: true); } catch (IOException) { }
        var title = $"your model {Path.GetFileName(original)} in {program.Name}: ";
        if (ctx.Step(title + "a copy is made (your file is only read)", () => { File.Copy(original, copy, overwrite: true); File.SetAttributes(copy, FileAttributes.Normal); return $"{new FileInfo(copy).Length:N0} bytes from {original}"; }) is null) return;

        // A: the program opens the copy as it is, analyses and designs it.
        Snapshot? a = null;
        CsiProMax.Checks.Stability.StabilityCheck.Report? live = null;
        var liveWhy = "";
        var text = copy;
        var kinds = EngineRun.Kinds[program.Key].Select(k => k.Kind).ToList();
        ctx.Step(title + $"{program.Name} opens it as it is, analyses and designs it, and every table is read", () =>
        {
            var sub = new FieldContext(program, Path.Combine(folder, "as is"), new OfflineEtabs());
            Directory.CreateDirectory(sub.Folder);
            using var window = ScratchProgram.Start(program, sub, copy) ?? throw new FieldCheckException($"{program.Name} could not open your model: {Failed(sub)}");
            try
            {
                var link = window.Link;
                // Each part and each table read is written down as it starts: the 0.4.70 full test sat 18 minutes in this
                // step on a 25 KB Verification model with ETABS busy and no word of which call it was in.
                Progress = Path.Combine(sub.Folder, "progress.txt");
                Mark("read every model table");
                var model = ModelTables(link);
                Mark("the live instability check");
                // The check as the chat's find_instabilities makes it, read live from the program before it analyses.
                try { live = CsiProMax.Checks.Stability.StabilityCheck.Run(StabilityTools.Live.Read(link)); }
                catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { liveWhy = ex.Message; }
                Mark("export the text file");
                if (!TextKinds(program).Contains(Path.GetExtension(copy), StringComparer.OrdinalIgnoreCase)) text = TextFor(program, link, copy, folder);
                Mark("run the analysis");
                link.RunAnalysis();
                Mark("run the design");
                try { DesignResults.RunDesign(link, kinds); } catch (Exception ex) when (ex is EtabsException or ToolInputException) { }
                Mark("read every result and design table");
                a = new Snapshot(model, ResultTables(link, null));
                Mark("done");
                return $"{a.Model.Count} model tables, {a.Model.Sum(t => t.Value.Rows.Count):N0} rows; {a.Results.Count} result and design tables";
            }
            finally { Progress = null; window.Close(); }
        });
        if (a is null) return;

        // The pre-run check read live from the program (find_instabilities) must not call unstable a model the program
        // analyses: Ahmed 2026-11-27, "recheck that we're posting real things for instability".
        ctx.Step(title + $"find_instabilities on the model open in {program.Name} finds no error in it", () =>
        {
            if (live is null) throw new FieldCheckException("the check could not read the model from the program: " + liveWhy);
            var errors = live.Findings.Where(f => f.Level == CsiProMax.Checks.Stability.Level.Error).ToList();
            File.WriteAllLines(Path.Combine(folder, "instability check (live).txt"), live.Findings.Select(f => $"{f.Level} {f.Check}: {f.Message} | at {string.Join(", ", f.Objects.Take(8).Select(o => o.Kind + " " + o.Name))}").Concat(live.Notes.Select(n => "note: " + n)));
            FieldContext.Expect(errors.Count == 0, $"{errors.Count} error(s) the program does not have: " + string.Join(" | ", errors.Take(10).Select(f => $"{f.Check}: {f.Message} (at {string.Join(", ", f.Objects.Take(6).Select(o => o.Kind + " " + o.Name))})")));
            return $"{live.Warnings} warning(s); {live.Joints} joints, stiffness check {(live.StiffnessRan ? "made" : "not made")}";
        });

        // B: CSI ProMax reads the copy and the Engine writes it back and runs it.
        OfflineEtabs? mine = null;
        ctx.Step(title + "CSI ProMax reads it", () =>
        {
            var opened = OfflineEtabs.Open(text);
            opened.Model.Target = program.Key;
            mine = new OfflineEtabs(opened.Model, "");
            mine.SaveAs(Path.Combine(folder, "through CSI ProMax", Safe(name) + ".e2k"));
            return $"{mine.Frames().Count} frames, {mine.Areas().Count} areas" + (opened.Notes.Count > 0 ? "; " + string.Join(" | ", opened.Notes.Take(5)) : "");
        });
        if (mine is null) return;
        // The pre-run check must not call a model the program analyses unstable (Ahmed 2026-11-27: his model ran in ETABS
        // with no instability, and the check said "point 1983 free in UZ" and "no mass").
        ctx.Step(title + $"the pre-run instability check finds no error in it ({program.Name} analyses it as it is)", () =>
        {
            var report = CsiProMax.Checks.Stability.StabilityCheck.Run(CsiProMax.Checks.Stability.FromStructural.Read(mine.Model, program.Key));
            var errors = report.Findings.Where(f => f.Level == CsiProMax.Checks.Stability.Level.Error).ToList();
            File.WriteAllLines(Path.Combine(folder, "instability check.txt"), report.Findings.Select(f => $"{f.Level} {f.Check}: {f.Message} | at {string.Join(", ", f.Objects.Take(8).Select(o => o.Kind + " " + o.Name))}").Concat(report.Notes.Select(n => "note: " + n)));
            FieldContext.Expect(errors.Count == 0, $"{errors.Count} error(s) the program does not have: " + string.Join(" | ", errors.Take(10).Select(f => $"{f.Check}: {f.Message} (at {string.Join(", ", f.Objects.Take(6).Select(o => o.Kind + " " + o.Name))})")));
            return $"{report.Warnings} warning(s); {report.Joints} joints, stiffness check {(report.StiffnessRan ? "made" : "not made")}";
        });
        JsonObject? status = null;
        ctx.Step(title + $"the Engine writes it, {program.Name} opens that with no message, analyses and designs it, and the results come back", () =>
        {
            var started = EngineRun.Start(mine, analyse: true, kinds: kinds, keepOpen: false);
            FieldContext.Expect(started["ok"]?.GetValue<bool>() == true, "the run did not start: " + started["error"]);
            var until = DateTime.UtcNow.AddMinutes(60);
            while (EngineRun.Status(false)["done"]?.GetValue<bool>() != true && DateTime.UtcNow < until) Thread.Sleep(2000);
            status = EngineRun.Status(true);
            FieldContext.Expect(status["done"]?.GetValue<bool>() == true, "the run did not finish within 60 minutes: " + status["phase"]);
            FieldContext.Expect(string.IsNullOrEmpty(status["error"]?.GetValue<string>()), "the run stopped: " + status["error"]);
            FieldContext.Expect(status["pack"] is JsonObject, "no results came back");
            return string.Join(" | ", (status["lines"] as JsonArray ?? []).Select(l => l?.GetValue<string>()).TakeLast(2));
        });
        if (status?["pack"] is not JsonObject pack) return;

        var analysed = status["analysed_file"]?.GetValue<string>() ?? "";
        ctx.Step(title + "every model table is the same after the round trip, row by row and field by field", () =>
        {
            FieldContext.Expect(File.Exists(analysed), $"the Engine's analysed model is not there to compare ({analysed})");
            var b = Packs.PackCopy.Read(program, analysed, ModelTables);
            FieldContext.Expect(b.Value is not null, "the Engine's model could not be read from a copy: " + b.Said);
            var lost = Compare(a.Model, b.Value!, model: true);
            // The whole list beside the model, for the lossless threads: every table, row and field, before and after.
            File.WriteAllLines(Path.Combine(folder, "model table differences.txt"), lost.Count > 0 ? lost : ["none"]);
            FieldContext.Expect(lost.Count == 0, $"{lost.Count} difference(s): " + string.Join(" | ", lost.Take(25)));
            return $"{a.Model.Count} tables, {a.Model.Sum(t => t.Value.Rows.Count):N0} rows, all the same";
        });
        ctx.Step(title + "the Engine's analysis and design results match the program's own run of your model", () =>
        {
            var b = FromPack(pack);
            var shared = a.Results.Keys.Where(b.ContainsKey).ToList();
            FieldContext.Expect(shared.Count > 0, $"no result table to compare: the program's run gave {string.Join(", ", a.Results.Keys.Take(10))}; the Engine's {string.Join(", ", b.Keys.Take(10))}");
            var missing = a.Results.Keys.Where(k => !b.ContainsKey(k) && a.Results[k].Rows.Count > 0).ToList();
            var off = Compare(a.Results.Where(t => shared.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value), b, model: false);
            File.WriteAllLines(Path.Combine(folder, "result differences.txt"), (off.Count > 0 ? off : ["none"]).Concat(missing.Select(m => m + ": not brought back by the Engine")));
            FieldContext.Expect(missing.Count == 0, "the Engine brought back none of: " + string.Join(", ", missing.Take(20)));
            FieldContext.Expect(off.Count == 0, $"{off.Count} difference(s): " + string.Join(" | ", off.Take(25)));
            return $"{shared.Count} result and design tables, {shared.Sum(k => a.Results[k].Rows.Count):N0} rows, all the same";
        });
    }

    /// <summary>The model as text for CSI ProMax: ETABS's own export of it (File > Export > ETABS .e2k, through the
    /// API), or the .$2k SAP2000 keeps beside it, or else SAP2000's own tables of it written as a .s2k.</summary>
    private static string TextFor(CsiProgram program, ICsiLink link, string copy, string folder)
    {
        if (TwinOf(program, copy) is { } twin) return twin;
        if (program.Key == CsiProgram.Sap2000.Key)
        {
            // No .$2k beside it: the model as SAP2000's own tables give it, every importable table, as its .s2k would be.
            var s2k = Path.Combine(folder, Path.GetFileNameWithoutExtension(copy) + " tables.s2k");
            File.WriteAllText(s2k, Sap2000.SapTablesText.Read(link));
            return s2k;
        }
        var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(copy) + ".e2k");
        var kinds = AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic).Select(x => { try { return x.GetType("ETABSv1.eFileTypeIO") ?? x.GetType("CSiAPIv1.eFileTypeIO"); } catch (Exception) { return null; } }).FirstOrDefault(t => t is not null)
            ?? throw new FieldCheckException($"{program.Name}'s API has no file export here (no eFileTypeIO), so the model cannot be given to CSI ProMax as text");
        var said = link.Raw("export the model as an .e2k", m => (object)Convert.ToInt32(m.File.ExportFile(target, Enum.Parse(kinds, "TextFile"))));
        FieldContext.Expect(Convert.ToInt32(said) == 0 && File.Exists(target), $"{program.Name} did not export the model as text (it answered {said})");
        return target;
    }

    public sealed record Snapshot(Dictionary<string, TableData> Model, Dictionary<string, TableData> Results);

    /// <summary>The file each part and table read of the program's own run is noted in as it starts (null: none).</summary>
    [ThreadStatic] private static string? Progress;

    private static void Mark(string what)
    {
        if (Progress is not { } file) return;
        try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {what}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the note is only a help */ }
    }

    /// <summary>Every model table (the ones the program can import), as the program gives it.</summary>
    public static Dictionary<string, TableData> ModelTables(ICsiLink link)
    {
        var all = new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in link.Tables().Where(t => t.Import > 0 || t.Import < 0 && !IsResult(t.Key)))
            try { Mark("  model table " + t.Key); var d = link.ReadTable(t.Key, null, null); if (d.Rows.Count > 0) all[t.Key] = d; }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { }
        return all;
    }

    /// <summary>The result and design tables (those the program cannot import), for every case and combination.</summary>
    public static Dictionary<string, TableData> ResultTables(ICsiLink link, IReadOnlyCollection<string>? only)
    {
        var all = new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase);
        var cases = link.LoadCases(); var combos = link.Combos();
        foreach (var t in link.Tables().Where(t => (t.Import == 0 || t.Import < 0 && IsResult(t.Key)) && (only is null || only.Contains(t.Key))))
            try { Mark("  result table " + t.Key); var d = link.ReadTable(t.Key, cases, combos); if (d.Rows.Count > 0) all[t.Key] = d; }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { }
        return all;
    }

    private static bool IsResult(string key) => new[] { "Results", "Reactions", "Displacement", "Forces", "Drift", "Modal", "Design", "Summary", "Stresses", "Strains", "Accelerations", "Velocities" }
        .Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static Dictionary<string, TableData> FromPack(JsonObject pack)
    {
        var all = new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, t) in pack["tables"] as JsonObject ?? [])
        {
            if (t?["fields"] is not JsonArray f || t["rows"] is not JsonArray rows) continue;
            all[key] = new TableData(f.Select(x => x?.ToString() ?? "").ToList(), rows.OfType<JsonArray>().Select(r => r.Select(v => v?.ToString() ?? "").ToArray()).ToList());
        }
        return all;
    }

    /// <summary>Fields that name an object for the program's own bookkeeping, not the model: left out of the comparison.</summary>
    private static readonly HashSet<string> NotCompared = new(StringComparer.OrdinalIgnoreCase) { "GUID", "ObjGUID" };

    /// <summary>
    /// The differences between two sets of tables: a table only one has, and rows matched by their text fields (story,
    /// label, case, ...; in order within each such key), then field by field, numbers within a small tolerance.
    /// </summary>
    public static List<string> Compare(Dictionary<string, TableData> a, Dictionary<string, TableData> b, bool model)
    {
        var out_ = new List<string>();
        foreach (var (key, ta) in a)
        {
            if (!b.TryGetValue(key, out var tb)) { out_.Add($"{key}: the table is gone ({ta.Rows.Count} rows)"); continue; }
            var fields = ta.Fields.Where(f => !NotCompared.Contains(f)).ToList();
            var missingFields = fields.Where(f => !tb.Fields.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missingFields.Count > 0) out_.Add($"{key}: field(s) gone: {string.Join(", ", missingFields)}");
            var common = fields.Except(missingFields).ToList();
            int[] ia = common.Select(f => IndexOf(ta.Fields, f)).ToArray(), ib = common.Select(f => IndexOf(tb.Fields, f)).ToArray();
            string KeyOf(string[] r, int[] idx) => string.Join("|", idx.Select(i => i < r.Length ? r[i] : "").Where(v => !IsNumber(v)));
            var groupsB = tb.Rows.GroupBy(r => KeyOf(r, ib)).ToDictionary(g => g.Key, g => g.ToList());
            var scale = common.Select((_, k) => ta.Rows.Select(r => Num(r, ia[k])).Where(v => !double.IsNaN(v)).Select(Math.Abs).DefaultIfEmpty(0).Max()).ToArray();
            foreach (var g in ta.Rows.GroupBy(r => KeyOf(r, ia)))
            {
                if (!groupsB.TryGetValue(g.Key, out var rowsB)) { out_.Add($"{key}: row {Show(g.Key)} is gone"); continue; }
                var rowsA = g.ToList();
                if (rowsA.Count != rowsB.Count) out_.Add($"{key}: row {Show(g.Key)} has {rowsB.Count} line(s), not {rowsA.Count}");
                for (var n = 0; n < Math.Min(rowsA.Count, rowsB.Count); n++)
                    for (var k = 0; k < common.Count; k++)
                    {
                        var va = ia[k] < rowsA[n].Length ? rowsA[n][ia[k]] : ""; var vb = ib[k] < rowsB[n].Length ? rowsB[n][ib[k]] : "";
                        if (!Same(va, vb, scale[k], model)) out_.Add($"{key}: {Show(g.Key)} {common[k]} is '{vb}', not '{va}'");
                    }
                groupsB.Remove(g.Key);
            }
            foreach (var extra in groupsB.Keys.Take(5)) out_.Add($"{key}: row {Show(extra)} was added");
        }
        if (model) foreach (var key in b.Keys.Where(k => !a.ContainsKey(k))) out_.Add($"{key}: a table the original does not have ({b[key].Rows.Count} rows)");
        return out_;
    }

    private static string Show(string key) => key.Length == 0 ? "(the only row)" : key.Length > 120 ? key[..120] + "..." : key;

    private static int IndexOf(IReadOnlyList<string> fields, string f)
    {
        for (var i = 0; i < fields.Count; i++) if (string.Equals(fields[i], f, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool IsNumber(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    private static double Num(string[] r, int i) => i >= 0 && i < r.Length && double.TryParse(r[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>Text the same (case and outer spaces aside), numbers within 1e-4 of each other relatively, or of the
    /// column's largest value for a value near zero (results: 1e-3).</summary>
    public static bool Same(string a, string b, double scale, bool model)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
        var rel = model ? 1e-4 : 1e-3;
        return Math.Abs(x - y) <= rel * Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), 1e-3 * scale) + 1e-9;
    }

    private static string Failed(FieldContext ctx) => string.Join(" ", ctx.Steps.Where(s => !s.Ok && !s.Skipped).Select(s => s.Name + ": " + s.Detail));

    private static string Safe(string name) => new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
}
