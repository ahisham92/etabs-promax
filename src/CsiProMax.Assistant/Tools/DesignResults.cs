using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CsiProMax.Assistant.Etabs;
using CsiProMax.CsiTables;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Every design result the CSI programs give, for the review pack and the Viewer: steel frame, concrete frame (beams,
/// columns, joints), composite beam and column, steel joist, shear wall pier and spandrel, slab and punching shear.
/// ETABS's API has summary calls for steel and concrete frames only; shear wall, composite and slab design have none, so
/// the design tables (DatabaseTables) are the main source for all of them. Where a table is missing or empty, two more
/// ways are tried: the API's own summary calls (steel and concrete frames), and table files the engineer exported from
/// the program (File > Export > tables to Excel or to text) saved beside the model. Nothing here designs anything.
/// </summary>
public static class DesignResults
{
    /// <summary>A kind of design. On: what its rows belong to (frame, pier, spandrel, slab, point).</summary>
    public sealed record Family(string Key, string Name, string On);

    public static readonly Family Punching = new("punching", "Punching Shear", "point");
    public static readonly Family Pier = new("pier", "Shear Wall Pier Design", "pier");
    public static readonly Family Spandrel = new("spandrel", "Shear Wall Spandrel Design", "spandrel");
    public static readonly Family Joist = new("joist", "Steel Joist Design", "frame");
    public static readonly Family CompositeColumn = new("compcol", "Composite Column Design", "frame");
    public static readonly Family Composite = new("composite", "Composite Beam Design", "frame");
    public static readonly Family Connection = new("connection", "Steel Connection Design", "frame");
    public static readonly Family Slab = new("slab", "Concrete Slab Design", "slab");
    public static readonly Family Steel = new("steel", "Steel Frame Design", "frame");
    public static readonly Family Concrete = new("concrete", "Concrete Frame Design", "frame");
    public static readonly Family OtherFrame = new("otherframe", "Other Frame Design", "frame");

    public static IReadOnlyList<Family> All { get; } = [Steel, Concrete, Composite, CompositeColumn, Joist, Connection, Pier, Spandrel, Slab, Punching, OtherFrame];

    private static readonly string[] NotDesign = ["preference", "overwrite", "assign", "definition", "propert", "forces", "sectiondesigner", "rebarpref", "rebarsize", "checklist"];
    private static readonly string[] DesignWords = ["design", "summary", "envelope", "pmm", "ratio", "capacity"];

    /// <summary>The kind of design a table holds, by its name, or null for a table that is not a design result.</summary>
    public static Family? FamilyOf(string table)
    {
        var n = ResultTools.Norm(table);
        // What the engineer entered (design load combinations, overwrites, preferences) is not a result: one rule, Verdict's.
        if (Verdict.IsInputTable(table) || NotDesign.Any(w => n.Contains(w, StringComparison.Ordinal))) return null;
        bool Has(params string[] words) => words.Any(w => n.Contains(w, StringComparison.Ordinal));
        if (Has("punch")) return Punching;
        if (!Has(DesignWords) && !Has("strip", "reinforc", "rebar")) return null;
        if (Has("pier")) return Pier;
        if (Has("spandrel")) return Spandrel;
        if (Has("joist")) return Joist;
        if (Has("composite") && Has("column")) return CompositeColumn;
        if (Has("composite", "compbeam")) return Composite;
        if (Has("connection", "baseplate")) return Connection;
        if (Has("slab", "strip")) return Slab;
        if (Has("steel") && Has(DesignWords)) return Steel;
        if (Has("concrete") && (Has("beam", "column", "joint", "frame") || Has(DesignWords))) return Concrete;
        if (Has("aluminum", "coldformed", "coldform")) return OtherFrame;
        // SAFE's beam design and anything else that calls itself a design summary.
        if (Has("beam", "column") && Has("design")) return Concrete;
        if (Has("design") && Has("summary")) return OtherFrame;
        return null;
    }

    public static bool IsDesignTable(string table) => FamilyOf(table) is not null;

    /// <summary>Tables that say which walls (and columns, beams) make each pier and spandrel, and their sections.</summary>
    public static bool IsWallTable(string table)
    {
        var n = ResultTools.Norm(table);
        return (n.Contains("pier", StringComparison.Ordinal) || n.Contains("spandrel", StringComparison.Ordinal))
               && (n.Contains("label", StringComparison.Ordinal) || n.Contains("sectionpropert", StringComparison.Ordinal));
    }

    // ---- which wall belongs to which pier and spandrel ----

    /// <summary>
    /// Each object's pier and spandrel label: [type, name, pier, spandrel] with type "a" (area) or "f" (frame), for
    /// objects that have one. Read from the assignment tables first, then from the API one object at a time.
    /// </summary>
    public static JsonArray WallLabels(ICsiLink e, IReadOnlyList<TableName> known, List<string> notes)
    {
        var labels = new Dictionary<(string Type, string Name), (string Pier, string Spandrel)>();
        void Put(string type, string name, string? pier, string? spandrel)
        {
            if (name.Length == 0) return;
            labels.TryGetValue((type, name), out var was);
            labels[(type, name)] = (Real(pier) ?? was.Pier ?? "", Real(spandrel) ?? was.Spandrel ?? "");
        }
        foreach (var t in known.Where(t => !t.Empty && IsWallTable(t.Name) && ResultTools.Norm(t.Name).Contains("label", StringComparison.Ordinal)))
        {
            TableData data;
            try { data = Pack.ReadTable(e, t.Key, null, null); }
            catch (EtabsException) { continue; }
            var n = ResultTools.Norm(t.Name);
            var type = n.Contains("area", StringComparison.Ordinal) || n.Contains("wall", StringComparison.Ordinal) || n.Contains("shell", StringComparison.Ordinal) ? "a" : "f";
            var unique = Col(data.Fields, "UniqueName", "Unique Name", "Area", "Frame");
            var pier = Col(data.Fields, "Pier Name", "PierName", "Pier", "Pier Label", "PierLabel");
            var spandrel = Col(data.Fields, "Spandrel Name", "SpandrelName", "Spandrel", "Spandrel Label", "SpandrelLabel");
            if (unique < 0 || pier < 0 && spandrel < 0) continue;
            foreach (var r in data.Rows) Put(type, r[unique], pier >= 0 ? r[pier] : null, spandrel >= 0 ? r[spandrel] : null);
        }
        if (labels.Count == 0 && e is OfflineEtabs offline) foreach (var (type, name, p, s) in offline.WallLabels()) Put(type, name, p, s);
        if (labels.Count == 0 && e is ComEtabs) ReadWallLabelsByApi(e, Put, notes);
        return new JsonArray(labels.Where(l => l.Value.Pier.Length + l.Value.Spandrel.Length > 0).OrderBy(l => l.Key.Type).ThenBy(l => l.Key.Name, StringComparer.Ordinal)
            .Select(l => (JsonNode)new JsonArray(l.Key.Type, l.Key.Name, l.Value.Pier, l.Value.Spandrel)).ToArray());
    }

    private static string? Real(string? label) => string.IsNullOrWhiteSpace(label) || label.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) ? null : label.Trim();

    private static void ReadWallLabelsByApi(ICsiLink e, Action<string, string, string?, string?> put, List<string> notes)
    {
        var walls = e.Areas().Where(a => a.Kind == "wall").Select(a => a.Name).ToList();
        var frames = e.Frames().Select(f => f.Name).ToList();
        if (walls.Count == 0) return;
        try
        {
            e.Raw("read the pier and spandrel labels", m =>
            {
                foreach (var (type, list) in new[] { ("a", walls), ("f", frames) })
                    foreach (var name in list)
                    {
                        string pier = "", spandrel = "";
                        if (type == "a") { m.AreaObj.GetPier(name, ref pier); m.AreaObj.GetSpandrel(name, ref spandrel); }
                        else { m.FrameObj.GetPier(name, ref pier); m.FrameObj.GetSpandrel(name, ref spandrel); }
                        put(type, name, pier, spandrel);
                    }
                return null;
            });
        }
        catch (EtabsException ex) { notes.Add("The pier and spandrel labels could not be read, so the Viewer cannot put the wall design on the walls (" + ex.Message + ")."); }
    }

    // ---- the API's own design summaries, when the tables have none ----

    /// <summary>
    /// Steel and concrete frame design results through the API's summary calls, for the families the tables did not
    /// give. ETABS has no such call for shear walls, composite beams or slabs: those come only from the tables or an
    /// export. Each result is a table shaped like the program's own, named "... (from the API)".
    /// </summary>
    public static List<(string Name, TableData Data)> FromApi(ICsiLink e, ISet<string> have, List<string> notes)
    {
        var made = new List<(string, TableData)>();
        if (e is not (ComEtabs or Sap2000.SapLink)) return made; // SAP2000's link wraps ComEtabs: its design summaries are the same calls
        var stories = new Dictionary<string, string>(StringComparer.Ordinal);
        try { foreach (var f in e.Frames()) stories[f.Name] = f.Story; }
        catch (EtabsException) { }
        string StoryOf(string frame) => stories.TryGetValue(frame, out var s) ? s : "";
        if (!have.Contains(Steel.Key) && Try(e, "read the steel design results", m =>
            {
                int n = 0; string[] frame = [], combo = [], err = [], warn = []; double[] ratio = [], loc = []; int[] type = [];
                int ret = m.DesignSteel.GetSummaryResults("ALL", ref n, ref frame, ref ratio, ref type, ref loc, ref combo, ref err, ref warn, 1);
                if (ret != 0 || n == 0) return null;
                return new TableData(["Story", "UniqueName", "Ratio", "Ratio Type", "Location", "Combo", "Errors", "Warnings"],
                    Enumerable.Range(0, n).Select(i => new[] { StoryOf(frame[i]), frame[i], N(ratio[i]), RatioType(At(type, i)), N(At(loc, i)), At(combo, i) ?? "", At(err, i) ?? "", At(warn, i) ?? "" }).ToList());
            }, notes) is { } steel) made.Add(("Steel Frame Design Summary (from the API)", steel));
        if (!have.Contains(Concrete.Key))
        {
            if (Try(e, "read the concrete column design results", m =>
                {
                    int n = 0; string[] frame = [], pmmCombo = [], vCombo2 = [], vCombo3 = [], err = [], warn = []; int[] option = [];
                    double[] loc = [], pmmArea = [], pmmRatio = [], av2 = [], av3 = [];
                    int ret = m.DesignConcrete.GetSummaryResultsColumn("ALL", ref n, ref frame, ref option, ref loc, ref pmmCombo, ref pmmArea, ref pmmRatio,
                        ref vCombo2, ref av2, ref vCombo3, ref av3, ref err, ref warn, 1);
                    if (ret != 0 || n == 0) return null;
                    return new TableData(["Story", "UniqueName", "Design Option", "Location", "PMM Combo", "PMM Area", "PMM Ratio", "V Major Combo", "Av Major", "V Minor Combo", "Av Minor", "Errors", "Warnings"],
                        Enumerable.Range(0, n).Select(i => new[]
                        {
                            StoryOf(frame[i]), frame[i], At(option, i) == 1 ? "Check" : "Design", N(At(loc, i)), At(pmmCombo, i) ?? "", N(At(pmmArea, i)),
                            At(option, i) == 1 ? N(At(pmmRatio, i)) : "", At(vCombo2, i) ?? "", N(At(av2, i)), At(vCombo3, i) ?? "", N(At(av3, i)), At(err, i) ?? "", At(warn, i) ?? "",
                        }).ToList());
                }, notes) is { } columns) made.Add(("Concrete Column Design Summary (from the API)", columns));
            if (Try(e, "read the concrete beam design results", m =>
                {
                    int n = 0; string[] frame = [], topCombo = [], botCombo = [], vCombo = [], tlCombo = [], ttCombo = [], err = [], warn = [];
                    double[] loc = [], top = [], bot = [], av = [], tl = [], tt = [];
                    int ret = m.DesignConcrete.GetSummaryResultsBeam("ALL", ref n, ref frame, ref loc, ref topCombo, ref top, ref botCombo, ref bot,
                        ref vCombo, ref av, ref tlCombo, ref tl, ref ttCombo, ref tt, ref err, ref warn, 1);
                    if (ret != 0 || n == 0) return null;
                    return new TableData(["Story", "UniqueName", "Location", "As Top Combo", "As Top", "As Bottom Combo", "As Bottom", "Av Combo", "Av", "Torsion Al", "Torsion At", "Errors", "Warnings"],
                        Enumerable.Range(0, n).Select(i => new[]
                        {
                            StoryOf(frame[i]), frame[i], N(At(loc, i)), At(topCombo, i) ?? "", N(At(top, i)), At(botCombo, i) ?? "", N(At(bot, i)),
                            At(vCombo, i) ?? "", N(At(av, i)), N(At(tl, i)), N(At(tt, i)), At(err, i) ?? "", At(warn, i) ?? "",
                        }).ToList());
                }, notes) is { } beams) made.Add(("Concrete Beam Design Summary (from the API)", beams));
        }
        return made;
    }

    private static T? At<T>(T[]? list, int i) => list is not null && i < list.Length ? list[i] : default;
    private static string N(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string RatioType(int type) => type switch
    {
        1 => "PMM", 2 => "Major Shear", 3 => "Minor Shear", 4 => "Major Beam-Column Capacity", 5 => "Minor Beam-Column Capacity", 6 => "Other", _ => type.ToString(CultureInfo.InvariantCulture),
    };

    private static TableData? Try(ICsiLink e, string what, Func<dynamic, TableData?> work, List<string> notes)
    {
        try { return e.Raw(what, m => work(m)) as TableData; }
        catch (EtabsException ex) { notes.Add($"The API could not {what} ({ex.Message})."); return null; }
    }

    // ---- running the design ----

    private static readonly (string Kind, string Api, string Menu)[] Designs =
    [
        ("steel", "DesignSteel", "Design > Steel Frame Design > Start Design/Check"),
        ("concrete", "DesignConcrete", "Design > Concrete Frame Design > Start Design/Check"),
        ("composite", "DesignCompositeBeam", "Design > Composite Beam Design > Start Design/Check"),
        ("shear_wall", "DesignShearWall", "Design > Shear Wall Design > Start Design/Check"),
        ("slab", "DesignConcreteSlab", "Design > Concrete Slab Design > Start Design/Check"),
    ];

    /// <summary>
    /// Starts each kind of design through the API where it has a call for it (StartDesign). The API has none for some
    /// kinds in some versions (shear walls among them): those are listed with the menu that runs them in the program.
    /// </summary>
    public static JsonObject RunDesign(ICsiLink e, IReadOnlyList<string> kinds)
    {
        if (e is OfflineEtabs) throw new EtabsException(OfflineEtabs.NoAnalysis);
        if (!e.Info().Locked) throw new ToolInputException($"The analysis has not run in {e.Program.Name}, so there is nothing to design yet. Run the analysis first (run_analysis).");
        var wanted = kinds.Count == 0 ? Designs.Select(d => d.Kind).ToList() : kinds.Select(k => k.ToLowerInvariant().Replace(' ', '_')).ToList();
        var ran = new JsonArray(); var manual = new JsonArray(); var nothing = new JsonArray();
        foreach (var (kind, api, menu) in Designs.Where(d => wanted.Contains(d.Kind)))
        {
            string outcome;
            try
            {
                outcome = (string)e.Raw("start the " + kind.Replace('_', ' ') + " design", m =>
                {
                    dynamic design;
                    try { design = api switch { "DesignSteel" => m.DesignSteel, "DesignConcrete" => m.DesignConcrete, "DesignCompositeBeam" => m.DesignCompositeBeam, "DesignShearWall" => m.DesignShearWall, _ => m.DesignConcreteSlab }; }
                    catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { return "no api"; }
                    int ret;
                    try { ret = api == "DesignConcreteSlab" ? StartSlab(design) : design.StartDesign(); }
                    catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { return "no api"; }
                    return ret == 0 ? "ran" : "nothing";
                })!;
            }
            catch (EtabsException ex) { outcome = "failed: " + ex.Message; }
            // No API call for it (ETABS 23's shear walls): the program's own menu, picked as a person would.
            if (outcome == "no api" && ProcessOf(e) is int pid) outcome = ByMenu(e, pid, menu);
            if (outcome == "ran") ran.Add(kind);
            else if (outcome == "nothing") nothing.Add(new JsonObject { ["kind"] = kind, ["note"] = $"{e.Program.Name} did not design (no members of this kind, or no design code set). If there are, run {menu} there." });
            else manual.Add(new JsonObject { ["kind"] = kind, ["menu"] = menu, ["why"] = outcome == "no api" ? $"{e.Program.Name}'s API has no call to start this design" : outcome.StartsWith("no api; ", StringComparison.Ordinal) ? $"{e.Program.Name}'s API has no call to start this design, and " + outcome["no api; ".Length..] : outcome });
        }
        var result = new JsonObject { ["ran"] = ran, ["not_designed"] = nothing };
        if (manual.Count > 0)
        {
            result["run_in_program"] = manual;
            result["note"] = "Ask the engineer to run these from the menu given, then export_pack takes their results from the design tables. If the tables stay empty, the engineer can export them (File > Export > tables to Excel) into the model's folder with the model's name, and the pack takes them from that file.";
        }
        return result;
    }

    /// <summary>The process of the program behind the link, when it is a running program (never a stand-in).</summary>
    internal static Func<ICsiLink, int?> ProcessOf = e => e is ComEtabs c ? c.ProcessId : null;

    /// <summary>Picks the design's menu (MenuCommand); given so a test stands in for the program's window.</summary>
    internal static Func<int, IReadOnlyList<string>, string?> PickMenu = (pid, path) => MenuCommand.Pick(pid, path);

    /// <summary>
    /// Starts a design from the program's menu ("Design > Shear Wall Design > Start Design/Check") and waits for it: the
    /// program answers its API again only once the design is done, since both run on its window's thread.
    /// </summary>
    internal static string ByMenu(ICsiLink e, int pid, string menu)
    {
        var path = menu.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var why = PickMenu(pid, path);
        if (why is not null) return $"no api; the menu could not be picked either: {why}";
        Thread.Sleep(1500);
        try { e.Info(); } catch (EtabsException ex) { return "failed after the menu was picked: " + ex.Message; }
        return "ran";
    }

    /// <summary>SAFE names it StartSlabDesign; ETABS, where it has one, StartDesign.</summary>
    private static int StartSlab(dynamic design)
    {
        try { return (int)design.StartSlabDesign(); }
        catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { return (int)design.StartDesign(); }
    }

    // ---- table files the engineer exported from the program ----

    /// <summary>
    /// Table files beside the model that the program exported (File > Export > tables to Excel, or to a text file): the
    /// .xlsx and .txt files in the model's folder whose name starts with the model's name. Only files that hold CSI
    /// tables are read; anything else there is left alone.
    /// </summary>
    public static IEnumerable<string> ExportsBeside(string modelFile)
    {
        string? folder = null, model = null;
        try
        {
            if (modelFile.Length > 0 && Path.IsPathRooted(modelFile)) { folder = Path.GetDirectoryName(modelFile); model = Path.GetFileNameWithoutExtension(modelFile); }
        }
        catch (ArgumentException) { }
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(model) || !Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => (f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                            && Path.GetFileName(f).StartsWith(model, StringComparison.OrdinalIgnoreCase)
                            && new FileInfo(f).Length is > 0 and < 80_000_000)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(6).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The tables in an exported file: CSI's Excel layout (a sheet per table, "TABLE:  name" in the first
    /// row, then the field names, the units and the rows) or CSI's text layout (TABLE: "name", then Field=Value rows).</summary>
    public static List<(string Name, TableData Data)> ReadExport(string path)
    {
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return ReadXlsx(path);
        if (path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An Access file cannot be read here. Export the tables to Excel (.xlsx) or to a text file instead.");
        var text = File.ReadAllText(path);
        var tables = new List<(string, TableData)>();
        foreach (var (name, rows) in CsiTableText.Parse(text))
        {
            if (rows.Count == 0) continue;
            var fields = rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            tables.Add((Title(name), new TableData(fields, rows.Select(r => fields.Select(f => r.TryGetValue(f, out var v) ? v : "").ToArray()).ToList())));
        }
        return tables;
    }

    /// <summary>A table name the text file wrote in capitals, in the words the program shows it with.</summary>
    private static string Title(string name) =>
        name.Any(char.IsLower) ? name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());

    private static List<(string Name, TableData Data)> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument? Xml(string name) { var en = zip.GetEntry(name); if (en is null) return null; using var s = en.Open(); return XDocument.Load(s); }
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships", pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        var shared = (Xml("xl/sharedStrings.xml")?.Root?.Elements(main + "si") ?? []).Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value))).ToList();
        var targets = (Xml("xl/_rels/workbook.xml.rels")?.Root?.Elements(pkg + "Relationship") ?? []).ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        var tables = new List<(string, TableData)>();
        foreach (var sheet in Xml("xl/workbook.xml")?.Root?.Element(main + "sheets")?.Elements(main + "sheet") ?? [])
        {
            if (!targets.TryGetValue((string?)sheet.Attribute(rel + "id") ?? "", out var target)) continue;
            var entry = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var doc = Xml(entry);
            if (doc is null) continue;
            var rows = new List<string[]>();
            foreach (var row in doc.Descendants(main + "row"))
            {
                var cells = new SortedDictionary<int, string>();
                foreach (var c in row.Elements(main + "c"))
                {
                    var at = ColumnOf((string?)c.Attribute("r") ?? "");
                    var t = (string?)c.Attribute("t");
                    var v = c.Element(main + "v")?.Value ?? "";
                    cells[at < 0 ? cells.Count : at] = t switch
                    {
                        "s" => int.TryParse(v, out var k) && k < shared.Count ? shared[k] : "",
                        "inlineStr" => string.Concat(c.Descendants(main + "t").Select(x => x.Value)),
                        _ => v,
                    };
                }
                var width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
                var line = new string[width];
                for (var i = 0; i < width; i++) line[i] = cells.TryGetValue(i, out var s) ? s : "";
                rows.Add(line);
            }
            // CSI's layout: "TABLE:  name", the field names, the units, then the rows.
            var first = rows.FindIndex(r => r.Length > 0 && r[0].TrimStart().StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase));
            if (first < 0 || first + 1 >= rows.Count) continue;
            var name = rows[first][0].Trim()[6..].Trim().Trim('"');
            var fields = rows[first + 1].Select(f => f.Trim()).ToList();
            while (fields.Count > 0 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1);
            if (fields.Count == 0) continue;
            var start = first + 2;
            // The units row: no value in it is a number, and it is not the first row of data.
            if (start < rows.Count && rows[start].All(v => v.Length == 0 || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) && LooksLikeUnits(rows[start])) start++;
            var data = rows.Skip(start).Where(r => r.Any(v => v.Length > 0)).Select(r => Enumerable.Range(0, fields.Count).Select(i => i < r.Length ? r[i] : "").ToArray()).ToList();
            tables.Add((name, new TableData(fields, data)));
        }
        return tables;
    }

    private static bool LooksLikeUnits(string[] row) => row.Any(v => v.Length > 0 && (v.Contains('/') || v.Contains('-') || v is "m" or "mm" or "cm" or "ft" or "in" or "kN" or "N" or "kip" or "lb" or "tonf" or "kgf" or "sec" or "Unitless" or "%" || v.Contains("²") || v.Contains('^')))
                                                    || row.All(v => v.Length == 0);

    private static int ColumnOf(string reference)
    {
        var n = 0; var any = false;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') { n = n * 26 + (ch - 'A' + 1); any = true; }
            else break;
        }
        return any ? n - 1 : -1;
    }

    internal static int Col(IReadOnlyList<string> fields, params string[] names)
    {
        foreach (var name in names)
        {
            var want = ResultTools.Norm(name);
            for (var i = 0; i < fields.Count; i++) if (ResultTools.Norm(fields[i]) == want) return i;
        }
        return -1;
    }

    // ---- a summary of what design a model has ----

    /// <summary>Per family: the tables, how many rows, and whether a ratio was found. For the field test and the chat.</summary>
    public static JsonObject Survey(IReadOnlyList<TableName> known)
    {
        var o = new JsonObject();
        foreach (var group in known.Where(t => IsDesignTable(t.Name)).GroupBy(t => FamilyOf(t.Name)!.Key))
            o[group.Key] = new JsonObject
            {
                ["with_data"] = new JsonArray(group.Where(t => !t.Empty).Select(t => (JsonNode)t.Name).ToArray()),
                ["empty"] = new JsonArray(group.Where(t => t.Empty).Select(t => (JsonNode)t.Name).ToArray()),
            };
        return o;
    }
}
