using CsiProMax.E2k;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Model;

/// <summary>A function as ETABS keeps it under "$ FUNCTIONS": a response spectrum (period, value) or a time history
/// (time, value), with the spectrum's damping.</summary>
public sealed record FunctionDef(string Name, string Kind, IReadOnlyList<(double X, double Y)> Points, double Damping = 0.05);

/// <summary>One load of a load case: a load pattern (LOADPAT) or a ground acceleration (ACCEL U1, U2, U3), with its
/// scale factor, and for a spectrum or history the function, the angle and the arrival time.</summary>
public sealed record CaseLoad(string Kind, string Name, double Factor, string? Function = null, double Angle = 0, double Arrival = 0);

/// <summary>A load case of any type the CSI programs have, and its options. Only the options of its type are used.</summary>
public sealed record CaseDef(string Name, string Type, IReadOnlyList<CaseLoad> Loads)
{
    public string? InitialCase { get; init; }
    public int MaxModes { get; init; } = 12;
    public int MinModes { get; init; } = 1;
    public string ModalCase { get; init; } = "Modal";
    public string ModalCombo { get; init; } = "CQC";
    public string DirCombo { get; init; } = "SRSS";
    public double Damping { get; init; } = 0.05;
    public double Eccentricity { get; init; }
    public int Steps { get; init; } = 100;
    public double StepSize { get; init; } = 0.01;
    public string Geometric { get; init; } = "None";
}

/// <summary>
/// Functions, load cases, the P-Delta option and the loads other than uniform gravity, written the way the model file
/// keeps them (ETABS's own keywords), so the Modeler defines everything the CSI programs' Define and Assign menus do.
/// </summary>
public static class AnalysisDefs
{
    public const string Functions = "FUNCTIONS";
    public const string Options = "ANALYSIS OPTIONS";

    /// <summary>The load case types, as ETABS names them.</summary>
    public static readonly string[] CaseTypes =
    [
        "Linear Static", "Nonlinear Static", "Modal - Eigen", "Modal - Ritz", "Response Spectrum",
        "Linear Modal History", "Linear Direct History", "Nonlinear Modal History (FNA)", "Nonlinear Direct History", "Buckling",
    ];

    public static readonly string[] ModalCombos = ["CQC", "SRSS", "ABS", "GMC", "NRC 10 Percent", "Double Sum"];
    public static readonly string[] DirCombos = ["SRSS", "ABS", "CQC3"];
    public static readonly string[] GeometricOptions = ["None", "P-Delta", "P-Delta and Large Displacements"];

    private static RawSection Section(StructuralModel m, string heading, string after)
    {
        var s = m.Passthrough.Find(x => x.Heading == heading);
        if (s is not null) return s;
        s = new RawSection { Heading = heading, After = m.Passthrough.Exists(x => x.Heading == after) || E2kSections.IsKnown(after) ? after : "" };
        m.Passthrough.Add(s);
        return s;
    }

    // ---- functions ----

    public static List<FunctionDef> ListFunctions(StructuralModel m)
    {
        var lines = m.Passthrough.Find(x => x.Heading == Functions)?.Lines ?? [];
        var kinds = new Dictionary<string, (string Kind, double Damp)>(StringComparer.Ordinal);
        var points = new Dictionary<string, List<(double, double)>>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var t = Tokenize(raw);
            if (t.Count < 3 || t[0] != "FUNCTION") continue;
            var name = Unquote(t[1]);
            if (Value(t, "FUNCTYPE") is { } type)
                kinds[name] = (type.ToUpperInvariant() switch { "SPECTRUM" => "spectrum", "HISTORY" => "history", var x => x.ToLowerInvariant() }, TryNumber(Value(t, "DAMPRATIO"), out var d) ? d : 0.05);
            if (Value(t, "TIMEVAL") is { } values)
            {
                var n = values.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => TryNumber(v, out var x) ? x : double.NaN).ToArray();
                var list = points.TryGetValue(name, out var have) ? have : points[name] = [];
                for (var i = 0; i + 1 < n.Length; i += 2) list.Add((n[i], n[i + 1]));
            }
        }
        return kinds.Select(k => new FunctionDef(k.Key, k.Value.Kind, points.GetValueOrDefault(k.Key) ?? [], k.Value.Damp)).ToList();
    }

    public static void SetFunction(StructuralModel m, FunctionDef f)
    {
        if (f.Kind is not ("spectrum" or "history")) throw new ArgumentException("A function is a spectrum or a history.");
        if (f.Points.Count < 2) throw new ArgumentException($"The function {f.Name} needs two points at least.");
        for (var i = 1; i < f.Points.Count; i++)
            if (f.Points[i].X <= f.Points[i - 1].X) throw new ArgumentException($"The function {f.Name}: the {(f.Kind == "spectrum" ? "periods" : "times")} must go up ({Number(f.Points[i].X)} after {Number(f.Points[i - 1].X)}).");
        if (f.Points.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.Y))) throw new ArgumentException($"The function {f.Name} has a point that is not a number.");
        var s = Section(m, Functions, "AREA ASSIGNS");
        s.Lines.RemoveAll(l => Tokenize(l) is { Count: > 1 } t && t[0] == "FUNCTION" && Unquote(t[1]) == f.Name);
        s.Lines.Add(f.Kind == "spectrum"
            ? Line(["FUNCTION", Quote(f.Name), "FUNCTYPE", Quote("SPECTRUM"), "DAMPRATIO", Number(f.Damping), "SPECTYPE", Quote("USER")])
            : Line(["FUNCTION", Quote(f.Name), "FUNCTYPE", Quote("HISTORY"), "HISTTYPE", Quote("USER")]));
        // ETABS writes the values in lines of a few points each.
        foreach (var chunk in f.Points.Chunk(50))
            s.Lines.Add(Line(["FUNCTION", Quote(f.Name), "TIMEVAL", Quote(string.Join("  ", chunk.Select(p => Number(p.X) + "  " + Number(p.Y))))]));
    }

    public static bool DeleteFunction(StructuralModel m, string name)
    {
        var s = m.Passthrough.Find(x => x.Heading == Functions);
        SetFunctionSource(m, name, null);
        // SAP2000's own rows of it (its code, type and parameters; a power spectral density or steady state function lives there only).
        var sap = SapFunctions.Remove(m, name);
        // ETABS's own rows of it (its code or type and parameters, from the Engine's ETABS forms).
        sap |= EtabsFunctions.Remove(m, name);
        return (s is not null && s.Lines.RemoveAll(l => Tokenize(l) is { Count: > 1 } t && t[0] == "FUNCTION" && Unquote(t[1]) == name) > 0) | sap;
    }

    /// <summary>How a function was made in its definition form (Ahmed 2026-11-27, every field): the function type
    /// chosen at "Choose Function Type to Add" (a code, From File, User, Sine, Cosine, Ramp, Sawtooth, Triangular,
    /// User Periodic) and that form's parameters, so Modify/Show opens the same form with the same values. The
    /// programs get the points (a user function, as their own Convert to User Defined makes it); this row stays in the
    /// .cpm only ("model|" is no program's table).</summary>
    public const string FunctionSources = "model|FUNCTION SOURCES";

    public static IReadOnlyDictionary<string, string>? FunctionSource(StructuralModel m, string name) =>
        m.Kept is not null && m.Kept.TryGetValue(FunctionSources, out var rows) ? rows.Find(r => r.GetValueOrDefault("Name") == name) : null;

    /// <summary>Sets (or with null removes) a function's source row.</summary>
    public static void SetFunctionSource(StructuralModel m, string name, IReadOnlyDictionary<string, string>? row)
    {
        m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
        var rows = m.Kept.TryGetValue(FunctionSources, out var have) ? have : m.Kept[FunctionSources] = [];
        rows.RemoveAll(r => r.GetValueOrDefault("Name") == name);
        if (row is not null) rows.Add(new Dictionary<string, string>(row) { ["Name"] = name });
        if (rows.Count == 0) m.Kept.Remove(FunctionSources);
    }

    /// <summary>The load cases that use a function.</summary>
    public static IEnumerable<string> UsersOfFunction(StructuralModel m, string name) =>
        m.LoadCases.Where(c => c.Lines.Any(l => Value(l, "FUNC") == name)).Select(c => c.Name);

    // ---- load cases ----

    public static string CaseType(LoadCase c) => c.Lines.Select(l => Value(l, "TYPE")).FirstOrDefault(v => v is not null) ?? "";

    public static void SetCase(StructuralModel m, CaseDef c, string? was = null)
    {
        var type = CaseTypes.FirstOrDefault(t => t.Equals(c.Type, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There is no load case type '{c.Type}'. Types: {string.Join(", ", CaseTypes)}.");
        var functions = ListFunctions(m).ToDictionary(f => f.Name, f => f.Kind, StringComparer.Ordinal);
        var cases = m.LoadCases.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        bool modal = type.StartsWith("Modal"), spectrum = type == "Response Spectrum", history = type.Contains("History");
        if (!modal && c.Loads.Count == 0) throw new ArgumentException($"The {type} case {c.Name} needs a load at least.");
        foreach (var l in c.Loads)
        {
            if (l.Kind == "accel")
            {
                if (l.Name is not ("U1" or "U2" or "U3" or "UX" or "UY" or "UZ")) throw new ArgumentException($"An acceleration is in U1, U2 or U3, not '{l.Name}'.");
                if (!modal && !spectrum && !history) throw new ArgumentException($"A {type} case takes load patterns, not accelerations.");
            }
            else if (!m.LoadPatterns.Exists(p => p.Name == l.Name)) throw new ArgumentException($"There is no load pattern '{l.Name}'.");
            if (spectrum || history)
            {
                var need = spectrum ? "spectrum" : "history";
                if (l.Function is null) throw new ArgumentException($"Each load of a {type} case needs a {need} function.");
                if (!functions.TryGetValue(l.Function, out var k)) throw new ArgumentException($"There is no function '{l.Function}'. Define it first (Define > Functions).");
                if (k != need) throw new ArgumentException($"{l.Function} is a {k} function; a {type} case needs a {need} function.");
            }
            if (spectrum && l.Kind != "accel") throw new ArgumentException("A response spectrum case takes accelerations (U1, U2, U3).");
        }
        if ((spectrum || type is "Linear Modal History" or "Nonlinear Modal History (FNA)") && !m.LoadCases.Exists(x => x.Name == c.ModalCase && CaseType(x).StartsWith("Modal")))
            throw new ArgumentException($"There is no modal case '{c.ModalCase}' for {c.Name} to use.");
        if (c.InitialCase is { } init && init.Length > 0 && !(cases.Contains(init) && init != c.Name && CaseType(m.LoadCases.First(x => x.Name == init)).StartsWith("Nonlinear")))
            throw new ArgumentException($"The initial conditions come from a nonlinear case: '{init}' is not one.");
        if (modal && (c.MaxModes < 1 || c.MinModes < 1 || c.MinModes > c.MaxModes)) throw new ArgumentException("The modes: 1 at least, and the minimum not above the maximum.");
        if (spectrum && !ModalCombos.Contains(c.ModalCombo)) throw new ArgumentException($"The modal combination is one of {string.Join(", ", ModalCombos)}.");
        if (spectrum && !DirCombos.Contains(c.DirCombo)) throw new ArgumentException($"The directional combination is one of {string.Join(", ", DirCombos)}.");
        if (history && (c.Steps < 1 || c.StepSize <= 0)) throw new ArgumentException("A history needs steps (1 at least) and a step size above 0.");
        if (c.Damping < 0 || c.Damping >= 1) throw new ArgumentException("The damping is a ratio from 0 to under 1, e.g. 0.05.");
        if (!GeometricOptions.Contains(c.Geometric, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"The geometric nonlinearity is one of {string.Join(", ", GeometricOptions)}.");

        var lines = new List<List<string>>
        {
            new() { "TYPE", Quote(type), "INITCOND", c.InitialCase is { Length: > 0 } ? Quote(c.InitialCase) : Quote("PRESET") },
        };
        if (modal) lines.Add(["MAXMODES", c.MaxModes.ToString(), "MINMODES", c.MinModes.ToString(), "EIGENSHIFTFREQ", "0", "EIGENCUTOFF", "0", "EIGENTOL", "1E-09"]);
        if (spectrum) lines.Add(["MODALCASE", Quote(c.ModalCase), "MODALCOMBO", Quote(c.ModalCombo), "DIRCOMBO", Quote(c.DirCombo), "DAMPING", Number(c.Damping), "ECCRATIO", Number(c.Eccentricity)]);
        if (history)
        {
            var h = new List<string> { "NUMSTEPS", c.Steps.ToString(), "STEPSIZE", Number(c.StepSize), "DAMPING", Number(c.Damping) };
            if (type.Contains("Modal")) h.InsertRange(0, ["MODALCASE", Quote(c.ModalCase)]);
            lines.Add(h);
        }
        if (type is "Nonlinear Static" or "Nonlinear Direct History" && !c.Geometric.Equals("None", StringComparison.OrdinalIgnoreCase))
            lines.Add(["GEONONLIN", Quote(c.Geometric.Equals("P-Delta", StringComparison.OrdinalIgnoreCase) ? "PDELTA" : "PDELTA LARGE DISP")]);
        // ETABS 23 reads NUMBUCKLINGMODES (it ignored NUMMODES and took 6, 2026-11-26 experiment F).
        if (type == "Buckling") lines.Add(["NUMBUCKLINGMODES", Math.Max(1, c.MaxModes).ToString()]);
        foreach (var l in c.Loads)
        {
            var row = l.Kind == "accel"
                ? new List<string> { "ACCEL", Quote(l.Name.Replace("UX", "U1").Replace("UY", "U2").Replace("UZ", "U3")) }
                : new List<string> { "LOADPAT", Quote(l.Name) };
            if (l.Function is not null) { row.Add("FUNC"); row.Add(Quote(l.Function)); }
            row.Add("SF"); row.Add(Number(l.Factor));
            if (spectrum || history) { if (l.Angle != 0) { row.Add("ANGLE"); row.Add(Number(l.Angle)); } }
            if (history && l.Arrival != 0) { row.Add("ARRIVALTIME"); row.Add(Number(l.Arrival)); }
            lines.Add(row);
        }

        var old = was ?? c.Name;
        var existing = m.LoadCases.Find(x => x.Name == old);
        if (was is not null && existing is null) throw new ArgumentException($"There is no load case '{was}'.");
        if (c.Name != old && cases.Contains(c.Name)) throw new ArgumentException($"There is a load case '{c.Name}' already.");
        if (existing is null) m.LoadCases.Add(new LoadCase { Name = c.Name, Lines = lines });
        else
        {
            existing.Name = c.Name; existing.Lines = lines;
            SapLoadTables.Rename(m, "case", old, c.Name);
            EtabsLoadTables.Rename(m, "case", old, c.Name);
            if (c.Name != old)
                foreach (var combo in m.Combos)
                    foreach (var line in combo.Lines)
                        if (line.Count >= 2 && line[0] == "LOADCASE" && Unquote(line[1]) == old) line[1] = Quote(c.Name);
        }
    }

    /// <summary>A case as its parts, for the forms and the other programs' files.</summary>
    public static CaseDef ReadCase(LoadCase c)
    {
        string? V(string key) => c.Lines.Select(l => Value(l, key)).FirstOrDefault(v => v is not null);
        double D(string key, double fallback) => TryNumber(V(key), out var d) ? d : fallback;
        int I(string key, int fallback) => (int)D(key, fallback);
        var loads = c.Lines.Where(l => l.Count >= 2 && l[0] is "LOADPAT" or "ACCEL")
            .Select(l => new CaseLoad(l[0] == "ACCEL" ? "accel" : "pattern", Unquote(l[1]), TryNumber(Value(l, "SF"), out var sf) ? sf : 1, Value(l, "FUNC"),
                TryNumber(Value(l, "ANGLE"), out var a) ? a : 0, TryNumber(Value(l, "ARRIVALTIME"), out var t) ? t : 0)).ToList();
        var init = V("INITCOND");
        var geo = V("GEONONLIN");
        return new CaseDef(c.Name, CaseType(c), loads)
        {
            InitialCase = init is null or "PRESET" ? null : init, MaxModes = I("MAXMODES", I("NUMMODES", I("NUMBUCKLINGMODES", 12))), MinModes = I("MINMODES", 1),
            ModalCase = V("MODALCASE") ?? "Modal", ModalCombo = V("MODALCOMBO") ?? "CQC", DirCombo = V("DIRCOMBO") ?? "SRSS",
            Damping = D("DAMPING", 0.05), Eccentricity = D("ECCRATIO", 0), Steps = I("NUMSTEPS", 100), StepSize = D("STEPSIZE", 0.01),
            Geometric = geo is null ? "None" : geo.Contains("LARGE") ? "P-Delta and Large Displacements" : "P-Delta",
        };
    }

    // ---- P-Delta ----

    /// <summary>The P-Delta option: none, non-iterative from the mass, or iterative from load patterns with factors.</summary>
    public static (string Method, IReadOnlyList<(string Pattern, double Factor)> Loads, int Iterations, double Tolerance) PDelta(StructuralModel m)
    {
        var lines = (m.Passthrough.Find(x => x.Heading == Options)?.Lines ?? []).Select(Tokenize).Where(t => t.Count > 0 && t[0] == "PDELTA").ToList();
        var method = lines.Select(t => Value(t, "METHOD")).FirstOrDefault(v => v is not null) ?? "NONE";
        var loads = lines.Where(t => Value(t, "LOADPAT") is not null).Select(t => (Value(t, "LOADPAT")!, TryNumber(Value(t, "SF"), out var f) ? f : 1)).ToList();
        var it = lines.Select(t => Value(t, "MAXITER")).FirstOrDefault(v => v is not null);
        var tol = lines.Select(t => Value(t, "TOLERANCE")).FirstOrDefault(v => v is not null);
        return (method switch { "NONITERATIVE" => "non-iterative", "ITERATIVE" => "iterative", _ => "none" }, loads,
            TryNumber(it, out var i) ? (int)i : 1, TryNumber(tol, out var tl) ? tl : 0.001);
    }

    public static void SetPDelta(StructuralModel m, string method, IReadOnlyList<(string Pattern, double Factor)> loads, int iterations = 5, double tolerance = 0.001)
    {
        method = method.ToLowerInvariant();
        if (method is not ("none" or "non-iterative" or "iterative")) throw new ArgumentException("P-Delta is none, non-iterative (from the mass) or iterative (from load patterns).");
        if (method == "iterative")
        {
            if (loads.Count == 0) throw new ArgumentException("Iterative P-Delta needs the load patterns it is based on.");
            foreach (var (p, _) in loads) if (!m.LoadPatterns.Exists(x => x.Name == p)) throw new ArgumentException($"There is no load pattern '{p}'.");
            if (iterations < 1 || tolerance <= 0) throw new ArgumentException("Iterative P-Delta needs 1 iteration at least and a tolerance above 0.");
        }
        var s = Section(m, Options, "SHELL OBJECT LOADS");
        var at = s.Lines.FindIndex(l => Tokenize(l) is { Count: > 0 } t && t[0] == "PDELTA");
        s.Lines.RemoveAll(l => Tokenize(l) is { Count: > 0 } t && t[0] == "PDELTA");
        var add = new List<string>();
        if (method == "iterative")
        {
            add.Add(Line(["PDELTA", "METHOD", Quote("ITERATIVE"), "MAXITER", iterations.ToString(), "TOLERANCE", Number(tolerance)]));
            add.AddRange(loads.Select(l => Line(["PDELTA", "LOADPAT", Quote(l.Pattern), "SF", Number(l.Factor)])));
        }
        else add.Add(Line(["PDELTA", "METHOD", Quote(method == "none" ? "NONE" : "NONITERATIVE")]));
        s.Lines.InsertRange(at < 0 ? s.Lines.Count : Math.Min(at, s.Lines.Count), add);
    }

    // ---- loads other than uniform gravity ----

    /// <summary>The object load kinds the Assign menu gives, beyond the uniform loads.</summary>
    public static readonly string[] LoadKinds =
    [
        "joint_force", "joint_displacement", "joint_temperature",
        "frame_point", "frame_trapezoid", "frame_uniform", "frame_temperature", "frame_strain",
        "shell_uniform", "shell_temperature", "shell_strain",
    ];

    /// <summary>The tokens of one load after the object and story, in ETABS's keywords.</summary>
    public static List<string> LoadTokens(string kind, string pattern, IReadOnlyDictionary<string, double> v, string dir)
    {
        double G(string k, double fallback = 0) => v.TryGetValue(k, out var x) ? x : fallback;
        var lc = new List<string> { "LC", Quote(pattern) };
        List<string> Pick(params string[] keys) => keys.Where(v.ContainsKey).SelectMany(k => new[] { k.ToUpperInvariant(), Number(v[k]) }).ToList();
        return kind switch
        {
            "joint_force" => [.. new[] { "TYPE", Quote("FORCE") }, .. lc, .. Pick("fx", "fy", "fz", "mx", "my", "mz")],
            "joint_displacement" => [.. new[] { "TYPE", Quote("GROUNDDISP") }, .. lc, .. Pick("ux", "uy", "uz", "rx", "ry", "rz")],
            "joint_temperature" or "frame_temperature" or "shell_temperature" =>
                [.. new[] { "TYPE", Quote("TEMP") }, .. lc, "TEMP", Number(G("temperature")), .. v.ContainsKey("gradient") ? new[] { "TEMPGRAD", Number(G("gradient")) } : []],
            "frame_strain" or "shell_strain" => [.. new[] { "TYPE", Quote("STRAIN") }, .. lc, "STRAIN", Number(G("strain")), "COMPONENT", Quote(dir.Length > 0 ? dir : "11")],
            "frame_point" => [.. new[] { "TYPE", Quote(v.ContainsKey("moment") ? "POINTM" : "POINTF"), "DIR", Quote(dir) }, .. lc, "FVAL", Number(v.ContainsKey("moment") ? G("moment") : G("value")), "RDIST", Number(G("distance", 0.5))],
            "frame_trapezoid" => [.. new[] { "TYPE", Quote("TRAPF"), "DIR", Quote(dir) }, .. lc, "FSTART", Number(G("start")), "FEND", Number(G("end")), "RDSTART", Number(G("from", 0)), "RDEND", Number(G("to", 1))],
            "frame_uniform" or "shell_uniform" => [.. new[] { "TYPE", Quote("UNIFF"), "DIR", Quote(dir) }, .. lc, "FVAL", Number(G("value"))],
            _ => throw new ArgumentException($"There is no load kind '{kind}'. Kinds: {string.Join(", ", LoadKinds)}."),
        };
    }
}
