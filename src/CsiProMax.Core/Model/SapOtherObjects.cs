using System.Globalization;
using System.IO.Compression;
using System.Text;
using CsiProMax.CsiTables;

namespace CsiProMax.Model;

/// <summary>
/// SAP2000's cables, tendons, solids and links in a model opened from a .s2k: the Modeler holds them as SAP2000's own
/// tables, kept as the file had them (<see cref="StructuralModel.Kept"/>, "sap2000|CONNECTIVITY - CABLE" and so on), so
/// they go back into the .s2k unchanged. This class finds each object and where it is (its joints from the file's
/// JOINT COORDINATES), so the Engine draws them and SAP2000's Assign > Cable, Tendon, Solid and Link/Support forms can
/// set their rows; a changed kept row goes out through the .s2k like any other (SourceTables merges it in).
/// </summary>
public static class SapOtherObjects
{
    public static readonly string[] Kinds = ["cable", "tendon", "solid", "link"];

    /// <summary>The field that names the object in its tables.</summary>
    public static string Key(string kind) => kind switch { "cable" => "Cable", "tendon" => "Tendon", "solid" => "Solid", "link" => "Link", _ => throw new ArgumentException($"There is no SAP2000 object kind '{kind}'.") };

    public static string Connectivity(string kind) => "CONNECTIVITY - " + Key(kind).ToUpperInvariant();

    /// <summary>
    /// SAP2000 26.3's assignment tables of each kind (field/table-keys/sap2000-26.3-field-names.txt): the kind, whether an
    /// object has several rows (a face or edge each), and the fields after the object's name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Kind, bool Many, string[] Fields)> Tables = new Dictionary<string, (string, bool, string[])>(StringComparer.OrdinalIgnoreCase)
    {
        ["Cable Section Assignments"] = ("cable", false, ["CableSect", "MatProp"]),
        ["Cable Property Modifiers"] = ("cable", false, ["AMod", "MassMod", "WeightMod"]),
        ["Cable Output Station Assignments"] = ("cable", false, ["StationType", "MinNumSta", "MaxStaSpcg", "AddAtElmInt", "AddAtPtLoad"]),
        ["Cable Added Mass Assignments"] = ("cable", false, ["MassPerLen"]),
        ["Cable Material Temperatures"] = ("cable", false, ["Temp", "JtPattern"]),
        ["Tendon Section Assignments"] = ("tendon", false, ["TendonSect"]),
        ["Tendon Local Axes Assignments 1 - Typical"] = ("tendon", false, ["Angle", "AdvanceAxes"]),
        ["Tendon Tension And Compression Limits"] = ("tendon", false, ["TensLimit", "CompLimit", "Tension", "Compression"]),
        ["Tendon Material Temperatures"] = ("tendon", false, ["Temp", "JtPattern"]),
        ["Solid Property Assignments"] = ("solid", false, ["SolidProp"]),
        ["Solid Local Axes Assignments 1 - Typical"] = ("solid", false, ["AngleA", "AngleB", "AngleC", "AdvanceAxes"]),
        ["Solid Local Axes Assignments 2 - Advanced"] = ("solid", false, ["LocalPlane", "AxOption1", "AxCoordSys", "AxCoordDir", "AxVecJt1", "AxVecJt2", "PlOption1", "PlCoordSys", "CoordDir1", "CoordDir2", "PlVecJt1", "PlVecJt2", "AxVecX", "AxVecY", "AxVecZ", "PlVecX", "PlVecY", "PlVecZ"]),
        ["Solid Spring Assignments"] = ("solid", true, ["Type", "Stiffness", "SimpleType", "LinkProp", "Face", "Dir1Type", "Dir", "NormalDir", "CoordSys", "VecX", "VecY", "VecZ", "Axis2Angle"]),
        ["Solid Material Temperatures"] = ("solid", false, ["JtPattern", "Temp"]),
        ["Solid Auto Mesh Assignments"] = ("solid", false, ["AutoMesh", "MeshType", "Number1", "Number2", "Number3", "Max1", "Max2", "Max3", "AddSupport", "AddSuppSurf"]),
        ["Solid Edge Constraint Assignments"] = ("solid", true, ["Edge", "Constrained"]),
        ["Link Property Assignments"] = ("link", false, ["LinkType", "LinkJoints", "LinkProp", "LinkFDProp", "PropMod"]),
        ["Link Local Axes Assignments 1 - Typical"] = ("link", false, ["Angle", "AdvanceAxes"]),
        ["Link Local Axes Assignments 2 - Advanced"] = ("link", false, ["LocalPlane", "AxOption1", "AxCoordSys", "AxCoordDir", "AxVecJt1", "AxVecJt2", "PlOption1", "PlCoordSys", "CoordDir1", "CoordDir2", "PlVecJt1", "PlVecJt2", "AxVecX", "AxVecY", "AxVecZ", "PlVecX", "PlVecY", "PlVecZ"]),
    };

    public static bool Has(string table) => Tables.ContainsKey(table);

    private static string KeptKey(string table) => "sap2000|" + table.ToUpperInvariant();

    /// <summary>The rows of a kept table (an empty list when the file had none).</summary>
    public static List<Dictionary<string, string>> Kept(StructuralModel m, string table) =>
        m.Kept is not null && m.Kept.TryGetValue(KeptKey(table), out var rows) ? rows : [];

    /// <summary>Each object of the kinds the file has, with its joints and where they are.</summary>
    public static List<(string Kind, string Name, List<string> Joints, List<double[]> Points)> List(StructuralModel m)
    {
        var all = new List<(string, string, List<string>, List<double[]>)>();
        if (m.Kept is null) return all;
        Dictionary<string, double[]>? at = null;
        foreach (var kind in Kinds)
        {
            var rows = Kept(m, Connectivity(kind));
            if (rows.Count == 0) continue;
            at ??= Joints(m);
            foreach (var r in rows)
            {
                var name = r.GetValueOrDefault(Key(kind)) ?? "";
                if (name.Length == 0) continue;
                var joints = JointsOf(kind, r);
                var pts = joints.Select(j => at.TryGetValue(j, out var p) ? p : null).ToList();
                if (pts.Count == 0 || pts.Exists(p => p is null)) continue;
                all.Add((kind, name, joints, pts!));
            }
        }
        return all;
    }

    /// <summary>A connectivity row's joints: I and J of a cable, tendon or link (a one-joint link has I only), the 8 corners of a solid.</summary>
    public static List<string> JointsOf(string kind, IReadOnlyDictionary<string, string> r) => kind == "solid"
        ? Enumerable.Range(1, 8).Select(i => r.GetValueOrDefault("Joint" + i.ToString(CultureInfo.InvariantCulture)) ?? "").Where(j => j.Length > 0).ToList()
        : new[] { r.GetValueOrDefault("JointI") ?? "", r.GetValueOrDefault("JointJ") ?? "" }.Where(j => j.Length > 0).ToList();

    private static string? jointsFor;
    private static Dictionary<string, double[]> jointsCache = [];

    /// <summary>Where each joint of the file is: the file's own JOINT COORDINATES (global X, Y, Z), kept with the model.</summary>
    public static Dictionary<string, double[]> Joints(StructuralModel m)
    {
        var at = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var kept = Kept(m, "JOINT COORDINATES");
        if (kept.Count == 0 && m.Source is { Program: "sap2000", Original.Length: > 0 } src)
        {
            if (jointsFor != src.Original)
            {
                var tables = CsiTableText.Parse(Unpack(src.Original));
                jointsCache = Read(tables.FirstOrDefault(t => t.Key.Equals("JOINT COORDINATES", StringComparison.OrdinalIgnoreCase)).Value ?? []);
                jointsFor = src.Original;
            }
            return new Dictionary<string, double[]>(jointsCache, StringComparer.OrdinalIgnoreCase);
        }
        return kept.Count > 0 ? Read(kept) : at;
    }

    private static Dictionary<string, double[]> Read(List<Dictionary<string, string>> rows)
    {
        var at = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        double N(Dictionary<string, string> r, string a, string b) =>
            double.TryParse(r.GetValueOrDefault(a) ?? r.GetValueOrDefault(b), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        foreach (var r in rows)
            if (r.GetValueOrDefault("Joint") is { Length: > 0 } j)
                at[j] = [N(r, "GlobalX", "XorR"), N(r, "GlobalY", "Y"), N(r, "GlobalZ", "Z")];
        return at;
    }

    private static string Unpack(string packed)
    {
        try
        {
            using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(packed)), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException) { return ""; }
    }

    /// <summary>The object's rows of a table, without its name.</summary>
    public static List<Dictionary<string, string>> Rows(StructuralModel m, string kind, string name, string table)
    {
        var key = Key(kind);
        return Kept(m, table).Where(r => r.GetValueOrDefault(key) == name).Select(r => r.Where(kv => kv.Key != key && kv.Key != "Frame" && kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)).ToList();
    }

    /// <summary>Every table's rows of one object (for the forms).</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> RowsOf(StructuralModel m, string kind, string name) =>
        Tables.Where(t => t.Value.Kind == kind).Select(t => (t.Key, Rows: Rows(m, kind, name, t.Key))).Where(t => t.Rows.Count > 0)
            .ToDictionary(t => t.Key, t => t.Rows, StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks rows for a kind's tables: each table must be the kind's, each field one of the table's.</summary>
    public static void Check(string kind, IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables)
    {
        foreach (var (table, rows) in tables)
        {
            if (!Tables.TryGetValue(table, out var t) || t.Kind != kind) throw new ArgumentException($"'{table}' is not one of SAP2000's {kind} assignment tables.");
            if (!t.Many && rows.Count > 1) throw new ArgumentException($"'{table}' has one row per {kind}.");
            foreach (var r in rows)
                foreach (var f in r.Keys)
                    if (!t.Fields.Contains(f, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"'{table}' has no field '{f}'.");
        }
    }

    /// <summary>Sets the objects' rows of each table given (no rows takes the table off them), in SAP2000's field order.</summary>
    public static void Set(StructuralModel m, string kind, IReadOnlyCollection<string> names, IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables)
    {
        Check(kind, tables);
        var have = List(m).Where(o => o.Kind == kind).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var n in names) if (!have.Contains(n)) throw new ArgumentException($"There is no {kind} '{n}'.");
        var key = Key(kind);
        foreach (var (table0, rows) in tables)
        {
            var table = Tables.Keys.First(k => k.Equals(table0, StringComparison.OrdinalIgnoreCase));
            var fields = Tables[table].Fields;
            var given = rows.Where(r => r.Values.Any(v => v.Length > 0)).ToList();
            m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
            var list = m.Kept.TryGetValue(KeptKey(table), out var had) ? had : [];
            foreach (var n in names)
            {
                // The file's rows stay where they are and keep their other fields (GUID, Frame, ...): the form's
                // fields are set on them in order; rows beyond the form's go, rows beyond the file's go after the last.
                var mine = list.Where(r => r.GetValueOrDefault(key) == n).ToList();
                var at = mine.Count > 0 ? list.IndexOf(mine[^1]) + 1 : list.Count;
                for (var i = 0; i < Math.Max(mine.Count, given.Count); i++)
                {
                    if (i >= given.Count) { list.Remove(mine[i]); continue; }
                    var row = i < mine.Count ? mine[i] : new Dictionary<string, string>(StringComparer.Ordinal) { [key] = n };
                    foreach (var f in fields)
                    {
                        var v = given[i].FirstOrDefault(kv => kv.Key.Equals(f, StringComparison.OrdinalIgnoreCase)).Value;
                        if (v is { Length: > 0 }) row[f] = v;
                        else if (i >= mine.Count || given[i].Keys.Any(k => k.Equals(f, StringComparison.OrdinalIgnoreCase))) row.Remove(f);
                    }
                    if (i >= mine.Count) { list.Insert(Math.Min(at, list.Count), row); at = list.IndexOf(row) + 1; }
                }
            }
            if (list.Count == 0) m.Kept.Remove(KeptKey(table)); else m.Kept[KeptKey(table)] = list;
        }
    }

    /// <summary>
    /// Reverse Connectivity of cables and links (and the tendon's ends): joint I and J change places. A solid's Switch
    /// Faces swaps two faces' corners instead (<see cref="SwitchFaces"/>).
    /// </summary>
    public static void Reverse(StructuralModel m, string kind, IReadOnlyCollection<string> names)
    {
        if (kind == "solid") throw new ArgumentException("A solid has no I and J end: use Switch Faces.");
        var key = Key(kind);
        foreach (var r in Kept(m, Connectivity(kind)).Where(r => names.Contains(r.GetValueOrDefault(key) ?? "")))
        {
            if (!r.TryGetValue("JointJ", out var j) || j.Length == 0) continue;
            (r["JointI"], r["JointJ"]) = (j, r["JointI"]);
        }
    }

    /// <summary>The corners of each face of a solid, as SAP2000 numbers them (face 1: corners 1 3 5 7 ... see SAP2000's solid object).</summary>
    public static readonly int[][] Faces = [[1, 3, 5, 7], [2, 4, 6, 8], [1, 2, 5, 6], [3, 4, 7, 8], [1, 2, 3, 4], [5, 6, 7, 8]];

    /// <summary>Assign > Solid > Switch Faces: the solid's corners renumbered so face <paramref name="a"/> and face
    /// <paramref name="b"/> change places (the opposite faces swap with them). With move, the face-based rows (springs)
    /// follow their face.</summary>
    public static void SwitchFaces(StructuralModel m, IReadOnlyCollection<string> names, int a, int b, bool move)
    {
        if (a is < 1 or > 6 || b is < 1 or > 6) throw new ArgumentException("A solid has faces 1 to 6.");
        if (a == b) return;
        // The corner permutation that takes face a onto face b: faces come in opposite pairs (1-2, 3-4, 5-6) along the
        // solid's three directions; switching two faces swaps their directions (and flips where the pair differs).
        int Axis(int f) => (f - 1) / 2;
        bool Neg(int f) => (f - 1) % 2 == 1;
        var axisA = Axis(a); var axisB = Axis(b);
        int[] Bits(int corner) { var c = corner - 1; return [c & 1, (c >> 1) & 1, (c >> 2) & 1]; }
        // SAP2000's corner order: corner = 1 + x + 2y + 4z with faces 1/2 = x 0/1 ... the face lists above are by these bits.
        int Corner(int[] bits) => 1 + bits[0] + 2 * bits[1] + 4 * bits[2];
        var map = new int[9];
        for (var c = 1; c <= 8; c++)
        {
            var bits = Bits(c);
            var nb = (int[])bits.Clone();
            if (axisA == axisB) { nb[axisA] = 1 - bits[axisA]; }
            else
            {
                nb[axisA] = Neg(a) == Neg(b) ? bits[axisB] : 1 - bits[axisB];
                nb[axisB] = Neg(a) == Neg(b) ? bits[axisA] : 1 - bits[axisA];
            }
            map[c] = Corner(nb);
        }
        foreach (var r in Kept(m, Connectivity("solid")).Where(r => names.Contains(r.GetValueOrDefault("Solid") ?? "")))
        {
            var old = Enumerable.Range(1, 8).Select(i => r.GetValueOrDefault("Joint" + i.ToString(CultureInfo.InvariantCulture)) ?? "").ToArray();
            for (var c = 1; c <= 8; c++) r["Joint" + map[c].ToString(CultureInfo.InvariantCulture)] = old[c - 1];
        }
        if (!move) return;
        string Swap(string f) => f == a.ToString(CultureInfo.InvariantCulture) ? b.ToString(CultureInfo.InvariantCulture) : f == b.ToString(CultureInfo.InvariantCulture) ? a.ToString(CultureInfo.InvariantCulture) : f;
        foreach (var r in Kept(m, "Solid Spring Assignments").Where(r => names.Contains(r.GetValueOrDefault("Solid") ?? "")))
            if (r.TryGetValue("Face", out var f)) r["Face"] = Swap(f);
    }
}
