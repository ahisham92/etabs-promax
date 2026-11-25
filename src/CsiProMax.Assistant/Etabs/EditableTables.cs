namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// Writes rows into one of the program's editable database tables and applies them. ETABS 23's API has no call for
/// some definitions (a user coefficient seismic load, ASCE 7-16 wind, a user response spectrum, a named mass source:
/// read from its ETABSv1.dll and its "Table and Field Keys.xml", 0.4.17 field test), and its tables take them.
/// </summary>
public static class EditableTables
{
    /// <summary>
    /// Puts <paramref name="rows"/> into the table <paramref name="key"/>: a row already there with the same <paramref name="keyField"/>
    /// takes the given fields and keeps its others; the table's other rows are kept. Throws with the program's import log when it does not take them.
    /// </summary>
    public static string Put(ICsiLink e, string key, string keyField, IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string what)
    {
        var program = e.Program.Name;
        return (string)e.Raw(what, m =>
        {
            int version = 0, records = 0;
            string[] fields = [], data = [];
            int read = Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray(key, "", ref version, ref fields, ref records, ref data));
            fields ??= []; data ??= [];
            if (fields.Length == 0)
            {
                int n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                if (Convert.ToInt32(m.DatabaseTables.GetAllFieldsInTable(key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable)) == 0) fields = keys ?? [];
                records = 0; data = [];
            }
            if (fields.Length == 0) throw new EtabsException($"{program} gave no fields for the table '{key}' (answer {read}), so it cannot {what}.");

            // A field the editing read leaves out (ETABS 23 leaves out AutoLoad of "Load Pattern Definitions") is added as a column.
            var missing = rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(k => !fields.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
            {
                int n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                m.DatabaseTables.GetAllFieldsInTable(key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
                var known = (keys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var add = missing.Where(known.Contains).ToArray();
                if (add.Length > 0)
                {
                    var wider = new string[records * (fields.Length + add.Length)];
                    for (var r = 0; r < records; r++)
                        for (var c = 0; c < fields.Length + add.Length; c++)
                            wider[r * (fields.Length + add.Length) + c] = c < fields.Length && r * fields.Length + c < data.Length ? data[r * fields.Length + c] ?? "" : "";
                    fields = fields.Concat(add).ToArray();
                    data = wider;
                }
            }

            var at = Array.FindIndex(fields, f => string.Equals(f, keyField, StringComparison.OrdinalIgnoreCase));
            var names2 = rows.Select(r => r.TryGetValue(keyField, out var v) ? v : "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string[]>();
            var before = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            for (var r = 0; r < records; r++)
            {
                var row = data.Skip(r * fields.Length).Take(fields.Length).Select(v => v ?? "").ToArray();
                if (row.Length != fields.Length) continue;
                if (at >= 0 && names2.Contains(row[at])) before.TryAdd(row[at], row);
                else kept.Add(row);
            }
            foreach (var given in rows)
            {
                // A row already there for this name keeps the fields not given (its GUID, its self weight).
                var row = given.TryGetValue(keyField, out var own) && before.Remove(own, out var old) ? old.ToArray() : Enumerable.Repeat("", fields.Length).ToArray();
                foreach (var (field, value) in given)
                {
                    var i = Array.FindIndex(fields, f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) throw new EtabsException($"{program}'s table '{key}' has no field '{field}'. Its fields: {string.Join(", ", fields)}.");
                    row[i] = value;
                }
                kept.Add(row);
            }

            var all = kept.SelectMany(r => r).ToArray();
            var f2 = fields.ToArray();
            try
            {
                if (Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray(key, ref version, ref f2, kept.Count, ref all)) != 0)
                    throw new EtabsException($"{program} did not take the rows for '{key}', so it cannot {what}.");
                int fatal = 0, errors = 0, warnings = 0, info = 0; string log = "";
                int applied = Convert.ToInt32(m.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref info, ref log));
                log ??= "";
                if (applied != 0 || fatal + errors > 0)
                    throw new EtabsException($"{program} did not apply '{key}' ({fatal} fatal, {errors} errors), so it cannot {what}. Its import log: {Cut(log)}" + About(m, key, rows));
                return log;
            }
            catch
            {
                try { m.DatabaseTables.CancelTableEditing(); } catch { /* nothing more to undo */ }
                throw;
            }
        })!;
    }

    /// <summary>What the program says each given field holds, so a refused value can be put right from the message.</summary>
    private static string About(dynamic m, string key, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        try
        {
            int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
            m.DatabaseTables.GetAllFieldsInTable(key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
            keys ??= []; about ??= [];
            var given = rows.SelectMany(r => r.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parts = Enumerable.Range(0, Math.Min(keys.Length, about.Length)).Where(i => given.Contains(keys[i]) && !string.IsNullOrWhiteSpace(about[i]))
                .Select(i => $"{keys[i]}: {about[i].Trim()}").ToList();
            return parts.Count == 0 ? "" : " The fields: " + Cut(string.Join(" | ", parts), 1500);
        }
        catch { return ""; }
    }

    /// <summary>The first row of a table whose <paramref name="keyField"/> is <paramref name="name"/>, field by field; null when there is none.</summary>
    public static Dictionary<string, string>? Read(ICsiLink e, string key, string keyField, string name)
    {
        try
        {
            return (Dictionary<string, string>?)e.Raw($"read {name} from {key}", m =>
            {
                int version = 0, records = 0; string[] fields = [], data = [];
                if (Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray(key, "", ref version, ref fields, ref records, ref data)) != 0) return null;
                fields ??= []; data ??= [];
                var at = Array.FindIndex(fields, f => string.Equals(f, keyField, StringComparison.OrdinalIgnoreCase));
                for (var r = 0; r < records && at >= 0; r++)
                {
                    var row = data.Skip(r * fields.Length).Take(fields.Length).Select(v => v ?? "").ToArray();
                    if (row.Length == fields.Length && string.Equals(row[at], name, StringComparison.OrdinalIgnoreCase))
                        return fields.Select((f, i) => (f, row[i])).ToDictionary(x => x.f, x => x.Item2, StringComparer.OrdinalIgnoreCase);
                }
                return null;
            });
        }
        catch (EtabsException) { return null; }
    }

    public static IReadOnlyDictionary<string, string> Row(params (string Field, string Value)[] values) =>
        values.ToDictionary(v => v.Field, v => v.Value, StringComparer.OrdinalIgnoreCase);

    public static string YesNo(bool value) => value ? "Yes" : "No";

    public static string Num(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string Cut(string text, int most = 600)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > most ? text[..most] + "..." : text;
    }
}
