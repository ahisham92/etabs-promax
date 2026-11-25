using System.Globalization;
using System.Text;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.S2k;

/// <summary>
/// Writes a model as a SAP2000 text file (.s2k), which licensed SAP2000 opens with File > Import > SAP2000 .s2k Text
/// File. SAP2000 has no stories: every member becomes joints and objects in 3D, stories become Z grid lines and
/// groups (one per story, so a story can still be selected), and a rigid diaphragm becomes one diaphragm constraint
/// per story. Units, materials, sections, supports, loads, load cases and combinations go across as they are.
/// What has no plain SAP2000 equal (decks, links, design preferences, pier labels) is left out and named in Notes.
/// </summary>
public static class S2kWriter
{
    /// <summary>The SAP2000 version written into the file. SAP2000 opens files from its own version and older.</summary>
    public const string ProgramVersion = "24.0.0";

    /// <summary>Fixed by the tests so the header line does not change.</summary>
    public static DateTime? SavedAt { get; set; }

    public sealed record Result(string Text, int Joints, int Frames, int Areas, IReadOnlyList<string> Notes);

    private const double Tol = 1e-6;

    public static Result WriteFile(StructuralModel m, string path)
    {
        var result = Write(m, Path.GetFileName(path));
        File.WriteAllText(path, result.Text, new UTF8Encoding(false));
        return result;
    }

    public static Result Write(StructuralModel m, string fileName = "model.s2k")
    {
        var notes = new List<string>();
        var tables = new List<string>();
        var force = Force(m.Units.Force);
        var length = Length(m.Units.Length);

        Add(tables, "PROGRAM CONTROL", Row(("ProgramName", "SAP2000"), ("Version", ProgramVersion), ("CurrUnits", $"{force}, {length}, {(m.Units.Temperature.StartsWith('F') ? "F" : "C")}")));
        Add(tables, "ACTIVE DEGREES OF FREEDOM", Row(("UX", "Yes"), ("UY", "Yes"), ("UZ", "Yes"), ("RX", "Yes"), ("RY", "Yes"), ("RZ", "Yes")));
        Add(tables, "COORDINATE SYSTEMS", Row(("Name", "GLOBAL"), ("Type", "Cartesian"), ("X", "0"), ("Y", "0"), ("Z", "0"), ("AboutZ", "0"), ("AboutY", "0"), ("AboutX", "0")));

        // Grids: the plan grids, and a Z grid line at each story level so the levels can still be found in SAP2000.
        var grids = new List<IReadOnlyList<(string, string)>>();
        foreach (var g in m.Grids)
            grids.Add(Row(("CoordSys", "GLOBAL"), ("AxisDir", g.Direction == "Y" ? "Y" : "X"), ("GridID", g.Label), ("XRYZCoord", N(g.Coordinate)),
                ("LineType", "Primary"), ("LineColor", "Gray8Dark"), ("Visible", "Yes"), ("BubbleLoc", g.Direction == "Y" ? "Start" : "End")));
        for (var i = 0; i < m.Stories.Count; i++)
            grids.Add(Row(("CoordSys", "GLOBAL"), ("AxisDir", "Z"), ("GridID", m.Stories[i].Name), ("XRYZCoord", N(m.Elevation(i))),
                ("LineType", "Primary"), ("LineColor", "Gray8Dark"), ("Visible", "Yes"), ("BubbleLoc", "End")));
        Add(tables, "GRID LINES", grids);

        Materials(m, tables, notes, m.Units.Length);
        var shapes = FrameSections(m, tables, notes);
        var shells = AreaSections(m, tables, notes);

        // Joints: every member end and corner, the supports and the loaded points, merged where they meet.
        var joints = new Dictionary<(long, long, long), string>();
        var jointRows = new List<IReadOnlyList<(string, string)>>();
        string Joint(Xyz p)
        {
            var key = (Key(p.X), Key(p.Y), Key(p.Z));
            if (joints.TryGetValue(key, out var name)) return name;
            name = (joints.Count + 1).ToString(CultureInfo.InvariantCulture);
            joints[key] = name;
            jointRows.Add(Row(("Joint", name), ("CoordSys", "GLOBAL"), ("CoordType", "Cartesian"), ("XorR", N(p.X)), ("Y", N(p.Y)), ("Z", N(p.Z)),
                ("SpecialJt", "No"), ("GlobalX", N(p.X)), ("GlobalY", N(p.Y)), ("GlobalZ", N(p.Z))));
            return name;
        }

        var groups = new Dictionary<string, List<(string Type, string Label)>>(StringComparer.Ordinal);
        void Group(string story, string type, string label)
        {
            if (!groups.TryGetValue(story, out var list)) groups[story] = list = [];
            list.Add((type, label));
        }

        var frames = Physical.Frames(m).ToList();
        var frameNames = new Dictionary<(string Label, string Story), string>();
        var connect = new List<IReadOnlyList<(string, string)>>();
        var frameSections = new List<IReadOnlyList<(string, string)>>();
        var lengths = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var f in frames)
        {
            var name = Name(f.Label, f.Story);
            frameNames[(f.Label, f.Story)] = name;
            lengths[name] = f.Length;
            connect.Add(Row(("Frame", name), ("JointI", Joint(f.I)), ("JointJ", Joint(f.J)), ("IsCurved", "No")));
            Group(f.Story, "Frame", name);
            if (f.Section is { Length: > 0 } section && shapes.TryGetValue(section, out var shape))
                frameSections.Add(Row(("Frame", name), ("SectionType", shape), ("AutoSelect", "N.A."), ("AnalSect", section), ("MatProp", "Default")));
            else notes.Add($"Frame {name} has no section that went across ({f.Section ?? "none"}); SAP2000 gives it its default.");
        }

        var areas = Physical.Areas(m).ToList();
        var areaNames = new Dictionary<(string Label, string Story), string>();
        var areaConnect = new List<IReadOnlyList<(string, string)>>();
        var areaSections = new List<IReadOnlyList<(string, string)>>();
        var diaphragms = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var a in areas)
        {
            var name = Name(a.Label, a.Story);
            areaNames[(a.Label, a.Story)] = name;
            var corners = a.Corners.Select(Joint).ToList();
            var row = new List<(string, string)> { ("Area", name), ("NumJoints", corners.Count.ToString(CultureInfo.InvariantCulture)) };
            for (var i = 0; i < corners.Count; i++) row.Add(("Joint" + (i + 1).ToString(CultureInfo.InvariantCulture), corners[i]));
            areaConnect.Add(row);
            Group(a.Story, "Area", name);
            if (a.Section is { Length: > 0 } section && shells.Contains(section))
                areaSections.Add(Row(("Area", name), ("Section", section), ("MatProp", "Default")));
            else notes.Add($"Area {name} has no section that went across ({a.Section ?? "none"}); SAP2000 gives it its default.");
            // A slab on a rigid diaphragm: its corners join that story's diaphragm constraint.
            var assign = m.AreaAssigns.Find(x => x.Label == a.Label && x.Story == a.Story);
            if (assign?.Diaphragm is { Length: > 0 } d && a.Kind == AreaKind.Floor)
            {
                var constraint = d + "_" + a.Story;
                if (!diaphragms.TryGetValue(constraint, out var set)) diaphragms[constraint] = set = [];
                foreach (var c in corners) set.Add(c);
            }
        }

        // Supports and point diaphragms.
        var restraints = new List<IReadOnlyList<(string, string)>>();
        var points = m.Points.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var pa in m.PointAssigns)
        {
            var story = m.StoryIndex(pa.Story);
            if (story < 0 || !points.TryGetValue(pa.Point, out var p)) continue;
            var joint = Joint(Physical.At(m, p, story));
            Group(pa.Story, "Joint", joint);
            if (pa.Restraint is { Length: > 0 } r)
            {
                var dofs = r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToUpperInvariant()).ToHashSet();
                restraints.Add(Row(("Joint", joint), ("U1", YesNo(dofs.Contains("UX"))), ("U2", YesNo(dofs.Contains("UY"))), ("U3", YesNo(dofs.Contains("UZ"))),
                    ("R1", YesNo(dofs.Contains("RX"))), ("R2", YesNo(dofs.Contains("RY"))), ("R3", YesNo(dofs.Contains("RZ")))));
            }
            if (pa.Diaphragm is { Length: > 0 } d)
            {
                var constraint = d + "_" + pa.Story;
                if (!diaphragms.TryGetValue(constraint, out var set)) diaphragms[constraint] = set = [];
                set.Add(joint);
            }
        }

        // Point loads need their joints before the joint table is written.
        var jointLoads = new List<IReadOnlyList<(string, string)>>();
        var jointDisps = new List<IReadOnlyList<(string, string)>>();
        foreach (var load in m.PointLoads)
        {
            var story = m.StoryIndex(load.Story);
            if (story < 0 || !points.TryGetValue(load.Object, out var p)) { notes.Add($"A point load on {load.Object} at {load.Story} points at nothing and was left out."); continue; }
            var type = E2kText.Unquote(E2kText.Value(load.Tokens, "TYPE") ?? "FORCE");
            double V(string key) => E2kText.TryNumber(E2kText.Value(load.Tokens, key) is { } t ? E2kText.Unquote(t) : null, out var v) ? v : 0;
            if (type.Equals("GROUNDDISP", StringComparison.OrdinalIgnoreCase))
            {
                jointDisps.Add(Row(("Joint", Joint(Physical.At(m, p, story))), ("LoadPat", load.Pattern), ("CoordSys", "GLOBAL"),
                    ("U1", N(V("UX"))), ("U2", N(V("UY"))), ("U3", N(V("UZ"))), ("R1", N(V("RX"))), ("R2", N(V("RY"))), ("R3", N(V("RZ")))));
                continue;
            }
            if (!type.Equals("FORCE", StringComparison.OrdinalIgnoreCase)) { notes.Add($"A {type} point load on {load.Object} at {load.Story} has no plain SAP2000 equal and was left out."); continue; }
            jointLoads.Add(Row(("Joint", Joint(Physical.At(m, p, story))), ("LoadPat", load.Pattern), ("CoordSys", "GLOBAL"),
                ("F1", N(V("FX"))), ("F2", N(V("FY"))), ("F3", N(V("FZ"))), ("M1", N(V("MX"))), ("M2", N(V("MY"))), ("M3", N(V("MZ")))));
        }

        Add(tables, "JOINT COORDINATES", jointRows);
        Add(tables, "CONNECTIVITY - FRAME", connect);
        Add(tables, "CONNECTIVITY - AREA", areaConnect);
        Add(tables, "FRAME SECTION ASSIGNMENTS", frameSections);
        Add(tables, "AREA SECTION ASSIGNMENTS", areaSections);
        Add(tables, "JOINT RESTRAINT ASSIGNMENTS", restraints);

        if (diaphragms.Count > 0)
        {
            Add(tables, "CONSTRAINT DEFINITIONS - DIAPHRAGM", diaphragms.Keys.Select(k => Row(("Name", k), ("CoordSys", "GLOBAL"), ("Axis", "Z"), ("MultiLevel", "No"))));
            Add(tables, "JOINT CONSTRAINT ASSIGNMENTS", diaphragms.SelectMany(d => d.Value.Select(j => Row(("Joint", j), ("Constraint", d.Key), ("Type", "Diaphragm")))));
        }

        // Groups, one per story.
        if (groups.Count > 0)
        {
            Add(tables, "GROUPS 1 - DEFINITIONS", m.Stories.Where(s => groups.ContainsKey(s.Name)).Select(s => Row(("GroupName", s.Name), ("Selection", "Yes"),
                ("SectionCut", "Yes"), ("Steel", "Yes"), ("Concrete", "Yes"), ("Aluminum", "Yes"), ("ColdFormed", "Yes"), ("Stage", "Yes"), ("Bridge", "Yes"),
                ("AutoSeismic", "No"), ("AutoWind", "No"), ("SelDesSteel", "No"), ("SelDesAlum", "No"), ("SelDesCold", "No"), ("MassWeight", "Yes"))));
            Add(tables, "GROUPS 2 - ASSIGNMENTS", groups.SelectMany(g => g.Value.Distinct().Select(o => Row(("GroupName", g.Key), ("ObjectType", o.Type), ("ObjectLabel", o.Label)))));
        }

        // Loads.
        var patternTypes = m.LoadPatterns.ToDictionary(p => p.Name, p => DesignType(p.Type), StringComparer.OrdinalIgnoreCase);
        Add(tables, "LOAD PATTERN DEFINITIONS", m.LoadPatterns.Select(p => Row(("LoadPat", p.Name), ("DesignType", DesignType(p.Type)), ("SelfWtMult", N(p.SelfWeight)))));

        var frameLoads = new List<IReadOnlyList<(string, string)>>();
        var framePoints = new List<IReadOnlyList<(string, string)>>();
        var frameTemps = new List<IReadOnlyList<(string, string)>>();
        var frameStrains = new List<IReadOnlyList<(string, string)>>();
        foreach (var load in m.LineLoads)
        {
            if (!frameNames.TryGetValue((load.Object, load.Story), out var frame)) { notes.Add($"A line load on {load.Object} at {load.Story} points at nothing and was left out."); continue; }
            var type = E2kText.Unquote(E2kText.Value(load.Tokens, "TYPE") ?? "");
            var dir = E2kText.Unquote(E2kText.Value(load.Tokens, "DIR") ?? "GRAV");
            double T(string key, double fallback = 0) => E2kText.TryNumber(E2kText.Value(load.Tokens, key), out var v) ? v : fallback;
            switch (type.ToUpperInvariant())
            {
                case "POINTF" or "POINTM":
                {
                    var rel = T("RDIST", 0.5);
                    framePoints.Add(Row(("Frame", frame), ("LoadPat", load.Pattern), ("CoordSys", "GLOBAL"), ("Type", type.Equals("POINTM", StringComparison.OrdinalIgnoreCase) ? "Moment" : "Force"),
                        ("Dir", Direction(dir)), ("DistType", "RelDist"), ("RelDist", N(rel)), ("AbsDist", N(rel * lengths[frame])), ("Force", N(T("FVAL")))));
                    continue;
                }
                case "TRAPF":
                {
                    double a0 = T("RDSTART", 0), b0 = T("RDEND", 1);
                    frameLoads.Add(Row(("Frame", frame), ("LoadPat", load.Pattern), ("CoordSys", "GLOBAL"), ("Type", "Force"), ("Dir", Direction(dir)),
                        ("DistType", "RelDist"), ("RelDistA", N(a0)), ("RelDistB", N(b0)), ("AbsDistA", N(a0 * lengths[frame])), ("AbsDistB", N(b0 * lengths[frame])), ("FOverLA", N(T("FSTART"))), ("FOverLB", N(T("FEND")))));
                    continue;
                }
                case "TEMP":
                    frameTemps.Add(Row(("Frame", frame), ("LoadPat", load.Pattern), ("Type", "Temperature"), ("Value", N(T("TEMP")))));
                    if (T("TEMPGRAD") != 0) frameTemps.Add(Row(("Frame", frame), ("LoadPat", load.Pattern), ("Type", "Gradient2"), ("Value", N(T("TEMPGRAD")))));
                    continue;
                case "STRAIN":
                    frameStrains.Add(Row(("Frame", frame), ("LoadPat", load.Pattern), ("Component", "Strain" + (E2kText.Value(load.Tokens, "COMPONENT") ?? "11")), ("Value", N(T("STRAIN")))));
                    continue;
            }
            if (!type.Equals("UNIFF", StringComparison.OrdinalIgnoreCase) || !E2kText.TryNumber(E2kText.Value(load.Tokens, "FVAL"), out var value))
            { notes.Add($"A {type} line load on {frame} has no plain SAP2000 equal here and was left out."); continue; }
            frameLoads.Add(Row(("Frame", frame), ("LoadPat", load.Pattern), ("CoordSys", "GLOBAL"), ("Type", "Force"), ("Dir", Direction(dir)),
                ("DistType", "RelDist"), ("RelDistA", "0"), ("RelDistB", "1"), ("AbsDistA", "0"), ("AbsDistB", N(lengths[frame])), ("FOverLA", N(value)), ("FOverLB", N(value))));
        }
        Add(tables, "FRAME LOADS - DISTRIBUTED", frameLoads);
        Add(tables, "FRAME LOADS - POINT", framePoints);
        Add(tables, "FRAME LOADS - TEMPERATURE", frameTemps);
        Add(tables, "FRAME LOADS - STRAIN", frameStrains);

        var areaLoads = new List<IReadOnlyList<(string, string)>>();
        var areaTemps = new List<IReadOnlyList<(string, string)>>();
        var areaStrains = new List<IReadOnlyList<(string, string)>>();
        foreach (var load in m.AreaLoads)
        {
            if (!areaNames.TryGetValue((load.Object, load.Story), out var area)) { notes.Add($"An area load on {load.Object} at {load.Story} points at nothing and was left out."); continue; }
            var type = E2kText.Unquote(E2kText.Value(load.Tokens, "TYPE") ?? "");
            var dir = E2kText.Unquote(E2kText.Value(load.Tokens, "DIR") ?? "GRAV");
            double T(string key) => E2kText.TryNumber(E2kText.Value(load.Tokens, key), out var v) ? v : 0;
            if (type.Equals("TEMP", StringComparison.OrdinalIgnoreCase))
            {
                areaTemps.Add(Row(("Area", area), ("LoadPat", load.Pattern), ("Type", "Temperature"), ("Value", N(T("TEMP")))));
                if (T("TEMPGRAD") != 0) areaTemps.Add(Row(("Area", area), ("LoadPat", load.Pattern), ("Type", "Gradient3"), ("Value", N(T("TEMPGRAD")))));
                continue;
            }
            if (type.Equals("STRAIN", StringComparison.OrdinalIgnoreCase))
            {
                areaStrains.Add(Row(("Area", area), ("LoadPat", load.Pattern), ("Component", "Strain" + (E2kText.Value(load.Tokens, "COMPONENT") ?? "11")), ("Value", N(T("STRAIN")))));
                continue;
            }
            if (!type.Equals("UNIFF", StringComparison.OrdinalIgnoreCase) || !E2kText.TryNumber(E2kText.Value(load.Tokens, "FVAL"), out var value))
            { notes.Add($"A {type} area load on {area} has no plain SAP2000 equal here and was left out."); continue; }
            areaLoads.Add(Row(("Area", area), ("LoadPat", load.Pattern), ("CoordSys", "GLOBAL"), ("Dir", Direction(dir)), ("UnifLoad", N(value))));
        }
        Add(tables, "AREA LOADS - UNIFORM", areaLoads);
        Add(tables, "AREA LOADS - TEMPERATURE", areaTemps);
        Add(tables, "AREA LOADS - STRAIN", areaStrains);
        Add(tables, "JOINT LOADS - FORCE", jointLoads);
        Add(tables, "JOINT LOADS - GROUND DISPLACEMENT", jointDisps);

        Functions(m, tables);
        LoadCases(m, tables, notes, patternTypes);
        Combinations(m, tables, notes);
        var source = CsiProMax.Lateral.MassSource.Of(m);
        Add(tables, "MASS SOURCE", source.Loads.Count == 0
            ? [Row(("MassSource", "MSSSRC1"), ("Elements", source.Elements ? "Yes" : "No"), ("Masses", source.AddedMass ? "Yes" : "No"), ("Loads", "No"), ("IsDefault", "Yes"))]
            : source.Loads.Select(l => Row(("MassSource", "MSSSRC1"), ("Elements", source.Elements ? "Yes" : "No"), ("Masses", source.AddedMass ? "Yes" : "No"), ("Loads", "Yes"), ("IsDefault", "Yes"), ("LoadPat", l.Pattern), ("Multiplier", N(l.Factor)))).ToList());


        var at = SavedAt ?? DateTime.Now;
        var header = $"File {fileName} was saved on {at.ToString("M/d/yy", CultureInfo.InvariantCulture)} at {at.ToString("H:mm:ss", CultureInfo.InvariantCulture)} by CSI ProMax";
        return new Result(CsiTableText.Document(header, tables), joints.Count, connect.Count, areaConnect.Count, notes.Distinct().ToList());
    }

    private static void Materials(StructuralModel m, List<string> tables, List<string> notes, string lengthUnit)
    {
        var general = new List<IReadOnlyList<(string, string)>>();
        var basic = new List<IReadOnlyList<(string, string)>>();
        var steel = new List<IReadOnlyList<(string, string)>>();
        var concrete = new List<IReadOnlyList<(string, string)>>();
        var rebar = new List<IReadOnlyList<(string, string)>>();
        var g = Gravity(lengthUnit);
        foreach (var mat in m.Materials)
        {
            var type = mat.Type.ToLowerInvariant() switch
            {
                "concrete" => "Concrete", "steel" => "Steel", "rebar" => "Rebar", "tendon" => "Tendon", "aluminum" => "Aluminum", "coldformed" => "ColdFormed", _ => "NoDesign",
            };
            var uniaxial = type is "Rebar" or "Tendon";
            general.Add(Row(("Material", mat.Name), ("Type", type), ("SymType", uniaxial ? "Uniaxial" : "Isotropic"), ("TempDepend", "No"), ("Color", type == "Concrete" ? "Gray8Dark" : "Blue")));
            var weight = mat.Number("WEIGHTPERVOLUME") ?? 0;
            var e = mat.Number("E") ?? 0;
            var u = mat.Number("U") ?? 0.2;
            var a = mat.Number("A") ?? 0;
            basic.Add(uniaxial
                ? Row(("Material", mat.Name), ("UnitWeight", N(weight)), ("UnitMass", N(weight / g)), ("E1", N(e)), ("A1", N(a)))
                : Row(("Material", mat.Name), ("UnitWeight", N(weight)), ("UnitMass", N(weight / g)), ("E1", N(e)), ("G12", N(e / (2 * (1 + u)))), ("U12", N(u)), ("A1", N(a))));
            switch (type)
            {
                case "Concrete":
                    concrete.Add(Row(("Material", mat.Name), ("Fc", N(mat.Number("FC") ?? 0)), ("LtWtConc", "No"), ("SSCurveOpt", "Mander"), ("SSHysType", "Takeda"),
                        ("SFc", "0.002"), ("SCap", "0.005"), ("FinalSlope", "-0.1"), ("FAngle", "0"), ("DAngle", "0")));
                    break;
                case "Steel":
                    var fy = mat.Number("FY") ?? 0; var fu = mat.Number("FU") ?? 0;
                    steel.Add(Row(("Material", mat.Name), ("Fy", N(fy)), ("Fu", N(fu)), ("EffFy", N(mat.Number("FYE") ?? fy)), ("EffFu", N(mat.Number("FUE") ?? fu)),
                        ("SSCurveOpt", "Simple"), ("SSHysType", "Kinematic"), ("SHard", "0.015"), ("SMax", "0.11"), ("SRup", "0.17"), ("FinalSlope", "-0.1")));
                    break;
                case "Rebar":
                    var ry = mat.Number("FY") ?? 0; var ru = mat.Number("FU") ?? 0;
                    rebar.Add(Row(("Material", mat.Name), ("Fy", N(ry)), ("Fu", N(ru)), ("EffFy", N(mat.Number("FYE") ?? ry)), ("EffFu", N(mat.Number("FUE") ?? ru)),
                        ("SSCurveOpt", "Simple"), ("SSHysType", "Kinematic"), ("SHard", "0.01"), ("SCap", "0.09"), ("FinalSlope", "-0.1"), ("UseCTDef", "No")));
                    break;
                case "NoDesign":
                    notes.Add($"Material {mat.Name} ({mat.Type}) goes across as a no-design material.");
                    break;
            }
        }
        Add(tables, "MATERIAL PROPERTIES 01 - GENERAL", general);
        Add(tables, "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", basic);
        Add(tables, "MATERIAL PROPERTIES 03A - STEEL DATA", steel);
        Add(tables, "MATERIAL PROPERTIES 03B - CONCRETE DATA", concrete);
        Add(tables, "MATERIAL PROPERTIES 03E - REBAR DATA", rebar);
    }

    /// <summary>Writes the frame sections SAP2000 can take as they are, and gives each one's SAP2000 shape name.</summary>
    private static Dictionary<string, string> FrameSections(StructuralModel m, List<string> tables, List<string> notes)
    {
        var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<IReadOnlyList<(string, string)>>();
        foreach (var s in m.FrameSections)
        {
            var shape = s.Shape.ToLowerInvariant();
            double d = s.Number("D") ?? 0, b = s.Number("B") ?? 0, tf = s.Number("TF") ?? 0, tw = s.Number("TW") ?? 0;
            double bb = s.Number("BBOTTOM") ?? b, tfb = s.Number("TFBOTTOM") ?? tf;
            Props? p;
            string name;
            if (shape.Contains("rectangular") && d > 0 && b > 0) { name = "Rectangular"; p = Rectangle(d, b); }
            else if (shape.Contains("circle") && d > 0) { name = "Circle"; p = Circle(d); }
            else if (shape.Contains("i/wide flange") && d > 0 && b > 0 && tf > 0 && tw > 0) { name = "I/Wide Flange"; p = ISection(d, b, tf, tw, bb, tfb); }
            else if ((shape.Contains("tube") || shape.Contains("box")) && d > 0 && b > 0 && tf > 0 && tw > 0) { name = "Box/Tube"; p = Tube(d, b, tf, tw); }
            else if (shape.Contains("pipe") && d > 0 && tw > 0) { name = "Pipe"; p = Pipe(d, tw); }
            else
            {
                if (m.LineAssigns.Exists(x => x.Section == s.Name))
                    notes.Add($"Frame section {s.Name} ({s.Shape}) has no plain SAP2000 shape here and was left out: define it in SAP2000 (Define > Section Properties > Frame Sections).");
                continue;
            }
            var concrete = m.Materials.Find(x => x.Name == s.Material)?.IsConcrete ?? false;
            var isBeam = m.Passthrough.Find(r => r.Heading == "CONCRETE SECTIONS")?.Lines.Any(l => l.Contains(E2kText.Quote(s.Name)) && l.Contains("\"Beam\"")) ?? false;
            shapes[s.Name] = name;
            rows.Add(Row(("SectionName", s.Name), ("Material", s.Material), ("Shape", name), ("t3", N(d)), ("t2", N(name == "Circle" || name == "Pipe" ? 0 : b)),
                ("tf", N(name is "I/Wide Flange" or "Box/Tube" ? tf : name == "Pipe" ? tw : 0)), ("tw", N(name is "I/Wide Flange" or "Box/Tube" ? tw : 0)),
                ("t2b", N(name == "I/Wide Flange" ? bb : 0)), ("tfb", N(name == "I/Wide Flange" ? tfb : 0)),
                ("Area", N(p.Value.A)), ("TorsConst", N(p.Value.J)), ("I33", N(p.Value.I33)), ("I22", N(p.Value.I22)), ("I23", "0"),
                ("AS2", N(p.Value.As2)), ("AS3", N(p.Value.As3)), ("S33", N(p.Value.S33)), ("S22", N(p.Value.S22)), ("Z33", N(p.Value.Z33)), ("Z22", N(p.Value.Z22)),
                ("R33", N(Math.Sqrt(p.Value.I33 / p.Value.A))), ("R22", N(Math.Sqrt(p.Value.I22 / p.Value.A))),
                ("ConcCol", YesNo(concrete && !isBeam)), ("ConcBeam", YesNo(concrete && isBeam)), ("Color", "Yellow"),
                ("AMod", "1"), ("A2Mod", "1"), ("A3Mod", "1"), ("JMod", "1"), ("I2Mod", "1"), ("I3Mod", "1"), ("MMod", "1"), ("WMod", "1")));
        }
        Add(tables, "FRAME SECTION PROPERTIES 01 - GENERAL", rows);
        return shapes;
    }

    private static HashSet<string> AreaSections(StructuralModel m, List<string> tables, List<string> notes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<IReadOnlyList<(string, string)>>();
        foreach (var s in m.ShellSections)
        {
            if (s.Thickness is not { } t || t <= 0 || s.Material.Length == 0)
            {
                if (m.AreaAssigns.Exists(x => x.Section == s.Name))
                    notes.Add($"Slab or wall section {s.Name} has no plain SAP2000 equal here (a deck, say) and was left out.");
                continue;
            }
            var modelling = (s.Get("MODELINGTYPE") ?? "ShellThin").ToLowerInvariant() switch
            {
                "membrane" => "Membrane", "shellthick" => "Shell-Thick", "layered" => "Shell-Thin", _ => "Shell-Thin",
            };
            names.Add(s.Name);
            rows.Add(Row(("Section", s.Name), ("Material", s.Material), ("MatAngle", "0"), ("AreaType", "Shell"), ("Type", modelling), ("DrillDOF", "Yes"),
                ("Thickness", N(t)), ("BendThick", N(t)), ("Color", s.IsWall ? "Red" : "Cyan"),
                ("F11Mod", "1"), ("F22Mod", "1"), ("F12Mod", "1"), ("M11Mod", "1"), ("M22Mod", "1"), ("M12Mod", "1"), ("V13Mod", "1"), ("V23Mod", "1"), ("MMod", "1"), ("WMod", "1")));
        }
        Add(tables, "AREA SECTION PROPERTIES", rows);
        return names;
    }

    private static void LoadCases(StructuralModel m, List<string> tables, List<string> notes, Dictionary<string, string> patternTypes)
    {
        var defs = new List<IReadOnlyList<(string, string)>>();
        var statics = new List<IReadOnlyList<(string, string)>>();
        var modal = new List<IReadOnlyList<(string, string)>>();
        var more = new Dictionary<string, List<IReadOnlyList<(string, string)>>>();
        foreach (var c in m.LoadCases)
        {
            var type = Get(c.Lines, "TYPE") ?? "";
            if (type.Equals("Linear Static", StringComparison.OrdinalIgnoreCase))
            {
                var loads = c.Lines.Where(l => l.Count >= 2 && l[0] == "LOADPAT").ToList();
                var first = loads.Count > 0 ? E2kText.Unquote(loads[0][1]) : "";
                defs.Add(Row(("Case", c.Name), ("Type", "LinStatic"), ("InitialCond", "Zero"), ("DesignType", patternTypes.GetValueOrDefault(first, "Other")), ("RunCase", "Yes")));
                foreach (var l in loads)
                    statics.Add(Row(("Case", c.Name), ("LoadType", "Load pattern"), ("LoadName", E2kText.Unquote(l[1])), ("LoadSF", E2kText.Unquote(E2kText.Value(l, "SF") ?? "1"))));
            }
            else if (type.StartsWith("Modal", StringComparison.OrdinalIgnoreCase))
            {
                defs.Add(Row(("Case", c.Name), ("Type", "LinModal"), ("InitialCond", "Zero"), ("DesignType", "Other"), ("RunCase", "Yes")));
                modal.Add(Row(("Case", c.Name), ("ModeType", type.Contains("Ritz", StringComparison.OrdinalIgnoreCase) ? "Ritz" : "Eigen"),
                    ("MaxNumModes", Get(c.Lines, "MAXMODES") ?? "12"), ("MinNumModes", Get(c.Lines, "MINMODES") ?? "1"),
                    ("EigenShift", "0"), ("EigenCutoff", "0"), ("EigenTol", "1E-09"), ("AutoShift", "Yes")));
            }
            else if (Other(m, c, type, defs, statics, more, patternTypes)) { }
            else notes.Add($"Load case {c.Name} ({type}) has no plain SAP2000 equal here and was left out: define it in SAP2000 (Define > Load Cases).");
        }
        Add(tables, "LOAD CASE DEFINITIONS", defs);
        Add(tables, "CASE - STATIC 1 - LOAD ASSIGNMENTS", statics);
        Add(tables, "CASE - MODAL 1 - GENERAL", modal);
        foreach (var (table, rows) in more) Add(tables, table, rows);
    }

    /// <summary>The functions as SAP2000's user function tables.</summary>
    private static void Functions(StructuralModel m, List<string> tables)
    {
        var fs = AnalysisDefs.ListFunctions(m);
        Add(tables, "FUNCTION - RESPONSE SPECTRUM - USER", fs.Where(f => f.Kind == "spectrum").SelectMany(f => f.Points.Select(p => Row(("Name", f.Name), ("Period", N(p.X)), ("Accel", N(p.Y)), ("FuncDamp", N(f.Damping))))));
        Add(tables, "FUNCTION - TIME HISTORY - USER", fs.Where(f => f.Kind == "history").SelectMany(f => f.Points.Select(p => Row(("Name", f.Name), ("Time", N(p.X)), ("Value", N(p.Y))))));
    }

    /// <summary>The case types beyond linear static and modal: nonlinear static, response spectrum, histories, buckling.</summary>
    private static bool Other(StructuralModel m, LoadCase c, string type, List<IReadOnlyList<(string, string)>> defs, List<IReadOnlyList<(string, string)>> statics,
        Dictionary<string, List<IReadOnlyList<(string, string)>>> more, Dictionary<string, string> patternTypes)
    {
        var d = AnalysisDefs.ReadCase(c);
        void To(string table, IReadOnlyList<(string, string)> row) { if (!more.TryGetValue(table, out var list)) more[table] = list = []; list.Add(row); }
        string Dir(string u) => u switch { "U1" => "U1", "U2" => "U2", _ => "U3" };
        var initial = d.InitialCase is { } i ? i : "Zero";
        switch (type)
        {
            case "Nonlinear Static":
                defs.Add(Row(("Case", c.Name), ("Type", "NonStatic"), ("InitialCond", initial), ("DesignType", patternTypes.GetValueOrDefault(d.Loads.FirstOrDefault()?.Name ?? "", "Other")), ("RunCase", "Yes")));
                foreach (var l in d.Loads) statics.Add(Row(("Case", c.Name), ("LoadType", "Load pattern"), ("LoadName", l.Name), ("LoadSF", N(l.Factor))));
                To("CASE - STATIC 4 - NONLINEAR PARAMETERS", Row(("Case", c.Name), ("GeoNonLin", d.Geometric switch { "P-Delta" => "P-Delta", "None" => "None", _ => "P-Delta plus Large Displacements" })));
                return true;
            case "Response Spectrum":
                defs.Add(Row(("Case", c.Name), ("Type", "LinRespSpec"), ("InitialCond", "Zero"), ("DesignType", "Quake"), ("RunCase", "Yes")));
                To("CASE - RESPONSE SPECTRUM 1 - GENERAL", Row(("Case", c.Name), ("ModalCase", d.ModalCase), ("ModalCombo", d.ModalCombo), ("DirCombo", d.DirCombo), ("DampingType", "Constant"), ("ConstDamp", N(d.Damping)), ("EccenRatio", N(d.Eccentricity))));
                foreach (var l in d.Loads)
                    To("CASE - RESPONSE SPECTRUM 2 - LOAD ASSIGNMENTS", Row(("Case", c.Name), ("LoadType", "Acceleration"), ("LoadName", Dir(l.Name)), ("CoordSys", "GLOBAL"), ("Angle", N(l.Angle)), ("TransAccSF", N(l.Factor)), ("Function", l.Function ?? "")));
                return true;
            case "Linear Modal History" or "Nonlinear Modal History (FNA)" or "Linear Direct History" or "Nonlinear Direct History":
            {
                var modalHist = type.Contains("Modal");
                var sapType = type switch { "Linear Modal History" => "LinModHist", "Nonlinear Modal History (FNA)" => "NonModHist", "Linear Direct History" => "LinDirHist", _ => "NonDirHist" };
                var prefix = modalHist ? "CASE - MODAL HISTORY" : "CASE - DIRECT HISTORY";
                defs.Add(Row(("Case", c.Name), ("Type", sapType), ("InitialCond", initial), ("DesignType", "Quake"), ("RunCase", "Yes")));
                To(prefix + " 1 - GENERAL", modalHist
                    ? Row(("Case", c.Name), ("ModalCase", d.ModalCase), ("Motion", "Transient"), ("OutSteps", d.Steps.ToString(CultureInfo.InvariantCulture)), ("StepSize", N(d.StepSize)), ("ConstDamp", N(d.Damping)))
                    : Row(("Case", c.Name), ("Motion", "Transient"), ("OutSteps", d.Steps.ToString(CultureInfo.InvariantCulture)), ("StepSize", N(d.StepSize))));
                foreach (var l in d.Loads)
                    To(prefix + " 2 - LOAD ASSIGNMENTS", l.Kind == "accel"
                        ? Row(("Case", c.Name), ("LoadType", "Accel"), ("LoadName", Dir(l.Name)), ("Function", l.Function ?? ""), ("LoadSF", N(l.Factor)), ("TimeFactor", "1"), ("ArrivalTime", N(l.Arrival)), ("CoordSys", "GLOBAL"), ("Angle", N(l.Angle)))
                        : Row(("Case", c.Name), ("LoadType", "Load pattern"), ("LoadName", l.Name), ("Function", l.Function ?? ""), ("LoadSF", N(l.Factor)), ("TimeFactor", "1"), ("ArrivalTime", N(l.Arrival))));
                return true;
            }
            case "Buckling":
                defs.Add(Row(("Case", c.Name), ("Type", "LinBuckling"), ("InitialCond", initial), ("DesignType", "Other"), ("RunCase", "Yes")));
                To("CASE - BUCKLING 1 - GENERAL", Row(("Case", c.Name), ("NumBuckModes", d.MaxModes.ToString(CultureInfo.InvariantCulture)), ("EigenTol", "1E-09")));
                foreach (var l in d.Loads) To("CASE - BUCKLING 2 - LOAD ASSIGNMENTS", Row(("Case", c.Name), ("LoadType", "Load pattern"), ("LoadName", l.Name), ("LoadSF", N(l.Factor))));
                return true;
        }
        return false;
    }

    private static void Combinations(StructuralModel m, List<string> tables, List<string> notes)
    {
        var combos = m.Combos.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cases = m.LoadCases.ToDictionary(c => c.Name, c => Get(c.Lines, "TYPE") ?? "", StringComparer.OrdinalIgnoreCase);
        var rows = new List<IReadOnlyList<(string, string)>>();
        foreach (var c in m.Combos)
        {
            var type = Get(c.Lines, "TYPE") ?? "Linear Add";
            var items = c.Lines.Where(l => l.Count >= 2 && l[0] is "LOADCASE" or "LOADCOMBO").ToList();
            var first = true;
            foreach (var l in items)
            {
                var item = E2kText.Unquote(l[1]);
                var caseType = combos.Contains(item) ? "Response Combo"
                    : cases.TryGetValue(item, out var t) && t.StartsWith("Modal", StringComparison.OrdinalIgnoreCase) ? "Linear Modal" : "Linear Static";
                var sf = E2kText.Unquote(E2kText.Value(l, "SF") ?? "1");
                rows.Add(first
                    ? Row(("ComboName", c.Name), ("ComboType", type), ("AutoDesign", "No"), ("CaseType", caseType), ("CaseName", item), ("ScaleFactor", sf),
                        ("SteelDesign", "None"), ("ConcDesign", "None"), ("AlumDesign", "None"), ("ColdDesign", "None"))
                    : Row(("ComboName", c.Name), ("CaseType", caseType), ("CaseName", item), ("ScaleFactor", sf)));
                first = false;
            }
            if (first) notes.Add($"Combination {c.Name} has no cases and was left out.");
        }
        Add(tables, "COMBINATION DEFINITIONS", rows);
    }

    public readonly record struct Props(double A, double J, double I33, double I22, double As2, double As3, double S33, double S22, double Z33, double Z22);

    /// <summary>A solid rectangle, depth d (along local 2, the strong axis 3) and width b.</summary>
    public static Props Rectangle(double d, double b)
    {
        double a = Math.Max(d, b) / 2, c = Math.Min(d, b) / 2;
        var j = a * Math.Pow(c, 3) * (16.0 / 3 - 3.36 * c / a * (1 - Math.Pow(c, 4) / (12 * Math.Pow(a, 4))));
        double i33 = b * d * d * d / 12, i22 = d * b * b * b / 12, area = b * d;
        return new Props(area, j, i33, i22, area * 5 / 6, area * 5 / 6, i33 / (d / 2), i22 / (b / 2), b * d * d / 4, d * b * b / 4);
    }

    public static Props Circle(double d)
    {
        var r = d / 2;
        var area = Math.PI * r * r;
        var i = Math.PI * Math.Pow(r, 4) / 4;
        return new Props(area, 2 * i, i, i, area * 0.9, area * 0.9, i / r, i / r, d * d * d / 6, d * d * d / 6);
    }

    public static Props Pipe(double d, double t)
    {
        double ro = d / 2, ri = Math.Max(0, ro - t);
        var area = Math.PI * (ro * ro - ri * ri);
        var i = Math.PI * (Math.Pow(ro, 4) - Math.Pow(ri, 4)) / 4;
        var z = (Math.Pow(d, 3) - Math.Pow(2 * ri, 3)) / 6;
        return new Props(area, 2 * i, i, i, area / 2, area / 2, i / ro, i / ro, z, z);
    }

    public static Props Tube(double d, double b, double tf, double tw)
    {
        double di = d - 2 * tf, bi = b - 2 * tw;
        var area = b * d - bi * di;
        double i33 = (b * d * d * d - bi * di * di * di) / 12, i22 = (d * b * b * b - di * bi * bi * bi) / 12;
        var j = 2 * tf * tw * Math.Pow(b - tw, 2) * Math.Pow(d - tf, 2) / (b * tw + d * tf - tw * tw - tf * tf);
        return new Props(area, j, i33, i22, 2 * d * tw, 2 * b * tf, i33 / (d / 2), i22 / (b / 2), (b * d * d - bi * di * di) / 4, (d * b * b - di * bi * bi) / 4);
    }

    public static Props ISection(double d, double b, double tf, double tw, double bb, double tfb)
    {
        var hw = d - tf - tfb;
        var area = b * tf + bb * tfb + hw * tw;
        // Centroid from the bottom, for flanges that may differ.
        var y = (bb * tfb * tfb / 2 + hw * tw * (tfb + hw / 2) + b * tf * (d - tf / 2)) / area;
        var i33 = b * tf * tf * tf / 12 + b * tf * Math.Pow(d - tf / 2 - y, 2)
                + bb * tfb * tfb * tfb / 12 + bb * tfb * Math.Pow(tfb / 2 - y, 2)
                + tw * hw * hw * hw / 12 + tw * hw * Math.Pow(tfb + hw / 2 - y, 2);
        var i22 = tf * b * b * b / 12 + tfb * bb * bb * bb / 12 + hw * tw * tw * tw / 12;
        var j = (b * tf * tf * tf + bb * tfb * tfb * tfb + (d - (tf + tfb) / 2) * tw * tw * tw) / 3;
        var top = Math.Max(y, d - y);
        var z33 = b * tf * (d - tf / 2 - y) + bb * tfb * (y - tfb / 2) + tw * (Math.Pow(Math.Max(0, d - tf - y), 2) + Math.Pow(Math.Max(0, y - tfb), 2)) / 2;
        var z22 = (tf * b * b + tfb * bb * bb + hw * tw * tw) / 4;
        return new Props(area, j, i33, i22, d * tw, (b * tf + bb * tfb) * 5 / 6, i33 / top, i22 / (Math.Max(b, bb) / 2), z33, z22);
    }

    private static string? Get(List<List<string>> lines, string key)
    {
        foreach (var l in lines)
            if (E2kText.Value(l, key) is { } v) return E2kText.Unquote(v);
        return null;
    }

    /// <summary>ETABS's load pattern types in SAP2000's words.</summary>
    public static string DesignType(string etabsType) => etabsType.ToLowerInvariant() switch
    {
        "dead" => "Dead", "super dead" => "Super Dead", "live" => "Live", "reducible live" => "Reduce Live", "roof live" => "Roof Live",
        "seismic" or "seismic (drift)" => "Quake", "wind" => "Wind", "snow" => "Snow", "temperature" => "Temperature", "notional" => "Notional",
        "pattern live" or "pattern live - auto" => "Pattern Live", "prestress-final" or "prestress-transfer" => "Prestress", "construction" => "Construction",
        _ => "Other",
    };

    private static string Direction(string etabsDir) => etabsDir.ToUpperInvariant() switch
    {
        "GRAV" => "Gravity", "X" => "X", "Y" => "Y", "Z" => "Z", "1" => "1", "2" => "2", "3" => "3", _ => "Gravity",
    };

    private static string Force(string unit) => unit.ToUpperInvariant() switch
    {
        "KN" => "KN", "N" => "N", "KGF" => "Kgf", "TONF" => "Tonf", "LB" => "lb", "KIP" => "Kip", _ => "KN",
    };

    private static string Length(string unit) => unit.ToUpperInvariant() switch
    {
        "M" => "m", "MM" => "mm", "CM" => "cm", "IN" or "INCH" => "in", "FT" or "FEET" => "ft", _ => "m",
    };

    /// <summary>Gravity in the model's length unit per second squared, to turn unit weight into unit mass.</summary>
    private static double Gravity(string unit) => unit.ToUpperInvariant() switch
    {
        "MM" => 9806.65, "CM" => 980.665, "IN" or "INCH" => 386.0886, "FT" or "FEET" => 32.17405, _ => 9.80665,
    };

    /// <summary>The SAP2000 name of an ETABS object on a story: label and story, which is unique and still says where it was.</summary>
    public static string Name(string label, string story) => label + "_" + story;

    private static long Key(double v) => (long)Math.Round(v / Tol);
    private static string N(double v) => CsiTableText.Number(v);
    private static string YesNo(bool v) => v ? "Yes" : "No";
    private static IReadOnlyList<(string, string)> Row(params (string, string)[] pairs) => pairs;

    private static void Add(List<string> tables, string name, IReadOnlyList<(string, string)> row) => tables.Add(CsiTableText.Table(name, [row]));

    private static void Add(List<string> tables, string name, IEnumerable<IReadOnlyList<(string, string)>> rows)
    {
        var list = rows.ToList();
        if (list.Count > 0) tables.Add(CsiTableText.Table(name, list));
    }
}
