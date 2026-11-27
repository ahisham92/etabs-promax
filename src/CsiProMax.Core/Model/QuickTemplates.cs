namespace CsiProMax.Model;

/// <summary>
/// What ETABS 23.2's File > New Model > New Model Quick Templates form and its "Structural Geometry and Properties for ..."
/// forms hold, read box by box on Ahmed's PC (2026-11-27). Lengths are in metres and loads in kN/m² (the model's units).
/// </summary>
public sealed class QuickTemplate
{
    /// <summary>blank, grid_only, beam (SAP2000's), steel_deck, staggered_truss, flat_slab, flat_slab_perimeter_beams, waffle_slab, two_way_or_ribbed_slab.</summary>
    public string Kind { get; set; } = "grid_only";
    public string Title { get; set; } = "Model";

    /// <summary>Grid lines spaced along X (a line at x) and along Y (a line at y), each with its label, as Grid System Data has them.</summary>
    public List<QuickGrid> XGrids { get; set; } = [];
    public List<QuickGrid> YGrids { get; set; } = [];
    /// <summary>Stories from the lowest up (name, height), as Story Data has them; the base level is "Base".</summary>
    public List<(string Name, double Height)> Stories { get; set; } = [];

    // Overhangs
    public double LeftEdge { get; set; }
    public double RightEdge { get; set; }
    public double TopEdge { get; set; }
    public double BottomEdge { get; set; }

    // Structural System Properties (concrete)
    public double SlabThickness { get; set; } = 0.2;
    public double ColumnSize { get; set; } = 0.3;
    public bool StiffAtColumns { get; set; }

    // Drop Panel Dimensions
    public bool DropPanels { get; set; }
    public double DropSize { get; set; } = 3;
    public double DropThickness { get; set; } = 0.35;

    // Beam Dimensions (perimeter beams; Transverse Beam uses the X values)
    public double BeamXDepth { get; set; } = 0.6;
    public double BeamXWidth { get; set; } = 0.3;
    public double BeamYDepth { get; set; } = 0.6;
    public double BeamYWidth { get; set; } = 0.3;

    // Rib Dimensions
    public bool Ribbed { get; set; } = true;
    public double RibDepth { get; set; } = 0.35;
    public double RibWidthTop { get; set; } = 0.125;
    public double RibWidthBottom { get; set; } = 0.12;
    public double RibSpacing { get; set; } = 0.8;
    /// <summary>X or Y: the direction the ribs run along.</summary>
    public string RibDirection { get; set; } = "X";

    // Steel Deck
    public bool SecondaryBeams { get; set; } = true;
    public string SecondaryDirection { get; set; } = "X";
    public double? SecondaryMaxSpacing { get; set; } = 3;
    public int? SecondaryNumber { get; set; }
    /// <summary>none, perimeter or intersecting.</summary>
    public string MomentFrame { get; set; } = "intersecting";

    // Staggered Truss
    public double VierendeelWidth { get; set; } = 2;
    public int PanelsBefore { get; set; } = 3;
    public int PanelsAfter { get; set; } = 3;
    public bool FirstBayTrussAtTop { get; set; } = true;

    // Load
    public string DeadPattern { get; set; } = "Dead";
    public double DeadLoad { get; set; }
    public string LivePattern { get; set; } = "Live";
    public double LiveLoad { get; set; }

    /// <summary>none, pinned or fixed.</summary>
    public string Restraints { get; set; } = "pinned";
    /// <summary>rigid, semi-rigid or none.</summary>
    public string Diaphragm { get; set; } = "rigid";
}

public sealed record QuickGrid(string Label, double At, bool Visible = true, string? Bubble = null);

public static class QuickTemplates
{
    public static readonly string[] Kinds =
        ["blank", "grid_only", "beam", "steel_deck", "staggered_truss", "flat_slab", "flat_slab_perimeter_beams", "waffle_slab", "two_way_or_ribbed_slab"];

    /// <summary>The section names ETABS 23.2's template forms list (Structural System Properties).</summary>
    public static readonly string[] SteelDeckSections = ["A-LatCol", "A-LatBm", "A-GravCol", "A-GravBm", "A-CompBm", "Deck1"];
    public static readonly string[] StaggeredTrussSections = ["A-TrChdW10", "A-TrWebW10", "A-LatCol", "A-LatBm", "Plank1"];

    public static StructuralModel Build(QuickTemplate t)
    {
        var kind = Kinds.FirstOrDefault(k => k.Equals(t.Kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The template is one of {string.Join(", ", Kinds)}, not '{t.Kind}'.");
        if (t.Stories.Count == 0) throw new ArgumentException("Give at least one story.");
        var m = ModelEdit.New(string.IsNullOrWhiteSpace(t.Title) ? "Model" : t.Title.Trim());
        foreach (var (name, height) in t.Stories) m.AddStory(name, height);
        if (kind == "blank") return m;

        if (t.XGrids.Count == 0 || t.YGrids.Count == 0) throw new ArgumentException("Give at least one grid line each way.");
        foreach (var (g, dir) in t.XGrids.Select(g => (g, "X")).Concat(t.YGrids.Select(g => (g, "Y"))))
        {
            m.AddGrid(g.Label, dir, g.At);
            var line = m.Grids[^1];
            line.Extra = ["VISIBLE", g.Visible ? "\"Yes\"" : "\"No\"", "BUBBLELOC", "\"" + (g.Bubble ?? (dir == "X" ? "End" : "Start")) + "\""];
        }
        if (kind == "grid_only") return m;

        var xs = t.XGrids.Select(g => g.At).Distinct().OrderBy(v => v).ToArray();
        var ys = t.YGrids.Select(g => g.At).Distinct().OrderBy(v => v).ToArray();
        string? dia = t.Diaphragm.ToLowerInvariant() switch
        {
            "rigid" => "D1",
            "semi-rigid" or "semirigid" => "D1",
            "none" or "no diaphragm" => null,
            _ => throw new ArgumentException("The floor diaphragm is rigid, semi-rigid or none."),
        };
        var stories = t.Stories.Select(s => s.Name).ToList();

        if (kind == "beam")
        {
            // SAP2000's Beam template: a continuous beam over the grid lines, restrained at each one.
            m.AddRectangularSection("Beam1", ModelEdit.DefaultConcrete, 0.5, 0.3, beam: true);
            var story = stories[^1];
            for (var i = 0; i + 1 < xs.Length; i++) m.AddBeam(xs[i], ys[0], xs[i + 1], ys[0], story, "Beam1");
            var r = t.Restraints.ToLowerInvariant();
            if (r != "none") foreach (var x in xs) m.AddSupport(x, ys[0], story, r == "fixed");
            return m;
        }
        switch (kind)
        {
            case "steel_deck": SteelDeck(m, t, xs, ys, stories, dia); break;
            case "staggered_truss": StaggeredTruss(m, t, xs, ys, stories, dia); break;
            default: Concrete(m, t, kind, xs, ys, stories, dia); break;
        }
        if (dia is not null && t.Diaphragm.StartsWith("semi", StringComparison.OrdinalIgnoreCase))
            foreach (var d in m.Diaphragms.Where(d => d.Name == dia)) d.Type = "SEMIRIGID";
        Supports(m, t, xs, ys, kind);
        return m;
    }

    // ---- concrete floors: flat slab, flat slab with perimeter beams, waffle, two way or ribbed ----

    private static void Concrete(StructuralModel m, QuickTemplate t, string kind, double[] xs, double[] ys, List<string> stories, string? dia)
    {
        Positive(t.SlabThickness, "The slab thickness"); Positive(t.ColumnSize, "The column size");
        const string conc = ModelEdit.DefaultConcrete;
        var col = "ConcCol"; m.AddRectangularSection(col, conc, t.ColumnSize, t.ColumnSize, beam: false);
        string slab;
        if (kind == "waffle_slab" || kind == "two_way_or_ribbed_slab" && t.Ribbed)
        {
            Positive(t.RibDepth, "The rib depth"); Positive(t.RibSpacing, "The rib spacing");
            slab = kind == "waffle_slab" ? "Waffle1" : "Ribbed1";
            var ribs = new PropertyDefs.Ribs(t.RibDepth, t.RibWidthTop, t.RibWidthBottom, t.RibSpacing, t.RibDirection.Equals("Y", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            if (kind == "waffle_slab") Waffle(m, slab, t.SlabThickness, conc, ribs);
            else PropertyDefs.SetShellType(m, slab, null, false, "Ribbed", "ShellThin", t.SlabThickness, conc, false, ribs);
        }
        else
        {
            slab = "Slab1";
            PropertyDefs.SetShellType(m, slab, null, false, "Slab", kind == "two_way_or_ribbed_slab" ? "ShellThin" : "ShellThick", t.SlabThickness, conc, false);
        }
        string? drop = null, stiff = null;
        var drops = t.DropPanels && kind is "flat_slab" or "flat_slab_perimeter_beams" or "waffle_slab";
        if (drops)
        {
            Positive(t.DropSize, "The drop panel size"); Positive(t.DropThickness, "The drop panel thickness");
            drop = "Drop1"; PropertyDefs.SetShellType(m, drop, null, false, "Drop", "ShellThick", t.DropThickness, conc, false);
        }
        if (t.StiffAtColumns) { stiff = "Stiff1"; PropertyDefs.SetShellType(m, stiff, null, false, "Stiff", "ShellThin", Math.Max(t.SlabThickness, t.DropPanels ? t.DropThickness : 0) * 2, conc, false); }
        string? beamX = null, beamY = null;
        if (kind == "flat_slab_perimeter_beams")
        {
            Positive(t.BeamXDepth, "The X-direction beam depth"); Positive(t.BeamYDepth, "The Y-direction beam depth");
            beamX = "ConcBmX"; m.AddRectangularSection(beamX, conc, t.BeamXDepth, t.BeamXWidth, beam: true);
            beamY = "ConcBmY"; m.AddRectangularSection(beamY, conc, t.BeamYDepth, t.BeamYWidth, beam: true);
        }
        else if (kind == "two_way_or_ribbed_slab")
        {
            Positive(t.BeamXDepth, "The transverse beam depth");
            beamX = beamY = "ConcBm"; m.AddRectangularSection(beamX, conc, t.BeamXDepth, t.BeamXWidth, beam: true);
        }

        double x0 = xs[0] - t.LeftEdge, x1 = xs[^1] + t.RightEdge, y0 = ys[0] - t.BottomEdge, y1 = ys[^1] + t.TopEdge;
        var px = Edges(xs, x0, x1); var py = Edges(ys, y0, y1);
        foreach (var story in stories)
        {
            foreach (var x in xs) foreach (var y in ys) m.AddColumn(x, y, story, col);
            for (var i = 0; i + 1 < px.Length; i++)
                for (var j = 0; j + 1 < py.Length; j++)
                {
                    var a = m.AddSlab([(px[i], py[j]), (px[i + 1], py[j]), (px[i + 1], py[j + 1]), (px[i], py[j + 1])], story, slab, dia);
                    Loads(m, t, a.Label, story);
                }
            foreach (var x in xs) foreach (var y in ys)
            {
                if (drop is not null) Square(m, x, y, t.DropSize, x0, x1, y0, y1, story, drop, dia);
                if (stiff is not null) Square(m, x, y, t.ColumnSize, x0, x1, y0, y1, story, stiff, dia);
            }
            if (kind == "flat_slab_perimeter_beams")
            {
                foreach (var y in new[] { ys[0], ys[^1] }.Distinct()) for (var i = 0; i + 1 < xs.Length; i++) m.AddBeam(xs[i], y, xs[i + 1], y, story, beamX!);
                foreach (var x in new[] { xs[0], xs[^1] }.Distinct()) for (var j = 0; j + 1 < ys.Length; j++) m.AddBeam(x, ys[j], x, ys[j + 1], story, beamY!);
            }
            else if (kind == "two_way_or_ribbed_slab")
            {
                // A two way slab has beams on every grid line; ribs run between the beams across them (Transverse Beam).
                var ribsAlongX = t.RibDirection.Equals("X", StringComparison.OrdinalIgnoreCase);
                if (!t.Ribbed || !ribsAlongX) foreach (var y in ys) for (var i = 0; i + 1 < xs.Length; i++) m.AddBeam(xs[i], y, xs[i + 1], y, story, beamX!);
                if (!t.Ribbed || ribsAlongX) foreach (var x in xs) for (var j = 0; j + 1 < ys.Length; j++) m.AddBeam(x, ys[j], x, ys[j + 1], story, beamY!);
            }
        }
    }

    /// <summary>A waffle slab section: ribs both ways at the one spacing.</summary>
    private static void Waffle(StructuralModel m, string name, double thickness, string material, PropertyDefs.Ribs ribs)
    {
        var sec = PropertyDefs.SetShellType(m, name, null, false, "Ribbed", "ShellThin", thickness, material, false, ribs);
        var line = sec.Lines[0];
        var at = line.IndexOf("SLABTYPE");
        if (at >= 0) line[at + 1] = "\"Waffle\"";
        var axis = line.IndexOf("SLABRIBPARALLELTOAXIS");
        if (axis >= 0) line.RemoveRange(axis, 2);
        var spacing = line.IndexOf("SLABRIBSPACING");
        if (spacing >= 0) { line[spacing] = "SLABRIBSPACINGDIR1"; line.InsertRange(spacing + 2, ["SLABRIBSPACINGDIR2", line[spacing + 1]]); }
    }

    // ---- steel deck ----

    private static void SteelDeck(StructuralModel m, QuickTemplate t, double[] xs, double[] ys, List<string> stories, string? dia)
    {
        const string steel = "S355";
        if (!m.Materials.Exists(x => x.Name == steel)) throw new ArgumentException("The steel S355 is missing from the seed model.");
        I(m, "A-LatCol", steel, 0.36, 0.37, 0.02, 0.012);
        I(m, "A-LatBm", steel, 0.45, 0.19, 0.015, 0.009);
        I(m, "A-GravCol", steel, 0.26, 0.26, 0.0125, 0.0075);
        I(m, "A-GravBm", steel, 0.36, 0.17, 0.0127, 0.008);
        I(m, "A-CompBm", steel, 0.3, 0.15, 0.0107, 0.0071);
        PropertyDefs.SetDeck(m, "Deck1", null, new PropertyDefs.Deck("Filled", ModelEdit.DefaultConcrete, steel,
            0.0889, 0.0762, 0.1524, 0.1143, 0.3048, 0.000875, 0, 0.01905, 0.1524, 413685));

        var momentAll = t.MomentFrame.Equals("intersecting", StringComparison.OrdinalIgnoreCase);
        var momentPerimeter = t.MomentFrame.Equals("perimeter", StringComparison.OrdinalIgnoreCase);
        if (!momentAll && !momentPerimeter && !t.MomentFrame.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The moment frame type is none, perimeter or intersecting.");
        bool EdgeX(double x) => Near(x, xs[0]) || Near(x, xs[^1]);
        bool EdgeY(double y) => Near(y, ys[0]) || Near(y, ys[^1]);
        double x0 = xs[0] - t.LeftEdge, x1 = xs[^1] + t.RightEdge, y0 = ys[0] - t.BottomEdge, y1 = ys[^1] + t.TopEdge;
        var px = Edges(xs, x0, x1); var py = Edges(ys, y0, y1);
        foreach (var story in stories)
        {
            foreach (var x in xs) foreach (var y in ys)
                m.AddColumn(x, y, story, momentAll || momentPerimeter && (EdgeX(x) || EdgeY(y)) ? "A-LatCol" : "A-GravCol");
            foreach (var y in ys) for (var i = 0; i + 1 < xs.Length; i++)
                Gravity(m.AddBeam(xs[i], y, xs[i + 1], y, story, momentAll || momentPerimeter && EdgeY(y) ? "A-LatBm" : "A-GravBm"));
            foreach (var x in xs) for (var j = 0; j + 1 < ys.Length; j++)
                Gravity(m.AddBeam(x, ys[j], x, ys[j + 1], story, momentAll || momentPerimeter && EdgeX(x) ? "A-LatBm" : "A-GravBm"));
            if (t.SecondaryBeams)
            {
                var alongX = t.SecondaryDirection.Equals("X", StringComparison.OrdinalIgnoreCase);
                var (spans, across) = alongX ? (xs, ys) : (ys, xs);
                for (var j = 0; j + 1 < across.Length; j++)
                {
                    var width = across[j + 1] - across[j];
                    var n = t.SecondaryNumber is > 0 ? t.SecondaryNumber.Value
                        : t.SecondaryMaxSpacing is > 0 ? Math.Max(0, (int)Math.Ceiling(width / t.SecondaryMaxSpacing.Value - 1e-9) - 1)
                        : throw new ArgumentException("Give the secondary beams' maximum spacing or number.");
                    for (var k = 1; k <= n; k++)
                    {
                        var c = across[j] + width * k / (n + 1);
                        for (var i = 0; i + 1 < spans.Length; i++)
                        {
                            var b = alongX ? m.AddBeam(spans[i], c, spans[i + 1], c, story, "A-CompBm") : m.AddBeam(c, spans[i], c, spans[i + 1], story, "A-CompBm");
                            PropertyDefs.SetReleases(b, ["M2", "M3"], ["M2", "M3"]);
                        }
                    }
                }
            }
            for (var i = 0; i + 1 < px.Length; i++)
                for (var j = 0; j + 1 < py.Length; j++)
                {
                    var a = m.AddSlab([(px[i], py[j]), (px[i + 1], py[j]), (px[i + 1], py[j + 1]), (px[i], py[j + 1])], story, "Deck1", dia);
                    Loads(m, t, a.Label, story);
                }
        }

        void Gravity(LineAssign b) { if (b.Section == "A-GravBm") PropertyDefs.SetReleases(b, ["M2", "M3"], ["M2", "M3"]); }
    }

    // ---- staggered truss ----

    private static void StaggeredTruss(StructuralModel m, QuickTemplate t, double[] xs, double[] ys, List<string> stories, string? dia)
    {
        const string steel = "S355";
        I(m, "A-TrChdW10", steel, 0.26, 0.256, 0.0173, 0.0107);
        I(m, "A-TrWebW10", steel, 0.25, 0.146, 0.0091, 0.0061);
        I(m, "A-LatCol", steel, 0.36, 0.37, 0.02, 0.012);
        I(m, "A-LatBm", steel, 0.45, 0.19, 0.015, 0.009);
        PropertyDefs.SetShellType(m, "Plank1", null, false, "Slab", "Membrane", 0.2, ModelEdit.DefaultConcrete, true);
        if (t.PanelsBefore < 1 || t.PanelsAfter < 1) throw new ArgumentException("A truss has at least one panel before and after the Vierendeel panel.");
        if (t.VierendeelWidth < 0) throw new ArgumentException("The Vierendeel panel width is 0 or more.");
        if (ys.Length < 2) throw new ArgumentException("A staggered truss spans between two grid lines in Y at least.");
        double yA = ys[0], yB = ys[^1], span = yB - yA;
        if (!(span > t.VierendeelWidth)) throw new ArgumentException("The Vierendeel panel is wider than the truss span.");
        var panel = (span - t.VierendeelWidth) / (t.PanelsBefore + t.PanelsAfter);
        var nodes = new List<double> { yA };
        for (var k = 1; k <= t.PanelsBefore; k++) nodes.Add(yA + panel * k);
        if (t.VierendeelWidth > 0) nodes.Add(nodes[^1] + t.VierendeelWidth);
        for (var k = 1; k <= t.PanelsAfter; k++) nodes.Add(nodes[^1] + panel);
        nodes[^1] = yB;
        var vierendeel = t.VierendeelWidth > 0 ? t.PanelsBefore : -1;

        double x0 = xs[0] - t.LeftEdge, x1 = xs[^1] + t.RightEdge, y0 = yA - t.BottomEdge, y1 = yB + t.TopEdge;
        var px = Edges(xs, x0, x1); var py = Edges([yA, yB], y0, y1);
        var levels = new List<string> { "Base" }; levels.AddRange(stories);
        for (var s = 1; s < levels.Count; s++)
        {
            var story = levels[s];
            foreach (var x in xs) { m.AddColumn(x, yA, story, "A-LatCol"); m.AddColumn(x, yB, story, "A-LatCol"); }
            foreach (var y in new[] { yA, yB }) for (var i = 0; i + 1 < xs.Length; i++) m.AddBeam(xs[i], y, xs[i + 1], y, story, "A-LatBm");
            for (var i = 0; i + 1 < px.Length; i++)
                for (var j = 0; j + 1 < py.Length; j++)
                {
                    var a = m.AddSlab([(px[i], py[j]), (px[i + 1], py[j]), (px[i + 1], py[j + 1]), (px[i], py[j + 1])], story, "Plank1", dia);
                    Loads(m, t, a.Label, story);
                }
        }
        // Trusses one story deep on every other story, the next grid line's a story higher or lower: the first bay's
        // reaches the top level when "First Bay has Truss at Top Level" is ticked.
        var top = levels.Count - 1;
        for (var g = 0; g < xs.Length; g++)
        {
            var x = xs[g];
            var start = (t.FirstBayTrussAtTop ? top : top - 1) - g % 2;
            for (var s = start; s >= 1; s -= 2)
            {
                string below = levels[s - 1], story = levels[s];
                for (var k = 0; k + 1 < nodes.Count; k++)
                {
                    m.AddBeam(x, nodes[k], x, nodes[k + 1], story, "A-TrChdW10");
                    m.AddBeam(x, nodes[k], x, nodes[k + 1], below, "A-TrChdW10");
                    if (k == vierendeel) continue;
                    var up = k < t.PanelsBefore;
                    PropertyDefs.SetReleases(up ? m.AddBrace(x, nodes[k], x, nodes[k + 1], story, "A-TrWebW10") : m.AddBrace(x, nodes[k + 1], x, nodes[k], story, "A-TrWebW10"), ["M2", "M3"], ["M2", "M3"]);
                }
                for (var k = 1; k + 1 < nodes.Count; k++) m.AddColumn(x, nodes[k], story, "A-TrWebW10");
            }
        }
    }

    // ---- shared ----

    private static void Supports(StructuralModel m, QuickTemplate t, double[] xs, double[] ys, string kind)
    {
        var r = t.Restraints.ToLowerInvariant();
        if (r == "none") return;
        if (r is not ("pinned" or "fixed")) throw new ArgumentException("The restraints at the bottom are none, pinned or fixed.");
        var at = kind == "staggered_truss" ? xs.SelectMany(x => new[] { (x, ys[0]), (x, ys[^1]) }) : xs.SelectMany(x => ys.Select(y => (x, y)));
        foreach (var (x, y) in at.Distinct()) m.AddSupport(x, y, "Base", r == "fixed");
    }

    private static void Loads(StructuralModel m, QuickTemplate t, string area, string story)
    {
        if (t.DeadLoad != 0) { Pattern(m, t.DeadPattern, "Dead"); m.AddAreaLoad(area, story, t.DeadPattern, t.DeadLoad); }
        if (t.LiveLoad != 0) { Pattern(m, t.LivePattern, "Live"); m.AddAreaLoad(area, story, t.LivePattern, t.LiveLoad); }
    }

    private static void Pattern(StructuralModel m, string name, string type)
    {
        if (!m.LoadPatterns.Exists(p => p.Name == name)) m.AddLoadPattern(name, type);
    }

    /// <summary>A square floor piece of this size centred on (x, y), cut at the slab's edge.</summary>
    private static void Square(StructuralModel m, double x, double y, double size, double x0, double x1, double y0, double y1, string story, string section, string? dia)
    {
        double a = Math.Max(x0, x - size / 2), b = Math.Min(x1, x + size / 2), c = Math.Max(y0, y - size / 2), d = Math.Min(y1, y + size / 2);
        if (b - a <= ModelEdit.Tolerance || d - c <= ModelEdit.Tolerance) return;
        m.AddSlab([(a, c), (b, c), (b, d), (a, d)], story, section, dia);
    }

    private static void I(StructuralModel m, string name, string steel, double d, double b, double tf, double tw) =>
        PropertyDefs.SetShape(m, name, null, "Steel I/Wide Flange", steel, new Dictionary<string, double> { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw });

    /// <summary>The grid positions with the overhang edges outside them (an edge on a grid line is not repeated).</summary>
    private static double[] Edges(double[] at, double lo, double hi)
    {
        var o = new List<double>();
        if (lo < at[0] - ModelEdit.Tolerance) o.Add(lo);
        o.AddRange(at);
        if (hi > at[^1] + ModelEdit.Tolerance) o.Add(hi);
        return [.. o];
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= ModelEdit.Tolerance;

    private static void Positive(double v, string what)
    {
        if (!(v > 0)) throw new ArgumentException($"{what} must be above 0.");
    }
}
