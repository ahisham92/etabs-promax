using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.E2k;
using CsiProMax.Lateral;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's Define and Assign items for the analysis: functions (response spectrum and time history, from a code,
/// a wave or points), load cases of every type (static, nonlinear static, modal eigen and Ritz, response spectrum,
/// modal and direct histories, linear and nonlinear, buckling), the mass source, P-Delta, and every object load
/// (joint forces and ground displacements, frame point and trapezoid loads, temperature and strain). Modeler only.
/// </summary>
public static class CaseTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Modeler's own Define item (no CSI program behind it). In a live program use its own tools or edit_table.");

    private static T Core<T>(Func<T> work)
    {
        try { return work(); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
    }

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("define_function",
            "Adds or changes a response spectrum or time history function (Define > Functions). from 'points': the [x, y] pairs given (period and Sa in g, or time and value). from 'code': a code's spectrum (ASCE 7, IBC, Eurocode 8, ECP 201, IS 1893, UBC 97, NBCC, with the inputs lateral_codes lists for it). from 'sine', 'cosine' or 'ramp': a time history wave of the given period, cycles, amplitude and steps (a ramp with ramp_time and max_time: up to the amplitude at ramp_time, held to max_time). 'sawtooth' (period, ramp_time, cycles, amplitude), 'triangular' (period, cycles, amplitude), 'periodic' (points of one cycle, repeated cycles times). 'file': points read from a file by the form (file, header_lines, prefix_chars, ... kept with it). preview true returns the points and changes nothing (the form's Function Graph and Convert to User Defined).",
            """
            {"type":"object","required":["name","kind"],"properties":{
              "name":{"type":"string"},"was":{"type":"string"},"kind":{"type":"string","enum":["spectrum","history","psd","steady"],"description":"psd (power spectral density) and steady (steady state) are SAP2000's own: points [frequency, value], with sap_table."},
              "sap_table":{"type":"string","description":"SAP2000's own function table the function is kept in, e.g. 'Function - Response Spectrum - ASCE 7-16' (its code or type and parameters go back to SAP2000 as they are)."},
              "sap_values":{"type":"object","additionalProperties":{"type":["string","number"]},"description":"That table's parameter fields (SAP2000 field names) for this function."},
              "sap_points":{"type":"array","items":{"type":"array","items":{"type":"number"}},"description":"The points SAP2000's table keeps when they differ from the function's own (User Periodic: one cycle)."},
              "from":{"type":"string","enum":["points","code","sine","cosine","ramp","sawtooth","triangular","periodic","file"],"description":"Default points."},
              "ramp_time":{"type":"number"},"max_time":{"type":"number"},"preview":{"type":"boolean"},"units":{"type":"string","description":"Frequency Units Hz or RPM (power spectral density, steady state), as the form shows the frequencies."},
              "file":{"type":"object","additionalProperties":{"type":["string","number","boolean"]},"description":"The From File form's fields, kept with the function."},
              "points":{"type":"array","items":{"type":"array","items":{"type":"number"}}},
              "code":{"type":"string"},"values":{"type":"object","additionalProperties":{"type":["string","number"]}},
              "damping":{"type":"number","description":"Spectrum damping ratio. Default 0.05."},
              "period":{"type":"number"},"cycles":{"type":"number"},"amplitude":{"type":"number"},"steps_per_cycle":{"type":"integer"},"last_period":{"type":"number"}}}
            """, Ask.Changes, DefineFunction, a => $"Define the function {a.Text("name")}"),
        new("define_load_case",
            "Adds or changes a load case of any type (Define > Load Cases): Linear Static, Nonlinear Static (P-Delta or large displacements, from an initial case), Modal - Eigen, Modal - Ritz, Response Spectrum (modal case, CQC/SRSS/ABS/GMC, directional SRSS/ABS/CQC3, damping, eccentricity), Linear Modal History, Linear Direct History, Nonlinear Modal History (FNA), Nonlinear Direct History (steps, step size, damping) and Buckling. Each load is a load pattern or an acceleration U1/U2/U3, with its scale factor, and its function for a spectrum or history.",
            """
            {"type":"object","required":["name","type"],"properties":{
              "name":{"type":"string"},"was":{"type":"string"},"type":{"type":"string"},
              "loads":{"type":"array","items":{"type":"object","properties":{"pattern":{"type":"string"},"accel":{"type":"string","enum":["U1","U2","U3"]},"factor":{"type":"number"},"function":{"type":"string"},"angle":{"type":"number"},"arrival":{"type":"number"}}}},
              "initial_case":{"type":"string"},"max_modes":{"type":"integer"},"min_modes":{"type":"integer"},"modal_case":{"type":"string"},
              "modal_combo":{"type":"string"},"dir_combo":{"type":"string"},"damping":{"type":"number"},"eccentricity":{"type":"number"},
              "steps":{"type":"integer"},"step_size":{"type":"number"},"geometric":{"type":"string","enum":["None","P-Delta","P-Delta and Large Displacements"]}}}
            """, Ask.Changes, DefineCase, a => $"Define the load case {a.Text("name")}"),
        new("define_mass_source",
            "Sets the mass source (Define > Mass Source): its name, the elements' own mass, the added mass, and load patterns with their factors (e.g. Dead 1, SDL 1, Live 0.25), and the mass options (lateral, vertical, lumped at the stories, centroid moved by a ratio of the diaphragm width). The seismic loads by code are worked out from it again.",
            """{"type":"object","properties":{"elements":{"type":"boolean","description":"Default true."},"added_mass":{"type":"boolean","description":"Default true."},"name":{"type":"string","description":"Mass Source Name; default the current one."},"lateral":{"type":"boolean","description":"Include Lateral Mass; default true."},"vertical":{"type":"boolean","description":"Include Vertical Mass; default false."},"lump":{"type":"boolean","description":"Lump Lateral Mass at Story Levels; default true."},"move":{"type":"boolean","description":"Adjust Diaphragm Lateral Mass to Move Mass Centroid by move_x and move_y (ratios of the diaphragm width)."},"move_x":{"type":"number"},"move_y":{"type":"number"},"loads":{"type":"array","items":{"type":"object","required":["pattern","factor"],"properties":{"pattern":{"type":"string"},"factor":{"type":"number"}}}}}}""",
            Ask.Changes, DefineMassSource, a => "Set the mass source"),
        new("define_pdelta",
            "Sets the P-Delta option (Define > P-Delta Options): none, non-iterative based on the mass, or iterative based on load patterns with factors (e.g. Dead 1.2, Live 0.5), with the iterations and tolerance.",
            """{"type":"object","required":["method"],"properties":{"method":{"type":"string","enum":["none","non-iterative","iterative"]},"loads":{"type":"array","items":{"type":"object","required":["pattern","factor"],"properties":{"pattern":{"type":"string"},"factor":{"type":"number"}}}},"iterations":{"type":"integer"},"tolerance":{"type":"number"}}}""",
            Ask.Changes, DefinePDelta, a => $"Set P-Delta to {a.Text("method")}"),
        new("assign_object_loads",
            "Assigns a load of any kind to joints, frames or shells (Assign > Joint, Frame or Shell Loads), names as label@story (joints: point@story): joint_force (fx..mz), joint_displacement (ux..rz), joint_temperature, frame_point (value or moment at relative distance), frame_trapezoid (start, end, from, to as relative distances), frame_uniform and shell_uniform in any direction, frame_temperature and shell_temperature (temperature change, gradient), frame_strain and shell_strain. dir: GRAV, X, Y, Z or a local axis 1, 2, 3. replace (default true) takes off the same kind of load in that pattern first.",
            """
            {"type":"object","required":["kind","pattern","objects"],"properties":{
              "kind":{"type":"string"},"pattern":{"type":"string"},"objects":{"type":"array","items":{"type":"string"}},
              "values":{"type":"object","additionalProperties":{"type":"number"}},"dir":{"type":"string"},"replace":{"type":"boolean"}}}
            """, Ask.Changes, AssignLoads, a => $"Assign {a.Text("kind").Replace('_', ' ')} loads in {a.Text("pattern")}"),
        new("set_load_cases_to_run",
            "Analyze > Set Load Cases to Run: which load cases the next run runs. run lists the cases to run, not_run the cases to leave out; all true runs every case, all false none. A case not named keeps its choice. The run sets the program's own run flags before it starts (SAP2000's file gets RunCase=No).",
            """{"type":"object","properties":{"run":{"type":"array","items":{"type":"string"}},"not_run":{"type":"array","items":{"type":"string"}},"all":{"type":"boolean"}}}""",
            Ask.Changes, SetCasesToRun, a => "Set the load cases to run"),
        new("analysis_definitions",
            "What the analysis Define forms show: the functions with their points, every load case with its type, loads and options, the mass source and the P-Delta option.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => Definitions(Offline(e).Model)),
    ];

    // ---- functions ----

    private static JsonNode DefineFunction(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var name = a.Need("name").Trim();
        var kind = a.Need("kind").ToLowerInvariant();
        var from = a.Text("from", "points").ToLowerInvariant();
        if (kind is not ("spectrum" or "history" or "psd" or "steady")) throw new ToolInputException("kind is spectrum, history, psd or steady.");
        // SAP2000's own table for the function (its code or type and every parameter, written back to the .s2k as they are).
        var sapTable = a.Text("sap_table").Trim();
        if (sapTable.Length > 0)
        {
            sapTable = SapFunctions.Table(sapTable) ?? throw new ToolInputException($"There is no SAP2000 function table '{sapTable}'.");
            if (SapFunctions.KindOf(sapTable) != kind) throw new ToolInputException($"The table \"{sapTable}\" is for {SapFunctions.KindOf(sapTable)} functions, not {kind}.");
        }
        else if (kind is "psd" or "steady") throw new ToolInputException("A power spectral density or steady state function is SAP2000's own: give its sap_table.");
        var sapValues = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a.TryGetProperty("sap_values", out var sv) && sv.ValueKind == JsonValueKind.Object)
            foreach (var x in sv.EnumerateObject()) sapValues[x.Name] = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText();
        if (kind is "psd" or "steady" && from is not ("points" or "file")) throw new ToolInputException("A power spectral density or steady state function is from points or a file.");
        List<(double, double)> points;
        if (from is "points" or "periodic" or "file")
        {
            points = [];
            if (a.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var pair in p.EnumerateArray())
                {
                    var n = pair.ValueKind == JsonValueKind.Array ? pair.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray() : [];
                    if (n.Length != 2) throw new ToolInputException("Each point is [x, y].");
                    points.Add((n[0], n[1]));
                }
        }
        else if (from == "code")
        {
            if (kind != "spectrum") throw new ToolInputException("A code gives a spectrum: use kind spectrum.");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (a.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object)
                foreach (var x in v.EnumerateObject()) values[x.Name] = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText();
            points = Core(() => LateralLoads.SpectrumPoints(a.Need("code"), values, a.Number("last_period", 8)));
        }
        else if (from is not ("sine" or "cosine" or "ramp" or "sawtooth" or "triangular"))
            throw new ToolInputException($"There is no function type '{from}'.");
        else if (from == "ramp" && a.TryGetProperty("ramp_time", out _))
        {
            // ETABS's Ramp: Ramp Time, Amplitude, Maximum Time.
            double rt = a.Number("ramp_time", 1), max = a.Number("max_time", 2), amp = a.Number("amplitude", 1);
            if (kind != "history") throw new ToolInputException("A ramp is a time history: use kind history.");
            if (rt <= 0 || max <= rt) throw new ToolInputException("A ramp needs a ramp time above 0 and a maximum time above the ramp time.");
            points = [(0, 0), (rt, amp), (max, amp)];
        }
        else if (from is "sawtooth" or "triangular")
        {
            if (kind != "history") throw new ToolInputException($"A {from} wave is a time history: use kind history.");
            double period = a.Number("period", 1), cycles = a.Number("cycles", 1), amp = a.Number("amplitude", 1), rt = a.Number("ramp_time", period / 8);
            if (period <= 0 || cycles < 1 || cycles > 100000) throw new ToolInputException("A wave needs a period above 0 and 1 to 100000 cycles.");
            if (from == "sawtooth" && (rt <= 0 || rt > period / 4)) throw new ToolInputException("The sawtooth ramp time is above 0 and at most a quarter of the period.");
            var one = from == "sawtooth"
                ? new (double T, double Y)[] { (rt, amp), (period / 2 - rt, amp), (period / 2 + rt, -amp), (period - rt, -amp), (period, 0) }
                : new (double T, double Y)[] { (period / 4, amp), (3 * period / 4, -amp), (period, 0) };
            points = [(0, 0)];
            for (var c = 0; c < (int)Math.Round(cycles); c++)
                foreach (var (t, y) in one) points.Add((Math.Round(c * period + t, 9), y));
        }
        else
        {
            if (kind != "history") throw new ToolInputException($"A {from} wave is a time history: use kind history.");
            double period = a.Number("period", 1), cycles = a.Number("cycles", 1), amp = a.Number("amplitude", 1);
            var steps = a.Whole("steps_per_cycle", 16);
            if (period <= 0 || cycles <= 0 || steps < 2) throw new ToolInputException("A wave needs a period and cycles above 0 and 2 steps a cycle at least.");
            // Counted as a double first: cycles of 1e9 would wrap round an int (the robustness run found it).
            if (cycles * steps > 100000) throw new ToolInputException("That wave has more than 100000 points.");
            var n = (int)Math.Round(cycles * steps);
            points = Enumerable.Range(0, n + 1).Select(i =>
            {
                var t = i * period / steps;
                var y = from switch { "sine" => Math.Sin(2 * Math.PI * t / period), "cosine" => Math.Cos(2 * Math.PI * t / period), _ => Math.Min(1, t / (period * cycles)) };
                return (Math.Round(t, 9), Math.Round(amp * y, 9));
            }).ToList();
        }
        if (from == "periodic")
        {
            // User Periodic: one cycle of points, repeated Number of Cycles times.
            if (kind != "history") throw new ToolInputException("A user periodic function is a time history: use kind history.");
            var cycles = a.Number("cycles", 1);
            if (points.Count < 2 || points[0].Item1 != 0) throw new ToolInputException("One cycle of a user periodic function starts at time 0 and has two points at least.");
            if (cycles < 1 || cycles * points.Count > 100000) throw new ToolInputException("A user periodic function has 1 cycle at least and 100000 points at most.");
            var one = points; var span = one[^1].Item1;
            points = [.. one];
            for (var c = 1; c < (int)Math.Round(cycles); c++) points.AddRange(one.Skip(1).Select(p => (Math.Round(c * span + p.Item1, 9), p.Item2)));
        }
        if (a.Flag("preview"))
        {
            if (points.Count == 0) throw new ToolInputException("The function has no points.");
            return new JsonObject { ["points"] = new JsonArray(points.Select(p => (JsonNode)Pair(p)).ToArray()) };
        }
        if (kind is "psd" or "steady")
        {
            if (points.Count < 2) throw new ToolInputException($"The function {name} needs two points at least.");
            for (var i = 1; i < points.Count; i++)
                if (points[i].Item1 <= points[i - 1].Item1) throw new ToolInputException($"The function {name}: the frequencies must go up ({points[i].Item1} after {points[i - 1].Item1}).");
            var wasOther = a.Text("was").Trim();
            return link.Edit(m => Core(() =>
            {
                if (wasOther.Length > 0 && wasOther != name)
                {
                    if (!AnalysisDefs.DeleteFunction(m, wasOther)) throw new ArgumentException($"There is no function '{wasOther}'.");
                    foreach (var c in m.LoadCases) foreach (var l in c.Lines) for (var i = 0; i + 1 < l.Count; i++) if (l[i] == "FUNC" && E2kText.Unquote(l[i + 1]) == wasOther) l[i + 1] = E2kText.Quote(name);
                }
                // ETABS keeps its own power spectral density and steady state functions in the model file: SAP2000's form takes one over.
                var same = AnalysisDefs.ListFunctions(m).Find(x => x.Name == name);
                if (same is not null && same.Kind != (kind == "psd" ? "powerspecdensity" : "steadystate")) throw new ArgumentException($"There is already a function named {name}.");
                if (same is not null) AnalysisDefs.DeleteFunction(m, name);
                SapFunctions.Set(m, sapTable, name, sapValues, points);
                // The form's own choices the table has no field for (User's Frequency Units, From File's file settings).
                var keepSrc = new Dictionary<string, string> { ["Type"] = from };
                if (a.Text("units") is { Length: > 0 } units) keepSrc["units"] = units;
                if (from == "file" && a.TryGetProperty("file", out var ff) && ff.ValueKind == JsonValueKind.Object) foreach (var x in ff.EnumerateObject()) keepSrc["f:" + x.Name] = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText();
                AnalysisDefs.SetFunctionSource(m, name, keepSrc);
                return (JsonNode)new JsonObject { ["done"] = $"The {(kind == "psd" ? "power spectral density" : "steady state")} function {name}, {points.Count} points.", ["points"] = points.Count, ["first"] = Pair(points[0]), ["last"] = Pair(points[^1]) };
            }));
        }
        var f = new FunctionDef(name, kind, points, a.Number("damping", 0.05));
        var was = a.Text("was").Trim();
        // What the definition form showed, so Modify/Show opens it again with the same fields.
        var source = new Dictionary<string, string> { ["Type"] = from };
        if (from == "code") { source["Code"] = a.Text("code"); if (a.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Object) foreach (var x in vs.EnumerateObject()) source["v:" + x.Name] = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText(); }
        foreach (var k in new[] { "period", "cycles", "amplitude", "steps_per_cycle", "ramp_time", "max_time", "last_period" })
            if (a.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number) source[k] = x.GetRawText();
        if (from == "periodic" && a.TryGetProperty("points", out var cyc)) source["cycle"] = cyc.GetRawText();
        if (from == "file" && a.TryGetProperty("file", out var fl) && fl.ValueKind == JsonValueKind.Object) foreach (var x in fl.EnumerateObject()) source["f:" + x.Name] = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText();
        return link.Edit(m => Core(() =>
        {
            if (was.Length > 0 && was != name)
            {
                if (!AnalysisDefs.DeleteFunction(m, was)) throw new ArgumentException($"There is no function '{was}'.");
                foreach (var c in m.LoadCases) foreach (var l in c.Lines) for (var i = 0; i + 1 < l.Count; i++) if (l[i] == "FUNC" && E2kText.Unquote(l[i + 1]) == was) l[i + 1] = E2kText.Quote(name);
            }
            if (SapFunctions.Find(m, name) is { } other && SapFunctions.KindOf(other) is "psd" or "steady") throw new ArgumentException($"There is already a function named {name}.");
            AnalysisDefs.SetFunction(m, f);
            AnalysisDefs.SetFunctionSource(m, name, source);
            // SAP2000's own rows: set by the SAP2000 form; any other change of the function makes it a plain one again.
            SapFunctions.Remove(m, name);
            if (sapTable.Length > 0)
            {
                if (kind == "spectrum" && !sapValues.ContainsKey("FuncDamp") && SapFunctions.Tables[sapTable].Contains("FuncDamp")) sapValues["FuncDamp"] = f.Damping.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                var rowPoints = points;
                if (a.TryGetProperty("sap_points", out var sp) && sp.ValueKind == JsonValueKind.Array)
                    rowPoints = sp.EnumerateArray().Select(x => x.EnumerateArray().Select(y => y.GetDouble()).ToArray()).Where(x => x.Length == 2).Select(x => (x[0], x[1])).ToList();
                SapFunctions.Set(m, sapTable, name, sapValues, rowPoints);
            }
            return (JsonNode)new JsonObject { ["done"] = $"The {kind} function {name}, {points.Count} points.", ["points"] = points.Count, ["first"] = Pair(points[0]), ["last"] = Pair(points[^1]) };
        }));
    }

    private static JsonArray Pair((double X, double Y) p) => new(p.X, p.Y);

    // ---- load cases ----

    private static JsonNode DefineCase(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var loads = new List<CaseLoad>();
        if (a.TryGetProperty("loads", out var ls) && ls.ValueKind == JsonValueKind.Array)
            foreach (var l in ls.EnumerateArray())
            {
                var accel = l.Text("accel");
                var pattern = l.Text("pattern");
                if (accel.Length == 0 && pattern.Length == 0) throw new ToolInputException("Each load is a pattern or an acceleration (accel U1, U2 or U3).");
                loads.Add(new CaseLoad(accel.Length > 0 ? "accel" : "pattern", accel.Length > 0 ? accel.ToUpperInvariant() : pattern, l.Number("factor", 1),
                    l.Text("function") is { Length: > 0 } fn ? fn : null, l.Number("angle", 0), l.Number("arrival", 0)));
            }
        var c = new CaseDef(a.Need("name").Trim(), a.Need("type"), loads)
        {
            InitialCase = a.Text("initial_case") is { Length: > 0 } i ? i : null,
            MaxModes = a.Whole("max_modes", 12), MinModes = a.Whole("min_modes", 1), ModalCase = a.Text("modal_case", "Modal"),
            ModalCombo = a.Text("modal_combo", "CQC"), DirCombo = a.Text("dir_combo", "SRSS"), Damping = a.Number("damping", 0.05),
            Eccentricity = a.Number("eccentricity", 0.05), Steps = a.Whole("steps", 100), StepSize = a.Number("step_size", 0.01), Geometric = a.Text("geometric", "None"),
        };
        var was = a.Text("was") is { Length: > 0 } w ? w : null;
        return link.Edit(m => Core(() =>
        {
            var wasOff = was is not null && was != c.Name && !RunCases.Runs(m, was);
            AnalysisDefs.SetCase(m, c, was);
            if (wasOff) RunCases.Set(m, c.Name, false); // a renamed case keeps its Set Load Cases to Run choice
            return (JsonNode)new JsonObject { ["done"] = $"The {AnalysisDefs.CaseType(m.LoadCases.First(x => x.Name == c.Name))} case {c.Name}, {loads.Count} load{(loads.Count == 1 ? "" : "s")}." };
        }));
    }

    // ---- mass source and P-Delta ----

    private static List<(string, double)> PatternFactors(JsonElement a)
    {
        var list = new List<(string, double)>();
        if (a.TryGetProperty("loads", out var ls) && ls.ValueKind == JsonValueKind.Array)
            foreach (var l in ls.EnumerateArray()) list.Add((l.Need("pattern"), l.Number("factor")));
        return list;
    }

    private static JsonNode DefineMassSource(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var loads = PatternFactors(a);
        foreach (var (p, f) in loads)
        {
            if (!link.Model.LoadPatterns.Exists(x => x.Name == p)) throw new ToolInputException($"There is no load pattern '{p}'.");
            if (f < 0) throw new ToolInputException($"The factor of {p} is below 0.");
        }
        var elements = a.Flag("elements", true);
        if (!elements && loads.Count == 0 && !a.Flag("added_mass", true)) throw new ToolInputException("A mass source with nothing in it gives the model no mass.");
        return link.Edit(m =>
        {
            var now = MassSource.Of(m);
            var name = a.Text("name", now.Name).Trim();
            if (name.Length == 0) name = now.Name;
            var move = a.Flag("move", now.Move);
            new MassSource(name, elements, a.Flag("added_mass", true), loads, a.Flag("lateral", now.Lateral), a.Flag("vertical", now.Vertical), a.Flag("lump", now.Lump),
                move, move ? a.Number("move_x", now.MoveX) : 0, move ? a.Number("move_y", now.MoveY) : 0).WriteTo(m);
            return (JsonNode)new JsonObject { ["done"] = $"The mass source {name}: {(elements ? "the elements, " : "")}{string.Join(", ", loads.Select(l => $"{l.Item1} × {E2kText.Number(l.Item2)}"))}".TrimEnd(',', ' ') };
        });
    }

    private static JsonNode DefinePDelta(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var method = a.Need("method");
        var loads = PatternFactors(a);
        return link.Edit(m => Core(() =>
        {
            AnalysisDefs.SetPDelta(m, method, loads, a.Whole("iterations", 5), a.Number("tolerance", 0.001));
            return (JsonNode)new JsonObject { ["done"] = $"P-Delta: {method}." };
        }));
    }

    // ---- object loads ----

    private static readonly string[] Dirs = ["GRAV", "X", "Y", "Z", "1", "2", "3", "XPROJ", "YPROJ", "GRAVPROJ"];

    private static JsonNode AssignLoads(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var kind = a.Need("kind").ToLowerInvariant();
        if (!AnalysisDefs.LoadKinds.Contains(kind)) throw new ToolInputException($"There is no load kind '{kind}'. Kinds: {string.Join(", ", AnalysisDefs.LoadKinds)}.");
        var pattern = a.Need("pattern");
        if (!link.Model.LoadPatterns.Exists(p => p.Name == pattern)) throw new ToolInputException($"There is no load pattern '{pattern}'.");
        if (link.Model.LoadPatterns.First(p => p.Name == pattern).Auto is not null && kind.StartsWith("joint"))
            throw new ToolInputException($"{pattern} has a code load, whose joint loads are worked out: put these loads in another pattern.");
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (a.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var x in v.EnumerateObject())
                values[x.Name.ToLowerInvariant()] = x.Value.ValueKind == JsonValueKind.Number ? x.Value.GetDouble() : throw new ToolInputException($"'{x.Name}' needs a number.");
        if (values.Count == 0) throw new ToolInputException("Give the load's values, e.g. {\"fx\":10} or {\"temperature\":20}.");
        if (values.Values.Any(d => double.IsNaN(d) || double.IsInfinity(d))) throw new ToolInputException("A value is not a number.");
        foreach (var k in new[] { "distance", "from", "to" })
            if (values.TryGetValue(k, out var r) && (r < 0 || r > 1)) throw new ToolInputException($"'{k}' is a relative distance, from 0 to 1.");
        var dir = a.Text("dir", kind.EndsWith("strain") ? "11" : "GRAV").ToUpperInvariant();
        if (!kind.EndsWith("strain") && !Dirs.Contains(dir)) throw new ToolInputException($"dir is one of {string.Join(", ", Dirs)}.");
        var tokens = AnalysisDefs.LoadTokens(kind, pattern, values, dir);
        var type = E2kText.Value(tokens, "TYPE");
        var objects = a.Texts("objects");
        if (objects.Count == 0) throw new ToolInputException("Name the objects, as label@story.");
        var m0 = link.Model;
        foreach (var o in objects)
        {
            var at = o.LastIndexOf('@');
            if (at <= 0) throw new ToolInputException($"'{o}' is not label@story.");
            var (label, story) = (o[..at], o[(at + 1)..]);
            var ok = kind.StartsWith("joint") ? m0.Point(label) is not null && m0.StoryIndex(story) >= 0
                : kind.StartsWith("frame") ? m0.LineAssigns.Exists(x => x.Label == label && x.Story == story)
                : m0.AreaAssigns.Exists(x => x.Label == label && x.Story == story);
            if (!ok) throw new ToolInputException($"There is no {kind.Split('_')[0]} {o}.");
        }
        var replace = a.Flag("replace", true);
        return link.Edit(m =>
        {
            foreach (var o in objects)
            {
                var at = o.LastIndexOf('@');
                var (label, story) = (o[..at], o[(at + 1)..]);
                IList<ObjectLoad> list = kind.StartsWith("joint") ? m.PointLoads.Cast<ObjectLoad>().ToList() : kind.StartsWith("frame") ? m.LineLoads.Cast<ObjectLoad>().ToList() : m.AreaLoads.Cast<ObjectLoad>().ToList();
                if (replace)
                {
                    bool Same(ObjectLoad l) => l.Object == label && l.Story == story && l.Pattern == pattern && E2kText.Value(l.Tokens, "TYPE") == type;
                    if (kind.StartsWith("joint")) m.PointLoads.RemoveAll(Same); else if (kind.StartsWith("frame")) m.LineLoads.RemoveAll(Same); else m.AreaLoads.RemoveAll(Same);
                }
                if (kind.StartsWith("joint")) m.PointLoads.Add(new PointLoad { Object = label, Story = story, Tokens = [.. tokens] });
                else if (kind.StartsWith("frame")) m.LineLoads.Add(new LineLoad { Object = label, Story = story, Tokens = [.. tokens] });
                else m.AreaLoads.Add(new AreaLoad { Object = label, Story = story, Tokens = [.. tokens] });
            }
            return (JsonNode)new JsonObject { ["done"] = $"{kind.Replace('_', ' ')} loads in {pattern} on {objects.Count} object{(objects.Count == 1 ? "" : "s")}.", ["objects"] = objects.Count };
        });
    }

    // ---- Analyze > Set Load Cases to Run ----

    private static JsonNode SetCasesToRun(JsonElement a, ICsiLink e)
    {
        static List<string> Given(JsonElement a, string key) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
        var run = Given(a, "run");
        var notRun = Given(a, "not_run");
        bool? all = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("all", out var al) && al.ValueKind is JsonValueKind.True or JsonValueKind.False ? al.GetBoolean() : null;
        return Offline(e).Edit(m => Core(() =>
        {
            var cases = RunCases.List(m);
            if (run.Concat(notRun).FirstOrDefault(n => !cases.Exists(c => c.Name == n)) is { } bad) throw new ArgumentException($"The model has no load case {bad}.");
            var off = all switch { true => new HashSet<string>(StringComparer.Ordinal), false => cases.Select(c => c.Name).ToHashSet(StringComparer.Ordinal), _ => RunCases.NotRun(m) };
            foreach (var n in run) off.Remove(n);
            foreach (var n in notRun) off.Add(n);
            RunCases.SetNotRun(m, cases.Select(c => c.Name).Where(off.Contains));
            var count = cases.Count - off.Count;
            return (JsonNode)new JsonObject
            {
                ["done"] = $"{count} of {cases.Count} load case{(cases.Count == 1 ? "" : "s")} set to run.",
                ["not_run"] = new JsonArray(cases.Where(c => off.Contains(c.Name)).Select(c => (JsonNode)c.Name).ToArray()),
            };
        }));
    }

    // ---- what the forms show ----

    public static JsonObject Definitions(StructuralModel m)
    {
        var ms = MassSource.Of(m);
        var pd = AnalysisDefs.PDelta(m);
        return new JsonObject
        {
            ["functions"] = new JsonArray(AnalysisDefs.ListFunctions(m).Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name, ["kind"] = f.Kind, ["damping"] = f.Damping, ["points"] = new JsonArray(f.Points.Select(p => (JsonNode)Pair(p)).ToArray()),
                ["source"] = AnalysisDefs.FunctionSource(m, f.Name) is { } src ? new JsonObject(src.Where(x => x.Key != "Name").Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))) : null,
                ["used_by"] = new JsonArray(AnalysisDefs.UsersOfFunction(m, f.Name).Select(x => (JsonNode)x).ToArray()),
                ["sap_table"] = SapFunctions.Find(m, f.Name),
                ["sap_values"] = SapFunctions.Find(m, f.Name) is { } st ? new JsonObject(SapFunctions.Parameters(m, st, f.Name).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))) : null,
            }).Concat(SapFunctions.Others(m).Select(f => (JsonNode)new JsonObject
            {
                // SAP2000's power spectral density and steady state functions.
                ["name"] = f.Name, ["kind"] = f.Kind, ["damping"] = 0, ["points"] = new JsonArray(f.Points.Select(p => (JsonNode)Pair(p)).ToArray()),
                ["source"] = AnalysisDefs.FunctionSource(m, f.Name) is { } src ? new JsonObject(src.Where(x => x.Key != "Name").Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))) : null,
                ["used_by"] = new JsonArray(AnalysisDefs.UsersOfFunction(m, f.Name).Select(x => (JsonNode)x).ToArray()),
                ["sap_table"] = SapFunctions.Find(m, f.Name),
                ["sap_values"] = new JsonObject(SapFunctions.Parameters(m, SapFunctions.Find(m, f.Name)!, f.Name).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
            })).ToArray()),
            ["load_cases"] = new JsonArray(m.LoadCases.Select(c =>
            {
                var d = AnalysisDefs.ReadCase(c);
                return (JsonNode)new JsonObject
                {
                    ["name"] = d.Name, ["type"] = d.Type, ["initial_case"] = d.InitialCase, ["max_modes"] = d.MaxModes, ["min_modes"] = d.MinModes,
                    ["modal_case"] = d.ModalCase, ["modal_combo"] = d.ModalCombo, ["dir_combo"] = d.DirCombo, ["damping"] = d.Damping, ["eccentricity"] = d.Eccentricity,
                    ["steps"] = d.Steps, ["step_size"] = d.StepSize, ["geometric"] = d.Geometric, ["run"] = RunCases.Runs(m, d.Name),
                    ["loads"] = new JsonArray(d.Loads.Select(l => (JsonNode)new JsonObject
                    {
                        [l.Kind == "accel" ? "accel" : "pattern"] = l.Name, ["factor"] = l.Factor, ["function"] = l.Function, ["angle"] = l.Angle, ["arrival"] = l.Arrival,
                    }).ToArray()),
                };
            }).ToArray()),
            ["cases_to_run"] = new JsonArray(RunCases.List(m).Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["type"] = c.Type, ["run"] = c.Run }).ToArray()),
            ["case_types"] = new JsonArray(ProgramOptions.CaseTypes(m.Target).Select(t => (JsonNode)t).ToArray()),
            ["modal_combos"] = new JsonArray(AnalysisDefs.ModalCombos.Select(t => (JsonNode)t).ToArray()),
            ["dir_combos"] = new JsonArray(AnalysisDefs.DirCombos.Select(t => (JsonNode)t).ToArray()),
            ["mass_source"] = new JsonObject
            {
                ["name"] = ms.Name, ["elements"] = ms.Elements, ["added_mass"] = ms.AddedMass,
                ["lateral"] = ms.Lateral, ["vertical"] = ms.Vertical, ["lump"] = ms.Lump, ["move"] = ms.Move, ["move_x"] = ms.MoveX, ["move_y"] = ms.MoveY,
                ["loads"] = new JsonArray(ms.Loads.Select(l => (JsonNode)new JsonObject { ["pattern"] = l.Pattern, ["factor"] = l.Factor }).ToArray()),
            },
            ["pdelta"] = new JsonObject
            {
                ["method"] = pd.Method, ["iterations"] = pd.Iterations, ["tolerance"] = pd.Tolerance,
                ["loads"] = new JsonArray(pd.Loads.Select(l => (JsonNode)new JsonObject { ["pattern"] = l.Pattern, ["factor"] = l.Factor }).ToArray()),
            },
            ["load_kinds"] = new JsonArray(AnalysisDefs.LoadKinds.Select(t => (JsonNode)t).ToArray()),
        };
    }
}
