using System.Globalization;
using CsiProMax.E2k;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Model;

/// <summary>
/// Post-tensioning and slab design input as ETABS and SAFE keep it: tendon sections, tendons (plan layout, vertical
/// profile, strands, bonding), their jacking loads and losses, the hyperstatic load case, and design strips with their
/// strip design overwrites. Kept in the model's own e2k sections, word for word as ETABS 20 writes them (read from
/// ETABS's own export of its "ACI 318-19 PT-SL Ex001" example, tests/CsiProMax.Tests/samples/etabs-PT-SL-v20.e2k), so a
/// file from ETABS keeps everything it had and what the Modeler adds is what ETABS writes itself. The .f2k and .s2k
/// writers read the same objects through <see cref="Tendons"/> and <see cref="Strips"/>.
/// </summary>
public static class SlabDefs
{
    public const string TendonSections = "TENDON SECTIONS";
    public const string TendonConnect = "TENDON CONNECTIVITIES";
    public const string TendonLayout = "TENDON HORIZONTAL LAYOUT";
    public const string TendonProfile = "TENDON VERTICAL PROFILE";
    public const string StripGeometry = "STRIP GEOMETRY";
    public const string TendonAssigns = "TENDON ASSIGNS";
    public const string TendonLoads = "TENDON OBJECT LOADS";
    public const string SlabPreferences = "CONCRETE SLAB DESIGN PREFERENCES";
    public const string StripOverwrites = "CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED";
    public const string FemOverwrites = "CONCRETE SLAB DESIGN OVERWRITES - FEM BASED";
    public const string PunchingOverwrites = "CONCRETE SLAB DESIGN PUNCHING OVERWRITES - GENERAL";
    public const string PunchingPerimeter = "CONCRETE SLAB DESIGN PUNCHING OVERWRITES - USER PERIMETER";

    /// <summary>ETABS 20's own order of the sections around these (from its export), so a new section goes where ETABS puts it.</summary>
    public static readonly string[] FileOrder =
    [
        "PROGRAM INFORMATION", "CONTROLS", "STORIES - IN SEQUENCE FROM TOP", "GRIDS", "DIAPHRAGM NAMES", "MATERIAL PROPERTIES", "REBAR DEFINITIONS",
        "FRAME SECTIONS", "AUTO SELECT SECTION LISTS", "CONCRETE SECTIONS", TendonSections, "SLAB PROPERTIES", "DECK PROPERTIES", "WALL PROPERTIES",
        "LINK PROPERTIES", "PANEL ZONE PROPERTIES", "PIER/SPANDREL NAMES", "POINT COORDINATES", "LINE CONNECTIVITIES", "AREA CONNECTIVITIES",
        TendonConnect, TendonLayout, TendonSupports, TendonProfile, StripGeometry, "GROUPS", "POINT ASSIGNS", "LINE ASSIGNS", "AREA ASSIGNS", TendonAssigns,
        "LOAD PATTERNS", "POINT OBJECT LOADS", "FRAME OBJECT LOADS", "SHELL OBJECT LOADS", TendonLoads, "ANALYSIS OPTIONS", "MASS SOURCE",
        "FUNCTIONS", "GENERALIZED DISPLACEMENTS", "LOAD CASES", "LOAD COMBINATIONS", "GENERAL DESIGN PREFERENCES", "STEEL DESIGN PREFERENCES",
        "CONCRETE DESIGN PREFERENCES", "COMPOSITE DESIGN PREFERENCES", "COMPOSITE COLUMN DESIGN PREFERENCES", "WALL DESIGN PREFERENCES",
        SlabPreferences, StripOverwrites, FemOverwrites, PunchingOverwrites, PunchingPerimeter,
    ];

    /// <summary>Where the jack is, as the tools name it, and as ETABS's .e2k and SAFE's .f2k write it. "End" and "J End" are
    /// from ETABS 20 and SAFE 20, "Both" from ETABS 23.2; the rest follow the same pattern and are checked by the field test.</summary>
    public static readonly (string Key, string E2k, string F2k)[] JackEnds = [("I", "Start", "I End"), ("J", "End", "J End"), ("Both", "Both", "Both Ends")];

    public static readonly string[] StripDesignTypes = ["Column", "Middle", "No Design"];

    // ---- the model's own sections ----

    private static RawSection? Find(StructuralModel m, string heading) => m.Passthrough.Find(s => s.Heading == heading);

    /// <summary>The section, made where ETABS puts it when the model has none yet.</summary>
    public static RawSection Section(StructuralModel m, string heading)
    {
        var s = Find(m, heading);
        if (s is not null) return s;
        var at = Array.IndexOf(FileOrder, heading);
        var after = "";
        for (var i = at - 1; i >= 0; i--)
        {
            var h = FileOrder[i];
            if (Find(m, h) is not null || E2kSections.IsKnown(h)) { after = h; break; }
        }
        s = new RawSection { Heading = heading, After = after };
        m.Passthrough.Add(s);
        return s;
    }

    private static IEnumerable<List<string>> Rows(StructuralModel m, string heading, string word) =>
        (Find(m, heading)?.Lines ?? []).Select(Tokenize).Where(t => t.Count > 1 && t[0] == word);

    private static void RemoveRows(StructuralModel m, string heading, Func<List<string>, bool> which)
    {
        var s = Find(m, heading);
        if (s is null) return;
        s.Lines.RemoveAll(l => Tokenize(l) is { Count: > 1 } t && which(t));
        if (s.Lines.Count == 0) m.Passthrough.Remove(s);
    }

    private static bool Is(List<string> t, string name, string? story) =>
        Unquote(t[1]) == name && (story is null || t.Count > 2 && string.Equals(Unquote(t[2]), story, StringComparison.OrdinalIgnoreCase));

    private static double D(IReadOnlyList<string> t, string key, double fallback = 0) => TryNumber(Value(t, key), out var d) ? d : fallback;

    private static string N(double v) => Number(Math.Round(v, 9));

    // ---- tendon sections ----

    public sealed record TendonSection(string Name, string Material, double StrandArea);

    public static List<TendonSection> TendonSectionList(StructuralModel m) =>
        Rows(m, TendonSections, "TENDONSECTION").Select(t => new TendonSection(Unquote(t[1]), Value(t, "MATERIAL") ?? "", D(t, "STRANDAREA"))).ToList();

    public static TendonSection SetTendonSection(StructuralModel m, string name, string material, double strandArea)
    {
        Named(name, "tendon section");
        var mat = m.Materials.Find(x => x.Name == material) ?? throw new ArgumentException($"There is no material '{material}'. Define a Tendon material first (Define > Material Properties).");
        if (!mat.Type.Equals("Tendon", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"'{material}' is a {mat.Type} material; a tendon section takes a Tendon material.");
        if (!(strandArea > 0) || !double.IsFinite(strandArea)) throw new ArgumentException("The strand area is more than 0.");
        RemoveRows(m, TendonSections, t => t[0] == "TENDONSECTION" && Unquote(t[1]) == name);
        Section(m, TendonSections).Lines.Add(Line(["TENDONSECTION", Quote(name), "MATERIAL", Quote(material), "STRANDAREA", N(strandArea)]));
        return new TendonSection(name, material, strandArea);
    }

    public static void DeleteTendonSection(StructuralModel m, string name)
    {
        if (!TendonSectionList(m).Exists(s => s.Name == name)) throw new ArgumentException($"There is no tendon section '{name}'.");
        var user = Tendons(m).FirstOrDefault(t => t.Section == name);
        if (user is not null) throw new ArgumentException($"The tendon section '{name}' is used by tendon {user.Name}; change that first.");
        RemoveRows(m, TendonSections, t => t[0] == "TENDONSECTION" && Unquote(t[1]) == name);
    }

    // ---- tendons ----

    /// <summary>One span of a tendon's vertical profile (a reverse parabola, ETABS's span type; with A = B = 0 it is a
    /// simple parabola): its length, the inflection points A and B and the low point C as fractions of the length, and
    /// the height of the tendon's centroid at the left end, the low point and the right end (ZL, ZC, ZR, elevations).</summary>
    public sealed record TendonSpan(double Length, double A, double B, double C, double ZL, double ZC, double ZR, string Label = "", string Type = "Reverse Parabola");

    public sealed record TendonJack(string TransferPattern, string FinalPattern, double Stress, string End);

    /// <summary>The losses (Assign > Tendon Loads > Tendon Losses): "Fixed" (stresses lost at stressing and in the long term),
    /// "Percent" (percentages of the jacking stress), or "Detailed": friction (curvature and wobble coefficients), anchorage
    /// set slip, and the long-term stresses lost to elastic shortening, creep, shrinkage and steel relaxation. As ETABS 23.2
    /// writes them (its export of PT-SL Ex001 with both, 2026-11-26).</summary>
    public sealed record TendonLoss(string Method, double Stressing, double LongTerm, double Curvature = 0, double Wobble = 0, double AnchorSlip = 0,
        double ElasticShortening = 0, double Creep = 0, double Shrinkage = 0, double Relaxation = 0);

    public static readonly string[] LossMethods = ["Fixed", "Percent", "Detailed"];

    private static TendonLoss ReadLoss(IReadOnlyList<string> l) => (Value(l, "LOSSTYPE") ?? "Fixed").ToUpperInvariant() switch
    {
        "PERCENT" => new TendonLoss("Percent", D(l, "LOSSPERCENTST"), D(l, "LOSSPERCENTLT")),
        "DETAILED" => new TendonLoss("Detailed", 0, 0, D(l, "CURVCOEFF"), D(l, "WOBBLECOEFF"), D(l, "ANCHORSLIP"), D(l, "ELASTICSHORT"), D(l, "CREEPSTRESS"), D(l, "SHRINKSTRESS"), D(l, "STEELRELAX")),
        _ => new TendonLoss("Fixed", D(l, "LOSSFIXEDST"), D(l, "LOSSFIXEDLT")),
    };

    private static List<string> LossLine(string name, string story, TendonLoss loss)
    {
        List<string> t = ["TENDONLOAD", Quote(name), Quote(story), "TYPE", Quote("Losses"), "LOSSTYPE", Quote(loss.Method)];
        if (loss.Method == "Percent") t.AddRange(["LOSSPERCENTST", N(loss.Stressing), "LOSSPERCENTLT", N(loss.LongTerm)]);
        else if (loss.Method == "Detailed")
            t.AddRange(["CURVCOEFF", N(loss.Curvature), "WOBBLECOEFF", N(loss.Wobble), "ANCHORSLIP", N(loss.AnchorSlip), "ELASTICSHORT", N(loss.ElasticShortening),
                "CREEPSTRESS", N(loss.Creep), "SHRINKSTRESS", N(loss.Shrinkage), "STEELRELAX", N(loss.Relaxation)]);
        else t.AddRange(["LOSSFIXEDST", N(loss.Stressing), "LOSSFIXEDLT", N(loss.LongTerm)]);
        return t;
    }

    /// <summary>A short description of the losses, for answers.</summary>
    public static string Describe(TendonLoss l) => l.Method switch
    {
        "Percent" => $"{l.Stressing}% at stressing, {l.LongTerm}% long term",
        "Detailed" => $"friction {l.Curvature}/{l.Wobble}, anchor set {l.AnchorSlip}; long term: shortening {l.ElasticShortening}, creep {l.Creep}, shrinkage {l.Shrinkage}, relaxation {l.Relaxation}",
        _ => $"{l.Stressing} at stressing, {l.LongTerm} long term",
    };

    public sealed record Tendon(string Name, string Story, List<string> Points, List<double[]> Plan, double Datum,
        List<TendonSpan> Spans, string Section, int Strands, bool Bonded, TendonJack? Jack, TendonLoss? Loss)
    {
        /// <summary>The plan length of the tendon.</summary>
        public double Length => Enumerable.Range(1, Math.Max(0, Plan.Count - 1)).Sum(i => Math.Sqrt(Math.Pow(Plan[i][0] - Plan[i - 1][0], 2) + Math.Pow(Plan[i][1] - Plan[i - 1][1], 2)));

        /// <summary>The tendon's height at a distance along its plan.</summary>
        public double ZAt(double s)
        {
            var start = 0.0;
            foreach (var sp in Spans)
            {
                if (s <= start + sp.Length + 1e-9 || ReferenceEquals(sp, Spans[^1])) return SpanZ(sp, Math.Clamp(s - start, 0, sp.Length));
                start += sp.Length;
            }
            return Datum;
        }

        /// <summary>Points along the tendon in 3D, for drawing: (x, y, z) every so often.</summary>
        public List<double[]> Shape(int perSpan = 16)
        {
            var list = new List<double[]>();
            var total = Length;
            if (total <= 0) return list;
            var n = Math.Max(2, perSpan * Math.Max(1, Spans.Count));
            for (var k = 0; k <= n; k++)
            {
                var s = total * k / n;
                var (x, y) = PlanAt(s);
                list.Add([x, y, ZAt(s)]);
            }
            return list;
        }

        public (double X, double Y) PlanAt(double s)
        {
            for (var i = 1; i < Plan.Count; i++)
            {
                var len = Math.Sqrt(Math.Pow(Plan[i][0] - Plan[i - 1][0], 2) + Math.Pow(Plan[i][1] - Plan[i - 1][1], 2));
                if (s <= len || i == Plan.Count - 1)
                {
                    var f = len <= 0 ? 0 : Math.Clamp(s / len, 0, 1);
                    return (Plan[i - 1][0] + f * (Plan[i][0] - Plan[i - 1][0]), Plan[i - 1][1] + f * (Plan[i][1] - Plan[i - 1][1]));
                }
                s -= len;
            }
            return (Plan[0][0], Plan[0][1]);
        }
    }

    /// <summary>The height of a reverse parabola span at x from its left end: a parabola from the left end down to the
    /// low point C, reversed (curving the other way) over the first A of the span, and the same on the right with B.</summary>
    public static double SpanZ(TendonSpan sp, double x)
    {
        var L = sp.Length;
        if (L <= 0) return sp.ZL;
        var xc = sp.C * L;
        if (x <= xc)
        {
            var a = Math.Clamp(sp.A, 0, sp.C) * L;
            return Half(x, xc, sp.ZL, sp.ZC, a);
        }
        var b = Math.Clamp(sp.B, 0, 1 - sp.C) * L;
        return Half(L - x, L - xc, sp.ZR, sp.ZC, b);
    }

    /// <summary>From the high end (u = 0, height zh) to the low point (u = w, height zl), with a reversed parabola over the
    /// first r: two parabolas meeting with the same slope at u = r.</summary>
    private static double Half(double u, double w, double zh, double zl, double r)
    {
        if (w <= 0) return zl;
        var drop = zh - zl;
        if (r <= 1e-12) { var f = (w - u) / w; return zl + drop * f * f; }
        // Main parabola z = zl + k (w - u)^2 from r to w; the reversed one z = zh - j u^2 from 0 to r; same height and slope at r.
        var k = drop / (w * (w - r));
        var j = k * (w - r) / r;
        return u <= r ? zh - j * u * u : zl + k * (w - u) * (w - u);
    }

    public const string TendonSupports = "TENDON SUPPORT POINTS";

    /// <summary>
    /// Whether ETABS of this version writes a tendon's support points and its profile heights from the tendon's datum.
    /// ETABS 23.2 does ("$ TENDON SUPPORT POINTS", ZL -5 ZC -9 ZR -5 in its export of PT-SL Ex001); ETABS 20 does neither
    /// (ZL 43 ZC 39 ZR 43, the heights themselves). ETABS 23 drops the WHOLE .e2k, with no message, when a tendon has no
    /// support points (bisected on Ahmed's PC, main 3cdc64d run, 2026-11-26: 4 stories, 0 frames, 0 areas).
    /// </summary>
    public static bool WritesSupportPoints(string? version) =>
        int.TryParse((version ?? "").Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) && major >= 23;

    /// <summary>
    /// The kept sections as ETABS of the model's version reads them: from ETABS 23 on, the tendons' support points
    /// (one at each end of each span, with its distance along the tendon as a fraction and its height) and the profile
    /// heights from the datum. The model keeps ETABS 20's form (heights themselves, no support points).
    /// </summary>
    public static List<RawSection> ForEtabs(StructuralModel m, List<RawSection> sections)
    {
        if (!WritesSupportPoints(m.ProgramVersion)) return sections.Where(r => r.Heading != TendonSupports).ToList();
        var tendons = Tendons(m);
        if (tendons.Count == 0) return sections.Where(r => r.Heading != TendonSupports).ToList();
        var datum = tendons.ToDictionary(t => (t.Name, t.Story.ToUpperInvariant()), t => t.Datum);
        var list = new List<RawSection>();
        foreach (var r in sections)
        {
            if (r.Heading == TendonSupports) continue;
            if (r.Heading != TendonProfile) { list.Add(r); continue; }
            var lines = r.Lines.Select(l =>
            {
                var t = Tokenize(l);
                if (t.Count < 3 || t[0] != "TENDONVP" || !datum.TryGetValue((Unquote(t[1]), Unquote(t[2]).ToUpperInvariant()), out var d)) return l;
                for (var i = 3; i + 1 < t.Count; i++)
                    if (t[i] is "ZL" or "ZC" or "ZR" && TryNumber(t[i + 1], out var z)) t[i + 1] = N(z - d);
                return Line(t);
            }).ToList();
            list.Add(new RawSection { Heading = r.Heading, After = TendonSupports, Lines = lines });
        }
        var sp = new List<string>();
        foreach (var t in tendons)
        {
            if (t.Spans.Count == 0 || t.Length <= 0) continue;
            var q = Quote(t.Name); var st = Quote(t.Story);
            sp.Add(Line(["TENDONSP", q, st, "NUMSUPPORTS", (t.Spans.Count + 1).ToString(CultureInfo.InvariantCulture)]));
            var at = 0.0;
            for (var k = 0; k <= t.Spans.Count; k++)
            {
                if (k > 0) at += t.Spans[k - 1].Length;
                var s = Math.Min(at, t.Length);
                var (x, y) = t.PlanAt(s);
                var z = k == 0 ? t.Spans[0].ZL : t.Spans[k - 1].ZR;
                sp.Add(Line(["TENDONSP", q, st, "SUPPNUM", (k + 1).ToString(CultureInfo.InvariantCulture), "SUPPORTTYPE", Quote("None"), "RD", N(s / t.Length),
                    "GLOBALX", N(x), "GLOBALY", N(y), "GLOBALZ", N(z)]));
            }
        }
        if (sp.Count > 0) list.Add(new RawSection { Heading = TendonSupports, After = TendonLayout, Lines = sp });
        return list;
    }

    /// <summary>
    /// A file in ETABS 23's form (with support points) back to the model's form: profile heights from the datum become
    /// heights, and the support points are dropped (they are written again from the spans).
    /// </summary>
    public static void FromEtabs(StructuralModel m)
    {
        var sp = Find(m, TendonSupports);
        if (sp is null) return;
        m.Passthrough.Remove(sp);
        var vp = Find(m, TendonProfile);
        if (vp is null) return;
        var datum = new Dictionary<(string, string), double>();
        foreach (var h in Rows(m, TendonLayout, "TENDONHL").Where(h => h.Count > 2 && Value(h, "GLOBALZ") is not null))
            datum.TryAdd((Unquote(h[1]), Unquote(h[2]).ToUpperInvariant()), D(h, "GLOBALZ"));
        vp.Lines = vp.Lines.Select(l =>
        {
            var t = Tokenize(l);
            if (t.Count < 3 || t[0] != "TENDONVP" || !datum.TryGetValue((Unquote(t[1]), Unquote(t[2]).ToUpperInvariant()), out var d)) return l;
            for (var i = 3; i + 1 < t.Count; i++)
                if (t[i] is "ZL" or "ZC" or "ZR" && TryNumber(t[i + 1], out var z)) t[i + 1] = N(z + d);
            return Line(t);
        }).ToList();
    }

    public static List<Tendon> Tendons(StructuralModel m)
    {
        var list = new List<Tendon>();
        foreach (var t in Rows(m, TendonConnect, "TENDON"))
        {
            if (t.Count < 3) continue;
            var name = Unquote(t[1]); var story = Unquote(t[2]);
            var n = (int)D(t, "NUMPTS");
            var at = t.IndexOf("NUMPTS");
            var points = at < 0 ? [] : t.Skip(at + 2).Take(n).Select(Unquote).ToList();
            var plan = new List<double[]>(); var datum = 0.0; var gotZ = false;
            foreach (var h in Rows(m, TendonLayout, "TENDONHL").Where(h => Is(h, name, story) && Value(h, "GLOBALX") is not null))
            {
                double[] p = [D(h, "GLOBALX"), D(h, "GLOBALY")];
                if (!gotZ) { datum = D(h, "GLOBALZ"); gotZ = true; }
                if (plan.Count == 0 || Math.Abs(plan[^1][0] - p[0]) + Math.Abs(plan[^1][1] - p[1]) > 1e-9) plan.Add(p);
            }
            if (plan.Count == 0)
                foreach (var pn in points) if (m.Point(pn) is { } pp) plan.Add([pp.X, pp.Y]);
            if (!gotZ && m.StoryIndex(story) >= 0) datum = m.Elevation(m.StoryIndex(story));
            var spans = Rows(m, TendonProfile, "TENDONVP").Where(v => Is(v, name, story))
                .OrderBy(v => D(v, "SPANNUM"))
                .Select(v => new TendonSpan(D(v, "L"), D(v, "A"), D(v, "B"), D(v, "C", 0.5), D(v, "ZL", datum), D(v, "ZC", datum), D(v, "ZR", datum), Value(v, "SPANLABEL") ?? "", Value(v, "SPANTYPE") ?? "Reverse Parabola"))
                .ToList();
            var asg = Rows(m, TendonAssigns, "TENDONASSIGN").FirstOrDefault(a => Is(a, name, story));
            TendonJack? jack = null; TendonLoss? loss = null;
            foreach (var l in Rows(m, TendonLoads, "TENDONLOAD").Where(l => Is(l, name, story)))
            {
                if (string.Equals(Value(l, "TYPE"), "Loads", StringComparison.OrdinalIgnoreCase))
                {
                    var end = JackEnds.FirstOrDefault(j => j.E2k.Equals(Value(l, "JACKLOC") ?? "", StringComparison.OrdinalIgnoreCase)).Key ?? "J";
                    jack = new TendonJack(Value(l, "TRANSFERLOADPAT") ?? "", Value(l, "FINALLOADPAT") ?? "", D(l, "JACKSTRESS"), end);
                }
                else if (string.Equals(Value(l, "TYPE"), "Losses", StringComparison.OrdinalIgnoreCase))
                    loss = ReadLoss(l);
            }
            list.Add(new Tendon(name, story, points, plan, datum, spans, asg is null ? "" : Value(asg, "PROPERTY") ?? "",
                asg is null ? 1 : (int)D(asg, "NUMSTRANDS", 1), asg is not null && string.Equals(Value(asg, "BONDED"), "YES", StringComparison.OrdinalIgnoreCase), jack, loss));
        }
        return list;
    }

    /// <summary>A span as the tools give it: its length (0: the rest of the tendon shared out), the depth of the tendon's
    /// centroid below the slab top at its left end, low point and right end, and A, B, C as fractions of its length.</summary>
    public sealed record SpanInput(double Length, double DepthLeft, double DepthLow, double DepthRight, double A = 0.1, double B = 0.1, double C = 0.5);

    /// <summary>Adds or replaces a tendon on a story: through plan points (x, y) at the slab top of the story, its profile
    /// span by span, its section and strands, and its jacking and losses.</summary>
    public static Tendon SetTendon(StructuralModel m, string name, string story, IReadOnlyList<double[]> plan, IReadOnlyList<SpanInput> spans,
        string section, int strands, bool bonded, TendonJack? jack, TendonLoss? loss)
    {
        Named(name, "tendon");
        var si = m.StoryIndex(story);
        if (si <= 0) throw new ArgumentException(si == 0 ? "A tendon goes in a slab on a story above the base." : $"There is no story '{story}'.");
        story = m.Stories[si].Name;
        if (plan.Count < 2) throw new ArgumentException("A tendon runs through at least two plan points [x, y].");
        if (plan.Any(p => p.Length != 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) throw new ArgumentException("Each tendon point is [x, y].");
        for (var i = 1; i < plan.Count; i++)
            if (Math.Abs(plan[i][0] - plan[i - 1][0]) + Math.Abs(plan[i][1] - plan[i - 1][1]) < 1e-9) throw new ArgumentException("Two tendon points in a row are the same point.");
        if (!TendonSectionList(m).Exists(s => s.Name == section)) throw new ArgumentException($"There is no tendon section '{section}'. Define it first (Define > Tendon Sections).");
        if (strands < 1) throw new ArgumentException("A tendon has 1 strand at least.");
        if (spans.Count == 0) throw new ArgumentException("Give the tendon's profile: one span at least, with the depths of the tendon below the slab top.");
        var datum = m.Elevation(si);
        var draft = new Tendon(name, story, [], plan.Select(p => new[] { p[0], p[1] }).ToList(), datum, [], section, strands, bonded, jack, loss);
        var total = draft.Length;
        var given = spans.Sum(s => Math.Max(0, s.Length));
        var open = spans.Count(s => s.Length <= 0);
        if (given > total * (1 + 1e-6)) throw new ArgumentException($"The spans add up to {N(given)}, more than the tendon's length {N(total)}.");
        if (open == 0 && Math.Abs(given - total) > Math.Max(1e-6, total * 1e-4)) throw new ArgumentException($"The spans add up to {N(given)}; the tendon is {N(total)} long. Give the lengths so they add up, or 0 for a span to take the rest.");
        var share = open == 0 ? 0 : (total - given) / open;
        if (open > 0 && share <= 0) throw new ArgumentException("The spans with a given length already take the whole tendon.");
        var thick = SlabThicknessAt(m, story, plan) ?? 0;
        var made = new List<TendonSpan>();
        for (var i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            foreach (var (v, what) in new[] { (s.DepthLeft, "left"), (s.DepthLow, "low point"), (s.DepthRight, "right") })
            {
                if (!double.IsFinite(v) || v < 0) throw new ArgumentException($"Span {i + 1}: the depth at the {what} is the distance below the slab top, 0 or more.");
                if (thick > 0 && v > thick) throw new ArgumentException($"Span {i + 1}: the depth at the {what} ({N(v)}) is below the slab, which is {N(thick)} thick.");
            }
            if (!(s.C > 0 && s.C < 1)) throw new ArgumentException($"Span {i + 1}: C (where the low point is, as a fraction of the span) is between 0 and 1.");
            if (s.A < 0 || s.A > s.C || s.B < 0 || s.B > 1 - s.C) throw new ArgumentException($"Span {i + 1}: A is from 0 to C, and B from 0 to 1 - C.");
            if (i > 0 && Math.Abs(spans[i - 1].DepthRight - s.DepthLeft) > 1e-9) throw new ArgumentException($"Span {i + 1} starts at depth {N(s.DepthLeft)} but span {i} ends at {N(spans[i - 1].DepthRight)}: a tendon is continuous.");
            var len = s.Length > 0 ? s.Length : share;
            var label = spans.Count == 1 ? "Span 1" : $"Span {i + 1}";
            made.Add(new TendonSpan(len, s.A, s.B, s.C, datum - s.DepthLeft, datum - s.DepthLow, datum - s.DepthRight, label));
        }
        if (jack is not null)
        {
            foreach (var (p, what) in new[] { (jack.TransferPattern, "transfer"), (jack.FinalPattern, "final") })
            {
                var pat = m.LoadPatterns.Find(x => x.Name == p) ?? throw new ArgumentException($"There is no load pattern '{p}' for the {what} prestress. Add it (type Prestress-{(what == "final" ? "Final" : "Transfer")}).");
                var want = what == "final" ? "Prestress-Final" : "Prestress-Transfer";
                if (!pat.Type.Equals(want, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"The {what} prestress pattern is of type {want}; '{p}' is {pat.Type}.");
            }
            if (!(jack.Stress > 0) || !double.IsFinite(jack.Stress)) throw new ArgumentException("The jacking stress is more than 0.");
            var end = JackEnds.FirstOrDefault(j => j.Key.Equals(jack.End, StringComparison.OrdinalIgnoreCase)).Key ?? throw new ArgumentException("The jack is at the I end, the J end or both: I, J or Both.");
            jack = jack with { End = end };
        }
        if (loss is not null)
        {
            // ETABS writes a tendon's losses with its jacking load (TYPE "Loads" then TYPE "Losses"); losses with no jacking load nothing.
            if (jack is null) throw new ArgumentException("Losses go with the jacking: give the jacking (patterns and stress) too.");
            var method = LossMethods.FirstOrDefault(x => x.Equals(loss.Method, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("The losses are 'Fixed' (stresses), 'Percent' (of the jacking stress) or 'Detailed' (friction, anchor set and long-term stresses).");
            loss = loss with { Method = method };
            double[] all = [loss.Stressing, loss.LongTerm, loss.Curvature, loss.Wobble, loss.AnchorSlip, loss.ElasticShortening, loss.Creep, loss.Shrinkage, loss.Relaxation];
            if (all.Any(v => v < 0 || !double.IsFinite(v))) throw new ArgumentException("The losses are 0 or more.");
            if (method == "Percent" && loss.Stressing + loss.LongTerm >= 100) throw new ArgumentException("The losses add up to 100% or more of the jacking stress.");
            if (method == "Detailed" && jack is not null && loss.ElasticShortening + loss.Creep + loss.Shrinkage + loss.Relaxation >= jack.Stress)
                throw new ArgumentException("The long-term losses add up to the jacking stress or more.");
            if (method == "Fixed" && jack is not null && loss.Stressing + loss.LongTerm >= jack.Stress) throw new ArgumentException("The losses add up to the jacking stress or more.");
        }

        DeleteTendonRows(m, name, story);
        var points = draft.Plan.Select(p => m.PointAt(p[0], p[1]).Name).ToList();
        Section(m, TendonConnect).Lines.Add(Line(["TENDON", Quote(name), Quote(story), "NUMPTS", points.Count.ToString(CultureInfo.InvariantCulture)], points.Select(Quote), ["LAYER", Quote("OTHER")]));
        var hl = Section(m, TendonLayout).Lines;
        hl.Add(Line(["TENDONHL", Quote(name), Quote(story), "NUMSEGS", (points.Count - 1).ToString(CultureInfo.InvariantCulture)]));
        for (var i = 1; i < draft.Plan.Count; i++)
        {
            hl.Add(Line(["TENDONHL", Quote(name), Quote(story), "SEGMENTNUM", i.ToString(CultureInfo.InvariantCulture), "CURVETYPE", Quote("Straight"), "NUMCURVEPTS", "2"]));
            for (var k = 0; k < 2; k++)
            {
                var p = draft.Plan[i - 1 + k];
                hl.Add(Line(["TENDONHL", Quote(name), Quote(story), "SEGMENTNUM", i.ToString(CultureInfo.InvariantCulture), "POINT", (k + 1).ToString(CultureInfo.InvariantCulture), "GLOBALX", N(p[0]), "GLOBALY", N(p[1]), "GLOBALZ", N(datum)]));
            }
        }
        var vp = Section(m, TendonProfile).Lines;
        for (var i = 0; i < made.Count; i++)
        {
            var s = made[i];
            vp.Add(Line(["TENDONVP", Quote(name), Quote(story), "SPANNUM", (i + 1).ToString(CultureInfo.InvariantCulture), "SPANLABEL", Quote(s.Label), "SPANTYPE", Quote(s.Type),
                "L", N(s.Length), "A", N(s.A), "B", N(s.B), "C", N(s.C), "ZL", N(s.ZL), "ZC", N(s.ZC), "ZR", N(s.ZR)]));
        }
        Section(m, TendonAssigns).Lines.Add(Line(["TENDONASSIGN", Quote(name), Quote(story), "PROPERTY", Quote(section), "NUMSTRANDS", strands.ToString(CultureInfo.InvariantCulture), "BONDED", Quote(bonded ? "YES" : "NO")]));
        if (jack is not null)
            Section(m, TendonLoads).Lines.Add(Line(["TENDONLOAD", Quote(name), Quote(story), "TYPE", Quote("Loads"), "TRANSFERLOADPAT", Quote(jack.TransferPattern), "FINALLOADPAT", Quote(jack.FinalPattern),
                "JACKLOC", Quote(JackEnds.First(j => j.Key == jack.End).E2k), "JACKSTRESS", N(jack.Stress)]));
        if (loss is not null)
            Section(m, TendonLoads).Lines.Add(Line(LossLine(name, story, loss)));
        return Tendons(m).First(t => t.Name == name && t.Story == story);
    }

    private static void DeleteTendonRows(StructuralModel m, string name, string story)
    {
        RemoveRows(m, TendonConnect, t => t[0] == "TENDON" && Is(t, name, story));
        RemoveRows(m, TendonLayout, t => t[0] == "TENDONHL" && Is(t, name, story));
        RemoveRows(m, TendonProfile, t => t[0] == "TENDONVP" && Is(t, name, story));
        RemoveRows(m, TendonSupports, t => t[0] == "TENDONSP" && Is(t, name, story));
        RemoveRows(m, TendonAssigns, t => t[0] == "TENDONASSIGN" && Is(t, name, story));
        RemoveRows(m, TendonLoads, t => t[0] == "TENDONLOAD" && Is(t, name, story));
    }

    public static void DeleteTendon(StructuralModel m, string name, string? story = null)
    {
        var hit = Tendons(m).Where(t => t.Name == name && (story is null || string.Equals(t.Story, story, StringComparison.OrdinalIgnoreCase))).ToList();
        if (hit.Count == 0) throw new ArgumentException($"There is no tendon '{name}'{(story is null ? "" : " on " + story)}.");
        foreach (var t in hit) DeleteTendonRows(m, t.Name, t.Story);
    }

    /// <summary>The thickness of the thickest slab on the story under the middle of a plan line, or null when none is there.</summary>
    public static double? SlabThicknessAt(StructuralModel m, string story, IReadOnlyList<double[]> plan)
    {
        double? best = null;
        var probes = new List<(double, double)>();
        for (var i = 0; i < plan.Count; i++)
        {
            probes.Add((plan[i][0], plan[i][1]));
            if (i > 0) probes.Add(((plan[i][0] + plan[i - 1][0]) / 2, (plan[i][1] + plan[i - 1][1]) / 2));
        }
        foreach (var aa in m.AreaAssigns.Where(a => string.Equals(a.Story, story, StringComparison.OrdinalIgnoreCase)))
        {
            var area = m.Area(aa.Label);
            if (area is null || area.Kind != AreaKind.Floor) continue;
            var poly = area.PointNames.Select(m.Point).Where(p => p is not null).Select(p => (p!.X, p.Y)).ToList();
            if (poly.Count < 3 || !probes.Any(q => Inside(poly, q.Item1, q.Item2))) continue;
            var t = m.ShellSections.Find(s => s.Name == aa.Section)?.Thickness;
            if (t is > 0 && (best is null || t > best)) best = t;
        }
        return best;
    }

    private static bool Inside(List<(double X, double Y)> poly, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            // On an edge counts as inside: a tendon along a slab edge is in the slab.
            var (xi, yi) = poly[i]; var (xj, yj) = poly[j];
            var cross = (xj - xi) * (y - yi) - (yj - yi) * (x - xi);
            if (Math.Abs(cross) < 1e-9 && x >= Math.Min(xi, xj) - 1e-9 && x <= Math.Max(xi, xj) + 1e-9 && y >= Math.Min(yi, yj) - 1e-9 && y <= Math.Max(yi, yj) + 1e-9) return true;
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    // ---- the hyperstatic load case ----

    /// <summary>A hyperstatic load case (Define > Load Cases > Hyperstatic) of a linear static prestress case: the secondary
    /// forces of post-tensioning that slab design uses. Written as ETABS 20 writes it.</summary>
    public static LoadCase SetHyperstatic(StructuralModel m, string name, string baseCase, double factor = 1)
    {
        Named(name, "load case");
        var b = m.LoadCases.Find(c => c.Name == baseCase) ?? throw new ArgumentException($"There is no load case '{baseCase}'.");
        if (AnalysisDefs.CaseType(b) != "Linear Static") throw new ArgumentException($"A hyperstatic case is made from a linear static case; '{baseCase}' is {AnalysisDefs.CaseType(b)}.");
        if (!b.Lines.Any(l => l.Count >= 2 && l[0] == "LOADPAT" && m.LoadPatterns.Find(p => p.Name == Unquote(l[1]))?.Type.StartsWith("Prestress", StringComparison.OrdinalIgnoreCase) == true))
            throw new ArgumentException($"'{baseCase}' has no prestress load pattern in it; a hyperstatic case is of a prestress case.");
        if (!(factor > 0) || !double.IsFinite(factor)) throw new ArgumentException("The scale factor is more than 0.");
        var existing = m.LoadCases.Find(c => c.Name == name);
        if (existing is not null && AnalysisDefs.CaseType(existing) != "Hyperstatic") throw new ArgumentException($"There is already a {AnalysisDefs.CaseType(existing)} load case '{name}'.");
        if (m.Combos.Exists(c => c.Name == name) || m.LoadPatterns.Exists(p => p.Name == name)) throw new ArgumentException($"The name '{name}' is taken.");
        var c = existing ?? new LoadCase { Name = name };
        c.Lines = [["TYPE", Quote("Hyperstatic"), "HYPERSTATICCASE", Quote(baseCase), "HYPERSTATICSUPPORT", Quote("Springs"), "HYPERSTATICSF", N(factor)]];
        if (existing is null) m.LoadCases.Add(c);
        return c;
    }

    // ---- design strips ----

    public sealed record Strip(string Name, string Story, string Layer, List<double[]> Plan, double WidthLeft, double WidthRight,
        string DesignType = "Column", bool Design = true, bool IgnorePT = false, string RebarMaterial = "", double Llrf = 1);

    /// <summary>The model's design strips: those in its e2k sections and, from Modeler files made before 0.4.54, the hand-drawn ones kept apart.</summary>
    public static List<Strip> Strips(StructuralModel m)
    {
        var list = new List<Strip>();
        var rows = Rows(m, StripGeometry, "STRIP").ToList();
        foreach (var key in rows.Select(r => (Name: Unquote(r[1]), Story: Value(r, "STORY") ?? "")).Distinct())
        {
            var mine = rows.Where(r => Unquote(r[1]) == key.Name && (Value(r, "STORY") ?? "") == key.Story).ToList();
            var plan = new List<double[]>();
            double wl = 0, wr = 0;
            foreach (var r in mine)
            {
                var pn = Value(r, "POINT");
                if (pn is not null && m.Point(pn) is { } p) plan.Add([p.X, p.Y]);
                if (Value(r, "WALEFT") is not null && wl == 0) { wl = D(r, "WALEFT"); wr = D(r, "WARIGHT"); }
            }
            var ow = Rows(m, StripOverwrites, "SLABSTRIPDESIGNOVERWRITES").FirstOrDefault(o => Is(o, key.Name, key.Story));
            var layer = ow is null ? Guess(key.Name) : Value(ow, "LAYER") ?? Guess(key.Name);
            list.Add(new Strip(key.Name, key.Story, layer, plan, wl, wr,
                ow is null ? "Column" : Value(ow, "DESIGNTYPE") ?? "Column",
                ow is null || !string.Equals(Value(ow, "DESIGN"), "No", StringComparison.OrdinalIgnoreCase),
                ow is not null && string.Equals(Value(ow, "IGNOREPT"), "Yes", StringComparison.OrdinalIgnoreCase),
                ow is null ? "" : Value(ow, "REBARMAT") ?? "", ow is null ? 1 : D(ow, "RLLF", 1)));
        }
        foreach (var s in m.Extras?.Strips ?? [])
            if (!list.Exists(x => x.Name == s.Name)) list.Add(new Strip(s.Name, "", s.Layer, s.Points, s.WidthLeft, s.WidthRight));
        return list;
    }

    private static string Guess(string name) => name.StartsWith("CSB", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MSB", StringComparison.OrdinalIgnoreCase) ? "B" : "A";

    /// <summary>Adds or replaces a design strip on a story (ETABS and SAFE: Draw > Draw Design Strips), with its strip
    /// design overwrites (Design > Concrete Slab Design > Strip Overwrites).</summary>
    public static Strip SetStrip(StructuralModel m, string name, string story, string layer, IReadOnlyList<double[]> points, double left, double right,
        string designType = "Column", bool design = true, bool ignorePT = false, string rebarMaterial = "", double llrf = 1)
    {
        Named(name, "strip");
        layer = layer.Trim().ToUpperInvariant() is "A" or "B" ? layer.Trim().ToUpperInvariant() : throw new ArgumentException("The strip layer is A or B.");
        var si = m.StoryIndex(story);
        if (si < 0) throw new ArgumentException($"There is no story '{story}'.");
        story = m.Stories[si].Name;
        if (points.Count < 2) throw new ArgumentException("A strip runs through at least two points (x, y).");
        if (points.Any(p => p.Length != 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) throw new ArgumentException("Each strip point is [x, y].");
        for (var i = 1; i < points.Count; i++)
            if (Math.Abs(points[i][0] - points[i - 1][0]) + Math.Abs(points[i][1] - points[i - 1][1]) < 1e-9) throw new ArgumentException("Two strip points in a row are the same point.");
        if (!double.IsFinite(left) || !double.IsFinite(right) || left < 0 || right < 0) throw new ArgumentException("The strip's widths to the left and right are 0 or more.");
        if (left + right <= 0) throw new ArgumentException("A strip has a width.");
        designType = StripDesignTypes.FirstOrDefault(t => t.Equals(designType.Replace(" Strip", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The strip design type is one of {string.Join(", ", StripDesignTypes)}.");
        if (rebarMaterial.Length > 0 && m.Materials.Find(x => x.Name == rebarMaterial) is null) throw new ArgumentException($"There is no material '{rebarMaterial}'.");
        if (!(llrf > 0 && llrf <= 1)) throw new ArgumentException("The live load reduction factor is more than 0 and at most 1.");
        DeleteStripRows(m, name);
        m.Extras?.Strips.RemoveAll(s => s.Name == name);
        if (m.Extras is not null) ExtrasDefs.Tidy(m);
        var names = points.Select(p => m.PointAt(p[0], p[1]).Name).ToList();
        var g = Section(m, StripGeometry).Lines;
        g.Add(Line(["STRIP", Quote(name), "STORY", Quote(story), "NUMSEGS", (names.Count - 1).ToString(CultureInfo.InvariantCulture), "POINT", Quote(names[0]), "WALEFT", N(left), "WARIGHT", N(right), "AUTOWIDEN", Quote("No")]));
        for (var i = 1; i < names.Count; i++)
        {
            // The end of a segment; where another segment starts there too, its start widths follow (the same widths).
            var row = new List<string> { "STRIP", Quote(name), "STORY", Quote(story), "POINT", Quote(names[i]), "WBLEFT", N(left), "WBRIGHT", N(right) };
            if (i < names.Count - 1) row.AddRange(["WALEFT", N(left), "WARIGHT", N(right)]);
            g.Add(Line(row));
        }
        var rebar = rebarMaterial.Length > 0 ? rebarMaterial : m.Materials.FirstOrDefault(x => x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
        var ow = new List<string> { "SLABSTRIPDESIGNOVERWRITES", Quote(name), Quote(story), "LAYER", Quote(layer), "DESIGNTYPE", Quote(designType), "RLLF", N(llrf), "DESIGN", Quote(design ? "Yes" : "No"), "IGNOREPT", Quote(ignorePT ? "Yes" : "No") };
        if (rebar.Length > 0) ow.AddRange(["REBARMAT", Quote(rebar)]);
        ow.AddRange(["COVERTYPE", Quote("Preferences")]);
        Section(m, StripOverwrites).Lines.Add(Line(ow));
        return Strips(m).First(s => s.Name == name);
    }

    private static void DeleteStripRows(StructuralModel m, string name)
    {
        RemoveRows(m, StripGeometry, t => t[0] == "STRIP" && Unquote(t[1]) == name);
        RemoveRows(m, StripOverwrites, t => t[0] == "SLABSTRIPDESIGNOVERWRITES" && Unquote(t[1]) == name);
    }

    public static void DeleteStrip(StructuralModel m, string name)
    {
        if (!Strips(m).Exists(s => s.Name == name)) throw new ArgumentException($"There is no strip '{name}'.");
        DeleteStripRows(m, name);
        m.Extras?.Strips.RemoveAll(s => s.Name == name);
        if (m.Extras is not null) ExtrasDefs.Tidy(m);
    }

    private static void Named(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"Give the {what} a name.");
        if (name.Contains('"')) throw new ArgumentException("A name cannot have a double quote in it.");
    }
    // ---- punching shear overwrites (Design > Concrete Slab Design > Punching Shear Overwrites) ----

    public static readonly string[] PunchingChecks = ["Program Determined", "Yes", "No"];
    public static readonly string[] PunchingLocations = ["Auto", "Interior", "Edge", "Corner"];
    public static readonly string[] PunchingReinforcements = ["None", "Ties", "Stud Rails"];
    public static readonly string[] PunchingPatterns = ["Radial", "Orthogonal"];

    /// <summary>A punching shear overwrite at a point (a column or a point load): whether it is checked, the location type, the
    /// critical perimeter (ETABS's own, or one given by its corners about the point: x, y and the corner radius) and the shear
    /// reinforcement (ties or stud rails, their pattern, yield stress, bar diameter and spacing). As ETABS 23.2 writes it.</summary>
    public sealed record Punching(string Point, string Story, string Check = "Program Determined", string Location = "Auto", List<double[]>? Perimeter = null,
        string Reinforcement = "None", string Pattern = "Radial", double Fy = 0, double Diameter = 0, double Spacing = 0)
    {
        public string Name => Point + "@" + Story;
    }

    public static List<Punching> PunchingList(StructuralModel m)
    {
        var list = new List<Punching>();
        foreach (var t in Rows(m, PunchingOverwrites, "PUNCHINGSHEAROVERWRITES"))
        {
            if (t.Count < 3) continue;
            var point = Unquote(t[1]); var story = Unquote(t[2]);
            var corners = Rows(m, PunchingPerimeter, "PUNCHINGSHEARPERIMETER").Where(r => Is(r, point, story)).OrderBy(r => D(r, "POINTNUM"))
                .Select(r => new[] { D(r, "X"), D(r, "Y"), D(r, "RADIUS") }).ToList();
            var user = string.Equals(Value(t, "PERIMETER"), "User Perimeter", StringComparison.OrdinalIgnoreCase);
            list.Add(new Punching(point, story, Value(t, "CHECK") ?? "Program Determined", Value(t, "LOCTYPE") ?? "Auto", user ? corners : null,
                Value(t, "REINFTYPE") ?? "None", Value(t, "REINFPAT") ?? "Radial", D(t, "REINFFY"), D(t, "REINFDIA"), D(t, "REINFSPCG")));
        }
        return list;
    }

    private static string OneOf(string[] options, string value, string what) =>
        options.FirstOrDefault(o => o.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"The {what} is one of {string.Join(", ", options)}; not '{value}'.");

    public static Punching SetPunching(StructuralModel m, Punching p)
    {
        var level = m.StoryIndex(p.Story);
        if (level < 0) throw new ArgumentException($"There is no story '{p.Story}'.");
        if (level == 0) throw new ArgumentException("Punching is checked in a slab: pick the slab's story, not the base.");
        if (m.Point(p.Point) is null) throw new ArgumentException($"There is no point '{p.Point}'.");
        p = p with
        {
            Story = m.Stories[level].Name, Check = OneOf(PunchingChecks, p.Check, "punching check"), Location = OneOf(PunchingLocations, p.Location, "location type"),
            Reinforcement = OneOf(PunchingReinforcements, p.Reinforcement, "shear reinforcement"), Pattern = OneOf(PunchingPatterns, p.Pattern, "reinforcement pattern"),
        };
        if (p.Perimeter is { } c)
        {
            if (c.Count < 3) throw new ArgumentException("A user perimeter has 3 corners or more, about the point: [[x, y], ...] (with a third number, the corner radius, if it is rounded).");
            if (c.Any(k => k.Length < 2 || k.Take(3).Any(v => !double.IsFinite(v)) || (k.Length > 2 && k[2] < 0))) throw new ArgumentException("Each corner of the perimeter is x, y and, if it is rounded, its radius (0 or more).");
            p = p with { Perimeter = c.Select(k => new[] { k[0], k[1], k.Length > 2 ? k[2] : 0 }).ToList() };
        }
        if (p.Reinforcement != "None" && (!(p.Fy > 0) || !(p.Diameter > 0) || !(p.Spacing > 0)))
            throw new ArgumentException("Shear reinforcement needs its yield stress (fy), bar diameter and spacing, each more than 0.");
        DeletePunching(m, p.Point, p.Story, quiet: true);
        List<string> t = ["PUNCHINGSHEAROVERWRITES", Quote(p.Point), Quote(p.Story), "CHECK", Quote(p.Check), "LOCTYPE", Quote(p.Location),
            "PERIMETER", Quote(p.Perimeter is null ? "Auto" : "User Perimeter"), "EFFDEPTH", Quote("Auto"), "REINFTYPE", Quote(p.Reinforcement)];
        if (p.Reinforcement != "None") t.AddRange(["REINFPAT", Quote(p.Pattern), "REINFFY", N(p.Fy), "REINFDIA", N(p.Diameter), "REINFSPCG", N(p.Spacing)]);
        Section(m, PunchingOverwrites).Lines.Add(Line(t));
        if (p.Perimeter is { } corners)
        {
            var lines = Section(m, PunchingPerimeter).Lines;
            for (var i = 0; i < corners.Count; i++)
                lines.Add(Line(["PUNCHINGSHEARPERIMETER", Quote(p.Point), Quote(p.Story), "POINTNUM", (i + 1).ToString(CultureInfo.InvariantCulture),
                    "X", N(corners[i][0]), "Y", N(corners[i][1]), "RADIUS", N(corners[i][2]), "ISNULL", Quote("No")]));
        }
        return p;
    }

    public static void DeletePunching(StructuralModel m, string point, string story, bool quiet = false)
    {
        if (!quiet && !PunchingList(m).Exists(x => x.Point == point && string.Equals(x.Story, story, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There is no punching overwrite at point {point} on {story}.");
        RemoveRows(m, PunchingOverwrites, t => t[0] == "PUNCHINGSHEAROVERWRITES" && Is(t, point, story));
        RemoveRows(m, PunchingPerimeter, t => t[0] == "PUNCHINGSHEARPERIMETER" && Is(t, point, story));
    }
    // ---- openings in slabs (Draw > Draw Floor/Wall Openings) ----

    private const string AreaConnect = "AREA CONNECTIVITIES";

    /// <summary>An opening: an area object of type AREA assigned OPENING "Yes", as ETABS 23.2 writes it, on its story.</summary>
    public sealed record Opening(string Name, string Story, List<double[]> Plan);

    private static IEnumerable<(string Line, List<string> Tokens)> OpeningLines(StructuralModel m) =>
        (m.UnparsedLines.GetValueOrDefault(AreaConnect) ?? []).Select(l => (l, Tokenize(l))).Where(x => x.Item2.Count > 3 && x.Item2[0] == "AREA" && x.Item2[2] == "AREA");

    public static List<Opening> Openings(StructuralModel m)
    {
        var list = new List<Opening>();
        foreach (var (_, t) in OpeningLines(m))
        {
            var name = Unquote(t[1]);
            if (!int.TryParse(t[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || t.Count < 4 + n) continue;
            var plan = t.Skip(4).Take(n).Select(pn => m.Point(Unquote(pn))).Where(p => p is not null).Select(p => new[] { p!.X, p.Y }).ToList();
            foreach (var a in m.AreaAssigns.Where(a => a.Label == name && a.Extra.Any(e => Value(e, "OPENING") is { } v && Unquote(v).Equals("Yes", StringComparison.OrdinalIgnoreCase))))
                list.Add(new Opening(name, a.Story, plan));
        }
        return list;
    }

    public static Opening SetOpening(StructuralModel m, string name, string story, IReadOnlyList<double[]> plan)
    {
        Named(name, "opening");
        var level = m.StoryIndex(story);
        if (level <= 0) throw new ArgumentException(level < 0 ? $"There is no story '{story}'." : "An opening is in a floor: pick the slab's story, not the base.");
        story = m.Stories[level].Name;
        if (plan.Count < 3 || plan.Any(p => p.Length < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) throw new ArgumentException("An opening has 3 corners or more in plan: [[x, y], ...].");
        var area = 0.0;
        for (var i = 0; i < plan.Count; i++) { var a = plan[i]; var b = plan[(i + 1) % plan.Count]; area += a[0] * b[1] - b[0] * a[1]; }
        if (Math.Abs(area) < 1e-12) throw new ArgumentException("The opening's corners enclose no area.");
        if (m.Areas.Exists(a => a.Label == name) && !OpeningLines(m).Any(x => Unquote(x.Tokens[1]) == name)) throw new ArgumentException($"There is already an area '{name}'; give the opening another name.");
        DeleteOpening(m, name, quiet: true);
        var names = plan.Select(p => m.PointAt(p[0], p[1]).Name).ToList();
        if (!m.UnparsedLines.TryGetValue(AreaConnect, out var lines)) m.UnparsedLines[AreaConnect] = lines = [];
        lines.Add(Line(new List<string> { "AREA", Quote(name), "AREA", names.Count.ToString(CultureInfo.InvariantCulture) }.Concat(names.Select(Quote)).Concat(names.Select(_ => "0"))));
        m.AreaAssigns.Add(new AreaAssign { Label = name, Story = story, Extra = [["OPENING", Quote("Yes")]] });
        return new Opening(name, story, plan.Select(p => new[] { p[0], p[1] }).ToList());
    }

    public static void DeleteOpening(StructuralModel m, string name, bool quiet = false)
    {
        var had = m.UnparsedLines.TryGetValue(AreaConnect, out var lines) && lines.RemoveAll(l => Tokenize(l) is { Count: > 3 } t && t[0] == "AREA" && t[2] == "AREA" && Unquote(t[1]) == name) > 0;
        if (!had && !quiet) throw new ArgumentException($"There is no opening '{name}'.");
        m.AreaAssigns.RemoveAll(a => a.Label == name && a.Extra.Any(e => Value(e, "OPENING") is not null));
    }

    // ---- null lines, slab line releases and slab rebar ----
    // Taken from ETABS 23.2 on Ahmed's PC (2026-11-26, the "J" export of PT-SL Ex001): a null line is LINE "L1" LINE
    // "i" "j" 0 with LINEASSIGN SECTION "NONE"; a slab line release is NOT in the .e2k at all, only in the table "Area
    // Assignments - Slab Line Releases" (UniqueName of the line, ShearL, MomentL, ShearR, MomentR, Yes or No), with no
    // API call; ETABS Plus 23.2 refuses every slab rebar object ("Slab Rebar Object Geometry" and "Slab Rebar Property
    // Assignments" answered 1 with an empty log). So releases and rebar live in the Modeler's own file (.cpm), go into
    // the .f2k in SAFE's tables, and into a running program through its tables.

    private const string LineConnect = "LINE CONNECTIVITIES";

    /// <summary>A null line (Draw > Draw Null Lines): a line with no section, which a slab line release sits on.</summary>
    public sealed record NullLine(string Name, string Story, double[] I, double[] J);

    private static IEnumerable<List<string>> NullLineTokens(StructuralModel m) =>
        (m.UnparsedLines.GetValueOrDefault(LineConnect) ?? []).Select(Tokenize).Where(t => t.Count >= 5 && t[0] == "LINE" && t[2] == "LINE");

    public static List<NullLine> NullLines(StructuralModel m)
    {
        var list = new List<NullLine>();
        foreach (var t in NullLineTokens(m))
        {
            var name = Unquote(t[1]);
            if (m.Point(Unquote(t[3])) is not { } i || m.Point(Unquote(t[4])) is not { } j) continue;
            foreach (var a in m.LineAssigns.Where(a => a.Label == name))
                list.Add(new NullLine(name, a.Story, [i.X, i.Y], [j.X, j.Y]));
        }
        return list;
    }

    public static NullLine SetNullLine(StructuralModel m, string name, string story, double[] i, double[] j)
    {
        Named(name, "null line");
        var level = m.StoryIndex(story);
        if (level <= 0) throw new ArgumentException(level < 0 ? $"There is no story '{story}'." : "A null line for a slab is on the slab's story, not the base.");
        story = m.Stories[level].Name;
        if (i.Length < 2 || j.Length < 2 || !double.IsFinite(i[0] + i[1] + j[0] + j[1])) throw new ArgumentException("A null line goes from [x, y] to [x, y].");
        if (Math.Abs(i[0] - j[0]) + Math.Abs(i[1] - j[1]) < 1e-9) throw new ArgumentException("The null line's two ends are at the same place.");
        if (m.Lines.Exists(l => l.Label == name)) throw new ArgumentException($"There is already a line '{name}' (a beam, column or brace); give the null line another name.");
        // ETABS deletes a line drawn on top of another one, and stops opening the .e2k on a "Log" window to say so
        // ("Following line(s) were duplicated and will be deleted in this model", 0.4.58 full field test).
        if (OnTop(m, name, story, i, j) is { } under)
            throw new ArgumentException($"There is already the line '{under}' from [{i[0]}, {i[1]}] to [{j[0]}, {j[1]}] on {story}; ETABS deletes a line drawn on top of another. Draw the null line where no beam or line is.");
        DeleteNullLine(m, name, quiet: true);
        var a = m.PointAt(i[0], i[1]).Name; var b = m.PointAt(j[0], j[1]).Name;
        if (!m.UnparsedLines.TryGetValue(LineConnect, out var lines)) m.UnparsedLines[LineConnect] = lines = [];
        lines.Add(Line(["LINE", Quote(name), "LINE", Quote(a), Quote(b), "0"]));
        m.LineAssigns.Add(new LineAssign { Label = name, Story = story, Section = "NONE" });
        return new NullLine(name, story, [i[0], i[1]], [j[0], j[1]]);
    }

    /// <summary>The label of a beam or null line (other than <paramref name="name"/>) that already joins these two
    /// plan points on this story, or null.</summary>
    public static string? OnTop(StructuralModel m, string name, string story, double[] i, double[] j)
    {
        static bool At(PlanPoint? p, double[] xy) => p is not null && Math.Abs(p.X - xy[0]) < 1e-6 && Math.Abs(p.Y - xy[1]) < 1e-6;
        bool Same(PlanPoint? p, PlanPoint? q) => (At(p, i) && At(q, j)) || (At(p, j) && At(q, i));
        foreach (var l in m.Lines)
            if (l.Label != name && l.StoriesBelowI == 0 && Same(m.Point(l.PointI), m.Point(l.PointJ)) && m.LineAssigns.Exists(x => x.Label == l.Label && x.Story == story))
                return l.Label;
        foreach (var n in NullLines(m))
            if (n.Name != name && n.Story == story && ((Near(n.I, i) && Near(n.J, j)) || (Near(n.I, j) && Near(n.J, i))))
                return n.Name;
        return null;
    }

    private static bool Near(double[] p, double[] q) => Math.Abs(p[0] - q[0]) < 1e-6 && Math.Abs(p[1] - q[1]) < 1e-6;

    public static void DeleteNullLine(StructuralModel m, string name, bool quiet = false)
    {
        var had = m.UnparsedLines.TryGetValue(LineConnect, out var lines) && lines.RemoveAll(l => Tokenize(l) is { Count: >= 5 } t && t[0] == "LINE" && t[2] == "LINE" && Unquote(t[1]) == name) > 0;
        if (!had && !quiet) throw new ArgumentException($"There is no null line '{name}'.");
        if (had) m.LineAssigns.RemoveAll(a => a.Label == name);
        DeleteLineRelease(m, name, quiet: true);
    }

    /// <summary>Which of a slab line release's four actions are released, on the line's left and right.</summary>
    public sealed record LineRelease(string Line, string Story, bool ShearLeft, bool MomentLeft, bool ShearRight, bool MomentRight);

    /// <summary>The table a running ETABS or SAFE takes slab line releases in (ETABS 23.2's field keys). The Modeler keeps
    /// the row on the null line's assignment (<see cref="AssignTables"/>), so the .cpm, Excel and the program's tables carry it.</summary>
    public const string LineReleaseTable = "Area Assignments - Slab Line Releases";

    private static bool Yes(IReadOnlyDictionary<string, string> r, string k) => r.TryGetValue(k, out var v) && v.Equals("Yes", StringComparison.OrdinalIgnoreCase);

    public static List<LineRelease> LineReleases(StructuralModel m) =>
        m.LineAssigns.Where(a => a.Tables?.ContainsKey(LineReleaseTable) == true).Select(a => AssignTables.Row(a, LineReleaseTable) is var r
            ? new LineRelease(a.Label, a.Story, Yes(r, "ShearL"), Yes(r, "MomentL"), Yes(r, "ShearR"), Yes(r, "MomentR")) : null!).ToList();

    /// <summary>The row of a release as the program's table takes it.</summary>
    public static Dictionary<string, string> LineReleaseRow(LineRelease r, string uniqueName) => new(StringComparer.Ordinal)
    {
        ["UniqueName"] = uniqueName, ["ShearL"] = YesNo(r.ShearLeft), ["MomentL"] = YesNo(r.MomentLeft), ["ShearR"] = YesNo(r.ShearRight), ["MomentR"] = YesNo(r.MomentRight),
    };

    private static string YesNo(bool b) => b ? "Yes" : "No";

    public static LineRelease SetLineRelease(StructuralModel m, LineRelease r)
    {
        var line = NullLines(m).Find(l => l.Name == r.Line) ?? throw new ArgumentException($"There is no null line '{r.Line}'. A slab line release sits on a null line: draw one first (points from and to).");
        if (!(r.ShearLeft || r.MomentLeft || r.ShearRight || r.MomentRight)) throw new ArgumentException("Release at least one of shear or moment, on the left or the right.");
        r = r with { Story = line.Story };
        var row = LineReleaseRow(r, "");
        row.Remove("UniqueName");
        AssignTables.Set(m.LineAssigns.First(a => a.Label == r.Line && a.Story == r.Story), new Dictionary<string, List<Dictionary<string, string>>> { [LineReleaseTable] = [row] });
        return r;
    }

    public static void DeleteLineRelease(StructuralModel m, string line, bool quiet = false)
    {
        var had = false;
        foreach (var a in m.LineAssigns.Where(a => a.Label == line && a.Tables?.ContainsKey(LineReleaseTable) == true))
        {
            a.Tables!.Remove(LineReleaseTable);
            if (a.Tables.Count == 0) a.Tables = null;
            had = true;
        }
        if (!had && !quiet) throw new ArgumentException($"There is no slab line release on '{line}'.");
    }

    private static List<Dictionary<string, string>> KeptRows(StructuralModel m, string key, bool make)
    {
        m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
        if (m.Kept.TryGetValue(key, out var rows)) return rows;
        rows = [];
        if (make) m.Kept[key] = rows;
        return rows;
    }

    /// <summary>
    /// A slab rebar object (Draw > Draw Slab Rebar): a band of bars from (X1, Y1) to (X2, Y2) on a story, its widths
    /// left and right of that line, its depth below the slab top (vertical offset) and its layer, with the bar size,
    /// rebar material and either a number of bars or a most spacing. As ETABS 23.2's table keys name the fields.
    /// </summary>
    public sealed record SlabRebar(string Name, string Story, double[] From, double[] To, double WidthLeft, double WidthRight, double VerticalOffset, string Layer,
        string BarSize, string Material, int TotalBars, double MaxSpacing);

    public const string SlabRebarKept = "model|SLAB REBAR";
    public const string SlabRebarGeometryTable = "Slab Rebar Object Geometry";
    public const string SlabRebarPropertyTable = "Slab Rebar Property Assignments";

    private static double Num(Dictionary<string, string> r, string k) => TryNumber(r.GetValueOrDefault(k), out var d) ? d : 0;

    public static List<SlabRebar> SlabRebars(StructuralModel m) =>
        KeptRows(m, SlabRebarKept, false).Select(r => new SlabRebar(r.GetValueOrDefault("Name", ""), r.GetValueOrDefault("Story", ""), [Num(r, "X1"), Num(r, "Y1")], [Num(r, "X2"), Num(r, "Y2")],
            Num(r, "WidthLeft"), Num(r, "WidthRight"), Num(r, "VertOffset"), r.GetValueOrDefault("Layer", ""), r.GetValueOrDefault("RebarSize", ""), r.GetValueOrDefault("Material", ""),
            (int)Num(r, "TotalBars"), Num(r, "MaxSpacing"))).ToList();

    /// <summary>The two rows of a slab rebar object as the program's tables take them (geometry, then bars), by field key.</summary>
    public static (Dictionary<string, string> Geometry, Dictionary<string, string> Bars) SlabRebarRows(SlabRebar b) =>
        (new(StringComparer.Ordinal)
        {
            ["Name"] = b.Name, ["Story"] = b.Story, ["X1"] = N(b.From[0]), ["Y1"] = N(b.From[1]), ["X2"] = N(b.To[0]), ["Y2"] = N(b.To[1]),
            ["VertOffset"] = N(b.VerticalOffset), ["WidthLeft"] = N(b.WidthLeft), ["WidthRight"] = N(b.WidthRight), ["Layer"] = b.Layer,
        },
        b.TotalBars > 0
            ? new(StringComparer.Ordinal) { ["Name"] = b.Name, ["RebarSize"] = b.BarSize, ["Material"] = b.Material, ["BarNumType"] = "Total Bars", ["TotalBars"] = b.TotalBars.ToString(CultureInfo.InvariantCulture) }
            : new(StringComparer.Ordinal) { ["Name"] = b.Name, ["RebarSize"] = b.BarSize, ["Material"] = b.Material, ["BarNumType"] = "Max Spacing", ["MaxSpacing"] = N(b.MaxSpacing) });

    public static SlabRebar SetSlabRebar(StructuralModel m, SlabRebar b)
    {
        Named(b.Name, "slab rebar");
        var level = m.StoryIndex(b.Story);
        if (level <= 0) throw new ArgumentException(level < 0 ? $"There is no story '{b.Story}'." : "Slab rebar is in a slab: pick the slab's story, not the base.");
        b = b with { Story = m.Stories[level].Name };
        if (b.From.Length < 2 || b.To.Length < 2 || !double.IsFinite(b.From[0] + b.From[1] + b.To[0] + b.To[1]) || Math.Abs(b.From[0] - b.To[0]) + Math.Abs(b.From[1] - b.To[1]) < 1e-9)
            throw new ArgumentException("Slab rebar goes from [x, y] to another [x, y].");
        if (!(b.WidthLeft >= 0) || !(b.WidthRight >= 0) || b.WidthLeft + b.WidthRight <= 0) throw new ArgumentException("The band's widths left and right are 0 or more, and not both 0.");
        if (!(b.VerticalOffset >= 0)) throw new ArgumentException("The vertical offset (the bars' depth below the slab top) is 0 or more.");
        if (b.BarSize.Length == 0) throw new ArgumentException("Give the bar size (a rebar of Define > Reinforcing Bar Sizes).");
        if (DefineTables.RebarSizes(m) is { Count: > 0 } sizes && !sizes.Exists(x => x.Name == b.BarSize))
            throw new ArgumentException($"There is no bar size '{b.BarSize}'. The model's: {string.Join(", ", sizes.Select(x => x.Name))}.");
        if (!m.Materials.Exists(x => x.Name == b.Material && x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There is no rebar material '{b.Material}'. The model's: {string.Join(", ", m.Materials.Where(x => x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name))}.");
        if ((b.TotalBars > 0) == (b.MaxSpacing > 0)) throw new ArgumentException("Give either the number of bars (total_bars) or their most spacing (max_spacing), not both.");
        DeleteSlabRebar(m, b.Name, quiet: true);
        var (g, bars) = SlabRebarRows(b);
        foreach (var (k, v) in bars) if (k != "Name") g[k] = v;
        KeptRows(m, SlabRebarKept, true).Add(g);
        return b;
    }

    public static void DeleteSlabRebar(StructuralModel m, string name, bool quiet = false)
    {
        var rows = KeptRows(m, SlabRebarKept, false);
        if (rows.RemoveAll(r => r.GetValueOrDefault("Name") == name) == 0 && !quiet) throw new ArgumentException($"There is no slab rebar '{name}'.");
        if (rows.Count == 0) m.Kept?.Remove(SlabRebarKept);
    }
}
