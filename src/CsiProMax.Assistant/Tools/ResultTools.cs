using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Results and checks. Every number comes from ETABS's own result tables after its own analysis and design: nothing
/// here analyses or designs. A check reads a table, finds the governing rows and compares them with a limit the
/// engineer gives (or a default that is named in the answer), so it gives the same answer every time.
/// </summary>
public static class ResultTools
{
    public static IReadOnlyList<Tool> All { get; } =
    [
        new("results_overview",
            "After the analysis has run: the modal periods and mass participation, the base reactions per load case, and which result and design tables have data. Start result work here.",
            """{"type":"object","properties":{"cases":{"type":"array","items":{"type":"string"},"description":"Load cases for the base reactions. Default: all."}}}""",
            Ask.Never, Overview),

        new("check_drift",
            "Story drift check from ETABS's Story Drifts table: the largest drift ratio per story and direction with its load case, the governing story, and pass or fail against 'limit' when one is given (for example 0.002 or 1/500 as 0.002). Without a limit it reports the values only. The limit depends on the code and the load case, so ask the engineer when it is not known.",
            """
            {"type":"object","properties":{
              "limit":{"type":"number","description":"Allowed drift ratio, e.g. 0.002. Leave out to report without a verdict."},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}}}}
            """, Ask.Never, Drift),

        new("check_torsion",
            "Torsional irregularity check from ETABS's Story Max Over Avg Displacements (or Drifts) table: the largest ratio of maximum to average per story and direction. Above 'irregular' (default 1.2) is torsional irregularity and above 'extreme' (default 1.4) is extreme, the limits most seismic codes use; pass others when the project's code differs.",
            """
            {"type":"object","properties":{
              "irregular":{"type":"number"},"extreme":{"type":"number"},
              "use":{"type":"string","enum":["displacements","drifts"],"description":"Default displacements."},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}}}}
            """, Ask.Never, Torsion),

        new("check_story_irregularity",
            "Vertical irregularity checks from ETABS's tables. Soft story: a story's lateral stiffness below 'soft' (default 0.7) of the story above or below 'soft_average' (default 0.8) of the average of the three above, from Story Stiffness. Mass: a story's mass above 'mass' (default 1.5) times an adjacent story's, from Mass Summary by Story (the roof is left out). Pass other ratios when the project's code differs.",
            """
            {"type":"object","properties":{
              "soft":{"type":"number"},"soft_average":{"type":"number"},"mass":{"type":"number"},
              "cases":{"type":"array","items":{"type":"string"},"description":"Lateral load cases for the stiffness. Default: all."}}}
            """, Ask.Never, Irregularity),

        new("check_modal",
            "Modal check from ETABS's Modal Participating Mass Ratios table: the periods of the first modes, which direction each moves, and whether the summed mass participation reaches 'min_mass' (default 0.9) in X and Y.",
            """{"type":"object","properties":{"min_mass":{"type":"number"},"modes":{"type":"integer","description":"How many modes to list (default 6)."}}}""",
            Ask.Never, Modal),

        new("design_ratios",
            "Design check from ETABS's own design summary tables (steel frame, concrete column and beam, shear wall, composite, as the model has them, for the design code set in ETABS): how many members, the largest demand over capacity ratio, and every member above 'limit' (default 1.0) with its story, ratio and governing combination. The design must have been run in ETABS first. Give 'table' to read one summary table only.",
            """
            {"type":"object","properties":{
              "limit":{"type":"number"},"table":{"type":"string"},
              "list":{"type":"integer","description":"How many members above the limit to list per table (default 40)."}}}
            """, Ask.Never, DesignRatios),

        new("check_report",
            "Runs every result check in one go (modal, drift, torsion, story irregularity, design ratios) and gives one list of findings with pass, fail or not available for each. Use it for a check report, then put it in a document with make_document if the engineer wants a file.",
            """
            {"type":"object","properties":{
              "drift_limit":{"type":"number","description":"Allowed drift ratio. Leave out to report drift without a verdict."},
              "design_limit":{"type":"number"},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}}}}
            """, Ask.Never, Report),

        new("model_snapshot",
            "Keeps a snapshot of the model as it is now under a name: every object with its section and position, the sections, load patterns and combinations, and the main results when the analysis has run (periods, base reactions, largest drifts). Take one before a round of changes, then compare_snapshots shows what changed.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string","description":"Short name, e.g. before-core-change."}}}""",
            Ask.Never, Snapshot),

        new("list_snapshots", "Lists the snapshots kept for this model, newest first.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => ListSnapshots(e)),

        new("compare_snapshots",
            "Compares two snapshots of this model, or one snapshot with the model as it is now: objects added, removed, moved or given another section, sections and load definitions that changed, and how the periods, base reactions and drifts moved.",
            """
            {"type":"object","required":["from"],"properties":{
              "from":{"type":"string","description":"The earlier snapshot's name."},
              "to":{"type":"string","description":"The later snapshot's name. Default: the model now."}}}
            """, Ask.Never, Compare),
    ];

    // ---- tables ----

    internal static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The index of the first field whose name (letters and digits only, any case) is one of the names; -1 when none.</summary>
    internal static int Col(IReadOnlyList<string> fields, params string[] names)
    {
        foreach (var name in names)
        {
            var want = Norm(name);
            for (var i = 0; i < fields.Count; i++)
                if (Norm(fields[i]) == want) return i;
        }
        return -1;
    }

    internal static double? Num(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;

    private static double R(double v, int digits = 6) => Math.Round(v, digits);

    /// <summary>The table whose name or key is one of the names (first match wins), with data, or null.</summary>
    private static TableData? Read(ICsiLink e, IReadOnlyList<TableName> known, JsonElement a, bool results, params string[] names)
    {
        foreach (var name in names)
        {
            var table = known.FirstOrDefault(t => Norm(t.Name) == Norm(name) || Norm(t.Key) == Norm(name));
            if (table is null) continue;
            IReadOnlyList<string>? cases = null, combos = null;
            if (results)
            {
                cases = a.Texts("cases");
                combos = a.Texts("combinations");
                if (cases.Count == 0 && combos.Count == 0) { cases = e.LoadCases(); combos = e.Combos(); }
            }
            TableData data;
            try { data = e.ReadTable(table.Key, cases, combos); }
            catch (EtabsException) { continue; }
            if (data.Rows.Count > 0) return data;
        }
        return null;
    }

    private static JsonObject Missing(string what, string table) => new()
    {
        ["status"] = "not available",
        ["note"] = $"ETABS's '{table}' table has no data, so {what} could not be checked. Run the analysis (and the design, for design ratios) in ETABS first, with the lateral load cases defined.",
    };

    // ---- overview and modal ----

    private static JsonNode Overview(JsonElement a, ICsiLink e)
    {
        var known = e.Tables();
        var result = new JsonObject { ["locked"] = e.Info().Locked };
        result["modal"] = ModalCore(e, known, a, 0.9, 3);
        var reactions = Read(e, known, a, true, "Base Reactions");
        if (reactions is null) result["base_reactions"] = Missing("the base reactions", "Base Reactions");
        else
        {
            var f = reactions.Fields;
            int oc = Col(f, "OutputCase", "Output Case", "Load Case/Combo"), st = Col(f, "StepType", "Step Type");
            int fx = Col(f, "FX"), fy = Col(f, "FY"), fz = Col(f, "FZ"), mx = Col(f, "MX"), my = Col(f, "MY"), mz = Col(f, "MZ");
            var rows = new JsonArray();
            foreach (var r in reactions.Rows.Take(200))
            {
                var row = new JsonObject { ["case"] = oc >= 0 ? r[oc] : "" };
                if (st >= 0 && r[st].Length > 0) row["step"] = r[st];
                foreach (var (name, at) in new[] { ("FX", fx), ("FY", fy), ("FZ", fz), ("MX", mx), ("MY", my), ("MZ", mz) })
                    if (at >= 0 && Num(r[at]) is { } v) row[name] = R(v, 3);
                rows.Add(row);
            }
            result["base_reactions"] = rows;
        }
        var withData = known.Where(t => !t.Empty).Select(t => t.Name).ToList();
        result["result_tables_with_data"] = new JsonArray(withData.Where(n => IsResultTable(n)).Take(60).Select(n => (JsonNode)n).ToArray());
        result["design_tables_with_data"] = new JsonArray(withData.Where(IsDesignSummary).Take(40).Select(n => (JsonNode)n).ToArray());
        var info = e.Info();
        result["units"] = $"{info.Force}, {info.Length}";
        return result;
    }

    private static bool IsResultTable(string name) =>
        new[] { "Story Drifts", "Story Max", "Story Stiffness", "Story Forces", "Base Reactions", "Modal", "Joint Displacements", "Joint Reactions", "Diaphragm", "Element Forces", "Centers Of Mass", "Mass Summary", "Pier Forces", "Spandrel Forces" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    internal static bool IsDesignSummary(string name) =>
        name.Contains("Summary", StringComparison.OrdinalIgnoreCase) &&
        new[] { "Design", "Column", "Beam", "Pier", "Spandrel", "Brace", "Joint", "Slab", "Wall" }.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)) &&
        !name.Contains("Mass Summary", StringComparison.OrdinalIgnoreCase) && !name.Contains("Preferences", StringComparison.OrdinalIgnoreCase);

    private static JsonNode Modal(JsonElement a, ICsiLink e) => ModalCore(e, e.Tables(), a, a.Number("min_mass", 0.9), Math.Max(1, a.Whole("modes", 6)));

    private static JsonObject ModalCore(ICsiLink e, IReadOnlyList<TableName> known, JsonElement a, double minMass, int list)
    {
        const string table = "Modal Participating Mass Ratios";
        var data = Read(e, known, default, false, table);
        if (data is null) return Missing("the modes", table);
        return ModalCheck(data, minMass, list);
    }

    internal static JsonObject ModalCheck(TableData data, double minMass, int list)
    {
        var f = data.Fields;
        int mode = Col(f, "Mode"), period = Col(f, "Period"), ux = Col(f, "UX"), uy = Col(f, "UY"), rz = Col(f, "RZ");
        int sx = Col(f, "SumUX", "Sum UX"), sy = Col(f, "SumUY", "Sum UY");
        if (period < 0) return Unreadable(data, "Modal Participating Mass Ratios", "Period");
        var modes = new JsonArray();
        double sumX = 0, sumY = 0;
        var count = 0;
        foreach (var r in data.Rows)
        {
            count++;
            double x = ux >= 0 ? Num(r[ux]) ?? 0 : 0, y = uy >= 0 ? Num(r[uy]) ?? 0 : 0, z = rz >= 0 ? Num(r[rz]) ?? 0 : 0;
            sumX = sx >= 0 && Num(r[sx]) is { } cx ? cx : sumX + x;
            sumY = sy >= 0 && Num(r[sy]) is { } cy ? cy : sumY + y;
            if (count > list) continue;
            var moves = z > x && z > y ? "torsion" : x >= y ? "X" : "Y";
            modes.Add(new JsonObject { ["mode"] = mode >= 0 ? r[mode] : count.ToString(CultureInfo.InvariantCulture), ["period"] = R(Num(r[period]) ?? 0, 4), ["UX"] = R(x, 4), ["UY"] = R(y, 4), ["RZ"] = R(z, 4), ["mainly"] = moves });
        }
        var ok = sumX >= minMass && sumY >= minMass;
        var result = new JsonObject
        {
            ["status"] = ok ? "pass" : "fail",
            ["modes_in_table"] = count,
            ["sum_mass_x"] = R(sumX, 4), ["sum_mass_y"] = R(sumY, 4), ["min_mass"] = minMass,
            ["modes"] = modes,
        };
        if (!ok) result["note"] = $"The modes in the table carry {sumX:P0} of the mass in X and {sumY:P0} in Y, below {minMass:P0}. More modes are needed in the modal case.";
        if (modes.Count > 0 && modes[0]!["mainly"]!.GetValue<string>() == "torsion") result["warning"] = "The first mode is mainly torsion.";
        return result;
    }

    private static JsonObject Unreadable(TableData data, string table, string field) => new()
    {
        ["status"] = "not available",
        ["note"] = $"ETABS's '{table}' table has no '{field}' field in this version. Its fields: {string.Join(", ", data.Fields)}. Read it with read_table instead.",
    };

    // ---- drift ----

    private static JsonNode Drift(JsonElement a, ICsiLink e)
    {
        var data = Read(e, e.Tables(), a, true, "Story Drifts");
        if (data is null) return Missing("the drift", "Story Drifts");
        double? limit = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : null;
        if (limit is <= 0) throw new ToolInputException("'limit' is a drift ratio above 0, e.g. 0.002.");
        return DriftCheck(data, limit, e.Stories().Select(s => s.Name).ToList());
    }

    internal static JsonObject DriftCheck(TableData data, double? limit, IReadOnlyList<string> storyOrder)
    {
        var f = data.Fields;
        int story = Col(f, "Story"), oc = Col(f, "OutputCase", "Output Case", "Load Case/Combo"), dir = Col(f, "Direction", "Dir"), drift = Col(f, "Drift"), label = Col(f, "Label");
        if (story < 0 || drift < 0) return Unreadable(data, "Story Drifts", story < 0 ? "Story" : "Drift");
        var worst = new Dictionary<(string Story, string Dir), (double Drift, string Case, string Label)>();
        foreach (var r in data.Rows)
        {
            if (Num(r[drift]) is not { } d) continue;
            d = Math.Abs(d);
            var key = (r[story], dir >= 0 ? r[dir] : "");
            if (!worst.TryGetValue(key, out var had) || d > had.Drift) worst[key] = (d, oc >= 0 ? r[oc] : "", label >= 0 ? r[label] : "");
        }
        if (worst.Count == 0) return Unreadable(data, "Story Drifts", "Drift");
        var order = storyOrder.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i, StringComparer.OrdinalIgnoreCase);
        var rows = worst.OrderByDescending(w => order.TryGetValue(w.Key.Story, out var i) ? i : -1).ThenBy(w => w.Key.Dir, StringComparer.Ordinal).ToList();
        var top = rows.MaxBy(w => w.Value.Drift);
        var result = new JsonObject
        {
            ["status"] = limit is null ? "reported" : top.Value.Drift <= limit ? "pass" : "fail",
            ["max_drift"] = R(top.Value.Drift), ["max_as_1_over"] = top.Value.Drift > 0 ? Math.Round(1 / top.Value.Drift) : null,
            ["at_story"] = top.Key.Story, ["direction"] = top.Key.Dir, ["case"] = top.Value.Case,
        };
        if (limit is { } lim)
        {
            result["limit"] = lim;
            result["stories_over_limit"] = new JsonArray(rows.Where(w => w.Value.Drift > lim).Select(w => (JsonNode)$"{w.Key.Story} {w.Key.Dir}".Trim()).ToArray());
        }
        else result["note"] = "No limit was given, so there is no verdict. The limit depends on the code and the load case.";
        result["by_story"] = new JsonArray(rows.Select(w =>
        {
            var row = new JsonObject { ["story"] = w.Key.Story, ["direction"] = w.Key.Dir, ["drift"] = R(w.Value.Drift), ["case"] = w.Value.Case };
            if (limit is { } x) row["ratio_to_limit"] = R(w.Value.Drift / x, 3);
            return (JsonNode)row;
        }).ToArray());
        return result;
    }

    // ---- torsion ----

    private static JsonNode Torsion(JsonElement a, ICsiLink e)
    {
        var drifts = a.Text("use", "displacements") == "drifts";
        var names = drifts ? new[] { "Story Max Over Avg Drifts", "Story Max/Avg Drifts" } : new[] { "Story Max Over Avg Displacements", "Story Max/Avg Displacements" };
        var data = Read(e, e.Tables(), a, true, names);
        if (data is null) return Missing("torsion", names[0]);
        return TorsionCheck(data, a.Number("irregular", 1.2), a.Number("extreme", 1.4), names[0]);
    }

    internal static JsonObject TorsionCheck(TableData data, double irregular, double extreme, string table)
    {
        var f = data.Fields;
        int story = Col(f, "Story"), oc = Col(f, "OutputCase", "Output Case", "Load Case/Combo"), dir = Col(f, "Direction", "Dir"), ratio = Col(f, "Ratio");
        int max = Col(f, "Maximum", "Max Drift", "MaxDrift", "Max"), avg = Col(f, "Average", "Avg Drift", "AvgDrift", "Avg");
        if (story < 0 || (ratio < 0 && (max < 0 || avg < 0))) return Unreadable(data, table, "Ratio");
        var worst = new Dictionary<(string Story, string Dir), (double Ratio, string Case)>();
        foreach (var r in data.Rows)
        {
            var value = ratio >= 0 ? Num(r[ratio]) : null;
            if (value is null && max >= 0 && avg >= 0 && Num(r[max]) is { } m && Num(r[avg]) is { } v && Math.Abs(v) > 1e-12) value = Math.Abs(m / v);
            if (value is not { } x) continue;
            var key = (r[story], dir >= 0 ? r[dir] : "");
            if (!worst.TryGetValue(key, out var had) || x > had.Ratio) worst[key] = (x, oc >= 0 ? r[oc] : "");
        }
        if (worst.Count == 0) return Unreadable(data, table, "Ratio");
        var top = worst.MaxBy(w => w.Value.Ratio);
        var verdict = top.Value.Ratio > extreme ? "extreme torsional irregularity" : top.Value.Ratio > irregular ? "torsional irregularity" : "regular";
        return new JsonObject
        {
            ["status"] = top.Value.Ratio > irregular ? "fail" : "pass",
            ["verdict"] = verdict,
            ["max_ratio"] = R(top.Value.Ratio, 3), ["at_story"] = top.Key.Story, ["direction"] = top.Key.Dir, ["case"] = top.Value.Case,
            ["irregular_above"] = irregular, ["extreme_above"] = extreme,
            ["stories_above"] = new JsonArray(worst.Where(w => w.Value.Ratio > irregular).OrderByDescending(w => w.Value.Ratio)
                .Select(w => (JsonNode)new JsonObject { ["story"] = w.Key.Story, ["direction"] = w.Key.Dir, ["ratio"] = R(w.Value.Ratio, 3), ["case"] = w.Value.Case }).ToArray()),
        };
    }

    // ---- soft story and mass ----

    private static JsonNode Irregularity(JsonElement a, ICsiLink e)
    {
        var known = e.Tables();
        var order = e.Stories().Skip(1).Select(s => s.Name).Reverse().ToList(); // top first
        var stiffness = Read(e, known, a, true, "Story Stiffness");
        var mass = Read(e, known, default, false, "Mass Summary by Story", "Mass Summary By Story");
        var result = new JsonObject
        {
            ["soft_story"] = stiffness is null ? Missing("the story stiffness", "Story Stiffness") : SoftStoryCheck(stiffness, order, a.Number("soft", 0.7), a.Number("soft_average", 0.8)),
            ["mass"] = mass is null ? Missing("the story masses", "Mass Summary by Story") : MassCheck(mass, order, a.Number("mass", 1.5)),
        };
        var states = new[] { result["soft_story"]!["status"]!.GetValue<string>(), result["mass"]!["status"]!.GetValue<string>() };
        result["status"] = states.Contains("fail") ? "fail" : states.All(s => s == "pass") ? "pass" : "not available";
        return result;
    }

    /// <param name="topFirst">Story names from the top story down.</param>
    internal static JsonObject SoftStoryCheck(TableData data, IReadOnlyList<string> topFirst, double soft, double softAverage)
    {
        var f = data.Fields;
        int story = Col(f, "Story"), oc = Col(f, "OutputCase", "Output Case", "Load Case/Combo");
        int kx = Col(f, "StiffX", "Stiff X", "Stiffness X"), ky = Col(f, "StiffY", "Stiff Y", "Stiffness Y");
        if (story < 0 || (kx < 0 && ky < 0)) return Unreadable(data, "Story Stiffness", "StiffX");
        var findings = new JsonArray();
        var checkedPairs = 0;
        foreach (var (dir, at) in new[] { ("X", kx), ("Y", ky) })
        {
            if (at < 0) continue;
            foreach (var group in data.Rows.GroupBy(r => oc >= 0 ? r[oc] : ""))
            {
                // The largest stiffness per story for this case (a case with steps gives several rows).
                var k = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in group)
                    if (Num(r[at]) is { } v && (!k.TryGetValue(r[story], out var had) || Math.Abs(v) > had)) k[r[story]] = Math.Abs(v);
                var list = topFirst.Where(k.ContainsKey).Select(s => (Story: s, K: k[s])).Where(s => s.K > 0).ToList();
                if (list.Count < 2 || list.Max(s => s.K) <= 0) continue;
                for (var i = 1; i < list.Count; i++)
                {
                    checkedPairs++;
                    var above = list[i - 1].K;
                    var ratio = list[i].K / above;
                    double? ratioAverage = i >= 3 ? list[i].K / ((list[i - 1].K + list[i - 2].K + list[i - 3].K) / 3) : null;
                    if (ratio < soft || ratioAverage < softAverage)
                        findings.Add(new JsonObject
                        {
                            ["story"] = list[i].Story, ["direction"] = dir, ["case"] = group.Key,
                            ["ratio_to_story_above"] = R(ratio, 3), ["ratio_to_average_of_three_above"] = ratioAverage is { } ra ? R(ra, 3) : null,
                        });
                }
            }
        }
        if (checkedPairs == 0) return new JsonObject { ["status"] = "not available", ["note"] = "The Story Stiffness table has no stiffness for two stories of one lateral load case." };
        return new JsonObject
        {
            ["status"] = findings.Count == 0 ? "pass" : "fail",
            ["soft_below"] = soft, ["soft_average_below"] = softAverage,
            ["soft_stories"] = findings,
        };
    }

    internal static JsonObject MassCheck(TableData data, IReadOnlyList<string> topFirst, double limit)
    {
        var f = data.Fields;
        int story = Col(f, "Story"), ux = Col(f, "UX", "MassX", "Mass X"), uy = Col(f, "UY", "MassY", "Mass Y");
        var at = ux >= 0 ? ux : uy;
        if (story < 0 || at < 0) return Unreadable(data, "Mass Summary by Story", "UX");
        var mass = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in data.Rows)
            if (Num(r[at]) is { } v) mass[r[story]] = v;
        var list = topFirst.Where(mass.ContainsKey).Select(s => (Story: s, M: mass[s])).ToList();
        if (list.Count < 3) return new JsonObject { ["status"] = "not available", ["note"] = "Fewer than three stories have a mass, so there is nothing to compare." };
        var findings = new JsonArray();
        for (var i = 1; i < list.Count; i++) // the roof (index 0) is not judged
        {
            double? above = i - 1 >= 1 ? list[i - 1].M : null, below = i + 1 < list.Count ? list[i + 1].M : null;
            var worst = new[] { above, below }.Where(m => m is > 0).Select(m => list[i].M / m!.Value).DefaultIfEmpty(0).Max();
            if (worst > limit) findings.Add(new JsonObject { ["story"] = list[i].Story, ["mass"] = R(list[i].M, 3), ["ratio_to_adjacent"] = R(worst, 3) });
        }
        return new JsonObject
        {
            ["status"] = findings.Count == 0 ? "pass" : "fail", ["mass_above"] = limit, ["heavy_stories"] = findings,
            ["story_masses"] = new JsonArray(list.Select(s => (JsonNode)new JsonObject { ["story"] = s.Story, ["mass"] = R(s.M, 3) }).ToArray()),
        };
    }

    // ---- design ratios ----

    private static JsonNode DesignRatios(JsonElement a, ICsiLink e)
    {
        var known = e.Tables();
        var asked = a.Text("table");
        var limit = a.Number("limit", 1.0);
        var list = Math.Max(0, a.Whole("list", 40));
        var tables = asked.Length > 0
            ? known.Where(t => string.Equals(t.Name, asked, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Key, asked, StringComparison.OrdinalIgnoreCase)).ToList()
            : known.Where(t => !t.Empty && IsDesignSummary(t.Name)).ToList();
        if (asked.Length > 0 && tables.Count == 0) throw new ToolInputException($"There is no table '{asked}'. The design summary tables with data: {string.Join("; ", known.Where(t => !t.Empty && IsDesignSummary(t.Name)).Select(t => t.Name))}.");
        var results = new JsonArray();
        double top = 0;
        var over = 0;
        foreach (var table in tables)
        {
            TableData data;
            try { data = e.ReadTable(table.Key, null, null); }
            catch (EtabsException) { continue; }
            if (data.Rows.Count == 0) continue;
            var one = DesignCheck(data, table.Name, limit, list);
            if (one is null) continue;
            results.Add(one);
            top = Math.Max(top, one["max_ratio"]!.GetValue<double>());
            over += one["over_limit"]!.GetValue<int>();
        }
        if (results.Count == 0)
            return new JsonObject { ["status"] = "not available", ["note"] = "No design summary table has ratios. Run the design in ETABS first (Design menu), then ask again. list_tables with contains 'Summary' shows what exists." };
        return new JsonObject { ["status"] = over == 0 ? "pass" : "fail", ["limit"] = limit, ["max_ratio"] = R(top, 3), ["members_over_limit"] = over, ["tables"] = results };
    }

    /// <summary>One design summary table: null when it has no ratio field.</summary>
    internal static JsonObject? DesignCheck(TableData data, string table, double limit, int list)
    {
        var f = data.Fields;
        var ratios = Enumerable.Range(0, f.Count).Where(i => { var n = Norm(f[i]); return (n.Contains("ratio") || n.Contains("dc") && n.Length <= 8) && !n.Contains("combo") && !n.Contains("rebar"); }).ToList();
        if (ratios.Count == 0) return null;
        int story = Col(f, "Story"), label = Col(f, "Label", "Pier", "Spandrel", "Beam", "Column"), unique = Col(f, "UniqueName", "Unique Name"), section = Col(f, "DesignSect", "Design Section", "Section", "DesignSection");
        var combos = Enumerable.Range(0, f.Count).Where(i => Norm(f[i]).Contains("combo")).ToList();
        var members = new List<(double Ratio, string[] Row, int Field)>();
        foreach (var r in data.Rows)
        {
            double best = double.NaN;
            var bestAt = -1;
            foreach (var i in ratios)
                if (Num(r[i]) is { } v && (double.IsNaN(best) || v > best)) { best = v; bestAt = i; }
            if (bestAt >= 0) members.Add((best, r, bestAt));
        }
        if (members.Count == 0) return null;
        var failing = members.Where(m => m.Ratio > limit).OrderByDescending(m => m.Ratio).ToList();
        JsonObject Row((double Ratio, string[] Row, int Field) m)
        {
            var row = new JsonObject { ["ratio"] = R(m.Ratio, 3), ["governs"] = f[m.Field] };
            if (story >= 0) row["story"] = m.Row[story];
            if (label >= 0) row["label"] = m.Row[label];
            if (unique >= 0) row["name"] = m.Row[unique];
            if (section >= 0) row["section"] = m.Row[section];
            // The combination field that sits nearest before or after the governing ratio.
            if (combos.Count > 0) row["combination"] = m.Row[combos.OrderBy(c => Math.Abs(c - m.Field)).First()];
            return row;
        }
        var result = new JsonObject
        {
            ["table"] = table, ["members"] = members.Count, ["max_ratio"] = R(members.Max(m => m.Ratio), 3), ["over_limit"] = failing.Count,
            ["worst"] = Row(members.MaxBy(m => m.Ratio)),
            ["members_over_limit"] = new JsonArray(failing.Take(list).Select(m => (JsonNode)Row(m)).ToArray()),
        };
        if (failing.Count > list) result["note"] = $"Listing {list} of {failing.Count} members over the limit, worst first.";
        return result;
    }

    // ---- report ----

    internal static JsonNode Report(JsonElement a, ICsiLink e)
    {
        var info = e.Info();
        var parts = new (string Name, Func<JsonNode> Run)[]
        {
            ("modal", () => Modal(default, e)),
            ("drift", () => Drift(With(a, "limit", "drift_limit"), e)),
            ("torsion", () => Torsion(a, e)),
            ("story_irregularity", () => Irregularity(a, e)),
            ("design_ratios", () => DesignRatios(With(default, "limit", "design_limit", a), e)),
        };
        var checks = new JsonObject();
        var summary = new JsonArray();
        foreach (var (name, run) in parts)
        {
            JsonNode node;
            try { node = run(); }
            catch (Exception ex) when (ex is EtabsException or ToolInputException) { node = new JsonObject { ["status"] = "not available", ["note"] = ex.Message }; }
            checks[name] = node;
            summary.Add($"{name.Replace('_', ' ')}: {node["status"]?.GetValue<string>() ?? "reported"}");
        }
        return new JsonObject
        {
            ["model"] = info.File, ["units"] = $"{info.Force}, {info.Length}", ["analysis_run"] = info.Locked,
            ["summary"] = summary, ["checks"] = checks,
            ["note"] = "Every value is read from ETABS's own result tables. Limits that were not given are the defaults named in each check; confirm them against the project's code.",
        };
    }

    /// <summary>The arguments with <paramref name="from"/> (read from <paramref name="source"/> or the arguments) passed on as <paramref name="to"/>.</summary>
    private static JsonElement With(JsonElement a, string to, string from, JsonElement? source = null)
    {
        var o = a.ValueKind == JsonValueKind.Object ? JsonNode.Parse(a.GetRawText())!.AsObject() : new JsonObject();
        var src = source ?? a;
        o.Remove(to);
        if (src.ValueKind == JsonValueKind.Object && src.TryGetProperty(from, out var v) && v.ValueKind == JsonValueKind.Number) o[to] = v.GetDouble();
        return JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
    }

    // ---- snapshots ----

    /// <summary>Where snapshots are kept. Tests point it at a folder of their own.</summary>
    public static string SnapshotRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsiProMax", "snapshots");

    private static string Safe(string name)
    {
        var clean = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        return clean.Length == 0 ? throw new ToolInputException("Give the snapshot a name with letters or digits.") : clean.Length > 60 ? clean[..60] : clean;
    }

    private static string FolderFor(ICsiLink e)
    {
        var file = e.Info().File;
        var model = file.Length > 0 ? Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/').Last()) : "unsaved";
        return Path.Combine(SnapshotRoot, Safe(model.Length > 0 ? model : "unsaved"));
    }

    internal static JsonObject Capture(ICsiLink e)
    {
        var info = e.Info();
        var snap = new JsonObject
        {
            ["model"] = info.File, ["units"] = $"{info.Force}, {info.Length}", ["taken"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["analysis_run"] = info.Locked,
            ["stories"] = new JsonArray(e.Stories().Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["elevation"] = R(s.Elevation, 4) }).ToArray()),
            ["frames"] = new JsonObject(e.Frames().GroupBy(f => f.Name).Select(g => g.First()).Select(f => KeyValuePair.Create(f.Name, (JsonNode?)new JsonObject
            {
                ["kind"] = f.Kind, ["section"] = f.Section, ["story"] = f.Story,
                ["at"] = new JsonArray(R(f.X1, 4), R(f.Y1, 4), R(f.Z1, 4), R(f.X2, 4), R(f.Y2, 4), R(f.Z2, 4)),
            }))),
            ["areas"] = new JsonObject(e.Areas().GroupBy(x => x.Name).Select(g => g.First()).Select(x => KeyValuePair.Create(x.Name, (JsonNode?)new JsonObject
            {
                ["kind"] = x.Kind, ["section"] = x.Section,
                ["at"] = new JsonArray(x.Corners.SelectMany(c => new[] { (JsonNode)R(c.X, 4), R(c.Y, 4), R(c.Z, 4) }).ToArray()),
            }))),
            ["frame_sections"] = new JsonArray(e.FrameSections().Select(s => (JsonNode)s).ToArray()),
            ["area_sections"] = new JsonArray(e.AreaSections().Select(s => (JsonNode)s).ToArray()),
            ["materials"] = new JsonArray(e.Materials().Select(s => (JsonNode)s).ToArray()),
            ["load_patterns"] = new JsonArray(e.LoadPatterns().Select(p => (JsonNode)$"{p.Name} ({p.Type}, self weight {p.SelfWeight.ToString(CultureInfo.InvariantCulture)})").ToArray()),
            ["load_cases"] = new JsonArray(e.LoadCases().Select(s => (JsonNode)s).ToArray()),
            ["combinations"] = new JsonArray(e.Combos().Select(s => (JsonNode)s).ToArray()),
        };
        var results = new JsonObject();
        try
        {
            var known = e.Tables();
            if (Read(e, known, default, false, "Modal Participating Mass Ratios") is { } modal && Col(modal.Fields, "Period") is >= 0 and var p)
                results["periods"] = new JsonArray(modal.Rows.Take(6).Select(r => (JsonNode)R(Num(r[p]) ?? 0, 4)).ToArray());
            if (Read(e, known, default, true, "Base Reactions") is { } base_)
            {
                int oc = Col(base_.Fields, "OutputCase", "Output Case", "Load Case/Combo"), fx = Col(base_.Fields, "FX"), fy = Col(base_.Fields, "FY"), fz = Col(base_.Fields, "FZ");
                var reactions = new JsonObject();
                foreach (var r in base_.Rows.Take(300))
                {
                    var name = oc >= 0 ? r[oc] : "";
                    if (name.Length == 0 || reactions.ContainsKey(name)) continue;
                    reactions[name] = new JsonArray(fx >= 0 ? R(Num(r[fx]) ?? 0, 2) : 0, fy >= 0 ? R(Num(r[fy]) ?? 0, 2) : 0, fz >= 0 ? R(Num(r[fz]) ?? 0, 2) : 0);
                }
                results["base_reactions_fx_fy_fz"] = reactions;
            }
            if (Read(e, known, default, true, "Story Drifts") is { } drifts)
            {
                var check = DriftCheck(drifts, null, e.Stories().Select(s => s.Name).ToList());
                if (check["max_drift"] is { } max) results["max_drift"] = new JsonObject { ["drift"] = max.GetValue<double>(), ["story"] = check["at_story"]!.GetValue<string>(), ["case"] = check["case"]!.GetValue<string>() };
            }
        }
        catch (EtabsException) { }
        if (results.Count > 0) snap["results"] = results;
        return snap;
    }

    private static JsonNode Snapshot(JsonElement a, ICsiLink e)
    {
        var name = Safe(a.Need("name"));
        var folder = FolderFor(e);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name + ".json");
        var replaced = File.Exists(file);
        var snap = Capture(e);
        File.WriteAllText(file, snap.ToJsonString());
        return new JsonObject
        {
            ["snapshot"] = name, ["replaced_an_older_one"] = replaced,
            ["frames"] = snap["frames"]!.AsObject().Count, ["areas"] = snap["areas"]!.AsObject().Count, ["has_results"] = snap.ContainsKey("results"),
        };
    }

    private static JsonNode ListSnapshots(ICsiLink e)
    {
        var folder = FolderFor(e);
        var files = Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList() : [];
        return new JsonObject
        {
            ["count"] = files.Count,
            ["snapshots"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(f.Name), ["taken"] = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) }).ToArray()),
        };
    }

    private static JsonObject Load(ICsiLink e, string name)
    {
        var file = Path.Combine(FolderFor(e), Safe(name) + ".json");
        if (!File.Exists(file))
        {
            var have = ListSnapshots(e)["snapshots"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToList();
            throw new ToolInputException($"There is no snapshot '{name}' for this model." + (have.Count > 0 ? " The snapshots are: " + string.Join(", ", have) + "." : " Take one with model_snapshot first."));
        }
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    private static JsonNode Compare(JsonElement a, ICsiLink e)
    {
        var from = Load(e, a.Need("from"));
        var toName = a.Text("to");
        var to = toName.Length > 0 ? Load(e, toName) : Capture(e);
        var result = Difference(from, to);
        result["from"] = a.Need("from");
        result["to"] = toName.Length > 0 ? toName : "the model now";
        return result;
    }

    internal static JsonObject Difference(JsonObject from, JsonObject to)
    {
        const int list = 60;
        var result = new JsonObject();
        var changes = 0;
        foreach (var kind in new[] { "frames", "areas" })
        {
            var before = from[kind]?.AsObject() ?? [];
            var after = to[kind]?.AsObject() ?? [];
            var added = after.Where(o => !before.ContainsKey(o.Key)).Select(o => o.Key).ToList();
            var removed = before.Where(o => !after.ContainsKey(o.Key)).Select(o => o.Key).ToList();
            var resectioned = new List<string>();
            var moved = new List<string>();
            foreach (var (name, was) in before)
            {
                if (after[name] is not { } now) continue;
                var s1 = was!["section"]?.GetValue<string>() ?? "";
                var s2 = now["section"]?.GetValue<string>() ?? "";
                if (s1 != s2) resectioned.Add($"{name}: {s1} to {s2}");
                if (was["at"]!.ToJsonString() != now["at"]!.ToJsonString()) moved.Add(name);
            }
            changes += added.Count + removed.Count + resectioned.Count + moved.Count;
            result[kind] = new JsonObject
            {
                ["before"] = before.Count, ["after"] = after.Count,
                ["added"] = added.Count, ["removed"] = removed.Count, ["section_changed"] = resectioned.Count, ["moved"] = moved.Count,
                ["added_names"] = Names(added, list), ["removed_names"] = Names(removed, list), ["section_changes"] = Names(resectioned, list), ["moved_names"] = Names(moved, list),
            };
        }
        var definitions = new JsonObject();
        foreach (var key in new[] { "stories", "frame_sections", "area_sections", "materials", "load_patterns", "load_cases", "combinations" })
        {
            var before = (from[key]?.AsArray() ?? []).Select(n => n!.ToJsonString()).ToList();
            var after = (to[key]?.AsArray() ?? []).Select(n => n!.ToJsonString()).ToList();
            var added = after.Except(before).ToList();
            var removed = before.Except(after).ToList();
            if (added.Count + removed.Count == 0) continue;
            changes += added.Count + removed.Count;
            definitions[key] = new JsonObject { ["added"] = Names(added.Select(Unquote), list), ["removed"] = Names(removed.Select(Unquote), list) };
        }
        result["definitions"] = definitions;
        var r1 = from["results"]?.AsObject();
        var r2 = to["results"]?.AsObject();
        if (r1 is null || r2 is null)
            result["results"] = r1 is null && r2 is null ? "Neither has results." : r1 is null ? "Only the later one has results." : "Only the earlier one has results (changing the model unlocks it and drops them: run the analysis again).";
        else
        {
            var moved = new JsonObject();
            if (r1["periods"] is JsonArray p1 && r2["periods"] is JsonArray p2)
                moved["periods"] = new JsonArray(Enumerable.Range(0, Math.Min(p1.Count, p2.Count)).Select(i => (JsonNode)new JsonObject
                {
                    ["mode"] = i + 1, ["before"] = p1[i]!.GetValue<double>(), ["after"] = p2[i]!.GetValue<double>(),
                    ["change_percent"] = p1[i]!.GetValue<double>() > 0 ? R((p2[i]!.GetValue<double>() / p1[i]!.GetValue<double>() - 1) * 100, 2) : null,
                }).ToArray());
            if (r1["max_drift"] is JsonObject d1 && r2["max_drift"] is JsonObject d2)
                moved["max_drift"] = new JsonObject { ["before"] = d1.DeepClone(), ["after"] = d2.DeepClone() };
            if (r1["base_reactions_fx_fy_fz"] is JsonObject b1 && r2["base_reactions_fx_fy_fz"] is JsonObject b2)
            {
                var reactions = new JsonObject();
                foreach (var (name, was) in b1)
                    if (b2[name] is { } now && was!.ToJsonString() != now.ToJsonString())
                        reactions[name] = new JsonObject { ["before"] = was.DeepClone(), ["after"] = now.DeepClone() };
                moved["base_reactions_changed"] = reactions;
            }
            result["results"] = moved;
        }
        result["changes"] = changes;
        if (changes == 0) result["note"] = "The model's objects and definitions are the same in both.";
        return result;
    }

    private static string Unquote(string json) => json.Length >= 2 && json[0] == '"' ? JsonNode.Parse(json)!.GetValue<string>() : json;

    private static JsonArray Names(IEnumerable<string> names, int list)
    {
        var all = names.ToList();
        var array = new JsonArray(all.Take(list).Select(n => (JsonNode)n).ToArray());
        if (all.Count > list) array.Add($"and {all.Count - list} more");
        return array;
    }
}
