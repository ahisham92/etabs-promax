namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// Writes rows into one of the program's editable database tables and applies them. ETABS 23's API has no call for
/// some definitions (a user coefficient seismic load, ASCE 7-16 wind, a user response spectrum, a named mass source:
/// read from its ETABSv1.dll and its "Table and Field Keys.xml", 0.4.17 field test), and its tables take them.
/// </summary>
public static class EditableTables
{
    /// <summary>
    /// Selects every load pattern for display before a table is read. SAP2000 26 shows only the patterns selected for
    /// display in its database tables (DEAD alone in a new model, none in a model imported from .s2k), so a pattern added
    /// through the API was not in "Load Pattern Definitions": edit_table found no rows (0.4.18 and 0.4.20 field tests;
    /// tried on a scratch SAP2000 26: 1 row before, 2 after selecting both patterns). Writing such a table back could
    /// leave the hidden patterns out. Best effort: a program without the call reads as before.
    /// </summary>
    public static void ShowEveryPattern(dynamic m)
    {
        try
        {
            int n = 0; string[] names = [];
            if (Convert.ToInt32(m.LoadPatterns.GetNameList(ref n, ref names)) != 0 || names is not { Length: > 0 }) return;
            m.DatabaseTables.SetLoadPatternsSelectedForDisplay(ref names);
        }
        catch (Exception) { /* the table is read as the program shows it */ }
    }

    /// <summary>
    /// Puts <paramref name="rows"/> into the table <paramref name="key"/>: a row already there with the same <paramref name="keyField"/>
    /// takes the given fields and keeps its others; the table's other rows are kept. Throws with the program's import log when it does not take them.
    /// </summary>
    public static string Put(ICsiLink e, string key, string keyField, IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string what)
    {
        var program = e.Program.Name;
        // SAP2000 26 refuses every import of this table for a model with loads and is left with almost no load patterns,
        // and putting them back is refused too (0.4.22 and 0.4.34 field tests): it is never written here.
        if (e.Program.Key == CsiProgram.Sap2000.Key && string.Equals(key, "Load Pattern Definitions", StringComparison.OrdinalIgnoreCase))
            throw new EtabsException($"{program} does not take its 'Load Pattern Definitions' table back (every import is refused and leaves no load patterns), so it cannot {what}. Use add_load_pattern, or Define > Load Patterns in {program}.");
        return (string)e.Raw(what, m =>
        {
            int version = 0, records = 0;
            string[] fields = [], data = [];
            ShowEveryPattern(m);
            int read = Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray(key, "", ref version, ref fields, ref records, ref data));
            fields ??= []; data ??= [];
            if (fields.Length == 0)
            {
                int n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                if (Convert.ToInt32(m.DatabaseTables.GetAllFieldsInTable(key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable)) == 0) fields = keys ?? [];
                records = 0; data = [];
            }
            if (fields.Length == 0) throw new EtabsException($"{program} gave no fields for the table '{key}' (answer {read}), so it cannot {what}.");

            // The rows as read, to put back when the apply is refused and takes rows with it (SAP2000 26, 0.4.22 field test).
            var readFields = fields.ToArray();
            var readRows = Enumerable.Range(0, records).Select(r => data.Skip(r * readFields.Length).Take(readFields.Length).Select(v => v ?? "").ToArray())
                .Where(r => r.Length == readFields.Length).ToList();

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
                {
                    try { m.DatabaseTables.CancelTableEditing(); } catch { /* nothing more to undo */ }
                    string restored = Tools.CsiTools.RestoreRows(m, key, readFields, readRows);
                    throw new EtabsException($"{program} did not apply '{key}' ({fatal} fatal, {errors} errors), so it cannot {what}. {restored} Its import log: {Cut(log)}" + About(m, key, rows));
                }
                return log;
            }
            catch
            {
                try { m.DatabaseTables.CancelTableEditing(); } catch { /* nothing more to undo */ }
                throw;
            }
        })!;
    }

    /// <summary>
    /// SAP2000 26 only: gives one load pattern an auto lateral code that its API has no Set call for, by importing its
    /// row of "Load Pattern Definitions" (type, self weight, AutoLoad = the code) together with its row of the code's
    /// own table, in ONE apply. Tried on a hidden SAP2000 26 on Ahmed's PC (2026-11-26): the code's table alone is
    /// taken silently and keeps nothing, and the patterns' table without the code's rows drops a pattern whose code has
    /// no row (and with it the patterns of the 0.4.22 and 0.4.34 field tests); the two together set ASCE 7-22, KDS 41 17
    /// 00-2019 and AASHTO 2020, with every load kept. Only the one pattern's row is sent, and the patterns are counted
    /// before and after. Gives the import log.
    /// </summary>
    public static string PutAutoLoad(ICsiLink e, string pattern, string designType, string code, string codeKey, IReadOnlyDictionary<string, string> codeRow, string what)
    {
        var program = e.Program.Name;
        if (e.Program.Key != CsiProgram.Sap2000.Key) throw new InvalidOperationException("PutAutoLoad is for SAP2000's tables.");
        return (string)e.Raw(what, m =>
        {
            int n = 0; string[] before = [];
            m.LoadPatterns.GetNameList(ref n, ref before);
            before ??= [];
            double self = 0;
            try { m.LoadPatterns.GetSelfWTMultiplier(pattern, ref self); } catch { /* a new pattern weighs nothing */ }
            int version = 0, count = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
            m.DatabaseTables.GetAllFieldsInTable(codeKey, ref version, ref count, ref keys, ref names, ref about, ref units, ref importable);
            var known = (keys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var row = new Dictionary<string, string>(codeRow, StringComparer.OrdinalIgnoreCase) { ["LoadPat"] = pattern };
            if (known.Contains("Dir") && !row.ContainsKey("Dir")) row["Dir"] = "X";
            var wrong = row.Keys.Where(k => known.Count > 0 && !known.Contains(k)).ToList();
            if (wrong.Count > 0) throw new Tools.ToolInputException($"{program}'s table '{codeKey}' has no field {string.Join(", ", wrong)}. Its fields: {string.Join(", ", keys ?? [])}.");
            var codeFields = row.Keys.ToArray();
            var codeData = codeFields.Select(f => row[f]).ToArray();
            string[] patternFields = ["LoadPat", "DesignType", "SelfWtMult", "AutoLoad"];
            string[] patternData = [pattern, designType, self.ToString("R", System.Globalization.CultureInfo.InvariantCulture), code];
            int v1 = 1, v2 = version;
            try
            {
                if (Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray("Load Pattern Definitions", ref v1, ref patternFields, 1, ref patternData)) != 0
                    || Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray(codeKey, ref v2, ref codeFields, 1, ref codeData)) != 0)
                    throw new EtabsException($"{program} did not take the rows for {pattern}'s {code}, so it cannot {what}.");
                int fatal = 0, errors = 0, warnings = 0, info = 0; string log = "";
                int applied = Convert.ToInt32(m.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref info, ref log));
                log ??= "";
                int n2 = 0; string[] after = [];
                m.LoadPatterns.GetNameList(ref n2, ref after);
                var lost = before.Except(after ?? [], StringComparer.OrdinalIgnoreCase).ToList();
                if (lost.Count > 0)
                    throw new EtabsException($"{program} lost the load patterns {string.Join(", ", lost)} when it was given {pattern}'s {code}: do not save the model; close it without saving and open it again. Its import log: {Cut(log)}");
                if (applied != 0 || fatal > 0)
                    throw new EtabsException($"{program} did not apply {pattern}'s {code} ({fatal} fatal, {errors} errors), so it cannot {what}. Its import log: {Cut(log)}" + About(m, codeKey, new List<IReadOnlyDictionary<string, string>> { row }));
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
                ShowEveryPattern(m);
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
