namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// Writes rows into one of the program's editable database tables and applies them. ETABS 23's API has no call for
/// some definitions (a user coefficient seismic load, ASCE 7-16 wind, a user response spectrum, a named mass source:
/// read from its ETABSv1.dll and its "Table and Field Keys.xml", 0.4.17 field test), and its tables take them.
/// </summary>
public static class EditableTables
{
    private static long _refusals;
    private static string _lastRefused = "";

    /// <summary>How many table imports the program has refused so far (the field test compares it before and after a step).</summary>
    public static long Refusals => Interlocked.Read(ref _refusals);

    /// <summary>The last refused import, whole: the table, its fields, every row sent and the uncut import log.</summary>
    public static string LastRefused { get { lock (_gate) return _lastRefused; } }

    private static readonly object _gate = new();

    /// <summary>
    /// Keeps a refused import whole for the field test's report (Ahmed, 2026-11-26: one run must bring everything
    /// needed to fix it, with no experiment on the PC afterwards).
    /// </summary>
    internal static void Refused(string answer, string log, IEnumerable<(string Key, string[] Fields, IReadOnlyList<string[]> Rows)> tables)
    {
        var text = new System.Text.StringBuilder();
        text.Append(answer).Append('.');
        foreach (var (key, fields, rows) in tables)
        {
            text.Append($" Table '{key}', fields {string.Join(" | ", fields)}; {rows.Count} rows sent:");
            foreach (var r in rows) text.Append(" [").Append(string.Join(" | ", r)).Append(']');
            text.Append('.');
        }
        text.Append(" Whole import log: ").Append((log ?? "").Replace("\r", "").Replace("\n", " / ").Trim());
        lock (_gate) _lastRefused = text.ToString();
        Interlocked.Increment(ref _refusals);
    }

    /// <summary>Rows of a flat array, <paramref name="width"/> values each.</summary>
    private static List<string[]> Chunk(string[] all, int width) => width <= 0 ? [] : all.Chunk(width).Select(c => c.ToArray()).ToList();

    /// <summary>
    /// True when every error of an import log is a field the program left at its default while it read the record
    /// ("Error reading field TL; field ignored and default values will be used", ETABS 23 KDS table, 0.4.55 and main
    /// 3cdc64d runs): the rows were taken. A record skipped is never this.
    /// </summary>
    public static bool OnlyFieldsIgnored(string log, int errors)
    {
        log ??= "";
        if (log.Contains("record skipped", StringComparison.OrdinalIgnoreCase) || log.Contains("fatal", StringComparison.OrdinalIgnoreCase)) return false;
        var ignored = System.Text.RegularExpressions.Regex.Matches(log, "field ignored", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        return errors > 0 && ignored >= errors;
    }

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

    /// <summary>Renames each given field that is not one of <paramref name="keys"/> but is one of the names the program shows
    /// (<paramref name="names"/>, matched without case, spaces or punctuation) to that field's key.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ByKey(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string[] keys, string[] names)
    {
        static string Plain(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var known = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>();
        for (var i = 0; i < Math.Min(keys.Length, names.Length); i++) if (names[i] is { Length: > 0 } nm) byName.TryAdd(Plain(nm), keys[i]);
        return rows.Select(r => (IReadOnlyDictionary<string, string>)r.ToDictionary(kv => known.Contains(kv.Key) ? kv.Key : byName.TryGetValue(Plain(kv.Key), out var k) ? k : kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)).ToList();
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

            // A table ETABS edits by its objects' unique names (its editing read has UniqueName, no Story and Label: Concrete
            // Slab Design Overwrites, Frame Floor Meshing Option, Composite Column Design Overwrites) takes a row given by
            // story and label under the object's unique name. Sent with Story and Label as extra columns, every row went
            // in twice, the program's own row and one with no unique name, which it skipped ("Frame object is not
            // defined; record skipped", 0.4.73 saved-4 and Ex002, "12 of 24" read).
            var byLabel = keyField.Split('|') is var kf && kf.Length == 2 && kf.Contains("Story", StringComparer.OrdinalIgnoreCase) && kf.Contains("Label", StringComparer.OrdinalIgnoreCase);
            if (byLabel && fields.Contains("UniqueName", StringComparer.OrdinalIgnoreCase)
                && !fields.Contains("Label", StringComparer.OrdinalIgnoreCase) && !fields.Contains("Story", StringComparer.OrdinalIgnoreCase))
            {
                rows = rows.Select(r =>
                {
                    string Get(string k) => r.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value ?? "";
                    var unique = Get("UniqueName").Trim();
                    if (unique.Length == 0) unique = (string?)UniqueNameOf(m, key, Get("Label"), Get("Story")) ?? "";
                    if (unique.Length == 0) return r;
                    var o = r.Where(kv => !kv.Key.Equals("Story", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals("Label", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals("UniqueName", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    o["UniqueName"] = unique;
                    return (IReadOnlyDictionary<string, string>)o;
                }).ToList();
                if (rows.All(r => r.ContainsKey("UniqueName"))) keyField = "UniqueName";
            }

            // The rows as read, to put back when the apply is refused and takes rows with it (SAP2000 26, 0.4.22 field test).
            var readFields = fields.ToArray();
            var readRows = Enumerable.Range(0, records).Select(r => data.Skip(r * readFields.Length).Take(readFields.Length).Select(v => v ?? "").ToArray())
                .Where(r => r.Length == readFields.Length).ToList();

            // A row given by the names the program shows ("Trans NL Stiffness", as Display > Show Tables heads its columns)
            // goes in under the program's field keys: ETABS 23.2's link, spring and panel zone forms are kept that way.
            if (rows.SelectMany(r => r.Keys).Any(k => !fields.Contains(k, StringComparer.OrdinalIgnoreCase)))
            {
                int n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                m.DatabaseTables.GetAllFieldsInTable(key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
                rows = ByKey(rows, keys ?? [], names ?? []);
                keyField = string.Join("|", keyField.Split('|').Select(k => ByKey([new Dictionary<string, string> { [k] = "" }], keys ?? [], names ?? [])[0].Keys.First()));
            }

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

            // A key of several fields ("Story|Label", an object's assignment rows) matches when all of them do.
            // No key field (a form's one-row table, design preferences): the given row is merged into the row there.
            var keyFields = keyField.Length == 0 ? [] : keyField.Split('|');
            var ats = keyFields.Select(k => Array.FindIndex(fields, f => string.Equals(f, k, StringComparison.OrdinalIgnoreCase))).ToArray();
            var at = ats.All(i => i >= 0) ? 0 : -1;
            string KeyOfRow(string[] row) => string.Join("\u0001", ats.Select(i => row[i]));
            string KeyOfGiven(IReadOnlyDictionary<string, string> r) => string.Join("\u0001", keyFields.Select(k => r.TryGetValue(k, out var v) ? v : ""));
            var names2 = rows.Select(KeyOfGiven).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string[]>();
            var before = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            for (var r = 0; r < records; r++)
            {
                var row = data.Skip(r * fields.Length).Take(fields.Length).Select(v => v ?? "").ToArray();
                if (row.Length != fields.Length) continue;
                if (at >= 0 && names2.Contains(KeyOfRow(row))) before.TryAdd(KeyOfRow(row), row);
                else kept.Add(row);
            }
            var replaced = new List<string[]>();
            foreach (var given in rows)
            {
                // A row already there for this name keeps the fields not given (its GUID, its self weight).
                var row = before.Remove(KeyOfGiven(given), out var old) ? old.ToArray() : Enumerable.Repeat("", fields.Length).ToArray();
                if (old is not null) replaced.Add(old);
                foreach (var (field, value) in given)
                {
                    var i = Array.FindIndex(fields, f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) throw new EtabsException($"{program}'s table '{key}' has no field '{field}'. Its fields: {string.Join(", ", fields)}.");
                    row[i] = value;
                }
                kept.Add(row);
            }

            // Fields the table shows but does not import (ETABS 23's KDS table: TL, Fa, Fv, SDS, SD1, worked out by ETABS)
            // are left out: sent back, each is an error ("Error reading field TL; field ignored"), and the import was taken
            // as refused although its record was read (0.4.55 ETABS run).
            {
                int n = 0, v2 = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                try { m.DatabaseTables.GetAllFieldsInTable(key, ref v2, ref n, ref keys, ref names, ref about, ref units, ref importable); } catch { keys = []; }
                keys ??= []; importable ??= [];
                var shown = keys.Select((k, i) => (k, ok: i < importable.Length && importable[i])).ToList();
                var drop = importable.Any(x => x) ? Enumerable.Range(0, fields.Length).Where(i => !keyFields.Contains(fields[i], StringComparer.OrdinalIgnoreCase)
                    && shown.Any(s => s.k.Equals(fields[i], StringComparison.OrdinalIgnoreCase) && !s.ok)).ToHashSet() : [];
                // A field blank in every row is left out too: ETABS 23 skipped the Modeler's joint mass row for its blank
                // MMI X ("Field name: MMI X ... Error reading field; record skipped", 0.4.57 full test). Blank means the
                // program's default, which leaving the field out gives as well.
                // ETABS 23 refuses a joint added-mass row whose mass moments of inertia (MMIX, MMIY, MMIZ) are blank, and
                // also when they are left out ("Field name: MMI X ... Error reading field; record skipped", 0.4.57, 0.4.59
                // and 0.4.60 full tests): a blank one is sent as 0, ETABS's own value for no rotational mass.
                if (e.Program.Key == CsiProgram.Etabs.Key)
                    foreach (var i in Enumerable.Range(0, fields.Length).Where(i => System.Text.RegularExpressions.Regex.IsMatch(fields[i], "^MMI ?[XYZ]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                        foreach (var r in kept) if (i < r.Length && r[i].Trim().Length == 0) r[i] = "0";
                if (e.Program.Key == CsiProgram.Etabs.Key)
                foreach (var i in Enumerable.Range(0, fields.Length).Where(i => !keyFields.Contains(fields[i], StringComparer.OrdinalIgnoreCase)
                             && !fields[i].Equals("UniqueName", StringComparison.OrdinalIgnoreCase) && kept.Count > 0 && kept.All(r => i < r.Length && r[i].Trim().Length == 0)
                             // ...unless a row there has a value in it that the given rows make blank: left out, ETABS would
                             // keep its value (a slab design overwrite's blank rebar material came back DefaultRebar_500, 0.4.73).
                             && replaced.All(r => i >= r.Length || r[i].Trim().Length == 0)))
                    drop.Add(i);
                if (drop.Count > 0 && drop.Count < fields.Length)
                {
                    var keep = Enumerable.Range(0, fields.Length).Where(i => !drop.Contains(i)).ToArray();
                    kept = kept.Select(r => keep.Select(i => r[i]).ToArray()).ToList();
                    fields = keep.Select(i => fields[i]).ToArray();
                }
            }
            var all = kept.SelectMany(r => r).ToArray();
            var f2 = fields.ToArray();
            try
            {
                try { m.DatabaseTables.CancelTableEditing(); } catch { /* nothing staged */ } // a table left staged by an earlier call would be applied instead (SAP2000, 2026-11-26)
                if (Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray(key, ref version, ref f2, kept.Count, ref all)) != 0)
                    throw new EtabsException($"{program} did not take the rows for '{key}', so it cannot {what}.");
                int fatal = 0, errors = 0, warnings = 0, info = 0; string log = "";
                var units = PresentUnits(m);
                int applied = Convert.ToInt32(m.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref info, ref log));
                KeepUnits(m, units);
                log ??= "";
                if (applied != 0 || fatal > 0 || (errors > 0 && !OnlyFieldsIgnored(log, errors)))
                {
                    Refused($"ApplyEditedTables answered {applied} ({fatal} fatal, {errors} errors, {warnings} warnings)", log, [(key, fields, kept)]);
                    try { m.DatabaseTables.CancelTableEditing(); } catch { /* nothing more to undo */ }
                    string restored = Tools.CsiTools.RestoreRows(m, key, readFields, readRows);
                    throw new EtabsException($"{program} did not apply '{key}' ({fatal} fatal, {errors} errors), so it cannot {what}. {restored} Its import log: {LogCut(log)}" + About(m, key, rows));
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

    /// <summary>The unique name of the object a table row names by label and story: an area for a slab, wall or shell
    /// table, a point for a joint or point table, else a frame first; null when the program knows none.</summary>
    internal static string? UniqueNameOf(dynamic m, string table, string label, string story)
    {
        if (label.Trim().Length == 0) return null;
        var t = table.ToLowerInvariant();
        string[] order = t.Contains("joint") || t.Contains("point") ? ["point", "frame", "area"]
            : t.Contains("slab") || t.Contains("area") || t.Contains("shell") || t.Contains("wall") ? ["area", "frame", "point"]
            : ["frame", "area", "point"];
        foreach (var kind in order)
        {
            try
            {
                string name = "";
                dynamic obj = kind == "point" ? m.PointObj : kind == "area" ? m.AreaObj : m.FrameObj;
                if (Convert.ToInt32(obj.GetNameFromLabel(label.Trim(), story.Trim(), ref name)) == 0 && !string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception) { /* no such object of this kind, or no such call: try the next kind */ }
        }
        return null;
    }

    /// <summary>
    /// Makes the present units the model's own (its database units) after a file is opened: ETABS 23 kept its start-up
    /// lb, in, F as the present units after opening the Modeler's kN, m .e2k (0.4.57 and 0.4.59 full tests,
    /// get_model_context), and every tool after reads and writes in the present units.
    /// </summary>
    internal static void PresentAsDatabase(dynamic m)
    {
        try
        {
            object database = m.GetDatabaseUnits();
            if (Convert.ToInt32(database) <= 0) return;
            object now = m.GetPresentUnits();
            if (Convert.ToInt32(now) != Convert.ToInt32(database)) m.SetPresentUnits(database);
        }
        catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { /* a program without the calls keeps its units */ }
    }

    /// <summary>The program's present units (its eUnits value), or null when it does not say.</summary>
    internal static object? PresentUnits(dynamic m)
    {
        try { return m.GetPresentUnits(); } catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>
    /// Puts the present units back as they were before a table import, when the import changed them: ETABS 23 showed
    /// lb, in, F right after the Modeler's tables went into a kN, m model (0.4.57 full test, get_model_context), and every
    /// tool after reads and writes in the present units.
    /// </summary>
    internal static void KeepUnits(dynamic m, object? before)
    {
        if (before is null) return;
        try
        {
            object now = m.GetPresentUnits();
            if (!Equals(Convert.ToInt32(now), Convert.ToInt32(before))) m.SetPresentUnits(before);
        }
        catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { /* best effort */ }
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
            // Tables left staged by an earlier call go into the next import: SAP2000 keeps them through File > Open, then
            // refuses the new rows and applies the old ones (experiment E, 2026-11-26: it tried to delete every other pattern).
            var trace = new List<string>();
            try { trace.Add("cancel " + Convert.ToInt32(m.DatabaseTables.CancelTableEditing())); } catch (Exception ex) { trace.Add("cancel threw " + ex.GetType().Name); }
            trace.Add("locked at start " + Locked(m));
            // A locked model (it holds analysis results) refuses every table: answer 1, no log (0.4.51, experiment G).
            // Unlocked for the change, as edit_table does; the results are deleted, and the answer says so.
            if (Convert.ToBoolean(m.GetModelIsLocked()) && Convert.ToInt32(m.SetModelIsLocked(false)) != 0)
                throw new EtabsException($"{program}'s model is locked (it holds analysis results) and did not unlock, so it cannot {what}.");
            Dictionary<string, string> before = Codes(m);
            // Each other pattern's code that the API can set, as its Get call gives it now, to give back after the import.
            var held = new Dictionary<string, Tools.SapLateral.Held>(StringComparer.OrdinalIgnoreCase);
            foreach (var (other, had) in before)
            {
                if (other.Equals(pattern, StringComparison.OrdinalIgnoreCase) || had.Length == 0) continue;
                string seismic = "";
                try { if (Convert.ToInt32(m.LoadPatterns.GetAutoSeismicCode(other, ref seismic)) != 0) seismic = ""; } catch { seismic = ""; }
                try
                {
                    if (Tools.SapLateral.Hold(e, other, (seismic ?? "").Length > 0 ? CsiProMax.Lateral.LateralCodes.Seismic : CsiProMax.Lateral.LateralCodes.Wind, had) is { } h) held[other] = h;
                }
                catch (Exception ex) when (ex is EtabsException or Tools.ToolInputException) { /* not held: a change to it is said after the import */ }
            }
            // The pattern's own code is taken off first: given KDS 41 17 00-2019 straight after KBC 2016, SAP2000 26 took
            // the rows and then deleted "1 KBC 2016 auto seismic loads", leaving the pattern with None; after SetNone it
            // takes KDS (experiment M on Ahmed's PC, 2026-11-26; the last code of the 0.4.56 field test that failed).
            var mineNow = before.TryGetValue(pattern, out var c0) && c0.Length > 0 ? c0 : TableCode(m, pattern) ?? "";
            if (mineNow.Length > 0 && !mineNow.Equals("None", StringComparison.OrdinalIgnoreCase) && !SameCode(mineNow, code))
            {
                try
                {
                    if (designType.StartsWith("Wind", StringComparison.OrdinalIgnoreCase)) m.LoadPatterns.AutoWind.SetNone(pattern);
                    else m.LoadPatterns.AutoSeismic.SetNone(pattern);
                    trace.Add($"took {mineNow} off {pattern} first");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { trace.Add($"taking {mineNow} off first failed: {ex.GetType().Name}"); }
            }
            double self = 0;
            try { m.LoadPatterns.GetSelfWTMultiplier(pattern, ref self); } catch { /* a new pattern weighs nothing */ }

            // The patterns' table as SAP2000 lists it (no display selection: what it lists is what it keeps).
            int version = 0, records = 0; string[] fields = [], data = [];
            ShowEveryPattern(m);
            var read = Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray("Load Pattern Definitions", "", ref version, ref fields, ref records, ref data));
            fields ??= []; data ??= [];
            trace.Add($"read patterns {read}: v{version}, {records} rows, {data.Length} values, fields {string.Join("|", fields)}");
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
                trace.Add("locked before set " + Locked(m));
                var setAnswer = Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray("Load Pattern Definitions", ref version, ref pf, rows.Count, ref all));
                trace.Add($"set patterns {setAnswer} (v{version}, {rows.Count} rows: {Cut(string.Join("; ", rows.Select(r => string.Join(",", r))), 300)})");
                if (setAnswer != 0)
                    throw new EtabsException($"{program} did not take the patterns' rows, so it cannot {what}.");
                foreach (var t in tables)
                {
                    // The table's own version, as SAP2000 gave it (0.4.51: sent as 0, the import never ran: answer 1, no log).
                    int tv = Math.Max(1, Math.Max(cv, sv)); var tf = t.Fields.ToArray(); var td = t.Data.ToArray();
                    var codeAnswer = Convert.ToInt32(m.DatabaseTables.SetTableForEditingArray(t.Key, ref tv, ref tf, t.Count, ref td));
                    trace.Add($"set '{t.Key}' {codeAnswer} (v{tv}, {t.Count} rows, fields {string.Join("|", tf)})");
                    if (codeAnswer != 0)
                        throw new EtabsException($"{program} did not take the rows for '{t.Key}', so it cannot {what}.");
                }
                int fatal = 0, errors = 0, warnings = 0, info = 0; string log = "";
                int applied = Convert.ToInt32(m.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref info, ref log));
                log ??= "";
                Dictionary<string, string> after = Codes(m);
                if (applied != 0 || fatal + errors > 0)
                    Refused($"ApplyEditedTables answered {applied} ({fatal} fatal, {errors} errors, {warnings} warnings)", log,
                        new[] { ("Load Pattern Definitions", pf, (IReadOnlyList<string[]>)Chunk(all, pf.Length)) }.Concat(tables.Select(t => (t.Key, t.Fields.ToArray(), (IReadOnlyList<string[]>)Chunk(t.Data.ToArray(), t.Fields.Length)))));
                if (applied != 0 && fatal == 0 && errors == 0 && log.Trim().Length == 0)
                    throw new EtabsException($"{program} did not run the import of {pattern}'s {code} (it answered {applied}, with no log), so it cannot {what}. {NoImportAfterText} Patterns after: {after.Count} of {before.Count}; " +
                        $"locked now: {Locked(m)}; the windows of {program}: {OpenWindows(e.Program)}; thread {Thread.CurrentThread.GetApartmentState()}; steps: {string.Join("; ", trace)}.");
                var changed = before.Where(kv => kv.Key != pattern && (!after.TryGetValue(kv.Key, out var now) || !SameCode(now, kv.Value, empty: true))).Select(kv => kv.Key).ToList();
                // A pattern whose code was given by the API and that the import took off (field test with 0.4.56's fix:
                // ASCE 7-22 on CPM-AUTO-S took CPM-ELFY's ASCE 7-16) gets it back by its own Set call, with the values
                // its Get call gave before the import.
                var given2 = new List<string>();
                foreach (var lost in changed.Where(p => after.ContainsKey(p) && held.ContainsKey(p)).ToList())
                    try { Tools.SapLateral.Reapply(e, held[lost]); given2.Add(lost); } catch (EtabsException) { /* said below */ }
                if (given2.Count > 0)
                {
                    after = Codes(m);
                    changed = before.Where(kv => kv.Key != pattern && (!after.TryGetValue(kv.Key, out var now) || !SameCode(now, kv.Value, empty: true))).Select(kv => kv.Key).ToList();
                }
                if (changed.Count > 0)
                    throw new EtabsException($"{program} changed or lost the load patterns {string.Join(", ", changed)} when it was given {pattern}'s {code}: do not save the model; close it without saving and open it again. " +
                        $"The import answered {applied} ({fatal} fatal, {errors} errors, {warnings} warnings); patterns before {before.Count}, after {after.Count} ({Cut(string.Join(", ", after.Select(kv => kv.Value.Length == 0 ? kv.Key : $"{kv.Key}={kv.Value}")), 400)}); " +
                        $"sent: the patterns' table ({rows.Count} rows: {Cut(string.Join(", ", rows.Select(r => r[auto].Length == 0 ? r[pat] : $"{r[pat]}={r[auto]}")), 400)}) and {string.Join(", ", tables.Select(t => $"'{t.Key}' ({t.Count})"))}. Its import log: {LogCut(log)}");
                if (applied != 0 || fatal > 0)
                    throw new EtabsException($"{program} did not apply {pattern}'s {code} ({fatal} fatal, {errors} errors), so it cannot {what}. Its import log: {LogCut(log)}" + About(m, codeKey, new List<IReadOnlyDictionary<string, string>> { given }));
                // GetAutoSeismicCode / GetAutoWindCode answer "" for a code the API has no name for (IS 1893:2016, KDS,
                // IRC 6-2017, Live Load: field test with 0.4.56's fix): then the patterns' table says which code it has.
                if (!after.TryGetValue(pattern, out string? took) || !SameCode(took, code))
                    if (string.IsNullOrEmpty(took) && TableCode(m, pattern) is { } listed) took = listed;
                // A "Wind - Live Load" pattern has no AutoLoad name at all, by the API or its table: its row in the code's
                // own table is what says it took the code (experiment M, 2026-11-26: "Auto Wind - Live Load" kept the row).
                if (!SameCode(took, code) && (string.IsNullOrEmpty(took) || took.Equals("None", StringComparison.OrdinalIgnoreCase)) && HasRow(m, codeKey, pattern)) took = code;
                if (!SameCode(took, code))
                    throw new EtabsException($"{program} took the rows but {pattern}'s code is '{took}', not {code}, so it cannot {what}. Its import log: {LogCut(log)}");
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
    /// Why SAP2000 26 answers 1 to ApplyEditedTables with no log and loses every load pattern: its process has opened an
    /// .s2k (File > Open or File > Import of a text file) since it started. Then it refuses every table import, even after
    /// the .sdb is opened again, and a new SAP2000 opening the same .sdb takes the same rows (experiment N on Ahmed's PC,
    /// 2026-11-26: a tiny two-pattern .s2k was enough; the 20 table codes of the 0.4.54 field test).
    /// </summary>
    public const string NoImportAfterText = "SAP2000 26 does this in a session that has opened an .s2k text file (File > Open or File > Import): it then refuses every table import until it is restarted. " +
        "Save the model as an .sdb, close SAP2000, open the .sdb in a new SAP2000 and try again.";

    /// <summary>True when the table <paramref name="key"/> has a row whose LoadPat is <paramref name="pattern"/>.</summary>
    private static bool HasRow(dynamic m, string key, string pattern)
    {
        try
        {
            int version = 0, records = 0; string[] fields = [], data = [];
            ShowEveryPattern(m);
            if (Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray(key, "", ref version, ref fields, ref records, ref data)) != 0) return false;
            fields ??= []; data ??= [];
            int pat = Array.FindIndex(fields, f => f.Equals("LoadPat", StringComparison.OrdinalIgnoreCase));
            return pat >= 0 && Enumerable.Range(0, records).Any(r => string.Equals(data.ElementAtOrDefault(r * fields.Length + pat), pattern, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>A pattern's AutoLoad as "Load Pattern Definitions" lists it; null when it is not listed or the table is not read.</summary>
    private static string? TableCode(dynamic m, string pattern)
    {
        try
        {
            int version = 0, records = 0; string[] fields = [], data = [];
            ShowEveryPattern(m);
            if (Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray("Load Pattern Definitions", "", ref version, ref fields, ref records, ref data)) != 0) return null;
            fields ??= []; data ??= [];
            int pat = Array.FindIndex(fields, f => f.Equals("LoadPat", StringComparison.OrdinalIgnoreCase)), auto = Array.FindIndex(fields, f => f.Equals("AutoLoad", StringComparison.OrdinalIgnoreCase));
            if (pat < 0 || auto < 0) return null;
            for (var r = 0; r < records; r++)
                if (string.Equals(data.ElementAtOrDefault(r * fields.Length + pat), pattern, StringComparison.OrdinalIgnoreCase))
                    return data.ElementAtOrDefault(r * fields.Length + auto) ?? "";
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Every load pattern with its auto lateral code ("" for none), by the API.</summary>
    private static Dictionary<string, string> Codes(dynamic m)
    {
        int n = 0; string[] names = [];
        if (Convert.ToInt32(m.LoadPatterns.GetNameList(ref n, ref names)) != 0) throw new EtabsException("The load patterns could not be listed (GetNameList refused).");
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

    private static string Locked(dynamic m)
    {
        try { return Convert.ToBoolean(m.GetModelIsLocked()) ? "yes" : "no"; } catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
    }

    /// <summary>The visible windows of the program's processes, by title: a dialog left open there refuses an import.</summary>
    private static string OpenWindows(CsiProgram program)
    {
        try
        {
            var titles = System.Diagnostics.Process.GetProcessesByName(program.ProcessName).SelectMany(p => ScratchDialogs.Of(p.Id).Select(w => $"{p.Id}: {w.Title}")).ToList();
            return titles.Count == 0 ? "none seen" : string.Join(" | ", titles);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "not read (" + ex.GetType().Name + ")"; }
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

    internal static string LogCut(string text, int most = 600)
    {
        // An import log opens with many lines of options: its error and warning lines come first, so the reason is
        // never cut off (ETABS 20's logs ended at the options, 0.4.53 field test).
        var trouble = Trouble(text);
        if (trouble.Length > 0) text = trouble + " || " + text;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        most = trouble.Length > 0 ? Math.Max(most, trouble.Length + 200) : most;
        return text.Length > most ? text[..most] + "..." : text;
    }

    /// <summary>The lines of an import log that say what went wrong (error, warning, not found, invalid...), joined; empty when none does.</summary>
    public static string Trouble(string log)
    {
        string[] words = ["error", "warning", "not found", "invalid", "not valid", "not allowed", "does not exist", "cannot", "could not", "unknown", "failed", "must be", "out of range"];
        var all = log.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        // ETABS and SAP2000 put "*** Error Message ***" on a line of its own and say what is wrong on the lines after it
        // (0.4.55 ETABS run: the reason was cut off), so each such header goes with the lines that follow it.
        var detailed = new List<string>();
        for (var i = 0; i < all.Count; i++)
            if (System.Text.RegularExpressions.Regex.IsMatch(all[i], @"^\*+\s*(error|warning)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                detailed.Add(string.Join(" ", all.Skip(i + 1).TakeWhile(l => !l.StartsWith('*')).Take(4)));
        var lines = detailed.Where(l => l.Length > 0).Concat(all
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\*+\s*(error|warning)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(l => words.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)) && !l.Contains("Elements Deleted", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Import Type", StringComparison.OrdinalIgnoreCase))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(Number of )?(errors?|warnings?)\s*[:=]?\s*0\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            ).Distinct().Take(12);
        return string.Join(" | ", lines);
    }
}
