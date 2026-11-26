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
    /// SAP2000 26 only: gives one load pattern an auto lateral code that its API has no Set call for, by importing
    /// "Load Pattern Definitions" and the code's own table in ONE apply. Tried on a hidden SAP2000 26 on Ahmed's PC
    /// (2026-11-26): the code's table alone is taken silently and keeps nothing; the two together set ASCE 7-22, KDS,
    /// TSC-2018, NBCC 2020, KBC 2016, TCVN, CSA S6-14, AASHTO 2020... with every load kept, SAP2000 filling in its own
    /// defaults for the fields not given. The patterns' table deletes a pattern it lists that the rows sent leave out
    /// (only a pattern a load case uses survived), and drops a pattern whose code has no row in its code's table (the
    /// wipes of the 0.4.22 and 0.4.34 field tests). So every row it lists goes back as read, each coded one with its
    /// code's rows as read, and the patterns and their codes are compared before and after. Gives the import log.
    /// The pattern-import experiment (2026-11-26, Ahmed's PC) refined it: with every pattern selected for display the
    /// table lists them all; a blank AutoLoad keeps a pattern's code, and naming a code with no row clears it (an API
    /// wind code has no table), so every other pattern goes back blank except those on the code being given.
    /// </summary>
    public static string PutAutoLoad(ICsiLink e, string pattern, string designType, string code, string codeKey, IReadOnlyDictionary<string, string> codeRow, string what)
    {
        var program = e.Program.Name;
        if (e.Program.Key != CsiProgram.Sap2000.Key) throw new InvalidOperationException("PutAutoLoad is for SAP2000's tables.");
        var tableKeys = e.Tables().Select(t => t.Key).ToList();
        return (string)e.Raw(what, m =>
        {
            Dictionary<string, string> before = Codes(m);
            double self = 0;
            try { m.LoadPatterns.GetSelfWTMultiplier(pattern, ref self); } catch { /* a new pattern weighs nothing */ }

            // The patterns' table as SAP2000 lists it (no display selection: what it lists is what it keeps).
            int version = 0, records = 0; string[] fields = [], data = [];
            ShowEveryPattern(m);
            m.DatabaseTables.GetTableForEditingArray("Load Pattern Definitions", "", ref version, ref fields, ref records, ref data);
            fields ??= []; data ??= [];
            if (fields.Length == 0) { fields = ["LoadPat", "DesignType", "SelfWtMult", "AutoLoad"]; records = 0; data = []; }
            int F(string f) => Array.FindIndex(fields, x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
            var (pat, type, sw, auto) = (F("LoadPat"), F("DesignType") is >= 0 and var dt ? dt : F("Type"), F("SelfWtMult"), F("AutoLoad"));
            if (auto < 0 && pat >= 0)
            {
                // Not listed (no pattern has a code yet): the field goes on the end, blank for the others.
                var wide = fields.Length + 1;
                data = Enumerable.Range(0, records).SelectMany(r => data.Skip(r * fields.Length).Take(fields.Length).Append("")).ToArray();
                fields = [.. fields, "AutoLoad"]; auto = wide - 1;
            }
            if (pat < 0 || type < 0 || auto < 0) throw new EtabsException($"{program}'s 'Load Pattern Definitions' has no LoadPat, DesignType or AutoLoad field (its fields: {string.Join(", ", fields)}), so it cannot {what}.");
            var rows = Enumerable.Range(0, records).Select(r => data.Skip(r * fields.Length).Take(fields.Length).Select(v => v ?? "").ToArray()).Where(r => r.Length == fields.Length).ToList();
            var mine = rows.FindIndex(r => string.Equals(r[pat], pattern, StringComparison.OrdinalIgnoreCase));
            var row = mine >= 0 ? rows[mine] : Enumerable.Repeat("", fields.Length).ToArray();
            row[pat] = pattern; row[type] = designType; row[auto] = code;
            if (sw >= 0) row[sw] = self.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (mine >= 0) rows[mine] = row; else rows.Add(row);

            // The other patterns keep their codes by a blank AutoLoad: SAP2000 26 keeps a pattern's code when the field is
            // blank ("AutoLoad field is blank; default value is used"), and clears it when the field names a code that has
            // no row in the import, as a wind code set through the API never has (no 'Auto Wind - ASCE 7-16' table is
            // offered). Their own code tables are not sent: sending one that still holds a moved pattern's row gave a
            // second pattern the new code (the pattern-import experiment on Ahmed's PC, 2026-11-26). A pattern with the
            // code being given goes back with its row of that code's table, as read, so that table keeps it.
            var tables = new List<(string Key, string[] Fields, string[] Data, int Count)>();
            int sv = 0, sn = 0; string[] sf = [], sd = [];
            var sameCode = rows.Any(r => r != row && SameCode(r[auto], code));
            if (sameCode)
            {
                ShowEveryPattern(m);
                if (Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray(codeKey, "", ref sv, ref sf, ref sn, ref sd)) != 0) { sf = []; sd = []; sn = 0; }
                sf ??= []; sd ??= [];
                if (sf.Length > 0 && sn > 0) tables.Add((codeKey, sf, sd, sn));
            }
            var slp = Array.FindIndex(sf, x => x.Equals("LoadPat", StringComparison.OrdinalIgnoreCase));
            foreach (var other in rows.Where(r => r != row))
            {
                var hasRow = slp >= 0 && SameCode(other[auto], code) && Enumerable.Range(0, sn).Any(r => string.Equals(sd.ElementAtOrDefault(r * sf.Length + slp), other[pat], StringComparison.OrdinalIgnoreCase));
                if (!hasRow) other[auto] = "";
            }
            tables.RemoveAll(t => t.Count == 0);

            // The code's own row: the pattern, its direction, and the inputs given; SAP2000 fills in the rest.
            int cv = 0, count = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
            m.DatabaseTables.GetAllFieldsInTable(codeKey, ref cv, ref count, ref keys, ref names, ref about, ref units, ref importable);
            var known = (keys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var given = new Dictionary<string, string>(codeRow, StringComparer.OrdinalIgnoreCase) { ["LoadPat"] = pattern };
            if (known.Contains("Dir") && !given.ContainsKey("Dir")) given["Dir"] = "X";
            var wrong = given.Keys.Where(k => known.Count > 0 && !known.Contains(k)).ToList();
            if (wrong.Count > 0) throw new Tools.ToolInputException($"{program}'s table '{codeKey}' has no field {string.Join(", ", wrong)}. Its fields: {string.Join(", ", keys ?? [])}.");
            var codeFields = given.Keys.ToArray();
            var codeData = codeFields.Select(f => given[f]).ToArray();
            var at = tables.FindIndex(t => t.Key == codeKey);
            if (at >= 0)
            {
                // Other patterns with this same code keep their rows; this pattern's row is the one given.
                var t = tables[at];
                var p = Array.FindIndex(t.Fields, x => x.Equals("LoadPat", StringComparison.OrdinalIgnoreCase));
                var kept = Enumerable.Range(0, t.Count).Select(r => t.Data.Skip(r * t.Fields.Length).Take(t.Fields.Length).ToArray()).Where(r => p < 0 || !string.Equals(r[p], pattern, StringComparison.OrdinalIgnoreCase)).ToList();
                var mineRow = t.Fields.Select(f => given.TryGetValue(f, out var x) ? x : "").ToArray();
                kept.Add(mineRow);
                tables[at] = (t.Key, t.Fields, kept.SelectMany(r => r).ToArray(), kept.Count);
            }
            else tables.Add((codeKey, codeFields, codeData, 1));

            var all = rows.SelectMany(r => r).ToArray();
            var pf = fields.ToArray();
            try
            {
                if (Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray("Load Pattern Definitions", ref version, ref pf, rows.Count, ref all)) != 0)
                    throw new EtabsException($"{program} did not take the patterns' rows, so it cannot {what}.");
                foreach (var t in tables)
                {
                    int tv = 0; var tf = t.Fields.ToArray(); var td = t.Data.ToArray();
                    if (Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray(t.Key, ref tv, ref tf, t.Count, ref td)) != 0)
                        throw new EtabsException($"{program} did not take the rows for '{t.Key}', so it cannot {what}.");
                }
                int fatal = 0, errors = 0, warnings = 0, info = 0; string log = "";
                int applied = Convert.ToInt32(m.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref info, ref log));
                log ??= "";
                Dictionary<string, string> after = Codes(m);
                var changed = before.Where(kv => kv.Key != pattern && (!after.TryGetValue(kv.Key, out var now) || !SameCode(now, kv.Value, empty: true))).Select(kv => kv.Key).ToList();
                if (changed.Count > 0)
                    throw new EtabsException($"{program} changed or lost the load patterns {string.Join(", ", changed)} when it was given {pattern}'s {code}: do not save the model; close it without saving and open it again. " +
                        $"The import answered {applied} ({fatal} fatal, {errors} errors, {warnings} warnings); patterns before {before.Count}, after {after.Count} ({Cut(string.Join(", ", after.Select(kv => kv.Value.Length == 0 ? kv.Key : $"{kv.Key}={kv.Value}")), 400)}); " +
                        $"sent: the patterns' table ({rows.Count} rows: {Cut(string.Join(", ", rows.Select(r => r[auto].Length == 0 ? r[pat] : $"{r[pat]}={r[auto]}")), 400)}) and {string.Join(", ", tables.Select(t => $"'{t.Key}' ({t.Count})"))}. Its import log: {Cut(log)}");
                if (applied != 0 || fatal > 0)
                    throw new EtabsException($"{program} did not apply {pattern}'s {code} ({fatal} fatal, {errors} errors), so it cannot {what}. Its import log: {Cut(log)}" + About(m, codeKey, new List<IReadOnlyDictionary<string, string>> { given }));
                if (!after.TryGetValue(pattern, out string? took) || !SameCode(took, code))
                    throw new EtabsException($"{program} took the rows but {pattern}'s code is '{took}', not {code}, so it cannot {what}. Its import log: {Cut(log)}");
                return log;
            }
            catch
            {
                try { m.DatabaseTables.CancelTableEditing(); } catch { /* nothing more to undo */ }
                throw;
            }
        })!;
    }

    /// <summary>Every load pattern with its auto lateral code ("" for none), by the API.</summary>
    private static Dictionary<string, string> Codes(dynamic m)
    {
        int n = 0; string[] names = [];
        m.LoadPatterns.GetNameList(ref n, ref names);
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names ?? [])
        {
            string seismic = "", wind = "";
            try { if (Convert.ToInt32(m.LoadPatterns.GetAutoSeismicCode(name, ref seismic)) != 0) seismic = ""; } catch { seismic = ""; }
            try { if (Convert.ToInt32(m.LoadPatterns.GetAutoWindCode(name, ref wind)) != 0) wind = ""; } catch { wind = ""; }
            codes[name] = (seismic ?? "") + (wind ?? "");
        }
        return codes;
    }

    /// <summary>A code's name with only its letters and digits ("ASCE7-16" and "ASCE 7-16" are one code).</summary>
    internal static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// One code by two of SAP2000's names: the table's ("User Coefficient", "ASCE 7-16") and the one GetAutoSeismicCode
    /// and the AutoLoad field give ("USER COEFF", "ASCE7-16"): the same letters and digits, or one the start of the other
    /// (0.4.44 field test: CPM-ELFX's USER COEFF found no table). <paramref name="empty"/>: two empty names are one.
    /// </summary>
    /// <summary>The code in a table's key after "Auto Seismic - " or "Auto Wind - " (SAP2000 26: no "Load Pattern Definitions - " in front).</summary>
    private static string? After(string key, string marker)
    {
        var at = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : key[(at + marker.Length)..];
    }

    internal static bool SameCode(string? a, string? b, bool empty = false)
    {
        var (x, y) = (Norm(a ?? ""), Norm(b ?? ""));
        if (x.Length == 0 || y.Length == 0) return empty && x.Length == 0 && y.Length == 0;
        return x == y || x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal);
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
