namespace CsiProMax.Model;

/// <summary>
/// The assignments of Assign > Frame, Shell and Joint that ETABS's .e2k has no keyword for (added mass, output stations,
/// auto mesh, edge releases and constraints, insertion point and local axes of shells, material and thickness
/// overwrites, tension and compression limits, panel zones, ...): ETABS 23's own assignment tables, with the fields and
/// field names its "Table and Field Keys" give (2026-11-26). An object keeps its rows in <see cref="Assign.Tables"/> by
/// the full table name, without Story, Label and UniqueName; they go into ETABS through its database tables, and into
/// SAP2000's .s2k where SAP2000 has a table for them.
/// </summary>
public static class AssignTables
{
    /// <summary>Each table: which objects it is for (frame, area, joint) and its fields with the names the program shows.</summary>
    public static readonly IReadOnlyDictionary<string, (string Kind, (string Key, string Label)[] Fields)> Tables =
        new Dictionary<string, (string, (string, string)[])>(StringComparer.OrdinalIgnoreCase)
    {
        ["Frame Assignments - Additional Mass"] = ("frame", [("AddedMass", "Added Mass")]),
        ["Frame Assignments - Advanced Nonprismatic Section Parameters"] = ("frame", [("ClearLength", "Clear Length"), ("RelDist", "Relative Distance")]),
        ["Frame Assignments - Column Splice Overwrites"] = ("frame", [("SpliceOpt", "Splice Option"), ("SpliceHt", "Splice Height")]),
        ["Frame Assignments - Floor Cracking"] = ("frame", [("Consider", "Consider for Cracking")]),
        ["Frame Assignments - Frame Auto Mesh Options"] = ("frame", [("AutoMesh", "Auto Mesh"), ("AtIntJoints", "At Intermediate Joints"), ("AtIntersect", "At Intersections"), ("MinNumber", "Min Number?"), ("NumSegments", "Number Segments"), ("MaxLength", "Max Length?"), ("SegLength", "Segment Length")]),
        ["Frame Assignments - Frame Floor Meshing Option"] = ("frame", [("MeshOption", "Offset Option")]),
        ["Frame Assignments - Material Overwrites"] = ("frame", [("Material", "Material")]),
        ["Frame Assignments - Moment Frame Beam Connection Type"] = ("frame", [("Type", "Type"), ("ConnLoc", "Connection Location"), ("SidePlOpt", "Sideplate Option"), ("RBSOption", "RBS Option"), ("x1", "x1"), ("x2", "x2"), ("x3", "x3"), ("a", "a"), ("b", "b"), ("c", "c")]),
        ["Frame Assignments - Output Stations"] = ("frame", [("StationOpt", "Station Option"), ("MaxSpacing", "Max Spacing"), ("MinStations", "Min Stations")]),
        ["Frame Assignments - Rebar Ratio For Creep Analysis"] = ("frame", [("RebarOpt", "Rebar Option"), ("UserRatio", "User Ratio")]),
        ["Frame Assignments - Tension and Compression Limits"] = ("frame", [("IncludeTens", "Include Tension Limit"), ("TensLimit", "Tension Limit"), ("IncludeComp", "Include Compression Limit"), ("CompLimit", "Compression Limit")]),
        ["Area Assignments - Additional Mass"] = ("area", [("AddedMass", "Added Mass")]),
        ["Area Assignments - Auto Edge Constraints"] = ("area", [("Constraint", "Constraint")]),
        ["Area Assignments - Edge Releases"] = ("area", [("EdgeNumber", "Edge Number"), ("PointLabels", "Point Labels"), ("RInShear", "Release In Plane Shear"), ("RDirForce", "Release In Plane Direct Force"), ("ROutShear", "Release Out of Plane Shear"), ("RBending", "Release Bending"), ("RTwisting", "Release Twisting"), ("SInShear", "Spring In Plane Shear"), ("SDirForce", "Spring In Plane Direct Force"), ("SOutShear", "Spring Out of plane Shear"), ("SBending", "Spring Bending"), ("STwisting", "Spring Twisting")]),
        ["Area Assignments - Floor Auto Mesh Options"] = ("area", [("MeshOption", "Mesh Option"), ("N1", "N1"), ("N2", "N2"), ("AtBeams", "At Beams"), ("AtWalls", "At Walls"), ("AtGrids", "At Grids"), ("Submesh", "Submesh"), ("SubmeshSize", "Submesh Size"), ("Restraints", "Add Restraints")]),
        ["Area Assignments - Floor Cracking"] = ("area", [("Consider", "Consider for Cracking")]),
        ["Area Assignments - Insertion Point"] = ("area", [("CardinalPt", "Cardinal Point"), ("CoordSys", "Coordinate System"), ("PointNumber", "Point Number"), ("PointLabel", "Point Label"), ("PointName", "Point Unique Name"), ("Offset1", "Offset1"), ("Offset2", "Offset2"), ("Offset3", "Offset3"), ("Transform", "Transform")]),
        ["Area Assignments - Local Axes"] = ("area", [("Angle", "Angle")]),
        ["Area Assignments - Material Overwrites"] = ("area", [("Material", "Material")]),
        // On a null line, not an area: its UniqueName is the line's (ETABS 23.2, the J export of 2026-11-26).
        ["Area Assignments - Slab Line Releases"] = ("frame", [("ShearL", "Shear Left"), ("MomentL", "Moment Left"), ("ShearR", "Shear Right"), ("MomentR", "Moment Right")]),
        ["Area Assignments - Slab Rib Locations"] = ("area", [("X", "X"), ("Y", "Y")]),
        ["Area Assignments - Thickness Overwrites"] = ("area", [("PointNumber", "Point Number"), ("PointLabel", "Point Label"), ("PointName", "Point Unique Name"), ("Thickness", "Thickness Overwrite")]),
        ["Area Assignments - Wall Auto Mesh Options"] = ("area", [("MeshOption", "Mesh Option"), ("Nv", "Nv"), ("Nh", "Nh"), ("Restraints", "Add Restraints")]),
        ["Area Assignments - Wall Rebar Ratio for Creep Analysis"] = ("area", [("RebarOption", "Vertical Rebar Option"), ("UserRatio", "User Ratio")]),
        ["Joint Assignments - Additional Mass"] = ("joint", [("MassXY", "Mass X and Y"), ("MassZ", "MassZ"), ("MMIX", "MMI X"), ("MMIY", "MMI Y"), ("MMIZ", "MMI Z")]),
        ["Joint Assignments - Floor Meshing Option"] = ("joint", [("IncludeInMesh", "IncludeInMesh")]),
        ["Joint Assignments - Panel Zone"] = ("joint", [("PanelZone", "Panel Zone Assignment"), ("GenPanelZone", "Generated PanelZone")]),
    };

    /// <summary>The tables with a row per edge or corner of the object, not one row.</summary>
    public static readonly string[] ManyRows = ["Area Assignments - Edge Releases", "Area Assignments - Thickness Overwrites", "Area Assignments - Slab Rib Locations"];

    /// <summary>The kind of objects an assignment is for.</summary>
    public static string KindOf(Assign a) => a switch { LineAssign => "frame", AreaAssign => "area", _ => "joint" };

    /// <summary>
    /// Checks and sets an object's rows: each table must be one of this kind's, each field one of the table's; a table
    /// given with no rows (or only empty values) is taken off. Values are kept as text, as the programs' tables hold them.
    /// </summary>
    public static void Set(Assign a, IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables)
    {
        var kind = KindOf(a);
        foreach (var (table, rows) in tables)
        {
            if (!Tables.TryGetValue(table, out var t) || t.Kind != kind)
                throw new ArgumentException($"There is no {kind} assignment table '{table}'. The tables: {string.Join(", ", Tables.Where(x => x.Value.Kind == kind).Select(x => x.Key))}.");
            var name = Tables.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));
            if (rows.Count > 1 && !ManyRows.Contains(name)) throw new ArgumentException($"'{name}' holds one row for an object, not {rows.Count}.");
            foreach (var row in rows)
                foreach (var f in row.Keys)
                    if (!t.Fields.Any(x => x.Key.Equals(f, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"'{name}' has no field '{f}'. Its fields: {string.Join(", ", t.Fields.Select(x => x.Key))}.");
            a.Tables ??= new(StringComparer.OrdinalIgnoreCase);
            var clean = rows.Select(r => r.Where(kv => kv.Value is not null).ToDictionary(kv => t.Fields.First(x => x.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)).Key, kv => kv.Value.Trim(), StringComparer.Ordinal))
                .Where(r => r.Values.Any(v => v.Length > 0)).ToList();
            if (clean.Count == 0) a.Tables.Remove(name);
            else a.Tables[name] = clean;
        }
        if (a.Tables is { Count: 0 }) a.Tables = null;
    }

    /// <summary>The first row of an object's table, or an empty one.</summary>
    public static IReadOnlyDictionary<string, string> Row(Assign a, string table) =>
        a.Tables is not null && a.Tables.TryGetValue(table, out var rows) && rows.Count > 0 ? rows[0] : new Dictionary<string, string>();

    /// <summary>Every object's rows by table, each with its Story and Label first (a joint's label is its point).</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> Rows(StructuralModel model)
    {
        var all = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        void Take(Assign a, string label)
        {
            foreach (var (table, rows) in a.Tables ?? [])
                foreach (var row in rows)
                {
                    var full = new Dictionary<string, string>(StringComparer.Ordinal) { ["Story"] = a.Story, ["Label"] = label };
                    foreach (var (k, v) in row) full[k] = v;
                    (all.TryGetValue(table, out var list) ? list : all[table] = []).Add(full);
                }
        }
        foreach (var a in model.LineAssigns) Take(a, a.Label);
        foreach (var a in model.AreaAssigns) Take(a, a.Label);
        foreach (var a in model.PointAssigns) Take(a, a.Point);
        return all;
    }
}
