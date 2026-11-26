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
            "Adds or changes a response spectrum or time history function (Define > Functions). from 'points': the [x, y] pairs given (period and Sa in g, or time and value). from 'code': a code's spectrum (ASCE 7, IBC, Eurocode 8, ECP 201, IS 1893, UBC 97, NBCC, with the inputs lateral_codes lists for it). from 'sine', 'cosine' or 'ramp': a time history wave of the given period, cycles, amplitude and steps.",
            """
            {"type":"object","required":["name","kind"],"properties":{
              "name":{"type":"string"},"was":{"type":"string"},"kind":{"type":"string","enum":["spectrum","history"]},
              "from":{"type":"string","enum":["points","code","sine","cosine","ramp"],"description":"Default points."},
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
            "Sets the mass source (Define > Mass Source): the elements' own mass, the added mass, and load patterns with their factors (e.g. Dead 1, SDL 1, Live 0.25). The seismic loads by code are worked out from it again.",
            """{"type":"object","properties":{"elements":{"type":"boolean","description":"Default true."},"added_mass":{"type":"boolean","description":"Default true."},"loads":{"type":"array","items":{"type":"object","required":["pattern","factor"],"properties":{"pattern":{"type":"string"},"factor":{"type":"number"}}}}}}""",
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
        List<(double, double)> points;
        if (from == "points")
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
        var f = new FunctionDef(name, kind, points, a.Number("damping", 0.05));
        var was = a.Text("was").Trim();
        return link.Edit(m => Core(() =>
        {
            if (was.Length > 0 && was != name)
            {
                if (!AnalysisDefs.DeleteFunction(m, was)) throw new ArgumentException($"There is no function '{was}'.");
                foreach (var c in m.LoadCases) foreach (var l in c.Lines) for (var i = 0; i + 1 < l.Count; i++) if (l[i] == "FUNC" && E2kText.Unquote(l[i + 1]) == was) l[i + 1] = E2kText.Quote(name);
            }
            AnalysisDefs.SetFunction(m, f);
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
            Eccentricity = a.Number("eccentricity", 0), Steps = a.Whole("steps", 100), StepSize = a.Number("step_size", 0.01), Geometric = a.Text("geometric", "None"),
        };
        var was = a.Text("was") is { Length: > 0 } w ? w : null;
        return link.Edit(m => Core(() =>
        {
            AnalysisDefs.SetCase(m, c, was);
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
            var name = MassSource.Of(m).Name;
            new MassSource(name, elements, a.Flag("added_mass", true), loads).WriteTo(m);
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
                ["used_by"] = new JsonArray(AnalysisDefs.UsersOfFunction(m, f.Name).Select(x => (JsonNode)x).ToArray()),
            }).ToArray()),
            ["load_cases"] = new JsonArray(m.LoadCases.Select(c =>
            {
                var d = AnalysisDefs.ReadCase(c);
                return (JsonNode)new JsonObject
                {
                    ["name"] = d.Name, ["type"] = d.Type, ["initial_case"] = d.InitialCase, ["max_modes"] = d.MaxModes, ["min_modes"] = d.MinModes,
                    ["modal_case"] = d.ModalCase, ["modal_combo"] = d.ModalCombo, ["dir_combo"] = d.DirCombo, ["damping"] = d.Damping, ["eccentricity"] = d.Eccentricity,
                    ["steps"] = d.Steps, ["step_size"] = d.StepSize, ["geometric"] = d.Geometric,
                    ["loads"] = new JsonArray(d.Loads.Select(l => (JsonNode)new JsonObject
                    {
                        [l.Kind == "accel" ? "accel" : "pattern"] = l.Name, ["factor"] = l.Factor, ["function"] = l.Function, ["angle"] = l.Angle, ["arrival"] = l.Arrival,
                    }).ToArray()),
                };
            }).ToArray()),
            ["case_types"] = new JsonArray(ProgramOptions.CaseTypes(m.Target).Select(t => (JsonNode)t).ToArray()),
            ["modal_combos"] = new JsonArray(AnalysisDefs.ModalCombos.Select(t => (JsonNode)t).ToArray()),
            ["dir_combos"] = new JsonArray(AnalysisDefs.DirCombos.Select(t => (JsonNode)t).ToArray()),
            ["mass_source"] = new JsonObject
            {
                ["name"] = ms.Name, ["elements"] = ms.Elements, ["added_mass"] = ms.AddedMass,
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
