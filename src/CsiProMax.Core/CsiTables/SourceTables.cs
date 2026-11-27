using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CsiProMax.Model;

namespace CsiProMax.CsiTables;

/// <summary>
/// A SAP2000 file opened in CSI ProMax goes back out with nothing lost (Ahmed 2026-11-27: "it should be nothing lost
/// whenever we get into etabs or out or sap or safe it should be exactly the same").
///
/// The Modeler holds a SAP2000 model in its own shape (stories, plan points), so what it writes of the model is not the
/// file it read: joints are numbered again, members get story names, and fields it has no place for are left out. So
/// when a file is opened, every table of it is kept as it was (<see cref="ModelSource.Original"/>), and so is what the
/// writer makes of the model just read (<see cref="ModelSource.Written"/>, the base). When the model is written again,
/// the writer's tables are set against the base, table by table and row by row:
/// - a table the same as the base is written as the original had it, every row and field, word for word;
/// - a table that changed is the original with only the change put in: a row taken away goes, a changed row keeps
///   the original's other fields with the writer's new values, a new row is added beside the rows of the same object.
/// The writer's names (joint "12", frame "B3_Story2") are put back to the file's own (joint "J17", frame "305") by
/// where the objects are, so a new member joins the original joints. A new object whose name the file already gives to
/// another one gets a name of its own.
/// </summary>
public static class SourceTables
{
    /// <summary>The source to keep in the model: the file's text and what the writer made of the model read from it.</summary>
    public static ModelSource Capture(string program, string original, string written) =>
        new() { Program = program, Original = Pack(TablesOnly(original)), Written = Pack(written) };

    /// <summary>
    /// The file to write: the original tables with the changes from the base to <paramref name="written"/> put in.
    /// The first line (what wrote the file) is <paramref name="written"/>'s.
    /// </summary>
    /// <param name="asFiled">Tables written as the file had them (kept tables): their names are the file's already.</param>
    public static string Merge(ModelSource source, string written, IReadOnlyCollection<string>? asFiled = null)
    {
        var filed = new HashSet<string>(asFiled ?? [], StringComparer.OrdinalIgnoreCase);
        var original = Read(Unpack(source.Original));
        var baseTables = Read(Unpack(source.Written));
        var now = Read(written);

        // Names: the writer's to the file's, by where the objects are; then the writer's own names nothing maps
        // (new objects) that the file gives to another object, made unique.
        // (Each set by where its objects are: the writer numbers joints afresh, so its "12" before and after a change
        // need not be the same joint.)
        var names = new NewNames(original);
        Translate(baseTables, Names(original, baseTables, names), filed);
        Translate(now, Names(original, now, names), filed);

        var o = original.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var b = baseTables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var n = now.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var output = new List<Table>();
        foreach (var t in original)
        {
            b.TryGetValue(t.Name, out var tb);
            n.TryGetValue(t.Name, out var tn);
            if (SameRows(tb, tn)) { output.Add(t); continue; }
            var merged = MergeTable(t, tb, tn);
            if (merged.Rows.Count > 0) output.Add(merged);
        }
        // Tables the file did not have: what the writer adds since the base, after the table it writes before them.
        Table? before = null;
        foreach (var t in now)
        {
            if (o.ContainsKey(t.Name)) { before = output.Find(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) ?? before; continue; }
            b.TryGetValue(t.Name, out var tb);
            if (SameRows(tb, t)) continue;
            var merged = MergeTable(new Table(t.Name, "", []), tb, t);
            if (merged.Rows.Count == 0) continue;
            var at = before is null ? 0 : output.IndexOf(before) + 1;
            output.Insert(at, merged);
            before = merged;
        }

        var text = new StringBuilder(written.Length + 1024);
        var first = written.Replace("\r", "", StringComparison.Ordinal).Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase)) ?? "";
        text.Append(first).Append("\r\n\r\n");
        foreach (var t in output)
        {
            text.Append(t.Heading.Length > 0 ? t.Heading : $"TABLE:  \"{t.Name.ToUpperInvariant()}\"").Append("\r\n");
            foreach (var r in t.Rows) text.Append(r.Raw ?? Line(r.Pairs)).Append("\r\n");
            text.Append("\r\n");
        }
        return text.Append("END TABLE DATA\r\n").ToString();
    }

    // ---- tables as read, rows in order with their own lines --------------------------------------------------------

    private sealed class Row(List<(string Key, string Value)> pairs, string? raw)
    {
        public List<(string Key, string Value)> Pairs { get; set; } = pairs;
        /// <summary>The row's line(s) as the file had them; null once the row is changed or new.</summary>
        public string? Raw { get; set; } = raw;
        public string? Get(string key) { foreach (var p in Pairs) if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return p.Value; return null; }
    }

    private sealed record Table(string Name, string Heading, List<Row> Rows);

    /// <summary>The text up to END TABLE DATA (what a program reads).</summary>
    private static string TablesOnly(string text)
    {
        var end = Regex.Match(text, @"^\s*END TABLE DATA", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return end.Success ? text[..(end.Index + end.Length)] : text;
    }

    private static List<Table> Read(string text)
    {
        var tables = new List<Table>();
        var byName = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
        Table? current = null;
        var pending = new StringBuilder();
        var raw = new StringBuilder();
        foreach (var lineRaw in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var trimmedEnd = lineRaw.TrimEnd();
            if (trimmedEnd.EndsWith(" _", StringComparison.Ordinal))
            {
                pending.Append(trimmedEnd[..^2]).Append(' ');
                raw.Append(lineRaw).Append("\r\n");
                continue;
            }
            raw.Append(lineRaw);
            var line = pending.Append(trimmedEnd).ToString().Trim();
            var rawText = raw.ToString();
            pending.Clear(); raw.Clear();
            if (line.Length == 0 || line.StartsWith('$')) continue;
            if (line.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[6..].Trim().Trim('"');
                if (!byName.TryGetValue(name, out current)) { byName[name] = current = new Table(name, rawText, []); tables.Add(current); }
                continue;
            }
            if (line.StartsWith("END TABLE DATA", StringComparison.OrdinalIgnoreCase)) break;
            if (current is null) continue;
            var pairs = CsiTableText.Pairs(line).Select(kv => (kv.Key, kv.Value)).ToList();
            if (pairs.Count > 0) current.Rows.Add(new Row(pairs, rawText));
        }
        return tables;
    }

    private static string Line(List<(string Key, string Value)> pairs) =>
        "   " + string.Join("   ", pairs.Select(p => CsiTableText.Key(p.Key) + "=" + CsiTableText.Value(p.Value)));

    // ---- comparing rows ------------------------------------------------------------------------------------------

    /// <summary>A value as compared: numbers to 9 significant digits (a unit conversion there and back is the same
    /// number), text as it is.</summary>
    private static string Canon(string v)
    {
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return Math.Abs(d) < 1e-9 ? "0" : d.ToString("G9", CultureInfo.InvariantCulture);
        return v.Trim();
    }

    private static string Sig(Row r) =>
        string.Join("\u0001", r.Pairs.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key.ToUpperInvariant() + "=" + Canon(p.Value)));

    private static bool SameRows(Table? a, Table? b)
    {
        var ra = a?.Rows ?? []; var rb = b?.Rows ?? [];
        if (ra.Count != rb.Count) return false;
        if (ra.Count == 0) return true;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in ra) counts[Sig(r)] = counts.GetValueOrDefault(Sig(r)) + 1;
        foreach (var r in rb)
        {
            var s = Sig(r);
            if (!counts.TryGetValue(s, out var c) || c == 0) return false;
            counts[s] = c - 1;
        }
        return true;
    }

    private static bool Agrees(Row w, Row o)
    {
        foreach (var (k, v) in w.Pairs)
            if (o.Get(k) is { } ov && Canon(ov) != Canon(v)) return false;
        return true;
    }

    /// <summary>The original table with the change from the base table to the new one put in.</summary>
    private static Table MergeTable(Table original, Table? baseTable, Table? now)
    {
        var rows = original.Rows.Select(r => new Row(r.Pairs, r.Raw)).ToList();
        var removed = new List<Row>();
        var pool = new Dictionary<string, Queue<Row>>(StringComparer.Ordinal);
        foreach (var r in baseTable?.Rows ?? [])
        {
            var s = Sig(r);
            if (!pool.TryGetValue(s, out var q)) pool[s] = q = new Queue<Row>();
            q.Enqueue(r);
        }
        var added = new List<Row>();
        foreach (var r in now?.Rows ?? [])
            if (pool.TryGetValue(Sig(r), out var q) && q.Count > 0) q.Dequeue();
            else added.Add(r);
        foreach (var q in pool.Values) removed.AddRange(q);
        // The base's rows in their order (a removed row is paired with a new row of the same object, in order).
        var order = new Dictionary<Row, int>(ReferenceEqualityComparer.Instance);
        foreach (var (r, i) in (baseTable?.Rows ?? []).Select((r, i) => (r, i))) order[r] = i;
        removed.Sort((x, y) => order[x].CompareTo(order[y]));

        static string Lead(Row r) => r.Pairs.Count == 0 ? "" : r.Pairs[0].Key.ToUpperInvariant() + "=" + r.Pairs[0].Value;
        var waiting = new Dictionary<string, Queue<Row>>(StringComparer.Ordinal);
        foreach (var r in removed) { if (!waiting.TryGetValue(Lead(r), out var q)) waiting[Lead(r)] = q = new Queue<Row>(); q.Enqueue(r); }
        var changes = new List<(Row? Old, Row? New)>();
        foreach (var a in added)
            changes.Add(waiting.TryGetValue(Lead(a), out var q) && q.Count > 0 ? (q.Dequeue(), a) : (null, a));
        foreach (var q in waiting.Values) while (q.Count > 0) changes.Add((q.Dequeue(), null));

        // Finding the original row a base row stands for: the rows with the same first value, then every field the
        // two share agreeing (or the only such row, when the base has only one too).
        var index = new Dictionary<string, Dictionary<string, List<int>>>(StringComparer.OrdinalIgnoreCase);
        List<int> With(string key, string value)
        {
            if (!index.TryGetValue(key, out var found))
            {
                var made = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (var i = 0; i < rows!.Count; i++)
                    if (rows[i].Get(key) is { } v) { if (!made.TryGetValue(v, out var l)) made[v] = l = []; l.Add(i); }
                index[key] = found = made;
            }
            return found.TryGetValue(value, out var list) ? list : [];
        }
        var baseLeads = (baseTable?.Rows ?? []).GroupBy(Lead).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var gone = new HashSet<int>();
        int Find(Row w)
        {
            if (w.Pairs.Count == 0) return -1;
            var candidates = With(w.Pairs[0].Key, w.Pairs[0].Value).Where(i => !gone.Contains(i)).ToList();
            foreach (var i in candidates) if (Agrees(w, rows[i])) return i;
            return candidates.Count == 1 && baseLeads.GetValueOrDefault(Lead(w)) == 1 ? candidates[0] : -1;
        }

        var after = new Dictionary<int, List<Row>>();
        void Insert(Row a)
        {
            var at = a.Pairs.Count == 0 ? [] : With(a.Pairs[0].Key, a.Pairs[0].Value);
            var where = at.Count > 0 ? at[^1] : rows.Count - 1;
            if (!after.TryGetValue(where, out var l)) after[where] = l = [];
            l.Add(new Row([.. a.Pairs], null));
        }
        foreach (var (old, @new) in changes)
        {
            var i = old is null ? -1 : Find(old);
            if (old is not null && i < 0)
            {
                // The base row has no row of the original (the writer made it): a changed one is added as new.
                if (@new is not null) Insert(@new);
                continue;
            }
            if (@new is null) { gone.Add(i); continue; }
            if (i < 0) { Insert(@new); continue; }
            // Changed: the original row with the fields the writer changed; a field the writer no longer writes is taken out.
            var pairs = rows[i].Pairs.ToList();
            foreach (var (k, _) in old!.Pairs)
                if (@new.Get(k) is null) pairs.RemoveAll(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
            foreach (var (k, v) in @new.Pairs)
            {
                // Only what the change touched: a field the writer gives as it gave it before stays as the original has it.
                if (old.Get(k) is { } was && Canon(was) == Canon(v)) continue;
                var at = pairs.FindIndex(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) { if (Canon(pairs[at].Value) != Canon(v)) pairs[at] = (pairs[at].Key, v); }
                else pairs.Add((k, v));
            }
            if (!pairs.SequenceEqual(rows[i].Pairs)) rows[i] = new Row(pairs, null);
        }

        var result = new List<Row>();
        if (after.TryGetValue(-1, out var first)) result.AddRange(first);
        for (var i = 0; i < rows.Count; i++)
        {
            if (!gone.Contains(i)) result.Add(rows[i]);
            if (after.TryGetValue(i, out var l)) result.AddRange(l);
        }
        return original with { Rows = result };
    }

    // ---- names ----------------------------------------------------------------------------------------------------

    private enum Kind { Joint, Frame, Area, Link }

    private static readonly Regex JointField = new(@"^Joint([IJK]|\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What kind of object a field names, in the tables of objects (a section's "Area" is a number, not a name).</summary>
    private static Kind? KindOf(string table, string field, Row row)
    {
        var t = table.ToUpperInvariant();
        if (t.Contains("PROPERTIES", StringComparison.Ordinal) || t.Contains("DEFINITIONS", StringComparison.Ordinal)) return null;
        if (field.Equals("ObjectLabel", StringComparison.OrdinalIgnoreCase) && t.StartsWith("GROUPS", StringComparison.Ordinal))
            return (row.Get("ObjectType") ?? "").ToUpperInvariant() switch { "JOINT" => Kind.Joint, "FRAME" => Kind.Frame, "AREA" => Kind.Area, "LINK" => Kind.Link, _ => null };
        if (JointField.IsMatch(field) && (t.StartsWith("JOINT", StringComparison.Ordinal) || t.StartsWith("CONNECTIVITY", StringComparison.Ordinal) || t.StartsWith("CONSTRAINT", StringComparison.Ordinal))) return Kind.Joint;
        if (field.Equals("Frame", StringComparison.OrdinalIgnoreCase) && (t.StartsWith("FRAME", StringComparison.Ordinal) || t == "CONNECTIVITY - FRAME")) return Kind.Frame;
        if (field.Equals("Area", StringComparison.OrdinalIgnoreCase) && (t.StartsWith("AREA", StringComparison.Ordinal) || t == "CONNECTIVITY - AREA")) return Kind.Area;
        if (field.Equals("Link", StringComparison.OrdinalIgnoreCase) && (t.StartsWith("LINK", StringComparison.Ordinal) || t == "CONNECTIVITY - LINK")) return Kind.Link;
        return null;
    }

    private static readonly (Kind Kind, string Table, string Field)[] Defining =
    [
        (Kind.Joint, "JOINT COORDINATES", "Joint"), (Kind.Frame, "CONNECTIVITY - FRAME", "Frame"),
        (Kind.Area, "CONNECTIVITY - AREA", "Area"), (Kind.Link, "CONNECTIVITY - LINK", "Link"),
    ];

    private static IEnumerable<string> Defined(List<Table> tables, Kind kind)
    {
        var d = Defining.First(x => x.Kind == kind);
        return tables.Find(t => t.Name.Equals(d.Table, StringComparison.OrdinalIgnoreCase))?.Rows.Select(r => r.Get(d.Field) ?? "").Where(v => v.Length > 0) ?? [];
    }

    private static List<Row> RowsOf(List<Table> tables, string name) => tables.Find(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Rows ?? [];

    /// <summary>Names for objects the file does not have, shared by the base and the new tables: the same object (by
    /// where it is) gets the same name in both, and never a name the file gives another object.</summary>
    private sealed class NewNames(List<Table> original)
    {
        private readonly Dictionary<(Kind, string), string> _given = [];
        private readonly Dictionary<Kind, HashSet<string>> _used = Enum.GetValues<Kind>().ToDictionary(k => k, k => new HashSet<string>(Defined(original, k), StringComparer.OrdinalIgnoreCase));

        public string For(Kind kind, string where, string wanted)
        {
            if (_given.TryGetValue((kind, where), out var had)) return had;
            var used = _used[kind];
            var name = wanted;
            for (var i = 1; used.Contains(name); i++) name = wanted + "~" + i.ToString(CultureInfo.InvariantCulture);
            used.Add(name);
            return _given[(kind, where)] = name;
        }
    }

    private static string? At(Row r)
    {
        string? x = r.Get("GlobalX"), y = r.Get("GlobalY"), z = r.Get("GlobalZ");
        if (x is null && (r.Get("CoordType") ?? "Cartesian").Equals("Cartesian", StringComparison.OrdinalIgnoreCase) && (r.Get("CoordSys") ?? "GLOBAL").Equals("GLOBAL", StringComparison.OrdinalIgnoreCase))
            (x, y, z) = (r.Get("XorR"), r.Get("Y"), r.Get("Z"));
        if (x is null || y is null || z is null) return null;
        static string R(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Round(d, 5).ToString("R", CultureInfo.InvariantCulture) : v;
        return R(x) + "|" + R(y) + "|" + R(z);
    }

    /// <summary>One written set's names to the file's: joints by where they are, frames and links by their joints,
    /// areas by their corners; an object the file does not have gets a name of its own.</summary>
    private static Dictionary<Kind, Dictionary<string, string>> Names(List<Table> original, List<Table> written, NewNames fresh)
    {
        var map = Enum.GetValues<Kind>().ToDictionary(k => k, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var joints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in RowsOf(original, "JOINT COORDINATES"))
            if (r.Get("Joint") is { Length: > 0 } j && At(r) is { } at) joints.TryAdd(at, j);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in RowsOf(written, "JOINT COORDINATES"))
            if (r.Get("Joint") is { Length: > 0 } j && !map[Kind.Joint].ContainsKey(j))
            {
                var at = At(r) ?? "name:" + j;
                map[Kind.Joint][j] = joints.TryGetValue(at, out var oj) && taken.Add(oj) ? oj : fresh.For(Kind.Joint, at, j);
            }

        string J(string? w) => w is null ? "" : map[Kind.Joint].GetValueOrDefault(w, w);
        void ByJoints(Kind kind, string table, string field, Func<Row, IEnumerable<string?>> ends, bool ordered)
        {
            string Key(IEnumerable<string> e) => ordered ? string.Join("|", e) : string.Join("|", e.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var byEnds = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in RowsOf(original, table))
                if (r.Get(field) is { Length: > 0 } name)
                {
                    var e = ends(r).Select(x => x ?? "").ToList();
                    foreach (var k in (ordered ? [Key(e), Key(Enumerable.Reverse(e))] : new[] { Key(e) }).Distinct())
                    { if (!byEnds.TryGetValue(k, out var q)) byEnds[k] = q = new Queue<string>(); q.Enqueue(name); }
                }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in RowsOf(written, table))
                if (r.Get(field) is { Length: > 0 } name && !map[kind].ContainsKey(name))
                {
                    var key = Key(ends(r).Select(J));
                    string? o = null;
                    if (byEnds.TryGetValue(key, out var q))
                    {
                        while (q.Count > 0 && used.Contains(q.Peek())) q.Dequeue();
                        if (q.Count > 0) { o = q.Dequeue(); used.Add(o); }
                    }
                    // Two new objects on the same joints are told apart by their order.
                    var nth = seen[key] = seen.GetValueOrDefault(key) + 1;
                    map[kind][name] = o ?? fresh.For(kind, key + "#" + nth.ToString(CultureInfo.InvariantCulture), name);
                }
        }
        ByJoints(Kind.Frame, "CONNECTIVITY - FRAME", "Frame", r => [r.Get("JointI"), r.Get("JointJ")], ordered: true);
        ByJoints(Kind.Link, "CONNECTIVITY - LINK", "Link", r => [r.Get("JointI"), r.Get("JointJ")], ordered: true);
        ByJoints(Kind.Area, "CONNECTIVITY - AREA", "Area", r => r.Pairs.Where(p => Regex.IsMatch(p.Key, @"^Joint\d+$", RegexOptions.IgnoreCase) && p.Value.Length > 0).Select(p => (string?)p.Value), ordered: false);
        return map;
    }

    private static void Translate(List<Table> tables, Dictionary<Kind, Dictionary<string, string>> map, HashSet<string> filed)
    {
        foreach (var t in tables.Where(t => !filed.Contains(t.Name)))
            foreach (var r in t.Rows)
                for (var i = 0; i < r.Pairs.Count; i++)
                {
                    var (k, v) = r.Pairs[i];
                    if (KindOf(t.Name, k, r) is { } kind && map[kind].TryGetValue(v, out var to) && !to.Equals(v, StringComparison.Ordinal))
                        r.Pairs[i] = (k, to);
                }
    }

    // ---- packing --------------------------------------------------------------------------------------------------

    private static string Pack(string text)
    {
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(text));
        return Convert.ToBase64String(packed.ToArray());
    }

    private static string Unpack(string packed)
    {
        if (packed.Length == 0) return "";
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(packed)), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
