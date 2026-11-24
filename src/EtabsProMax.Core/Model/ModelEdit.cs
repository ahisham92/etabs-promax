using EtabsProMax.E2k;

namespace EtabsProMax.Model;

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
        var line = beam
            ? E2kText.Line(bars, ["TYPE", "\"Beam\"", "COVERTOP", "0.06", "COVERBOTTOM", "0.06", "ATI", "0", "ABI", "0", "ATJ", "0", "ABJ", "0"])
            : E2kText.Line(bars, ["TYPE", "\"Column\"", "PATTERN", "\"R-5-3\"", "TRANSREINF", "\"TIES\"", "DESIGNCHECK", "\"DESIGN\"", "COVER", "0.04",
                "LONGBARAREA", "0.0003142", "CONFINEBARAREA", "7.85E-05", "CONFINEBARSPACING", "0.15", "NUMCONFINEBARS3", "3", "NUMCONFINEBARS2", "3"]);
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
                                     ((l.PointI == i.Name && l.PointJ == j.Name) || (kind == LineKind.Beam && l.PointI == j.Name && l.PointJ == i.Name)));
        if (line is null)
        {
            line = new LineObject { Label = NextName(m.Lines.Select(l => l.Label), prefix), Kind = kind, PointI = i.Name, PointJ = j.Name, StoriesBelowI = below };
            m.Lines.Add(line);
        }
        var assign = m.LineAssigns.Find(a => a.Label == line.Label && a.Story == story);
        if (assign is null)
        {
            // What ETABS itself writes on a new member: output stations and automatic meshing.
            List<string> defaults = kind == LineKind.Beam
                ? ["CARDINALPT", "8", "MAXSTASPC", "0.5", "AUTOMESH", "\"YES\"", "MESHATINTERSECTIONS", "\"YES\""]
                : ["MINNUMSTA", "3", "AUTOMESH", "\"YES\"", "MESHATINTERSECTIONS", "\"YES\""];
            m.LineAssigns.Add(assign = new LineAssign { Label = line.Label, Story = story, Extra = [defaults] });
        }
        assign.Section = section;
        return assign;
    }

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
            List<string> defaults = kind == AreaKind.Wall
                ? ["OBJMESHTYPE", "\"DEFAULT\"", "ADDRESTRAINT", "\"No\"", "CARDINALPOINT", "\"MIDDLE\"", "TRANSFORMSTIFFNESSFOROFFSETS", "\"No\""]
                : ["OBJMESHTYPE", "\"DEFAULT\"", "ADDRESTRAINT", "\"No\"", "CARDINALPOINT", "\"TOP\"", "TRANSFORMSTIFFNESSFOROFFSETS", "\"No\""];
            m.AreaAssigns.Add(assign = new AreaAssign { Label = area.Label, Story = story, Extra = [defaults] });
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

    /// <summary>The next free name: prefix plus the smallest number above every name of that form in use.</summary>
    private static string NextName(IEnumerable<string> used, string prefix)
    {
        var max = 0;
        foreach (var name in used)
            if (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length), out var n) && n > max) max = n;
        return prefix + (max + 1);
    }
}
