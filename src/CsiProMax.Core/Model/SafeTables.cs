using System.Reflection;
using CsiProMax.CsiTables;

namespace CsiProMax.Model;

/// <summary>
/// SAFE's own definition and option tables (Ahmed 2026-11-27: every SAFE form identical to SAFE's, every option working),
/// by the table and field names SAFE writes in its own .f2k (Seeds/tables-safe.txt, read from SAFE 23.1 and 20.1 exports).
/// What a SAFE form sets is a row of one of these tables in <see cref="StructuralModel.DefinitionTables"/>, under
/// <see cref="Prefix"/> + the table's name, so it never mixes with ETABS's tables of the same name (ETABS 23 and SAFE 23 share
/// most table names but not every field). Rows are kept by SAFE's field names (as the .f2k has them); the .f2k writer lays
/// them over the rows it makes from the model (<see cref="WriteInto"/>): the form's fields win, the rest stay as written,
/// and a definition the model does not have goes in as the form set it. Nothing goes to ETABS or SAP2000.
/// </summary>
public static class SafeTables
{
    /// <summary>The start of a SAFE table's name in <see cref="StructuralModel.DefinitionTables"/>.</summary>
    public const string Prefix = "SAFE | ";

    /// <summary>(field key as SAFE's API gives it, field name as SAFE's .f2k writes it), by table, in SAFE's order.</summary>
    public static readonly IReadOnlyDictionary<string, (string Key, string Label)[]> All = Load();

    /// <summary>The choices SAFE wrote for a field (from its own files), by table and field name.</summary>
    public static readonly IReadOnlyDictionary<string, Dictionary<string, string[]>> Seen = LoadSeen();

    /// <summary>
    /// The tables a SAFE form sets: definitions and options. Object tables (connectivity, assignments, per-object
    /// overwrites) belong to the objects, and summaries, lists and the discretized tendon points are SAFE's own results.
    /// </summary>
    public static IEnumerable<string> FormTables => All.Keys.Where(IsFormTable);

    private static bool IsFormTable(string t)
    {
        var u = t.ToUpperInvariant();
        string[] not = ["CONNECTIVITY", "ASSIGNMENTS", "SUMMARY", "DISCRETIZED", "ELONGATION", "MATERIAL LIST", "PROGRAM CONTROL", "DIMENSION LINE", "OBJECT GEOMETRY",
            "GRID DEFINITIONS", "OVERWRITES"];
        return !not.Any(n => u.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>True for a SAFE table name as the model keeps it (with <see cref="Prefix"/>).</summary>
    public static bool Has(string table) => Full(table) is not null;

    /// <summary>The model's name of a SAFE form table (with <see cref="Prefix"/>), from that name or null.</summary>
    public static string? Full(string table)
    {
        if (!table.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var bare = table[Prefix.Length..].Trim();
        return FormTables.FirstOrDefault(k => k.Equals(bare, StringComparison.OrdinalIgnoreCase)) is { } k ? Prefix + k : null;
    }

    /// <summary>SAFE's table name (without <see cref="Prefix"/>).</summary>
    public static string Bare(string full) => full.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? full[Prefix.Length..] : full;

    /// <summary>A table's fields as the model keeps them: (field name, field name), SAFE's .f2k names.</summary>
    public static (string Key, string Label)[] Fields(string full) => All[Bare(full)].Select(f => (f.Label, f.Label)).ToArray();

    /// <summary>The field name a row is keyed by (Name, Material...), or null for a one-row options table.</summary>
    public static string? KeyField(string full)
    {
        var first = All[Bare(full)][0];
        return first.Key is "Name" or "Material" or "UniqueName" or "Unique Name" || first.Label is "Name" or "Material" ? first.Label : null;
    }

    /// <summary>SAFE's field name for a field given by its name or its API key.</summary>
    public static string? Label(string full, string field) =>
        All[Bare(full)].FirstOrDefault(f => f.Label.Equals(field, StringComparison.OrdinalIgnoreCase)) is { Label: { } l } ? l
        : All[Bare(full)].FirstOrDefault(f => f.Key.Equals(field, StringComparison.OrdinalIgnoreCase)) is { Label: { } l2 } ? l2 : null;

    /// <summary>
    /// Sets a definition's rows (or a one-row table's row): fields by SAFE's name or API key; a blank value is SAFE's own
    /// default. No rows removes the definition.
    /// </summary>
    public static void Set(StructuralModel m, string table, string name, IReadOnlyList<Dictionary<string, string>> rows)
    {
        var full = Full(table) ?? throw new ArgumentException($"There is no SAFE table '{table}'. SAFE's tables: {string.Join(", ", FormTables.Select(t => Prefix + t))}.");
        var key = KeyField(full);
        if (key is not null && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the definition's name.");
        var clean = new List<Dictionary<string, string>>();
        foreach (var row in rows)
        {
            var c = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (f, v) in row)
            {
                var label = Label(full, f) ?? throw new ArgumentException($"SAFE's '{Bare(full)}' has no field '{f}'. Its fields: {string.Join(", ", All[Bare(full)].Select(x => x.Label))}.");
                if (v is not null && v.Trim().Length > 0) c[label] = v.Trim();
            }
            if (c.Count == 0) continue;
            if (key is not null) c[key] = name.Trim();
            clean.Add(c);
        }
        m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
        var list = m.DefinitionTables.TryGetValue(full, out var l) ? l : m.DefinitionTables[full] = [];
        if (key is null) list.Clear();
        else list.RemoveAll(r => r.GetValueOrDefault(key) == name.Trim());
        list.AddRange(key is null ? clean.Take(1) : clean);
        if (list.Count == 0) m.DefinitionTables.Remove(full);
        if (m.DefinitionTables.Count == 0) m.DefinitionTables = null;
    }

    /// <summary>The model's SAFE form tables with their rows.</summary>
    public static IEnumerable<(string Full, List<Dictionary<string, string>> Rows)> Of(StructuralModel m) =>
        (m.DefinitionTables ?? []).Where(t => Has(t.Key) && t.Value.Count > 0).Select(t => (Full(t.Key)!, t.Value));

    /// <summary>
    /// Lays the SAFE forms' rows over the .f2k's tables (table text, as <see cref="CsiTableText.Table"/> makes it): in a
    /// table the writer made, a definition's rows take the form's fields over the written ones (row by row; the written
    /// row's other fields stay); a definition the table has not, and a table the writer did not make, go in as the form
    /// set them.
    /// </summary>
    public static void WriteInto(StructuralModel m, List<string> tables)
    {
        foreach (var (full, rows) in Of(m))
        {
            var name = Bare(full).ToUpperInvariant();
            var key = KeyField(full);
            var at = tables.FindIndex(t => CsiTableText.Names(t) is [var n] && n == name);
            var written = at >= 0 ? CsiTableText.Parse(at >= 0 ? tables[at] : "").Values.FirstOrDefault() ?? [] : [];
            var result = new List<Dictionary<string, string>>();
            var names = rows.Select(r => key is null ? "" : r.GetValueOrDefault(key) ?? "").Distinct().ToList();
            string NameOf(Dictionary<string, string> r) => key is null ? "" : r.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";
            // The written rows in their order; a definition the form has rows of takes them where its first row was.
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in written)
            {
                var n = NameOf(w);
                if (!names.Contains(n)) { result.Add(w); continue; }
                if (!done.Add(n)) continue;
                var mine = rows.Where(r => key is null || r.GetValueOrDefault(key) == n).ToList();
                var theirs = written.Where(r => NameOf(r) == n).ToList();
                for (var i = 0; i < mine.Count; i++)
                {
                    var row = i < theirs.Count ? new Dictionary<string, string>(theirs[i], StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (f, v) in mine[i]) row[f] = v;
                    result.Add(row);
                }
            }
            foreach (var n in names.Where(n => !done.Contains(n)))
                result.AddRange(rows.Where(r => key is null || r.GetValueOrDefault(key) == n).Select(r => new Dictionary<string, string>(r, StringComparer.OrdinalIgnoreCase)));
            var text = CsiTableText.Table(name, result.Select(r => (IReadOnlyList<(string, string)>)Ordered(full, r)));
            if (at >= 0) tables[at] = text;
            else tables.Add(text);
        }
    }

    /// <summary>A row's fields in SAFE's order (fields SAFE's list does not name stay after, as they were).</summary>
    private static List<(string, string)> Ordered(string full, Dictionary<string, string> row)
    {
        var order = All[Bare(full)].Select(f => f.Label).ToList();
        var known = order.Where(row.ContainsKey).Select(f => (f, row[f]));
        var other = row.Where(kv => !order.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Select(kv => (kv.Key, kv.Value));
        return known.Concat(other).ToList();
    }

    private static IEnumerable<(string Table, string Fields, string Seen)> Lines()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tables-safe.txt")!;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || !line.Contains(" :: ", StringComparison.Ordinal)) continue;
            var (table, rest) = (line[..line.IndexOf(" :: ", StringComparison.Ordinal)], line[(line.IndexOf(" :: ", StringComparison.Ordinal) + 4)..]);
            var seenAt = rest.IndexOf("  ## seen in SAFE: ", StringComparison.Ordinal);
            yield return (table.Trim(), seenAt >= 0 ? rest[..seenAt] : rest, seenAt >= 0 ? rest[(seenAt + 19)..] : "");
        }
    }

    private static Dictionary<string, (string, string)[]> Load() =>
        Lines().ToDictionary(l => l.Table, l => l.Fields.Split('|').Select(p => p.Split('=', 2)).Select(p => (p[0], p.Length > 1 ? p[1] : p[0])).ToArray(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, Dictionary<string, string[]>> LoadSeen() =>
        Lines().ToDictionary(l => l.Table, l => l.Seen.Length == 0 ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            : l.Seen.Split("; ").Select(p => p.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1].Split('/'), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
