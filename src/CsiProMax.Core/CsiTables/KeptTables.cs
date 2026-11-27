using CsiProMax.Model;

namespace CsiProMax.CsiTables;

/// <summary>
/// The tables of a SAP2000 or SAFE file the Modeler keeps as they were (<see cref="StructuralModel.Kept"/>): read in
/// when the file opens, written back into the same program's file after the tables CSI ProMax writes itself (a table
/// it writes is never written twice), so a file opened and saved again keeps every table it had.
/// </summary>
public static class KeptTables
{
    public static void Keep(StructuralModel m, string program, string table, IEnumerable<Dictionary<string, string>> rows)
    {
        var list = rows.Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
        if (list.Count == 0) return;
        m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
        m.Kept[program + "|" + table.ToUpperInvariant()] = list;
    }

    public static IEnumerable<(string Table, List<Dictionary<string, string>> Rows)> Of(StructuralModel m, string program) =>
        (m.Kept ?? []).Where(k => k.Key.StartsWith(program + "|", StringComparison.OrdinalIgnoreCase)).Select(k => (k.Key[(program.Length + 1)..], k.Value));

    /// <summary>Adds the program's kept tables that <paramref name="tables"/> (table text) does not have yet; answers
    /// the tables added (their rows name objects as the file did, not as CSI ProMax's writer does).</summary>
    public static List<string> WriteInto(StructuralModel m, string program, List<string> tables)
    {
        var have = tables.Select(t => t.StartsWith("TABLE:", StringComparison.Ordinal) && t.IndexOf('"') is var a && a > 0 && t.IndexOf('"', a + 1) is var b && b > a ? t[(a + 1)..b] : "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var (table, rows) in Of(m, program))
            if (!have.Contains(table))
            {
                tables.Add(CsiTableText.Table(table, rows.Select(r => (IReadOnlyList<(string, string)>)r.Select(kv => (kv.Key, kv.Value)).ToList())));
                added.Add(table);
            }
        return added;
    }
}
