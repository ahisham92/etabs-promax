using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>The assistant's tools on the open ETABS model. Each works on many objects in one call.</summary>
public static class EtabsTools
{
    public const int DefaultRows = 100;

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("get_model_context",
            $"The open {Csi.Name} model at a glance: file, units, stories with elevations, how many columns, beams, braces, floors and walls, the section, material, load pattern, load case and combination names, and what is selected. Call it once at the start of a conversation.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => Context(e)),

        new("query_objects",
            "Lists columns, beams, braces, floors or walls with their section, story and coordinates. Filter by kind, story, section, names, or what is selected. Gives the total count, counts by section, and up to 'limit' objects.",
            """
            {"type":"object","properties":{
              "kind":{"type":"string","enum":["column","beam","brace","floor","wall","all"],"description":"Default all."},
              "story":{"type":"string","description":"Frames only: the story name."},
              "section":{"type":"string"},
              "names":{"type":"array","items":{"type":"string"}},
              "selected_only":{"type":"boolean"},
              "limit":{"type":"integer","description":"How many objects to list (default 100). 0 gives counts only."}}}
            """, Ask.Never, Query),

        new("select_objects",
            $"Selects frames and areas in {Csi.Name} by name, so the engineer sees them highlighted.",
            """
            {"type":"object","properties":{
              "frames":{"type":"array","items":{"type":"string"}},
              "areas":{"type":"array","items":{"type":"string"}},
              "keep_selection":{"type":"boolean","description":"Add to what is selected. Default: the selection is cleared first."}}}
            """, Ask.Never, Select),

        new("list_tables",
            $"Lists {Csi.Name}'s own tables (the ones under Display > Show Tables): model definitions, assignments, analysis results, design results. Give 'contains' to search by name. Tables with no data are left out unless include_empty is true.",
            """{"type":"object","properties":{"contains":{"type":"string"},"include_empty":{"type":"boolean"}}}""", Ask.Never, ListTables),

        new("read_table",
            $"Reads one {Csi.Name} table by its name or key from list_tables: reactions, story drifts, modal periods, member forces, design summaries, section definitions and so on. Results need a finished analysis. Choose load cases or combinations for result tables, 'fields' to keep columns, 'where' to keep rows whose field equals a value, and offset/limit to page.",
            """
            {"type":"object","required":["table"],"properties":{
              "table":{"type":"string","description":"Table name or key, as list_tables gives it."},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}},
              "fields":{"type":"array","items":{"type":"string"}},
              "where":{"type":"object","description":"Field name to the value it must equal, e.g. {\"Story\":\"Story3\"}.","additionalProperties":{"type":"string"}},
              "offset":{"type":"integer"},"limit":{"type":"integer","description":"Rows to return (default 100)."}}}
            """, Ask.Never, ReadTable),

        new("add_columns",
            "Adds columns at plan points, one per story, each from the story below up to that story.",
            """
            {"type":"object","required":["points","section"],"properties":{
              "points":{"type":"array","description":"Plan points as [x, y].","items":{"type":"array","items":{"type":"number"}}},
              "stories":{"type":"array","items":{"type":"string"},"description":"Story names. Default: every story."},
              "section":{"type":"string","description":"A frame section name from get_model_context."}}}
            """, Ask.Changes, AddColumns, a => $"Add columns ({a.Text("section")}) at {Count(a, "points")} points"),

        new("add_beams",
            "Adds beams along plan lines at the elevation of each story given.",
            """
            {"type":"object","required":["lines","section"],"properties":{
              "lines":{"type":"array","description":"Each line as [x1, y1, x2, y2].","items":{"type":"array","items":{"type":"number"}}},
              "stories":{"type":"array","items":{"type":"string"},"description":"Story names. Default: every story."},
              "section":{"type":"string"}}}
            """, Ask.Changes, AddBeams, a => $"Add beams ({a.Text("section")}) on {Count(a, "lines")} lines"),

        new("add_slabs",
            "Adds floor slabs: each outline is a list of plan points, placed at the elevation of each story given.",
            """
            {"type":"object","required":["outlines","section"],"properties":{
              "outlines":{"type":"array","description":"Each outline as [[x, y], [x, y], ...], three points or more, in order.","items":{"type":"array","items":{"type":"array","items":{"type":"number"}}}},
              "stories":{"type":"array","items":{"type":"string"},"description":"Story names. Default: every story."},
              "section":{"type":"string","description":"A slab section name."}}}
            """, Ask.Changes, AddSlabs, a => $"Add slabs ({a.Text("section")}), {Count(a, "outlines")} per story"),

        new("add_walls",
            "Adds walls along plan lines, one panel per story, each from the story below up to that story.",
            """
            {"type":"object","required":["lines","section"],"properties":{
              "lines":{"type":"array","description":"Each line as [x1, y1, x2, y2].","items":{"type":"array","items":{"type":"number"}}},
              "stories":{"type":"array","items":{"type":"string"},"description":"Story names. Default: every story."},
              "section":{"type":"string","description":"A wall section name."}}}
            """, Ask.Changes, AddWalls, a => $"Add walls ({a.Text("section")}) on {Count(a, "lines")} lines"),

        new("set_section",
            "Gives frames (columns, beams, braces) or areas (floors, walls) another section.",
            """
            {"type":"object","required":["section"],"properties":{
              "frames":{"type":"array","items":{"type":"string"}},
              "areas":{"type":"array","items":{"type":"string"}},
              "section":{"type":"string"}}}
            """, Ask.Changes, SetSection, a => $"Change {Count(a, "frames") + Count(a, "areas")} objects to section {a.Text("section")}"),

        new("set_supports",
            "Sets supports: fixed, pinned or free. At named points, or at the bottom of every column and wall on the base (at_base).",
            """
            {"type":"object","required":["type"],"properties":{
              "type":{"type":"string","enum":["fixed","pinned","free"]},
              "points":{"type":"array","items":{"type":"string"}},
              "at_base":{"type":"boolean"}}}
            """, Ask.Changes, SetSupports, a => $"Set {a.Text("type")} supports"),

        new("add_load_pattern",
            $"Adds a load pattern ({Csi.Name} adds its load case with it).",
            """
            {"type":"object","required":["name","type"],"properties":{
              "name":{"type":"string"},
              "type":{"type":"string","description":"Dead, Super Dead, Live, Reducible Live, Roof Live, Seismic, Wind, Snow, Temperature, Notional or Other."},
              "self_weight":{"type":"number","description":"Self weight multiplier. Default 0."}}}
            """, Ask.Changes, AddPattern, a => $"Add load pattern {a.Text("name")} ({a.Text("type")})"),

        new("set_loads",
            "Puts a uniform gravity load on frames (force per length) or areas (force per area), in the model's units. Positive is down.",
            """
            {"type":"object","required":["pattern","value"],"properties":{
              "frames":{"type":"array","items":{"type":"string"}},
              "areas":{"type":"array","items":{"type":"string"}},
              "pattern":{"type":"string"},
              "value":{"type":"number"},
              "add":{"type":"boolean","description":"Add to the load already there. Default: replace it."}}}
            """, Ask.Changes, SetLoads, a => $"Load {Count(a, "frames") + Count(a, "areas")} objects with {a.Number("value", 0)} in {a.Text("pattern")}"),

        new("add_combination",
            "Adds a linear load combination from load cases and factors.",
            """
            {"type":"object","required":["name","cases"],"properties":{
              "name":{"type":"string"},
              "cases":{"type":"array","items":{"type":"object","required":["case","factor"],"properties":{"case":{"type":"string"},"factor":{"type":"number"}}}}}}
            """, Ask.Changes, AddCombo, a => $"Add combination {a.Text("name")}"),

        new("delete_objects",
            "Deletes frames and areas by name. The engineer is always asked first.",
            """{"type":"object","properties":{"frames":{"type":"array","items":{"type":"string"}},"areas":{"type":"array","items":{"type":"string"}}}}""",
            Ask.Always, Delete, a => "Delete " + string.Join(" and ", new[] { Plural(Count(a, "frames"), "frame"), Plural(Count(a, "areas"), "area") }.Where(t => t.Length > 0))),

        new("run_analysis",
            $"Runs the analysis in {Csi.Name} ({Csi.Name}'s own solver, on its license). The model must be saved. The engineer is always asked first. It can take a while.",
            """{"type":"object","properties":{}}""", Ask.Always,
            (_, e) => { e.RunAnalysis(); return new JsonObject { ["done"] = true, ["note"] = "The analysis ran. Read results with read_table." }; }, _ => "Run the analysis"),

        new("run_design",
            "Runs the design in the program after the analysis (the program's own design, on its licence): steel frame, concrete frame, composite beam, shear wall, as the model has them, or the ones in 'kinds'. The engineer is always asked first. A kind whose design the API cannot start is listed with the menu to use instead. Then design_ratios and export_pack read the results.",
            """{"type":"object","properties":{"kinds":{"type":"array","items":{"type":"string","enum":["steel","concrete","composite","shear_wall","slab"]}}}}""",
            Ask.Always, (a, e) => DesignResults.RunDesign(e, a.Texts("kinds")), a => "Run the design" + (a.Texts("kinds") is { Count: > 0 } k ? " (" + string.Join(", ", k) + ")" : "")),

        new("save_model",
            $"Saves the model in {Csi.Name} under its current file name. The engineer is always asked first.",
            """{"type":"object","properties":{}}""", Ask.Always,
            (_, e) => { e.Save(); return new JsonObject { ["saved"] = e.Info().File }; }, _ => "Save the model"),

        .. ResultTools.All,
    ];

    private static string Plural(int count, string word) => count == 0 ? "" : count == 1 ? "1 " + word : $"{count} {word}s";

    private static int Count(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    private static JsonArray List(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());

    private static JsonNode Context(ICsiLink e)
    {
        var info = e.Info();
        var frames = e.Frames();
        var areas = e.Areas();
        var selected = e.Selected();
        return new JsonObject
        {
            ["file"] = info.File,
            // The program the model is open in, by its own name: this tool answers in ETABS, SAFE and SAP2000 alike.
            ["program"] = e is OfflineEtabs ? "CSI ProMax Modeler" : e.Program.Name,
            ["version"] = info.Version,
            ["units"] = new JsonObject { ["force"] = info.Force, ["length"] = info.Length, ["temperature"] = info.Temperature },
            ["locked"] = info.Locked,
            ["stories"] = new JsonArray(e.Stories().Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["elevation"] = Args.Round(s.Elevation), ["height"] = Args.Round(s.Height) }).ToArray()),
            ["counts"] = new JsonObject
            {
                ["columns"] = frames.Count(f => f.Kind == "column"), ["beams"] = frames.Count(f => f.Kind == "beam"), ["braces"] = frames.Count(f => f.Kind == "brace"),
                ["floors"] = areas.Count(a => a.Kind == "floor"), ["walls"] = areas.Count(a => a.Kind == "wall"),
            },
            ["frame_sections"] = List(e.FrameSections()),
            ["slab_and_wall_sections"] = List(e.AreaSections()),
            ["materials"] = List(e.Materials()),
            ["load_patterns"] = new JsonArray(e.LoadPatterns().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["self_weight"] = p.SelfWeight }).ToArray()),
            ["load_cases"] = List(e.LoadCases()),
            ["combinations"] = List(e.Combos()),
            ["selected"] = new JsonArray(selected.Take(200).Select(s => (JsonNode)new JsonObject { ["kind"] = s.Kind, ["name"] = s.Name }).ToArray()),
            ["selected_count"] = selected.Count,
        };
    }

    private static JsonNode Query(JsonElement a, ICsiLink e)
    {
        var kind = a.Text("kind", "all");
        var story = a.Text("story");
        var section = a.Text("section");
        var names = a.Texts("names").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var limit = a.Whole("limit", DefaultRows);
        HashSet<string>? selectedFrames = null, selectedAreas = null;
        if (a.Flag("selected_only"))
        {
            var selected = e.Selected();
            selectedFrames = selected.Where(s => s.Kind == "frame").Select(s => s.Name).ToHashSet();
            selectedAreas = selected.Where(s => s.Kind == "area").Select(s => s.Name).ToHashSet();
        }
        var rows = new List<(string Section, JsonObject Row)>();
        if (kind is "all" or "column" or "beam" or "brace")
            foreach (var f in e.Frames())
            {
                if (kind != "all" && f.Kind != kind) continue;
                if (story.Length > 0 && !string.Equals(f.Story, story, StringComparison.OrdinalIgnoreCase)) continue;
                if (section.Length > 0 && !string.Equals(f.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                if (names.Count > 0 && !names.Contains(f.Name)) continue;
                if (selectedFrames is not null && !selectedFrames.Contains(f.Name)) continue;
                var length = Math.Sqrt(Math.Pow(f.X2 - f.X1, 2) + Math.Pow(f.Y2 - f.Y1, 2) + Math.Pow(f.Z2 - f.Z1, 2));
                rows.Add((f.Section, new JsonObject
                {
                    ["name"] = f.Name, ["kind"] = f.Kind, ["section"] = f.Section, ["story"] = f.Story, ["length"] = Args.Round(length),
                    ["from"] = new JsonArray(Args.Round(f.X1), Args.Round(f.Y1), Args.Round(f.Z1)), ["to"] = new JsonArray(Args.Round(f.X2), Args.Round(f.Y2), Args.Round(f.Z2)),
                }));
            }
        if (story.Length == 0 && kind is "all" or "floor" or "wall")
            foreach (var area in e.Areas())
            {
                if (kind != "all" && area.Kind != kind) continue;
                if (section.Length > 0 && !string.Equals(area.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                if (names.Count > 0 && !names.Contains(area.Name)) continue;
                if (selectedAreas is not null && !selectedAreas.Contains(area.Name)) continue;
                rows.Add((area.Section, new JsonObject
                {
                    ["name"] = area.Name, ["kind"] = area.Kind, ["section"] = area.Section,
                    ["corners"] = new JsonArray(area.Corners.Select(c => (JsonNode)new JsonArray(Args.Round(c.X), Args.Round(c.Y), Args.Round(c.Z))).ToArray()),
                }));
            }
        var result = new JsonObject
        {
            ["count"] = rows.Count,
            ["by_section"] = new JsonObject(rows.GroupBy(r => r.Section).OrderByDescending(g => g.Count()).Select(g => KeyValuePair.Create(g.Key.Length > 0 ? g.Key : "(none)", (JsonNode?)g.Count()))),
            ["objects"] = new JsonArray(rows.Take(Math.Max(0, limit)).Select(r => (JsonNode)r.Row).ToArray()),
        };
        if (rows.Count > limit && limit > 0) result["note"] = $"Showing {limit} of {rows.Count}. Narrow the filter, or raise limit.";
        if (story.Length > 0 && kind is "all" or "floor" or "wall") result["areas_note"] = "Floors and walls are not filtered by story: ask without story and use the corner elevations.";
        return result;
    }

    private static JsonNode Select(JsonElement a, ICsiLink e)
    {
        if (!a.Flag("keep_selection")) e.ClearSelection();
        var frames = a.Texts("frames");
        var areas = a.Texts("areas");
        var failed = new JsonArray();
        var done = 0;
        foreach (var (kind, name) in frames.Select(f => ("frame", f)).Concat(areas.Select(x => ("area", x))))
            try { e.Select(kind, name); done++; }
            catch (EtabsException ex) { failed.Add($"{name}: {ex.Message}"); }
        e.Refresh();
        return new JsonObject { ["selected"] = done, ["failed"] = failed };
    }

    private static JsonNode ListTables(JsonElement a, ICsiLink e)
    {
        var contains = a.Text("contains");
        var all = a.Flag("include_empty");
        var tables = e.Tables().Where(t => (all || !t.Empty) && (contains.Length == 0 || t.Name.Contains(contains, StringComparison.OrdinalIgnoreCase) || t.Key.Contains(contains, StringComparison.OrdinalIgnoreCase))).ToList();
        return new JsonObject { ["count"] = tables.Count, ["tables"] = List(tables.Take(400).Select(t => t.Key == t.Name ? t.Name : $"{t.Name} [{t.Key}]")) };
    }

    private static JsonNode ReadTable(JsonElement a, ICsiLink e)
    {
        var asked = a.Need("table");
        var known = e.Tables();
        var table = known.FirstOrDefault(t => string.Equals(t.Key, asked, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, asked, StringComparison.OrdinalIgnoreCase));
        if (table is null)
        {
            var close = known.Where(t => t.Name.Contains(asked, StringComparison.OrdinalIgnoreCase)).Take(8).Select(t => t.Name).ToList();
            throw new ToolInputException($"There is no table '{asked}'." + (close.Count > 0 ? " Close names: " + string.Join("; ", close) + "." : " Find it with list_tables."));
        }
        var data = e.ReadTable(table.Key, a.Texts("cases"), a.Texts("combinations"));
        var fields = data.Fields.ToList();
        IEnumerable<string[]> rows = data.Rows;
        if (a.TryGetProperty("where", out var where) && where.ValueKind == JsonValueKind.Object)
            foreach (var rule in where.EnumerateObject())
            {
                var at = fields.FindIndex(f => string.Equals(f, rule.Name, StringComparison.OrdinalIgnoreCase));
                if (at < 0) throw new ToolInputException($"The table has no field '{rule.Name}'. Its fields: {string.Join(", ", fields)}.");
                var value = rule.Value.ToString();
                rows = rows.Where(r => string.Equals(r[at], value, StringComparison.OrdinalIgnoreCase));
            }
        var keep = Enumerable.Range(0, fields.Count).ToList();
        var wanted = a.Texts("fields");
        if (wanted.Count > 0)
        {
            keep = wanted.Select(w => fields.FindIndex(f => string.Equals(f, w, StringComparison.OrdinalIgnoreCase))).ToList();
            if (keep.Contains(-1)) throw new ToolInputException($"The table has no field '{wanted[keep.IndexOf(-1)]}'. Its fields: {string.Join(", ", fields)}.");
        }
        var matched = rows.ToList();
        var offset = Math.Max(0, a.Whole("offset", 0));
        var limit = Math.Max(1, a.Whole("limit", DefaultRows));
        var result = new JsonObject
        {
            ["table"] = table.Name,
            ["fields"] = List(keep.Select(i => fields[i])),
            ["row_count"] = matched.Count,
            ["rows"] = new JsonArray(matched.Skip(offset).Take(limit).Select(r => (JsonNode)List(keep.Select(i => r[i]))).ToArray()),
        };
        if (matched.Count > offset + limit) result["note"] = $"Rows {offset + 1} to {offset + limit} of {matched.Count}. Pass offset={offset + limit} for the next ones.";
        if (matched.Count == 0 && data.Rows.Count == 0) result["note"] = "The table is empty. Result tables need a finished analysis and a load case or combination chosen.";
        return result;
    }

    /// <summary>The stories to work on, each with the elevation below it.</summary>
    private static List<(StoryRow Story, double Below)> Levels(JsonElement a, ICsiLink e)
    {
        var stories = e.Stories();
        if (stories.Count < 2) throw new ToolInputException("The model has no stories yet.");
        var wanted = a.Texts("stories");
        var levels = new List<(StoryRow, double)>();
        for (var i = 1; i < stories.Count; i++)
            if (wanted.Count == 0 || wanted.Contains(stories[i].Name, StringComparer.OrdinalIgnoreCase)) levels.Add((stories[i], stories[i - 1].Elevation));
        var unknown = wanted.Where(w => !stories.Skip(1).Any(s => string.Equals(s.Name, w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0) throw new ToolInputException($"No story named {string.Join(", ", unknown)}. The stories are: {string.Join(", ", stories.Skip(1).Select(s => s.Name))}.");
        return levels;
    }

    private static string Section(JsonElement a, IReadOnlyList<string> known, string what)
    {
        var section = a.Need("section");
        return known.FirstOrDefault(k => string.Equals(k, section, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolInputException($"There is no {what} section '{section}'. The model has: {string.Join(", ", known)}.");
    }

    private static (double X1, double Y1, double X2, double Y2) Line(JsonElement l)
    {
        if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() < 4) throw new ToolInputException("A line is [x1, y1, x2, y2].");
        return (l[0].GetDouble(), l[1].GetDouble(), l[2].GetDouble(), l[3].GetDouble());
    }

    private static JsonNode Added(ICsiLink e, List<string> names, string what)
    {
        e.Refresh();
        return new JsonObject { ["added"] = names.Count, ["what"] = what, ["names"] = List(names.Take(300)) };
    }

    private static JsonNode AddColumns(JsonElement a, ICsiLink e)
    {
        var section = Section(a, e.FrameSections(), "frame");
        var points = a.Items("points").Select(p => p.Point()).ToList();
        var names = new List<string>();
        foreach (var (story, below) in Levels(a, e))
            foreach (var (x, y) in points) names.Add(e.AddFrame(x, y, below, x, y, story.Elevation, section));
        return Added(e, names, "columns");
    }

    private static JsonNode AddBeams(JsonElement a, ICsiLink e)
    {
        var section = Section(a, e.FrameSections(), "frame");
        var lines = a.Items("lines").Select(Line).ToList();
        var names = new List<string>();
        foreach (var (story, _) in Levels(a, e))
            foreach (var l in lines) names.Add(e.AddFrame(l.X1, l.Y1, story.Elevation, l.X2, l.Y2, story.Elevation, section));
        return Added(e, names, "beams");
    }

    private static JsonNode AddSlabs(JsonElement a, ICsiLink e)
    {
        var section = Section(a, e.AreaSections(), "slab");
        var outlines = a.Items("outlines").Select(o => o.EnumerateArray().Select(p => p.Point()).ToList()).ToList();
        if (outlines.Any(o => o.Count < 3)) throw new ToolInputException("A slab outline needs three points or more.");
        var names = new List<string>();
        foreach (var (story, _) in Levels(a, e))
            foreach (var outline in outlines) names.Add(e.AddArea(outline.Select(p => (p.X, p.Y, story.Elevation)).ToList(), section));
        return Added(e, names, "slabs");
    }

    private static JsonNode AddWalls(JsonElement a, ICsiLink e)
    {
        var section = Section(a, e.AreaSections(), "wall");
        var lines = a.Items("lines").Select(Line).ToList();
        var names = new List<string>();
        foreach (var (story, below) in Levels(a, e))
            foreach (var l in lines)
                names.Add(e.AddArea([(l.X1, l.Y1, below), (l.X2, l.Y2, below), (l.X2, l.Y2, story.Elevation), (l.X1, l.Y1, story.Elevation)], section));
        return Added(e, names, "walls");
    }

    /// <summary>Runs one step per name, keeps going after a failure, and reports each one that failed.</summary>
    private static JsonNode Each(ICsiLink e, IEnumerable<(string Name, Action Step)> steps)
    {
        var failed = new JsonArray();
        var done = 0;
        foreach (var (name, step) in steps)
            try { step(); done++; }
            catch (EtabsException ex) { failed.Add($"{name}: {ex.Message}"); }
        e.Refresh();
        return new JsonObject { ["done"] = done, ["failed"] = failed };
    }

    private static JsonNode SetSection(JsonElement a, ICsiLink e)
    {
        var frames = a.Texts("frames");
        var areas = a.Texts("areas");
        if (frames.Count + areas.Count == 0) throw new ToolInputException("Give 'frames' or 'areas' to change.");
        var frameSection = frames.Count > 0 ? Section(a, e.FrameSections(), "frame") : "";
        var areaSection = areas.Count > 0 ? Section(a, e.AreaSections(), "slab or wall") : "";
        return Each(e, frames.Select(f => (f, (Action)(() => e.SetFrameSection(f, frameSection))))
            .Concat(areas.Select(x => (x, (Action)(() => e.SetAreaSection(x, areaSection))))));
    }

    private static JsonNode SetSupports(JsonElement a, ICsiLink e)
    {
        var type = a.Need("type");
        bool[] restraint = type switch
        {
            "fixed" => [true, true, true, true, true, true],
            "pinned" => [true, true, true, false, false, false],
            "free" => [false, false, false, false, false, false],
            _ => throw new ToolInputException("type is fixed, pinned or free."),
        };
        var points = a.Texts("points");
        if (a.Flag("at_base"))
        {
            var stories = e.Stories();
            var baseZ = stories.Count > 0 ? stories[0].Elevation : 0;
            var ends = new List<(double X, double Y)>();
            foreach (var f in e.Frames().Where(f => f.Kind != "beam"))
            {
                if (Math.Abs(f.Z1 - baseZ) < 1e-6) ends.Add((f.X1, f.Y1));
                if (Math.Abs(f.Z2 - baseZ) < 1e-6) ends.Add((f.X2, f.Y2));
            }
            foreach (var w in e.Areas().Where(w => w.Kind == "wall"))
                ends.AddRange(w.Corners.Where(c => Math.Abs(c.Z - baseZ) < 1e-6).Select(c => (c.X, c.Y)));
            points.AddRange(e.Points().Where(p => Math.Abs(p.Z - baseZ) < 1e-6 && ends.Any(x => Math.Abs(x.X - p.X) < 1e-6 && Math.Abs(x.Y - p.Y) < 1e-6)).Select(p => p.Name));
        }
        points = points.Distinct().ToList();
        if (points.Count == 0) throw new ToolInputException("No points: give 'points', or at_base with columns or walls standing on the base.");
        return Each(e, points.Select(p => (p, (Action)(() => e.SetRestraint(p, restraint)))));
    }

    private static JsonNode AddPattern(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        if (e.LoadPatterns().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ToolInputException($"The load pattern '{name}' is already there.");
        e.AddLoadPattern(name, a.Need("type"), a.Number("self_weight", 0));
        return new JsonObject { ["added"] = name };
    }

    private static JsonNode SetLoads(JsonElement a, ICsiLink e)
    {
        var pattern = a.Need("pattern");
        var known = e.LoadPatterns().Select(p => p.Name).ToList();
        pattern = known.FirstOrDefault(k => string.Equals(k, pattern, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolInputException($"There is no load pattern '{pattern}'. The model has: {string.Join(", ", known)}.");
        var value = a.Number("value");
        var replace = !a.Flag("add");
        var frames = a.Texts("frames");
        var areas = a.Texts("areas");
        if (frames.Count + areas.Count == 0) throw new ToolInputException("Give 'frames' or 'areas' to load.");
        return Each(e, frames.Select(f => (f, (Action)(() => e.SetFrameLoad(f, pattern, value, replace))))
            .Concat(areas.Select(x => (x, (Action)(() => e.SetAreaLoad(x, pattern, value, replace))))));
    }

    private static JsonNode AddCombo(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        if (e.Combos().Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) throw new ToolInputException($"The combination '{name}' is already there.");
        var known = e.LoadCases();
        var cases = a.Items("cases").Select(c => (Case: c.Need("case"), Factor: c.Number("factor"))).ToList();
        var unknown = cases.Where(c => !known.Contains(c.Case, StringComparer.OrdinalIgnoreCase)).Select(c => c.Case).ToList();
        if (unknown.Count > 0) throw new ToolInputException($"No load case named {string.Join(", ", unknown)}. The model has: {string.Join(", ", known)}.");
        if (cases.Count == 0) throw new ToolInputException("A combination needs at least one case.");
        e.AddCombo(name, cases);
        return new JsonObject { ["added"] = name, ["cases"] = cases.Count };
    }

    private static JsonNode Delete(JsonElement a, ICsiLink e)
    {
        var frames = a.Texts("frames");
        var areas = a.Texts("areas");
        if (frames.Count + areas.Count == 0) throw new ToolInputException("Give 'frames' or 'areas' to delete.");
        return Each(e, frames.Select(f => (f, (Action)(() => e.Delete("frame", f)))).Concat(areas.Select(x => (x, (Action)(() => e.Delete("area", x))))));
    }
}
