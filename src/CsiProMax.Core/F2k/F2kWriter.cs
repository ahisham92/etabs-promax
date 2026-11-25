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
public sealed record F2kOptions(string? Story = null, double? Subgrade = null, bool CompressionOnly = true, bool Strips = true, string Version = "16.0.0");

/// <summary>What was written, for the answer to the engineer.</summary>
public sealed record F2kResult(string Text, string Story, int Slabs, int Beams, int Columns, int Walls, int Supports, int Strips, int Loads, IReadOnlyList<string> Notes);

/// <summary>
/// Writes one floor of a model as a SAFE text file (.f2k), the table text SAFE imports with File > Import > SAFE .F2K
/// file (and ETABS's own "Export story as SAFE .F2K" writes). SAFE models one level: slabs, beams and drops on it,
/// the columns and walls below and above it as its supports, point supports, soil under a mat, design strips,
/// loads and combinations. Z is 0 at the slab; columns and walls reach down (and up) by their story heights.
/// Nothing is analysed here: SAFE does that after the import.
/// </summary>
public static class F2kWriter
{
    private static string N(double v) => CsiTableText.Number(v);

    private static List<(string, string)> Row(params (string Key, string Value)[] pairs) => [.. pairs];

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

        // ---- points: every corner and end on the level (z = 0), and the far ends of columns (z = -h or +h) ----
        var points = new List<(string Name, double X, double Y, double Z)>();
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        string Point(double x, double y, double dz)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(x, 6)},{Math.Round(y, 6)},{Math.Round(dz, 6)}");
            if (index.TryGetValue(key, out var name)) return name;
            name = (points.Count + 1).ToString(CultureInfo.InvariantCulture);
            points.Add((name, x, y, dz));
            index[key] = name;
            return name;
        }

        var areaRows = new List<(string Label, string Section, List<string> Points)>();
        foreach (var s in slabs) areaRows.Add((Unique(areaRows.Select(r => r.Label), s.Label), s.Section ?? "", s.Corners.Select(c => Point(c.X, c.Y, 0)).ToList()));
        var lineRows = new List<(string Label, string Type, string Section, string I, string J)>();
        foreach (var b in beams) lineRows.Add((Unique(lineRows.Select(r => r.Label), b.Label), "Beam", b.Section ?? "", Point(b.I.X, b.I.Y, 0), Point(b.J.X, b.J.Y, 0)));
        var farEnds = new List<string>();
        foreach (var c in below)
        {
            var bottom = c.I.Z < c.J.Z ? c.I : c.J;
            var far = Point(bottom.X, bottom.Y, bottom.Z - z);
            farEnds.Add(far);
            lineRows.Add((Unique(lineRows.Select(r => r.Label), c.Label), "Column", c.Section ?? "", far, Point(bottom.X, bottom.Y, 0)));
        }
        foreach (var c in above)
        {
            var top = c.I.Z > c.J.Z ? c.I : c.J;
            var far = Point(top.X, top.Y, top.Z - z);
            farEnds.Add(far);
            lineRows.Add((Unique(lineRows.Select(r => r.Label), c.Label + "A"), "Column", c.Section ?? "", Point(top.X, top.Y, 0), far));
        }
        // Walls become wall lines along their trace on the level.
        var wallRows = new List<(string Label, string Section, string I, string J, double Height)>();
        foreach (var (w, up) in wallsBelow.Select(w => (w, false)).Concat(wallsAbove.Select(w => (w, true))))
        {
            var onLevel = w.Corners.Where(c => At(c.Z)).ToList();
            if (onLevel.Count < 2) continue;
            var height = w.Corners.Max(c => c.Z) - w.Corners.Min(c => c.Z);
            wallRows.Add((Unique(wallRows.Select(r => r.Label), w.Label + (up ? "A" : "")), w.Section ?? "", Point(onLevel[0].X, onLevel[0].Y, 0), Point(onLevel[1].X, onLevel[1].Y, 0), up ? height : -height));
        }

        // ---- supports: restraints on the level, and the far ends of the columns ----
        var supports = new List<(string Point, string Restraint)>();
        foreach (var pa in m.PointAssigns.Where(p => p.Restraint is { Length: > 0 } && string.Equals(p.Story, story, StringComparison.OrdinalIgnoreCase)))
            if (m.Point(pa.Point) is { } pp) supports.Add((Point(pp.X, pp.Y, 0), pa.Restraint!));
        foreach (var far in farEnds.Distinct()) supports.Add((far, "UX UY UZ RX RY RZ"));
        if (below.Count + above.Count + wallRows.Count + supports.Count == 0 && options.Subgrade is null)
            notes.Add("Nothing holds the slab up: no columns, walls, supports or soil. Add supports or give a subgrade modulus before analysing in SAFE.");

        // ---- what is used ----
        var usedSlab = areaRows.Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedBeam = lineRows.Where(r => r.Type == "Beam").Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedColumn = lineRows.Where(r => r.Type == "Column").Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedWall = wallRows.Select(r => r.Section).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = new List<string>();
        var units = $"{m.Units.Force}, {m.Units.Length.ToLowerInvariant()}, {m.Units.Temperature}";
        tables.Add(CsiTableText.Table("PROGRAM CONTROL", [Row(("ProgramName", "SAFE"), ("Version", options.Version), ("ProgLevel", "PostTensioning"), ("CurrUnits", units), ("MergeTol", "0.001"), ("ModelDatum", N(z)), ("StHtAbove", N(StoryHeight(m, level + 1))), ("StHtBelow", N(StoryHeight(m, level))))]));

        // Materials: the ones the used sections name, plus every concrete (SAFE designs with them).
        var sectionMaterials = m.FrameSections.Where(s => usedBeam.Contains(s.Name) || usedColumn.Contains(s.Name)).Select(s => s.Material)
            .Concat(m.ShellSections.Where(s => usedSlab.Contains(s.Name) || usedWall.Contains(s.Name)).Select(s => s.Material)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materials = m.Materials.Where(x => x.IsConcrete || sectionMaterials.Contains(x.Name) || x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase)).ToList();
        tables.Add(CsiTableText.Table("MATERIAL PROPERTIES 01 - GENERAL", materials.Select(x => (IReadOnlyList<(string, string)>)Row(("Material", x.Name), ("Type", x.Type.Length > 0 ? x.Type : "Concrete"), ("SymType", x.Get("SYMTYPE") ?? "Isotropic")))));
        tables.Add(CsiTableText.Table("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", materials.Select(x => (IReadOnlyList<(string, string)>)Row(("Material", x.Name), ("UnitWeight", N(x.Number("WEIGHTPERVOLUME") ?? 0)), ("E1", N(x.Number("E") ?? 0)), ("U12", N(x.Number("U") ?? 0.2)), ("A1", N(x.Number("A") ?? 1e-5))))));
        var concretes = materials.Where(x => x.IsConcrete).ToList();
        if (concretes.Count > 0)
            tables.Add(CsiTableText.Table("MATERIAL PROPERTIES 03 - CONCRETE", concretes.Select(x => (IReadOnlyList<(string, string)>)Row(("Material", x.Name), ("Fc", N(x.Number("FC") ?? 0)), ("LtWtConc", "No")))));
        var rebars = materials.Where(x => x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rebars.Count > 0)
            tables.Add(CsiTableText.Table("MATERIAL PROPERTIES 04 - REBAR", rebars.Select(x => (IReadOnlyList<(string, string)>)Row(("Material", x.Name), ("Fy", N(x.Number("FY") ?? 0)), ("Fu", N(x.Number("FU") ?? 0))))));

        // Slab properties: slabs, drops, mats and footings by the slab type ETABS gives them.
        var slabProps = m.ShellSections.Where(s => usedSlab.Contains(s.Name)).ToList();
        if (slabProps.Count > 0)
            tables.Add(CsiTableText.Table("SLAB PROPERTIES 01 - GENERAL", slabProps.Select(s => (IReadOnlyList<(string, string)>)Row(
                ("Slab", s.Name), ("Type", SlabType(s)), ("MatProp", s.Material), ("Thickness", N(s.Thickness ?? 0)), ("ThickPlate", s.Get("MODELINGTYPE") is "ShellThick" ? "Yes" : "No"), ("Ortho", "No")))));
        var beamProps = m.FrameSections.Where(s => usedBeam.Contains(s.Name)).ToList();
        if (beamProps.Count > 0)
            tables.Add(CsiTableText.Table("BEAM PROPERTIES 01 - GENERAL", beamProps.Select(s => (IReadOnlyList<(string, string)>)Row(
                ("Beam", s.Name), ("Type", "Rectangular"), ("MatProp", s.Material), ("Depth", N(s.Number("D") ?? 0)), ("WidthTop", N(s.Number("B") ?? 0)), ("WidthBot", N(s.Number("B") ?? 0))))));
        var columnProps = m.FrameSections.Where(s => usedColumn.Contains(s.Name)).ToList();
        if (columnProps.Count > 0)
            tables.Add(CsiTableText.Table("COLUMN PROPERTIES 01 - GENERAL", columnProps.Select(s => (IReadOnlyList<(string, string)>)Row(
                ("Column", s.Name), ("Type", s.Shape.Contains("Circle", StringComparison.OrdinalIgnoreCase) ? "Circular" : "Rectangular"), ("MatProp", s.Material),
                ("SecDim2", N(s.Number("B") ?? s.Number("D") ?? 0)), ("SecDim3", N(s.Number("D") ?? 0)), ("AutoRigid", "No"), ("AutoDrop", "No"), ("IncludeCap", "No")))));
        var wallProps = m.ShellSections.Where(s => usedWall.Contains(s.Name)).ToList();
        if (wallProps.Count > 0)
            tables.Add(CsiTableText.Table("WALL PROPERTIES", wallProps.Select(s => (IReadOnlyList<(string, string)>)Row(("Wall", s.Name), ("MatProp", s.Material), ("Thickness", N(s.Thickness ?? 0)), ("AutoRigid", "No")))));
        if (options.Subgrade is { } k)
            tables.Add(CsiTableText.Table("SOIL PROPERTIES", [Row(("Soil", "SOIL1"), ("Subgrade", N(k)), ("NonlinOpt", options.CompressionOnly ? "Compression Only" : "None"))]));

        // Geometry
        tables.Add(CsiTableText.Table("OBJECT GEOMETRY - POINT COORDINATES", points.Select(p => (IReadOnlyList<(string, string)>)Row(("Point", p.Name), ("GlobalX", N(p.X)), ("GlobalY", N(p.Y)), ("GlobalZ", N(p.Z)), ("SpecialPt", "No")))));
        if (lineRows.Count + wallRows.Count > 0)
            tables.Add(CsiTableText.Table("OBJECT GEOMETRY - LINES 01 - GENERAL", lineRows.Select(l => (IReadOnlyList<(string, string)>)Row(("Line", l.Label), ("PointI", l.I), ("PointJ", l.J), ("LineType", l.Type)))
                .Concat(wallRows.Select(w => (IReadOnlyList<(string, string)>)Row(("Line", w.Label), ("PointI", w.I), ("PointJ", w.J), ("LineType", "Wall"), ("Height", N(w.Height)))))));
        if (areaRows.Count > 0)
            tables.Add(CsiTableText.Table("OBJECT GEOMETRY - AREAS 01 - GENERAL", areaRows.Select(a =>
            {
                var row = Row(("Area", a.Label), ("NumPoints", a.Points.Count.ToString(CultureInfo.InvariantCulture)));
                for (var i = 0; i < a.Points.Count; i++) row.Add(("Point" + (i + 1).ToString(CultureInfo.InvariantCulture), a.Points[i]));
                row.Add(("AreaType", "Slab"));
                return (IReadOnlyList<(string, string)>)row;
            })));

        // Design strips along the grid lines.
        var stripCount = 0;
        if (options.Strips && points.Count > 0)
        {
            var rows = Strips(m, points.Where(p => Math.Abs(p.Z) < tol).ToList(), out stripCount);
            if (rows.Count > 0) tables.Add(CsiTableText.Table("OBJECT GEOMETRY - DESIGN STRIPS", rows));
            else notes.Add("The model has no grid lines, so no design strips were made. Draw strips in SAFE, or add grids first.");
        }

        // Assignments
        if (areaRows.Count > 0)
            tables.Add(CsiTableText.Table("SLAB PROPERTY ASSIGNMENTS", areaRows.Select(a => (IReadOnlyList<(string, string)>)Row(("Area", a.Label), ("SlabProp", a.Section)))));
        if (lineRows.Any(l => l.Type == "Beam"))
            tables.Add(CsiTableText.Table("BEAM PROPERTY ASSIGNMENTS", lineRows.Where(l => l.Type == "Beam").Select(l => (IReadOnlyList<(string, string)>)Row(("Line", l.Label), ("BeamProp", l.Section)))));
        if (lineRows.Any(l => l.Type == "Column"))
            tables.Add(CsiTableText.Table("COLUMN PROPERTY ASSIGNMENTS", lineRows.Where(l => l.Type == "Column").Select(l => (IReadOnlyList<(string, string)>)Row(("Line", l.Label), ("ColProp", l.Section)))));
        if (wallRows.Count > 0)
            tables.Add(CsiTableText.Table("WALL PROPERTY ASSIGNMENTS", wallRows.Select(w => (IReadOnlyList<(string, string)>)Row(("Line", w.Label), ("WallProp", w.Section)))));
        if (options.Subgrade is not null && areaRows.Count > 0)
            tables.Add(CsiTableText.Table("SOIL PROPERTY ASSIGNMENTS", areaRows.Select(a => (IReadOnlyList<(string, string)>)Row(("Area", a.Label), ("SoilProp", "SOIL1")))));
        if (supports.Count > 0)
            tables.Add(CsiTableText.Table("RESTRAINT ASSIGNMENTS", supports.DistinctBy(s => s.Point).Select(s =>
            {
                var dofs = s.Restraint.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(d => d.ToUpperInvariant()).ToHashSet();
                return (IReadOnlyList<(string, string)>)Row(("Point", s.Point), ("UX", Yes(dofs, "UX")), ("UY", Yes(dofs, "UY")), ("UZ", Yes(dofs, "UZ")), ("RX", Yes(dofs, "RX")), ("RY", Yes(dofs, "RY")), ("RZ", Yes(dofs, "RZ")));
            })));

        // Loads
        tables.Add(CsiTableText.Table("LOAD PATTERNS", m.LoadPatterns.Select(p => (IReadOnlyList<(string, string)>)Row(("LoadPat", p.Name), ("Type", PatternType(p.Type)), ("SelfWtMult", N(p.SelfWeight))))));
        var caseRows = new List<IReadOnlyList<(string, string)>>();
        var caseLoads = new List<IReadOnlyList<(string, string)>>();
        foreach (var p in m.LoadPatterns)
        {
            caseRows.Add(Row(("LoadCase", p.Name), ("Type", "LinStatic"), ("InitialCond", "Zero")));
            caseLoads.Add(Row(("LoadCase", p.Name), ("LoadType", "Load Pattern"), ("LoadName", p.Name), ("LoadSF", "1")));
        }
        if (caseRows.Count > 0)
        {
            tables.Add(CsiTableText.Table("LOAD CASES 01 - GENERAL", caseRows));
            tables.Add(CsiTableText.Table("LOAD CASES 02 - STATIC", caseLoads));
        }

        var loads = 0;
        var slabLabels = slabs.Select(s => s.Label).ToHashSet(StringComparer.Ordinal);
        var surface = m.AreaLoads.Where(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase) && slabLabels.Contains(l.Object))
            .Select(l => (l.Object, l.Pattern, Value: Num(E2kText.Value(l.Tokens, "FVAL")), Type: E2kText.Value(l.Tokens, "TYPE") ?? "")).Where(l => l.Value is not null && l.Type.Equals("UNIFF", StringComparison.OrdinalIgnoreCase)).ToList();
        if (surface.Count > 0)
            tables.Add(CsiTableText.Table("LOAD ASSIGNMENTS - SURFACE LOADS", surface.Select(l => (IReadOnlyList<(string, string)>)Row(("Area", l.Object), ("LoadPat", l.Pattern), ("Dir", "Gravity"), ("UnifLoad", N(l.Value!.Value))))));
        loads += surface.Count;
        var beamLabels = beams.Select(b => b.Label).ToHashSet(StringComparer.Ordinal);
        var lineLoads = m.LineLoads.Where(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase) && beamLabels.Contains(l.Object))
            .Select(l => (l.Object, l.Pattern, Value: Num(E2kText.Value(l.Tokens, "FVAL")), Type: E2kText.Value(l.Tokens, "TYPE") ?? "")).Where(l => l.Value is not null && l.Type.Equals("UNIFF", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lineLoads.Count > 0)
            tables.Add(CsiTableText.Table("LOAD ASSIGNMENTS - DISTRIBUTED LOADS", lineLoads.Select(l => (IReadOnlyList<(string, string)>)Row(("Line", l.Object), ("LoadPat", l.Pattern), ("Type", "Force"), ("Dir", "Gravity"), ("DistType", "RelDist"), ("RelDistA", "0"), ("RelDistB", "1"), ("FOverLA", N(l.Value!.Value)), ("FOverLB", N(l.Value!.Value))))));
        loads += lineLoads.Count;
        var skipped = m.AreaLoads.Count(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase)) + m.LineLoads.Count(l => string.Equals(l.Story, story, StringComparison.OrdinalIgnoreCase)) - loads;
        if (skipped > 0) notes.Add($"{skipped} load(s) on {story} are not uniform gravity loads on slabs or beams and were left out; add them in SAFE.");
        if (m.PointLoads.Count > 0) notes.Add("Point loads are not written to the SAFE file yet; add them in SAFE.");

        var combos = m.Combos.Select(c => (c.Name, Type: c.Lines.Select(l => E2kText.Value(l, "TYPE")).FirstOrDefault(t => t is not null) ?? "Linear Add",
            Items: c.Lines.Select(l => (Case: E2kText.Value(l, "LOADCASE") ?? E2kText.Value(l, "LOADCOMBO"), Sf: Num(E2kText.Value(l, "SF")))).Where(i => i.Case is not null && i.Sf is not null).ToList())).ToList();
        if (combos.Any(c => c.Items.Count > 0))
            tables.Add(CsiTableText.Table("LOAD COMBINATIONS", combos.SelectMany(c => c.Items.Select((i, n) => (IReadOnlyList<(string, string)>)(n == 0
                ? Row(("Combo", c.Name), ("Load", i.Case!), ("SF", N(i.Sf!.Value)), ("Type", c.Type), ("DSStrength", "Yes"), ("DSServInit", "No"), ("DSServNorm", "No"), ("DSServLong", "No"))
                : Row(("Combo", c.Name), ("Load", i.Case!), ("SF", N(i.Sf!.Value))))))));

        var header = $"File written by CSI ProMax Modeler on {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: story {story} of {(m.Title.Length > 0 ? m.Title : "the model")}";
        notes.Add("This SAFE file has not been checked against an import in SAFE on this PC yet: if SAFE reports a table it does not know, say which, and it will be fixed.");
        return new F2kResult(CsiTableText.Document(header, tables), story, areaRows.Count, lineRows.Count(l => l.Type == "Beam"), lineRows.Count(l => l.Type == "Column"), wallRows.Count,
            supports.DistinctBy(s => s.Point).Count(), stripCount, loads, notes);
    }

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

    /// <summary>SAFE's load pattern types: ETABS's names, upper case, with the ones SAFE lacks as OTHER.</summary>
    private static string PatternType(string etabs) => etabs.ToUpperInvariant() switch
    {
        "DEAD" => "DEAD",
        "SUPER DEAD" => "SUPER DEAD",
        "LIVE" => "LIVE",
        "REDUCIBLE LIVE" => "REDUCE LIVE",
        "ROOF LIVE" => "ROOF LIVE",
        "SNOW" => "SNOW",
        "WIND" => "WIND",
        "SEISMIC" or "QUAKE" => "QUAKE",
        "TEMPERATURE" => "TEMPERATURE",
        _ => "OTHER",
    };

    /// <summary>One strip per grid line, across the extent of the level, as wide as halfway to the neighbouring grids.</summary>
    private static List<IReadOnlyList<(string, string)>> Strips(StructuralModel m, IReadOnlyList<(string Name, double X, double Y, double Z)> level, out int count)
    {
        var rows = new List<IReadOnlyList<(string, string)>>();
        count = 0;
        if (level.Count == 0) return rows;
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
                var name = (alongX ? "CSA" : "CSB") + grids[i].Label;
                count++;
                var ends = alongX ? new[] { (x0, c), (x1, c) } : new[] { (c, y0), (c, y1) };
                for (var n = 0; n < 2; n++)
                    rows.Add(Row(("Strip", name), ("Point", (n + 1).ToString(CultureInfo.InvariantCulture)), ("GlobalX", N(ends[n].Item1)), ("GlobalY", N(ends[n].Item2)),
                        ("WBLeft", N(left)), ("WBRight", N(right)), ("WALeft", N(left)), ("WARight", N(right)), ("AutoWiden", "No"), ("Layer", alongX ? "A" : "B")));
            }
        }
        return rows;
    }
}
