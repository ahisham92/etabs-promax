using CsiProMax.Model;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// What the Modeler's forms hold that ETABS's .e2k has no keyword for (a material's damping, notes, time dependence,
/// user stress-strain curve, ...; an object's added mass, output stations, mesh options, edge releases, ...) is kept in the .cpm beside the .e2k as rows of ETABS's own tables. When CSI ProMax
/// opens that .e2k in ETABS, those rows go in through ETABS's database tables, so nothing the forms held is lost.
/// SAP2000 and SAFE get them in their own text files (.s2k, .f2k), which are tables already.
/// </summary>
public static class ModelerTables
{
    /// <summary>The Modeler's .cpm beside a program file, or null.</summary>
    public static string? ModelerFileBeside(string programFile)
    {
        var cpm = Path.ChangeExtension(programFile, ModelFile.Extension);
        return File.Exists(cpm) ? cpm : null;
    }

    /// <summary>
    /// Puts the table rows of the Modeler's model at <paramref name="cpm"/> into the program behind <paramref name="e"/>,
    /// table by table; says what went in, and what the program refused, without stopping at the first refusal.
    /// </summary>
    public static (List<string> Done, List<string> Refused) Apply(ICsiLink e, string cpm)
    {
        var model = ModelFile.Load(cpm);
        var done = new List<string>(); var refused = new List<string>();
        foreach (var (table, rows) in ProgramTables.MaterialRows(model).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            try
            {
                EditableTables.Put(e, table, "Material", rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList(), $"put the Modeler's '{table}' in");
                done.Add($"{table}: {rows.Count} row{(rows.Count == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{table}: {ex.Message}"); }
        }
        // The objects' assignments the .e2k has no keyword for, by story and label (AssignTables). A new row names its
        // object's UniqueName too, read from the program's summary of that kind of object.
        var unique = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string> UniqueOf(string table)
        {
            var summary = table.Split(" - ")[0] + " - Summary";
            if (unique.TryGetValue(summary, out var map)) return map;
            map = new(StringComparer.Ordinal);
            try
            {
                var t = e.ReadTable(summary, null, null);
                int s = IndexOf(t, "Story"), l = IndexOf(t, "Label"), u = IndexOf(t, "UniqueName");
                if (s >= 0 && l >= 0 && u >= 0) foreach (var r in t.Rows) map.TryAdd(r[s] + "@" + r[l], r[u]);
            }
            catch (EtabsException) { }
            return unique[summary] = map;
        }
        foreach (var (table, rows) in AssignTables.Rows(model).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            try
            {
                var names = UniqueOf(table);
                foreach (var r in rows) if (names.TryGetValue(r["Story"] + "@" + r["Label"], out var u)) r["UniqueName"] = u;
                EditableTables.Put(e, table, "Story|Label", rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList(), $"put the Modeler's '{table}' in");
                done.Add($"{table}: {rows.Count} row{(rows.Count == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{table}: {ex.Message}"); }
        }
        return (done, refused);
    }

    private static int IndexOf(TableData t, string field) => t.Fields.ToList().FindIndex(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
}
