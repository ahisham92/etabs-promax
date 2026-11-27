using System.Globalization;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.S2k;

/// <summary>
/// Opens a SAP2000 text file (.s2k, .$2k) as a Modeler model. A file CSI ProMax wrote comes back exactly (the model is
/// carried in it, see <see cref="EmbeddedModel"/>). A file SAP2000 wrote is read from its tables: SAP2000 has no
/// stories, so the levels come from the Z grid lines (or the joints' heights), each member goes on the level of its
/// top end, and what is level is a beam or a floor, what is upright a column or a wall, the rest braces and ramps.
/// What the Modeler has no place for is named in <see cref="Result.Notes"/>, quietly: nothing is asked.
/// </summary>
public static partial class S2kReader
{
    public sealed record Result(StructuralModel Model, bool Exact, IReadOnlyList<string> Notes);

    private const double Tol = 1e-6;

    public static Result ReadFile(string path) => Read(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static Result Read(string text, string title = "Model")
    {
        if (EmbeddedModel.Read(text) is { } carried) return new Result(carried, true, []);
        var tables = CsiTableText.Parse(text);
        if (tables.Count == 0) throw new E2kFormatException("This is not a SAP2000 text file: it has no tables.");
        var notes = new List<string>();
        var m = Tables(tables, title, notes, keep: true);
        m.Target = "sap2000";
        return new Result(m, false, notes.Distinct().ToList());
    }

    private static List<Dictionary<string, string>> T(Dictionary<string, List<Dictionary<string, string>>> t, string name) =>
        t.TryGetValue(name, out var rows) ? rows : [];

    private static string S(Dictionary<string, string> row, string key, string fallback = "") => row.TryGetValue(key, out var v) ? v : fallback;
    private static double D(Dictionary<string, string> row, string key, double fallback = 0) =>
        row.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    private static bool Yes(Dictionary<string, string> row, string key) => S(row, key).Equals("Yes", StringComparison.OrdinalIgnoreCase);

    internal static StructuralModel Tables(Dictionary<string, List<Dictionary<string, string>>> t, string title, List<string> notes, bool keep = false)
    {
        var m = ModelEdit.New(title);
        m.LoadPatterns.Clear(); m.LoadCases.Clear(); m.Combos.Clear();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, string>> Use(string name) { known.Add(name); return T(t, name); }

        // Units: "KN, m, C".
        if (Use("PROGRAM CONTROL").FirstOrDefault() is { } control && S(control, "CurrUnits").Split(',', StringSplitOptions.TrimEntries) is { Length: >= 2 } u)
        {
            m.Units.Force = u[0].ToUpperInvariant();
            m.Units.Length = u[1].ToUpperInvariant();
            if (u.Length > 2) m.Units.Temperature = u[2].ToUpperInvariant();
        }
        // Analyze > Set Active Degrees of Freedom: kept as the form's row when one of them is off.
        if (Use("ACTIVE DEGREES OF FREEDOM").FirstOrDefault() is { } activeDofRow && activeDofRow.Any(dofKv => dofKv.Value.Equals("No", StringComparison.OrdinalIgnoreCase)))
        {
            m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
            m.DefinitionTables["Active Degrees of Freedom"] = [activeDofRow.Where(dofKv => dofKv.Value.Length > 0).ToDictionary(dofKv => dofKv.Key.ToUpperInvariant(), dofKv => dofKv.Value, StringComparer.Ordinal)];
        }
        // The systems other than GLOBAL, and their grid lines, are kept as Define > Coordinate Systems/Grids has them.
        var otherSystems = Use("COORDINATE SYSTEMS").Where(cs => S(cs, "Name").Length > 0 && !S(cs, "Name").Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)).ToList();
        void KeepSap(string keepTable, IEnumerable<Dictionary<string, string>> keepRows)
        {
            var keepFields = DefineTables.SapHinges[keepTable];
            var keptRows = keepRows.Select(kr => kr.Where(kv => kv.Value.Length > 0 && keepFields.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kv => keepFields.First(f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), kv => kv.Value, StringComparer.Ordinal)).Where(kr => kr.Count > 0).ToList();
            if (keptRows.Count == 0) return;
            m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
            m.DefinitionTables[keepTable] = keptRows;
        }
        KeepSap("Coordinate Systems", otherSystems);

        // Joints.
        var joints = new Dictionary<string, Xyz>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Use("JOINT COORDINATES"))
        {
            var name = S(r, "Joint");
            if (name.Length == 0) continue;
            joints[name] = r.ContainsKey("GlobalX")
                ? new Xyz(D(r, "GlobalX"), D(r, "GlobalY"), D(r, "GlobalZ"))
                : new Xyz(D(r, "XorR"), D(r, "Y"), D(r, "Z"));
        }

        // Grids and levels: the Z grid lines are the levels; without them, every height a joint is at.
        var levels = new List<(string Name, double Z)>();
        var gridRows = Use("GRID LINES").ToList();
        KeepSap("Grid Lines", gridRows.Where(gl => otherSystems.Any(o => S(o, "Name").Equals(S(gl, "CoordSys"), StringComparison.OrdinalIgnoreCase))));
        foreach (var r in gridRows.Where(gl => !otherSystems.Any(o => S(o, "Name").Equals(S(gl, "CoordSys"), StringComparison.OrdinalIgnoreCase))))
        {
            var dir = S(r, "AxisDir").ToUpperInvariant();
            if (dir == "Z") levels.Add((S(r, "GridID"), D(r, "XRYZCoord")));
            else if (dir is "X" or "Y") m.AddGrid(S(r, "GridID"), dir, D(r, "XRYZCoord"));
        }
        if (levels.Count == 0)
        {
            var zs = joints.Values.Select(j => j.Z).Distinct().OrderBy(z => z).ToList();
            var merged = new List<double>();
            foreach (var z in zs) if (merged.Count == 0 || z - merged[^1] > 1e-4) merged.Add(z);
            levels = merged.Select((z, i) => (i == 0 ? "Base" : "Story" + i.ToString(CultureInfo.InvariantCulture), z)).ToList();
            if (levels.Count == 0) levels.Add(("Base", 0));
        }
        levels = levels.GroupBy(l => Math.Round(l.Z / 1e-4)).Select(g => g.First()).OrderBy(l => l.Z).ToList();
        // A joint above the top level makes a level of its own.
        var top = joints.Count > 0 ? joints.Values.Max(j => j.Z) : levels[^1].Z;
        if (top > levels[^1].Z + 1e-4) levels.Add(("Story" + levels.Count.ToString(CultureInfo.InvariantCulture), top));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        m.Stories = [];
        for (var i = 0; i < levels.Count; i++)
        {
            var n = levels[i].Name.Length > 0 && names.Add(levels[i].Name) ? levels[i].Name : Unique(names, "Story" + i.ToString(CultureInfo.InvariantCulture));
            m.Stories.Add(i == 0 ? new Story { Name = n, Elevation = levels[0].Z } : new Story { Name = n, Height = levels[i].Z - levels[i - 1].Z });
        }
        var levelZ = levels.Select(l => l.Z).ToArray();

        // Where a joint is: its level (the first at or above it) and how far below that level, and its plan point.
        var plan = new Dictionary<(long, long, long), PlanPoint>();
        var at = new Dictionary<string, (int Level, PlanPoint Point)>(StringComparer.OrdinalIgnoreCase);
        (int Level, PlanPoint Point) Where(string joint)
        {
            if (at.TryGetValue(joint, out var w)) return w;
            var p = joints[joint];
            var level = Array.FindIndex(levelZ, z => z >= p.Z - 1e-4);
            if (level < 0) level = levelZ.Length - 1;
            var below = Math.Abs(levelZ[level] - p.Z) <= 1e-4 ? 0 : Math.Round(levelZ[level] - p.Z, 9);
            var key = (Key(p.X), Key(p.Y), Key(below));
            if (!plan.TryGetValue(key, out var point))
            {
                point = new PlanPoint { Name = (plan.Count + 1).ToString(CultureInfo.InvariantCulture), X = p.X, Y = p.Y, Below = below };
                plan[key] = point;
                m.Points.Add(point);
            }
            return at[joint] = (level, point);
        }

        Materials(m, Use, notes);
        var concreteBeams = Use("FRAME SECTION PROPERTIES 01 - GENERAL").Where(r => Yes(r, "ConcBeam")).Select(r => S(r, "SectionName")).ToHashSet(StringComparer.Ordinal);
        FrameSections(m, T(t, "FRAME SECTION PROPERTIES 01 - GENERAL"), concreteBeams, notes);

        // Frames: what is level is a beam, upright a column, else a brace; on the level of the top end.
        var sections = Use("FRAME SECTION ASSIGNMENTS").GroupBy(r => S(r, "Frame")).ToDictionary(g => g.Key, g => S(g.First(), "AnalSect", S(g.First(), "DesignSect")), StringComparer.OrdinalIgnoreCase);
        var lines = new Dictionary<string, LineObject>(StringComparer.Ordinal);
        var frameAt = new Dictionary<string, (string Label, string Story)>(StringComparer.OrdinalIgnoreCase);
        var storyNames = m.Stories.Select(s => s.Name).ToList();
        foreach (var r in Use("CONNECTIVITY - FRAME"))
        {
            string name = S(r, "Frame"), ji = S(r, "JointI"), jj = S(r, "JointJ");
            if (!joints.ContainsKey(ji) || !joints.ContainsKey(jj)) { notes.Add($"Frame {name} names a joint that is not in the file; left out."); continue; }
            var (a, b) = (joints[ji], joints[jj]);
            var kind = Math.Abs(a.Z - b.Z) <= 1e-4 ? LineKind.Beam : Math.Abs(a.X - b.X) <= Tol && Math.Abs(a.Y - b.Y) <= Tol ? LineKind.Column : LineKind.Brace;
            if (kind != LineKind.Beam && a.Z > b.Z) (ji, jj) = (jj, ji);
            var (wi, wj) = (Where(ji), Where(jj));
            if (kind == LineKind.Beam && wi.Level != wj.Level) kind = LineKind.Brace;
            var story = storyNames[wj.Level];
            var below = kind == LineKind.Beam ? 0 : wj.Level - wi.Level;
            var label = Label(name, story, storyNames);
            var line = new LineObject { Label = label, Kind = kind, PointI = wi.Point.Name, PointJ = wj.Point.Name, StoriesBelowI = below };
            if (lines.TryGetValue(label, out var had) && !Same(had, line) || frameAt.Values.Contains((label, story)))
            {
                label = Unique(lines.Keys, name);
                line.Label = label;
            }
            if (!lines.ContainsKey(label)) { lines[label] = line; m.Lines.Add(line); }
            var assign = new LineAssign { Label = label, Story = story, Extra = [ModelEdit.LineAssignDefaults(kind)] };
            if (sections.TryGetValue(name, out var sec) && sec.Length > 0) assign.Section = sec;
            m.LineAssigns.Add(assign);
            frameAt[name] = (label, story);
        }

        ShellSections(m, Use, t, joints, notes);

        // Areas: level ones are floors, upright ones walls, the rest ramps; on the level of their highest corner.
        var areaSections = Use("AREA SECTION ASSIGNMENTS").GroupBy(r => S(r, "Area")).ToDictionary(g => g.Key, g => S(g.First(), "Section"), StringComparer.OrdinalIgnoreCase);
        var areas = new Dictionary<string, AreaObject>(StringComparer.Ordinal);
        var areaAt = new Dictionary<string, (string Label, string Story)>(StringComparer.OrdinalIgnoreCase);
        var areaJoints = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Use("CONNECTIVITY - AREA"))
        {
            var name = S(r, "Area");
            var count = (int)D(r, "NumJoints", r.Keys.Count(k => k.StartsWith("Joint", StringComparison.OrdinalIgnoreCase)));
            var corners = Enumerable.Range(1, count).Select(i => S(r, "Joint" + i.ToString(CultureInfo.InvariantCulture))).ToList();
            if (corners.Count < 3 || corners.Exists(c => !joints.ContainsKey(c))) { notes.Add($"Area {name} names a joint that is not in the file; left out."); continue; }
            var xyz = corners.Select(c => joints[c]).ToList();
            var kind = AreaKindOf(xyz);
            var where = corners.Select(Where).ToList();
            var level = where.Max(w => w.Level);
            var story = storyNames[level];
            var label = Label(name, story, storyNames);
            var area = new AreaObject { Label = label, Kind = kind, PointNames = where.Select(w => w.Point.Name).ToList(), StoriesBelow = where.Select(w => level - w.Level).ToList() };
            if (areas.TryGetValue(label, out var had) && !(had.Kind == area.Kind && had.PointNames.SequenceEqual(area.PointNames) && had.StoriesBelow.SequenceEqual(area.StoriesBelow)) || areaAt.Values.Contains((label, story)))
            {
                label = Unique(areas.Keys, name);
                area.Label = label;
            }
            if (!areas.ContainsKey(label)) { areas[label] = area; m.Areas.Add(area); }
            var assign = new AreaAssign { Label = label, Story = story, Extra = [ModelEdit.AreaAssignDefaults(kind == AreaKind.Wall ? AreaKind.Wall : AreaKind.Floor)] };
            if (areaSections.TryGetValue(name, out var sec) && sec.Length > 0) assign.Section = sec;
            m.AreaAssigns.Add(assign);
            areaAt[name] = (label, story);
            areaJoints[name] = corners;
        }

        // Joint assignments: supports and diaphragms.
        var pointAssigns = new Dictionary<(string, string), PointAssign>();
        PointAssign PointOf(string joint)
        {
            var w = Where(joint);
            var key = (w.Point.Name, storyNames[w.Level]);
            if (!pointAssigns.TryGetValue(key, out var pa)) { pointAssigns[key] = pa = new PointAssign { Point = w.Point.Name, Story = storyNames[w.Level] }; m.PointAssigns.Add(pa); }
            return pa;
        }
        foreach (var r in Use("JOINT RESTRAINT ASSIGNMENTS"))
        {
            var j = S(r, "Joint");
            if (!joints.ContainsKey(j)) continue;
            var dofs = new[] { ("U1", "UX"), ("U2", "UY"), ("U3", "UZ"), ("R1", "RX"), ("R2", "RY"), ("R3", "RZ") }.Where(d => Yes(r, d.Item1)).Select(d => d.Item2).ToList();
            if (dofs.Count > 0) PointOf(j).Restraint = string.Join(" ", dofs);
        }
        Diaphragms(m, Use, joints, areaAt, areaJoints, storyNames, PointOf, notes);

        Releases(m, Use, frameAt, notes);
        Patterns(m, Use, notes);
        Functions(m, Use, notes);
        Cases(m, Use, notes);
        // Groups, constraints, springs, links, hinges, the mass source and moving loads.
        Extras(m, Use, t, joints, j => { var w = Where(j); return w.Point.Name + "@" + storyNames[w.Level]; }, frameAt, areaAt, storyNames, notes);
        Combos(m, Use, notes);
        // Analyze > Set Load Cases to Run: the cases the file has RunCase=No for (once every case, moving ones too, is in).
        var notRun = Use("LOAD CASE DEFINITIONS").Where(r => S(r, "RunCase", "Yes").Equals("No", StringComparison.OrdinalIgnoreCase)).Select(r => S(r, "Case"))
            .Where(n => RunCases.List(m).Any(c => c.Name == n)).ToList();
        if (notRun.Count > 0) RunCases.SetNotRun(m, notRun);
        Loads(m, Use, joints, frameAt, areaAt, Where, storyNames, notes);

        // SAP2000's hinge definitions go into the Modeler's own hinge forms (DefineTables.SapHinges).
        foreach (var name in t.Keys.Where(k => !known.Contains(k) && DefineTables.IsSap(k)).ToList())
        {
            known.Add(name);
            var full = DefineTables.SapHinges.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            var fields = DefineTables.SapHinges[full];
            var rows = t[name].Select(r => r.Where(kv => fields.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) && kv.Value.Length > 0)
                .ToDictionary(kv => fields.First(f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), kv => kv.Value, StringComparer.Ordinal)).Where(r => r.Count > 0).ToList();
            if (rows.Count == 0) continue;
            m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
            m.DefinitionTables[full] = rows;
        }
        // SAP2000's preference and analysis option tables go into the Modeler's forms of them (ProgramForms), as the file has them.
        foreach (var name in t.Keys.Where(k => !known.Contains(k) && ProgramForms.HasForm(ProgramOptions.Sap2000, k)).ToList())
        {
            known.Add(name);
            var full = ProgramForms.Catalog(ProgramOptions.Sap2000).Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            var formFields = ProgramForms.Fields(ProgramOptions.Sap2000, full);
            var formRow = (t[name].FirstOrDefault() ?? []).Where(fkv => fkv.Value.Length > 0 && formFields.Exists(ff => ff.Key.Equals(fkv.Key, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(fkv => formFields.First(ff => ff.Key.Equals(fkv.Key, StringComparison.OrdinalIgnoreCase)).Key, fkv => fkv.Value, StringComparer.Ordinal);
            if (formRow.Count == 0) continue;
            m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
            m.DefinitionTables[full] = [formRow];
        }
        // A table the Modeler has no form for is kept as the file has it, and goes back into the .s2k.
        foreach (var name in t.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            if (keep) KeptTables.Keep(m, "sap2000", name, t[name]);
            else notes.Add($"The table \"{name}\" ({t[name].Count} rows) has no place in the Modeler yet; it was not read.");
        return m;
    }

    private static string Unique(IEnumerable<string> used, string name)
    {
        var set = used as ISet<string> ?? used.ToHashSet(StringComparer.Ordinal);
        if (!set.Contains(name)) return name;
        for (var i = 2; ; i++) if (!set.Contains(name + "-" + i.ToString(CultureInfo.InvariantCulture))) return name + "-" + i.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"B12_Story3" on Story3 is B12 (the name CSI ProMax gives a member on a story); other names stay as they are.</summary>
    private static string Label(string name, string story, List<string> stories)
    {
        var suffix = "_" + story;
        return name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length ? name[..^suffix.Length] : name;
    }

    private static bool Same(LineObject a, LineObject b) =>
        a.Kind == b.Kind && a.PointI == b.PointI && a.PointJ == b.PointJ && a.StoriesBelowI == b.StoriesBelowI;

    private static AreaKind AreaKindOf(List<Xyz> c)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < c.Count; i++)
        {
            var a = c[i]; var b = c[(i + 1) % c.Count];
            x += a.Y * b.Z - a.Z * b.Y; y += a.Z * b.X - a.X * b.Z; z += a.X * b.Y - a.Y * b.X;
        }
        var n = Math.Sqrt(x * x + y * y + z * z);
        if (n < 1e-12) return AreaKind.Floor;
        return Math.Abs(z) / n > 0.999 ? AreaKind.Floor : Math.Abs(z) / n < 0.001 ? AreaKind.Wall : AreaKind.Ramp;
    }

    private static void Materials(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, List<string> notes)
    {
        var basic = use("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES").GroupBy(r => S(r, "Material")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var strength = use("MATERIAL PROPERTIES 03A - STEEL DATA").Concat(use("MATERIAL PROPERTIES 03B - CONCRETE DATA")).Concat(use("MATERIAL PROPERTIES 03E - REBAR DATA"))
            .Concat(use("MATERIAL PROPERTIES 03C - ALUMINUM DATA")).Concat(use("MATERIAL PROPERTIES 03D - COLDFORMED DATA")).Concat(use("MATERIAL PROPERTIES 03F - TENDON DATA"))
            .GroupBy(r => S(r, "Material")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var r in use("MATERIAL PROPERTIES 01 - GENERAL"))
        {
            var name = S(r, "Material");
            var type = S(r, "Type") switch
            {
                "Concrete" => "Concrete", "Steel" => "Steel", "Rebar" => "Rebar", "Tendon" => "Tendon", "Aluminum" => "Aluminum", "ColdFormed" => "ColdFormed", "Masonry" => "Masonry", _ => "Other",
            };
            var values = new Dictionary<string, double>();
            if (basic.TryGetValue(name, out var b))
            {
                values["WEIGHTPERVOLUME"] = D(b, "UnitWeight");
                values["E"] = D(b, "E1");
                if (b.ContainsKey("U12")) values["U"] = D(b, "U12");
                if (b.ContainsKey("A1")) values["A"] = D(b, "A1");
            }
            if (strength.TryGetValue(name, out var s))
            {
                if (s.ContainsKey("Fc")) values["FC"] = D(s, "Fc");
                if (s.ContainsKey("Fy")) values["FY"] = D(s, "Fy");
                if (s.ContainsKey("Fu")) values["FU"] = D(s, "Fu");
                if (s.ContainsKey("EffFy")) values["FYE"] = D(s, "EffFy");
                if (s.ContainsKey("EffFu")) values["FUE"] = D(s, "EffFu");
            }
            try { PropertyDefs.SetMaterial(m, name, m.Materials.Exists(x => x.Name == name) ? name : null, type, null, values); }
            catch (ArgumentException ex) { notes.Add($"Material {name}: {ex.Message} It was kept as SAP2000 gives it, without its strengths."); m.Materials.RemoveAll(x => x.Name == name); m.Materials.Add(new Material { Name = name, Lines = [["TYPE", Quote(type), "WEIGHTPERVOLUME", Number(values.GetValueOrDefault("WEIGHTPERVOLUME"))], ["SYMTYPE", Quote("Isotropic"), "E", Number(values.GetValueOrDefault("E")), "U", Number(values.GetValueOrDefault("U", 0.2)), "A", Number(values.GetValueOrDefault("A"))]] }); }

            // Everything else the material's forms hold (curves, hysteresis, damping, time dependence, acceptance
            // criteria, its grade and notes), by ETABS 23's table and field names.
            if (m.Materials.Find(x => x.Name == name) is { } mat)
            {
                var rows = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (table, fields) in ProgramTables.MaterialTables)
                {
                    if (ProgramTables.SapMaterialTable(table) is not { } sap) continue;
                    var mine = use(sap).Where(x => S(x, "Material") == name)
                        .Select(x => fields.Where(f => x.ContainsKey(ProgramTables.SapField(f)) && f is not "Type" and not "SymType" and not "GUID").ToDictionary(f => f, f => ProgramTables.SapValue(table, f, x[ProgramTables.SapField(f)], toSap: false), StringComparer.Ordinal))
                        .Where(x => x.Count > 0).ToList();
                    if (mine.Count == 0) continue;
                    rows[table] = ProgramTables.ManyRows.Contains(table) ? mine : [mine[0]];
                }
                try { if (rows.Count > 0) ProgramTables.SetMaterialTables(mat, rows); }
                catch (ArgumentException ex) { notes.Add($"Material {name}: {ex.Message}"); }
            }
        }
    }

    private static void FrameSections(StructuralModel m, List<Dictionary<string, string>> rows, HashSet<string> beams, List<string> notes)
    {
        foreach (var r in rows)
        {
            var name = S(r, "SectionName");
            var material = S(r, "Material");
            var concrete = m.Materials.Find(x => x.Name == material)?.IsConcrete ?? false;
            double d = D(r, "t3"), b = D(r, "t2"), tf = D(r, "tf"), tw = D(r, "tw");
            var shape = S(r, "Shape");
            var dims = new Dictionary<string, double>();
            string? etabs = null;
            switch (shape)
            {
                case "Rectangular" when concrete:
                    m.FrameSections.RemoveAll(x => x.Name == name);
                    m.Passthrough.Find(p => p.Heading == "CONCRETE SECTIONS")?.Lines.RemoveAll(l => l.Contains(Quote(name) + " ", StringComparison.Ordinal));
                    m.AddRectangularSection(name, material, d, b, beams.Contains(name));
                    Modifiers(m.FrameSections[^1], r);
                    Extras(m.FrameSections[^1], r);
                    continue;
                case "Rectangular": etabs = "Steel Tube"; notes.Add($"Frame section {name} is a solid {material} rectangle in SAP2000; it opens as a tube of that size, set its real shape in Define > Section Properties."); dims = new() { ["D"] = d, ["B"] = b, ["TF"] = d / 2.01, ["TW"] = b / 2.01 }; break;
                case "Circle": etabs = concrete ? "Concrete Circle" : "Steel Rod"; dims["D"] = d; break;
                case "I/Wide Flange": etabs = "Steel I/Wide Flange"; dims = new() { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw }; if (D(r, "t2b") > 0) { dims["BBOTTOM"] = D(r, "t2b"); dims["TFBOTTOM"] = D(r, "tfb"); } break;
                case "Box/Tube": etabs = "Steel Tube"; dims = new() { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw }; break;
                case "Pipe": etabs = "Steel Pipe"; dims = new() { ["D"] = d, ["TW"] = tw > 0 ? tw : tf }; break;
                case "Channel": etabs = "Steel Channel"; dims = new() { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw }; break;
                case "Tee": etabs = concrete ? "Concrete Tee" : "Steel Tee"; dims = new() { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw }; break;
                case "Angle": etabs = "Steel Angle"; dims = new() { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw }; break;
                case "Double Angle": etabs = "Steel Double Angle"; dims = new() { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw, ["DIS"] = D(r, "dis") }; break;
                case "Double Channel": etabs = "Steel Double Channel"; dims = new() { ["D"] = d, ["B"] = b, ["TF"] = tf, ["TW"] = tw, ["DIS"] = D(r, "dis") }; break;
            }
            FrameSection? sec = null;
            if (etabs is not null)
                try { sec = PropertyDefs.SetShape(m, name, m.FrameSections.Exists(x => x.Name == name) ? name : null, etabs, material, dims); }
                catch (ArgumentException ex) { notes.Add($"Frame section {name} ({shape}): {ex.Message}"); }
            if (sec is null)
            {
                // A shape the Modeler has no form for (SAP2000's General, Section Designer, nonprismatic): kept with its
                // properties, so the members still have their section and stiffness.
                m.FrameSections.RemoveAll(x => x.Name == name);
                sec = new FrameSection { Name = name, Lines = [["MATERIAL", Quote(material), "SHAPE", Quote("General"), "D", Number(d > 0 ? d : 1), "B", Number(b > 0 ? b : 1),
                    "AREA", Number(D(r, "Area")), "AS2", Number(D(r, "AS2")), "AS3", Number(D(r, "AS3")), "I33", Number(D(r, "I33")), "I22", Number(D(r, "I22")), "J", Number(D(r, "TorsConst"))]] };
                m.FrameSections.Add(sec);
                // SAP2000's own row is kept, so the section goes back to SAP2000 as it was and its form shows it.
                var fgFields = DefineTables.SapHinges[DefineTables.SapFrameGeneral];
                DefineTables.Set(m, DefineTables.SapFrameGeneral, name, [r.Where(kv => kv.Value.Length > 0 && fgFields.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(kv => fgFields.First(f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), kv => kv.Value, StringComparer.Ordinal)]);
            }
            Modifiers(sec, r);
            Extras(sec, r);
        }
    }

    /// <summary>A frame section's colour, notes and fillet radius as SAP2000 keeps them, into the section's other fields.</summary>
    private static void Extras(FrameSection s, Dictionary<string, string> r)
    {
        var fields = PropertyDefs.FrameExtraFields(s.Shape);
        var x = new Dictionary<string, string>(StringComparer.Ordinal);
        if (S(r, "Color") is { Length: > 0 } c && c != "Yellow") x["Color"] = c;
        if (S(r, "Notes") is { Length: > 0 } n) x["Notes"] = n;
        if (fields.Contains("FilletRad") && D(r, "FilletRadius") > 0) x["FilletRad"] = S(r, "FilletRadius");
        if (x.Count > 0) PropertyDefs.SetFrameExtras(s, x);
    }

    private static void Modifiers(FrameSection sec, Dictionary<string, string> r)
    {
        var mods = new Dictionary<string, double>
        {
            ["AMOD"] = D(r, "AMod", 1), ["A2MOD"] = D(r, "A2Mod", 1), ["A3MOD"] = D(r, "A3Mod", 1), ["JMOD"] = D(r, "JMod", 1),
            ["I2MOD"] = D(r, "I2Mod", 1), ["I3MOD"] = D(r, "I3Mod", 1), ["MMOD"] = D(r, "MMod", 1), ["WMOD"] = D(r, "WMod", 1),
        };
        PropertyDefs.SetModifiers(sec.Lines, PropertyDefs.FrameModifiers, mods);
    }

    private static void ShellSections(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, List<Dictionary<string, string>>> t,
        Dictionary<string, Xyz> joints, List<string> notes)
    {
        // A section is a wall's when an upright area uses it.
        var upright = new HashSet<string>(StringComparer.Ordinal);
        var sectionOf = T(t, "AREA SECTION ASSIGNMENTS").GroupBy(r => S(r, "Area")).ToDictionary(g => g.Key, g => S(g.First(), "Section"), StringComparer.OrdinalIgnoreCase);
        foreach (var r in T(t, "CONNECTIVITY - AREA"))
        {
            var count = (int)D(r, "NumJoints", 0);
            var c = Enumerable.Range(1, count).Select(i => S(r, "Joint" + i.ToString(CultureInfo.InvariantCulture))).Where(joints.ContainsKey).Select(j => joints[j]).ToList();
            if (c.Count >= 3 && AreaKindOf(c) == AreaKind.Wall && sectionOf.TryGetValue(S(r, "Area"), out var s)) upright.Add(s);
        }
        foreach (var r in use("AREA SECTION PROPERTIES"))
        {
            var name = S(r, "Section");
            var modelling = S(r, "Type") switch { "Shell-Thick" => "ShellThick", "Membrane" => "Membrane", "Shell-Layered/Nonlinear" => "Layered", _ => "ShellThin" };
            var thickness = D(r, "Thickness", D(r, "BendThick"));
            // Plane, Asolid and the plate shells: an Engine shell section of the same name (so the areas keep it) and
            // SAP2000's own row, which goes back to SAP2000 as it was.
            var sapOnlyArea = !S(r, "AreaType", "Shell").Equals("Shell", StringComparison.OrdinalIgnoreCase) || S(r, "Type").StartsWith("Plate", StringComparison.OrdinalIgnoreCase);
            if (sapOnlyArea)
            {
                var asFields = DefineTables.SapHinges[DefineTables.SapAreaSections];
                DefineTables.Set(m, DefineTables.SapAreaSections, name, [r.Where(kv => kv.Value.Length > 0 && asFields.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(kv => asFields.First(f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), kv => kv.Value, StringComparer.Ordinal)]);
                if (thickness <= 0) thickness = 1;
                modelling = S(r, "Type").Equals("Plate-Thick", StringComparison.OrdinalIgnoreCase) ? "ShellThick" : S(r, "Type").StartsWith("Plate", StringComparison.OrdinalIgnoreCase) ? "ShellThin" : "Membrane";
            }
            try
            {
                var sec = PropertyDefs.SetShellType(m, name, m.ShellSections.Exists(x => x.Name == name) ? name : null, upright.Contains(name), PropertyDefs.SlabTypes.FirstOrDefault(x => x.Equals(S(r, "SlabType"), StringComparison.OrdinalIgnoreCase)) ?? "Slab", modelling, thickness, S(r, "Material"), false);
                var mods = new Dictionary<string, double>();
                foreach (var (k, key) in new[] { ("F11MOD", "F11Mod"), ("F22MOD", "F22Mod"), ("F12MOD", "F12Mod"), ("M11MOD", "M11Mod"), ("M22MOD", "M22Mod"), ("M12MOD", "M12Mod"), ("V13MOD", "V13Mod"), ("V23MOD", "V23Mod"), ("MMOD", "MMod"), ("WMOD", "WMod") })
                    mods[k] = D(r, key, 1);
                PropertyDefs.SetModifiers(sec.Lines, PropertyDefs.ShellModifiers, mods);
                var x = new Dictionary<string, string>(StringComparer.Ordinal);
                if (S(r, "Color") is { Length: > 0 } c && c != (sec.IsWall ? "Red" : "Cyan")) x["Color"] = c;
                if (S(r, "Notes") is { Length: > 0 } n) x["Notes"] = n;
                if (D(r, "MatAngle") != 0) x["MatAngle"] = S(r, "MatAngle");
                if (D(r, "BendThick") > 0 && Math.Abs(D(r, "BendThick") - thickness) > 1e-12) x["BendThick"] = S(r, "BendThick");
                if (x.Count > 0) PropertyDefs.SetShellExtras(sec, x);
            }
            catch (ArgumentException ex) { notes.Add($"Area section {name}: {ex.Message}"); }
        }
    }

    private static void Diaphragms(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, Xyz> joints,
        Dictionary<string, (string Label, string Story)> areaAt, Dictionary<string, List<string>> areaJoints, List<string> stories,
        Func<string, PointAssign> pointOf, List<string> notes)
    {
        var members = use("JOINT CONSTRAINT ASSIGNMENTS").Where(r => S(r, "Type").Equals("Diaphragm", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => S(r, "Constraint")).ToDictionary(g => g.Key, g => g.Select(r => S(r, "Joint")).Where(joints.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        use("CONSTRAINT DEFINITIONS - DIAPHRAGM");
        var byJoint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (constraint, set) in members)
        {
            // "D1_Story2" (one per story, as CSI ProMax writes it) is the diaphragm D1.
            var name = stories.Select(s => "_" + s).FirstOrDefault(s => constraint.EndsWith(s, StringComparison.Ordinal) && constraint.Length > s.Length) is { } suffix ? constraint[..^suffix.Length] : constraint;
            if (m.Diaphragms.TrueForAll(d => d.Name != name)) m.Diaphragms.Add(new Diaphragm { Name = name });
            foreach (var j in set) byJoint[j] = name;
        }
        var onFloors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigns = m.AreaAssigns.ToDictionary(a => (a.Label, a.Story));
        foreach (var (area, corners) in areaJoints)
        {
            if (!areaAt.TryGetValue(area, out var where) || !assigns.TryGetValue(where, out var a)) continue;
            if (m.Area(where.Label)?.Kind != AreaKind.Floor) continue;
            if (corners.All(byJoint.ContainsKey) && corners.Select(c => byJoint[c]).Distinct().Count() == 1)
            {
                a.Diaphragm = byJoint[corners[0]];
                foreach (var c in corners) onFloors.Add(c);
            }
        }
        foreach (var (j, d) in byJoint) if (!onFloors.Contains(j)) pointOf(j).Diaphragm = d;
    }

    private static List<Dictionary<string, string>> T2(Func<string, List<Dictionary<string, string>>> use, string name) => use(name);

    private static void Releases(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, (string Label, string Story)> frameAt, List<string> notes)
    {
        var assigns = m.LineAssigns.ToDictionary(a => (a.Label, a.Story));
        LineAssign? Of(Dictionary<string, string> r) => frameAt.TryGetValue(S(r, "Frame"), out var w) && assigns.TryGetValue(w, out var a) ? a : null;
        foreach (var r in use("FRAME RELEASE ASSIGNMENTS 1 - GENERAL"))
        {
            if (Of(r) is not { } a) continue;
            var keys = new[] { ("P", "P"), ("V2", "V2"), ("V3", "V3"), ("T", "T"), ("M2", "M2"), ("M3", "M3") };
            try { PropertyDefs.SetReleases(a, keys.Where(k => Yes(r, k.Item1 + "I")).Select(k => k.Item2).ToList(), keys.Where(k => Yes(r, k.Item1 + "J")).Select(k => k.Item2).ToList()); }
            catch (ArgumentException ex) { notes.Add($"Frame {S(r, "Frame")}: {ex.Message}"); }
        }
        foreach (var r in use("FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY"))
        {
            if (Of(r) is not { } a) continue;
            var pfSprings = new[] { "I", "J" }.SelectMany(pfEnd => PropertyDefs.Releases.Select(pfDof => pfDof + pfEnd)).ToDictionary(pfKey => pfKey, pfKey => D(r, pfKey), StringComparer.Ordinal);
            PropertyDefs.SetPartialFixity(a, pfSprings);
        }
        // SAP2000's design overwrites, a row per frame, as the frame's rows of that table.
        foreach (var (owTable, owFields) in ProgramForms.OverwriteCatalog(ProgramOptions.Sap2000))
            foreach (var r in use(owTable.ToUpperInvariant()))
            {
                if (Of(r) is not { } a) continue;
                var owRow = owFields.Where(f => S(r, f.Key).Length > 0).ToDictionary(f => f.Key, f => S(r, f.Key), StringComparer.Ordinal);
                if (owRow.Count > 0) AssignTables.Set(a, new Dictionary<string, List<Dictionary<string, string>>> { [owTable] = [owRow] });
            }
        foreach (var r in use("FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL"))
            if (Of(r) is { } a && D(r, "Angle") != 0) PropertyDefs.Put(a.Extra, "ANG", Number(D(r, "Angle")));
        foreach (var r in use("FRAME INSERTION POINT ASSIGNMENTS"))
        {
            if (Of(r) is not { } a) continue;
            if (Cardinal(S(r, "CardinalPt")) is { } cp) { PropertyDefs.Remove(a.Extra, "CARDINALPT"); if (cp != "10") a.Extra.Insert(0, ["CARDINALPT", cp]); }
            // Mirror, joint offsets and the stiffness transform, kept as ETABS's insertion point row when any is not the default.
            var ipRow = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (ipFrom, ipTo) in new[] { ("JtOffsetXI", "XI"), ("JtOffsetYI", "YI"), ("JtOffsetZI", "ZI"), ("JtOffsetXJ", "XJ"), ("JtOffsetYJ", "YJ"), ("JtOffsetZJ", "ZJ") })
                if (D(r, ipFrom) != 0) ipRow[ipTo] = Number(D(r, ipFrom));
            if (Yes(r, "Mirror2")) ipRow["Mirror2"] = "Yes";
            if (Yes(r, "Mirror3")) ipRow["Mirror3"] = "Yes";
            if (S(r, "Transform").Equals("No", StringComparison.OrdinalIgnoreCase)) ipRow["NoTransform"] = "Yes";
            if (ipRow.Count > 0) { ipRow["OffsetCSys"] = S(r, "CoordSys").Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) ? "Global" : "Local"; AssignTables.Set(a, new Dictionary<string, List<Dictionary<string, string>>> { [AssignTables.Insertion] = [ipRow] }); }
        }
        foreach (var r in use("FRAME OFFSET ALONG LENGTH ASSIGNMENTS"))
        {
            if (Of(r) is not { } a) continue;
            PropertyDefs.Put(a.Extra, "LENGTHOFFI", Number(D(r, "Offset1")));
            PropertyDefs.Put(a.Extra, "LENGTHOFFJ", Number(D(r, "Offset2")));
            PropertyDefs.Put(a.Extra, "RIGIDZONE", Number(D(r, "RZ")));
        }
        foreach (var r in use("FRAME PROPERTY MODIFIERS"))
            if (Of(r) is { } a)
                PropertyDefs.SetModifiers(a.Extra, PropertyDefs.FrameModifiers, new Dictionary<string, double>
                {
                    ["AMOD"] = D(r, "AMod", 1), ["A2MOD"] = D(r, "AS2Mod", 1), ["A3MOD"] = D(r, "AS3Mod", 1), ["JMOD"] = D(r, "JMod", 1),
                    ["I2MOD"] = D(r, "I22Mod", 1), ["I3MOD"] = D(r, "I33Mod", 1), ["MMOD"] = D(r, "MassMod", 1), ["WMOD"] = D(r, "WeightMod", 1),
                });
    }

    /// <summary>SAP2000's insertion point name as the number ETABS uses.</summary>
    private static string? Cardinal(string name)
    {
        string[] words = ["Bottom Left", "Bottom Center", "Bottom Right", "Middle Left", "Middle Center", "Middle Right", "Top Left", "Top Center", "Top Right", "Centroid", "Shear Center"];
        var i = Array.FindIndex(words, w => name.StartsWith(w, StringComparison.OrdinalIgnoreCase) || name.StartsWith((Array.IndexOf(words, w) + 1).ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal));
        if (i >= 0) return (i + 1).ToString(CultureInfo.InvariantCulture);
        return int.TryParse(name.Split(' ')[0], out var n) && n is >= 1 and <= 11 ? n.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>SAP2000's pattern type in ETABS's words.</summary>
    public static string PatternType(string sap) => ModelEdit.LoadPatternTypes.FirstOrDefault(t => t.Equals(sap, StringComparison.OrdinalIgnoreCase)) ?? sap.ToLowerInvariant() switch
    {
        "dead" => "Dead", "super dead" => "Super Dead", "live" => "Live", "reduce live" => "Reducible Live", "roof live" => "Roof Live",
        "quake" => "Seismic", "wind" => "Wind", "snow" => "Snow", "temperature" => "Temperature", "notional" => "Notional",
        "pattern live" => "Pattern Live", "prestress" => "Prestress-Final", "construction" => "Construction", _ => "Other",
    };

    private static void Patterns(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, List<string> notes)
    {
        foreach (var r in use("LOAD PATTERN DEFINITIONS"))
        {
            var name = S(r, "LoadPat");
            if (name.Length == 0 || m.LoadPatterns.Exists(p => p.Name == name)) continue;
            m.LoadPatterns.Add(new LoadPattern { Name = name, Type = PatternType(S(r, "DesignType")), SelfWeight = D(r, "SelfWtMult") });
            if (S(r, "AutoLoad") is { Length: > 0 } auto && !auto.Equals("None", StringComparison.OrdinalIgnoreCase))
                notes.Add($"Load pattern {name} has SAP2000's automatic {auto} load: set it again in the Modeler (Define > Load Patterns > Modify Lateral Load).");
        }
    }

    private static void Functions(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, List<string> notes)
    {
        foreach (var g in use("FUNCTION - RESPONSE SPECTRUM - USER").GroupBy(r => S(r, "Name")))
            try { AnalysisDefs.SetFunction(m, new FunctionDef(g.Key, "spectrum", g.Select(r => (D(r, "Period"), D(r, "Accel"))).ToList(), D(g.First(), "FuncDamp", 0.05))); }
            catch (ArgumentException ex) { notes.Add(ex.Message); }
        foreach (var g in use("FUNCTION - TIME HISTORY - USER").GroupBy(r => S(r, "Name")))
            try { AnalysisDefs.SetFunction(m, new FunctionDef(g.Key, "history", g.Select(r => (D(r, "Time"), D(r, "Value"))).ToList())); }
            catch (ArgumentException ex) { notes.Add(ex.Message); }
    }

    private static void Cases(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, List<string> notes)
    {
        static ILookup<string, Dictionary<string, string>> By(List<Dictionary<string, string>> rows) => rows.ToLookup(r => S(r, "Case"), StringComparer.Ordinal);
        var statics = By(use("CASE - STATIC 1 - LOAD ASSIGNMENTS"));
        var nonlinear = By(use("CASE - STATIC 4 - NONLINEAR PARAMETERS"));
        var modal = By(use("CASE - MODAL 1 - GENERAL"));
        var spectrum = By(use("CASE - RESPONSE SPECTRUM 1 - GENERAL"));
        var spectrumLoads = By(use("CASE - RESPONSE SPECTRUM 2 - LOAD ASSIGNMENTS"));
        var modalHistory = By(use("CASE - MODAL HISTORY 1 - GENERAL"));
        var modalHistoryLoads = By(use("CASE - MODAL HISTORY 2 - LOAD ASSIGNMENTS"));
        var directHistory = By(use("CASE - DIRECT HISTORY 1 - GENERAL"));
        var directHistoryLoads = By(use("CASE - DIRECT HISTORY 2 - LOAD ASSIGNMENTS"));
        var buckling = By(use("CASE - BUCKLING 1 - GENERAL"));
        var bucklingLoads = By(use("CASE - BUCKLING 2 - LOAD ASSIGNMENTS"));
        static string Dir(string u) => u.ToUpperInvariant() switch { "U1" or "UX" => "U1", "U2" or "UY" => "U2", _ => "U3" };
        static List<CaseLoad> Patterns(IEnumerable<Dictionary<string, string>> rows) => rows
            .Where(r => S(r, "LoadType", "Load pattern").StartsWith("Load", StringComparison.OrdinalIgnoreCase))
            .Select(r => new CaseLoad("pattern", S(r, "LoadName"), D(r, "LoadSF", 1), S(r, "Function") is { Length: > 0 } f ? f : null, 0, D(r, "ArrivalTime"))).ToList();
        static List<CaseLoad> Mixed(IEnumerable<Dictionary<string, string>> rows) => rows.Select(r => S(r, "LoadType").StartsWith("Acc", StringComparison.OrdinalIgnoreCase)
            ? new CaseLoad("accel", Dir(S(r, "LoadName")), D(r, "LoadSF", D(r, "TransAccSF", 1)), S(r, "Function") is { Length: > 0 } f ? f : null, D(r, "Angle"), D(r, "ArrivalTime"))
            : new CaseLoad("pattern", S(r, "LoadName"), D(r, "LoadSF", 1), S(r, "Function") is { Length: > 0 } g ? g : null, 0, D(r, "ArrivalTime"))).ToList();

        var defs = new List<CaseDef>();
        foreach (var r in use("LOAD CASE DEFINITIONS"))
        {
            var name = S(r, "Case");
            var init = S(r, "InitialCond", "Zero") is var i && !i.Equals("Zero", StringComparison.OrdinalIgnoreCase) && i.Length > 0 ? i : null;
            var modalCase = S(r, "ModalCase", "MODAL");
            CaseDef? def = S(r, "Type") switch
            {
                "LinStatic" => new CaseDef(name, "Linear Static", Patterns(statics[name])),
                "NonStatic" => new CaseDef(name, "Nonlinear Static", Patterns(statics[name]))
                {
                    InitialCase = init,
                    Geometric = nonlinear[name].Select(x => S(x, "GeoNonLin")).FirstOrDefault() switch { "P-Delta" => "P-Delta", null or "" or "None" => "None", _ => "P-Delta and Large Displacements" },
                },
                "LinModal" => modal[name].FirstOrDefault() is var mo
                    ? new CaseDef(name, mo is not null && S(mo, "ModeType").Equals("Ritz", StringComparison.OrdinalIgnoreCase) ? "Modal - Ritz" : "Modal - Eigen", [])
                    { MaxModes = (int)(mo is null ? 12 : D(mo, "MaxNumModes", 12)), MinModes = (int)(mo is null ? 1 : D(mo, "MinNumModes", 1)) }
                    : null,
                "LinRespSpec" => spectrum[name].FirstOrDefault() is var sp
                    ? new CaseDef(name, "Response Spectrum", Mixed(spectrumLoads[name]).Select(l => l with { Kind = "accel", Name = Dir(l.Name) }).ToList())
                    {
                        ModalCase = modalCase,
                        ModalCombo = sp is null ? "CQC" : S(sp, "ModalCombo", "CQC"), DirCombo = sp is null ? "SRSS" : S(sp, "DirCombo", "SRSS"),
                        Damping = sp is null ? 0.05 : D(sp, "ConstDamp", 0.05), Eccentricity = sp is null ? 0 : D(sp, "EccenRatio"),
                    }
                    : null,
                "LinModHist" or "NonModHist" => modalHistory[name].FirstOrDefault() is var mh
                    ? new CaseDef(name, S(r, "Type") == "LinModHist" ? "Linear Modal History" : "Nonlinear Modal History (FNA)", Mixed(modalHistoryLoads[name]))
                    { InitialCase = init, ModalCase = modalCase, Steps = (int)(mh is null ? 100 : D(mh, "OutSteps", 100)), StepSize = mh is null ? 0.01 : D(mh, "StepSize", 0.01), Damping = mh is null ? 0.05 : D(mh, "ConstDamp", 0.05) }
                    : null,
                "LinDirHist" or "NonDirHist" => directHistory[name].FirstOrDefault() is var dh
                    ? new CaseDef(name, S(r, "Type") == "LinDirHist" ? "Linear Direct History" : "Nonlinear Direct History", Mixed(directHistoryLoads[name]))
                    { InitialCase = init, Steps = (int)(dh is null ? 100 : D(dh, "OutSteps", 100)), StepSize = dh is null ? 0.01 : D(dh, "StepSize", 0.01) }
                    : null,
                "LinBuckling" => new CaseDef(name, "Buckling", Patterns(bucklingLoads[name])) { InitialCase = init, MaxModes = (int)(buckling[name].FirstOrDefault() is { } bk ? D(bk, "NumBuckMode", 6) : 6) },
                _ => null,
            };
            if (S(r, "Type") == "LinMoving") continue; // read with the moving loads
            if (def is null) { notes.Add($"Load case {name} is SAP2000's {S(r, "Type")} case, which the Modeler has no form for yet; it was not read."); continue; }
            defs.Add(def);
        }
        // Modal cases first, then the cases others start from: a case goes in once what it names is there.
        var pending = defs.OrderBy(d => d.Type.StartsWith("Modal", StringComparison.Ordinal) ? 0 : 1).ToList();
        while (pending.Count > 0)
        {
            var done = new List<CaseDef>();
            var errors = new Dictionary<string, string>();
            foreach (var d in pending)
                try { AnalysisDefs.SetCase(m, d); done.Add(d); }
                catch (ArgumentException ex) { errors[d.Name] = ex.Message; }
            pending.RemoveAll(done.Contains);
            if (done.Count == 0)
            {
                foreach (var d in pending) notes.Add($"Load case {d.Name}: {errors[d.Name]}");
                break;
            }
        }
    }

    private static void Combos(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, List<string> notes)
    {
        var rows = use("COMBINATION DEFINITIONS");
        var order = new List<string>();
        var byName = new Dictionary<string, LoadCombo>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var name = S(r, "ComboName");
            if (!byName.TryGetValue(name, out var c))
            {
                byName[name] = c = new LoadCombo { Name = name, Lines = [["TYPE", Quote(S(r, "ComboType", "Linear Add"))]] };
                order.Add(name);
            }
            var item = S(r, "CaseName");
            if (item.Length == 0) continue;
            c.Lines.Add([S(r, "CaseType").Equals("Response Combo", StringComparison.OrdinalIgnoreCase) ? "LOADCOMBO" : "LOADCASE", Quote(item), "SF", S(r, "ScaleFactor", "1")]);
        }
        foreach (var name in order) m.Combos.Add(byName[name]);
    }

    private static void Loads(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, Xyz> joints,
        Dictionary<string, (string Label, string Story)> frameAt, Dictionary<string, (string Label, string Story)> areaAt,
        Func<string, (int Level, PlanPoint Point)> where, List<string> stories, List<string> notes)
    {
        static string Dir(string sap) => sap.ToUpperInvariant() switch
        {
            "GRAVITY" => "GRAV", "X" => "X", "Y" => "Y", "Z" => "Z", "1" => "1", "2" => "2", "3" => "3",
            "X PROJECTED" => "XPROJ", "Y PROJECTED" => "YPROJ", "GRAVITY PROJECTED" => "GRAVPROJ", _ => "GRAV",
        };
        var lengths = Physical.Frames(m).ToDictionary(f => (f.Label, f.Story), f => f.Length);
        foreach (var r in use("FRAME LOADS - DISTRIBUTED"))
        {
            if (!frameAt.TryGetValue(S(r, "Frame"), out var f)) continue;
            if (S(r, "Type", "Force").Equals("Moment", StringComparison.OrdinalIgnoreCase)) { notes.Add($"A distributed moment on frame {S(r, "Frame")} has no place in the Modeler yet; left out."); continue; }
            double a = D(r, "FOverLA"), b = D(r, "FOverLB", a);
            double from = D(r, "RelDistA", 0), to = D(r, "RelDistB", 1);
            if (S(r, "DistType").StartsWith("Abs", StringComparison.OrdinalIgnoreCase) && lengths.TryGetValue(f, out var len) && len > 0) { from = D(r, "AbsDistA") / len; to = D(r, "AbsDistB") / len; }
            var tokens = Math.Abs(a - b) < 1e-12 && from <= 1e-9 && to >= 1 - 1e-9
                ? AnalysisDefs.LoadTokens("frame_uniform", S(r, "LoadPat"), new Dictionary<string, double> { ["value"] = a }, Dir(S(r, "Dir", "Gravity")))
                : AnalysisDefs.LoadTokens("frame_trapezoid", S(r, "LoadPat"), new Dictionary<string, double> { ["start"] = a, ["end"] = b, ["from"] = from, ["to"] = to }, Dir(S(r, "Dir", "Gravity")));
            m.LineLoads.Add(new LineLoad { Object = f.Label, Story = f.Story, Tokens = tokens });
        }
        foreach (var r in use("FRAME LOADS - POINT"))
        {
            if (!frameAt.TryGetValue(S(r, "Frame"), out var f)) continue;
            var rel = D(r, "RelDist", 0.5);
            if (S(r, "DistType").StartsWith("Abs", StringComparison.OrdinalIgnoreCase) && lengths.TryGetValue(f, out var len) && len > 0) rel = D(r, "AbsDist") / len;
            var moment = S(r, "Type").Equals("Moment", StringComparison.OrdinalIgnoreCase);
            m.LineLoads.Add(new LineLoad { Object = f.Label, Story = f.Story, Tokens = AnalysisDefs.LoadTokens("frame_point", S(r, "LoadPat"),
                new Dictionary<string, double> { [moment ? "moment" : "value"] = D(r, "Force", D(r, "Moment")), ["distance"] = rel }, Dir(S(r, "Dir", "Gravity"))) });
        }
        Temperatures(use("FRAME LOADS - TEMPERATURE"), "Frame", frameAt, "frame_temperature", (o, s, tk) => m.LineLoads.Add(new LineLoad { Object = o, Story = s, Tokens = tk }));
        foreach (var r in use("FRAME LOADS - STRAIN"))
            if (frameAt.TryGetValue(S(r, "Frame"), out var f))
                m.LineLoads.Add(new LineLoad { Object = f.Label, Story = f.Story, Tokens = AnalysisDefs.LoadTokens("frame_strain", S(r, "LoadPat"), new Dictionary<string, double> { ["strain"] = D(r, "Value") }, S(r, "Component").Replace("Strain", "", StringComparison.OrdinalIgnoreCase)) });

        foreach (var r in use("AREA LOADS - UNIFORM"))
        {
            if (!areaAt.TryGetValue(S(r, "Area"), out var a)) continue;
            m.AreaLoads.Add(new AreaLoad { Object = a.Label, Story = a.Story, Tokens = AnalysisDefs.LoadTokens("shell_uniform", S(r, "LoadPat"), new Dictionary<string, double> { ["value"] = D(r, "UnifLoad") }, Dir(S(r, "Dir", "Gravity"))) });
        }
        Temperatures(use("AREA LOADS - TEMPERATURE"), "Area", areaAt, "shell_temperature", (o, s, tk) => m.AreaLoads.Add(new AreaLoad { Object = o, Story = s, Tokens = tk }));
        foreach (var r in use("AREA LOADS - STRAIN"))
            if (areaAt.TryGetValue(S(r, "Area"), out var a))
                m.AreaLoads.Add(new AreaLoad { Object = a.Label, Story = a.Story, Tokens = AnalysisDefs.LoadTokens("shell_strain", S(r, "LoadPat"), new Dictionary<string, double> { ["strain"] = D(r, "Value") }, S(r, "Component").Replace("Strain", "", StringComparison.OrdinalIgnoreCase)) });

        void Joint(string table, string kind, (string Sap, string Key)[] keys)
        {
            foreach (var r in use(table))
            {
                var j = S(r, "Joint");
                if (!joints.ContainsKey(j)) continue;
                var w = where(j);
                var v = keys.Where(k => D(r, k.Sap) != 0).ToDictionary(k => k.Key, k => D(r, k.Sap));
                m.PointLoads.Add(new PointLoad { Object = w.Point.Name, Story = stories[w.Level], Tokens = AnalysisDefs.LoadTokens(kind, S(r, "LoadPat"), v, "") });
            }
        }
        Joint("JOINT LOADS - FORCE", "joint_force", [("F1", "fx"), ("F2", "fy"), ("F3", "fz"), ("M1", "mx"), ("M2", "my"), ("M3", "mz")]);
        Joint("JOINT LOADS - GROUND DISPLACEMENT", "joint_displacement", [("U1", "ux"), ("U2", "uy"), ("U3", "uz"), ("R1", "rx"), ("R2", "ry"), ("R3", "rz")]);
    }

    private static void Temperatures(List<Dictionary<string, string>> rows, string field, Dictionary<string, (string Label, string Story)> at, string kind, Action<string, string, List<string>> add)
    {
        // A temperature and its gradient on one object and pattern are one load in the Modeler.
        foreach (var g in rows.GroupBy(r => (S(r, field), S(r, "LoadPat"))))
        {
            if (!at.TryGetValue(g.Key.Item1, out var o)) continue;
            var v = new Dictionary<string, double>();
            foreach (var r in g)
                if (S(r, "Type").StartsWith("Gradient", StringComparison.OrdinalIgnoreCase)) v["gradient"] = D(r, "Temp"); else v["temperature"] = D(r, "Temp");
            if (!v.ContainsKey("temperature")) v["temperature"] = 0;
            add(o.Label, o.Story, AnalysisDefs.LoadTokens(kind, g.Key.Item2, v, ""));
        }
    }

    private static long Key(double v) => (long)Math.Round(v / Tol);
}
