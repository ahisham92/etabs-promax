using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Checks.Stability;

/// <summary>
/// The Modeler's model (and any .e2k read into it) as the analysis sees it. ETABS keeps a plan point per label and
/// puts it on each story an object reaches, so a joint here is a label on a story, named "label@story" as the Viewer
/// names it. What the file holds word for word (releases, springs, the mass source, cases and combinations) is read
/// from the kept lines.
/// </summary>
public static class FromStructural
{
    private static readonly string[] Dofs = ["UX", "UY", "UZ", "RX", "RY", "RZ"];
    /// <summary>ETABS's release words, in the order the CSI programs list releases: P V2 V3 T M2 M3 at I, then at J.</summary>
    private static readonly string[] ReleaseWords = ["PI", "V2I", "V3I", "TI", "M2I", "M3I", "PJ", "V2J", "V3J", "TJ", "M2J", "M3J"];

    public static string Name(string label, string story) => label + "@" + story;

    public static AnalysisModel Read(StructuralModel m, string program = "modeler")
    {
        var a = new AnalysisModel { Program = program, LengthUnit = m.Units.Length, ForceUnit = m.Units.Force, JoinsAtIntersections = program != "sap2000" };
        if (m.Stories.Count == 0) { a.Notes.Add("The model has no stories."); return a; }

        foreach (var d in m.Diaphragms.Where(d => d.Type.Equals("RIGID", StringComparison.OrdinalIgnoreCase))) a.RigidDiaphragms.Add(d.Name);

        foreach (var mat in m.Materials.GroupBy(x => x.Name).Select(g => g.First()))
            a.Materials[mat.Name] = new MaterialProp(mat.Name, mat.Number("E"), mat.Number("WEIGHTPERVOLUME"), mat.Number("MASSPERVOLUME"), mat.Type);
        foreach (var s in m.FrameSections.GroupBy(x => x.Name).Select(g => g.First()))
            a.FrameProps[s.Name] = FrameSectionProps(s);
        foreach (var s in m.ShellSections.GroupBy(x => x.Name).Select(g => g.First()))
        {
            var type = s.Get("MODELINGTYPE") ?? (string.Equals(s.Get("PROPTYPE"), "Deck", StringComparison.OrdinalIgnoreCase) ? "Membrane" : "ShellThin");
            a.ShellProps[s.Name] = new ShellProp(s.Name, s.Material.Length > 0 ? s.Material : s.Get("CONCMATERIAL"), s.Thickness,
                type.Contains("Membrane", StringComparison.OrdinalIgnoreCase) ? "Membrane" : type.Contains("Plate", StringComparison.OrdinalIgnoreCase) ? "Plate" : "Shell",
                s.Get("PROPTYPE") ?? "Slab");
        }

        // Joints: every plan point on every story an object or an assignment puts it on.
        var points = m.Points.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
        var joints = new Dictionary<string, Joint>(StringComparer.Ordinal);
        string? Joint(string label, int story)
        {
            if (story < 0 || story >= m.Stories.Count || !points.TryGetValue(label, out var p)) return null;
            var name = Name(label, m.Stories[story].Name);
            if (!joints.ContainsKey(name))
            {
                var at = Physical.At(m, p, story);
                joints[name] = new Joint(name, label, m.Stories[story].Name, at.X, at.Y, at.Z, new bool[6]);
            }
            return name;
        }

        foreach (var pa in m.PointAssigns)
        {
            var name = Joint(pa.Point, m.StoryIndex(pa.Story));
            if (name is null) continue;
            var restraint = Dofs.Select(d => (pa.Restraint ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(d, StringComparer.OrdinalIgnoreCase)).ToArray();
            var spring = Has(pa.Extra, "SPRINGPROP");
            var diaphragm = pa.Diaphragm is { Length: > 0 } dn && a.RigidDiaphragms.Contains(dn) ? dn : null;
            joints[name] = joints[name] with { Restraint = restraint, Spring = spring, Diaphragm = diaphragm };
        }

        var lines = m.Lines.GroupBy(l => l.Label).ToDictionary(g => g.Key, g => g.First());
        foreach (var la in m.LineAssigns)
        {
            var story = m.StoryIndex(la.Story);
            if (story < 0 || !lines.TryGetValue(la.Label, out var line)) continue;
            var i = Joint(line.PointI, story - line.StoriesBelowI);
            var j = Joint(line.PointJ, story);
            if (i is null || j is null) continue;
            var releases = new bool[12];
            var words = (Find(la.Extra, "RELEASE") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var k = 0; k < 12; k++) releases[k] = words.Contains(ReleaseWords[k], StringComparer.OrdinalIgnoreCase);
            a.Frames.Add(new Frame(Name(la.Label, la.Story), la.Label, la.Story, line.Kind.ToString().ToLowerInvariant(), i, j,
                la.Section, releases, Meshed: program != "sap2000", Spring: Has(la.Extra, "SPRINGPROP")));
        }

        var areas = m.Areas.GroupBy(x => x.Label).ToDictionary(g => g.Key, g => g.First());
        foreach (var aa in m.AreaAssigns)
        {
            var story = m.StoryIndex(aa.Story);
            if (story < 0 || !areas.TryGetValue(aa.Label, out var area)) continue;
            var corners = new List<string>();
            for (var k = 0; k < area.PointNames.Count; k++)
            {
                var below = k < area.StoriesBelow.Count ? area.StoriesBelow[k] : 0;
                if (Joint(area.PointNames[k], story - below) is { } c) corners.Add(c);
            }
            var diaphragm = aa.Diaphragm is { Length: > 0 } dn && a.RigidDiaphragms.Contains(dn) ? dn : null;
            var meshNone = string.Equals(Find(aa.Extra, "OBJMESHTYPE"), "NONE", StringComparison.OrdinalIgnoreCase);
            a.Shells.Add(new Shell(Name(aa.Label, aa.Story), aa.Label, aa.Story, area.Kind == AreaKind.Wall ? "wall" : "floor", corners, aa.Section,
                Spring: Has(aa.Extra, "SPRINGPROP"), Diaphragm: diaphragm, MeshNone: meshNone));
            // A rigid diaphragm on a floor holds every corner of it.
            if (diaphragm is not null)
                foreach (var c in corners.Where(c => joints[c].Diaphragm is null)) joints[c] = joints[c] with { Diaphragm = diaphragm };
        }

        // Which joints carry loads (a load on a joint that nothing holds is lost).
        foreach (var pl in m.PointLoads)
        {
            var name = Name(pl.Object, pl.Story);
            if (joints.TryGetValue(name, out var j)) joints[name] = j with { Loaded = true };
        }
        a.Joints.AddRange(joints.Values);

        var loaded = m.PointLoads.Cast<ObjectLoad>().Concat(m.LineLoads).Concat(m.AreaLoads).Select(l => l.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in m.LoadPatterns)
            a.Patterns.Add(new Pattern(p.Name, p.Type, p.SelfWeight, loaded.Contains(p.Name) || p.Extra.Count > 0));

        ReadCases(m, a);
        ReadCombos(m, a);
        ReadMass(m, a);
        if (m.Passthrough.FirstOrDefault(p => p.Heading.Trim().Equals("FUNCTIONS", StringComparison.OrdinalIgnoreCase)) is { } functions)
            a.Functions = functions.Lines.Select(E2kText.Tokenize).Where(t => t.Count > 1 && t[0].Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))
                .Select(t => E2kText.Unquote(t[1])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return a;
    }

    /// <summary>The stiffness numbers a section's size gives: rectangles, circles, I and tee shapes, pipes and tubes.</summary>
    public static FrameProp FrameSectionProps(FrameSection s)
    {
        double? d = s.Number("D"), b = s.Number("B"), tf = s.Number("TF"), tw = s.Number("TW");
        var shape = s.Shape.ToLowerInvariant();
        double? area = null, i33 = null, i22 = null, j = null;
        if (shape.Contains("rectangular") && d is { } d1 && b is { } b1)
        {
            area = d1 * b1; i33 = b1 * d1 * d1 * d1 / 12; i22 = d1 * b1 * b1 * b1 / 12;
            var (lo, hi) = (Math.Min(d1, b1), Math.Max(d1, b1));
            j = hi * lo * lo * lo * (1.0 / 3 - 0.21 * lo / Math.Max(hi, 1e-12) * (1 - Math.Pow(lo / Math.Max(hi, 1e-12), 4) / 12));
        }
        else if (shape.Contains("circle") && d is { } dc)
        {
            area = Math.PI * dc * dc / 4; i33 = i22 = Math.PI * Math.Pow(dc, 4) / 64; j = 2 * i33;
        }
        else if ((shape.Contains("i/wide") || shape.Contains("tee") || shape.Contains("channel")) && d is { } di && b is { } bi && tf is { } f && tw is { } w)
        {
            var flanges = shape.Contains("tee") ? 1 : 2;
            area = flanges * bi * f + (di - flanges * f) * w;
            i33 = (bi * di * di * di - (bi - w) * Math.Pow(di - flanges * f, 3)) / 12;
            i22 = flanges * f * bi * bi * bi / 12 + (di - flanges * f) * w * w * w / 12;
            j = (flanges * bi * f * f * f + di * w * w * w) / 3;
        }
        else if ((shape.Contains("pipe") || shape.Contains("tube") || shape.Contains("box")) && d is { } dp)
        {
            var t = s.Number("T") ?? tw ?? tf ?? dp / 10;
            var bp = b ?? dp;
            if (shape.Contains("pipe")) { var di2 = dp - 2 * t; area = Math.PI * (dp * dp - di2 * di2) / 4; i33 = i22 = Math.PI * (Math.Pow(dp, 4) - Math.Pow(di2, 4)) / 64; j = 2 * i33; }
            else { area = dp * bp - (dp - 2 * t) * (bp - 2 * t); i33 = (bp * dp * dp * dp - (bp - 2 * t) * Math.Pow(dp - 2 * t, 3)) / 12; i22 = (dp * bp * bp * bp - (dp - 2 * t) * Math.Pow(bp - 2 * t, 3)) / 12; j = i33 + i22; }
        }
        // Sizes given as numbers that make nothing (a 0 depth) are kept as 0, so the check can say so.
        if (area is null && (d is 0 || b is 0)) area = 0;
        return new FrameProp(s.Name, s.Material.Length > 0 ? s.Material : null, area, i22, i33, j, s.Shape);
    }

    private static void ReadCases(StructuralModel m, AnalysisModel a)
    {
        foreach (var c in m.LoadCases)
        {
            var type = c.Lines.Select(l => E2kText.Value(l, "TYPE")).FirstOrDefault(t => t is not null) ?? "Linear Static";
            var patterns = c.Lines.Select(l => E2kText.Value(l, "LOADPAT")).OfType<string>().Distinct().ToList();
            var uses = c.Lines.SelectMany(l => new[] { E2kText.Value(l, "MODALCASE"), E2kText.Value(l, "INITCASE"), E2kText.Value(l, "PREVCASE") })
                .OfType<string>().Where(u => u.Length > 0).Distinct().ToList();
            var functions = c.Lines.SelectMany(l => new[] { E2kText.Value(l, "FUNC"), E2kText.Value(l, "FUNCTION") }).OfType<string>().Where(u => u.Length > 0).Distinct().ToList();
            a.Cases.Add(new Case(c.Name, type, patterns, uses, functions));
        }
    }

    private static void ReadCombos(StructuralModel m, AnalysisModel a)
    {
        foreach (var c in m.Combos)
        {
            var items = c.Lines.SelectMany(l => new[] { E2kText.Value(l, "LOADCASE"), E2kText.Value(l, "LOADCOMBO") })
                .OfType<string>().Distinct().ToList();
            a.Combos.Add(new Combo(c.Name, items));
        }
    }

    /// <summary>The mass source from the file's MASS SOURCE lines (kept word for word); the default one when several.</summary>
    private static void ReadMass(StructuralModel m, AnalysisModel a)
    {
        var raw = m.Passthrough.FirstOrDefault(p => p.Heading.Trim().Equals("MASS SOURCE", StringComparison.OrdinalIgnoreCase));
        if (raw is null) return;
        var rows = raw.Lines.Select(E2kText.Tokenize).Where(t => t.Count > 1 && t[0].Equals("MASSSOURCE", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0) return;
        var names = rows.Select(r => E2kText.Unquote(r[1])).Distinct().ToList();
        var chosen = rows.FirstOrDefault(r => Yes(E2kText.Value(r, "ISDEFAULT"))) is { } d ? E2kText.Unquote(d[1]) : names[0];
        var mine = rows.Where(r => E2kText.Unquote(r[1]) == chosen).ToList();
        bool Flag(string key) => mine.Any(r => Yes(E2kText.Value(r, key)));
        var loads = new List<(string, double)>();
        foreach (var r in mine)
            if (E2kText.Value(r, "LOADPAT") is { } lp) loads.Add((lp, E2kText.TryNumber(E2kText.Value(r, "MULTIPLIER") ?? E2kText.Value(r, "SF"), out var sf) ? sf : 1));
        var fromLoads = Flag("INCLUDELOADS");
        a.Mass = new MassSource(chosen, Flag("INCLUDEELEMENTS"), Flag("INCLUDEADDEDMASS"), fromLoads ? loads : []);
    }

    private static bool Yes(string? v) => string.Equals(v, "Yes", StringComparison.OrdinalIgnoreCase) || v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static string? Find(List<List<string>> extra, string key)
    {
        foreach (var line in extra)
            if (E2kText.Value(line, key) is { } v) return v;
        return null;
    }

    private static bool Has(List<List<string>> extra, string key) => Find(extra, key) is { Length: > 0 };
}
