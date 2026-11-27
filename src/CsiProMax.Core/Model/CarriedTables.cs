using System.Globalization;

namespace CsiProMax.Model;

/// <summary>
/// What a program's model holds that its text file does not carry (Ahmed 2026-11-27: "if something can not be carried
/// through e2k ... a workaround ... we work on the cpm file and when it is turned to edb the workaround converts the
/// required information"). Measured once, when a model comes from the program: the program's own tables of the model,
/// against the same tables after the program opened CSI ProMax's text file of it. Only what differed is kept here, in
/// the .cpm, as rows of the program's own tables; every time the model goes into that program again (a run, open_model),
/// the rows go in through its tables after the text file, so the program has the model exactly as it was.
/// </summary>
public sealed class CarriedTables
{
    /// <summary>The program the rows are for ("etabs", "sap2000", "safe"): they go only into that program.</summary>
    public string Program { get; set; } = "";
    /// <summary>When it was measured (UTC), for the reports.</summary>
    public DateTime Measured { get; set; }
    /// <summary>Each table's key fields ("Story|Label", "Name", ...; "" for a one-row table), by table.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each table's rows to put: its key fields and only the fields the text file lost (a whole row when the
    /// row was lost, every row when the table was).</summary>
    public Dictionary<string, List<Dictionary<string, string>>> Rows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int Count => Rows.Values.Sum(r => r.Count);
}

/// <summary>Works out <see cref="CarriedTables"/> from two reads of a program's model tables.</summary>
public static class TableCarry
{
    /// <summary>One table as the program gives it: field names and rows of values in that order.</summary>
    public sealed record Table(IReadOnlyList<string> Fields, IReadOnlyList<string[]> Rows);

    /// <summary>Fields that only number or tag an object for the program's own bookkeeping: never carried, never keys.</summary>
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.OrdinalIgnoreCase) { "GUID", "ObjGUID", "UniqueName", "UniqueNameI", "UniqueNameJ" };

    /// <summary>The field that names what a row is about, first match wins: an object by story and label, else a
    /// definition by name, else by what the table is about.</summary>
    private static readonly string[][] Identities =
    [
        ["Story", "Label"], ["Story", "PointBay"], ["Name"], ["Material"], ["LoadPattern"], ["LoadCase"], ["Case"], ["Combo"], ["ComboName"], ["Pier"],
        ["Spandrel"], ["Group"], ["GridSystem"], ["Function"], ["Link"], ["Tendon"], ["Strip"], ["Joint"], ["Point"], ["Frame"],
        ["Area"], ["Object"], ["Story"], ["Tower"], ["Label"],
    ];

    /// <summary>Fields that tell apart rows of one thing (a link's directions, a curve's points, a load's stations).</summary>
    private static readonly string[] Within =
        ["Direction", "DOF", "Dir", "Station", "StepNum", "Point Number", "PointNum", "EdgeNum", "LayerName", "Layer", "LoadPattern", "LoadCase", "Case", "Bar", "Order", "Tower"];

    /// <summary>The key fields of a table: what names the thing a row is about (<see cref="Identities"/>), and what
    /// tells apart several rows of one thing (<see cref="Within"/>, while the identity alone repeats). A table with
    /// none of them is keyed by every field that is never a number.</summary>
    public static List<string> KeyFields(Table t)
    {
        bool TextEverywhere(int i) => t.Rows.All(r => i < r.Length && !IsNumber(r[i]));
        var identity = Identities.FirstOrDefault(set => set.All(k => IndexOf(t.Fields, k) >= 0))?.Select(k => t.Fields[IndexOf(t.Fields, k)]).ToList();
        // A table of one row with nothing that names it (Miscellaneous Options, a form's settings): the row is matched
        // with the one row there, and put by merging into it. Keyed by its text fields, a changed Yes/No made it a
        // "lost" row that went in beside the program's own (0.4.73 Ex002: Bounding Plane Yes came back No).
        if (identity is null && t.Rows.Count <= 1) return [];
        if (identity is null)
            return t.Fields.Select((f, i) => (f, i)).Where(x => !Bookkeeping.Contains(x.f) && TextEverywhere(x.i)).Select(x => x.f).ToList();
        foreach (var w in Within)
        {
            if (Unique(t, identity)) break;
            var i = IndexOf(t.Fields, w);
            if (i >= 0 && !identity.Contains(t.Fields[i], StringComparer.OrdinalIgnoreCase)) identity.Add(t.Fields[i]);
        }
        return identity;
    }

    private static bool Unique(Table t, List<string> keys) => t.Rows.Select(r => RowKey(t, r, keys)).Distinct().Count() == t.Rows.Count;

    /// <summary>
    /// What of <paramref name="before"/> (the program's tables of its own model) is missing from or different in
    /// <paramref name="after"/> (the same tables once the program opened CSI ProMax's text file of the model).
    /// Tables the text file brought in whole are not kept.
    /// </summary>
    public static CarriedTables Measure(string program, IReadOnlyDictionary<string, Table> before, IReadOnlyDictionary<string, Table> after)
    {
        var carried = new CarriedTables { Program = program, Measured = DateTime.UtcNow };
        var auto = AutoPoints(before); var autoAfter = AutoPoints(after);
        foreach (var (key, a0) in before)
        {
            var a = WithoutInternal(WithoutAutoPoints(a0, auto));
            if (a.Rows.Count == 0) continue;
            if (after.TryGetValue(key, out var listed) && SameLists(key, a, WithoutInternal(listed))) continue;
            var keys = KeyFields(a);
            var fields = a.Fields.Where(f => !Bookkeeping.Contains(f)).ToList();
            var rows = new List<Dictionary<string, string>>();
            Dictionary<string, string> Whole(string[] r) => fields.ToDictionary(f => f, f => Value(a, r, f), StringComparer.OrdinalIgnoreCase);
            if (!after.TryGetValue(key, out var b0) || WithoutInternal(WithoutAutoPoints(b0, autoAfter)) is not { Rows.Count: > 0 } b)
            {
                rows.AddRange(a.Rows.Select(Whole));
            }
            else
            {
                var scale = fields.ToDictionary(f => f, f => a.Rows.Select(r => Number(Value(a, r, f))).Where(v => !double.IsNaN(v)).Select(Math.Abs).DefaultIfEmpty(0).Max(), StringComparer.OrdinalIgnoreCase);
                // Rows matched by their key, in order among rows of the same key.
                var byKey = b.Rows.GroupBy(r => RowKey(b, r, keys)).ToDictionary(g => g.Key, g => new Queue<string[]>(g));
                foreach (var r in a.Rows)
                {
                    if (!byKey.TryGetValue(RowKey(a, r, keys), out var q) || q.Count == 0) { rows.Add(Whole(r)); continue; }
                    var other = q.Dequeue();
                    var changed = fields.Where(f => !keys.Contains(f, StringComparer.OrdinalIgnoreCase) && (IndexOf(b.Fields, f) < 0 || !Same(Value(a, r, f), Value(b, other, f), scale[f]))).ToList();
                    if (changed.Count == 0) continue;
                    var row = keys.ToDictionary(k => k, k => Value(a, r, k), StringComparer.OrdinalIgnoreCase);
                    foreach (var f in changed) row[f] = Value(a, r, f);
                    rows.Add(row);
                }
            }
            if (rows.Count == 0) continue;
            carried.Keys[key] = string.Join("|", keys);
            carried.Rows[key] = rows;
        }
        return carried;
    }

    /// <summary>The points the program makes itself (ETABS's diaphragm centres: "Is Auto Point" Yes in "Point Object
    /// Connectivity"), by story and label. It places and numbers them on its own, takes none of their rows (0.4.73: "0
    /// fatal, 3 errors" and the whole table refused), so nothing of them is carried.</summary>
    public static HashSet<string> AutoPoints(IReadOnlyDictionary<string, Table> tables)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var t = tables.FirstOrDefault(x => x.Key.Equals("Point Object Connectivity", StringComparison.OrdinalIgnoreCase)).Value;
        if (t is null || IndexOf(t.Fields, "IsAuto") < 0) return set;
        foreach (var r in t.Rows)
            if (Value(t, r, "IsAuto").Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                set.Add(Value(t, r, "Story").Trim() + "\u0001" + Value(t, r, "PointBay").Trim());
        return set;
    }

    /// <summary>A named table set (ETABS's "Database Table Named Set Definitions") lists its tables, patterns, cases and
    /// combinations down columns, packed into as many rows as the longest list: one item fewer packs the others into
    /// other rows. The same when each set has the same items in each column, in any row.</summary>
    private static bool SameLists(string table, Table a, Table b)
    {
        if (!table.Equals("Database Table Named Set Definitions", StringComparison.OrdinalIgnoreCase) || IndexOf(a.Fields, "Name") < 0) return false;
        string Lists(Table t) => string.Join("\n", t.Rows.GroupBy(r => Value(t, r, "Name").Trim().ToUpperInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            g.Key + ":" + string.Join(";", t.Fields.Where(f => !Bookkeeping.Contains(f) && !f.Equals("Name", StringComparison.OrdinalIgnoreCase)).Select(f =>
                f + "=" + string.Join(",", g.Select(r => Value(t, r, f).Trim().ToUpperInvariant()).Where(v => v.Length > 0).Distinct().OrderBy(v => v, StringComparer.Ordinal))))));
        return Lists(a) == Lists(b);
    }

    /// <summary>ETABS's own internal items ("~LLRF", the live load reduction case it makes itself) are never carried: it
    /// takes no row naming one (0.4.73 Ex002: the table set's rows refused, "2 errors", and the table left as it was). A
    /// row about one is left out; one named among other values is left blank.</summary>
    private static Table WithoutInternal(Table t)
    {
        static bool Internal(string v) => v.TrimStart().StartsWith('~');
        if (!t.Rows.Any(r => r.Any(v => v is not null && Internal(v)))) return t;
        var keys = Identities.FirstOrDefault(set => set.All(k => IndexOf(t.Fields, k) >= 0)) ?? [];
        var at = keys.Select(k => IndexOf(t.Fields, k)).ToHashSet();
        var rows = t.Rows.Where(r => !at.Any(i => i < r.Length && r[i] is not null && Internal(r[i])))
            .Select(r => r.Select(v => v is not null && Internal(v) ? "" : v ?? "").ToArray()).ToList();
        return new Table(t.Fields, rows);
    }

    private static Table WithoutAutoPoints(Table t, HashSet<string> auto)
    {
        if (auto.Count == 0) return t;
        var label = IndexOf(t.Fields, "PointBay") >= 0 ? "PointBay" : IndexOf(t.Fields, "Label") >= 0 ? "Label" : null;
        var story = IndexOf(t.Fields, "Story") >= 0;
        if (label is null || !story) return t;
        var rows = t.Rows.Where(r => !auto.Contains(Value(t, r, "Story").Trim() + "\u0001" + Value(t, r, label).Trim())).ToList();
        return rows.Count == t.Rows.Count ? t : new Table(t.Fields, rows);
    }

    /// <summary>The order the tables go in: definitions (materials, sections, patterns, cases) before what uses them
    /// (objects, assignments, loads), and a table's own order otherwise.</summary>
    public static int Rank(string table)
    {
        var t = table.ToLowerInvariant();
        if (t.Contains("assignment")) return t.Contains("load") ? 9 : 8;
        if (t.Contains("material")) return 0;
        if (t.Contains("section") || t.Contains("propert") || t.Contains("rebar")) return 1;
        if (t.Contains("function")) return 2;
        if (t.Contains("pattern") || t.Contains("mass source")) return 3;
        if (t.Contains("load case") || t.Contains("case definition")) return 4;
        if (t.Contains("combination")) return 5;
        if (t.Contains("definition") || t.Contains("preference") || t.Contains("option")) return 6;
        if (t.Contains("connectivity") || t.Contains("object")) return 7;
        if (t.Contains("assignment")) return 8;
        if (t.Contains("load")) return 9;
        return 10;
    }

    private static string RowKey(Table t, string[] r, List<string> keys) => string.Join("\u0001", keys.Select(k => Value(t, r, k).Trim().ToUpperInvariant()));

    private static string Value(Table t, string[] r, string field)
    {
        var i = IndexOf(t.Fields, field);
        return i >= 0 && i < r.Length ? r[i] ?? "" : "";
    }

    private static int IndexOf(IReadOnlyList<string> fields, string f)
    {
        for (var i = 0; i < fields.Count; i++) if (string.Equals(fields[i], f, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool IsNumber(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    private static double Number(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    /// <summary>Text the same (case and outer spaces aside), numbers within 1e-4 of each other relatively, or of the
    /// column's largest value for a value near zero (as the field test's comparison).</summary>
    public static bool Same(string a, string b, double scale)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
        return Math.Abs(x - y) <= 1e-4 * Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), 1e-3 * scale) + 1e-9;
    }
}
