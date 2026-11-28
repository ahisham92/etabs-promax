using CsiProMax.E2k;

namespace CsiProMax.Model;

/// <summary>Builds and edits a model in everyday terms (a column at x, y on a story; a slab on these corners), taking
/// care of ETABS's plan points, object labels and per-story assignments. Coordinates are in the model's length unit.</summary>
public static class ModelEdit
{
    /// <summary>Points closer than this in plan are the same point (model length units).</summary>
    public const double Tolerance = 1e-6;

    /// <summary>
    /// A new, empty model with ETABS 23's own defaults: its materials, rebar sizes, analysis options, mass source,
    /// design preferences and the Dead and Live load patterns, as ETABS exports a new model (kN, m). ETABS refuses a
    /// text file that lacks these, so every model starts from them. Stories, grids and members are yours to add.
    /// </summary>
    public static StructuralModel New(string title, double baseElevation = 0)
    {
        var m = Seed.Etabs23();
        m.Title = title;
        m.Stories = [new Story { Name = "Base", Elevation = baseElevation }];
        m.Grids.Clear(); m.Points.Clear(); m.Lines.Clear(); m.Areas.Clear();
        m.PointAssigns.Clear(); m.LineAssigns.Clear(); m.AreaAssigns.Clear();
        m.PointLoads.Clear(); m.LineLoads.Clear(); m.AreaLoads.Clear();
        var info = m.Passthrough.Find(s => s.Heading == "PROJECT INFORMATION");
        if (info is not null) info.Lines = [E2kText.Line(["PROJECTINFO", "MODELNAME", E2kText.Quote(title)])];
        return m;
    }

    /// <summary>The concrete ETABS's default model comes with.</summary>
    public const string DefaultConcrete = "C30/37";
    private const string DefaultRebar = "DefaultRebar_500";

    public static Story AddStory(this StructuralModel m, string name, double height)
    {
        if (height <= 0) throw new ArgumentException($"Story '{name}' needs a height above 0.");
        if (m.StoryIndex(name) >= 0) throw new ArgumentException($"There is already a story named '{name}'.");
        var story = new Story { Name = name, Height = height };
        m.Stories.Add(story);
        return story;
    }

    public static void AddGrid(this StructuralModel m, string label, string direction, double coordinate) =>
        m.Grids.Add(new GridLine
        {
            Label = label, Direction = direction.ToUpperInvariant(), Coordinate = coordinate,
            Extra = ["VISIBLE", "\"Yes\"", "BUBBLELOC", direction.Equals("X", StringComparison.OrdinalIgnoreCase) ? "\"End\"" : "\"Start\""],
        });

    /// <summary>A length given in metres, in the model's length unit (a model in mm or in takes its covers in mm or in).</summary>
    public static double FromMetres(this StructuralModel m, double metres) =>
        metres / (CsiProMax.CsiTables.ModelTables.UnitFactor(m.Units.Length) ?? 1);

    /// <summary>A weight per volume given in kN/m³, in the model's force per length³.</summary>
    public static double FromKnPerM3(this StructuralModel m, double knPerM3)
    {
        double toN = CsiProMax.CsiTables.ModelTables.UnitFactor(m.Units.Force) ?? 1000, toM = CsiProMax.CsiTables.ModelTables.UnitFactor(m.Units.Length) ?? 1;
        return knPerM3 * 1000 / toN * toM * toM * toM;
    }

    /// <summary>The usual normal-weight concrete, 25 kN/m³, in the model's units.</summary>
    public static double ConcreteUnitWeight(this StructuralModel m) => m.FromKnPerM3(25);

    public static Material AddConcrete(this StructuralModel m, string name, double fc, double elasticModulus, double weightPerVolume)
    {
        var material = new Material
        {
            Name = name,
            Lines =
            [
                ["TYPE", "\"Concrete\"", "GRADE", E2kText.Quote(name), "WEIGHTPERVOLUME", E2kText.Number(weightPerVolume)],
                ["SYMTYPE", "\"Isotropic\"", "E", E2kText.Number(elasticModulus), "U", "0.2", "A", "0.0000099"],
                ["FC", E2kText.Number(fc)],
            ],
        };
        m.Materials.Add(material);
        return material;
    }

    /// <summary>A rectangular concrete column or beam section, with the reinforcement data ETABS keeps beside it
    /// (ETABS's defaults for a new section: designed, not checked).</summary>
    public static FrameSection AddRectangularSection(this StructuralModel m, string name, string material, double depth, double width, bool beam)
    {
        var section = new FrameSection
        {
            Name = name,
            Lines = [["MATERIAL", E2kText.Quote(material), "SHAPE", "\"Concrete Rectangular\"", "D", E2kText.Number(depth), "B", E2kText.Number(width)]],
        };
        m.FrameSections.Add(section);
        var rebar = m.Materials.Find(x => x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase))?.Name ?? DefaultRebar;
        string[] bars = ["CONCRETESECTION", E2kText.Quote(name), "LONGBARMATERIAL", E2kText.Quote(rebar), "CONFINEBARMATERIAL", E2kText.Quote(rebar)];
        // ETABS's defaults in SI (covers 60 and 40 mm, a 20 mm bar, 10 mm ties at 150 mm), in the model's own units: ETABS
        // reads these numbers in the model's units, so a model in mm or kip-in would otherwise get 0.06 mm covers.
        string L(double metres) => E2kText.Number(m.FromMetres(metres));
        string A(double squareMetres) => E2kText.Number(m.FromMetres(1) * m.FromMetres(1) * squareMetres);
        var line = beam
            ? E2kText.Line(bars, ["TYPE", "\"Beam\"", "COVERTOP", L(0.06), "COVERBOTTOM", L(0.06), "ATI", "0", "ABI", "0", "ATJ", "0", "ABJ", "0"])
            : E2kText.Line(bars, ["TYPE", "\"Column\"", "PATTERN", "\"R-5-3\"", "TRANSREINF", "\"TIES\"", "DESIGNCHECK", "\"DESIGN\"", "COVER", L(0.04),
                "LONGBARAREA", A(0.0003142), "CONFINEBARAREA", A(7.85E-05), "CONFINEBARSPACING", L(0.15), "NUMCONFINEBARS3", "3", "NUMCONFINEBARS2", "3"]);
        var concrete = m.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS");
        if (concrete is null) m.Passthrough.Add(concrete = new RawSection { Heading = "CONCRETE SECTIONS", After = "FRAME SECTIONS" });
        concrete.Lines.Add(line);
        return section;
    }

    public static ShellSection AddSlabSection(this StructuralModel m, string name, string material, double thickness)
    {
        var section = new ShellSection
        {
            Name = name,
            Lines = [["PROPTYPE", "\"Slab\"", "MATERIAL", E2kText.Quote(material), "MODELINGTYPE", "\"ShellThin\"", "SLABTYPE", "\"Slab\"", "SLABTHICKNESS", E2kText.Number(thickness)]],
        };
        m.ShellSections.Add(section);
        return section;
    }

    public static ShellSection AddWallSection(this StructuralModel m, string name, string material, double thickness)
    {
        var section = new ShellSection
        {
            Name = name,
            Lines = [["PROPTYPE", "\"Wall\"", "MATERIAL", E2kText.Quote(material), "MODELINGTYPE", "\"ShellThin\"", "WALLTHICKNESS", E2kText.Number(thickness)]],
        };
        m.ShellSections.Add(section);
        return section;
    }

    /// <summary>The plan point at (x, y), made if there is none there yet.</summary>
    public static PlanPoint PointAt(this StructuralModel m, double x, double y)
    {
        var point = m.Points.Find(p => p.Below == 0 && Math.Abs(p.X - x) <= Tolerance && Math.Abs(p.Y - y) <= Tolerance);
        if (point is not null) return point;
        point = new PlanPoint { Name = NextName(m.Points.Select(p => p.Name), ""), X = x, Y = y };
        m.Points.Add(point);
        return point;
    }

    public static LineAssign AddColumn(this StructuralModel m, double x, double y, string story, string section)
    {
        var p = m.PointAt(x, y);
        return m.AssignLine(LineKind.Column, p, p, 1, "C", story, section);
    }

    public static LineAssign AddBeam(this StructuralModel m, double x1, double y1, double x2, double y2, string story, string section)
    {
        var i = m.PointAt(x1, y1); var j = m.PointAt(x2, y2);
        if (i == j) throw new ArgumentException("A beam needs two different ends.");
        return m.AssignLine(LineKind.Beam, i, j, 0, "B", story, section);
    }

    /// <summary>A brace from (x1, y1) one story below to (x2, y2) on the story.</summary>
    public static LineAssign AddBrace(this StructuralModel m, double x1, double y1, double x2, double y2, string story, string section)
    {
        var i = m.PointAt(x1, y1); var j = m.PointAt(x2, y2);
        return m.AssignLine(LineKind.Brace, i, j, 1, "D", story, section);
    }

    private static LineAssign AssignLine(this StructuralModel m, LineKind kind, PlanPoint i, PlanPoint j, int below, string prefix, string story, string section)
    {
        m.RequireStory(story, kind == LineKind.Beam ? 0 : 1);
        var line = m.Lines.Find(l => l.Kind == kind && l.StoriesBelowI == below &&
                                     ((l.PointI == i.Name && l.PointJ == j.Name) || (kind == LineKind.Beam && below == 0 && l.PointI == j.Name && l.PointJ == i.Name)));
        if (line is null)
        {
            line = new LineObject { Label = NextName(m.Lines.Select(l => l.Label), prefix), Kind = kind, PointI = i.Name, PointJ = j.Name, StoriesBelowI = below };
            m.Lines.Add(line);
        }
        var assign = m.LineAssigns.Find(a => a.Label == line.Label && a.Story == story);
        if (assign is null)
        {
            m.LineAssigns.Add(assign = new LineAssign { Label = line.Label, Story = story, Extra = [LineAssignDefaults(kind)] });
        }
        assign.Section = section;
        return assign;
    }

    /// <summary>What ETABS itself writes on a new member's assignment: output stations and automatic meshing.</summary>
    public static List<string> LineAssignDefaults(LineKind kind) => kind == LineKind.Beam
        ? ["CARDINALPT", "8", "MAXSTASPC", "0.5", "AUTOMESH", "\"YES\"", "MESHATINTERSECTIONS", "\"YES\""]
        : ["MINNUMSTA", "3", "AUTOMESH", "\"YES\"", "MESHATINTERSECTIONS", "\"YES\""];

    /// <summary>What ETABS itself writes on a new slab's or wall's assignment.</summary>
    public static List<string> AreaAssignDefaults(AreaKind kind) => kind == AreaKind.Wall
        ? ["OBJMESHTYPE", "\"DEFAULT\"", "ADDRESTRAINT", "\"No\"", "CARDINALPOINT", "\"MIDDLE\"", "TRANSFORMSTIFFNESSFOROFFSETS", "\"No\""]
        : ["OBJMESHTYPE", "\"DEFAULT\"", "ADDRESTRAINT", "\"No\"", "CARDINALPOINT", "\"TOP\"", "TRANSFORMSTIFFNESSFOROFFSETS", "\"No\""];

    /// <summary>A floor slab on the story, corners in order around its edge.</summary>
    public static AreaAssign AddSlab(this StructuralModel m, IReadOnlyList<(double X, double Y)> corners, string story, string section, string? diaphragm = null)
    {
        if (corners.Count < 3) throw new ArgumentException("A slab needs at least 3 corners.");
        m.RequireStory(story, 0);
        var names = corners.Select(c => m.PointAt(c.X, c.Y).Name).ToList();
        return m.AssignArea(AreaKind.Floor, names, names.Select(_ => 0).ToList(), "F", story, section, diaphragm);
    }

    /// <summary>A wall between two plan points, from the story below up to the story.</summary>
    public static AreaAssign AddWall(this StructuralModel m, double x1, double y1, double x2, double y2, string story, string section)
    {
        m.RequireStory(story, 1);
        var i = m.PointAt(x1, y1).Name; var j = m.PointAt(x2, y2).Name;
        if (i == j) throw new ArgumentException("A wall needs two different ends.");
        return m.AssignArea(AreaKind.Wall, [i, j, j, i], [1, 1, 0, 0], "W", story, section, null);
    }

    /// <summary>
    /// Options > Tolerances' two inclinations in degrees (ETABS's MaxInclVertCol and MaxInclHorzBmFlr; blank is its
    /// default, 1 and 20). A frame within the first of vertical is a column, one within the second of horizontal a
    /// beam, else a brace; an area within the second of level is a floor, one within the first of upright a wall, else
    /// a ramp.
    /// </summary>
    public static (double FromVertical, double FromHorizontal) Inclinations(this StructuralModel m)
    {
        var row = m.DefinitionTables?.GetValueOrDefault("Preferences - Tolerance")?.FirstOrDefault();
        double Read(string key, double fallback) => row?.GetValueOrDefault(key) is { Length: > 0 } text &&
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 90 ? v : fallback;
        return (Read("MaxInclVertCol", 1), Read("MaxInclHorzBmFlr", 20));
    }

    /// <summary>A frame between two points, each at a story level (Draw Beam/Column/Brace in the 3-D view): a column,
    /// beam or brace by its inclination (<see cref="Inclinations"/>), one on a single level a beam. One object however
    /// many stories it spans.</summary>
    public static LineAssign AddFrame(this StructuralModel m, Xyz a, Xyz b, string section)
    {
        int la = m.LevelOf(a.Z), lb = m.LevelOf(b.Z);
        var plan = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        if (la == lb && plan <= Tolerance) throw new ArgumentException("A frame needs two different ends.");
        var (low, high) = la <= lb ? (a, b) : (b, a);
        int bottom = Math.Min(la, lb), top = Math.Max(la, lb);
        var (fromVertical, fromHorizontal) = m.Inclinations();
        var tilt = Math.Atan2(plan, Math.Abs(b.Z - a.Z)) * 180 / Math.PI; // 0 upright, 90 level
        var (kind, prefix) = la == lb || 90 - tilt <= fromHorizontal + 1e-9 ? (LineKind.Beam, "B") : tilt <= fromVertical + 1e-9 ? (LineKind.Column, "C") : (LineKind.Brace, "D");
        var i = m.PointAt(low.X, low.Y); var j = plan <= Tolerance ? i : m.PointAt(high.X, high.Y);
        return m.AssignLine(kind, i, j, top - bottom, prefix, m.Stories[top].Name, section);
    }

    /// <summary>An area on corners in one plane, each at a story level, in order round its edge (Draw Floor/Wall in the
    /// 3-D view): a floor, wall or ramp by its inclination (<see cref="Inclinations"/>), one on a single level a floor;
    /// a wall takes <paramref name="wallSection"/> when one is given. One object however many stories it spans.</summary>
    public static AreaAssign AddArea(this StructuralModel m, IReadOnlyList<Xyz> corners, string section, string? diaphragm = null, string? wallSection = null)
    {
        var levels = corners.Select(c => m.LevelOf(c.Z)).ToList();
        var plane = new WorkPlane { Points = WorkPlanes.FromCorners(corners) }; // three or more, not on one line, flat
        for (var k = 0; k < corners.Count; k++)
            for (var l = k + 1; l < corners.Count; l++)
                if (levels[k] == levels[l] && Math.Abs(corners[k].X - corners[l].X) <= Tolerance && Math.Abs(corners[k].Y - corners[l].Y) <= Tolerance)
                    throw new ArgumentException("Two of the area's corners are at one place.");
        if (levels.All(l => l == levels[0])) return m.AddSlab(corners.Select(c => (c.X, c.Y)).ToList(), m.Stories[levels[0]].Name, section, diaphragm);
        var (fromVertical, fromHorizontal) = m.Inclinations();
        var tilt = Math.Acos(Math.Min(1, Math.Abs(WorkPlanes.Normal(plane)[2]))) * 180 / Math.PI; // 0 level, 90 upright
        var kind = tilt <= fromHorizontal + 1e-9 ? AreaKind.Floor : 90 - tilt <= fromVertical + 1e-9 ? AreaKind.Wall : AreaKind.Ramp;
        var top = levels.Max();
        var names = corners.Select(c => m.PointAt(c.X, c.Y).Name).ToList();
        return m.AssignArea(kind, names, levels.Select(l => top - l).ToList(), kind == AreaKind.Wall ? "W" : "F", m.Stories[top].Name,
            kind == AreaKind.Wall && wallSection is { Length: > 0 } ? wallSection : section, null);
    }

    private static AreaAssign AssignArea(this StructuralModel m, AreaKind kind, List<string> names, List<int> below, string prefix, string story, string section, string? diaphragm)
    {
        var area = m.Areas.Find(a => a.Kind == kind && a.PointNames.SequenceEqual(names) && a.StoriesBelow.SequenceEqual(below));
        if (area is null)
        {
            area = new AreaObject { Label = NextName(m.Areas.Select(a => a.Label), prefix), Kind = kind, PointNames = names, StoriesBelow = below };
            m.Areas.Add(area);
        }
        var assign = m.AreaAssigns.Find(a => a.Label == area.Label && a.Story == story);
        if (assign is null)
        {
            m.AreaAssigns.Add(assign = new AreaAssign { Label = area.Label, Story = story, Extra = [AreaAssignDefaults(kind)] });
        }
        assign.Section = section;
        if (diaphragm is not null)
        {
            if (m.Diaphragms.TrueForAll(d => d.Name != diaphragm)) m.Diaphragms.Add(new Diaphragm { Name = diaphragm });
            assign.Diaphragm = diaphragm;
        }
        return assign;
    }

    /// <summary>A support at (x, y) on a level: fixed (all six directions) or pinned (the three translations).</summary>
    public static PointAssign AddSupport(this StructuralModel m, double x, double y, string story, bool fixedSupport = true)
    {
        m.RequireStory(story, 0);
        var point = m.PointAt(x, y);
        var assign = m.PointAssigns.Find(a => a.Point == point.Name && a.Story == story);
        if (assign is null) m.PointAssigns.Add(assign = new PointAssign { Point = point.Name, Story = story });
        assign.Restraint = fixedSupport ? "UX UY UZ RX RY RZ" : "UX UY UZ";
        return assign;
    }

    /// <summary>The load pattern types ETABS 23 offers, as it writes them. A type it does not know imports as "Other".</summary>
    public static readonly string[] LoadPatternTypes =
    [
        "Dead", "Super Dead", "Live", "Reducible Live", "Roof Live", "Pattern Live", "Pattern Live - Auto", "Notional", "Seismic",
        "Seismic (Drift)", "Wind", "Snow", "Construction", "Prestress-Final", "Prestress-Transfer", "Temperature", "Other",
    ];

    /// <summary>A load pattern and the linear static load case that runs it, as ETABS makes them together.
    /// <paramref name="type"/> is one of <see cref="LoadPatternTypes"/>.</summary>
    public static LoadPattern AddLoadPattern(this StructuralModel m, string name, string type, double selfWeight = 0)
    {
        type = LoadPatternTypes.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"ETABS has no load pattern type '{type}'. Types: {string.Join(", ", LoadPatternTypes)}.");
        if (m.LoadPatterns.Exists(p => p.Name == name)) throw new ArgumentException($"There is already a load pattern named '{name}'.");
        // The pattern brings its own load case of the same name: a case or combination named so already would make two.
        if (m.LoadCases.Exists(c => c.Name == name)) throw new ArgumentException($"There is already a load case named '{name}'; give the pattern another name.");
        if (m.Combos.Exists(c => c.Name == name)) throw new ArgumentException($"There is already a combination named '{name}'; give the pattern another name.");
        var pattern = new LoadPattern { Name = name, Type = type, SelfWeight = selfWeight };
        m.LoadPatterns.Add(pattern);
        m.LoadCases.Add(new LoadCase
        {
            Name = name,
            Lines = [["TYPE", "\"Linear Static\"", "INITCOND", "\"PRESET\""], ["LOADPAT", E2kText.Quote(name), "SF", "1"]],
        });
        return pattern;
    }

    /// <summary>A uniform gravity load on a slab (force per area), on one story.</summary>
    public static void AddAreaLoad(this StructuralModel m, string areaLabel, string story, string pattern, double value) =>
        m.AreaLoads.Add(new AreaLoad
        {
            Object = areaLabel, Story = story,
            Tokens = ["TYPE", "\"UNIFF\"", "DIR", "\"GRAV\"", "LC", E2kText.Quote(pattern), "FVAL", E2kText.Number(value)],
        });

    /// <summary>A uniform gravity load along a beam (force per length), on one story.</summary>
    public static void AddLineLoad(this StructuralModel m, string lineLabel, string story, string pattern, double value) =>
        m.LineLoads.Add(new LineLoad
        {
            Object = lineLabel, Story = story,
            Tokens = ["TYPE", "\"UNIFF\"", "DIR", "\"GRAV\"", "LC", E2kText.Quote(pattern), "FVAL", E2kText.Number(value)],
        });

    /// <summary>A linear-add combination: each case with its factor.</summary>
    public static LoadCombo AddCombo(this StructuralModel m, string name, params (string Case, double Factor)[] cases)
    {
        var combo = new LoadCombo { Name = name, Lines = [["TYPE", "\"Linear Add\""]] };
        foreach (var (loadCase, factor) in cases)
            combo.Lines.Add(["LOADCASE", E2kText.Quote(loadCase), "SF", E2kText.Number(factor)]);
        m.Combos.Add(combo);
        return combo;
    }

    private static void RequireStory(this StructuralModel m, string story, int levelsBelowNeeded)
    {
        var index = m.StoryIndex(story);
        if (index < 0) throw new ArgumentException($"There is no story named '{story}'. Stories: {string.Join(", ", m.Stories.Select(s => s.Name))}.");
        if (index < levelsBelowNeeded) throw new ArgumentException($"'{story}' is the base level: nothing can hang below it.");
    }

    /// <summary>The story whose level is at elevation z (to 1e-4 of the model's length unit).</summary>
    private static int LevelOf(this StructuralModel m, double z)
    {
        for (var i = 0; i < m.Stories.Count; i++)
            if (Math.Abs(m.Elevation(i) - z) <= 1e-4) return i;
        string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        throw new ArgumentException($"Elevation {N(z)} is not a story level: members join at story levels. The levels are: {string.Join(", ", m.Stories.Select((s, i) => $"{s.Name} {N(m.Elevation(i))}"))}.");
    }

    /// <summary>The next free name: prefix plus the smallest number above every name of that form in use.</summary>
    internal static string NextName(IEnumerable<string> used, string prefix)
    {
        var max = 0;
        foreach (var name in used)
            if (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length), out var n) && n > max) max = n;
        return prefix + (max + 1);
    }
}
