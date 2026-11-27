using System.Globalization;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.F2k;

/// <summary>
/// The tables SAFE writes into every .f2k besides the model's objects (Ahmed 2026-11-27: identical to SAFE's own
/// export): rebar sizes, auto select lists, named table sets, dimension lines, project information, the objects'
/// meshing, insertion and output station assignments, SAFE's summaries and material lists, design overwrites,
/// design preferences and SAFE's analysis, display and colour options. What the model has (its .e2k sections) is
/// written from it; what it has not is written as SAFE 20.1 writes it for a new model (its ACI 318-19 PT-SL and
/// RC-PN examples, lb and in), lengths in the model's units. Tables go in SAFE's order.
/// </summary>
internal static class F2kSafeTables
{
    private static List<(string, string)> Row(params (string Key, string Value)[] pairs) => [.. pairs];
    private static string N(double v) => CsiTableText.Number(v);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The model's lines of one .e2k section, each tokenized.</summary>
    private static IEnumerable<List<string>> Lines(StructuralModel m, string heading) =>
        m.Passthrough.Where(r => r.Heading == heading).SelectMany(r => r.Lines).Select(Tokenize).Where(t => t.Count > 0);

    /// <summary>Every KEY value pair of a line's tokens after the first <paramref name="skip"/>.</summary>
    private static Dictionary<string, string> Pairs(List<string> t, int skip)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = skip; i + 1 < t.Count; i += 2) d[t[i]] = Unquote(t[i + 1]);
        return d;
    }

    private static Dictionary<string, string> AllPairs(StructuralModel m, string heading, string word)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Lines(m, heading).Where(t => t[0] == word)) foreach (var (k, v) in Pairs(t, 1)) d[k] = v;
        return d;
    }

    private static string YesNo(string? v, string fallback) => v is null ? fallback : v.Equals("YES", StringComparison.OrdinalIgnoreCase) ? "Yes" : v.Equals("NO", StringComparison.OrdinalIgnoreCase) ? "No" : v;

    /// <summary>Inches to the model's length unit.</summary>
    private static double Inch(StructuralModel m) =>
        ModelUnits.LengthM("in") is { } inch && ModelUnits.LengthM(m.Units.Length) is { } unit && unit > 0 ? inch / unit : 1;

    /// <summary>The fields of SAFE's default tables that are lengths (converted from inches).</summary>
    private static readonly HashSet<string> Lengths = new(StringComparer.OrdinalIgnoreCase)
    {
        "Maximum Mesh Size", "MergeTol", "CoverTop", "CoverBot", "PTCGSTop", "PTCGSBotExt", "PTCGSBotInt", "Min. Long. Spacing", "Max. Long. Spacing", "Min. Trans. Spacing",
        "Min. Beam Depth", "Min. Web Thick.", "Min. Beam Span", "Min. Camber, abs", "Camber Abs. Max Limit", "Camber Interval",
    };

    /// <summary>SAFE 20.1's own tables of a new model (lb, in), as its exports of the ACI 318-19 examples have them.</summary>
    private static readonly Lazy<Dictionary<string, List<Dictionary<string, string>>>> Defaults = new(() => CsiTableText.Parse(DefaultText));

    private static List<Dictionary<string, string>> Default(StructuralModel m, string table)
    {
        var f = Inch(m);
        return Defaults.Value[table].Select(r => r.ToDictionary(kv => kv.Key, kv => Lengths.Contains(kv.Key) && Math.Abs(f - 1) > 1e-12 && double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? N(v * f) : kv.Value, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static string Table(string name, IEnumerable<IEnumerable<(string, string)>> rows) =>
        CsiTableText.Table(name, rows.Select(r => (IReadOnlyList<(string, string)>)r.ToList()));

    private static string Table(string name, IEnumerable<Dictionary<string, string>> rows) => Table(name, rows.Select(r => r.Select(kv => (kv.Key, kv.Value))));

    /// <summary>
    /// Adds what SAFE writes that the tables have not got yet, then puts every table in SAFE's order.
    /// <paramref name="areaOf"/> and <paramref name="frameOf"/> name the model's object behind each SAFE floor and frame.
    /// </summary>
    public static List<string> Complete(StructuralModel m, string story, double z, List<string> tables,
        IReadOnlyDictionary<string, (string Label, string Story)> areaOf, IReadOnlyDictionary<string, (string Label, string Story, string Type)> frameOf)
    {
        var have = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables) foreach (var (k, v) in CsiTableText.Parse(t)) have[k] = v;
        List<Dictionary<string, string>> T(string name) => have.TryGetValue(name, out var r) ? r : [];
        void Add(string name, string text) { if (!have.ContainsKey(name)) { tables.Add(text); foreach (var (k, v) in CsiTableText.Parse(text)) have[k] = v; } }
        string S(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";
        double D(Dictionary<string, string> r, string k) => double.TryParse(S(r, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var inch = Inch(m);

        // ---- rebar sizes (Define > Reinforcing Bar Sizes) ----
        var bars = Lines(m, "REBAR DEFINITIONS").Where(t => t[0] == "REBARDEFINITION" && t.Count >= 2).Select(t => (Name: Unquote(t[1]), P: Pairs(t, 2))).ToList();
        if (bars.Count > 0)
            Add("REINFORCING BAR SIZES", Table("REINFORCING BAR SIZES", bars.Select(b => Row(("Name", b.Name), ("Diameter", b.P.GetValueOrDefault("DIA") ?? "0"), ("Area", b.P.GetValueOrDefault("AREA") ?? "0")))));

        // ---- auto select lists: the first row with the list's design type, file and starting section ----
        var steelFile = T("FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE").Where(r => S(r, "File Name").Length > 0).GroupBy(r => S(r, "Name")).ToDictionary(g => g.Key, g => S(g.First(), "File Name"), StringComparer.OrdinalIgnoreCase);
        var autoMembers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Lines(m, "AUTO SELECT SECTION LISTS").Where(t => t[0] == "AUTOSECTION" && t.Count >= 2))
        {
            var name = Unquote(t[1]);
            if (!autoMembers.TryGetValue(name, out var list)) autoMembers[name] = list = [];
            list.AddRange(t.Skip(2).Select(Unquote));
        }
        var autos = m.FrameSections.Where(s => s.Shape.Equals("Auto Select", StringComparison.OrdinalIgnoreCase) && autoMembers.ContainsKey(s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        if (autos.Count > 0)
            Add("FRAME SECTION PROPERTY DEFINITIONS - AUTO SELECT LIST", Table("FRAME SECTION PROPERTY DEFINITIONS - AUTO SELECT LIST", autos.SelectMany(s =>
            {
                var members = autoMembers[s.Name];
                var file = members.Select(x => steelFile.GetValueOrDefault(x)).FirstOrDefault(x => x is not null);
                var first = Row(("Name", s.Name), ("DesignType", s.Get("AUTOSELECTDESIGNTYPE") ?? "Steel"), ("From File?", file is null ? "No" : "Yes"));
                if (file is not null) first.Add(("File Name", file));
                first.Add(("Starting Section", s.Get("STARTSECTION") ?? "Median"));
                first.Add(("Included Section", members.FirstOrDefault() ?? ""));
                return new[] { first }.Concat(members.Skip(1).Select(x => Row(("Name", s.Name), ("Included Section", x))));
            })));

        // ---- named sets of database tables ----
        var sets = new List<(string Name, Dictionary<string, string> Options, List<string> Keys, List<string> Patterns, List<string> Cases, List<string> Combos)>();
        foreach (var t in Lines(m, "DATABASE TABLE NAMED SETS").Where(t => t.Count >= 3))
        {
            var name = Unquote(t[1]);
            var set = sets.Find(x => x.Name == name);
            if (set.Name is null) { set = (name, new(StringComparer.OrdinalIgnoreCase), [], [], [], []); sets.Add(set); }
            switch (t[0])
            {
                case "DBTABLESET": foreach (var (k, v) in Pairs(t, 2)) set.Options[k] = v; break;
                case "DBTABLESETTABLEKEY": set.Keys.Add(Unquote(t[2])); break;
                case "DBTABLESETLOADPATTERN": set.Patterns.Add(Unquote(t[2])); break;
                case "DBTABLESETLOADCASE": set.Cases.Add(Unquote(t[2])); break;
                case "DBTABLESETCOMBO": set.Combos.Add(Unquote(t[2])); break;
            }
        }
        if (sets.Count > 0)
            Add("DATABASE TABLE NAMED SET DEFINITIONS", Table("DATABASE TABLE NAMED SET DEFINITIONS", sets.SelectMany(s =>
            {
                var n = new[] { s.Keys.Count, s.Patterns.Count, s.Cases.Count, s.Combos.Count, 1 }.Max();
                return Enumerable.Range(0, n).Select(i =>
                {
                    var row = Row(("Name", s.Name));
                    if (i < s.Keys.Count) row.Add(("Table Key", s.Keys[i]));
                    if (i < s.Patterns.Count) row.Add(("Load Pattern", s.Patterns[i]));
                    if (i < s.Cases.Count) row.Add(("Load Case", s.Cases[i]));
                    if (i < s.Combos.Count) row.Add(("Combo", s.Combos[i]));
                    if (i == 0)
                    {
                        string O(string k, string f) => s.Options.GetValueOrDefault(k) ?? f;
                        row.AddRange([("Base Reaction Option", YesNo(O("ISUSERBASEREAC", "No"), "No") == "Yes" ? "User Specified" : "Program Determined"),
                            ("Mode Option", YesNo(O("ISALLMODES", "Yes"), "Yes") == "Yes" ? "All" : "Range"), ("Buckling Mode Option", YesNo(O("ISALLBUCKLINGMODES", "Yes"), "Yes") == "Yes" ? "All" : "Range"),
                            ("Multistep", O("MULTISTEP", "Envelopes")), ("Nonlinear Static", O("NLSTATIC", "Envelopes")), ("Modal History", O("MODALHIST", "Envelopes")), ("Direct History", O("DIRECTHIST", "Envelopes")),
                            ("Load Combination", O("LOADCOMBO", "Envelopes")), ("Show Unformatted", YesNo(O("UNFORMATTED", "No"), "No")), ("Expose All", YesNo(O("EXPOSEALL", "Yes"), "Yes"))]);
                    }
                    return row;
                });
            })));

        // ---- default dimension lines along the grids: a line between each two neighbouring grid lines, on the side of
        // the first one's bubble, as SAFE draws them (Y grids first, then X), 0.8 of a bubble outside the grids ----
        var xs = m.Grids.Where(g => g.Direction != "Y").OrderBy(g => g.Coordinate).ToList();
        var ys = m.Grids.Where(g => g.Direction == "Y").OrderBy(g => g.Coordinate).ToList();
        if (xs.Count > 0 && ys.Count > 0 && Lines(m, "DIMENSION LINES").Any(t => t.Count >= 2 && t[0] == "DIMLINE" && t[1] == "DEFAULTSYSTEM"))
        {
            var bubble = BubbleSize(m) * 0.8;
            var dims = new List<List<(string, string)>>();
            List<(string, string)> Dim(double x1, double y1, double x2, double y2, double dx, double dy) =>
                Row(("Type", "Default"), ("Grid System", "G1"), ("Drawn X1", N(x1)), ("Drawn Y1", N(y1)), ("Drawn X2", N(x2)), ("Drawn Y2", N(y2)),
                    ("Display X1", N(x1 + dx)), ("Display Y1", N(y1 + dy)), ("Display X2", N(x2 + dx)), ("Display Y2", N(y2 + dy)), ("Length", N(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)))));
            for (var i = 0; i + 1 < ys.Count; i++)
            {
                var low = Bubble(ys[i]) == "Start";
                var x = low ? xs[0].Coordinate : xs[^1].Coordinate;
                dims.Add(Dim(x, ys[i].Coordinate, x, ys[i + 1].Coordinate, low ? -bubble : bubble, 0));
            }
            for (var i = 0; i + 1 < xs.Count; i++)
            {
                var low = Bubble(xs[i]) == "Start";
                var y = low ? ys[0].Coordinate : ys[^1].Coordinate;
                dims.Add(Dim(xs[i].Coordinate, y, xs[i + 1].Coordinate, y, 0, low ? -bubble : bubble));
            }
            Add("DIMENSION LINE OBJECT GEOMETRY", Table("DIMENSION LINE OBJECT GEOMETRY", dims));
        }

        // ---- the objects' assignments SAFE writes for each of them ----
        var floors = T("FLOOR OBJECT CONNECTIVITY").Select(r => S(r, "Unique Name").Length > 0 ? S(r, "Unique Name") : S(r, "UniqueName")).ToList();
        AreaAssign? AreaAssignOf(string name) => areaOf.TryGetValue(name, out var o) ? m.AreaAssigns.Find(a => a.Label == o.Label && string.Equals(a.Story, o.Story, StringComparison.OrdinalIgnoreCase)) : null;
        string? AreaValue(string name, string key) => AreaAssignOf(name) is { } a ? PropertyDefs.Value(a.Extra, key) is { } v ? Unquote(v) : null : null;
        bool AreaHas(string name, string key, string value) => AreaAssignOf(name) is { } a && a.Extra.Any(l => Enumerable.Range(0, Math.Max(0, l.Count - 1)).Any(i => l[i] == key && Unquote(l[i + 1]).Equals(value, StringComparison.OrdinalIgnoreCase)));
        if (floors.Count > 0)
        {
            Add("AREA ASSIGNMENTS - INSERTION POINT", Table("AREA ASSIGNMENTS - INSERTION POINT", floors.Select(f => Row(("UniqueName", f),
                ("Cardinal Point", (AreaValue(f, "CARDINALPOINT") ?? "TOP").ToUpperInvariant() switch { "MIDDLE" => "Middle", "BOTTOM" => "Bottom", _ => "Top" }),
                ("Transform", YesNo(AreaValue(f, "TRANSFORMSTIFFNESSFOROFFSETS"), "Yes"))))));
            Add("AREA ASSIGNMENTS - FLOOR AUTO MESH OPTIONS", Table("AREA ASSIGNMENTS - FLOOR AUTO MESH OPTIONS", floors.Select(f =>
            {
                var size = AreaValue(f, "MAXMESHSIZE");
                return Row(("UniqueName", f), ("Mesh Option", (AreaValue(f, "OBJMESHTYPE") ?? "AUTOMESH").ToUpperInvariant() switch { "NOMESH" or "NONE" => "No Mesh", _ => "Auto Cookie Cut" }),
                    ("At Beams", AreaHas(f, "MESHAT", "BEAMS") || AreaAssignOf(f) is null ? "Yes" : "No"), ("At Walls", AreaHas(f, "MESHAT", "WALLS") || AreaAssignOf(f) is null ? "Yes" : "No"),
                    ("At Grids", AreaHas(f, "MESHAT", "GRIDS") ? "Yes" : "No"), ("Submesh", "Yes"), ("Submesh Size", size ?? N(MeshSize(m) ?? 36 * inch)), ("Add Restraints", YesNo(AreaValue(f, "ADDRESTRAINT"), "No")));
            })));
            Add("AREA ASSIGNMENTS - AUTO EDGE CONSTRAINTS", Table("AREA ASSIGNMENTS - AUTO EDGE CONSTRAINTS", floors.Select(f => Row(("UniqueName", f), ("Constraint", YesNo(AreaValue(f, "EDGECONSTRAINT"), "Yes"))))));
        }
        var frames = new[] { "COLUMN OBJECT CONNECTIVITY", "BEAM OBJECT CONNECTIVITY", "BRACE OBJECT CONNECTIVITY" }.SelectMany(t => T(t).Select(r => (Name: S(r, "Unique Name").Length > 0 ? S(r, "Unique Name") : S(r, "UniqueName"), Length: D(r, "Length")))).ToList();
        LineAssign? LineAssignOf(string name) => frameOf.TryGetValue(name, out var o) ? m.LineAssigns.Find(a => a.Label == o.Label && string.Equals(a.Story, o.Story, StringComparison.OrdinalIgnoreCase)) : null;
        string? LineValue(string name, string key) => LineAssignOf(name) is { } a ? PropertyDefs.Value(a.Extra, key) is { } v ? Unquote(v) : null : null;
        if (frames.Count > 0)
        {
            var sections = T("FRAME ASSIGNMENTS - SECTION PROPERTIES").GroupBy(r => S(r, "UniqueName")).ToDictionary(g => g.Key, g => S(g.First(), "Section Property"), StringComparer.OrdinalIgnoreCase);
            Add("FRAME ASSIGNMENTS - SUMMARY", Table("FRAME ASSIGNMENTS - SUMMARY", frames.Select(f => Row(("UniqueName", f.Name), ("Design Type", frameOf.TryGetValue(f.Name, out var o) ? o.Type : "Beam"),
                ("Length", N(f.Length)), ("Analysis Section", sections.GetValueOrDefault(f.Name) ?? "None"), ("Design Section", "N/A"), ("Min Number Stations", LineValue(f.Name, "MINNUMSTA") ?? "3")))));
            Add("FRAME ASSIGNMENTS - OUTPUT STATIONS", Table("FRAME ASSIGNMENTS - OUTPUT STATIONS", frames.Select(f => LineValue(f.Name, "MAXSTASPC") is { } spacing
                ? Row(("UniqueName", f.Name), ("Station Option", "Max Spacing"), ("Max Spacing", spacing))
                : Row(("UniqueName", f.Name), ("Station Option", "Min Stations"), ("Min Stations", LineValue(f.Name, "MINNUMSTA") ?? "3")))));
            Add("FRAME ASSIGNMENTS - FRAME AUTO MESH OPTIONS", Table("FRAME ASSIGNMENTS - FRAME AUTO MESH OPTIONS", frames.Select(f => Row(("UniqueName", f.Name),
                ("Auto Mesh", YesNo(LineValue(f.Name, "AUTOMESH"), "Yes")), ("At Intermediate Joints", YesNo(LineValue(f.Name, "MESHATINTERMEDIATEJOINTS"), "Yes")),
                ("At Intersections", YesNo(LineValue(f.Name, "MESHATINTERSECTIONS"), "Yes")), ("Min Number?", "No"), ("Max Length?", "No")))));
            Add("FRAME ASSIGNMENTS - FRAME FLOOR MESHING OPTION", Table("FRAME ASSIGNMENTS - FRAME FLOOR MESHING OPTION", frames.Select(f => Row(("UniqueName", f.Name), ("Offset Option", "Include in Mesh")))));
        }

        // ---- SAFE's summaries of the points and areas ----
        var restraints = T("JOINT ASSIGNMENTS - RESTRAINTS").GroupBy(r => S(r, "UniqueName")).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var points = T("POINT OBJECT CONNECTIVITY");
        if (points.Count > 0)
            Add("JOINT ASSIGNMENTS - SUMMARY", Table("JOINT ASSIGNMENTS - SUMMARY", points.Select(p =>
            {
                var name = S(p, "UniqueName");
                var row = Row(("UniqueName", name), ("Diaphragm", "From Area"));
                if (restraints.TryGetValue(name, out var r) && new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Where(d => S(r, d) == "Yes").ToList() is { Count: > 0 } dofs) row.Add(("Restraints", string.Join("; ", dofs)));
                return row;
            })));
        if (T("AREA ASSIGNMENTS - SECTION PROPERTIES").Count > 0)
            Add("AREA ASSIGNMENTS - SUMMARY", Table("AREA ASSIGNMENTS - SUMMARY", T("AREA ASSIGNMENTS - SECTION PROPERTIES").Select(r => Row(("UniqueName", S(r, "UniqueName")), ("Section Property", S(r, "Section Property")), ("Property Type", S(r, "Property Type"))))));

        // ---- material lists: the weight of each object type and section, by material ----
        MaterialLists(m, have, Add);

        // ---- design overwrites of the floors (finite element based) and punching checks at the columns ----
        var fem = Lines(m, "CONCRETE SLAB DESIGN OVERWRITES - FEM BASED").Where(t => t[0] == "SLABFEMDESIGNOVERWRITES" && t.Count >= 3)
            .Select(t => (Label: Unquote(t[1]), Story: Unquote(t[2]), P: Pairs(t, 3))).ToList();
        var femRows = floors.Select(f => (f, o: fem.Find(x => areaOf.TryGetValue(f, out var a) && a.Label == x.Label && string.Equals(a.Story, x.Story, StringComparison.OrdinalIgnoreCase)))).Where(x => x.o.Label is not null).ToList();
        if (femRows.Count > 0)
            Add("CONCRETE SLAB DESIGN OVERWRITES - FINITE ELEMENT BASED", Table("CONCRETE SLAB DESIGN OVERWRITES - FINITE ELEMENT BASED", femRows.Select(x => Row(("Unique Name", x.f),
                ("Rebar Material", x.o.P.GetValueOrDefault("REBARMAT") ?? ""), ("Cover Specification Type", (x.o.P.GetValueOrDefault("COVERTYPE") ?? "Preferences").StartsWith("Pref", StringComparison.OrdinalIgnoreCase) ? "From Preferences" : x.o.P["COVERTYPE"]),
                ("LLRF", x.o.P.GetValueOrDefault("RLLF") ?? "1"), ("Design?", YesNo(x.o.P.GetValueOrDefault("DESIGN"), "Yes"))))));

        // ---- analysis options: meshing from the model's own, the rest as SAFE has them ----
        var mesh = AllPairs(m, "ANALYSIS OPTIONS", "AUTOMESHOPTIONS");
        Add("ANALYSIS MODELING OPTIONS", Table("ANALYSIS MODELING OPTIONS", Default(m, "ANALYSIS MODELING OPTIONS")));
        Add("ANALYSIS OPTIONS - SAPFIRE OPTIONS", Table("ANALYSIS OPTIONS - SAPFIRE OPTIONS", Default(m, "ANALYSIS OPTIONS - SAPFIRE OPTIONS")));
        Add("ANALYSIS OPTIONS - DESIGN AND RESPONSE RECOVERY OPTIONS", Table("ANALYSIS OPTIONS - DESIGN AND RESPONSE RECOVERY OPTIONS", Default(m, "ANALYSIS OPTIONS - DESIGN AND RESPONSE RECOVERY OPTIONS")));
        var floorMesh = Default(m, "ANALYSIS OPTIONS - AUTOMATIC MESH SETTINGS FOR FLOORS");
        if (mesh.GetValueOrDefault("MESHTYPE") is { } mt) floorMesh[0]["Mesh Option"] = mt.Equals("RECTANGULAR", StringComparison.OrdinalIgnoreCase) ? "Rectangular" : mt.Equals("GENERAL", StringComparison.OrdinalIgnoreCase) ? "General" : mt;
        if (mesh.GetValueOrDefault("LOCALIZEDFLOORMESHING") is { } lm) floorMesh[0]["Use Localized Meshing"] = YesNo(lm, "Yes");
        if (mesh.GetValueOrDefault("FLOORMESHMERGEJOINTS") is { } mj) floorMesh[0]["Merge Joints"] = YesNo(mj, "Yes");
        if (mesh.GetValueOrDefault("FLOORMESHMAXSIZE") is { } fs) floorMesh[0]["Maximum Mesh Size"] = fs;
        Add("ANALYSIS OPTIONS - AUTOMATIC MESH SETTINGS FOR FLOORS", Table("ANALYSIS OPTIONS - AUTOMATIC MESH SETTINGS FOR FLOORS", floorMesh));
        var wallMesh = Default(m, "ANALYSIS OPTIONS - AUTOMATIC RECTANGULAR MESH OPTIONS FOR WALLS");
        if (mesh.GetValueOrDefault("WALLMESHMAXSIZE") is { } ws) wallMesh[0]["Maximum Mesh Size"] = ws;
        Add("ANALYSIS OPTIONS - AUTOMATIC RECTANGULAR MESH OPTIONS FOR WALLS", Table("ANALYSIS OPTIONS - AUTOMATIC RECTANGULAR MESH OPTIONS FOR WALLS", wallMesh));
        Add("ANALYSIS OPTIONS - CRACKING ANALYSIS OPTIONS", Table("ANALYSIS OPTIONS - CRACKING ANALYSIS OPTIONS", Default(m, "ANALYSIS OPTIONS - CRACKING ANALYSIS OPTIONS")));
        foreach (var t in new[] { "PREFERENCES - TOLERANCE", "PREFERENCES - GRAPHICS", "MISCELLANEOUS OPTIONS", "OPTIONS - COLORS - DISPLAY", "OPTIONS - COLORS - OUTPUT" })
            Add(t, Table(t, Default(m, t)));

        // ---- project information ----
        var info = AllPairs(m, "PROJECT INFORMATION", "PROJECTINFO");
        if (info.Count > 0)
        {
            (string Key, string Field)[] fields = [("COMPANYNAME", "Company Name"), ("CLIENTNAME", "Client Name"), ("PROJECTNAME", "Project Name"), ("PROJECTNUMBER", "Project Number"),
                ("MODELNAME", "Model Name"), ("MODELDESCRIPTION", "Model Description"), ("REVISIONNUMBER", "Revision Number"), ("FRAMETYPE", "Frame Type"), ("ENGINEER", "Engineer"),
                ("CHECKER", "Checker"), ("SUPERVISOR", "Supervisor"), ("ISSUECODE", "Issue Code"), ("DESIGNCODE", "Design Code")];
            var row = fields.Where(f => info.ContainsKey(f.Key)).Select(f => (f.Field, info[f.Key])).ToList();
            if (!info.ContainsKey("REVISIONNUMBER")) row.Insert(row.FindIndex(x => x.Field == "Model Name") + 1, ("Revision Number", "0"));
            Add("PROJECT INFORMATION", Table("PROJECT INFORMATION", [row]));
        }

        // ---- design preferences: the model's own values in SAFE's tables of its codes ----
        DesignPreferences(m, Add);

        return Ordered(tables);
    }

    private static string Bubble(GridLine g)
    {
        for (var i = 0; i + 1 < g.Extra.Count; i++) if (g.Extra[i].Equals("BUBBLELOC", StringComparison.OrdinalIgnoreCase)) return Unquote(g.Extra[i + 1]);
        return g.Direction == "Y" ? "Start" : "End";
    }

    /// <summary>A grid line's bubble place as SAFE writes it ("End" for lettered, "Start" for numbered grids by default).</summary>
    internal static string BubbleOf(GridLine g) => Bubble(g);

    /// <summary>The grid bubble size (GRIDSYSTEM BUBBLESIZE).</summary>
    internal static double BubbleSize(StructuralModel m)
    {
        for (var i = 0; i + 1 < m.GridSystemExtra.Count; i++)
            if (m.GridSystemExtra[i].Equals("BUBBLESIZE", StringComparison.OrdinalIgnoreCase) && TryNumber(m.GridSystemExtra[i + 1], out var v)) return v;
        return 60 * Inch(m);
    }

    private static double? MeshSize(StructuralModel m) =>
        AllPairs(m, "ANALYSIS OPTIONS", "AUTOMESHOPTIONS").GetValueOrDefault("FLOORMESHMAXSIZE") is { } s && TryNumber(s, out var v) ? v : null;

    /// <summary>The weight of the floors, walls and frames of the file, by object type and material, and by section.</summary>
    private static void MaterialLists(StructuralModel m, Dictionary<string, List<Dictionary<string, string>>> have, Action<string, string> add)
    {
        string S(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";
        double D(Dictionary<string, string> r, string k) => double.TryParse(S(r, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        List<Dictionary<string, string>> T(string name) => have.TryGetValue(name, out var r) ? r : [];
        var weight = T("MATERIAL PROPERTIES - BASIC MECHANICAL PROPERTIES").GroupBy(r => S(r, "Material")).ToDictionary(g => g.Key, g => D(g.First(), "UnitWeight"), StringComparer.OrdinalIgnoreCase);
        var points = T("POINT OBJECT CONNECTIVITY").GroupBy(r => S(r, "UniqueName")).ToDictionary(g => g.Key, g => (X: D(g.First(), "X"), Y: D(g.First(), "Y"), Z: D(g.First(), "Z")), StringComparer.OrdinalIgnoreCase);
        var areaSection = T("AREA ASSIGNMENTS - SECTION PROPERTIES").GroupBy(r => S(r, "UniqueName")).ToDictionary(g => g.Key, g => S(g.First(), "Section Property"), StringComparer.OrdinalIgnoreCase);
        var shells = T("SLAB PROPERTY DEFINITIONS").Select(r => (Name: S(r, "Name"), Material: S(r, "Material"), T: D(r, "Slab Thickness"), Type: "Floor"))
            .Concat(T("WALL PROPERTY DEFINITIONS - SPECIFIED").Select(r => (Name: S(r, "Name"), Material: S(r, "Material"), T: D(r, "Wall Thickness"), Type: "Wall")))
            .GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var frameSection = T("FRAME ASSIGNMENTS - SECTION PROPERTIES").GroupBy(r => S(r, "UniqueName")).ToDictionary(g => g.Key, g => S(g.First(), "Section Property"), StringComparer.OrdinalIgnoreCase);
        var rects = T("FRAME SECTION PROPERTY DEFINITIONS - CONCRETE RECTANGULAR").GroupBy(r => S(r, "Name")).ToDictionary(g => g.Key, g => (Material: S(g.First(), "Material"), A: D(g.First(), "Depth") * D(g.First(), "Width")), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in T("FRAME SECTION PROPERTY DEFINITIONS - CONCRETE CIRCLE").GroupBy(r => S(r, "Name")).ToDictionary(g => g.Key, g => (Material: S(g.First(), "Material"), A: Math.PI * Math.Pow(D(g.First(), "Diameter"), 2) / 4))) rects[k] = v;
        var byType = new List<(string Type, string Material, double W)>();
        var bySection = new List<(string Section, string Type, double W)>();
        void Put(string type, string material, string section, double w)
        {
            var i = byType.FindIndex(x => x.Type == type && x.Material == material);
            if (i < 0) byType.Add((type, material, w)); else byType[i] = (type, material, byType[i].W + w);
            var j = bySection.FindIndex(x => x.Section == section && x.Type == type);
            if (j < 0) bySection.Add((section, type, w)); else bySection[j] = (section, type, bySection[j].W + w);
        }
        foreach (var table in new[] { "FLOOR OBJECT CONNECTIVITY", "WALL OBJECT CONNECTIVITY" })
            foreach (var r in T(table))
            {
                var name = S(r, "Unique Name").Length > 0 ? S(r, "Unique Name") : S(r, "UniqueName");
                if (!areaSection.TryGetValue(name, out var sec) || !shells.TryGetValue(sec, out var sh)) continue;
                var corners = Enumerable.Range(1, 64).Select(i => S(r, "UniquePt" + i.ToString(CultureInfo.InvariantCulture))).TakeWhile(c => c.Length > 0).Where(points.ContainsKey).Select(c => points[c]).ToList();
                if (corners.Count < 3) continue;
                double ax = 0, ay = 0, az = 0;
                for (var i = 0; i < corners.Count; i++)
                {
                    var (p, q) = (corners[i], corners[(i + 1) % corners.Count]);
                    ax += p.Y * q.Z - p.Z * q.Y; ay += p.Z * q.X - p.X * q.Z; az += p.X * q.Y - p.Y * q.X;
                }
                var area = Math.Sqrt(ax * ax + ay * ay + az * az) / 2;
                Put(sh.Type, sh.Material, sec, area * sh.T * weight.GetValueOrDefault(sh.Material));
            }
        foreach (var (table, type) in new[] { ("COLUMN OBJECT CONNECTIVITY", "Column"), ("BEAM OBJECT CONNECTIVITY", "Beam"), ("BRACE OBJECT CONNECTIVITY", "Brace") })
            foreach (var r in T(table))
            {
                var name = S(r, "Unique Name").Length > 0 ? S(r, "Unique Name") : S(r, "UniqueName");
                if (!frameSection.TryGetValue(name, out var sec) || !rects.TryGetValue(sec, out var fr)) continue;
                Put(type, fr.Material, sec, D(r, "Length") * fr.A * weight.GetValueOrDefault(fr.Material));
            }
        if (byType.Count == 0) return;
        add("MATERIAL LIST BY OBJECT TYPE", Table("MATERIAL LIST BY OBJECT TYPE", byType.Select(x => Row(("Object Type", x.Type), ("Material", x.Material), ("Weight", N(x.W))))));
        add("MATERIAL LIST BY SECTION PROPERTY", Table("MATERIAL LIST BY SECTION PROPERTY", bySection.Select(x => Row(("Section", x.Section), ("Object Type", x.Type), ("Weight", N(x.W))))));
    }

    /// <summary>SAFE's design preference tables, with what the model's .e2k preferences give.</summary>
    private static void DesignPreferences(StructuralModel m, Action<string, string> add)
    {
        var slab = AllPairs(m, "CONCRETE SLAB DESIGN PREFERENCES", "CONCRETESLABPREFERENCE");
        var frame = AllPairs(m, "CONCRETE DESIGN PREFERENCES", "CONCRETEPREFERENCE");
        var comp = AllPairs(m, "COMPOSITE DESIGN PREFERENCES", "COMPOSITEPREFERENCE");
        if (slab.Count + frame.Count + comp.Count == 0) return;
        // SAFE designs with its own frame code of the slab code's family (ACI 318-19 beside ACI 318-19 slabs).
        if (comp.GetValueOrDefault("CODE") is "AISC 360-10")
        {
            var row = Default(m, "COMPOSITE BEAM DESIGN PREFERENCES - AISC 360-10")[0];
            (string Key, string Field)[] map = [("%MIDDLERANGE", "Middle Range"), ("PATTERNLLF", "Pattern Live Load Factor"), ("SRLIMIT", "D/C Ratio Limit"), ("MIN%COMPOSITE", "Minimum PCC"),
                ("MAX%COMPOSITE", "Maximum PCC"), ("MINLONGSPACING", "Min. Long. Spacing"), ("MAXLONGSPACING", "Max. Long. Spacing"), ("MINTRANSSPACING", "Min. Trans. Spacing"),
                ("MAXSTUDSPERROW", "Max. Studs Per Row"), ("%DLCAMBER", "Camber DL"), ("MINDEPTHFORCAMBER", "Min. Beam Depth"), ("MINWEBTHICKFORCAMBER", "Min. Web Thick."),
                ("MINSPANFORCAMBER", "Min. Beam Span"), ("CAMBERIGNORE", "Min. Camber, abs"), ("CAMBERELMIN", "Minimum Camber, L/"), ("CAMBERABSMAX", "Camber Abs. Max Limit"),
                ("CAMBERRELMAX", "Camber Max Ratio"), ("CAMBERINTERVAL", "Camber Interval"), ("DLLIMIT", "Pre-Comp DL Ratio"), ("SLLIMIT", "SDL+LL Ratio"), ("LLLIMIT", "LL Ratio"),
                ("TLLIMIT", "Net Ratio"), ("DAMPINGRATIO", "Damping Ratio - Walking"), ("WALKINGACCELERATIONLIMIT", "Walking Acceleration Limit, a0/g"), ("STEELPRICE", "Steel Price ($)"),
                ("CONNECTORPRICE", "Stud Price ($)"), ("CAMBERPRICE", "Camber Price ($)")];
            foreach (var (k, f) in map) if (comp.TryGetValue(k, out var v) && row.ContainsKey(f)) row[f] = v;
            foreach (var (k, f) in new[] { ("SHORED", "Shored?"), ("SINGLESEGMENT", "Single Segment?"), ("CALCULATECAMBER", "Camber?"), ("CAMBERROUNDDOWN", "Round Camber Down?"), ("OPTIMIZEPRICE", "Optimize Price?") })
                if (comp.TryGetValue(k, out var v) && row.ContainsKey(f)) row[f] = YesNo(v, row[f]);
            add("COMPOSITE BEAM DESIGN PREFERENCES - AISC 360-10", Table("COMPOSITE BEAM DESIGN PREFERENCES - AISC 360-10", [row]));
        }
        var slabCode = slab.GetValueOrDefault("CODE");
        if (slabCode is "ACI 318-19" || (slabCode is null && frame.Count > 0))
        {
            var row = Default(m, "CONCRETE FRAME DESIGN PREFERENCES - ACI 318-19")[0];
            (string Key, string Field)[] map = [("NUMINTERCURVES", "# Interaction Curves"), ("NUMINTERPOINTS", "# Interaction Points"), ("OMEGA0", "Design System Omega0"), ("RHO", "Design System Rho"),
                ("SDS", "Design System Sds"), ("PHITENSIONCTRL", "Phi (Tension)"), ("PHICOMPRESSIONCTRLTIED", "Phi (Compression Tied)"), ("PHICOMPRESSIONCTRLSPIRAL", "Phi (Compression Spiral)"),
                ("PHISHEARTORSION", "Phi (Shear and Torsion)"), ("PHISHEARSEISMIC", "Phi (Shear Seismic)"), ("PHISHEARJOINT", "Phi (Shear Joint)"), ("PATTERNLLF", "Pattern Live Load Factor"), ("UFLIMIT", "Utlization Factor Limit")];
            foreach (var (k, f) in map) if (frame.TryGetValue(k, out var v) && row.ContainsKey(f)) row[f] = v;
            foreach (var (k, f) in new[] { ("CONSIDERMINECCENTRICITY", "Minimum Eccentricity?"), ("DESIGNFORBCCR", "Design for BCCR?"), ("IGNOREBENEFICIALPUINBEAMDESIGN", "Ignore Beneficial Pu for Beam Design?") })
                if (frame.TryGetValue(k, out var v)) row[f] = YesNo(v, row[f]);
            if (frame.GetValueOrDefault("SDC") is { Length: > 0 } sdc) row["Seismic Design Category"] = sdc;
            if (frame.GetValueOrDefault("THDESIGN") is { } th) row["Multi-Response Design"] = th.ToUpperInvariant() switch { "ENVELOPES" => "Envelopes", "EVERYSTEP-ALL" => "Step-by-Step - All", "EVERYSTEP" => "Step-by-Step", "LASTSTEP" => "Last Step", _ => row["Multi-Response Design"] };
            add("CONCRETE FRAME DESIGN PREFERENCES - ACI 318-19", Table("CONCRETE FRAME DESIGN PREFERENCES - ACI 318-19", [row]));
        }
        if (slabCode is "ACI 318-19")
        {
            var row = Default(m, "CONCRETE SLAB DESIGN PREFERENCES - ACI 318-19")[0];
            (string Key, string Field)[] map = [("PHITENACI", "PhiTen"), ("PHICOMPACI", "PhiComp"), ("PHISHEARACI", "PhiShear"), ("COVERTOP", "CoverTop"), ("COVERBOT", "CoverBot"), ("BARSIZE", "BarSize"),
                ("PTCGSTOP", "PTCGSTop"), ("PTCGSBOTEXT", "PTCGSBotExt"), ("PTCGSBOTINT", "PTCGSBotInt"), ("SLABTYPE", "SlabType"), ("INITCONCRAT", "InitConcRat"), ("LLFRACTION", "LLFraction")];
            foreach (var (k, f) in map) if (slab.TryGetValue(k, out var v)) row[f] = v;
            if (slab.GetValueOrDefault("INNERLAYER") is { Length: > 0 } layer) row["InnerLayer"] = layer.StartsWith("Layer", StringComparison.OrdinalIgnoreCase) ? layer : "Layer " + layer;
            if (slab.GetValueOrDefault("USERSTRESS") is { } us) row["UserStress"] = YesNo(us, "No");
            add("CONCRETE SLAB DESIGN PREFERENCES - ACI 318-19", Table("CONCRETE SLAB DESIGN PREFERENCES - ACI 318-19", [row]));
        }
    }

    /// <summary>SAFE's order of tables (its own exports, SAFE 20.1 and 23.1); others keep their place after the one before them.</summary>
    private static readonly string[] Order =
    [
        "PROGRAM CONTROL", "GRID DEFINITIONS - GENERAL", "GRID DEFINITIONS - GRID LINES", "MATERIAL PROPERTIES - GENERAL", "MATERIAL PROPERTIES - BASIC MECHANICAL PROPERTIES",
        "MATERIAL PROPERTIES - STEEL DATA", "MATERIAL PROPERTIES - CONCRETE DATA", "MATERIAL PROPERTIES - REBAR DATA", "MATERIAL PROPERTIES - TENDON DATA",
        "FRAME SECTION PROPERTY DEFINITIONS - SUMMARY", "FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE", "FRAME SECTION PROPERTY DEFINITIONS - CONCRETE RECTANGULAR",
        "FRAME SECTION PROPERTY DEFINITIONS - CONCRETE CIRCLE", "FRAME SECTION PROPERTY DEFINITIONS - AUTO SELECT LIST", "TENDON SECTION PROPERTIES",
        "AREA SECTION PROPERTY DEFINITIONS - SUMMARY", "SLAB PROPERTY DEFINITIONS", "DECK PROPERTY DEFINITIONS", "WALL PROPERTY DEFINITIONS - SPECIFIED",
        "LINK PROPERTY DEFINITIONS - SUMMARY", "LINK PROPERTY DEFINITIONS - LINEAR", "SPRING PROPERTY DEFINITIONS - AREA SPRINGS", "REINFORCING BAR SIZES", "LOAD PATTERN DEFINITIONS",
        "GROUP DEFINITIONS", "GROUP ASSIGNMENTS", "MASS SOURCE DEFINITION", "DATABASE TABLE NAMED SET DEFINITIONS", "LOAD CASE DEFINITIONS - SUMMARY", "LOAD CASE DEFINITIONS - LINEAR STATIC",
        "MODAL CASE DEFINITIONS - EIGEN", "LOAD CASE DEFINITIONS - HYPERSTATIC", "LOAD COMBINATION DEFINITIONS", "POINT OBJECT CONNECTIVITY", "COLUMN OBJECT CONNECTIVITY",
        "BEAM OBJECT CONNECTIVITY", "BRACE OBJECT CONNECTIVITY", "NULL LINE OBJECT CONNECTIVITY", "FLOOR OBJECT CONNECTIVITY", "WALL OBJECT CONNECTIVITY", "NULL AREA OBJECT CONNECTIVITY",
        "TENDON OBJECT CONNECTIVITY", "TENDON DISCRETIZED POINTS", "STRIP OBJECT CONNECTIVITY", "SLAB REBAR OBJECT GEOMETRY", "DIMENSION LINE OBJECT GEOMETRY", "JOINT ASSIGNMENTS - SUMMARY",
        "JOINT ASSIGNMENTS - RESTRAINTS", "FRAME ASSIGNMENTS - SUMMARY", "FRAME ASSIGNMENTS - SECTION PROPERTIES", "FRAME ASSIGNMENTS - END LENGTH OFFSETS", "FRAME ASSIGNMENTS - INSERTION POINT",
        "FRAME ASSIGNMENTS - OUTPUT STATIONS", "FRAME ASSIGNMENTS - FRAME AUTO MESH OPTIONS", "FRAME ASSIGNMENTS - FRAME FLOOR MESHING OPTION", "TENDON ASSIGNMENTS - PROPERTIES",
        "TENDON - TOTAL ELONGATION", "AREA ASSIGNMENTS - SUMMARY", "AREA ASSIGNMENTS - SECTION PROPERTIES", "AREA ASSIGNMENTS - INSERTION POINT", "AREA ASSIGNMENTS - FLOOR AUTO MESH OPTIONS",
        "AREA ASSIGNMENTS - AUTO EDGE CONSTRAINTS", "AREA ASSIGNMENTS - AREA SPRINGS", "AREA ASSIGNMENTS - SLAB LINE RELEASES", "SLAB REBAR PROPERTY ASSIGNMENTS", "JOINT LOADS ASSIGNMENTS - FORCE",
        "FRAME LOADS ASSIGNMENTS - POINT", "FRAME LOADS ASSIGNMENTS - DISTRIBUTED", "AREA LOAD ASSIGNMENTS - UNIFORM", "TENDON LOAD ASSIGNMENTS - JACKING STRESS", "TENDON LOAD ASSIGNMENTS - LOSS OPTIONS",
        "ANALYSIS MODELING OPTIONS", "ANALYSIS OPTIONS - SAPFIRE OPTIONS", "ANALYSIS OPTIONS - DESIGN AND RESPONSE RECOVERY OPTIONS", "ANALYSIS OPTIONS - AUTOMATIC MESH SETTINGS FOR FLOORS",
        "ANALYSIS OPTIONS - AUTOMATIC RECTANGULAR MESH OPTIONS FOR WALLS", "ANALYSIS OPTIONS - CRACKING ANALYSIS OPTIONS", "PREFERENCES - TOLERANCE", "PREFERENCES - GRAPHICS", "MISCELLANEOUS OPTIONS",
        "OPTIONS - COLORS - DISPLAY", "OPTIONS - COLORS - OUTPUT", "PROJECT INFORMATION", "MATERIAL LIST BY OBJECT TYPE", "MATERIAL LIST BY SECTION PROPERTY",
        "COMPOSITE BEAM DESIGN PREFERENCES - AISC 360-10", "CONCRETE DESIGN PREFERENCES - ACI 318-19", "CONCRETE FRAME DESIGN PREFERENCES - ACI 318-19", "CONCRETE SLAB DESIGN PREFERENCES - ACI 318-19",
        "CONCRETE SLAB DESIGN OVERWRITES - FINITE ELEMENT BASED", "CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED", "CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - GENERAL",
        "CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - PERIMETER", "CONCRETE SLAB DESIGN LOAD COMBINATION DATA",
    ];

    private static List<string> Ordered(List<string> tables)
    {
        var place = Order.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => (double)x.i, StringComparer.OrdinalIgnoreCase);
        var keyed = new List<(double At, int Seq, string Text)>();
        var last = -1.0;
        for (var i = 0; i < tables.Count; i++)
        {
            var name = CsiTableText.Names(tables[i]).FirstOrDefault() ?? "";
            if (place.TryGetValue(name, out var at)) last = at; else at = last + 0.5;
            keyed.Add((at, i, tables[i]));
        }
        return keyed.OrderBy(x => x.At).ThenBy(x => x.Seq).Select(x => x.Text).ToList();
    }

    private const string DefaultText = """
TABLE:  "ANALYSIS MODELING OPTIONS"
   "Two Dimensional Only"=No   "Rigid Diaphragm At Top"=No   "Ignore Vertical Offsets"=No

TABLE:  "ANALYSIS OPTIONS - SAPFIRE OPTIONS"
   "Solver Option"=Advanced   "Analysis Process"=Auto   "Number Analysis Threads"=0   "Max File Size"=0

TABLE:  "ANALYSIS OPTIONS - DESIGN AND RESPONSE RECOVERY OPTIONS"
   "Number Design Threads"=0   "Number Recovery Threads"=0   "Use Memory Mapped Files"="Program Determined"   "Allow Model Differences"=No

TABLE:  "ANALYSIS OPTIONS - AUTOMATIC MESH SETTINGS FOR FLOORS"
   "Mesh Option"=Rectangular   "Use Localized Meshing"=Yes   "Merge Joints"=Yes   "Maximum Mesh Size"=24

TABLE:  "ANALYSIS OPTIONS - AUTOMATIC RECTANGULAR MESH OPTIONS FOR WALLS"
   "Maximum Mesh Size"=48

TABLE:  "ANALYSIS OPTIONS - CRACKING ANALYSIS OPTIONS"
   "Reinforcement Source"="User and Designed"   "Minimum Tension Ratio"=0.0018   "Minimum Compression Ratio"=0

TABLE:  "PREFERENCES - TOLERANCE"
   MergeTol=0.1   MaxInclVertCol=1   MaxInclHorzBmFlr=20

TABLE:  "PREFERENCES - GRAPHICS"
   MaxFontSize=6   MinFontSize=3   MinSLineThick=1   MinPLineThick=4   AntiAliasing?=Yes   ConstantLineThick?=Yes   ShadingWithinElement?=No   AutoZoomStep=10   ShrinkFactor=70

TABLE:  "MISCELLANEOUS OPTIONS"
   "Start Page"=Yes   "Value at Cursor"=Yes   "Frame Info"=Yes   "Bounding Plane"=Yes   "Architectural Dimensioning"=No   "Moments on Tension Side"=Yes   "Animation Sound"=Yes   "Auto Save"=No   Crosshairs=No   "Model Explorer"=Yes

TABLE:  "OPTIONS - COLORS - DISPLAY"
   "Device Type"=Screen   ColorColumns=10507331   ColorBeams=10507331   ColorBraces=10507331   ColorLinks=10507331   ColorSprings=Black   ColorTendons=DarkCyan   ColorSlabPanels=Green   ColorWalls=Red   ColorFloors=Gray4 _
        ColorNullObjects=Gray8Dark   ColorOpenings=Black   ColorColStripA=Red   ColorColStripB=Blue   ColorMidStripA=Yellow   ColorMidStripB=Cyan   ColorStripEdge=16744703   ColorOtherStripFill=Green   ColorSupportLines=16744703 _
        ColorDimLines=10461087   ColorText=Black   ColorGridLines=Gray6   ColorBackground=White   ColorUserMeshEdge=5065984   ColorStlDesign=DarkCyan   ColorCompDesign=Cyan   ColorConcDesign=4227327   ColorNoDesign=4227327 _
        TransColumns=0.5   TransBeams=0.5   TransBraces=0.5   TransFloors=0.5   TransWalls=0.4   TransLinks=0.5   Darkness=0.3
   "Device Type"=Printer   ColorColumns=Black   ColorBeams=Black   ColorBraces=Black   ColorLinks=Gray8Dark   ColorSprings=Gray8Dark   ColorTendons=DarkCyan   ColorSlabPanels=Green   ColorWalls=Gray4   ColorFloors=Gray4 _
        ColorNullObjects=Gray4   ColorOpenings=Gray8Dark   ColorColStripA=Red   ColorColStripB=Blue   ColorMidStripA=Yellow   ColorMidStripB=Cyan   ColorStripEdge=16744703   ColorOtherStripFill=Green   ColorSupportLines=16744703 _
        ColorDimLines=10461087   ColorText=Black   ColorGridLines=Gray4   ColorBackground=White   ColorUserMeshEdge=5065984   ColorStlDesign=Black   ColorCompDesign=Gray4   ColorConcDesign=Gray8Dark   ColorNoDesign=Gray4   TransColumns=0.5 _
        TransBeams=0.5   TransBraces=0.5   TransFloors=0.5   TransWalls=0.4   TransLinks=0.5   Darkness=0.5
   "Device Type"="Color Printer"   ColorColumns=10507331   ColorBeams=Blue   ColorBraces=Blue   ColorLinks=10507331   ColorSprings=Black   ColorTendons=DarkCyan   ColorSlabPanels=Green   ColorWalls=Blue   ColorFloors=Gray4 _
        ColorNullObjects=Gray8Dark   ColorOpenings=Black   ColorColStripA=Red   ColorColStripB=Blue   ColorMidStripA=Yellow   ColorMidStripB=Cyan   ColorStripEdge=16744703   ColorOtherStripFill=Green   ColorSupportLines=16744703 _
        ColorDimLines=10461087   ColorText=Black   ColorGridLines=Gray6   ColorBackground=White   ColorUserMeshEdge=5065984   ColorStlDesign=DarkCyan   ColorCompDesign=Cyan   ColorConcDesign=4227327   ColorNoDesign=4227327 _
        TransColumns=0.5   TransBeams=0.5   TransBraces=0.5   TransFloors=0.5   TransWalls=0.4   TransLinks=0.5   Darkness=0.3

TABLE:  "OPTIONS - COLORS - OUTPUT"
   "Device Type"="Screen Classic"   Contour1=13107400   Contour2=6553828   Contour3=Red   Contour4=16639   Contour5=Orange   Contour6=43775   Contour7=54527   Contour8=Yellow   Contour9=65408   Contour10=Green   Contour11=8453888 _
        Contour12=Cyan   Contour13=16755200   Contour14=16733440   Contour15=Blue   TransContours=0   Ratio1=Cyan   Ratio2=Green   Ratio3=Yellow   Ratio4=Magenta   Ratio5=Red   RatioNotD=Gray4   RatioVal1=0.5   RatioVal2=0.7   RatioVal3=0.9 _
        RatioVal4=1   HState1=Black   HState2=Green   HState3=Cyan   HState4=Magenta   HState5=Red   HStatus1=Black   HStatus2=Green   HStatus3=Cyan   HStatus4=Red   DFillPos=Yellow   DFillNeg=Red   DFillRange=Blue   TransDiag=0.2
   "Device Type"="Screen GrayScale"   Contour1=Black   Contour2=3158064   Contour3=4210752   Contour4=5263440   Contour5=6316128   Contour6=7368816   Contour7=Gray8Dark   Contour8=Gray7   Contour9=Gray6   Contour10=Gray5   Contour11=Gray4 _
        Contour12=Gray3   Contour13=Gray2   Contour14=Gray1Light   Contour15=White   TransContours=0   Ratio1=Gray2   Ratio2=Gray4   Ratio3=Gray8Dark   Ratio4=4210752   Ratio5=Black   RatioNotD=Gray4   RatioVal1=0.5   RatioVal2=0.7 _
        RatioVal3=0.9   RatioVal4=1   HState1=Black   HState2=Gray4   HState3=Gray8Dark   HState4=4210752   HState5=Black   HStatus1=Black   HStatus2=Gray4   HStatus3=Gray8Dark   HStatus4=4210752   DFillPos=Yellow   DFillNeg=Red _
        DFillRange=Blue   TransDiag=0.2
   "Device Type"=Printer   Contour1=Black   Contour2=3158064   Contour3=4210752   Contour4=5263440   Contour5=6316128   Contour6=7368816   Contour7=Gray8Dark   Contour8=Gray7   Contour9=Gray6   Contour10=Gray5   Contour11=Gray4 _
        Contour12=Gray3   Contour13=Gray2   Contour14=Gray1Light   Contour15=White   TransContours=0   Ratio1=Gray2   Ratio2=Gray4   Ratio3=Gray8Dark   Ratio4=4210752   Ratio5=Black   RatioNotD=Gray4   RatioVal1=0.5   RatioVal2=0.7 _
        RatioVal3=0.9   RatioVal4=1   HState1=Black   HState2=Gray4   HState3=Gray8Dark   HState4=4210752   HState5=Black   HStatus1=Black   HStatus2=Gray4   HStatus3=Gray8Dark   HStatus4=4210752   DFillPos=Gray8Dark   DFillNeg=Gray8Dark _
        DFillRange=4210752   TransDiag=0.2
   "Device Type"="Color Printer"   Contour1=13107400   Contour2=6553828   Contour3=Red   Contour4=16639   Contour5=Orange   Contour6=43775   Contour7=54527   Contour8=Yellow   Contour9=65408   Contour10=Green   Contour11=8453888 _
        Contour12=Cyan   Contour13=16755200   Contour14=16733440   Contour15=Blue   TransContours=0   Ratio1=Cyan   Ratio2=Green   Ratio3=Yellow   Ratio4=Magenta   Ratio5=Red   RatioNotD=Gray4   RatioVal1=0.5   RatioVal2=0.7   RatioVal3=0.9 _
        RatioVal4=1   HState1=Black   HState2=Green   HState3=Cyan   HState4=Magenta   HState5=Red   HStatus1=Black   HStatus2=Green   HStatus3=Cyan   HStatus4=Red   DFillPos=Yellow   DFillNeg=Red   DFillRange=Blue   TransDiag=0.2

TABLE:  "COMPOSITE BEAM DESIGN PREFERENCES - AISC 360-10"
   Shored?=No   "Middle Range"=70   "Pattern Live Load Factor"=0.75   "D/C Ratio Limit"=1   "Minimum PCC"=25   "Maximum PCC"=100   "Single Segment?"=No   "Min. Long. Spacing"=4.5   "Max. Long. Spacing"=36   "Min. Trans. Spacing"=3 _
        "Max. Studs Per Row"=3   "Position of Studs"="Middle of the Ribs"   Camber?=Yes   "Camber DL"=80   "Min. Beam Depth"=13.5   "Min. Web Thick."=0.25   "Min. Beam Span"=288   "Min. Camber, abs"=0.75   "Minimum Camber, L/"=900 _
        "Camber Abs. Max Limit"=6   "Camber Max Ratio"=180   "Camber Interval"=0.25   "Round Camber Down?"=Yes   "Pre-Comp DL Ratio"=0   "SDL+LL Ratio"=240   "LL Ratio"=360   "Net Ratio"=240   "Ieff reduction Factor"=0.75 _
        "Vibration Criterion"=Walking   "Occupancy Category"="Paper Office"   "Walking Acceleration Limit, a0/g"=0.005   "Damping Ratio - Walking"=0.025   "Optimize Price?"=Yes   "Steel Price ($)"=1   "Stud Price ($)"=2 _
        "Camber Price ($)"=0   phi-b=0.9   phi-bcpp=0.9   phi-v=0.9   "Reaction Factor"=1

TABLE:  "CONCRETE FRAME DESIGN PREFERENCES - ACI 318-19"
   "Multi-Response Design"="Step-by-Step - All"   "# Interaction Curves"=24   "# Interaction Points"=11   "Minimum Eccentricity?"=Yes   "Design for BCCR?"=No   "Ignore Beneficial Pu for Beam Design?"=Yes   "Seismic Design Category"=B _
        "Design System Omega0"=2   "Design System Rho"=1   "Design System Sds"=0.5   "Phi (Tension)"=0.9   "Phi (Compression Tied)"=0.65   "Phi (Compression Spiral)"=0.75   "Phi (Shear and Torsion)"=0.75   "Phi (Shear Seismic)"=0.6 _
        "Phi (Shear Joint)"=0.85   "Pattern Live Load Factor"=0.75   "Utlization Factor Limit"=1

TABLE:  "CONCRETE SLAB DESIGN PREFERENCES - ACI 318-19"
   PhiTen=0.9   PhiComp=0.65   PhiShear=0.75   CoverTop=0.75   CoverBot=0.75   BarSize=#6   InnerLayer="Layer B"   PTCGSTop=1   PTCGSBotExt=1.75   PTCGSBotInt=1   SlabType="Two Way"   UserStress=No   InitConcRat=0.8   LLFraction=0.5
END TABLE DATA
""";
}
