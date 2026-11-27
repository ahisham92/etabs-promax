using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The review pack (.cpmx): one zip with the model as ETABS has it, the result and design tables ETABS produced, the
/// checks made from them, and review comments. A reviewer opens it in the CSI ProMax Viewer (one HTML file, nothing
/// to install, no ETABS). Results are copied from ETABS's tables as they are; nothing is analysed here.
/// </summary>
public static class Pack
{
    public const int Version = 1;
    /// <summary>
    /// Rows a pack takes of one table. Ahmed wants every result in the Viewer, and real element tables run past 60,000
    /// (0.4.33 field test: 44,000 shell rows and 139,000 SAP2000 frame stress rows on a small model), so a table is
    /// taken whole up to this, within <see cref="PackTables.TotalMostRows"/> for the whole pack.
    /// </summary>
    /// Ahmed 2026-11-26: lossless, no limits. Every row of every table is taken; a long table is stored in parts
    /// (<see cref="PartRows"/>) so no single text in the pack or its Viewer page grows past what .NET or a browser holds.
    public const int DefaultMostRows = int.MaxValue;

    /// <summary>Rows in one part of a long table, in the pack (tables/NNN-pK.json) and in the Viewer page (a script element each).</summary>
    public static int PartRows { get; set; } = 100_000;
    /// <summary>The largest model (frames and areas) whose instability check is made with the pack; a bigger one is checked with find_instabilities.</summary>
    public const int StabilityPackLimit = 8000;
    /// <summary>Design tables are taken longer: a concrete beam table has a row per station of every beam.</summary>
    public const int DesignMostRows = int.MaxValue;

    /// <summary>The result tables a pack takes when they have data. Design summaries are added by name.</summary>
    public static readonly string[] UsualTables =
    [
        "Story Definitions", "Modal Participating Mass Ratios", "Modal Periods And Frequencies", "Base Reactions", "Story Drifts",
        "Story Max Over Avg Displacements", "Story Max Over Avg Drifts", "Story Stiffness", "Story Forces", "Mass Summary by Story",
        "Centers Of Mass And Rigidity", "Joint Displacements", "Joint Reactions", "Diaphragm Center Of Mass Displacements",
        // What the Viewer draws force and moment diagrams from.
        "Element Forces - Beams", "Element Forces - Columns", "Element Forces - Braces",
    ];

    /// <summary>
    /// The program's stories; for a program without stories (SAP2000), the levels its joints stand on, so the Viewer's
    /// plan and elevation views have levels to step through (0.4.20 field test: the SAP2000 pack had none).
    /// </summary>
    internal static IReadOnlyList<StoryRow> Levels(ICsiLink e, IReadOnlyList<PointRow> points)
    {
        var stories = e.Stories();
        if (stories.Count > 0 || e.Program.Key != CsiProgram.Sap2000.Key || points.Count == 0) return stories;
        var span = points.Max(p => p.Z) - points.Min(p => p.Z);
        var tolerance = Math.Max(1e-6, span * 1e-4);
        var zs = new List<double>();
        foreach (var z in points.Select(p => p.Z).OrderBy(z => z))
            if (zs.Count == 0 || z - zs[^1] > tolerance) zs.Add(z);
        if (zs.Count < 2 || zs.Count > 200) return [];
        var rows = new List<StoryRow> { new("Base", zs[0], 0) };
        for (var i = 1; i < zs.Count; i++) rows.Add(new StoryRow($"Z {Math.Round(zs[i], 3).ToString(CultureInfo.InvariantCulture)}", zs[i], zs[i] - zs[i - 1]));
        return rows;
    }

    /// <summary>
    /// A file each table's name is written to just before it is read, when set (the field test sets it in its work
    /// folder): SAP2000 26.3 crashed in the middle of writing the pack (0.4.59 full test, heap corruption in ntdll), and
    /// the file says which table it was reading.
    /// </summary>
    public static string? TraceFile { get; set; }

    private static readonly object CacheLock = new();
    private static Dictionary<string, TableData>? _reads;

    /// <summary>
    /// Keeps every table a pack reads until the returned scope ends, for packs of one model that does not change in
    /// between: the field test's pack, the Viewer's pack and the watcher's two packs of the analysed scratch model read the
    /// same 161 tables four times, at about 2.4 s a table in ETABS 23.2 (0.4.61 full test: 4 passes of 9 minutes; Ahmed
    /// 2026-11-27 asked why). A scope inside another one changes nothing. Never set where the model may change meanwhile.
    /// </summary>
    public static IDisposable SameModel()
    {
        lock (CacheLock)
        {
            if (_reads is not null) return new Scope(false);
            _reads = new Dictionary<string, TableData>(StringComparer.Ordinal);
            return new Scope(true);
        }
    }

    private sealed class Scope(bool owns) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done || !owns) return;
            _done = true;
            lock (CacheLock) _reads = null;
        }
    }

    /// <summary>A table for a pack: from <see cref="SameModel"/>'s reads when one is open, else from the program.</summary>
    internal static TableData ReadTable(ICsiLink e, string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos)
    {
        var id = e.Program.Key + "\u0001" + key + "\u0001" + string.Join("\u0002", cases ?? []) + "\u0001" + string.Join("\u0002", combos ?? []);
        lock (CacheLock) if (_reads is not null && _reads.TryGetValue(id, out var kept)) return kept;
        var data = e.ReadTable(key, cases, combos);
        lock (CacheLock) if (_reads is not null) _reads[id] = data;
        return data;
    }

    private static void Trace(ICsiLink e, string table)
    {
        if (TraceFile is not { Length: > 0 } file) return;
        try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {e.Program.Name} {table}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only a trace */ }
    }

    /// <summary>
    /// How long a pack may take to write (Ahmed 2026-11-27: "in a minute or two or 3 maximum"). The field test fails a
    /// pack that takes longer, and the manifest's "timing" says where the time went.
    /// </summary>
    public static readonly TimeSpan MostTime = TimeSpan.FromMinutes(3);

    /// <summary>Where a pack's time goes: each step and each table read, in milliseconds, for the manifest and the trace.</summary>
    private sealed class Timing
    {
        private readonly System.Diagnostics.Stopwatch _all = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Diagnostics.Stopwatch _step = System.Diagnostics.Stopwatch.StartNew();
        private readonly JsonObject _steps = new();
        private readonly List<(string Table, int Rows, long Ms)> _tables = [];

        /// <summary>What the helper windows did (PackHelpers), when there were any.</summary>
        public JsonNode? Helpers { get; set; }

        /// <summary>The rows each table had as the program gave it, by table name (Missing checks the pack against them).</summary>
        public JsonObject ReadRows()
        {
            var o = new JsonObject();
            lock (_tables) foreach (var t in _tables) if (t.Rows >= 0) o[t.Table] = (o[t.Table]?.GetValue<int>() ?? 0) < t.Rows ? t.Rows : o[t.Table]?.GetValue<int>() ?? 0;
            return o;
        }

        public void Step(string name) { _steps[name] = (_steps[name]?.GetValue<long>() ?? 0) + _step.ElapsedMilliseconds; _step.Restart(); }

        public TableData Read(ICsiLink e, TableName t, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos)
        {
            Trace(e, t.Key);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rows = -1;
            try { var data = ReadTable(e, t.Key, cases, combos); rows = data.Rows.Count; return data; }
            finally
            {
                lock (_tables) _tables.Add((t.Name, rows, watch.ElapsedMilliseconds));
                Trace(e, $"  {t.Key}: {watch.ElapsedMilliseconds} ms, {rows} rows");
            }
        }

        public JsonObject Json()
        {
            Step("the rest");
            return new JsonObject
            {
                ["seconds"] = Math.Round(_all.Elapsed.TotalSeconds, 1),
                ["most_seconds"] = MostTime.TotalSeconds,
                ["steps_ms"] = _steps,
                ["tables_read"] = _tables.Count,
                ["tables_ms"] = _tables.Sum(t => t.Ms),
                // The slowest reads first: what to look at when a pack is slow.
                ["slowest"] = new JsonArray(_tables.OrderByDescending(t => t.Ms).Take(25).Select(t => (JsonNode)new JsonArray(t.Table, t.Rows, t.Ms)).ToArray()),
                ["helpers"] = Helpers,
            };
        }
    }

    public sealed record Built(byte[] Zip, JsonObject Content, int Frames, int Areas, IReadOnlyList<string> Tables, IReadOnlyList<string> Notes);

    /// <param name="extraTables">More tables to take, by name or key (for example "Element Forces - Beams").</param>
    public static Built Build(ICsiLink e, IReadOnlyList<string> extraTables, IReadOnlyList<string> cases, IReadOnlyList<string> combos,
        JsonObject? checks, JsonArray? comments, int mostRows = DefaultMostRows, string by = "", bool withTables = true,
        IReadOnlyList<string>? designFiles = null, bool instabilities = false, JsonObject? auto = null, string? fileAs = null, bool zip = true, Packs.PackHelpers? helpers = null)
    {
        // Every table is listed once and read once while the pack is written (with the checks before it, when the caller
        // started the session): the program is not asked again for what it gave already.
        using var session = PackSession.Start(e);
        var timing = new Timing();
        var info = e.Info();
        var notes = new List<string>();
        var file = info.File.Replace('\\', '/').Split('/').Last();
        var manifest = new JsonObject
        {
            ["format"] = "cpmx", ["version"] = Version,
            ["model"] = file.Length > 0 ? Path.GetFileNameWithoutExtension(file) : e is OfflineEtabs { Model.Title.Length: > 0 } own ? own.Model.Title : "Unsaved model",
            // fileAs: the engineer's model, when the pack was read from a copy of it (PackCopy).
            ["file"] = fileAs ?? info.File, ["etabs"] = info.Version, ["program"] = e.Program.Name,
            // The Modeler's model is made for one program (Ahmed 2026-11-26): the page offers what that one has.
            ["target"] = e is OfflineEtabs { Model: var tm } ? tm.Target : null,
            ["units"] = new JsonObject { ["force"] = info.Force, ["length"] = info.Length, ["temperature"] = info.Temperature },
            // The Modeler's model shown in other units than its own (the units control): the units it is saved in.
            ["saved_units"] = e is OfflineEtabs { SavedUnits: { } su } ? new JsonObject { ["force"] = su.Force, ["length"] = su.Length, ["temperature"] = su.Temperature } : null,
            ["analysis_run"] = info.Locked,
            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["by"] = by,
            ["made_with"] = "CSI ProMax",
        };
        // A pack the watcher wrote by itself after a run or a design (AutoPack): what it was made from.
        if (auto is not null) manifest["auto"] = auto;
        var frames = e.Frames();
        var areas = e.Areas();
        var points = e.Points();
        var model = new JsonObject
        {
            ["stories"] = new JsonArray(Levels(e, points).Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["elevation"] = R(s.Elevation), ["height"] = R(s.Height) }).ToArray()),
            ["frames"] = new JsonArray(frames.Select(f => (JsonNode)new JsonArray(f.Name, f.Kind, f.Section, f.Story, R(f.X1), R(f.Y1), R(f.Z1), R(f.X2), R(f.Y2), R(f.Z2))).ToArray()),
            ["areas"] = new JsonArray(areas.Select(a => (JsonNode)new JsonObject
            {
                ["n"] = a.Name, ["k"] = a.Kind, ["s"] = a.Section,
                ["c"] = new JsonArray(a.Corners.SelectMany(c => new[] { (JsonNode)R(c.X), R(c.Y), R(c.Z) }).ToArray()),
            }).ToArray()),
            ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(p.Name, R(p.X), R(p.Y), R(p.Z))).ToArray()),
            ["frame_sections"] = Texts(e.FrameSections()), ["area_sections"] = Texts(e.AreaSections()), ["materials"] = Texts(e.Materials()),
            ["load_patterns"] = new JsonArray(e.LoadPatterns().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["self_weight"] = p.SelfWeight }).ToArray()),
            ["load_cases"] = Texts(e.LoadCases()), ["combinations"] = Texts(e.Combos()),
        };

        timing.Step("model");
        Drawing.AddTo(model, e, points);
        timing.Step("drawing");

        var known = withTables ? e.Tables() : [];
        timing.Step("table list");
        var wanted = new List<TableName>();
        foreach (var name in UsualTables)
            if (known.FirstOrDefault(t => !t.Empty && (ResultTools.Norm(t.Name) == ResultTools.Norm(name) || ResultTools.Norm(t.Key) == ResultTools.Norm(name))) is { } t) wanted.Add(t);
        wanted.AddRange(known.Where(t => !t.Empty && (DesignResults.IsDesignTable(t.Name) || DesignResults.IsWallTable(t.Name))));
        wanted.AddRange(DesignListedEmpty(known));
        wanted.AddRange(known.Where(t => !t.Empty && ProgramTools.PackTable(e.Program, t.Name)));
        foreach (var name in extraTables)
        {
            var t = known.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase));
            if (t is null) notes.Add($"There is no table '{name}', so it was left out.");
            else wanted.Add(t);
        }
        if (cases.Count == 0 && combos.Count == 0) { cases = e.LoadCases(); combos = e.Combos(); }
        var tables = new JsonObject();
        foreach (var t in wanted.DistinctBy(t => t.Key))
        {
            TableData data;
            try { data = timing.Read(e, t, cases, combos); }
            catch (EtabsException ex) { notes.Add($"'{t.Name}' could not be read ({ex.Message})."); continue; }
            if (data.Rows.Count == 0) continue;
            tables[t.Name] = TableJson(t.Name, data, DesignResults.IsDesignTable(t.Name) ? Math.Max(mostRows, DesignMostRows) : mostRows, notes, null);
        }
        timing.Step("usual tables");
        if (withTables) AddEverything(e, known, wanted, cases, combos, tables, mostRows, notes, manifest, timing, helpers);
        timing.Step("every other table");
        if (withTables && tables.Count <= 1 && !info.Locked) notes.Add("The analysis has not been run, so the pack holds the model without results.");
        if (withTables) manifest["design"] = AddDesign(e, info, known, tables, designFiles ?? [], Math.Max(mostRows, DesignMostRows), notes);
        timing.Step("design");
        if (withTables && (areas.Any(a => a.Kind == "wall") || tables.Select(t => t.Key).Any(n => DesignResults.FamilyOf(n) is { On: "pier" or "spandrel" })))
            model["wall_labels"] = DesignResults.WallLabels(e, known, notes);
        else if (e is OfflineEtabs) model["wall_labels"] = DesignResults.WallLabels(e, [], notes);

        var content = new JsonObject { ["manifest"] = manifest, ["model"] = model, ["checks"] = checks, ["comments"] = comments ?? [], ["tables"] = tables };
        // What stops the analysis, as the checker found it when the pack was made: the Viewer lists and marks it.
        if (instabilities && frames.Count + areas.Count <= StabilityPackLimit)
        {
            try { content["instabilities"] = StabilityTools.Json(CsiProMax.Checks.Stability.StabilityCheck.Run(StabilityTools.Read(e)), e.Program, e is OfflineEtabs); }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException or ArgumentException) { notes.Add($"The instability check could not be made for the pack ({ex.Message})."); }
        }
        timing.Step("labels and instabilities");
        manifest["timing"] = timing.Json();
        manifest["read_rows"] = timing.ReadRows();
        if (withTables && manifest["timing"]!["seconds"]!.GetValue<double>() > MostTime.TotalSeconds)
            notes.Add($"The pack took {manifest["timing"]!["seconds"]} s to read from {e.Program.Name}, more than {MostTime.TotalMinutes:0} minutes: manifest.json's \"timing\" names the slowest tables.");
        // What the pack says about itself (tables read in parts, cut, left out or refused), for the Viewer and the checks.
        manifest["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray());
        return new Built(zip ? ZipOf(content) : [], content, frames.Count, areas.Count, tables.Select(t => t.Key).ToList(), notes);
    }

    /// <summary>
    /// What the program has that the pack does not (Ahmed 2026-11-27: the results of an analysis or a design "complete,
    /// no way to lose anything"): every result, design and design-input table the program lists for this model, read
    /// from it (inside the pack's read session, so from what the pack read), against the pack's table of that name:
    /// a table with rows that is not in the pack, a row or a field fewer, or rows left out. Empty when nothing is lost.
    /// Call it while the program is still open, with the cases and combinations the pack was built with.
    /// </summary>
    public static List<string> Missing(ICsiLink e, JsonObject content, IReadOnlyList<string> cases, IReadOnlyList<string> combos)
    {
        if (cases.Count == 0 && combos.Count == 0) { cases = e.LoadCases(); combos = e.Combos(); }
        var tables = content["tables"] as JsonObject ?? [];
        var read = content["manifest"]?["read_rows"] as JsonObject;
        var parts = (content["manifest"]?["notes"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").Where(n => n.Contains("at a time", StringComparison.Ordinal)).ToList();
        var lost = new List<string>();
        var readEmpty = DesignListedEmpty(e.Tables()).Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in e.Tables().DistinctBy(t => t.Key))
        {
            var kind = PackTables.Kind(t);
            if (kind is null && !DesignResults.IsDesignTable(t.Name) && !DesignResults.IsWallTable(t.Name)) continue;
            var held = tables[t.Name] as JsonObject;
            // A table the program lists as empty is not the model's (the pack leaves it out the same way): ETABS 23.2 lists
            // the design preferences of every code not in use as empty and still gives each its row of defaults, and
            // reading all 249 of them counted them as lost and took the field test's pack past 3 minutes (0.4.73).
            if (held is null && t.Empty && !readEmpty.Contains(t.Key)) continue;
            if (held is not null && held["left_out"] is { } cut) { lost.Add($"'{t.Name}': {cut} rows left out"); continue; }
            // A table read a load case at a time is not read whole again to count it: its rows came from the program.
            if (held is not null && parts.Any(n => n.Contains($"'{t.Name}'", StringComparison.Ordinal))) continue;
            // A table the pack read from the program (it or a helper window): the pack must hold every row it was given.
            if (held is not null && read?[t.Name]?.GetValue<int>() is int given)
            {
                var have = (held["rows"] as JsonArray)?.Count ?? 0;
                if (have < given) lost.Add($"'{t.Name}': {have} of {given} rows");
                continue;
            }
            TableData data;
            try { data = ReadTable(e, t.Key, cases, combos); }
            catch (EtabsException) { if (held is null && !t.Empty) lost.Add($"'{t.Name}': not in the pack, and the program would not give it"); continue; }
            if (data.Rows.Count == 0) continue;
            if (held is null) { lost.Add($"'{t.Name}': {data.Rows.Count} rows, not in the pack"); continue; }
            var rows = (held["rows"] as JsonArray)?.Count ?? 0;
            if (rows < data.Rows.Count) lost.Add($"'{t.Name}': {rows} of {data.Rows.Count} rows");
            var fields = (held["fields"] as JsonArray ?? []).Select(f => f?.ToString() ?? "").ToHashSet(StringComparer.Ordinal);
            var gone = data.Fields.Where(f => f.Length > 0 && !fields.Contains(f)).ToList();
            if (gone.Count > 0) lost.Add($"'{t.Name}': fields {string.Join(", ", gone)} not in the pack");
        }
        return lost;
    }

    /// <summary>
    /// Design tables the program lists as empty that are read all the same: ETABS 23.2 listed "Concrete Beam Design -
    /// Flexural Stress Check - Long Term / Normal / Transfer" as empty and gave 708 rows each (0.4.73 full test), so the
    /// pack left real design results out. Read: every such table of a design family that has data, unless it is of a
    /// design code other than the ones that family's tables with data are of (ETABS lists a table per code, all codes).
    /// </summary>
    internal static IReadOnlyList<TableName> DesignListedEmpty(IReadOnlyList<TableName> known)
    {
        var with = known.Where(t => !t.Empty && DesignResults.IsDesignTable(t.Name)).ToList();
        var codes = with.GroupBy(t => DesignResults.FamilyOf(t.Name)!.Key)
            .ToDictionary(g => g.Key, g => g.Select(t => CodeOf(t.Name)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));
        return known.Where(t => t.Empty && DesignResults.FamilyOf(t.Name) is { } f && codes.TryGetValue(f.Key, out var used)
                && (CodeOf(t.Name) is not { } code || used.Count == 0 || used.Contains(code)))
            .DistinctBy(t => t.Key).ToList();
    }

    /// <summary>The design code a table is named for: its last " - " part when that has a figure in it ("ACI 318-19",
    /// "Hong Kong CP2013", "Eurocode 2-2004"), else none ("Long Term").</summary>
    internal static string? CodeOf(string table)
    {
        var at = table.LastIndexOf(" - ", StringComparison.Ordinal);
        if (at < 0) return null;
        var last = table[(at + 3)..].Trim();
        return last.Any(char.IsDigit) ? last : null;
    }

    /// <summary>
    /// The .cpmx of a pack's content. Build makes it at once unless asked not to (zip: false): a pack read from a copy
    /// in a hidden program is zipped after that program is closed, so it holds a licence only while it reads.
    /// </summary>
    public static byte[] ZipOf(JsonObject content)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip, "manifest.json", content["manifest"]!.AsObject());
            Add(zip, "model.json", content["model"]!.AsObject());
            if (content["checks"] is JsonObject checks) Add(zip, "checks.json", checks);
            if (content["instabilities"] is JsonObject found) Add(zip, "instabilities.json", found);
            Add(zip, "comments.json", content["comments"] as JsonArray ?? []);
            var n = 0;
            foreach (var (_, table) in content["tables"] as JsonObject ?? []) AddTable(zip, $"tables/{++n:000}", table!.AsObject());
        }
        return stream.ToArray();
    }

    /// <summary>The pack with its .cpmx made (after <c>Build(..., zip: false)</c>); as it is when it has one.</summary>
    public static Built Zipped(Built built) => built.Zip.Length > 0 ? built : built with { Zip = ZipOf(built.Content) };

    /// <summary>
    /// Every other result, design and design-input table that has data (<see cref="PackTables"/>), within the pack's
    /// total of rows, and the steps of time history and nonlinear cases. What did not fit is named in the manifest, so
    /// the Viewer can say which tables to ask for.
    /// </summary>
    private static void AddEverything(ICsiLink e, IReadOnlyList<TableName> known, IReadOnlyList<TableName> wanted, IReadOnlyList<string> cases,
        IReadOnlyList<string> combos, JsonObject tables, int mostRows, List<string> notes, JsonObject manifest, Timing timing, Packs.PackHelpers? helpers = null)
    {
        if (helpers is { Asked: > 0 }) ReadTogether(e, known, wanted, cases, combos, tables, notes, timing, helpers);
        var left = new JsonArray();
        // The results and the inputs beside them each have their own total, so that a model with many result rows
        // still brings its load assignments and design inputs (0.4.49: SAP2000's Load Assigns were greyed out).
        var budget = new Dictionary<string, int> { ["result"] = PackTables.TotalMostRows, ["input"] = PackTables.InputMostRows };
        foreach (var t in PackTables.More(known, wanted))
        {
            if (tables.ContainsKey(t.Name)) continue;
            var kind = PackTables.Kind(t) ?? "result";
            if (budget[kind] <= 0) { left.Add((JsonNode)t.Name); continue; }
            TableData data;
            try { data = TooBig(e, t, known, cases, combos, tables, timing) is { } guess ? ByCase(e, t, cases, combos, timing, guess, notes) : timing.Read(e, t, cases, combos); }
            catch (EtabsException ex) { notes.Add($"'{t.Name}' could not be read ({ex.Message})."); continue; }
            if (data.Rows.Count == 0) continue;
            var most = Math.Min(mostRows, budget[kind]);
            tables[t.Name] = TableJson(t.Name, data, most, notes, kind == "input" ? "input" : null);
            budget[kind] -= Math.Min(data.Rows.Count, most);
        }
        timing.Step("results and inputs");
        if (PackTables.AddSteps(e, known, cases, combos, tables, mostRows, notes, (name, data, most) => TableJson(name, data, most, notes, null)) is { } steps) manifest["step_output"] = steps;
        timing.Step("time history steps");
        if (left.Count > 0)
        {
            manifest["left_out_tables"] = left;
            notes.Add($"{left.Count} more result tables did not fit in the pack ({string.Join(", ", left.Take(8).Select(n => n!.GetValue<string>()))}{(left.Count > 8 ? ", ..." : "")}): make the pack again with those tables named, or with fewer load cases.");
        }
    }

    /// <summary>
    /// The result tables read by the window that ran the model and the helper windows at once, each a different table
    /// (<see cref="Packs.PackHelpers"/>), the largest first, into <paramref name="tables"/> in the pack's usual order.
    /// Design and input tables stay with the window that ran the model: a design is in it alone until it is saved. A
    /// table a helper could not give is read by that window. Whatever is left is read after, as before.
    /// </summary>
    private static void ReadTogether(ICsiLink e, IReadOnlyList<TableName> known, IReadOnlyList<TableName> wanted, IReadOnlyList<string> cases,
        IReadOnlyList<string> combos, JsonObject tables, List<string> notes, Timing timing, Packs.PackHelpers helpers)
    {
        var todo = PackTables.More(known, wanted).Where(t => !tables.ContainsKey(t.Name)).ToList();
        bool Shared(TableName t) => PackTables.Kind(t) == "result" && !DesignResults.IsDesignTable(t.Name) && !DesignResults.IsWallTable(t.Name);
        var shared = new ConcurrentQueue<TableName>(todo.Where(Shared).OrderByDescending(Heavy));
        var own = new ConcurrentQueue<TableName>(); // what a helper could not give
        var got = new ConcurrentDictionary<string, TableData>(StringComparer.Ordinal);
        var byHelper = 0;
        void Note(string n) { lock (notes) notes.Add(n); }
        TableData ReadOne(ICsiLink by, TableName t) =>
            TooBig(by, t, known, cases, combos, new JsonObject(), timing) is { } guess ? ByCase(by, t, cases, combos, timing, guess, notes) : timing.Read(by, t, cases, combos);
        var helperThreads = new List<Thread>();
        var feeder = new Thread(() =>
        {
            foreach (var link in helpers.AsReady())
            {
                var h = new Thread(() =>
                {
                    using var session = Etabs.PackSession.Start(link);
                    while (shared.TryDequeue(out var t))
                    {
                        try { got[t.Name] = ReadOne(link, t); Interlocked.Increment(ref byHelper); }
                        catch (Exception ex) when (ex is EtabsException or System.Runtime.InteropServices.COMException or InvalidOperationException)
                        {
                            own.Enqueue(t);
                            if (ex is not EtabsException) break; // the helper went away: it takes no more
                        }
                    }
                }) { IsBackground = true, Name = "CSI ProMax pack helper read" };
                lock (helperThreads) helperThreads.Add(h);
                h.Start();
            }
        }) { IsBackground = true, Name = "CSI ProMax pack helpers" };
        feeder.Start();
        // The window that ran the model reads too, from the same queue, then what only it can give.
        while (shared.TryDequeue(out var t) || own.TryDequeue(out t))
        {
            try { got[t.Name] = ReadOne(e, t); }
            catch (EtabsException ex) { Note($"'{t.Name}' could not be read ({ex.Message})."); }
        }
        // Helpers still starting are not waited for once the queue is empty; those reading are.
        List<Thread> reading;
        lock (helperThreads) reading = helperThreads.ToList();
        foreach (var h in reading) h.Join();
        while (own.TryDequeue(out var t))
        {
            try { got[t.Name] = ReadOne(e, t); }
            catch (EtabsException ex) { Note($"'{t.Name}' could not be read ({ex.Message})."); }
        }
        foreach (var t in todo)
            if (got.TryGetValue(t.Name, out var data) && data.Rows.Count > 0)
                tables[t.Name] = TableJson(t.Name, data, int.MaxValue, notes, null);
        timing.Helpers = new JsonObject
        {
            ["asked"] = helpers.Asked, ["tables_by_helpers"] = byHelper,
            ["said"] = new JsonArray(helpers.Said.Select(x => (JsonNode)x).ToArray()),
        };
        timing.Step("results read together");
    }

    /// <summary>How large a result table is likely to be, to read the largest first: shell, then frame, then joint element results.</summary>
    private static int Heavy(TableName t)
    {
        var n = ResultTools.Norm(t.Name);
        return (n.Contains("shell") || n.Contains("area") ? 4 : 0) + (n.StartsWith("element", StringComparison.Ordinal) ? 2 : 0) + (n.Contains("frame") ? 1 : 0) + (n.StartsWith("joint", StringComparison.Ordinal) ? 1 : 0);
    }

    /// <summary>
    /// SAP2000's frame stresses: a row for every stress point of the section at every station of every case, about
    /// <see cref="StressRowsPerForceRow"/> rows for each row of the frame forces (0.4.67-test1: 215,488 of them for
    /// 79 frames; 0.4.67-test4: the large model's did not come back within 180 s). Read when it fits.
    /// </summary>
    public const string Stresses = "Element Stresses - Frames", StressesFrom = "Element Forces - Frames";

    /// <summary>Rows of <see cref="Stresses"/> for each row of <see cref="StressesFrom"/> (test1: 215,488 / 23,569).</summary>
    public const double StressRowsPerForceRow = 9.2;

    /// <summary>
    /// The most rows asked of the program in one call (about 30,000 rows a second, so under a minute): a bigger table is
    /// read one load case or combination at a time, so that each call comes back (0.4.67-test4: SAP2000 gave nothing of
    /// 6 million rows asked at once within the 3 minutes a call may take) and is not held whole twice in memory.
    /// </summary>
    public const int HeavyMostRows = 1_500_000;

    /// <summary>
    /// How long the pack of a large model may take (Ahmed 2026-11-27: more than 3 minutes "is fine but not much"); the
    /// large-model field step holds it to this.
    /// </summary>
    public static readonly TimeSpan LargeMostTime = TimeSpan.FromMinutes(6);

    /// <summary>A table read one load case, then one combination, at a time: the rows of each, one after the other.</summary>
    private static TableData ByCase(ICsiLink e, TableName t, IReadOnlyList<string> cases, IReadOnlyList<string> combos, Timing timing, long guess, List<string> notes)
    {
        var caseList = cases.Count > 0 ? cases : e.LoadCases();
        var comboList = combos.Count > 0 ? combos : e.Combos();
        string[]? fields = null;
        var rows = new List<string[]>((int)Math.Min(guess, int.MaxValue / 2));
        void Add(TableData part) { fields ??= part.Fields.ToArray(); rows.AddRange(part.Rows); }
        foreach (var c in caseList) Add(timing.Read(e, t, [c], []));
        foreach (var c in comboList) Add(timing.Read(e, t, [], [c]));
        lock (notes) notes.Add($"'{t.Name}' ({rows.Count:N0} rows) was read one load case or combination at a time ({caseList.Count + comboList.Count} parts).");
        return new TableData(fields ?? [], rows);
    }

    /// <summary>The rows <paramref name="t"/> would have when that is more than <see cref="HeavyMostRows"/>; otherwise null.</summary>
    private static long? TooBig(ICsiLink e, TableName t, IReadOnlyList<TableName> known, IReadOnlyList<string> cases, IReadOnlyList<string> combos, JsonObject tables, Timing timing)
    {
        if (!string.Equals(t.Name, Stresses, StringComparison.OrdinalIgnoreCase)) return null;
        int forces;
        if (tables[StressesFrom]?["rows"] is JsonArray have) forces = have.Count + (tables[StressesFrom]?["left_out"]?.GetValue<int>() ?? 0);
        else
        {
            var from = known.FirstOrDefault(k => string.Equals(k.Name, StressesFrom, StringComparison.OrdinalIgnoreCase));
            if (from is null) return null;
            try { forces = timing.Read(e, from, cases, combos).Rows.Count; }
            catch (EtabsException) { return null; }
        }
        var guess = (long)Math.Round(forces * StressRowsPerForceRow);
        return guess > HeavyMostRows ? guess : null;
    }

    private static JsonObject TableJson(string name, TableData data, int most, List<string> notes, string? from)
    {
        var table = new JsonObject
        {
            ["name"] = name, ["fields"] = Texts(data.Fields),
            ["rows"] = new JsonArray(data.Rows.Take(most).Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)v).ToArray())).ToArray()),
        };
        if (from is not null) table["from"] = from;
        if (data.Rows.Count > most)
        {
            table["left_out"] = data.Rows.Count - most;
            notes.Add($"'{name}' has {data.Rows.Count} rows; the first {most} are in the pack. Choose fewer load cases to take it whole.");
        }
        return table;
    }

    /// <summary>
    /// The design results beyond the tables that had data: the API's own summaries for the families no table gave,
    /// then tables exported from the program into files (given, or beside the model). Says per family where its
    /// results came from, and what to do in the program for a family that has none.
    /// </summary>
    private static JsonObject AddDesign(ICsiLink e, ModelInfo info, IReadOnlyList<TableName> known, JsonObject tables, IReadOnlyList<string> files, int most, List<string> notes)
    {
        var from = new Dictionary<string, List<string>>();
        void Took(string family, string what) { if (!from.TryGetValue(family, out var l)) from[family] = l = []; if (!l.Contains(what)) l.Add(what); }
        foreach (var (name, _) in tables) if (DesignResults.FamilyOf(name) is { } f) Took(f.Key, "the program's design tables");
        var have = new HashSet<string>(from.Keys);
        var apiNotes = new List<string>();
        foreach (var (name, data) in DesignResults.FromApi(e, have, apiNotes))
        {
            tables[name] = TableJson(name, data, most, notes, "api");
            Took(DesignResults.FamilyOf(name)!.Key, "the API's design summary");
        }
        // Exported files fill what is still missing: a family the live tables have is not replaced by an older file.
        var paths = files.Concat(DesignResults.ExportsBeside(info.File)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in paths)
        {
            List<(string Name, TableData Data)> read;
            try { read = DesignResults.ReadExport(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
            {
                if (files.Contains(path, StringComparer.OrdinalIgnoreCase)) notes.Add($"The file {path} could not be read ({ex.Message}).");
                continue;
            }
            var file = Path.GetFileName(path);
            foreach (var (name, data) in read)
            {
                var family = DesignResults.FamilyOf(name);
                var walls = DesignResults.IsWallTable(name);
                if (family is null && !walls || data.Rows.Count == 0 || tables.ContainsKey(name)) continue;
                if (family is not null && have.Contains(family.Key)) continue;
                tables[name] = TableJson(name, data, most, notes, "file: " + file);
                if (family is not null) Took(family.Key, $"the exported file {file} ({File.GetLastWriteTime(path):yyyy-MM-dd HH:mm})");
            }
        }
        foreach (var family in from.Keys.Where(k => !have.Contains(k) && from[k].Any(w => w.StartsWith("the exported", StringComparison.Ordinal))))
            notes.Add($"{DesignResults.All.First(f => f.Key == family).Name} came from {string.Join(", ", from[family])}: check it is from the model as it is now.");
        // Families the model has tables for, but empty: the design was not run (or not kept) in the program.
        var waiting = known.Where(t => t.Empty && DesignResults.FamilyOf(t.Name) is { } f && !from.ContainsKey(f.Key)).Select(t => DesignResults.FamilyOf(t.Name)!).DistinctBy(f => f.Key).ToList();
        foreach (var f in waiting) notes.Add(HowToGet(f, e.Program));
        notes.AddRange(apiNotes.Take(3));
        var o = new JsonObject();
        foreach (var (k, list) in from) o[k] = new JsonObject { ["name"] = DesignResults.All.First(f => f.Key == k).Name, ["from"] = new JsonArray(list.Select(x => (JsonNode)x).ToArray()) };
        if (waiting.Count > 0) o["not_run"] = new JsonArray(waiting.Select(f => (JsonNode)f.Name).ToArray());
        return o;
    }

    /// <summary>What to do in the program to have a family's results in the next pack.</summary>
    internal static string HowToGet(DesignResults.Family f, CsiProgram program)
    {
        var menu = f.Key switch
        {
            "pier" or "spandrel" => "Design > Shear Wall Design > Start Design/Check",
            "steel" => "Design > Steel Frame Design > Start Design/Check",
            "concrete" => "Design > Concrete Frame Design > Start Design/Check",
            "composite" => "Design > Composite Beam Design > Start Design/Check",
            "compcol" => "Design > Composite Column Design > Start Design/Check",
            "joist" => "Design > Steel Joist Design > Start Design/Check",
            "slab" or "punching" => "Design > Concrete Slab Design > Start Design/Check",
            "connection" => "Design > Steel Connection Design",
            _ => "the Design menu",
        };
        return $"{f.Name} has no results in {program.Name} yet: run {menu} there, then make the pack again. If {program.Name} still gives none, " +
               "export the design tables (File > Export > tables to Excel) into the model's folder with the model's name: the next pack takes them from that file.";
    }

    private static double R(double v) => Math.Round(v, 5);
    private static JsonArray Texts(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());

    /// <summary>A table into the pack: its first <see cref="PartRows"/> rows in NNN.json, the rest in NNN-p2.json, NNN-p3.json...</summary>
    private static void AddTable(ZipArchive zip, string stem, JsonObject table)
    {
        if (table["rows"] is not JsonArray rows || rows.Count <= PartRows) { Add(zip, stem + ".json", table); return; }
        var parts = (rows.Count + PartRows - 1) / PartRows;
        void Entry(string name, Action<Utf8JsonWriter> write)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            using var w = new Utf8JsonWriter(s);
            write(w);
        }
        Entry(stem + ".json", w => WriteSlice(w, table, rows, 0, PartRows, x => x.WriteNumber("parts", parts)));
        for (int start = PartRows, part = 2; start < rows.Count; start += PartRows, part++)
        {
            var (from, to, n) = (start, Math.Min(rows.Count, start + PartRows), part);
            Entry($"{stem}-p{part}.json", w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("name"); if (table["name"] is { } name) name.WriteTo(w); else w.WriteNullValue();
                w.WriteNumber("part", n);
                w.WritePropertyName("rows");
                w.WriteStartArray();
                for (var i = from; i < to; i++) { if (rows[i] is { } r) r.WriteTo(w); else w.WriteNullValue(); }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }
    }

    private static void Add(ZipArchive zip, string name, JsonNode node)
    {
        // Written straight into the entry: never one text of the whole node (0.4.68 full test: the large model's pack
        // ran the test out of memory).
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var w = new Utf8JsonWriter(s);
        node.WriteTo(w);
    }

    /// <summary>A table's head (everything but its rows) with rows [from, to) of it, written without copying a row.</summary>
    private static void WriteSlice(Utf8JsonWriter w, JsonObject table, JsonArray rows, int from, int to, Action<Utf8JsonWriter>? more = null)
    {
        w.WriteStartObject();
        foreach (var (k, v) in table)
        {
            if (k == "rows") continue;
            w.WritePropertyName(k);
            if (v is null) w.WriteNullValue(); else v.WriteTo(w);
        }
        more?.Invoke(w);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        for (var i = from; i < to; i++) { if (rows[i] is { } r) r.WriteTo(w); else w.WriteNullValue(); }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Reads a pack back: its content as the Viewer gets it.</summary>
    public static JsonObject Read(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        JsonNode? Text(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return JsonNode.Parse(reader.ReadToEnd());
        }
        var content = new JsonObject
        {
            ["manifest"] = Text("manifest.json") ?? throw new InvalidDataException("This is not a CSI ProMax pack (it has no manifest)."),
            ["model"] = Text("model.json") ?? throw new InvalidDataException("This is not a CSI ProMax pack (it has no model)."),
            ["checks"] = Text("checks.json"),
            ["comments"] = Text("comments.json") ?? new JsonArray(),
        };
        if (Text("instabilities.json") is { } found) content["instabilities"] = found;
        var tables = new JsonObject();
        var parts = new List<JsonObject>();
        foreach (var entry in zip.Entries.Where(en => en.FullName.StartsWith("tables/", StringComparison.Ordinal)).OrderBy(en => en.FullName, StringComparer.Ordinal))
            if (Text(entry.FullName) is JsonObject t && t["name"] is { } name)
            {
                if (t["part"] is not null) parts.Add(t);
                else tables[name.GetValue<string>()] = t;
            }
        // The rest of a long table, in its parts' order, joined to its first rows.
        foreach (var part in parts.OrderBy(p => p["part"]!.GetValue<int>()))
            if (tables[part["name"]!.GetValue<string>()] is JsonObject whole && whole["rows"] is JsonArray into && part["rows"] is JsonArray more)
            {
                foreach (var row in more.ToList()) { more.Remove(row); into.Add(row); }
                whole.Remove("parts");
            }
        content["tables"] = tables;
        return content;
    }

    /// <summary>The Viewer page. With <paramref name="content"/> the pack is inside it, so the reviewer opens one file.</summary>
    public static string ViewerHtml(JsonObject? content = null)
    {
        if (content is null) return Page();
        using var stream = new MemoryStream();
        WriteViewerHtml(stream, content);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static string Page()
    {
        using var stream = typeof(Pack).Assembly.GetManifestResourceStream("CsiProMax.viewer.html")
            ?? throw new InvalidOperationException("The Viewer page is missing from this build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The Viewer page with the pack inside, written straight to <paramref name="to"/>: never one text of the whole pack
    /// (0.4.68 full test: the large model's page ran the test out of memory). A long table's rows past the first part go
    /// in script elements of their own (pack-rows), which the page joins back. JSON written with the default encoder has
    /// no "&lt;", so nothing in it can end or confuse the script element.
    /// </summary>
    public static void WriteViewerHtml(Stream to, JsonObject content)
    {
        var html = Page();
        const string slot = "<script id=\"pack\" type=\"application/json\"></script>";
        var at = html.IndexOf(slot, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("The Viewer page has no place for a pack.");
        var utf8 = new UTF8Encoding(false);
        void Text(string t) { var b = utf8.GetBytes(t); to.Write(b, 0, b.Length); }
        Text(html[..at]);
        Text("<script id=\"pack\" type=\"application/json\">");
        var big = new List<(string Name, JsonObject Table, JsonArray Rows)>();
        using (var w = new Utf8JsonWriter(to))
        {
            w.WriteStartObject();
            foreach (var (k, v) in content)
            {
                w.WritePropertyName(k);
                if (k != "tables" || v is not JsonObject tables) { if (v is null) w.WriteNullValue(); else v.WriteTo(w); continue; }
                w.WriteStartObject();
                foreach (var (name, node) in tables)
                {
                    w.WritePropertyName(name);
                    if (node is JsonObject t && t["rows"] is JsonArray rows && rows.Count > PartRows) { WriteSlice(w, t, rows, 0, PartRows); big.Add((name, t, rows)); }
                    else if (node is null) w.WriteNullValue();
                    else node.WriteTo(w);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        Text("</script>\n");
        foreach (var (name, _, rows) in big)
            for (var start = PartRows; start < rows.Count; start += PartRows)
            {
                Text("<script class=\"pack-rows\" type=\"application/json\">");
                using (var w = new Utf8JsonWriter(to))
                {
                    w.WriteStartObject();
                    w.WriteString("table", name);
                    w.WritePropertyName("rows");
                    w.WriteStartArray();
                    for (var i = start; i < Math.Min(rows.Count, start + PartRows); i++) { if (rows[i] is { } r) r.WriteTo(w); else w.WriteNullValue(); }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                Text("</script>\n");
            }
        Text(html[(at + slot.Length)..]);
    }

    /// <summary>How many bytes <paramref name="node"/> is as JSON, counted as it is written, never held.</summary>
    public static long JsonSize(JsonNode node)
    {
        using var count = new CountingStream();
        using (var w = new Utf8JsonWriter(count)) node.WriteTo(w);
        return count.Length;
    }

    private sealed class CountingStream : Stream
    {
        private long _n;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _n;
        public override long Position { get => _n; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _n += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _n += buffer.Length;
    }

    /// <summary>
    /// The most rows the Engine window is handed at once with a run's results (a browser holds about half a gigabyte of
    /// text in one string). A pack with more is handed with each long table's first part, marked with its whole row
    /// count and parts; the whole of it is in the .cpmx and its Viewer page beside the model.
    /// </summary>
    public static int WindowMostRows { get; set; } = 3_000_000;

    /// <summary>The pack as the Engine window is given it (see <see cref="WindowMostRows"/>); the pack itself when it fits.</summary>
    public static JsonObject ForWindow(JsonObject content)
    {
        if (content["tables"] is not JsonObject all) return content;
        var total = all.Sum(t => (long)((t.Value?["rows"] as JsonArray)?.Count ?? 0));
        if (total <= WindowMostRows) return content;
        var shown = new JsonObject();
        foreach (var (k, v) in content) if (k != "tables") shown[k] = v?.DeepClone();
        var tables = new JsonObject();
        foreach (var (name, node) in all)
        {
            if (node is not JsonObject t || t["rows"] is not JsonArray rows || rows.Count <= PartRows) { tables[name] = node?.DeepClone(); continue; }
            var head = new JsonObject();
            foreach (var (k, v) in t) if (k != "rows") head[k] = v?.DeepClone();
            head["rows"] = new JsonArray(Enumerable.Range(0, PartRows).Select(i => rows[i]?.DeepClone()).ToArray());
            head["rows_total"] = rows.Count;
            head["parts"] = (rows.Count + PartRows - 1) / PartRows;
            head["whole_in_pack"] = true;
            tables[name] = head;
        }
        shown["tables"] = tables;
        return shown;
    }

    /// <summary>Writes the Viewer page of <paramref name="content"/> to <paramref name="file"/>, streamed.</summary>
    public static void WriteViewerHtml(string file, JsonObject content)
    {
        using var f = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        WriteViewerHtml(f, content);
    }

    /// <summary>
    /// The Viewer page for the model that is open now, served on this PC: it asks for the model again whenever the
    /// revision moves, and keeps the reader's view. With <paramref name="chat"/> (the AI chat is on in the window that
    /// serves it) the page has a Chat tab: the window's own conversation.
    /// </summary>
    public static string LiveViewerHtml(JsonObject content, int revision, bool chat = false)
    {
        var html = ViewerHtml(content);
        // cpmPull asks at once (after a hand edit); otherwise every second and a half.
        var live = "<script>window.cpmLive=true;" + (chat ? "window.cpmChat=true;" : "") + "(function(){let rev=" + revision.ToString(CultureInfo.InvariantCulture) + ";window.cpmPull=async()=>{try{const r=await fetch('model?rev='+rev,{cache:'no-store'});" +
                   "if(r.status!==200)return;const j=await r.json();rev=j.rev;cpmLoad(j.pack,true);}catch(e){}};setInterval(window.cpmPull,1500);})();</script>";
        return html.Replace("</body>", live + "</body>", StringComparison.Ordinal);
    }

    /// <summary>The comments in a pack (.cpmx) or in a comments file the Viewer saved (.json).</summary>
    public static (string Model, JsonArray Comments) ReadComments(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            var content = Read(bytes);
            return (content["manifest"]?["model"]?.GetValue<string>() ?? "", content["comments"]!.DeepClone().AsArray());
        }
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        if (JsonNode.Parse(text) is not JsonObject o || o["comments"] is not JsonArray list)
            throw new InvalidDataException("This file has no review comments. Give a pack (.cpmx) or the comments file the Viewer saved.");
        return (o["model"]?.GetValue<string>() ?? "", list.DeepClone().AsArray());
    }
}
