using System.Globalization;
using System.Text.Json.Serialization;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Lateral;

/// <summary>A seismic or wind load by a code (<see cref="LateralCodes"/>), with what the Modeler computed from it.</summary>
public sealed class LateralDef
{
    public string Kind { get; set; } = LateralCodes.Seismic;
    public string Code { get; set; } = "";
    /// <summary>The code's inputs by their key (<see cref="CodeParam.Key"/>), as typed. A key left out takes its default.</summary>
    public Dictionary<string, string> Values { get; set; } = [];
    /// <summary>The story forces from the last computation, bottom first, in the model's units.</summary>
    public List<StoryForce> Forces { get; set; } = [];
    /// <summary>The numbers behind them, as the CSI programs' lateral load tables show them (T, Cs, V, k...).</summary>
    public Dictionary<string, double> Results { get; set; } = [];
    /// <summary>Why the forces could not be computed (no weight, a story that is not there); null when they were.</summary>
    public string? Error { get; set; }

    public string? Get(string key) =>
        Values.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { } v && v.Trim().Length > 0 ? v.Trim() : null;
}

/// <summary>The force on one story: at its mass center, moved by the eccentricity, as Fx, Fy and the moment Mz.</summary>
public sealed record StoryForce(string Story, double Elevation, double Weight, double Fx, double Fy, double Mz);

/// <summary>
/// Computes the story forces of a code's seismic or wind load and gives them to the model as joint loads at each
/// story, spread over the story's joints so that their resultant is the force at the mass center (moved by the
/// eccentricity) and their moment is the torsion. That is what ETABS applies for an auto lateral load on a rigid
/// diaphragm, written as loads every CSI program reads from the model file.
/// </summary>
public static class LateralLoads
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Computes every pattern that has a code load and rewrites its joint loads. Never throws: a load that
    /// cannot be computed keeps no joint loads and says why in its <see cref="LateralDef.Error"/>.</summary>
    public static void Refresh(StructuralModel m)
    {
        if (!m.LoadPatterns.Exists(p => p.Auto is not null)) return;
        Geometry? geometry = null;
        foreach (var p in m.LoadPatterns.Where(p => p.Auto is not null))
        {
            try
            {
                geometry ??= new Geometry(m);
                Apply(m, p, geometry);
                p.Auto!.Error = null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or ArithmeticException)
            {
                m.PointLoads.RemoveAll(l => l.Pattern == p.Name);
                p.Auto!.Forces = [];
                p.Auto.Results = [];
                p.Auto.Error = ex.Message;
            }
        }
    }

    /// <summary>Checks a definition before it is kept: the code is known, its numbers are numbers, its stories are there.</summary>
    public static LateralCode Check(StructuralModel m, LateralDef def)
    {
        var code = LateralCodes.Find(def.Kind, def.Code)
            ?? throw new ArgumentException($"There is no {def.Kind.ToLowerInvariant()} code '{def.Code}'. The codes: {string.Join(", ", LateralCodes.Of(def.Kind).Select(c => c.Name))}.");
        def.Code = code.Name;
        foreach (var key in def.Values.Keys.ToList())
        {
            var param = code.Params.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{code.Name} has no input '{key}'. Its inputs: {string.Join(", ", code.Params.Select(p => p.Key))}.");
            var v = def.Values[key].Trim();
            if (v.Length == 0) continue;
            if (param.Kind == ParamKind.Number && !double.TryParse(v, NumberStyles.Float, Inv, out _)) throw new ArgumentException($"{param.Label} ({param.Key}) needs a number: '{v}'.");
            if (param.Kind == ParamKind.Choice && param.Choices is { } choices && !choices.Contains(v, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"{param.Label} ({param.Key}) is one of {string.Join(", ", choices)}: '{v}'.");
            if (param.Kind == ParamKind.Story && m.StoryIndex(v) < 0) throw new ArgumentException($"There is no story '{v}' ({param.Label}).");
        }
        return code;
    }

    // ---- inputs ----

    private sealed class In(LateralDef def, LateralCode code)
    {
        public LateralCode Code => code;

        public string Text(string key) => def.Get(key) ?? code.Params.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Default ?? "";

        public double? Opt(string key) => Text(key) is { Length: > 0 } t && double.TryParse(t, NumberStyles.Float, Inv, out var v) ? v : null;

        public double Num(string key) => Opt(key) ?? throw new ArgumentException($"{code.Name}: '{key}' needs a number.");
    }

    // ---- the model's stories, weights and joints ----

    private sealed class StoryData
    {
        public double Weight, Wx, Wy;
        public readonly Dictionary<string, (double X, double Y)> Joints = new(StringComparer.Ordinal);
    }

    private sealed class Geometry
    {
        public readonly StoryData[] Stories;
        public readonly double ForceToN, LengthToM;

        public Geometry(StructuralModel m)
        {
            ForceToN = ModelTables.UnitFactor(m.Units.Force) ?? throw new ArgumentException($"The force unit '{m.Units.Force}' is not one the Modeler knows.");
            LengthToM = ModelTables.UnitFactor(m.Units.Length) ?? throw new ArgumentException($"The length unit '{m.Units.Length}' is not one the Modeler knows.");
            Stories = Enumerable.Range(0, m.Stories.Count).Select(_ => new StoryData()).ToArray();
            var elevations = Enumerable.Range(0, m.Stories.Count).Select(m.Elevation).ToArray();
            int Level(double z)
            {
                var best = 0;
                for (var i = 1; i < elevations.Length; i++) if (Math.Abs(elevations[i] - z) < Math.Abs(elevations[best] - z)) best = i;
                return best;
            }
            void Mass(int story, double w, double x, double y)
            {
                if (w == 0) return;
                Stories[story].Weight += w; Stories[story].Wx += w * x; Stories[story].Wy += w * y;
            }

            var points = m.Points.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
            var lines = m.Lines.GroupBy(l => l.Label).ToDictionary(g => g.Key, g => g.First());
            var areas = m.Areas.GroupBy(a => a.Label).ToDictionary(g => g.Key, g => g.First());
            void Joint(string point, int story)
            {
                if (story > 0 && story < Stories.Length && points.TryGetValue(point, out var p)) Stories[story].Joints.TryAdd(point, (p.X, p.Y));
            }
            foreach (var a in m.LineAssigns)
                if (lines.TryGetValue(a.Label, out var l) && m.StoryIndex(a.Story) is var s and >= 0) { Joint(l.PointJ, s); Joint(l.PointI, s - l.StoriesBelowI); }
            foreach (var a in m.AreaAssigns)
                if (areas.TryGetValue(a.Label, out var ar) && m.StoryIndex(a.Story) is var s and >= 0)
                    for (var i = 0; i < ar.PointNames.Count; i++) Joint(ar.PointNames[i], s - (i < ar.StoriesBelow.Count ? ar.StoriesBelow[i] : 0));
            foreach (var a in m.PointAssigns) if (m.StoryIndex(a.Story) is var s and > 0 && a.Restraint is null) Joint(a.Point, s);

            // The mass: the elements' own weight (when the mass source counts them) and the loads it counts, each at
            // the story it is lumped to: a member that spans stories half to each end.
            var source = MassSource.Of(m);
            var materials = m.Materials.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Number("WEIGHTPERVOLUME") ?? 0);
            var frames = m.FrameSections.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());
            var shells = m.ShellSections.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());
            var selfFactor = (source.Elements ? 1.0 : 0.0) + source.Loads.Sum(l => (m.LoadPatterns.Find(p => p.Name == l.Pattern)?.SelfWeight ?? 0) * l.Factor);
            var factor = source.Loads.GroupBy(l => l.Pattern).ToDictionary(g => g.Key, g => g.Sum(l => l.Factor));
            var frameLength = new Dictionary<(string, string), double>();
            var areaSize = new Dictionary<(string, string), double>();
            foreach (var f in Physical.Frames(m))
            {
                frameLength[(f.Label, f.Story)] = f.Length;
                if (f.Section is null || !frames.TryGetValue(f.Section, out var sec)) continue;
                var w = (sec.Area ?? sec.Number("AREA") ?? 0) * f.Length * materials.GetValueOrDefault(sec.Material) * selfFactor;
                int a = Level(f.I.Z), b = Level(f.J.Z);
                Mass(a, w / 2, f.I.X, f.I.Y); Mass(b, w / 2, f.J.X, f.J.Y);
            }
            foreach (var ar in Physical.Areas(m))
            {
                areaSize[(ar.Label, ar.Story)] = ar.Area;
                var cx = ar.Corners.Average(c => c.X); var cy = ar.Corners.Average(c => c.Y);
                var w = ar.Section is not null && shells.TryGetValue(ar.Section, out var sec) ? (sec.Thickness ?? 0) * ar.Area * materials.GetValueOrDefault(sec.Material) * selfFactor : 0;
                var low = ar.Corners.Min(c => c.Z); var high = ar.Corners.Max(c => c.Z);
                if (Level(low) == Level(high)) Mass(Level(high), w, cx, cy);
                else { Mass(Level(low), w / 2, cx, cy); Mass(Level(high), w / 2, cx, cy); }
            }
            if (factor.Count == 0) return;
            double Grav(ObjectLoad l) =>
                (E2kText.Value(l.Tokens, "TYPE") ?? "").Equals("UNIFF", StringComparison.OrdinalIgnoreCase) && (E2kText.Value(l.Tokens, "DIR") ?? "GRAV").Equals("GRAV", StringComparison.OrdinalIgnoreCase)
                && E2kText.TryNumber(E2kText.Value(l.Tokens, "FVAL"), out var v) ? v : 0;
            var frameAt = Physical.Frames(m).ToDictionary(f => (f.Label, f.Story));
            var areaAt = Physical.Areas(m).ToDictionary(f => (f.Label, f.Story));
            foreach (var l in m.AreaLoads)
                if (factor.TryGetValue(l.Pattern, out var k) && areaAt.TryGetValue((l.Object, l.Story), out var ar))
                    Mass(Level(ar.Corners.Max(c => c.Z)), Grav(l) * ar.Area * k, ar.Corners.Average(c => c.X), ar.Corners.Average(c => c.Y));
            foreach (var l in m.LineLoads)
                if (factor.TryGetValue(l.Pattern, out var k) && frameAt.TryGetValue((l.Object, l.Story), out var f))
                {
                    var w = Grav(l) * f.Length * k;
                    Mass(Level(f.I.Z), w / 2, f.I.X, f.I.Y); Mass(Level(f.J.Z), w / 2, f.J.X, f.J.Y);
                }
            foreach (var l in m.PointLoads)
                if (factor.TryGetValue(l.Pattern, out var k) && points.TryGetValue(l.Object, out var p) && m.StoryIndex(l.Story) is var s and >= 0
                    && E2kText.TryNumber(E2kText.Value(l.Tokens, "FZ"), out var fz))
                    Mass(s, -fz * k, p.X, p.Y);
        }
    }

    // ---- the computation ----

    private static void Apply(StructuralModel m, LoadPattern p, Geometry g)
    {
        var def = p.Auto!;
        var code = Check(m, def);
        var a = new In(def, code);
        int bottom = a.Text("BottomStory") is { Length: > 0 } b ? m.StoryIndex(b) : 0;
        int top = a.Text("TopStory") is { Length: > 0 } t ? m.StoryIndex(t) : m.Stories.Count - 1;
        if (bottom < 0 || top < 0) throw new ArgumentException("A story of the range is not in the model.");
        if (top <= bottom) throw new ArgumentException("The top story must be above the bottom story.");
        var levels = Enumerable.Range(bottom + 1, top - bottom).ToList();
        var h = levels.Select(i => m.Elevation(i) - m.Elevation(bottom)).ToArray();
        var w = levels.Select(i => g.Stories[i].Weight).ToArray();
        var results = new Dictionary<string, double>();
        double[] fx, fy, mz = new double[levels.Count];

        if (code.Kind == LateralCodes.Wind) (fx, fy, mz) = Wind(m, a, g, levels, h, bottom, results);
        else if (code.Family == "user-loads") (fx, fy, mz) = UserLoads(m, a, levels);
        else
        {
            var total = w.Sum();
            if (total <= 0) throw new ArgumentException("The stories in the range have no weight: give the members sections and materials with a unit weight, or loads in the mass source.");
            var (cs, k, ft) = Seismic(m, a, g, h[^1] * g.LengthToM, results);
            var v = cs * total;
            results["W"] = total; results["V"] = v; results["k"] = k;
            var sum = Enumerable.Range(0, w.Length).Sum(i => w[i] * Math.Pow(h[i], k));
            var f = Enumerable.Range(0, w.Length).Select(i => sum > 0 ? (v - ft * v) * w[i] * Math.Pow(h[i], k) / sum : 0).ToArray();
            if (ft > 0) { f[^1] += ft * v; results["Ft"] = ft * v; }
            var x = a.Text("Dir").StartsWith("X", StringComparison.OrdinalIgnoreCase);
            fx = x ? f : new double[f.Length];
            fy = x ? new double[f.Length] : f;
        }

        // The eccentricity moves each story's force across the building: ±e times the plan size across it.
        var dir = code.Kind == LateralCodes.Seismic ? a.Text("Dir") : "";
        var sign = dir.EndsWith("+Ecc", StringComparison.OrdinalIgnoreCase) ? 1 : dir.EndsWith("-Ecc", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
        var userEcc = code.Family == "user-loads" && code.Kind == LateralCodes.Seismic ? a.Opt("Ecc") ?? 0 : 0;

        m.PointLoads.RemoveAll(l => l.Pattern == p.Name);
        var forces = new List<StoryForce>();
        for (var i = 0; i < levels.Count; i++)
        {
            var story = g.Stories[levels[i]];
            var name = m.Stories[levels[i]].Name;
            if (fx[i] == 0 && fy[i] == 0 && mz[i] == 0) { forces.Add(new(name, m.Elevation(levels[i]), w[i], 0, 0, 0)); continue; }
            if (story.Joints.Count == 0) throw new ArgumentException($"Story {name} has no joints to take its lateral force: draw members on it, or start the range above it.");
            var joints = story.Joints.ToList();
            double gx = joints.Average(j => j.Value.X), gy = joints.Average(j => j.Value.Y);
            double cx = story.Weight > 0 ? story.Wx / story.Weight : gx, cy = story.Weight > 0 ? story.Wy / story.Weight : gy;
            if (code.Kind == LateralCodes.Wind) { cx = gx; cy = gy; }
            double mx = cx, my = cy;
            double lx = joints.Max(j => j.Value.X) - joints.Min(j => j.Value.X), ly = joints.Max(j => j.Value.Y) - joints.Min(j => j.Value.Y);
            var ecc = sign * (a.Opt("Ecc") ?? 0);
            if (Math.Abs(fx[i]) >= Math.Abs(fy[i])) cy += ecc * ly; else cx += ecc * lx;
            var userMz = 0.0;
            if (userEcc != 0) userMz = -fx[i] * userEcc * ly + fy[i] * userEcc * lx;
            // The moment about the joints' centroid: the force at (cx, cy) plus the story's own moment.
            var moment = (cx - gx) * fy[i] - (cy - gy) * fx[i] + mz[i] + userMz;
            var r2 = joints.Sum(j => Math.Pow(j.Value.X - gx, 2) + Math.Pow(j.Value.Y - gy, 2));
            if (r2 == 0 && moment != 0 && Math.Abs(moment) > 1e-9 * (Math.Abs(fx[i]) + Math.Abs(fy[i]) + 1)) throw new ArgumentException($"Story {name} has its joints at one point, so it cannot take a torsion: give it more joints or no eccentricity.");
            foreach (var (point, (x, y)) in joints)
            {
                var jx = fx[i] / joints.Count - (r2 > 0 ? moment * (y - gy) / r2 : 0);
                var jy = fy[i] / joints.Count + (r2 > 0 ? moment * (x - gx) / r2 : 0);
                var tokens = new List<string> { "TYPE", E2kText.Quote("FORCE"), "LC", E2kText.Quote(p.Name) };
                if (Math.Abs(jx) > 1e-12) { tokens.Add("FX"); tokens.Add(E2kText.Number(Round(jx))); }
                if (Math.Abs(jy) > 1e-12) { tokens.Add("FY"); tokens.Add(E2kText.Number(Round(jy))); }
                if (tokens.Count > 4) m.PointLoads.Add(new PointLoad { Object = point, Story = name, Tokens = tokens });
            }
            // Shown as the CSI programs show it: the force at the mass center and the torsion about it.
            var torsion = (cx - mx) * fy[i] - (cy - my) * fx[i] + mz[i] + userMz;
            forces.Add(new(name, m.Elevation(levels[i]), Round(w[i]), Round(fx[i]), Round(fy[i]), Round(torsion)));
        }
        def.Forces = forces;
        def.Results = results.ToDictionary(kv => kv.Key, kv => Round(kv.Value));
    }

    private static double Round(double v) => Math.Abs(v) < 1e-12 ? 0 : Math.Round(v, 6 - (int)Math.Floor(Math.Log10(Math.Abs(v)) + 1) is var d and >= 0 and <= 12 ? d : 6);

    // ---- seismic ----

    /// <summary>Ct and x in metres for a system, by the code family's own values.</summary>
    private static (double Ct, double X) Ct(string family, string system)
    {
        var s = system.ToLowerInvariant();
        bool concrete = s.StartsWith("concrete"), steel = s.StartsWith("steel moment"), braced = s.Contains("braced");
        return family switch
        {
            "asce7" or "user-spectrum" => concrete ? (0.0466, 0.9) : steel ? (0.0724, 0.8) : braced ? (0.0731, 0.75) : (0.0488, 0.75),
            "ubc97" => steel ? (0.0853, 0.75) : concrete || braced ? (0.0731, 0.75) : (0.0488, 0.75),
            "nbcc" => steel ? (0.085, 0.75) : concrete ? (0.075, 0.75) : braced ? (0.025, 1.0) : (0.05, 0.75),
            _ => steel ? (0.085, 0.75) : concrete || braced ? (0.075, 0.75) : (0.05, 0.75),
        };
    }

    /// <summary>The base shear coefficient (of W), the height exponent and the top force share Ft/V.</summary>
    private static (double Cs, double K, double Ft) Seismic(StructuralModel m, In a, Geometry g, double hnM, Dictionary<string, double> r)
    {
        var family = a.Code.Family;
        if (family == "user-coefficient")
        {
            r["C"] = a.Num("C");
            return (a.Num("C"), a.Opt("K") ?? 1, 0);
        }
        var (ct0, x0) = Ct(family, a.Text("System"));
        var ct = a.Opt("Ct") ?? ct0; var x = a.Opt("x") ?? x0;
        var ta = ct * Math.Pow(hnM, x);
        var user = a.Text("Period").Equals("User", StringComparison.OrdinalIgnoreCase);
        var t = user ? a.Opt("UserT") ?? throw new ArgumentException("The period is User: give the user period T.") : ta;
        if (t <= 0) throw new ArgumentException("The period must be above 0.");
        r["Ta"] = ta;
        double KFromT(double tt) => tt <= 0.5 ? 1 : tt >= 2.5 ? 2 : 1 + (tt - 0.5) / 2;

        switch (family)
        {
            case "asce7":
            {
                var site = a.Text("SiteClass");
                double ss = a.Num("Ss"), s1 = a.Num("S1"), rr = a.Num("R"), ie = a.Num("I"), tl = a.Num("TL");
                var fa = a.Opt("Fa") ?? Asce.Fa(site, ss);
                var fv = a.Opt("Fv") ?? Asce.Fv(site, s1);
                var sds = a.Opt("SDS") ?? 2.0 / 3 * fa * ss;
                var sd1 = a.Opt("SD1") ?? 2.0 / 3 * fv * s1;
                if (user && a.Text("CuCap").Equals("Yes", StringComparison.OrdinalIgnoreCase)) t = Math.Min(t, Asce.Cu(sd1) * ta);
                var cs = sds / (rr / ie);
                cs = Math.Min(cs, t <= tl ? sd1 / (t * rr / ie) : sd1 * tl / (t * t * rr / ie));
                cs = Math.Max(cs, Math.Max(0.044 * sds * ie, 0.01));
                if (s1 >= 0.6) cs = Math.Max(cs, 0.5 * s1 / (rr / ie));
                r["Fa"] = fa; r["Fv"] = fv; r["SDS"] = sds; r["SD1"] = sd1; r["T"] = t; r["Cs"] = cs;
                return (cs, KFromT(t), 0);
            }
            case "ubc97":
            {
                double z = a.Num("Z"), na = a.Num("Na"), nv = a.Num("Nv"), rr = a.Num("R"), ie = a.Num("I");
                var soil = a.Text("SoilType");
                var ca = a.Opt("Ca") ?? Ubc.Ca(soil, z) * na;
                var cv = a.Opt("Cv") ?? Ubc.Cv(soil, z) * nv;
                if (user) t = Math.Min(t, (z >= 0.4 ? 1.3 : 1.4) * ta);
                var cs = Math.Min(cv * ie / (rr * t), 2.5 * ca * ie / rr);
                cs = Math.Max(cs, 0.11 * ca * ie);
                if (z >= 0.4) cs = Math.Max(cs, 0.8 * z * nv * ie / rr);
                var ft = t > 0.7 ? Math.Min(0.07 * t, 0.25) : 0;
                r["Ca"] = ca; r["Cv"] = cv; r["T"] = t; r["Cs"] = cs;
                return (cs, 1, ft);
            }
            case "ec8":
            {
                var ground = a.Text("Ground");
                var type2 = a.Text("SpectrumType") == "2";
                var (s0, tb0, tc0, td0) = Ec8.Table(ground, type2);
                double s = a.Opt("S") ?? s0, tb = a.Opt("TB") ?? tb0, tc = a.Opt("TC") ?? tc0, td = a.Opt("TD") ?? td0;
                var ag = a.Num("ag") * a.Num("GammaI");
                var q = a.Num("q");
                var beta = a.Num("Beta");
                var sd = Ec8.Sd(t, ag, s, tb, tc, td, q, beta);
                var lambda = a.Opt("Lambda") ?? (t <= 2 * tc && m.Stories.Count - 1 > 2 ? 0.85 : 1);
                r["ag"] = ag; r["S"] = s; r["TB"] = tb; r["TC"] = tc; r["TD"] = td; r["T"] = t; r["Sd"] = sd; r["lambda"] = lambda; r["Cs"] = sd * lambda;
                return (sd * lambda, 1, 0);
            }
            case "nbcc":
            {
                var mrf = a.Text("System").Contains("moment", StringComparison.OrdinalIgnoreCase);
                if (user) t = Math.Min(t, (mrf ? 1.5 : 2.0) * ta);
                double[] at = [0.2, 0.5, 1.0, 2.0, 5.0, 10.0];
                var sa = new[] { a.Num("Sa02"), a.Num("Sa05"), a.Num("Sa10"), a.Num("Sa20"), a.Num("Sa50"), a.Num("Sa100") };
                double S(double tt) => Interp(at, sa, tt);
                double mv = a.Num("Mv"), ie = a.Num("IE"), rd = a.Num("Rd"), ro = a.Num("Ro");
                var cs = S(t) * mv * ie / (rd * ro);
                var older = a.Code.Name.EndsWith("2005") || a.Code.Name.EndsWith("2010");
                cs = Math.Max(cs, S(older ? 2.0 : 4.0) * mv * ie / (rd * ro));
                if (rd >= 1.5) cs = Math.Min(cs, 2.0 / 3 * S(0.2) * ie / (rd * ro));
                var ft = t > 0.7 ? Math.Min(0.07 * t, 0.25) : 0;
                r["T"] = t; r["S(T)"] = S(t); r["Cs"] = cs;
                return (cs, 1, ft);
            }
            case "is1893":
            {
                var soil = a.Text("Soil");
                if (!user && a.Text("System").StartsWith("All", StringComparison.OrdinalIgnoreCase) && a.Opt("Ct") is null)
                {
                    // 7.6.2 (b): 0.09 h / √d, d the plan size along the force.
                    var x2 = a.Text("Dir").StartsWith("X", StringComparison.OrdinalIgnoreCase);
                    var all = g.Stories.SelectMany(s => s.Joints.Values).ToList();
                    var d = all.Count == 0 ? 0 : (x2 ? all.Max(j => j.X) - all.Min(j => j.X) : all.Max(j => j.Y) - all.Min(j => j.Y)) * g.LengthToM;
                    if (d > 0) t = ta = 0.09 * hnM / Math.Sqrt(d);
                    r["Ta"] = ta;
                }
                double z = a.Num("Z"), ie = a.Num("I"), rr = a.Num("R");
                var sa = Is.Sa(t, soil);
                var ah = z / 2 * ie / rr * sa;
                if (a.Code.Name.Contains("2016")) ah = Math.Max(ah, z switch { <= 0.10 => 0.007, <= 0.16 => 0.011, <= 0.24 => 0.016, _ => 0.024 });
                r["T"] = t; r["Sa/g"] = sa; r["Ah"] = ah; r["Cs"] = ah;
                return (ah, 2, 0);
            }
            case "user-spectrum":
            {
                var pts = Numbers(a.Text("Spectrum"), "the spectrum points");
                if (pts.Length < 4 || pts.Length % 2 != 0) throw new ArgumentException("The spectrum points are periods and Sa in pairs: T1 Sa1 T2 Sa2 ...");
                var ts = pts.Where((_, i) => i % 2 == 0).ToArray(); var vs = pts.Where((_, i) => i % 2 == 1).ToArray();
                for (var i = 1; i < ts.Length; i++) if (ts[i] <= ts[i - 1]) throw new ArgumentException("The spectrum periods must go up.");
                var sa = Interp(ts, vs, t);
                var cs = Math.Max(sa * a.Num("I") / a.Num("R"), a.Opt("Min") ?? 0);
                r["T"] = t; r["Sa"] = sa; r["Cs"] = cs;
                return (cs, a.Opt("K") ?? KFromT(t), 0);
            }
        }
        throw new ArgumentException($"{a.Code.Name} is not a seismic code the Modeler computes.");
    }

    private static double Interp(double[] at, double[] v, double t)
    {
        if (t <= at[0]) return v[0];
        for (var i = 1; i < at.Length; i++) if (t <= at[i]) return v[i - 1] + (v[i] - v[i - 1]) * (t - at[i - 1]) / (at[i] - at[i - 1]);
        return v[^1];
    }

    private static double[] Numbers(string text, string what) =>
        text.Split(new[] { ' ', ',', ';', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : throw new ArgumentException($"'{s}' in {what} is not a number.")).ToArray();

    private static (double[] Fx, double[] Fy, double[] Mz) UserLoads(StructuralModel m, In a, List<int> levels)
    {
        var fx = new double[levels.Count]; var fy = new double[levels.Count]; var mz = new double[levels.Count];
        foreach (var item in a.Text("Loads").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Trim().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var at = levels.FindIndex(i => m.Stories[i].Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (at < 0) throw new ArgumentException($"The user loads name the story '{parts[0]}', which is not in the range.");
            var n = Numbers(string.Join(' ', parts.Skip(1)), $"the loads of {parts[0]}");
            fx[at] += n.ElementAtOrDefault(0); fy[at] += n.ElementAtOrDefault(1); mz[at] += n.ElementAtOrDefault(2);
        }
        return (fx, fy, mz);
    }

    // ---- wind ----

    private static (double[] Fx, double[] Fy, double[] Mz) Wind(StructuralModel m, In a, Geometry g, List<int> levels, double[] h, int bottom, Dictionary<string, double> r)
    {
        if (a.Code.Family == "user-loads") return UserLoads(m, a, levels);
        var angle = (a.Opt("Angle") ?? 0) * Math.PI / 180;
        double dx = Math.Cos(angle), dy = Math.Sin(angle);
        // The exposed width: the plan across the wind, from the joints in the range.
        var joints = levels.SelectMany(i => g.Stories[i].Joints.Values).ToList();
        var across = joints.Count == 0 ? 0 : joints.Max(j => -j.X * dy + j.Y * dx) - joints.Min(j => -j.X * dy + j.Y * dx);
        var width = a.Opt("Width") ?? across;
        if (width <= 0) throw new ArgumentException("The building has no width across the wind: draw it first, or give the exposure width.");
        r["Width"] = width;
        var hTop = h[^1];
        // Each story takes the wind from halfway down to the story below to halfway up to the story above.
        var trib = new double[levels.Count];
        for (var i = 0; i < levels.Count; i++)
        {
            var below = i == 0 ? h[0] : h[i] - h[i - 1];
            var above = i == levels.Count - 1 ? 0 : h[i + 1] - h[i];
            trib[i] = (below + above) / 2;
        }
        // The pressures, in N/m² at a height in metres, then in the model's force per area.
        double toModel = g.LengthToM * g.LengthToM / g.ForceToN;
        Func<double, double> windward, leeward;
        var speed = a.Opt("Speed") ?? 0;
        var v = (a.Text("SpeedUnit").ToLowerInvariant() switch { "mph" => 0.44704, "km/h" => 1 / 3.6, _ => 1.0 }) * speed;
        double cpw = a.Opt("Cpw") ?? 0.8, cpl = a.Opt("Cpl") ?? 0.5;
        var hTopM = hTop * g.LengthToM;
        switch (a.Code.Family)
        {
            case "asce7-wind":
            {
                var (alpha, zg) = a.Text("Exposure").ToUpperInvariant() switch { "B" => (7.0, 365.76), "D" => (11.5, 213.36), _ => (9.5, 274.32) };
                double kzt = a.Num("Kzt"), kd = a.Num("Kd"), ke = a.Code.Name.EndsWith("16") || a.Code.Name.EndsWith("22") ? a.Num("Ke") : 1, gust = a.Num("G");
                var iw = a.Code.Name.EndsWith("05") ? a.Num("Iw") : 1;
                double Kz(double z) => 2.01 * Math.Pow(Math.Max(z, 4.57) / zg, 2 / alpha);
                double Q(double z) => 0.613 * Kz(z) * kzt * kd * ke * iw * v * v;
                windward = z => Q(z) * gust * cpw;
                leeward = _ => Q(hTopM) * gust * cpl;
                r["qh"] = Q(hTopM) * toModel;
                break;
            }
            case "ec1-wind":
            {
                var (z0, zmin) = a.Text("Terrain").ToUpperInvariant() switch { "0" => (0.003, 1.0), "I" => (0.01, 1.0), "III" => (0.3, 5.0), "IV" => (1.0, 10.0), _ => (0.05, 2.0) };
                double co = a.Num("Co"), rho = a.Num("Rho"), cscd = a.Num("CsCd");
                var kr = 0.19 * Math.Pow(z0 / 0.05, 0.07);
                double Qp(double z)
                {
                    var zz = Math.Max(z, zmin);
                    var vm = kr * Math.Log(zz / z0) * co * v;
                    var iv = 1 / (co * Math.Log(zz / z0));
                    return (1 + 7 * iv) * 0.5 * rho * vm * vm;
                }
                windward = z => Qp(z) * cscd * cpw;
                leeward = _ => Qp(hTopM) * cscd * cpl;
                r["qp(h)"] = Qp(hTopM) * toModel;
                break;
            }
            case "is875-wind":
            {
                double k1 = a.Num("k1"), k3 = a.Num("k3"), k4 = a.Num("k4");
                var cat = (int)(a.Opt("Category") ?? 2);
                double P(double z) { var vz = v * k1 * Is.K2(z, cat) * k3 * k4; return 0.6 * vz * vz; }
                windward = z => P(z) * cpw;
                leeward = _ => P(hTopM) * cpl;
                r["pz(h)"] = P(hTopM) * toModel;
                break;
            }
            case "user-pressure":
            {
                var pts = Numbers(a.Text("Profile"), "the pressure profile");
                if (pts.Length < 2 || pts.Length % 2 != 0) throw new ArgumentException("The pressure profile is heights and pressures in pairs: z1 p1 z2 p2 ...");
                var zs = pts.Where((_, i) => i % 2 == 0).ToArray(); var ps = pts.Where((_, i) => i % 2 == 1).ToArray();
                for (var i = 1; i < zs.Length; i++) if (zs[i] <= zs[i - 1]) throw new ArgumentException("The profile heights must go up.");
                var factor = a.Num("Factor");
                // Already in the model's units: the net pressure on the building.
                windward = z => Interp(zs, ps, z / g.LengthToM) * factor / toModel;
                leeward = _ => 0;
                break;
            }
            default: throw new ArgumentException($"{a.Code.Name} is not a wind code the Modeler computes.");
        }
        var fx = new double[levels.Count]; var fy = new double[levels.Count]; var mz = new double[levels.Count];
        var total = 0.0;
        for (var i = 0; i < levels.Count; i++)
        {
            var p = (windward(h[i] * g.LengthToM) + leeward(h[i] * g.LengthToM)) * toModel;
            var f = p * width * trib[i];
            fx[i] = f * dx; fy[i] = f * dy; total += f;
        }
        r["V"] = total;
        return (fx, fy, mz);
    }

    // ---- a code's spectrum as a function ----

    /// <summary>
    /// A code's response spectrum as (period, value in g) points from 0 to <paramref name="last"/> s, for a response
    /// spectrum function: ASCE 7 / IBC design Sa (11.4.6), Eurocode 8 / ECP 201 design Sd (3.2.2.5, with q),
    /// IS 1893 Sa/g, UBC 97 (Ca, Cv), NBCC S(T), or the user spectrum's points. Scale it in the load case by g
    /// (and Ie/R where the code divides by them).
    /// </summary>
    public static List<(double T, double Sa)> SpectrumPoints(string codeName, Dictionary<string, string> values, double last = 8)
    {
        var code = LateralCodes.Find(LateralCodes.Seismic, codeName)
            ?? throw new ArgumentException($"There is no seismic code '{codeName}'. The codes: {string.Join(", ", LateralCodes.Of(LateralCodes.Seismic).Where(c => c.Family is not ("user-coefficient" or "user-loads")).Select(c => c.Name))}.");
        var def = new LateralDef { Code = code.Name, Values = values };
        Check(new StructuralModel { Stories = [new Story { Name = "Base" }] }, def);
        var a = new In(def, code);
        Func<double, double> sa;
        var marks = new List<double>();
        switch (code.Family)
        {
            case "asce7":
            {
                var site = a.Text("SiteClass");
                double ss = a.Num("Ss"), s1 = a.Num("S1"), tl = a.Num("TL");
                var sds = a.Opt("SDS") ?? 2.0 / 3 * (a.Opt("Fa") ?? Asce.Fa(site, ss)) * ss;
                var sd1 = a.Opt("SD1") ?? 2.0 / 3 * (a.Opt("Fv") ?? Asce.Fv(site, s1)) * s1;
                double ts = sd1 / sds, t0 = 0.2 * ts;
                marks.AddRange([t0, ts, tl]);
                sa = t => t < t0 ? sds * (0.4 + 0.6 * t / t0) : t <= ts ? sds : t <= tl ? sd1 / t : sd1 * tl / (t * t);
                break;
            }
            case "ec8":
            {
                var (s0, tb0, tc0, td0) = Ec8.Table(a.Text("Ground"), a.Text("SpectrumType") == "2");
                double s = a.Opt("S") ?? s0, tb = a.Opt("TB") ?? tb0, tc = a.Opt("TC") ?? tc0, td = a.Opt("TD") ?? td0, ag = a.Num("ag") * a.Num("GammaI"), q = a.Num("q"), beta = a.Num("Beta");
                marks.AddRange([tb, tc, td]);
                sa = t => Ec8.Sd(t, ag, s, tb, tc, td, q, beta);
                break;
            }
            case "is1893":
            {
                var soil = a.Text("Soil");
                marks.AddRange([0.1, 0.4, 0.55, 0.67, 4]);
                sa = t => Is.Sa(t, soil);
                break;
            }
            case "ubc97":
            {
                double z = a.Num("Z");
                var soil = a.Text("SoilType");
                var ca = a.Opt("Ca") ?? Ubc.Ca(soil, z) * a.Num("Na");
                var cv = a.Opt("Cv") ?? Ubc.Cv(soil, z) * a.Num("Nv");
                double ts = cv / (2.5 * ca), t0 = 0.2 * ts;
                marks.AddRange([t0, ts]);
                sa = t => t < t0 ? ca + (2.5 * ca - ca) * t / t0 : t <= ts ? 2.5 * ca : cv / t;
                break;
            }
            case "nbcc":
            {
                double[] at = [0.2, 0.5, 1.0, 2.0, 5.0, 10.0];
                var v = new[] { a.Num("Sa02"), a.Num("Sa05"), a.Num("Sa10"), a.Num("Sa20"), a.Num("Sa50"), a.Num("Sa100") };
                marks.AddRange(at);
                sa = t => Interp(at, v, t);
                break;
            }
            case "user-spectrum":
            {
                var pts = Numbers(a.Text("Spectrum"), "the spectrum points");
                if (pts.Length < 4 || pts.Length % 2 != 0) throw new ArgumentException("The spectrum points are periods and Sa in pairs: T1 Sa1 T2 Sa2 ...");
                return Enumerable.Range(0, pts.Length / 2).Select(i => (pts[2 * i], pts[2 * i + 1])).ToList();
            }
            default: throw new ArgumentException($"{code.Name} has no spectrum: choose a code, or give the points.");
        }
        var periods = new SortedSet<double> { 0, last };
        foreach (var t in marks) if (t > 0 && t < last) periods.Add(Math.Round(t, 4));
        for (var t = 0.05; t < last; t += t < 1 ? 0.05 : t < 4 ? 0.1 : 0.5) periods.Add(Math.Round(t, 4));
        return periods.Select(t => (t, Math.Round(sa(t), 6))).ToList();
    }

    // ---- code tables ----

    private static class Asce
    {
        private static readonly double[] SsAt = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5];
        private static readonly double[] S1At = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6];
        private static readonly Dictionary<string, double?[]> FaT = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [0.9, 0.9, 0.9, 0.9, 0.9, 0.9], ["C"] = [1.3, 1.3, 1.2, 1.2, 1.2, 1.2],
            ["D"] = [1.6, 1.4, 1.2, 1.1, 1.0, 1.0], ["E"] = [2.4, 1.7, 1.3, null, null, null],
        };
        private static readonly Dictionary<string, double?[]> FvT = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["C"] = [1.5, 1.5, 1.5, 1.5, 1.5, 1.4],
            ["D"] = [2.4, 2.2, 2.0, 1.9, 1.8, 1.7], ["E"] = [4.2, null, null, null, null, null],
        };
        public static double Fa(string site, double ss) => Look(FaT, SsAt, site, ss, "Fa");
        public static double Fv(string site, double s1) => Look(FvT, S1At, site, s1, "Fv");
        private static double Look(Dictionary<string, double?[]> table, double[] at, string site, double value, string what)
        {
            if (!table.TryGetValue(site, out var row)) throw new ArgumentException($"Site class {site} has no {what} in the table: give {what}.");
            var i = Array.FindIndex(at, x => value <= x);
            if (i < 0) return row[^1] ?? throw Needs(site, what);
            if (i == 0) return row[0] ?? throw Needs(site, what);
            if (row[i - 1] is not { } lo || row[i] is not { } hi) throw Needs(site, what);
            return lo + (hi - lo) * (value - at[i - 1]) / (at[i] - at[i - 1]);
        }
        private static ArgumentException Needs(string site, string what) => new($"The code gives no {what} for site class {site} at this value (a site-specific study): give {what}.");
        public static double Cu(double sd1) => sd1 >= 0.4 ? 1.4 : sd1 >= 0.3 ? 1.4 : sd1 >= 0.2 ? 1.5 - (sd1 - 0.2) : sd1 >= 0.15 ? 1.6 - (sd1 - 0.15) * 2 : sd1 >= 0.1 ? 1.7 - (sd1 - 0.1) * 2 : 1.7;
    }

    private static class Ubc
    {
        private static readonly double[] Zs = [0.075, 0.15, 0.2, 0.3, 0.4];
        private static readonly Dictionary<string, double[]> CaT = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SA"] = [0.06, 0.12, 0.16, 0.24, 0.32], ["SB"] = [0.08, 0.15, 0.20, 0.30, 0.40], ["SC"] = [0.09, 0.18, 0.24, 0.33, 0.40],
            ["SD"] = [0.12, 0.22, 0.28, 0.36, 0.44], ["SE"] = [0.19, 0.30, 0.34, 0.36, 0.36],
        };
        private static readonly Dictionary<string, double[]> CvT = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SA"] = [0.06, 0.12, 0.16, 0.24, 0.32], ["SB"] = [0.08, 0.15, 0.20, 0.30, 0.40], ["SC"] = [0.13, 0.25, 0.32, 0.45, 0.56],
            ["SD"] = [0.18, 0.32, 0.40, 0.54, 0.64], ["SE"] = [0.26, 0.50, 0.64, 0.84, 0.96],
        };
        public static double Ca(string soil, double z) => Interp(Zs, Row(CaT, soil), z);
        public static double Cv(string soil, double z) => Interp(Zs, Row(CvT, soil), z);
        private static double[] Row(Dictionary<string, double[]> t, string soil) => t.TryGetValue(soil, out var r) ? r : throw new ArgumentException($"Soil profile {soil}: give Ca and Cv.");
    }

    private static class Ec8
    {
        public static (double S, double TB, double TC, double TD) Table(string ground, bool type2) => (ground.ToUpperInvariant(), type2) switch
        {
            ("A", false) => (1.0, 0.15, 0.4, 2.0), ("B", false) => (1.2, 0.15, 0.5, 2.0), ("C", false) => (1.15, 0.2, 0.6, 2.0),
            ("D", false) => (1.35, 0.2, 0.8, 2.0), ("E", false) => (1.4, 0.15, 0.5, 2.0),
            ("A", true) => (1.0, 0.05, 0.25, 1.2), ("B", true) => (1.35, 0.05, 0.25, 1.2), ("C", true) => (1.5, 0.1, 0.25, 1.2),
            ("D", true) => (1.8, 0.1, 0.3, 1.2), ("E", true) => (1.6, 0.05, 0.25, 1.2),
            _ => throw new ArgumentException($"Ground type {ground}: give S, TB, TC and TD."),
        };

        /// <summary>The design spectrum Sd(T) of EN 1998-1 3.2.2.5, in g.</summary>
        public static double Sd(double t, double ag, double s, double tb, double tc, double td, double q, double beta)
        {
            if (t <= tb) return ag * s * (2.0 / 3 + t / tb * (2.5 / q - 2.0 / 3));
            if (t <= tc) return ag * s * 2.5 / q;
            if (t <= td) return Math.Max(ag * s * 2.5 / q * tc / t, beta * ag);
            return Math.Max(ag * s * 2.5 / q * tc * td / (t * t), beta * ag);
        }
    }

    private static class Is
    {
        /// <summary>Sa/g of IS 1893 (Part 1) 2016, figure 2, for 5% damping.</summary>
        public static double Sa(double t, string soil)
        {
            var (flat, c) = soil.ToUpperInvariant() switch { "I" => (0.4, 1.0), "III" => (0.67, 1.67), _ => (0.55, 1.36) };
            if (t < 0.1) return 1 + 15 * t;
            if (t <= flat) return 2.5;
            if (t <= 4) return c / t;
            return c / 4;
        }

        private static readonly double[] Heights = [10, 15, 20, 30, 50, 100, 150, 200, 250, 300, 350, 400, 450, 500];
        private static readonly double[][] K2T =
        [
            [1.05, 1.09, 1.12, 1.15, 1.20, 1.26, 1.30, 1.32, 1.34, 1.35, 1.37, 1.38, 1.39, 1.40],
            [1.00, 1.05, 1.07, 1.12, 1.17, 1.24, 1.28, 1.30, 1.32, 1.34, 1.35, 1.36, 1.37, 1.38],
            [0.91, 0.97, 1.01, 1.06, 1.12, 1.20, 1.24, 1.27, 1.29, 1.31, 1.32, 1.34, 1.35, 1.36],
            [0.80, 0.80, 0.80, 0.97, 1.10, 1.20, 1.24, 1.27, 1.28, 1.30, 1.31, 1.32, 1.33, 1.34],
        ];

        /// <summary>k2 of IS 875 (Part 3) table 2: terrain category 1 to 4, height in metres.</summary>
        public static double K2(double z, int category) => Interp(Heights, K2T[Math.Clamp(category, 1, 4) - 1], z);
    }
}
