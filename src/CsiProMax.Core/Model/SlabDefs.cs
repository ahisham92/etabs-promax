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

    /// <summary>ETABS 20's own order of the sections around these (from its export), so a new section goes where ETABS puts it.</summary>
    public static readonly string[] FileOrder =
    [
        "PROGRAM INFORMATION", "CONTROLS", "STORIES - IN SEQUENCE FROM TOP", "GRIDS", "DIAPHRAGM NAMES", "MATERIAL PROPERTIES", "REBAR DEFINITIONS",
        "FRAME SECTIONS", "AUTO SELECT SECTION LISTS", "CONCRETE SECTIONS", TendonSections, "SLAB PROPERTIES", "DECK PROPERTIES", "WALL PROPERTIES",
        "LINK PROPERTIES", "PANEL ZONE PROPERTIES", "PIER/SPANDREL NAMES", "POINT COORDINATES", "LINE CONNECTIVITIES", "AREA CONNECTIVITIES",
        TendonConnect, TendonLayout, TendonProfile, StripGeometry, "GROUPS", "POINT ASSIGNS", "LINE ASSIGNS", "AREA ASSIGNS", TendonAssigns,
        "LOAD PATTERNS", "POINT OBJECT LOADS", "FRAME OBJECT LOADS", "SHELL OBJECT LOADS", TendonLoads, "ANALYSIS OPTIONS", "MASS SOURCE",
        "FUNCTIONS", "GENERALIZED DISPLACEMENTS", "LOAD CASES", "LOAD COMBINATIONS", "GENERAL DESIGN PREFERENCES", "STEEL DESIGN PREFERENCES",
        "CONCRETE DESIGN PREFERENCES", "COMPOSITE DESIGN PREFERENCES", "COMPOSITE COLUMN DESIGN PREFERENCES", "WALL DESIGN PREFERENCES",
        SlabPreferences, StripOverwrites, FemOverwrites, PunchingOverwrites,
    ];

    /// <summary>Where the jack is, as the tools name it, and as ETABS's .e2k and SAFE's .f2k write it. "End" and "J End" are
    /// from ETABS 20 and SAFE 20; the other two follow the same pattern and are checked by the field test.</summary>
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

    /// <summary>The losses: "Fixed" (stresses lost at stressing and in the long term), or "Percent" (fractions of the jacking stress).</summary>
    public sealed record TendonLoss(string Method, double Stressing, double LongTerm);

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

        private (double X, double Y) PlanAt(double s)
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
                    loss = new TendonLoss(Value(l, "LOSSTYPE") ?? "Fixed", D(l, "LOSSFIXEDST"), D(l, "LOSSFIXEDLT"));
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
            if (loss.Stressing < 0 || loss.LongTerm < 0 || !double.IsFinite(loss.Stressing + loss.LongTerm)) throw new ArgumentException("The losses are 0 or more.");
            if (loss.Method.Equals("Percent", StringComparison.OrdinalIgnoreCase))
            {
                // ETABS's percentage losses are a share of the jacking stress, the same as fixed losses of that size.
                if (jack is null) throw new ArgumentException("Percentage losses are a share of the jacking stress: give the jacking too.");
                if (loss.Stressing + loss.LongTerm >= 100) throw new ArgumentException("The losses add up to 100% or more of the jacking stress.");
                loss = new TendonLoss("Fixed", jack.Stress * loss.Stressing / 100, jack.Stress * loss.LongTerm / 100);
            }
            else if (!loss.Method.Equals("Fixed", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The losses are 'Fixed' (stresses) or 'Percent' (of the jacking stress).");
            else loss = loss with { Method = "Fixed" };
            if (jack is not null && loss.Stressing + loss.LongTerm >= jack.Stress) throw new ArgumentException("The losses add up to the jacking stress or more.");
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
            Section(m, TendonLoads).Lines.Add(Line(["TENDONLOAD", Quote(name), Quote(story), "TYPE", Quote("Losses"), "LOSSTYPE", Quote("Fixed"), "LOSSFIXEDST", N(loss.Stressing), "LOSSFIXEDLT", N(loss.LongTerm)]));
        return Tendons(m).First(t => t.Name == name && t.Story == story);
    }

    private static void DeleteTendonRows(StructuralModel m, string name, string story)
    {
        RemoveRows(m, TendonConnect, t => t[0] == "TENDON" && Is(t, name, story));
        RemoveRows(m, TendonLayout, t => t[0] == "TENDONHL" && Is(t, name, story));
        RemoveRows(m, TendonProfile, t => t[0] == "TENDONVP" && Is(t, name, story));
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
}
