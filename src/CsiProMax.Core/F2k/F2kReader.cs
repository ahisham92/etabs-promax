using System.Globalization;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;
using CsiProMax.S2k;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.F2k;

/// <summary>
/// Opens a SAFE text file (.f2k, .$fk) as a Modeler model. A file CSI ProMax wrote comes back exactly (the whole model
/// is carried in it, see <see cref="EmbeddedModel"/>). A file SAFE wrote is read from its tables, which are SAFE 20's
/// and later (as SAFE 20.1 and 23.1 export them): its floor becomes a story at the slab's level, with the base under it
/// where the columns and walls start, and its slabs, beams, columns, walls, supports, soil, tendons, design strips,
/// loads, load cases and combinations. SAFE's tables are put in SAP2000's words and read by the same reader, so a
/// member, a load or a case comes in the same way from either program. Nothing is asked; what has no place yet is
/// named in the notes.
/// </summary>
public static class F2kReader
{
    public static S2kReader.Result ReadFile(string path) => Read(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static S2kReader.Result Read(string text, string title = "Model")
    {
        if (EmbeddedModel.Read(text) is { } carried) return new S2kReader.Result(carried, true, []);
        var f = CsiTableText.Parse(text);
        if (f.Count == 0) throw new E2kFormatException("This is not a SAFE text file: it has no tables.");
        var notes = new List<string>();
        var (sap, used) = AsSap(f, notes);
        var m = S2kReader.Tables(sap, title, notes);
        // The notes name SAP2000's tables the translation made; say SAFE's instead.
        notes.RemoveAll(n => n.StartsWith("The table \"", StringComparison.Ordinal));
        SafeOnly(m, f, notes);
        used.UnionWith(SafeTables);
        m.Target = "safe";
        // Every other table (design preferences and overwrites, punching checks, insertion points, options, ...) is
        // kept as the file has it and goes back into the .f2k: nothing of the file is lost, and nothing is asked.
        foreach (var name in f.Keys.Where(k => !used.Contains(k) && !Derived(k)))
            KeptTables.Keep(m, "safe", name, f[name]);
        // Groups, offsets and insertion points were read for ETABS and SAP2000 too; SAFE gets them back as they were.
        foreach (var name in BothWays.Where(f.ContainsKey)) KeptTables.Keep(m, "safe", name, f[name]);
        return new S2kReader.Result(m, false, notes.Distinct().ToList());
    }

    /// <summary>What SAFE works out from the rest of the file each time it writes it (summaries, lists, discretized tendons).</summary>
    private static bool Derived(string table) =>
        table.EndsWith("SUMMARY", StringComparison.Ordinal) || table.StartsWith("MATERIAL LIST", StringComparison.Ordinal)
        || table is "TENDON DISCRETIZED POINTS" or "TENDON - TOTAL ELONGATION";

    /// <summary>Tables read into the model and also kept for the .f2k, which has no writer of its own for them yet.</summary>
    private static readonly string[] BothWays = ["GROUP DEFINITIONS", "GROUP ASSIGNMENTS", "FRAME ASSIGNMENTS - END LENGTH OFFSETS", "FRAME ASSIGNMENTS - INSERTION POINT", "LINK PROPERTY DEFINITIONS - LINEAR"];

    /// <summary>What SAFE writes about its own screen, options and summaries: nothing of the model is in them.</summary>
    private static bool Quiet(string table) =>
        table.StartsWith("PREFERENCES", StringComparison.Ordinal) || table.StartsWith("OPTIONS", StringComparison.Ordinal) || table.StartsWith("ANALYSIS OPTIONS", StringComparison.Ordinal)
        || table.EndsWith("SUMMARY", StringComparison.Ordinal) || table.StartsWith("MATERIAL LIST", StringComparison.Ordinal)
        || table is "MISCELLANEOUS OPTIONS" or "PROJECT INFORMATION" or "DATABASE TABLE NAMED SET DEFINITIONS" or "DIMENSION LINE OBJECT GEOMETRY" or "ANALYSIS MODELING OPTIONS"
            or "TENDON DISCRETIZED POINTS" or "TENDON - TOTAL ELONGATION" or "REINFORCING BAR SIZES" or "MASS SOURCE DEFINITION" or "GRID DEFINITIONS - GENERAL"
            or "FRAME SECTION PROPERTY DEFINITIONS - AUTO SELECT LIST" or "FRAME ASSIGNMENTS - OUTPUT STATIONS" or "FRAME ASSIGNMENTS - FRAME AUTO MESH OPTIONS"
            or "FRAME ASSIGNMENTS - FRAME FLOOR MESHING OPTION" or "AREA ASSIGNMENTS - FLOOR AUTO MESH OPTIONS" or "AREA ASSIGNMENTS - AUTO EDGE CONSTRAINTS";

    private static string S(Dictionary<string, string> row, string key, string fallback = "") => row.TryGetValue(key, out var v) ? v : fallback;
    private static double D(Dictionary<string, string> row, string key, double fallback = 0) =>
        row.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static (Dictionary<string, List<Dictionary<string, string>>> Sap, HashSet<string> Used) AsSap(Dictionary<string, List<Dictionary<string, string>>> f, List<string> notes)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, string>> T(string name) { used.Add(name); return f.TryGetValue(name, out var rows) ? rows : []; }
        var sap = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        void Put(string table, IEnumerable<Dictionary<string, string>> rows) { if (!sap.TryGetValue(table, out var l)) sap[table] = l = []; l.AddRange(rows); }
        static Dictionary<string, string> R(params (string K, string V)[] kv) { var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var (k, v) in kv) d[k] = v; return d; }
        static string Name(Dictionary<string, string> r) => S(r, "UniqueName", S(r, "Unique Name", S(r, "Name")));

        Put("PROGRAM CONTROL", T("PROGRAM CONTROL"));

        // Levels: the slab's level (SAFE's datum) and every other height a point is at, the base the lowest.
        var points = T("POINT OBJECT CONNECTIVITY");
        var general = T("GRID DEFINITIONS - GENERAL").FirstOrDefault();
        var datum = general is not null && general.ContainsKey("Model Datum") ? D(general, "Model Datum") : (double?)null;
        var zs = points.Select(p => D(p, "Z")).Append(datum ?? 0).Distinct().OrderBy(z => z).ToList();
        var levels = new List<double>();
        foreach (var z in zs) if (levels.Count == 0 || z - levels[^1] > 1e-4) levels.Add(z);
        var floor = datum ?? (points.Count > 0 ? points.GroupBy(p => Math.Round(D(p, "Z"), 6)).OrderByDescending(g => g.Count()).First().Key : 0);
        var floorName = general is not null && S(general, "Datum Name") is { Length: > 0 } dn ? dn : "Story1";
        var belowName = general is not null && S(general, "Story Name Below") is { Length: > 0 } bn ? bn : "Base";
        // A slab on the ground (nothing below it) still gets a base under it, a story height down.
        if (levels.Count > 0 && Math.Abs(levels[0] - floor) <= 1e-4)
        {
            var h = general is not null && D(general, "Story Height Below") > 0 ? D(general, "Story Height Below") : 1;
            levels.Insert(0, floor - h);
        }
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Put("GRID LINES", levels.Select((z, i) =>
        {
            var n = Math.Abs(z - floor) <= 1e-4 ? floorName : i == 0 ? belowName : z < floor ? $"{belowName} {i}" : $"{floorName} Above {i}";
            if (!named.Add(n)) n = n + " " + i.ToString(CultureInfo.InvariantCulture);
            return R(("AxisDir", "Z"), ("GridID", n), ("XRYZCoord", N(z)));
        }));
        Put("GRID LINES", T("GRID DEFINITIONS - GRID LINES").Select(g => R(("AxisDir", S(g, "Grid Line Type").StartsWith("Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "X"), ("GridID", S(g, "ID")), ("XRYZCoord", S(g, "Ordinate")))));

        // Materials: the same fields as SAP2000's, in tables of other names.
        Put("MATERIAL PROPERTIES 01 - GENERAL", T("MATERIAL PROPERTIES - GENERAL"));
        Put("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", T("MATERIAL PROPERTIES - BASIC MECHANICAL PROPERTIES"));
        Put("MATERIAL PROPERTIES 03A - STEEL DATA", T("MATERIAL PROPERTIES - STEEL DATA"));
        Put("MATERIAL PROPERTIES 03B - CONCRETE DATA", T("MATERIAL PROPERTIES - CONCRETE DATA"));
        Put("MATERIAL PROPERTIES 03E - REBAR DATA", T("MATERIAL PROPERTIES - REBAR DATA"));
        Put("MATERIAL PROPERTIES 03F - TENDON DATA", T("MATERIAL PROPERTIES - TENDON DATA"));

        // Frame sections, with their modifiers.
        (string, string)[] Mods(Dictionary<string, string> r) =>
        [
            ("AMod", S(r, "Area Modifier", "1")), ("A2Mod", S(r, "As2 Modifier", "1")), ("A3Mod", S(r, "As3 Modifier", "1")), ("JMod", S(r, "J Modifier", "1")),
            ("I2Mod", S(r, "I22 Modifier", "1")), ("I3Mod", S(r, "I33 Modifier", "1")), ("MMod", S(r, "Mass Modifier", "1")), ("WMod", S(r, "Weight Modifier", "1")),
        ];
        Put("FRAME SECTION PROPERTIES 01 - GENERAL", T("FRAME SECTION PROPERTY DEFINITIONS - CONCRETE RECTANGULAR").Select(r => R([("SectionName", S(r, "Name")), ("Material", S(r, "Material")), ("Shape", "Rectangular"),
            ("t3", S(r, "Depth")), ("t2", S(r, "Width")), ("ConcBeam", S(r, "Design Type") == "Beam" ? "Yes" : "No"), .. Mods(r)])));
        Put("FRAME SECTION PROPERTIES 01 - GENERAL", T("FRAME SECTION PROPERTY DEFINITIONS - CONCRETE CIRCLE").Select(r => R([("SectionName", S(r, "Name")), ("Material", S(r, "Material")), ("Shape", "Circle"),
            ("t3", S(r, "Diameter")), .. Mods(r)])));
        Put("FRAME SECTION PROPERTIES 01 - GENERAL", T("FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE").Select(r => R([("SectionName", S(r, "Name")), ("Material", S(r, "Material")), ("Shape", "I/Wide Flange"),
            ("t3", S(r, "Total Depth")), ("t2", S(r, "Top Flange Width")), ("tf", S(r, "Top Flange Thickness")), ("tw", S(r, "Web Thickness")),
            ("t2b", S(r, "Bottom Flange Width")), ("tfb", S(r, "Bottom Flange Thickness")), .. Mods(r)])));

        // Slab and wall properties.
        (string, string)[] ShellMods(Dictionary<string, string> r) =>
        [
            ("F11Mod", S(r, "f11 Modifier", "1")), ("F22Mod", S(r, "f22 Modifier", "1")), ("F12Mod", S(r, "f12 Modifier", "1")), ("M11Mod", S(r, "m11 Modifier", "1")),
            ("M22Mod", S(r, "m22 Modifier", "1")), ("M12Mod", S(r, "m12 Modifier", "1")), ("V13Mod", S(r, "v13 Modifier", "1")), ("V23Mod", S(r, "v23 Modifier", "1")),
            ("MMod", S(r, "Mass Modifier", "1")), ("WMod", S(r, "Weight Modifier", "1")),
        ];
        Put("AREA SECTION PROPERTIES", T("SLAB PROPERTY DEFINITIONS").Select(r => R([("Section", S(r, "Name")), ("Material", S(r, "Material")), ("AreaType", "Shell"),
            ("Type", S(r, "Modeling Type", "Shell-Thick")), ("Thickness", S(r, "Slab Thickness")), ("SlabType", S(r, "Property Type", "Slab")), .. ShellMods(r)])));
        Put("AREA SECTION PROPERTIES", T("WALL PROPERTY DEFINITIONS - SPECIFIED").Select(r => R([("Section", S(r, "Name")), ("Material", S(r, "Material")), ("AreaType", "Shell"),
            ("Type", S(r, "Modeling Type", "Shell-Thin")), ("Thickness", S(r, "Wall Thickness")), .. ShellMods(r)])));

        // Groups, frame end offsets and insertion points, and linear links, in SAP2000's tables.
        Put("GROUPS 1 - DEFINITIONS", T("GROUP DEFINITIONS").Select(r => R(("GroupName", S(r, "Name")), ("Color", S(r, "Color")))));
        Put("GROUPS 2 - ASSIGNMENTS", T("GROUP ASSIGNMENTS").Select(r => R(("GroupName", S(r, "Group Name")), ("ObjectType", S(r, "Object Type") switch { "Point" => "Joint", "Line" or "Beam" or "Column" or "Brace" => "Frame", "Floor" or "Wall" or "Area" => "Area", var o => o }),
            ("ObjectLabel", S(r, "Object Unique Name", S(r, "Object Label"))))));
        Put("FRAME OFFSET ALONG LENGTH ASSIGNMENTS", T("FRAME ASSIGNMENTS - END LENGTH OFFSETS")
            .Where(r => !S(r, "Offset Option", "Auto").Equals("Auto", StringComparison.OrdinalIgnoreCase) || D(r, "Offset I") != 0 || D(r, "Offset J") != 0 || D(r, "Rigid Factor") != 0)
            .Select(r => R(("Frame", Name(r)), ("LengthType", "User Defined"), ("Offset1", S(r, "Offset I", "0")), ("Offset2", S(r, "Offset J", "0")), ("RZ", S(r, "Rigid Factor", "0")))));
        Put("FRAME INSERTION POINT ASSIGNMENTS", T("FRAME ASSIGNMENTS - INSERTION POINT").Select(r => R(("Frame", Name(r)), ("CardinalPt", S(r, "Cardinal Point", "10")))));
        var links = T("LINK PROPERTY DEFINITIONS - LINEAR");
        Put("LINK PROPERTY DEFINITIONS 01 - GENERAL", links.Select(r => R(("Link", S(r, "Name")), ("LinkType", "Linear"))));
        Put("LINK PROPERTY DEFINITIONS 02 - LINEAR", links.SelectMany(r => new[] { "U1", "U2", "U3", "R1", "R2", "R3" }
            .Where(d => S(r, d + " Status", S(r, d + "Status")).Equals("Active", StringComparison.OrdinalIgnoreCase))
            .Select(d => R(("Link", S(r, "Name")), ("DOF", d), (d[0] == 'U' ? "TransKE" : "RotKE", S(r, "Stiffness " + d, "0")), (d[0] == 'U' ? "TransCE" : "RotCE", S(r, "Damping " + d, "0"))))));

        // Objects.
        Put("JOINT COORDINATES", points.Select(p => R(("Joint", Name(p)), ("XorR", S(p, "X")), ("Y", S(p, "Y")), ("Z", S(p, "Z")))));
        foreach (var table in new[] { "COLUMN OBJECT CONNECTIVITY", "BEAM OBJECT CONNECTIVITY", "BRACE OBJECT CONNECTIVITY" })
            Put("CONNECTIVITY - FRAME", T(table).Select(r => R(("Frame", Name(r)), ("JointI", S(r, "UniquePtI")), ("JointJ", S(r, "UniquePtJ")))));
        foreach (var table in new[] { "FLOOR OBJECT CONNECTIVITY", "WALL OBJECT CONNECTIVITY" })
            Put("CONNECTIVITY - AREA", T(table).Select(r =>
            {
                var corners = Enumerable.Range(1, 64).Select(i => S(r, "UniquePt" + i.ToString(CultureInfo.InvariantCulture))).TakeWhile(c => c.Length > 0).ToList();
                var row = R(("Area", Name(r)), ("NumJoints", corners.Count.ToString(CultureInfo.InvariantCulture)));
                for (var i = 0; i < corners.Count; i++) row["Joint" + (i + 1).ToString(CultureInfo.InvariantCulture)] = corners[i];
                return row;
            }));
        Put("JOINT RESTRAINT ASSIGNMENTS", T("JOINT ASSIGNMENTS - RESTRAINTS").Select(r => R(("Joint", Name(r)), ("U1", S(r, "UX")), ("U2", S(r, "UY")), ("U3", S(r, "UZ")), ("R1", S(r, "RX")), ("R2", S(r, "RY")), ("R3", S(r, "RZ")))));
        Put("FRAME SECTION ASSIGNMENTS", T("FRAME ASSIGNMENTS - SECTION PROPERTIES").Select(r => R(("Frame", Name(r)), ("AnalSect", S(r, "Section Property")))));
        Put("AREA SECTION ASSIGNMENTS", T("AREA ASSIGNMENTS - SECTION PROPERTIES").Select(r => R(("Area", Name(r)), ("Section", S(r, "Section Property")))));

        // Patterns, cases and combinations.
        Put("LOAD PATTERN DEFINITIONS", T("LOAD PATTERN DEFINITIONS").Select(r => R(("LoadPat", S(r, "Name")), ("DesignType", S(r, "Type")), ("SelfWtMult", S(r, "Self Weight Multiplier", "0")))));
        var summary = T("LOAD CASE DEFINITIONS - SUMMARY");
        var statics = T("LOAD CASE DEFINITIONS - LINEAR STATIC");
        var modal = T("MODAL CASE DEFINITIONS - EIGEN");
        var cases = new List<Dictionary<string, string>>();
        foreach (var name in statics.Select(r => S(r, "Name")).Distinct()) cases.Add(R(("Case", name), ("Type", "LinStatic")));
        foreach (var r in modal) cases.Add(R(("Case", S(r, "Name")), ("Type", "LinModal")));
        foreach (var r in summary.Where(r => S(r, "Type") is not ("Linear Static" or "Modal - Eigen" or "Hyperstatic")))
            notes.Add($"Load case {S(r, "Name")} is SAFE's {S(r, "Type")} case, which this reader does not take yet; it was not read.");
        Put("LOAD CASE DEFINITIONS", cases);
        Put("CASE - STATIC 1 - LOAD ASSIGNMENTS", statics.Where(r => S(r, "Load Type", "Load") == "Load").Select(r => R(("Case", S(r, "Name")), ("LoadType", "Load pattern"), ("LoadName", S(r, "Load Name")), ("LoadSF", S(r, "Load SF", "1")))));
        Put("CASE - MODAL 1 - GENERAL", modal.Select(r => R(("Case", S(r, "Name")), ("ModeType", "Eigen"), ("MaxNumModes", S(r, "Max Modes", "12")), ("MinNumModes", S(r, "Min Modes", "1")))));
        var combos = T("LOAD COMBINATION DEFINITIONS");
        var comboNames = combos.Select(r => S(r, "Name")).ToHashSet(StringComparer.Ordinal);
        var comboType = new Dictionary<string, string>(StringComparer.Ordinal);
        Put("COMBINATION DEFINITIONS", combos.Select(r =>
        {
            var name = S(r, "Name");
            if (S(r, "Type") is { Length: > 0 } t) comboType[name] = t;
            var item = S(r, "Load Name");
            return R(("ComboName", name), ("ComboType", comboType.GetValueOrDefault(name, "Linear Add")), ("CaseName", item), ("ScaleFactor", S(r, "SF", "1")),
                ("CaseType", comboNames.Contains(item) ? "Response Combo" : "Linear Static"));
        }));

        // Loads.
        Put("AREA LOADS - UNIFORM", T("AREA LOAD ASSIGNMENTS - UNIFORM").Select(r => R(("Area", Name(r)), ("LoadPat", S(r, "Load Pattern")), ("Dir", S(r, "Direction", "Gravity")), ("UnifLoad", S(r, "Load")))));
        Put("FRAME LOADS - DISTRIBUTED", T("FRAME LOADS ASSIGNMENTS - DISTRIBUTED").Select(r => R(("Frame", Name(r)), ("LoadPat", S(r, "Load Pattern")), ("Type", S(r, "Load Type", "Force")),
            ("Dir", S(r, "Direction", "Gravity")), ("DistType", S(r, "Distance Type", "Relative").StartsWith("Abs", StringComparison.OrdinalIgnoreCase) ? "AbsDist" : "RelDist"),
            ("RelDistA", S(r, "Relative Distance A", "0")), ("RelDistB", S(r, "Relative Distance B", "1")), ("AbsDistA", S(r, "Absolute Distance A", "0")), ("AbsDistB", S(r, "Absolute Distance B", "0")),
            ("FOverLA", S(r, "Force A", S(r, "Force"))), ("FOverLB", S(r, "Force B", S(r, "Force"))))));
        Put("FRAME LOADS - POINT", T("FRAME LOADS ASSIGNMENTS - POINT").Select(r => R(("Frame", Name(r)), ("LoadPat", S(r, "Load Pattern")), ("Type", S(r, "Load Type", "Force")),
            ("Dir", S(r, "Direction", "Gravity")), ("DistType", "RelDist"), ("RelDist", S(r, "Relative Distance", "0.5")), ("Force", S(r, "Force", S(r, "Moment"))))));
        Put("JOINT LOADS - FORCE", T("JOINT LOADS ASSIGNMENTS - FORCE").Select(r => R(("Joint", Name(r)), ("LoadPat", S(r, "Load Pattern")),
            ("F1", S(r, "FX", "0")), ("F2", S(r, "FY", "0")), ("F3", S(r, "FZ", "0")), ("M1", S(r, "MX", "0")), ("M2", S(r, "MY", "0")), ("M3", S(r, "MZ", "0")))));
        return (sap, used);
    }

    private static readonly string[] SafeTables =
    [
        "LOAD CASE DEFINITIONS - HYPERSTATIC", "SPRING PROPERTY DEFINITIONS - AREA SPRINGS", "AREA ASSIGNMENTS - AREA SPRINGS", "TENDON SECTION PROPERTIES",
        "TENDON ASSIGNMENTS - PROPERTIES", "TENDON LOAD ASSIGNMENTS - JACKING STRESS", "TENDON LOAD ASSIGNMENTS - LOSS OPTIONS", "TENDON OBJECT CONNECTIVITY",
        "CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED", "STRIP OBJECT CONNECTIVITY",
    ];

    /// <summary>What SAFE has that SAP2000's tables do not: hyperstatic cases, soil, tendons and design strips.</summary>
    private static void SafeOnly(StructuralModel m, Dictionary<string, List<Dictionary<string, string>>> f, List<string> notes)
    {
        List<Dictionary<string, string>> T(string name) => f.TryGetValue(name, out var rows) ? rows : [];
        // Decks, as ETABS keeps them (SHELLPROP ... PROPTYPE "Deck"), so slabs that use one keep their section.
        foreach (var r in T("DECK PROPERTY DEFINITIONS"))
        {
            var name = S(r, "Name");
            m.ShellSections.RemoveAll(x => x.Name == name);
            string Num(string key) => E2kText.Number(D(r, key));
            m.ShellSections.Add(new ShellSection { Name = name, Lines = [["PROPTYPE", E2kText.Quote("Deck"), "DECKTYPE", E2kText.Quote(S(r, "Deck Type", "Filled")), "CONCMATERIAL", E2kText.Quote(S(r, "Slab Material")),
                "DECKMATERIAL", E2kText.Quote(S(r, "Deck Material")), "DECKSLABDEPTH", Num("Slab Depth"), "DECKRIBDEPTH", Num("Rib Depth"), "DECKRIBWIDTHTOP", Num("Rib Width Top"),
                "DECKRIBWIDTHBOTTOM", Num("Rib Width Bottom"), "DECKRIBSPACING", Num("Rib Spacing"), "DECKSHEARTHICKNESS", Num("Deck Shear Thickness"), "DECKUNITWEIGHT", Num("Deck Unit Weight"),
                "SHEARSTUDDIAM", Num("Shear Stud Diameter"), "SHEARSTUDHEIGHT", Num("Shear Stud Height"), "SHEARSTUDFU", Num("Shear Stud Tensile Strength")]] });
        }
        foreach (var r in T("LOAD CASE DEFINITIONS - HYPERSTATIC"))
            try { SlabDefs.SetHyperstatic(m, S(r, "Name"), S(r, "Base Case"), D(r, "Relative SF", 1)); }
            catch (ArgumentException ex) { notes.Add($"Hyperstatic case {S(r, "Name")}: {ex.Message}"); }
        // SAFE keeps the combinations' order with the cases they name; one naming a hyperstatic case read now is fine.

        // Soil under the slabs: area springs.
        foreach (var r in T("SPRING PROPERTY DEFINITIONS - AREA SPRINGS"))
            try { PropertyDefs.SetSpring(m, "area", S(r, "Name"), new Dictionary<string, double> { ["U1"] = D(r, "Stiffnes U1", D(r, "Stiffness U1")), ["U2"] = D(r, "Stiffnes U2", D(r, "Stiffness U2")), ["U3"] = D(r, "Stiffnes U3", D(r, "Stiffness U3")) }); }
            catch (ArgumentException ex) { notes.Add($"Area spring {S(r, "Name")}: {ex.Message}"); }
        var floorStory = m.Stories.Count > 1 ? m.Stories.Select((s, i) => (s.Name, i)).OrderByDescending(x => m.AreaAssigns.Count(a => a.Story == x.Name)).First().Name : m.Stories[0].Name;
        var areaAssigns = m.AreaAssigns.GroupBy(a => a.Label).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var r in T("AREA ASSIGNMENTS - AREA SPRINGS"))
            if (areaAssigns.TryGetValue(S(r, "UniqueName", S(r, "Unique Name")), out var a)) PropertyDefs.Put(a.Extra, "SPRINGPROP", Quote(S(r, "Spring Property")));

        // Points by name, for strips and tendons.
        var points = T("POINT OBJECT CONNECTIVITY").GroupBy(p => S(p, "UniqueName", S(p, "Unique Name"))).ToDictionary(g => g.Key, g => new[] { D(g.First(), "X"), D(g.First(), "Y") }, StringComparer.OrdinalIgnoreCase);

        // Tendon sections, then the tendons with their profile, jacking and losses.
        foreach (var r in T("TENDON SECTION PROPERTIES"))
            try { SlabDefs.SetTendonSection(m, S(r, "Name"), S(r, "Material"), D(r, "StrandArea")); }
            catch (ArgumentException ex) { notes.Add($"Tendon section {S(r, "Name")}: {ex.Message}"); }
        var props = T("TENDON ASSIGNMENTS - PROPERTIES").GroupBy(r => S(r, "Name")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var jacks = T("TENDON LOAD ASSIGNMENTS - JACKING STRESS").GroupBy(r => S(r, "Name")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var losses = T("TENDON LOAD ASSIGNMENTS - LOSS OPTIONS").GroupBy(r => S(r, "Name")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var g in T("TENDON OBJECT CONNECTIVITY").GroupBy(r => S(r, "Name")))
        {
            var plan = new List<double[]>();
            foreach (var r in g)
            {
                double[]? p = r.ContainsKey("Global X") ? [D(r, "Global X"), D(r, "Global Y")] : S(r, "Point Name") is { Length: > 0 } pn && points.TryGetValue(pn, out var pp) ? pp : null;
                if (p is not null && (plan.Count == 0 || Math.Abs(plan[^1][0] - p[0]) + Math.Abs(plan[^1][1] - p[1]) > 1e-9)) plan.Add(p);
            }
            var spans = g.Where(r => r.ContainsKey("Span Number") && D(r, "L") > 0).Select(r =>
            {
                var l = D(r, "L");
                return new SlabDefs.SpanInput(l, -D(r, "ZL"), -D(r, "ZC"), -D(r, "ZR"), D(r, "A") / l, D(r, "B") / l, D(r, "C") / l);
            }).ToList();
            try
            {
                var p = props.GetValueOrDefault(g.Key);
                var jack = jacks.TryGetValue(g.Key, out var j)
                    ? new SlabDefs.TendonJack(S(j, "Transfer Load Pattern"), S(j, "Final Load Pattern"), D(j, "Jacking Stress"), SlabDefs.JackEnds.FirstOrDefault(e => e.F2k == S(j, "Jacking Location")).Key ?? "J")
                    : null;
                var loss = losses.TryGetValue(g.Key, out var lo)
                    ? S(lo, "Loss Method") == "Percent"
                        ? new SlabDefs.TendonLoss("Percent", D(lo, "Stressing Loss"), D(lo, "Long Term Loss"))
                        : new SlabDefs.TendonLoss("Fixed", D(lo, "Stressing Loss Fixed"), D(lo, "Long Term Loss Fixed"))
                    : null;
                SlabDefs.SetTendon(m, g.Key, floorStory, plan, spans, p is null ? "" : S(p, "Tendon Property"), p is null ? 1 : (int)D(p, "Number Strands", 1),
                    p is not null && S(p, "Bonding Options") == "Bonded", jack, loss);
            }
            catch (ArgumentException ex) { notes.Add($"Tendon {g.Key}: {ex.Message}"); }
        }

        // Design strips and their overwrites.
        var overwrites = T("CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED").GroupBy(r => S(r, "Strip Name")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var g in T("STRIP OBJECT CONNECTIVITY").GroupBy(r => S(r, "Name")))
        {
            var first = g.First();
            var names = new List<string> { S(first, "Strip Start Point") };
            names.AddRange(g.Select(r => S(r, "Segment End Point")));
            var plan = names.Where(points.ContainsKey).Select(n => points[n]).ToList();
            var ow = overwrites.GetValueOrDefault(g.Key);
            try
            {
                SlabDefs.SetStrip(m, g.Key, floorStory, S(first, "Layer", "A") is "B" ? "B" : "A", plan, D(first, "Start Width Left"), D(first, "Start Width Right"),
                    ow is null ? "Column" : S(ow, "Strip Design Type", "Column Strip"), ow is null || S(ow, "Design?", "Yes") == "Yes", ow is not null && S(ow, "Ignote PT?", S(ow, "Ignore PT?")) == "Yes",
                    ow is null ? "" : S(ow, "Rebar Material"), ow is null ? 1 : D(ow, "LLRF", 1));
            }
            catch (ArgumentException ex) { notes.Add($"Design strip {g.Key}: {ex.Message}"); }
        }
    }
}
