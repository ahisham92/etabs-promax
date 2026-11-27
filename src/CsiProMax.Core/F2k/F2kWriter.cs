using System.Globalization;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.F2k;

/// <summary>What goes into a SAFE file besides the model itself.</summary>
/// <param name="Story">The floor to write (SAFE models one floor or one foundation). Null: the story with the most slab area.</param>
/// <param name="Subgrade">A modulus of subgrade reaction (force per length cubed) for every slab, for a mat. Null: no soil.</param>
/// <param name="CompressionOnly">The soil springs push only (no tension), as soil does.</param>
/// <param name="Strips">Design strips along the grid lines, layer A along X grids and layer B along Y grids.</param>
/// <param name="Version">The SAFE version written into the header.</param>
public sealed record F2kOptions(string? Story = null, double? Subgrade = null, bool CompressionOnly = true, bool Strips = true, string Version = "20.1.0");

/// <summary>What was written, for the answer to the engineer.</summary>
public sealed record F2kResult(string Text, string Story, int Slabs, int Beams, int Columns, int Walls, int Supports, int Strips, int Loads, IReadOnlyList<string> Notes)
{
    /// <summary>The tendons of the story that went in.</summary>
    public int Tendons { get; init; }
}

/// <summary>
/// Writes one floor of a model as a SAFE text file (.f2k) in the tables SAFE 20 and later read and write (File >
/// Import > SAFE .F2K), table for table and field for field as SAFE 20.1 and 23.1 export them (their own exports of
/// the ACI 318-19 PT-SL and RC-PN examples, 2026-11-26), with the fields named as the programs' "Table and Field
/// Keys.xml" names them. SAFE models one level: slabs, beams and drops on it, the columns and walls below and above it
/// as its supports, point supports, soil under a mat (area springs), design strips with their design overwrites,
/// post-tensioning tendons with their jacking and losses, loads, load cases (hyperstatic too) and combinations.
/// Coordinates are the model's own (the slab at its elevation), as SAFE writes them. Nothing is analysed here.
/// </summary>
public static class F2kWriter
{
    private static string N(double v) => CsiTableText.Number(v);

    /// <summary>PROGRAM CONTROL's ProgLevel, as SAFE writes it: "Post Tensioning" for a model with tendons, else "Standard".</summary>
    public static string ProgLevel(int tendons) => tendons > 0 ? "Post Tensioning" : "Standard";

    private static List<(string, string)> Row(params (string Key, string Value)[] pairs) => [.. pairs];

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A cardinal point (1 to 11, the .e2k's CARDINALPT) by SAFE's name for it, "10 (Centroid)".</summary>
    internal static string CardinalName(string? point)
    {
        string[] names = ["Bottom Left", "Bottom Center", "Bottom Right", "Middle Left", "Middle Center", "Middle Right", "Top Left", "Top Center", "Top Right", "Centroid", "Shear Center"];
        var n = int.TryParse(E2kText.Unquote(point ?? "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 11 ? v : 10;
        return $"{n} ({names[n - 1]})";
    }

    /// <summary>A point's place, as the writer tells points apart.</summary>
    internal static string PointKey(double x, double y, double z) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(x, 6)},{Math.Round(y, 6)},{Math.Round(z, 6)}");

    /// <summary>
    /// SAFE models one floor to a file (its own exports have no story table): every story with slabs, each as its own
    /// .f2k, so a building of many floors loses none of them. Busiest first.
    /// </summary>
    public static IReadOnlyList<F2kResult> WriteEveryFloor(StructuralModel m, F2kOptions? options = null)
    {
        options ??= new F2kOptions();
        var areas = Physical.Areas(m).ToList();
        var busiest = BusiestStory(m, areas);
        var floors = m.Stories.Select((s, i) => (s.Name, Z: m.Elevation(i)))
            .Where(s => areas.Any(a => a.Kind == AreaKind.Floor && a.Corners.All(c => Math.Abs(c.Z - s.Z) < 1e-6)))
            .Select(s => s.Name).OrderBy(n => n == busiest ? 0 : 1).ToList();
        if (floors.Count == 0) floors.Add(busiest);
        return floors.Select(f => Write(m, options with { Story = f })).ToList();
    }

    public static F2kResult Write(StructuralModel m, F2kOptions? options = null)
    {
        options ??= new F2kOptions();
        var notes = new List<string>();
        var frames = Physical.Frames(m).ToList();
        var areas = Physical.Areas(m).ToList();
        var story = options.Story ?? BusiestStory(m, areas);
        var level = m.StoryIndex(story);
        if (level < 0) throw new ArgumentException($"There is no story named '{story}'. Stories: {string.Join(", ", m.Stories.Select(s => s.Name))}.");
        story = m.Stories[level].Name;
        var z = m.Elevation(level);
        const double tol = 1e-6;
        bool At(double value) => Math.Abs(value - z) < tol;

        var slabs = areas.Where(a => a.Kind == AreaKind.Floor && a.Corners.All(c => At(c.Z))).ToList();
        var beams = frames.Where(f => f.Kind == LineKind.Beam && At(f.I.Z) && At(f.J.Z)).ToList();
        var below = frames.Where(f => f.Kind == LineKind.Column && At(Math.Max(f.I.Z, f.J.Z))).ToList();
        var above = frames.Where(f => f.Kind == LineKind.Column && At(Math.Min(f.I.Z, f.J.Z))).ToList();
        var wallsBelow = areas.Where(a => a.Kind == AreaKind.Wall && At(a.Corners.Max(c => c.Z))).ToList();
        var wallsAbove = areas.Where(a => a.Kind == AreaKind.Wall && At(a.Corners.Min(c => c.Z))).ToList();
        if (slabs.Count == 0) notes.Add($"Story {story} has no slabs, so the SAFE file has no slab areas.");

        // ---- point objects: every corner and end, at its own place (the slab at the story's elevation) ----
        var points = new List<(string Name, double X, double Y, double Z)>();
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        // A model read from SAFE's file keeps SAFE's point names, so a point keeps its name (and SAFE its GUID) on saving.
        var safeNames = F2kSource.PointNames(m, story);
        var taken = new HashSet<string>(safeNames.Values, StringComparer.OrdinalIgnoreCase);
        var next = 0;
        string Point(double x, double y, double pz)
        {
            var key = PointKey(x, y, pz);
            if (index.TryGetValue(key, out var name)) return name;
            if (!safeNames.TryGetValue(key, out name!))
            {
                if (safeNames.Count == 0) name = I(points.Count + 1);
                else { do name = I(++next); while (taken.Contains(name)); }
            }
            points.Add((name, x, y, pz));
            index[key] = name;
            return name;
        }

        // Floors and walls share SAFE's area names; beams and columns share its frame names.
        var areaNames = new List<string>();
        string AreaName(string wanted) { var n = Unique(areaNames, wanted); areaNames.Add(n); return n; }
        var frameNames = new List<string>();
        string FrameName(string wanted) { var n = Unique(frameNames, wanted); frameNames.Add(n); return n; }

        var areaRows = new List<(string Label, string Section, List<string> Points, PhysicalArea Source)>();
        foreach (var s in slabs) areaRows.Add((AreaName(s.Label), s.Section ?? "", s.Corners.Select(c => Point(c.X, c.Y, z)).ToList(), s));
        var lineRows = new List<(string Label, string Type, string Section, string I, string J, double Length, PhysicalFrame Source)>();
        foreach (var b in beams) lineRows.Add((FrameName(b.Label), "Beam", b.Section ?? "", Point(b.I.X, b.I.Y, z), Point(b.J.X, b.J.Y, z), Dist(b.I.X, b.I.Y, b.J.X, b.J.Y), b));
        var farEnds = new List<string>();
        foreach (var c in below)
        {
            var bottom = c.I.Z < c.J.Z ? c.I : c.J;
            var far = Point(bottom.X, bottom.Y, bottom.Z);
            farEnds.Add(far);
            lineRows.Add((FrameName(c.Label), "Column", c.Section ?? "", far, Point(bottom.X, bottom.Y, z), z - bottom.Z, c));
        }
        foreach (var c in above)
        {
            var top = c.I.Z > c.J.Z ? c.I : c.J;
            var far = Point(top.X, top.Y, top.Z);
            farEnds.Add(far);
            lineRows.Add((FrameName(c.Label + "A"), "Column", c.Section ?? "", Point(top.X, top.Y, z), far, top.Z - z, c));
        }
        // Walls below and above the level, by their four corners.
        var wallRows = new List<(string Label, string Section, List<string> Points)>();
        foreach (var (w, up) in wallsBelow.Select(w => (w, false)).Concat(wallsAbove.Select(w => (w, true))))
        {
            if (w.Corners.Count < 3) continue;
            var farZ = up ? w.Corners.Max(c => c.Z) : w.Corners.Min(c => c.Z);
            wallRows.Add((AreaName(w.Label + (up ? "A" : "")), w.Section ?? "", w.Corners.Select(c => Point(c.X, c.Y, c.Z)).ToList()));
            foreach (var c in w.Corners.Where(c => Math.Abs(c.Z - farZ) < tol)) farEnds.Add(Point(c.X, c.Y, c.Z));
        }

        // ---- supports: restraints on the level, and the far ends of the columns and walls ----
        var supports = new List<(string Point, string Restraint)>();
        foreach (var pa in m.PointAssigns.Where(p => p.Restraint is { Length: > 0 } && string.Equals(p.Story, story, StringComparison.OrdinalIgnoreCase)))
            if (m.Point(pa.Point) is { } pp) supports.Add((Point(pp.X, pp.Y, z - pp.Below), pa.Restraint!));
        foreach (var far in farEnds.Distinct()) supports.Add((far, "UX UY UZ RX RY RZ"));
        if (below.Count + above.Count + wallRows.Count + supports.Count == 0 && options.Subgrade is null && !slabs.Any(sl => m.AreaAssigns.Find(x => x.Label == sl.Label && x.Story == sl.Story) is { } aa && PropertyDefs.Value(aa.Extra, "SPRINGPROP") is not null))
            notes.Add("Nothing holds the slab up: no columns, walls, supports or soil. Add supports or give a subgrade modulus before analysing in SAFE.");

        // ---- design strips: along the grid lines, and the ones drawn by hand (Define > Design Strips), which replace
        // grid strips of the same name. Each strip point is a point object, as SAFE keeps them. ----
        var own = SlabDefs.Strips(m).Where(x => x.Story.Length == 0 || string.Equals(x.Story, story, StringComparison.OrdinalIgnoreCase)).ToList();
        var strips = new List<SlabDefs.Strip>();
        if (options.Strips && points.Count > 0)
        {
            strips = GridStrips(m, points.Where(p => Math.Abs(p.Z - z) < tol).ToList());
            if (strips.Count == 0 && own.Count == 0) notes.Add("The model has no grid lines, so no design strips were made. Draw strips in SAFE, or add grids first.");
            var mine = own.Select(x => x.Name).ToHashSet();
            strips.RemoveAll(x => mine.Contains(x.Name));
        }
        var grid = strips.Count;
        strips.AddRange(own);
        var stripPoints = strips.Select(st => st.Plan.Select(p => Point(p[0], p[1], z)).ToList()).ToList();

        // ---- null lines with their slab line releases, and slab rebar, of the story ----
        var nullLines = SlabDefs.NullLines(m).Where(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase))
            .Select(l => (Line: l, Name: FrameName(l.Name), I: Point(l.I[0], l.I[1], z), J: Point(l.J[0], l.J[1], z))).ToList();
        var releases = SlabDefs.LineReleases(m).Select(r => (R: r, Line: nullLines.FirstOrDefault(n => n.Line.Name == r.Line).Name)).Where(x => x.Line is not null).ToList();
        var rebars = SlabDefs.SlabRebars(m).Where(b => string.Equals(b.Story, story, StringComparison.OrdinalIgnoreCase)).ToList();

        // ---- tendons of the story ----
        var tendons = SlabDefs.Tendons(m).Where(t => string.Equals(t.Story, story, StringComparison.OrdinalIgnoreCase) && t.Plan.Count >= 2).ToList();
        var tendonPoints = tendons.Select(t => t.Plan.Select(p => Point(p[0], p[1], z)).ToList()).ToList();

        // ---- point loads, on point objects of the level ----
        var pointLoads = new List<(string Point, string Pattern, double[] Values)>();
        foreach (var l in m.PointLoads.Where(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase)))
        {
            if (!(E2kText.Value(l.Tokens, "TYPE") ?? "FORCE").Equals("FORCE", StringComparison.OrdinalIgnoreCase) || m.Point(l.Object) is not { } pp) continue;
            var values = new[] { "FX", "FY", "FZ", "MX", "MY", "MZ" }.Select(k => Num(E2kText.Value(l.Tokens, k)) ?? 0).ToArray();
            if (values.All(v => v == 0)) continue;
            pointLoads.Add((Point(pp.X, pp.Y, z - pp.Below), l.Pattern, values));
        }

        // ---- openings in the slab, and punching overwrites at points of the level ----
        var openings = SlabDefs.Openings(m).Where(o => string.Equals(o.Story, story, StringComparison.OrdinalIgnoreCase) && o.Plan.Count >= 3)
            .Select(o => (Label: AreaName(o.Name), Points: o.Plan.Select(p => Point(p[0], p[1], z)).ToList())).ToList();
        var punching = new List<(string Point, SlabDefs.Punching P)>();
        // ETABS can keep a column's punching overwrite on its bottom joint, at the story below the slab: in SAFE it is the slab's point above it.
        var storyBelow = level > 0 ? m.Stories[level - 1].Name : "Base";
        var allPunching = SlabDefs.PunchingList(m);
        var onSlab = allPunching.Where(p => string.Equals(p.Story, story, StringComparison.OrdinalIgnoreCase)).Select(p => p.Point).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allPunching.Where(p => string.Equals(p.Story, story, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(p.Story, storyBelow, StringComparison.OrdinalIgnoreCase) && !onSlab.Contains(p.Point))))
            if (m.Point(p.Point) is { } pp) punching.Add((Point(pp.X, pp.Y, string.Equals(p.Story, story, StringComparison.OrdinalIgnoreCase) ? z - pp.Below : z), p));

        // ---- what is used ----
        var usedSlab = areaRows.Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedBeam = lineRows.Where(r => r.Type == "Beam").Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedColumn = lineRows.Where(r => r.Type == "Column").Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedWall = wallRows.Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Every tendon section, used or not, as SAFE's own files list them.
        var tendonSections = SlabDefs.TendonSectionList(m);

        var tables = new List<string>();
        var units = $"{m.Units.Force}, {m.Units.Length.ToLowerInvariant()}, {m.Units.Temperature}";
        // The licence level the file asks for: Post Tensioning only when it has tendons. SAFE 23.1 on a tester's PC
        // exports at "Standard" (field/safe-etabs-exports, 2026-11-26), and a file asking for more than the licence has
        // may not open in full there.
        var control = Row(("ProgramName", "SAFE"), ("Version", options.Version), ("ProgLevel", ProgLevel(tendons.Count)), ("CurrUnits", units));
        var slabCode = PreferenceCode(m);
        if (slabCode is not null) control.Add(("ConcSlbCode", slabCode));
        tables.Add(CsiTableText.Table("PROGRAM CONTROL", [control]));

        // Grids, with the level as the model's datum.
        var belowName = level > 0 ? m.Stories[level - 1].Name : "Base";
        tables.Add(CsiTableText.Table("GRID DEFINITIONS - GENERAL", [Row(("Name", "G1"), ("Type", "Cartesian"), ("Ux", "0"), ("Uy", "0"), ("Rz", "0"),
            ("Datum Name", story), ("Story Name Below", belowName), ("Model Datum", N(z)), ("Story Height Below", N(StoryHeight(m, level))))]));
        if (m.Grids.Count > 0)
            tables.Add(CsiTableText.Table("GRID DEFINITIONS - GRID LINES", m.Grids.OrderBy(g => g.Direction).ThenBy(g => g.Coordinate).Select(g => (IReadOnlyList<(string, string)>)Row(
                ("Name", "G1"), ("Grid Line Type", g.Direction == "Y" ? "Y (Cartesian)" : "X (Cartesian)"), ("ID", g.Label), ("Ordinate", N(g.Coordinate)),
                ("Bubble Location", g.Direction == "Y" ? "Start" : "End"), ("Visible", "Yes")))));

        // Materials: the ones the used sections name, plus every concrete and rebar (SAFE designs with them) and tendon steel.
        var sectionMaterials = m.FrameSections.Where(s => usedBeam.Contains(s.Name) || usedColumn.Contains(s.Name)).Select(s => s.Material)
            .Concat(m.ShellSections.Where(s => usedSlab.Contains(s.Name) || usedWall.Contains(s.Name)).Select(s => s.Material))
            .Concat(tendonSections.Select(s => s.Material)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Is(Material x, string type) => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase);
        // Every material of the model, used or not, as SAFE's own files list them (an ETABS model's steel and tendon
        // materials are SAFE's too, and a section that comes later needs its material there).
        _ = sectionMaterials;
        var materials = m.Materials.ToList();
        static string SymType(Material x) => x.Get("SYMTYPE") ?? (x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase) || x.Type.Equals("Tendon", StringComparison.OrdinalIgnoreCase) ? "Uniaxial" : "Isotropic");
        tables.Add(CsiTableText.Table("MATERIAL PROPERTIES - GENERAL", materials.Select(x =>
        {
            var row = Row(("Material", x.Name), ("Type", x.Type.Length > 0 ? x.Type : "Concrete"), ("SymType", SymType(x)));
            if (x.Get("GRADE") is { Length: > 0 } grade) row.Add(("Grade", grade));
            return (IReadOnlyList<(string, string)>)ProgramTables.With(row, ProgramTables.Row(x, "General").Where(kv => kv.Key is "Color" or "Notes").ToDictionary());
        })));
        tables.Add(CsiTableText.Table("MATERIAL PROPERTIES - BASIC MECHANICAL PROPERTIES", materials.Select(x =>
        {
            double e = x.Number("E") ?? 0, u = x.Number("U") ?? (x.IsConcrete ? 0.2 : 0.3);
            var row = Row(("Material", x.Name), ("DensityType", "Weight"), ("UnitWeight", N(x.Number("WEIGHTPERVOLUME") ?? 0)));
            if (x.Number("MASSPERVOLUME") is { } mass) row.Add(("UnitMass", N(mass)));
            row.Add(("E1", N(e)));
            // The shear modulus of an isotropic material, as SAFE gives it: E / 2(1 + U).
            if (SymType(x) == "Isotropic") row.Add(("G12", N(e / (2 * (1 + u)))));
            row.Add(("U12", N(u)));
            row.Add(("A1", N(x.Number("A") ?? 1e-5)));
            return (IReadOnlyList<(string, string)>)row;
        })));
        void MaterialData(string table, Func<Material, bool> which, Func<Material, List<(string, string)>> row)
        {
            var list = materials.Where(which).ToList();
            if (list.Count > 0) tables.Add(CsiTableText.Table(table, list.Select(x => (IReadOnlyList<(string, string)>)row(x))));
        }
        // What each material's forms hold beyond its .e2k keywords goes into SAFE's tables of the same names as ETABS's.
        Dictionary<string, string>? Of(Material x, string table) => ProgramTables.MaterialData(x).TryGetValue(table, out var r) && r.Count > 0 ? r[0] : null;
        // Expected strengths and the stress-strain curve, as the .e2k gives them (FYE, FUE, SSTYPE, HYSTYPE, strains).
        List<(string, string)> Strengths(Material x, bool curve)
        {
            var row = Row(("Material", x.Name), ("Fy", N(x.Number("FY") ?? 0)), ("Fu", N(x.Number("FU") ?? 0)));
            if (x.Number("FYE") is { } fye) row.Add(("Fye", N(fye)));
            if (x.Number("FUE") is { } fue) row.Add(("Fue", N(fue)));
            if (!curve) return row;
            if (x.Get("SSTYPE") is { Length: > 0 } ss) row.Add(("SSCurveOpt", ss));
            if (x.Get("HYSTYPE") is { Length: > 0 } hys) row.Add(("SSHysType", hys));
            foreach (var (key, field) in new[] { ("STRAINATHARDENING", "SHard"), ("STRAINATMAXSTRESS", "SMax"), ("STRAINATRUPTURE", "SRup"), ("FINALSLOPE", "FinalSlope") })
                if (x.Number(key) is { } v) row.Add((field, N(v)));
            return row;
        }
        MaterialData("MATERIAL PROPERTIES - STEEL DATA", x => Is(x, "Steel"), x => ProgramTables.With(Strengths(x, true), Of(x, "Steel Data")));
        MaterialData("MATERIAL PROPERTIES - CONCRETE DATA", x => x.IsConcrete, x => ProgramTables.With(Row(("Material", x.Name), ("Fc", N(x.Number("FC") ?? 0)), ("LtWtConc", "No")), Of(x, "Concrete Data") is { } c ? ProgramTables.WithoutSapOnly("Concrete Data", c) : null));
        MaterialData("MATERIAL PROPERTIES - REBAR DATA", x => Is(x, "Rebar"), x => ProgramTables.With(Strengths(x, true), Of(x, "Rebar Data")));
        MaterialData("MATERIAL PROPERTIES - TENDON DATA", x => Is(x, "Tendon"), x => ProgramTables.With(Row(("Material", x.Name), ("Fy", N(x.Number("FY") ?? 0)), ("Fu", N(x.Number("FU") ?? 0))), Of(x, "Tendon Data")));
        // SAFE's material tables are those six (its own .f2k files): the material's other forms (damping, time dependence,
        // nonlinear hysteresis parameters) stay in the .cpm for ETABS and SAP2000.

        // Frame sections, by their shape.
        // Every frame section of the model, used or not (SAFE's own files list them all, steel library sections too).
        var frameProps = m.FrameSections.ToList();
        // ETABS names a section's library file once, on the first section from it; the ones after it are from it too.
        var fileOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lastFile = "";
        foreach (var s in frameProps)
        {
            if (s.Get("FILE") is { Length: > 0 } f) lastFile = f;
            else if (lastFile.Length > 0 && PropertyDefs.Shape(s.Shape) is null && !s.Shape.Contains("Rectangular", StringComparison.OrdinalIgnoreCase) && !s.Shape.Contains("Circle", StringComparison.OrdinalIgnoreCase)) { fileOf[s.Name] = lastFile; continue; }
            if (s.Get("FILE") is { Length: > 0 } ownFile) fileOf[s.Name] = ownFile;
        }
        List<(string, string)> FrameMods(FrameSection s)
        {
            var fm = PropertyDefs.Modifiers(s.Lines, PropertyDefs.FrameModifiers);
            return [("Area Modifier", N(fm["AMOD"])), ("As2 Modifier", N(fm["A2MOD"])), ("As3 Modifier", N(fm["A3MOD"])), ("J Modifier", N(fm["JMOD"])),
                ("I22 Modifier", N(fm["I2MOD"])), ("I33 Modifier", N(fm["I3MOD"])), ("Mass Modifier", N(fm["MMOD"])), ("Weight Modifier", N(fm["WMOD"]))];
        }
        var shapeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (frameProps.Count > 0)
        {
            var rect = new List<IReadOnlyList<(string, string)>>();
            var circle = new List<IReadOnlyList<(string, string)>>();
            var steel = new List<IReadOnlyList<(string, string)>>();
            foreach (var s in frameProps)
            {
                var design = usedColumn.Contains(s.Name) || (!usedBeam.Contains(s.Name) && string.Equals(s.Get("DESIGNTYPE") ?? s.Get("CONCRETETYPE"), "Column", StringComparison.OrdinalIgnoreCase)) ? "Column" : "Beam";
                if (s.Shape.Contains("Circle", StringComparison.OrdinalIgnoreCase))
                {
                    shapeOf[s.Name] = "Concrete Circle";
                    circle.Add([.. Row(("Name", s.Name), ("Material", s.Material), ("From File?", "No"), ("Diameter", N(s.Number("D") ?? 0)), ("Design Type", design)), .. FrameMods(s)]);
                }
                else if (fileOf.TryGetValue(s.Name, out var file))
                {
                    shapeOf[s.Name] = "Steel I/Wide Flange";
                    steel.Add([.. Row(("Name", s.Name), ("Material", s.Material), ("From File?", "Yes"), ("File Name", file), ("Section in File", s.Shape)), .. FrameMods(s)]);
                }
                else if (s.Shape.Contains("Steel I", StringComparison.OrdinalIgnoreCase))
                {
                    shapeOf[s.Name] = "Steel I/Wide Flange";
                    double b = s.Number("B") ?? 0, tf = s.Number("TF") ?? 0;
                    steel.Add([.. Row(("Name", s.Name), ("Material", s.Material), ("From File?", "No"), ("Total Depth", N(s.Number("D") ?? 0)), ("Top Flange Width", N(b)), ("Top Flange Thickness", N(tf)),
                        ("Web Thickness", N(s.Number("TW") ?? 0)), ("Bottom Flange Width", N(s.Number("BBOTTOM") ?? b)), ("Bottom Flange Thickness", N(s.Number("TFBOTTOM") ?? tf))), .. FrameMods(s)]);
                }
                else
                {
                    if (!s.Shape.Contains("Rectangular", StringComparison.OrdinalIgnoreCase))
                        notes.Add($"The frame section {s.Name} ({s.Shape}) went in as a concrete rectangle of its depth and width; give it its own shape in SAFE.");
                    shapeOf[s.Name] = "Concrete Rectangular";
                    rect.Add([.. Row(("Name", s.Name), ("Material", s.Material), ("From File?", "No"), ("Depth", N(s.Number("D") ?? 0)), ("Width", N(s.Number("B") ?? s.Number("D") ?? 0)), ("Design Type", design)), .. FrameMods(s)]);
                }
            }
            tables.Add(CsiTableText.Table("FRAME SECTION PROPERTY DEFINITIONS - SUMMARY", frameProps.Select(s => (IReadOnlyList<(string, string)>)Row(("Name", s.Name), ("Shape", shapeOf[s.Name])))));
            if (steel.Count > 0) tables.Add(CsiTableText.Table("FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE", steel));
            if (rect.Count > 0) tables.Add(CsiTableText.Table("FRAME SECTION PROPERTY DEFINITIONS - CONCRETE RECTANGULAR", rect));
            if (circle.Count > 0) tables.Add(CsiTableText.Table("FRAME SECTION PROPERTY DEFINITIONS - CONCRETE CIRCLE", circle));
        }
        if (tendonSections.Count > 0)
            tables.Add(CsiTableText.Table("TENDON SECTION PROPERTIES", tendonSections.Select(s => (IReadOnlyList<(string, string)>)Row(("Name", s.Name), ("Material", s.Material), ("StrandArea", N(s.StrandArea))))));

        // Slab and wall properties: slabs, drops, mats and footings by the slab type ETABS gives them.
        static bool IsDeck(ShellSection s) => string.Equals(s.Get("PROPTYPE"), "Deck", StringComparison.OrdinalIgnoreCase);
        // Every slab, deck and wall property of the model, used or not, as SAFE's own files list them.
        var deckProps = m.ShellSections.Where(s => IsDeck(s) && !s.IsWall).ToList();
        var slabProps = m.ShellSections.Where(s => !IsDeck(s) && (!s.IsWall || usedSlab.Contains(s.Name))).ToList();
        List<(string, string)> ShellMods(ShellSection s)
        {
            var sm = PropertyDefs.Modifiers(s.Lines, PropertyDefs.ShellModifiers);
            return [("f11 Modifier", N(sm["F11MOD"])), ("f22 Modifier", N(sm["F22MOD"])), ("f12 Modifier", N(sm["F12MOD"])), ("m11 Modifier", N(sm["M11MOD"])), ("m22 Modifier", N(sm["M22MOD"])),
                ("m12 Modifier", N(sm["M12MOD"])), ("v13 Modifier", N(sm["V13MOD"])), ("v23 Modifier", N(sm["V23MOD"])), ("Mass Modifier", N(sm["MMOD"])), ("Weight Modifier", N(sm["WMOD"]))];
        }
        // Decks, by SAFE's own fields (the .e2k's DECK keywords).
        if (deckProps.Count > 0)
            tables.Add(CsiTableText.Table("DECK PROPERTY DEFINITIONS", deckProps.Select(s => (IReadOnlyList<(string, string)>)Row(("Name", s.Name), ("Deck Type", s.Get("DECKTYPE") ?? "Filled"),
                ("Slab Material", s.Get("CONCMATERIAL") ?? ""), ("Deck Material", s.Get("DECKMATERIAL") ?? ""), ("Slab Depth", N(s.Number("DECKSLABDEPTH") ?? 0)), ("Rib Depth", N(s.Number("DECKRIBDEPTH") ?? 0)),
                ("Rib Width Top", N(s.Number("DECKRIBWIDTHTOP") ?? 0)), ("Rib Width Bottom", N(s.Number("DECKRIBWIDTHBOTTOM") ?? 0)), ("Rib Spacing", N(s.Number("DECKRIBSPACING") ?? 0)),
                ("Deck Shear Thickness", N(s.Number("DECKSHEARTHICKNESS") ?? 0)), ("Deck Unit Weight", N(s.Number("DECKUNITWEIGHT") ?? 0)), ("Shear Stud Diameter", N(s.Number("SHEARSTUDDIAM") ?? 0)),
                ("Shear Stud Height", N(s.Number("SHEARSTUDHEIGHT") ?? 0)), ("Shear Stud Tensile Strength", N(s.Number("SHEARSTUDFU") ?? 0))).Concat(ShellMods(s)).ToList())));
        var wallProps = m.ShellSections.Where(s => (s.IsWall || usedWall.Contains(s.Name)) && !usedSlab.Contains(s.Name)).ToList();
        if (slabProps.Count + wallProps.Count > 0)
            tables.Add(CsiTableText.Table("AREA SECTION PROPERTY DEFINITIONS - SUMMARY", slabProps.Select(s => (IReadOnlyList<(string, string)>)Row(("Name", s.Name), ("Type", "Slab"), ("Element Type", ElementType(s, "Shell-Thick")), ("Material", s.Material), ("Total Thickness", N(s.Thickness ?? 0))))
                .Concat(wallProps.Select(s => (IReadOnlyList<(string, string)>)Row(("Name", s.Name), ("Type", "Wall"), ("Element Type", ElementType(s, "Shell-Thin")), ("Material", s.Material), ("Total Thickness", N(s.Thickness ?? 0)))))));
        if (slabProps.Count > 0)
            tables.Add(CsiTableText.Table("SLAB PROPERTY DEFINITIONS", slabProps.Select(s =>
            {
                var row = Row(("Name", s.Name), ("Modeling Type", ElementType(s, "Shell-Thick")), ("Property Type", SlabType(s)), ("Material", s.Material));
                if (s.Number("OVERALLDEPTH") is { } depth) row.Add(("Overall Depth", N(depth)));
                row.Add(("Slab Thickness", N(s.Thickness ?? 0)));
                if (s.Number("SLABRIBSPACING") is { } spacing)
                {
                    row.Add(("Rib Width at Top", N(s.Number("SLABRIBWIDTHTOP") ?? 0)));
                    row.Add(("Rib Width at Bottom", N(s.Number("SLABRIBWIDTHBOTTOM") ?? 0)));
                    row.Add((s.Number("SLABRIBPARALLELTOAXIS") == 2 ? "Rib Spacing 2" : "Rib Spacing 1", N(spacing)));
                }
                if (s.Get("ONEWAYLOADDIST") is { Length: > 0 } oneWay) row.Add(("One Way Load Distribution?", oneWay.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No"));
                row.AddRange(ShellMods(s));
                return (IReadOnlyList<(string, string)>)row;
            })));
        if (wallProps.Count > 0)
            tables.Add(CsiTableText.Table("WALL PROPERTY DEFINITIONS - SPECIFIED", wallProps.Select(s => (IReadOnlyList<(string, string)>)Row(
                ("Name", s.Name), ("Modeling Type", ElementType(s, "Shell-Thin")), ("Material", s.Material), ("Wall Thickness", N(s.Thickness ?? s.Number("WALLTHICKNESS") ?? 0)), ("Include Auto Rigid Zone?", "No")).Concat(ShellMods(s)).ToList())));

        // Soil: one subgrade modulus under every slab when asked for, else each slab's own area spring (Assign > Shell >
        // Area Springs, its U3 as the modulus). SAFE 20 keeps soil as area springs.
        var soilOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var soils = new List<IReadOnlyList<(string, string)>>();
        var nonlinear = options.CompressionOnly ? "Compression Only" : "Linear";
        IReadOnlyList<(string, string)> Soil(string name, double k) => Row(("Name", name), ("Stiffness Option", "User Specified"), ("Stiffnes U1", "0"), ("Stiffnes U2", "0"), ("Stiffnes U3", N(k)), ("Nonlinear Option for U3", nonlinear));
        if (options.Subgrade is { } subgrade)
        {
            soils.Add(Soil("SOIL1", subgrade));
            foreach (var a in areaRows) soilOf[a.Label] = "SOIL1";
        }
        else
        {
            var springDefs = PropertyDefs.Springs(m).Where(d => d.Kind == "area").ToList();
            foreach (var row in areaRows)
            {
                var s = row.Source;
                var assign = m.AreaAssigns.Find(x => x.Label == s.Label && x.Story == s.Story);
                if (assign is null || PropertyDefs.Value(assign.Extra, "SPRINGPROP") is not { } sp) continue;
                sp = E2kText.Unquote(sp);
                var def = springDefs.Find(d => d.Name == sp);
                if (def is null || def.Values.GetValueOrDefault("U3") <= 0) { notes.Add($"Slab {s.Label} has the area spring {sp} with no vertical stiffness (U3); no soil was put under it."); continue; }
                if (!soils.Exists(r => r[0].Item2 == sp)) soils.Add(Soil(sp, def.Values["U3"]));
                soilOf[row.Label] = sp;
            }
        }
        if (soils.Count > 0) tables.Add(CsiTableText.Table("SPRING PROPERTY DEFINITIONS - AREA SPRINGS", soils));

        // Loads: patterns, their cases (linear static and hyperstatic) and the combinations.
        var patternNames = m.LoadPatterns.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        tables.Add(CsiTableText.Table("LOAD PATTERN DEFINITIONS", m.LoadPatterns.Select(p => (IReadOnlyList<(string, string)>)Row(("Name", p.Name), ("Is Auto Load", "No"), ("Type", PatternType(p.Type)), ("Self Weight Multiplier", N(p.SelfWeight))))));
        var statics = new List<(string Name, List<(string Pattern, double Sf)> Loads)>();
        var hyper = new List<(string Name, string Base, string Support, double Sf)>();
        // Modal (eigen) cases, in SAFE 20's own table and fields (from SAFE 20's export, field/safe-etabs-exports).
        var modal = new List<(string Name, int Max, int Min)>();
        foreach (var c in m.LoadCases)
        {
            var type = AnalysisDefs.CaseType(c);
            if (type == "Linear Static")
                statics.Add((c.Name, c.Lines.Where(l => E2kText.Value(l, "LOADPAT") is not null).Select(l => (E2kText.Value(l, "LOADPAT")!, Num(E2kText.Value(l, "SF")) ?? 1)).ToList()));
            else if (type == "Modal - Eigen")
                modal.Add((c.Name, (int)(c.Lines.Select(l => Num(E2kText.Value(l, "MAXMODES"))).FirstOrDefault(v => v is not null) ?? 12),
                    (int)(c.Lines.Select(l => Num(E2kText.Value(l, "MINMODES"))).FirstOrDefault(v => v is not null) ?? 1)));
            else if (type == "Hyperstatic")
                hyper.Add((c.Name, c.Lines.Select(l => E2kText.Value(l, "HYPERSTATICCASE")).FirstOrDefault(v => v is not null) ?? "",
                    c.Lines.Select(l => E2kText.Value(l, "HYPERSTATICSUPPORT")).FirstOrDefault(v => v is not null) ?? "Springs", c.Lines.Select(l => Num(E2kText.Value(l, "HYPERSTATICSF"))).FirstOrDefault(v => v is not null) ?? 1));
        }
        // A pattern with no case of its own (an older model) still gets its linear static case, as SAFE makes one.
        foreach (var p in m.LoadPatterns.Where(p => !m.LoadCases.Exists(c => c.Name == p.Name))) statics.Add((p.Name, [(p.Name, 1)]));
        var other = m.LoadCases.Where(c => AnalysisDefs.CaseType(c) is not ("Linear Static" or "Hyperstatic" or "Modal - Eigen")).ToList();
        // Not yet in SAFE's own table and field names (no SAFE export with them to copy from): named, never dropped silently.
        if (other.Count > 0) notes.Add($"{other.Count} load case(s) of a kind this .f2k does not write yet ({string.Join(", ", other.Select(c => $"{c.Name}: {AnalysisDefs.CaseType(c)}").Take(6))}{(other.Count > 6 ? ", ..." : "")}); they are in the .e2k and .cpm.");
        var caseNames = statics.Select(c => c.Name).Concat(hyper.Select(h => h.Name)).Concat(modal.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        if (modal.Count > 0)
            tables.Add(CsiTableText.Table("MASS SOURCE DEFINITION", [Row(("Name", "MsSrc1"), ("Is Default", "Yes"), ("Include Lateral Mass?", "Yes"), ("Include Vertical Mass?", "No"),
                ("Lump Mass?", "Yes"), ("Source Self Mass?", "Yes"), ("Source Added Mass?", "Yes"), ("Source Load Patterns?", "No"), ("Move Mass Centroid?", "No"))]));
        if (statics.Count + hyper.Count + modal.Count > 0)
            tables.Add(CsiTableText.Table("LOAD CASE DEFINITIONS - SUMMARY", statics.Select(c => (IReadOnlyList<(string, string)>)Row(("Name", c.Name), ("Type", "Linear Static")))
                .Concat(modal.Select(c => (IReadOnlyList<(string, string)>)Row(("Name", c.Name), ("Type", "Modal - Eigen"))))
                .Concat(hyper.Select(h => (IReadOnlyList<(string, string)>)Row(("Name", h.Name), ("Type", "Hyperstatic"))))));
        if (modal.Count > 0)
            tables.Add(CsiTableText.Table("MODAL CASE DEFINITIONS - EIGEN", modal.Select(c => (IReadOnlyList<(string, string)>)Row(("Name", c.Name), ("Exclude Group", "None"), ("Mass Source", "MsSrc1"),
                ("Initial Condition", "Unstressed"), ("Max Modes", I(c.Max)), ("Min Modes", I(c.Min)), ("Freq Shift", "0"), ("Cutoff Freq", "0"), ("Convergence Tol", "1E-09"), ("Auto Shift?", "Yes"), ("Design Type", "Program Determined")))));
        if (statics.Count > 0)
            tables.Add(CsiTableText.Table("LOAD CASE DEFINITIONS - LINEAR STATIC", statics.SelectMany(c => c.Loads.Select((l, n) => (IReadOnlyList<(string, string)>)(n == 0
                ? Row(("Name", c.Name), ("Exclude Group", "None"), ("Initial Condition", "Unstressed"), ("Load Type", "Load"), ("Load Name", l.Pattern), ("Load SF", N(l.Sf)), ("Design Type", "Program Determined"))
                : Row(("Name", c.Name), ("Load Type", "Load"), ("Load Name", l.Pattern), ("Load SF", N(l.Sf))))))));
        if (hyper.Count > 0)
            tables.Add(CsiTableText.Table("LOAD CASE DEFINITIONS - HYPERSTATIC", hyper.Select(h => (IReadOnlyList<(string, string)>)Row(("Name", h.Name), ("Base Case", h.Base), ("Support Type", h.Support), ("Relative SF", N(h.Sf)), ("Design Type", "Program Determined")))));

        var combos = m.Combos.Select(c => (c.Name, Type: c.Lines.Select(l => E2kText.Value(l, "TYPE")).FirstOrDefault(t => t is not null) ?? "Linear Add",
            Slab: c.Lines.Select(l => E2kText.Value(l, "COMBOTYPE")).FirstOrDefault(t => t is not null),
            Items: c.Lines.Select(l => (Case: E2kText.Value(l, "LOADCASE") ?? E2kText.Value(l, "LOADCOMBO"), Sf: Num(E2kText.Value(l, "SF")))).Where(i => i.Case is not null && i.Sf is not null).ToList())).Where(c => c.Items.Count > 0).ToList();
        var comboNames = combos.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in combos)
            foreach (var i in c.Items.Where(i => !caseNames.Contains(i.Case!) && !comboNames.Contains(i.Case!)))
                notes.Add($"The combination {c.Name} names {i.Case}, a load case the SAFE file does not have; SAFE will leave it out or refuse the combination.");
        if (combos.Count > 0)
        {
            tables.Add(CsiTableText.Table("LOAD COMBINATION DEFINITIONS", combos.SelectMany(c => c.Items.Select((i, n) => (IReadOnlyList<(string, string)>)(n == 0
                ? Row(("Name", c.Name), ("Type", c.Type), ("Is Auto", "No"), ("Load Name", i.Case!), ("SF", N(i.Sf!.Value)))
                : Row(("Name", c.Name), ("Load Name", i.Case!), ("SF", N(i.Sf!.Value))))))));
            // Slab design takes the combinations ETABS marked for it with their kind; with none marked, every one as strength.
            var marked = combos.Where(c => c.Slab is not null).ToList();
            tables.Add(CsiTableText.Table("CONCRETE SLAB DESIGN LOAD COMBINATION DATA", (marked.Count > 0 ? marked : combos).Select(c => (IReadOnlyList<(string, string)>)Row(("Combo Type", c.Slab ?? "Strength"), ("Combo Name", c.Name)))));
        }

        // Objects
        tables.Add(CsiTableText.Table("POINT OBJECT CONNECTIVITY", points.Select(p => (IReadOnlyList<(string, string)>)Row(("UniqueName", p.Name), ("Is Auto Point", "No"), ("IsSpecial", "No"), ("X", N(p.X)), ("Y", N(p.Y)), ("Z", N(p.Z))))));
        var columns = lineRows.Where(l => l.Type == "Column").ToList();
        if (columns.Count > 0)
            tables.Add(CsiTableText.Table("COLUMN OBJECT CONNECTIVITY", columns.Select(l => (IReadOnlyList<(string, string)>)Row(("Unique Name", l.Label), ("UniquePtI", l.I), ("UniquePtJ", l.J), ("Length", N(l.Length))))));
        var beamRows = lineRows.Where(l => l.Type == "Beam").ToList();
        if (beamRows.Count > 0)
            tables.Add(CsiTableText.Table("BEAM OBJECT CONNECTIVITY", beamRows.Select(l => (IReadOnlyList<(string, string)>)Row(("Unique Name", l.Label), ("UniquePtI", l.I), ("UniquePtJ", l.J), ("Length", N(l.Length))))));
        if (areaRows.Count > 0)
            tables.Add(CsiTableText.Table("FLOOR OBJECT CONNECTIVITY", areaRows.Select(a =>
            {
                var row = Row(("Unique Name", a.Label));
                for (var i = 0; i < a.Points.Count; i++) row.Add(("UniquePt" + I(i + 1), a.Points[i]));
                return (IReadOnlyList<(string, string)>)row;
            })));
        if (wallRows.Count > 0)
            tables.Add(CsiTableText.Table("WALL OBJECT CONNECTIVITY", wallRows.Select(w =>
            {
                var row = Row(("UniqueName", w.Label));
                for (var i = 0; i < w.Points.Count; i++) row.Add(("UniquePt" + I(i + 1), w.Points[i]));
                return (IReadOnlyList<(string, string)>)row;
            })));
        if (nullLines.Count > 0)
            tables.Add(CsiTableText.Table("NULL LINE OBJECT CONNECTIVITY", nullLines.Select(l => (IReadOnlyList<(string, string)>)Row(("Unique Name", l.Name), ("UniquePtI", l.I), ("UniquePtJ", l.J)))));
        if (openings.Count > 0)
            tables.Add(CsiTableText.Table("NULL AREA OBJECT CONNECTIVITY", openings.Select(o =>
            {
                var row = Row(("Unique Name", o.Label));
                for (var i = 0; i < o.Points.Count; i++) row.Add(("UniquePt" + I(i + 1), o.Points[i]));
                return (IReadOnlyList<(string, string)>)row;
            })));
        if (tendons.Count > 0)
            tables.Add(CsiTableText.Table("TENDON OBJECT CONNECTIVITY", tendons.SelectMany((t, k) => TendonRows(t, tendonPoints[k], z))));
        if (strips.Count > 0)
            tables.Add(CsiTableText.Table("STRIP OBJECT CONNECTIVITY", strips.SelectMany((st, k) =>
            {
                var pts = stripPoints[k];
                var rows = new List<IReadOnlyList<(string, string)>>
                {
                    Row(("Name", st.Name), ("Number Segments", I(pts.Count - 1)), ("Strip Start Point", pts[0]), ("Segment End Point", pts[1]),
                        ("Start Width Left", N(st.WidthLeft)), ("Start Width Right", N(st.WidthRight)), ("End Width Left", N(st.WidthLeft)), ("End Width Right", N(st.WidthRight)), ("Auto Widen", "No"), ("Layer", st.Layer)),
                };
                // A strip of more segments: a row for each further segment end, with its widths.
                for (var n = 2; n < pts.Count; n++)
                    rows.Add(Row(("Name", st.Name), ("Segment End Point", pts[n]), ("End Width Left", N(st.WidthLeft)), ("End Width Right", N(st.WidthRight))));
                return rows;
            })));

        // Assignments
        if (supports.Count > 0)
            tables.Add(CsiTableText.Table("JOINT ASSIGNMENTS - RESTRAINTS", supports.DistinctBy(s => s.Point).Select(s =>
            {
                var dofs = s.Restraint.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(d => d.ToUpperInvariant()).ToHashSet();
                return (IReadOnlyList<(string, string)>)Row(("UniqueName", s.Point), ("UX", Yes(dofs, "UX")), ("UY", Yes(dofs, "UY")), ("UZ", Yes(dofs, "UZ")), ("RX", Yes(dofs, "RX")), ("RY", Yes(dofs, "RY")), ("RZ", Yes(dofs, "RZ")));
            })));
        if (lineRows.Count > 0)
            tables.Add(CsiTableText.Table("FRAME ASSIGNMENTS - SECTION PROPERTIES", lineRows.Where(l => l.Section.Length > 0).Select(l => (IReadOnlyList<(string, string)>)Row(
                ("UniqueName", l.Label), ("Shape", shapeOf.GetValueOrDefault(l.Section, "Concrete Rectangular")), ("Auto Select List", "N.A."), ("Section Property", l.Section)))));
        // Each beam's and column's end offsets and insertion point, as SAFE writes them for every frame.
        LineAssign? AssignOf(PhysicalFrame f) => m.LineAssigns.Find(x => x.Label == f.Label && string.Equals(x.Story, f.Story, StringComparison.OrdinalIgnoreCase));
        if (lineRows.Count > 0)
        {
            tables.Add(CsiTableText.Table("FRAME ASSIGNMENTS - END LENGTH OFFSETS", lineRows.Select(l =>
            {
                var x = AssignOf(l.Source)?.Extra ?? [];
                string? oi = PropertyDefs.Value(x, "LENGTHOFFI"), oj = PropertyDefs.Value(x, "LENGTHOFFJ"), rz = PropertyDefs.Value(x, "RIGIDZONE");
                return (IReadOnlyList<(string, string)>)Row(("UniqueName", l.Label), ("Offset Option", oi is null && oj is null && rz is null ? "Auto" : "User"),
                    ("Offset I", oi ?? "0"), ("Offset J", oj ?? "0"), ("Rigid Factor", rz ?? "0"), ("Self Weight Option", "Auto"));
            })));
            tables.Add(CsiTableText.Table("FRAME ASSIGNMENTS - INSERTION POINT", lineRows.Select(l =>
            {
                var a = AssignOf(l.Source);
                var ip = a is null ? new Dictionary<string, string>() : AssignTables.Row(a, AssignTables.Insertion);
                return (IReadOnlyList<(string, string)>)Row(("UniqueName", l.Label), ("Cardinal Point", CardinalName(a is null ? null : PropertyDefs.Value(a.Extra, "CARDINALPT"))),
                    ("Mirror2", ip.GetValueOrDefault("Mirror2") ?? "No"), ("Mirror3", ip.GetValueOrDefault("Mirror3") ?? "No"), ("No Transform Stiffness", ip.GetValueOrDefault("NoTransform") ?? "Yes"));
            })));
        }
        if (areaRows.Count + wallRows.Count + openings.Count > 0)
            tables.Add(CsiTableText.Table("AREA ASSIGNMENTS - SECTION PROPERTIES", areaRows.Where(a => a.Section.Length > 0).Select(a => (IReadOnlyList<(string, string)>)Row(("UniqueName", a.Label), ("Section Property", a.Section), ("Property Type", "Slab")))
                .Concat(wallRows.Where(w => w.Section.Length > 0).Select(w => (IReadOnlyList<(string, string)>)Row(("UniqueName", w.Label), ("Section Property", w.Section), ("Property Type", "Wall"))))
                .Concat(openings.Select(o => (IReadOnlyList<(string, string)>)Row(("UniqueName", o.Label), ("Section Property", "Opening"), ("Property Type", "Opening"))))));
        if (soilOf.Count > 0)
            tables.Add(CsiTableText.Table("AREA ASSIGNMENTS - AREA SPRINGS", areaRows.Where(a => soilOf.ContainsKey(a.Label)).Select(a => (IReadOnlyList<(string, string)>)Row(("UniqueName", a.Label), ("Spring Property", soilOf[a.Label])))));
        if (tendons.Count > 0)
        {
            tables.Add(CsiTableText.Table("TENDON ASSIGNMENTS - PROPERTIES", tendons.Select(t => (IReadOnlyList<(string, string)>)Row(("Name", t.Name), ("Tendon Property", t.Section), ("Number Strands", I(t.Strands)), ("Bonding Options", t.Bonded ? "Bonded" : "Unbonded")))));
            var jacked = tendons.Where(t => t.Jack is not null).ToList();
            if (jacked.Count > 0)
                tables.Add(CsiTableText.Table("TENDON LOAD ASSIGNMENTS - JACKING STRESS", jacked.Select(t => (IReadOnlyList<(string, string)>)Row(("Name", t.Name), ("Transfer Load Pattern", t.Jack!.TransferPattern),
                    ("Final Load Pattern", t.Jack.FinalPattern), ("Jacking Stress", N(t.Jack.Stress)), ("Jacking Location", SlabDefs.JackEnds.FirstOrDefault(j => j.Key == t.Jack.End || j.E2k == t.Jack.End).F2k ?? "J End")))));
            var lost = tendons.Where(t => t.Loss is not null).ToList();
            if (lost.Count > 0)
                tables.Add(CsiTableText.Table("TENDON LOAD ASSIGNMENTS - LOSS OPTIONS", lost.Select(t => (IReadOnlyList<(string, string)>)(t.Loss!.Method switch
                {
                    "Percent" => Row(("Name", t.Name), ("Loss Method", "Percent"), ("Stressing Loss", N(t.Loss.Stressing)), ("Long Term Loss", N(t.Loss.LongTerm))),
                    "Detailed" => Row(("Name", t.Name), ("Loss Method", "Detailed"), ("Curvature Coefficient", N(t.Loss.Curvature)), ("Wobble Coefficient", N(t.Loss.Wobble)),
                        ("Anchorage Set Slip", N(t.Loss.AnchorSlip)), ("Shortening Stress", N(t.Loss.ElasticShortening)), ("Creep Stress", N(t.Loss.Creep)),
                        ("Shrinkage Stress", N(t.Loss.Shrinkage)), ("Relaxation Stress", N(t.Loss.Relaxation))),
                    _ => Row(("Name", t.Name), ("Loss Method", "Fixed"), ("Stressing Loss Fixed", N(t.Loss.Stressing)), ("Long Term Loss Fixed", N(t.Loss.LongTerm))),
                }))));
        }

        // Loads on objects
        var loads = 0;
        var areaOf = areaRows.ToDictionary(a => a.Source.Label, a => a.Label, StringComparer.Ordinal);
        var surface = LoadSets.Expanded(m).Where(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase) && areaOf.ContainsKey(l.Object))
            .Select(l => (Area: areaOf[l.Object], l.Pattern, Value: Num(E2kText.Value(l.Tokens, "FVAL")), Type: E2kText.Value(l.Tokens, "TYPE") ?? "")).Where(l => l.Value is not null && l.Type.Equals("UNIFF", StringComparison.OrdinalIgnoreCase)).ToList();
        if (surface.Count > 0)
            tables.Add(CsiTableText.Table("AREA LOAD ASSIGNMENTS - UNIFORM", surface.Select(l => (IReadOnlyList<(string, string)>)Row(("UniqueName", l.Area), ("Load Pattern", l.Pattern), ("Direction", "Gravity"), ("Load", N(l.Value!.Value))))));
        loads += surface.Count;
        var beamOf = lineRows.Where(l => l.Type == "Beam").ToDictionary(l => l.Source.Label, l => l.Label, StringComparer.Ordinal);
        var lineLoads = m.LineLoads.Where(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase) && beamOf.ContainsKey(l.Object))
            .Select(l => (Beam: beamOf[l.Object], l.Pattern, Value: Num(E2kText.Value(l.Tokens, "FVAL")), Type: E2kText.Value(l.Tokens, "TYPE") ?? "")).Where(l => l.Value is not null && l.Type.Equals("UNIFF", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lineLoads.Count > 0)
            tables.Add(CsiTableText.Table("FRAME LOADS ASSIGNMENTS - DISTRIBUTED", lineLoads.Select(l => (IReadOnlyList<(string, string)>)Row(("UniqueName", l.Beam), ("Load Pattern", l.Pattern), ("Load Type", "Force"), ("Direction", "Gravity"),
                ("Distance Type", "Relative"), ("Relative Distance A", "0"), ("Relative Distance B", "1"), ("Force A", N(l.Value!.Value)), ("Force B", N(l.Value!.Value))))));
        loads += lineLoads.Count;
        if (pointLoads.Count > 0)
            tables.Add(CsiTableText.Table("JOINT LOADS ASSIGNMENTS - FORCE", pointLoads.Select(l => (IReadOnlyList<(string, string)>)Row(("UniqueName", l.Point), ("Load Pattern", l.Pattern),
                ("FX", N(l.Values[0])), ("FY", N(l.Values[1])), ("FZ", N(l.Values[2])), ("MX", N(l.Values[3])), ("MY", N(l.Values[4])), ("MZ", N(l.Values[5]))))));
        loads += pointLoads.Count;
        var skipped = LoadSets.Expanded(m).Count(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase)) + m.LineLoads.Count(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase))
            - surface.Count - lineLoads.Count;
        if (skipped > 0) notes.Add($"{skipped} load(s) on {story} are not uniform gravity loads on slabs or beams and were left out; add them in SAFE.");

        // Strip design overwrites, for the strips drawn by hand.
        if (own.Count > 0)
            tables.Add(CsiTableText.Table("CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED", own.Select(st =>
            {
                var row = Row(("Strip Name", st.Name), ("Strip Layer", st.Layer), ("Strip Design Type", StripType(st.DesignType)), ("Design?", st.Design ? "Yes" : "No"), ("Ignote PT?", st.IgnorePT ? "Yes" : "No"));
                if (st.RebarMaterial.Length > 0) row.Add(("Rebar Material", st.RebarMaterial));
                row.Add(("Cover Specification Type", "From Preferences"));
                row.Add(("LLRF", N(st.Llrf)));
                return (IReadOnlyList<(string, string)>)row;
            })));

        // Slab line releases on their null lines, and slab rebar, in the fields "Table and Field Keys.xml" names them
        // (ETABS 23.2; SAFE shares the tables). ETABS keeps neither in its .e2k: they go in through these tables.
        if (releases.Count > 0)
            tables.Add(CsiTableText.Table("AREA ASSIGNMENTS - SLAB LINE RELEASES", releases.Select(x => (IReadOnlyList<(string, string)>)Row(("UniqueName", x.Line!),
                ("Shear Left", x.R.ShearLeft ? "Yes" : "No"), ("Moment Left", x.R.MomentLeft ? "Yes" : "No"), ("Shear Right", x.R.ShearRight ? "Yes" : "No"), ("Moment Right", x.R.MomentRight ? "Yes" : "No")))));
        if (rebars.Count > 0)
        {
            tables.Add(CsiTableText.Table("SLAB REBAR OBJECT GEOMETRY", rebars.Select(b => (IReadOnlyList<(string, string)>)Row(("Name", b.Name), ("X1", N(b.From[0])), ("Y1", N(b.From[1])),
                ("X2", N(b.To[0])), ("Y2", N(b.To[1])), ("Vertical Offset", N(b.VerticalOffset)), ("Width Left", N(b.WidthLeft)), ("Width Right", N(b.WidthRight)), ("Layer", b.Layer)))));
            tables.Add(CsiTableText.Table("SLAB REBAR PROPERTY ASSIGNMENTS", rebars.Select(b => (IReadOnlyList<(string, string)>)(b.TotalBars > 0
                ? Row(("Name", b.Name), ("Rebar Size", b.BarSize), ("Material", b.Material), ("Bar Number Type", "Total Bars"), ("Total Bars", I(b.TotalBars)))
                : Row(("Name", b.Name), ("Rebar Size", b.BarSize), ("Material", b.Material), ("Bar Number Type", "Max Spacing"), ("Max Spacing", N(b.MaxSpacing)))))));
        }

        // Punching shear overwrites, with a user perimeter's corners about the point.
        if (punching.Count > 0)
        {
            tables.Add(CsiTableText.Table("CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - GENERAL", punching.Select(x =>
            {
                var row = Row(("Unique Name", x.Point), ("Check Punching Shear?", x.P.Check), ("Location Type", x.P.Location), ("Perimeter Type", x.P.Perimeter is null ? "Auto" : "User Perimeter"),
                    ("Effective Depth Type", "Auto"), ("Opening Definition", "Auto"), ("Reinforcement Type", x.P.Reinforcement));
                if (x.P.Reinforcement != "None")
                    // The programs' own table keys name RebarFy "Reinforcement Diameter" and RebarDia "Reinforcement Fy" (swapped in "Table and Field Keys.xml"
                    // of ETABS 23.2), and SAFE matches the names to the keys, so the yield stress goes under "Reinforcement Diameter" as SAFE itself writes it.
                    row.AddRange([("Reinforcement Pattern", x.P.Pattern), ("Reinforcement Diameter", N(x.P.Fy)), ("Reinforcement Fy", N(x.P.Diameter)), ("Reinforcement Spacing", N(x.P.Spacing))]);
                return (IReadOnlyList<(string, string)>)row;
            })));
            var corners = punching.Where(x => x.P.Perimeter is not null).SelectMany(x => x.P.Perimeter!.Select((c, i) => (IReadOnlyList<(string, string)>)Row(("Unique Name", x.Point), ("Point Number", I(i + 1)),
                ("X Coord", N(c[0])), ("Y Coord", N(c[1])), ("Radius", N(c[2])), ("Is Null?", "No")))).ToList();
            if (corners.Count > 0) tables.Add(CsiTableText.Table("CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - PERIMETER", corners));
        }

        // Groups, with the members that are in this floor's file (Define > Groups).
        var groups = GroupDefs.Groups(m);
        {
            // SAFE's own group of everything comes first in its files.
            if (!groups.Exists(g => g.Name.Equals("All", StringComparison.OrdinalIgnoreCase))) groups.Insert(0, new Group("All", "Yellow", []));
            tables.Add(CsiTableText.Table("GROUP DEFINITIONS", groups.Select(g => (IReadOnlyList<(string, string)>)Row(("Name", g.Name), ("Color", g.Color),
                ("Steel Design?", "No"), ("Concrete Design?", "No"), ("Composite Design?", "No")))));
            var members = new List<IReadOnlyList<(string, string)>>();
            foreach (var g in groups)
                foreach (var (type, label, at) in g.Members)
                {
                    string? name = null, kind = null;
                    if (type == "AREA" && areaRows.Find(r => r.Source.Label == label && string.Equals(r.Source.Story, at, StringComparison.OrdinalIgnoreCase)) is { Label: { } an }) (name, kind) = (an, "Area");
                    else if (type == "LINE" && lineRows.Find(r => r.Source.Label == label && string.Equals(r.Source.Story, at, StringComparison.OrdinalIgnoreCase)) is { Label: { } ln } lr) (name, kind) = (ln, lr.Type);
                    else if (type == "POINT" && string.Equals(at, story, StringComparison.OrdinalIgnoreCase) && m.Point(label) is { } gp && index.TryGetValue(PointKey(gp.X, gp.Y, z - gp.Below), out var pn)) (name, kind) = (pn, "Point");
                    if (name is not null) members.Add(Row(("Group Name", g.Name), ("Object Type", kind!), ("Object Unique Name", name)));
                }
            if (members.Count > 0) tables.Add(CsiTableText.Table("GROUP ASSIGNMENTS", members));
        }

        // The tables of a SAFE file the Modeler opened that it has no form for, as the file had them.
        KeptTables.WriteInto(m, "safe", tables);
        // A model that did not come from SAFE: the tables SAFE itself would write (defaults, display, summaries), the way SAFE writes them.
        if (!F2kSource.Has(m))
            tables = F2kSafeTables.Complete(m, story, z, tables,
                areaRows.GroupBy(a => a.Label).ToDictionary(g => g.Key, g => (g.First().Source.Label, g.First().Source.Story ?? ""), StringComparer.OrdinalIgnoreCase),
                lineRows.GroupBy(l => l.Label).ToDictionary(g => g.Key, g => (g.First().Source.Label, g.First().Source.Story ?? "", g.First().Type), StringComparer.OrdinalIgnoreCase));
        // What the Engine's SAFE forms set (SAFE's own definition and option tables, SafeTables): their fields over the written ones.
        SafeTables.WriteInto(m, tables);
        // A model read from SAFE's own file: what did not change goes back exactly as SAFE wrote it, field for field.
        tables = F2kSource.Merge(m, story, tables);

        var header = $"File written by CSI ProMax Modeler on {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: story {story} of {(m.Title.Length > 0 ? m.Title : "the model")}";
        return new F2kResult(CsiTableText.Document(header, tables), story, areaRows.Count, beamRows.Count, columns.Count, wallRows.Count,
            supports.DistinctBy(s => s.Point).Count(), strips.Count, loads, notes) { Tendons = tendons.Count };
    }

    /// <summary>
    /// A tendon's rows of "Tendon Object Connectivity", as SAFE 20 writes them: the first row has the first point and the
    /// first span, a row per further plan point, and a row per further span. A, B and C are lengths and the heights
    /// are below the slab top (the level), where the e2k gives fractions of the span and heights.
    /// </summary>
    private static IEnumerable<IReadOnlyList<(string, string)>> TendonRows(SlabDefs.Tendon t, List<string> pts, double z)
    {
        List<(string, string)> Span(SlabDefs.TendonSpan s) =>
        [
            ("Span ID", s.Label.Length > 0 ? s.Label : "Span"), ("End Support Type", "None"), ("Span Profile", s.Type), ("RD", "1"), ("L", N(s.Length)),
            ("A", N(s.A * s.Length)), ("B", N(s.B * s.Length)), ("C", N(s.C * s.Length)), ("ZL", N(s.ZL - z)), ("ZC", N(s.ZC - z)), ("ZR", N(s.ZR - z)),
        ];
        var first = Row(("Name", t.Name), ("Number Points", I(pts.Count)), ("Point Name", pts[0]), ("Horizontal Segment Number", "1"), ("Horizontal Curve Type", "Straight"),
            ("Number Control Points", "2"), ("Curve Point Number", "1"), ("Global X", N(t.Plan[0][0])), ("Global Y", N(t.Plan[0][1])),
            ("Number Vertical Spans", I(t.Spans.Count)), ("Datum Offset", "0"), ("First Support Type", "None"), ("Span Number", "1"));
        if (t.Spans.Count > 0) first.AddRange(Span(t.Spans[0]));
        first.Add(("Layer", "Other"));
        yield return first;
        for (var i = 1; i < pts.Count; i++)
        {
            // A plan point ends segment i; the next segment starts there (its first curve point).
            yield return Row(("Name", t.Name), ("Point Name", pts[i]), ("Horizontal Segment Number", I(i)), ("Curve Point Number", "2"), ("Global X", N(t.Plan[i][0])), ("Global Y", N(t.Plan[i][1])));
            if (i < pts.Count - 1)
                yield return Row(("Name", t.Name), ("Horizontal Segment Number", I(i + 1)), ("Horizontal Curve Type", "Straight"), ("Number Control Points", "2"), ("Curve Point Number", "1"),
                    ("Global X", N(t.Plan[i][0])), ("Global Y", N(t.Plan[i][1])));
        }
        for (var n = 1; n < t.Spans.Count; n++)
        {
            var row = Row(("Name", t.Name), ("Span Number", I(n + 1)));
            row.AddRange(Span(t.Spans[n]));
            yield return row;
        }
    }

    private static string StripType(string designType) => designType switch
    {
        "Column" => "Column Strip",
        "Middle" => "Middle Strip",
        _ => designType,
    };

    /// <summary>The code of the model's concrete slab design preferences (Design > Concrete Slab Design > View/Revise Preferences).</summary>
    private static string? PreferenceCode(StructuralModel m) =>
        m.Passthrough.Find(r => r.Heading == SlabDefs.SlabPreferences)?.Lines.Select(l => E2kText.Value(E2kText.Tokenize(l), "CODE")).FirstOrDefault(c => c is not null);

    private static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    /// <summary>SAFE's element type: thick or thin shell, or membrane.</summary>
    private static string ElementType(ShellSection s, string fallback) => s.Get("MODELINGTYPE") switch
    {
        "ShellThick" => "Shell-Thick",
        "ShellThin" => "Shell-Thin",
        "Membrane" => "Membrane",
        _ => fallback,
    };

    /// <summary>The story with the most slab area: the floor an engineer most likely means.</summary>
    public static string BusiestStory(StructuralModel m, IReadOnlyList<PhysicalArea>? areas = null)
    {
        areas ??= Physical.Areas(m).ToList();
        var best = areas.Where(a => a.Kind == AreaKind.Floor).GroupBy(a => a.Story).OrderByDescending(g => g.Sum(a => a.Area)).FirstOrDefault()?.Key;
        return best ?? (m.Stories.Count > 1 ? m.Stories[^1].Name : m.Stories[0].Name);
    }

    private static double StoryHeight(StructuralModel m, int index) => index > 0 && index < m.Stories.Count ? m.Stories[index].Height : 0;

    private static string Unique(IEnumerable<string> taken, string wanted)
    {
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(wanted)) return wanted;
        for (var i = 2; ; i++) if (!set.Contains(wanted + "-" + i.ToString(CultureInfo.InvariantCulture))) return wanted + "-" + i.ToString(CultureInfo.InvariantCulture);
    }

    private static string Yes(HashSet<string> dofs, string dof) => dofs.Contains(dof) ? "Yes" : "No";

    private static double? Num(string? text) => E2kText.TryNumber(text, out var d) ? d : null;

    private static string SlabType(ShellSection s) => (s.Get("SLABTYPE") ?? "Slab") switch
    {
        var t when t.Equals("Drop", StringComparison.OrdinalIgnoreCase) => "Drop",
        var t when t.Equals("Mat", StringComparison.OrdinalIgnoreCase) => "Mat",
        var t when t.Equals("Footing", StringComparison.OrdinalIgnoreCase) => "Footing",
        var t when t.Equals("Ribbed", StringComparison.OrdinalIgnoreCase) => "Ribbed",
        var t when t.Equals("Waffle", StringComparison.OrdinalIgnoreCase) => "Waffle",
        _ => "Slab",
    };

    /// <summary>SAFE 20's load pattern types are ETABS's own; a pattern with none is Other.</summary>
    private static string PatternType(string etabs) =>
        ModelEdit.LoadPatternTypes.FirstOrDefault(t => t.Equals(etabs, StringComparison.OrdinalIgnoreCase)) ?? (etabs.Equals("Quake", StringComparison.OrdinalIgnoreCase) ? "Seismic" : "Other");

    /// <summary>One strip per grid line, across the extent of the level, as wide as halfway to the neighbouring grids.</summary>
    private static List<SlabDefs.Strip> GridStrips(StructuralModel m, IReadOnlyList<(string Name, double X, double Y, double Z)> level)
    {
        var list = new List<SlabDefs.Strip>();
        if (level.Count == 0) return list;
        double x0 = level.Min(p => p.X), x1 = level.Max(p => p.X), y0 = level.Min(p => p.Y), y1 = level.Max(p => p.Y);
        foreach (var direction in new[] { "Y", "X" })
        {
            // A grid spaced along Y (a line y = c) runs along X: its strip is layer A.
            var grids = m.Grids.Where(g => g.Direction == direction).OrderBy(g => g.Coordinate).ToList();
            for (var i = 0; i < grids.Count; i++)
            {
                var c = grids[i].Coordinate;
                var left = i > 0 ? (c - grids[i - 1].Coordinate) / 2 : 0;
                var right = i + 1 < grids.Count ? (grids[i + 1].Coordinate - c) / 2 : 0;
                if (left == 0) left = right;
                if (right == 0) right = left;
                var alongX = direction == "Y";
                if (alongX ? c < y0 - 1e-6 || c > y1 + 1e-6 : c < x0 - 1e-6 || c > x1 + 1e-6) continue;
                List<double[]> ends = alongX ? [[x0, c], [x1, c]] : [[c, y0], [c, y1]];
                list.Add(new SlabDefs.Strip((alongX ? "CSA" : "CSB") + grids[i].Label, "", alongX ? "A" : "B", ends, left, right));
            }
        }
        return list;
    }
}
