using System.Globalization;
using CsiProMax.E2k;

namespace CsiProMax.Model;

/// <summary>
/// SAP2000 26.3's Assign > Joint, Frame, Area ... Loads forms on a model: each form gives SAP2000's own rows of its
/// table (Joint Loads - Force, Frame Loads - Distributed, Frame Loads - Gravity, Overwrites - Auto Wind Loads - Frame,
/// Frame Fireproofing Assignments, ...) and SAP2000's option (Add to Existing Loads, Replace Existing Loads, Delete
/// Existing Loads). A row the Modeler holds as its own load (a force in GLOBAL, a load along a frame in GLOBAL or Local
/// directions, a temperature or strain by element) becomes that load, so it is drawn and goes to ETABS too; any other
/// row is kept on its object as SAP2000 has it (<see cref="SapObjectTables"/>) and written back into the same table.
/// </summary>
public static class SapObjectLoads
{
    // The Modeler's own load kinds behind SAP2000's tables, and the TYPE tokens they are.
    private static readonly Dictionary<string, (string Kind, string[] Types)> Own = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Joint Loads - Force"] = ("joint", ["FORCE"]),
        ["Joint Loads - Ground Displacement"] = ("joint", ["GROUNDDISP"]),
        ["Frame Loads - Distributed"] = ("frame", ["UNIFF", "TRAPF"]),
        ["Frame Loads - Point"] = ("frame", ["POINTF", "POINTM"]),
        ["Frame Loads - Temperature"] = ("frame", ["TEMP"]),
        ["Frame Loads - Strain"] = ("frame", ["STRAIN"]),
        ["Area Loads - Uniform"] = ("area", ["UNIFF"]),
        ["Area Loads - Temperature"] = ("area", ["TEMP"]),
        ["Area Loads - Strain"] = ("area", ["STRAIN"]),
    };

    // Tables that hold one row for an object whatever its load pattern: Replace and Delete take every row off.
    private static readonly HashSet<string> PerObject = new(StringComparer.OrdinalIgnoreCase) { "Frame Fireproofing Assignments" };

    /// <summary>The SAP2000 tables a load form can set.</summary>
    public static IEnumerable<string> Forms => Own.Keys.Concat(SapObjectTables.Tables.Keys.Where(k => !SapObjectTables.IsNative(k))).Concat(SapOtherObjects.Tables.Keys.Where(SapOtherObjects.IsLoad));

    /// <summary>Sets the rows on each object (label@story; a joint is point@story). option: add, replace or delete.
    /// Says how many objects changed. Throws ArgumentException for a wrong table, object, field or option.</summary>
    public static int Assign(StructuralModel m, string table, IReadOnlyList<string> objects, string? pattern, IReadOnlyList<Dictionary<string, string>> rows, string option)
    {
        option = option.ToLowerInvariant();
        if (option is not ("add" or "replace" or "delete")) throw new ArgumentException("option is add, replace or delete.");
        if (SapOtherObjects.IsLoad(table)) return AssignOther(m, table, objects, pattern, rows, option);
        var native = Own.ContainsKey(table);
        var store = native ? Own.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase)) + SapObjectTables.Kept : table;
        if (!SapObjectTables.Has(store)) throw new ArgumentException($"There is no SAP2000 object table '{table}'. The tables: {string.Join(", ", Forms)}.");
        store = SapObjectTables.Name(store);
        var (kind, _, fields) = SapObjectTables.Tables[store];
        var byPattern = fields.Contains("LoadPat") && !PerObject.Contains(store);
        if (byPattern && string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Name the load pattern.");
        foreach (var p in rows.Select(r => r.FirstOrDefault(kv => kv.Key.Equals("LoadPat", StringComparison.OrdinalIgnoreCase)).Value).Append(byPattern ? pattern : null).Where(p => !string.IsNullOrEmpty(p)))
            if (!m.LoadPatterns.Exists(x => x.Name == p)) throw new ArgumentException($"There is no load pattern '{p}'.");
        foreach (var r in rows)
            foreach (var f in r.Keys)
                if (!fields.Contains(f, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"'{table}' has no field '{f}'. Its fields: {string.Join(", ", fields)}.");
        if (objects.Count == 0) throw new ArgumentException("Name the objects, as label@story.");
        var targets = objects.Select(o => Find(m, kind, o)).ToList();
        var lengths = kind == "frame" ? Physical.Frames(m).ToDictionary(f => (f.Label, f.Story), f => f.Length) : [];
        var types = native ? Own[table].Types : [];

        foreach (var (a, label) in targets)
        {
            if (option != "add")
            {
                bool Same(ObjectLoad l) => l.Object == label && l.Story == a.Story && l.Pattern == pattern && types.Contains(E2kText.Unquote(E2kText.Value(l.Tokens, "TYPE") ?? ""), StringComparer.OrdinalIgnoreCase);
                if (native)
                {
                    if (kind == "joint") m.PointLoads.RemoveAll(Same); else if (kind == "frame") m.LineLoads.RemoveAll(Same); else m.AreaLoads.RemoveAll(Same);
                }
                if (a.Tables?.TryGetValue(store, out var have) == true)
                {
                    have.RemoveAll(r => !byPattern || r.GetValueOrDefault("LoadPat") == pattern);
                    if (have.Count == 0) a.Tables.Remove(store);
                }
                if (!SapObjectTables.Tables[store].Many && option == "replace") a.Tables?.Remove(store);
            }
            if (option == "delete") { if (a.Tables is { Count: 0 }) a.Tables = null; continue; }
            foreach (var given in rows)
            {
                var r = given.ToDictionary(kv => fields.First(f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), kv => (kv.Value ?? "").Trim(), StringComparer.Ordinal);
                if (byPattern) r["LoadPat"] = pattern!;
                if (native && Tokens(table, r, lengths.GetValueOrDefault((label, a.Story))) is { } tokens)
                {
                    if (tokens.Count == 0) continue; // all zero: SAP2000 adds nothing
                    if (kind == "joint") m.PointLoads.Add(new PointLoad { Object = label, Story = a.Story, Tokens = tokens });
                    else if (kind == "frame") m.LineLoads.Add(new LineLoad { Object = label, Story = a.Story, Tokens = tokens });
                    else m.AreaLoads.Add(new AreaLoad { Object = label, Story = a.Story, Tokens = tokens });
                    continue;
                }
                a.Tables ??= new(StringComparer.OrdinalIgnoreCase);
                var list = a.Tables.TryGetValue(store, out var l) ? l : a.Tables[store] = [];
                if (!SapObjectTables.Tables[store].Many) list.Clear();
                list.Add(r.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
            }
            if (a.Tables is { Count: 0 }) a.Tables = null;
        }
        return targets.Count;
    }

    /// <summary>
    /// The Cable, Tendon, Solid and Link/Support Loads forms: their objects are SAP2000's own (by SAP2000 name, in a model
    /// opened from a .s2k), so the rows are kept in SAP2000's table (<see cref="SapOtherObjects"/>), set by load pattern.
    /// </summary>
    private static int AssignOther(StructuralModel m, string table, IReadOnlyList<string> objects, string? pattern, IReadOnlyList<Dictionary<string, string>> rows, string option)
    {
        table = SapOtherObjects.Tables.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));
        var (kind, _, fields) = SapOtherObjects.Tables[table];
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Name the load pattern.");
        if (!m.LoadPatterns.Exists(x => x.Name == pattern)) throw new ArgumentException($"There is no load pattern '{pattern}'.");
        foreach (var r in rows)
            foreach (var f in r.Keys)
                if (!fields.Contains(f, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"'{table}' has no field '{f}'. Its fields: {string.Join(", ", fields)}.");
        if (objects.Count == 0) throw new ArgumentException($"Name the {kind} objects, by SAP2000 name.");
        var have = SapOtherObjects.List(m).Where(o => o.Kind == kind).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var n in objects) if (!have.Contains(n)) throw new ArgumentException($"There is no {kind} '{n}'.");
        foreach (var n in objects.Distinct())
        {
            var list = SapOtherObjects.Rows(m, kind, n, table);
            if (option != "add") list.RemoveAll(r => r.GetValueOrDefault("LoadPat") == pattern);
            if (option != "delete")
                foreach (var r in rows)
                {
                    var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["LoadPat"] = pattern };
                    foreach (var (k, v) in r) if (!k.Equals("LoadPat", StringComparison.OrdinalIgnoreCase)) row[fields.First(f => f.Equals(k, StringComparison.OrdinalIgnoreCase))] = v;
                    list.Add(row);
                }
            // Every field named (blank when not given), so a row put where another was keeps none of its fields.
            var full = list.Select(r => fields.ToDictionary(f => f, f => r.GetValueOrDefault(f) ?? "", StringComparer.Ordinal)).ToList();
            SapOtherObjects.Set(m, kind, [n], new Dictionary<string, List<Dictionary<string, string>>> { [table] = full });
        }
        return objects.Distinct().Count();
    }

    private static (Assign A, string Label) Find(StructuralModel m, string kind, string o)
    {
        var at = o.LastIndexOf('@');
        if (at <= 0) throw new ArgumentException($"'{o}' is not label@story.");
        var (label, story) = (o[..at], o[(at + 1)..]);
        if (kind == "frame") return (m.LineAssigns.Find(x => x.Label == label && x.Story == story) ?? throw new ArgumentException($"There is no frame {o}."), label);
        if (kind == "area") return (m.AreaAssigns.Find(x => x.Label == label && x.Story == story) ?? throw new ArgumentException($"There is no area {o}."), label);
        if (m.Point(label) is null || m.StoryIndex(story) < 0) throw new ArgumentException($"There is no joint {o}.");
        var pa = m.PointAssigns.Find(x => x.Point == label && x.Story == story);
        if (pa is null) m.PointAssigns.Add(pa = new PointAssign { Point = label, Story = story });
        return (pa, label);
    }

    private static double N(Dictionary<string, string> r, string key, double fallback = 0) =>
        r.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static string T(Dictionary<string, string> r, string key, string fallback) => r.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    /// <summary>The Modeler's own load for a SAP2000 row (empty: a load of nothing), or null when only the row holds it.</summary>
    public static List<string>? Tokens(string table, Dictionary<string, string> r, double length)
    {
        var pattern = r.GetValueOrDefault("LoadPat") ?? "";
        var cs = T(r, "CoordSys", "GLOBAL").ToUpperInvariant();
        var dir = T(r, "Dir", "Gravity").ToUpperInvariant();
        var plain = cs == "GLOBAL" ? dir is "GRAVITY" or "X" or "Y" or "Z" : cs == "LOCAL" && dir is "1" or "2" or "3";
        var etabsDir = dir == "GRAVITY" ? "GRAV" : dir;
        var abs = T(r, "DistType", "RelDist").StartsWith("Abs", StringComparison.OrdinalIgnoreCase);
        double Rel(string relKey, string absKey, double fallback) => abs ? (length > 0 ? N(r, absKey) / length : double.NaN) : N(r, relKey, fallback);
        Dictionary<string, double> NonZero(params (string Sap, string Key)[] keys) => keys.Where(k => N(r, k.Sap) != 0).ToDictionary(k => k.Key, k => N(r, k.Sap));
        switch (table.ToUpperInvariant())
        {
            case "JOINT LOADS - FORCE":
            case "JOINT LOADS - GROUND DISPLACEMENT":
            {
                if (cs != "GLOBAL") return null;
                var force = table.Equals("Joint Loads - Force", StringComparison.OrdinalIgnoreCase);
                var v = force ? NonZero(("F1", "fx"), ("F2", "fy"), ("F3", "fz"), ("M1", "mx"), ("M2", "my"), ("M3", "mz")) : NonZero(("U1", "ux"), ("U2", "uy"), ("U3", "uz"), ("R1", "rx"), ("R2", "ry"), ("R3", "rz"));
                return v.Count == 0 ? [] : AnalysisDefs.LoadTokens(force ? "joint_force" : "joint_displacement", pattern, v, "");
            }
            case "FRAME LOADS - DISTRIBUTED":
            {
                if (!plain || !T(r, "Type", "Force").Equals("Force", StringComparison.OrdinalIgnoreCase)) return null;
                double a = N(r, "FOverLA"), b = N(r, "FOverLB", a), from = Rel("RelDistA", "AbsDistA", 0), to = Rel("RelDistB", "AbsDistB", 1);
                if (double.IsNaN(from) || double.IsNaN(to) || from < -1e-9 || to > 1 + 1e-9 || to < from) return null;
                if (a == 0 && b == 0) return [];
                return Math.Abs(a - b) < 1e-12 && from <= 1e-9 && to >= 1 - 1e-9
                    ? AnalysisDefs.LoadTokens("frame_uniform", pattern, new Dictionary<string, double> { ["value"] = a }, etabsDir)
                    : AnalysisDefs.LoadTokens("frame_trapezoid", pattern, new Dictionary<string, double> { ["start"] = a, ["end"] = b, ["from"] = Math.Clamp(from, 0, 1), ["to"] = Math.Clamp(to, 0, 1) }, etabsDir);
            }
            case "FRAME LOADS - POINT":
            {
                if (!plain) return null;
                var moment = T(r, "Type", "Force").Equals("Moment", StringComparison.OrdinalIgnoreCase);
                var value = N(r, moment ? "Moment" : "Force", N(r, "Force"));
                var at = Rel("RelDist", "AbsDist", 0.5);
                if (double.IsNaN(at) || at < -1e-9 || at > 1 + 1e-9) return null;
                if (value == 0) return [];
                return AnalysisDefs.LoadTokens("frame_point", pattern, new Dictionary<string, double> { [moment ? "moment" : "value"] = value, ["distance"] = Math.Clamp(at, 0, 1) }, etabsDir);
            }
            case "FRAME LOADS - TEMPERATURE":
                if (!T(r, "Type", "Temperature").Equals("Temperature", StringComparison.OrdinalIgnoreCase) || T(r, "JtPattern", "").Length > 0) return null;
                return AnalysisDefs.LoadTokens("frame_temperature", pattern, new Dictionary<string, double> { ["temperature"] = N(r, "Temp") }, "");
            case "FRAME LOADS - STRAIN":
            {
                var component = T(r, "Component", "Strain11");
                if (T(r, "JtPattern", "").Length > 0 || !component.StartsWith("Strain", StringComparison.OrdinalIgnoreCase) || component.Length != 8) return null;
                if (N(r, "Strain") == 0) return [];
                return AnalysisDefs.LoadTokens("frame_strain", pattern, new Dictionary<string, double> { ["strain"] = N(r, "Strain") }, component[6..]);
            }
            case "AREA LOADS - UNIFORM":
                if (!plain) return null;
                return N(r, "UnifLoad") == 0 ? [] : AnalysisDefs.LoadTokens("shell_uniform", pattern, new Dictionary<string, double> { ["value"] = N(r, "UnifLoad") }, etabsDir);
            case "AREA LOADS - TEMPERATURE":
                if (!T(r, "Type", "Temperature").Equals("Temperature", StringComparison.OrdinalIgnoreCase) || T(r, "JtPattern", "").Length > 0) return null;
                return AnalysisDefs.LoadTokens("shell_temperature", pattern, new Dictionary<string, double> { ["temperature"] = N(r, "Temp") }, "");
            case "AREA LOADS - STRAIN":
            {
                var component = T(r, "Component", "Strain11");
                if (T(r, "JtPattern", "").Length > 0 || !component.StartsWith("Strain", StringComparison.OrdinalIgnoreCase) || component.Length != 8) return null;
                if (N(r, "Strain") == 0) return [];
                return AnalysisDefs.LoadTokens("shell_strain", pattern, new Dictionary<string, double> { ["strain"] = N(r, "Strain") }, component[6..]);
            }
        }
        return null;
    }
}
