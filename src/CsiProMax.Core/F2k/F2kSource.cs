using CsiProMax.CsiTables;
using CsiProMax.Model;

namespace CsiProMax.F2k;

/// <summary>
/// Makes a SAFE file come back exactly (Ahmed 2026-11-27: "nothing lost whenever we get into etabs or out or sap or
/// safe, it should be exactly the same"). When the Modeler opens a .f2k SAFE wrote, every table of it is kept under the
/// model as the file had it (the source), with the tables CSI ProMax would write for the model as it was read (the
/// base). Saving the .f2k again is then a three-way merge, table by table, row by row, field by field:
/// <list type="bullet">
/// <item>a table the engineer did not change (what CSI ProMax writes for it now is what it wrote when the file was read)
/// goes back exactly as SAFE wrote it, every row, field, GUID and order;</item>
/// <item>in a changed table, a field the engineer changed takes the new value and every other field keeps SAFE's own,
/// a new row is written as CSI ProMax writes it, a row taken away is left out, and a row CSI ProMax has no place for
/// (it never wrote it) stays as it was;</item>
/// <item>a table CSI ProMax never writes (summaries, discretized tendons, options) goes back as it was; those SAFE works
/// out from the rest (summaries, material lists, discretized tendons) only while nothing else changed, so a stale
/// summary never goes into SAFE.</item>
/// </list>
/// The source lives in the model's kept tables under "model|" names, which no program's file gets as such.
/// </summary>
public static class F2kSource
{
    private const string Order = "model|SAFE SOURCE";
    private const string SourcePrefix = "model|SAFE SOURCE|";
    private const string BasePrefix = "model|SAFE BASE|";

    /// <summary>Whether the model carries a SAFE file it was read from.</summary>
    public static bool Has(StructuralModel m) => m.Kept?.ContainsKey(Order) == true;

    /// <summary>The story the source file was SAFE's floor of.</summary>
    public static string? Story(StructuralModel m) => m.Kept?.GetValueOrDefault(Order)?.FirstOrDefault()?.GetValueOrDefault("Story");

    /// <summary>SAFE's point names by place, when the model is that file's and <paramref name="story"/> its floor; else none.</summary>
    public static Dictionary<string, string> PointNames(StructuralModel m, string story)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Has(m) || !string.Equals(Story(m), story, StringComparison.OrdinalIgnoreCase)) return names;
        static double D(Dictionary<string, string> r, string k) =>
            r.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        foreach (var r in m.Kept!.GetValueOrDefault(SourcePrefix + "POINT OBJECT CONNECTIVITY") ?? [])
            if ((r.GetValueOrDefault("UniqueName") ?? r.GetValueOrDefault("Unique Name")) is { Length: > 0 } name)
                names.TryAdd(F2kWriter.PointKey(D(r, "X"), D(r, "Y"), D(r, "Z")), name);
        return names;
    }

    /// <summary>Keeps the file's tables (in their order) and what the writer makes of the model just read, for <see cref="Merge"/>.</summary>
    public static void Remember(StructuralModel m, IReadOnlyDictionary<string, List<Dictionary<string, string>>> file, IEnumerable<string> order)
    {
        Forget(m);
        string story;
        try { story = F2kWriter.Write(m).Story; }
        catch (ArgumentException) { return; }
        m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
        var names = order.Where(file.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var list = names.Select(t => new Dictionary<string, string>(StringComparer.Ordinal) { ["Table"] = t.ToUpperInvariant() }).ToList();
        if (list.Count == 0) list.Add(new Dictionary<string, string>(StringComparer.Ordinal));
        list[0]["Story"] = story;
        m.Kept[Order] = list;
        foreach (var t in names) m.Kept[SourcePrefix + t.ToUpperInvariant()] = file[t].Select(Copy).ToList();
        // The base: what the writer makes of the model as read (with SAFE's point names), before any merge.
        F2kResult written;
        reading = true;
        try { written = F2kWriter.Write(m, new F2kOptions(Story: story)); }
        catch (ArgumentException) { Forget(m); return; }
        finally { reading = false; }
        foreach (var (t, rows) in Tables(written.Text)) m.Kept[BasePrefix + t] = rows;
    }

    [ThreadStatic] private static bool reading;

    /// <summary>Drops the source (a model that is no longer the SAFE file's, such as one made new).</summary>
    public static void Forget(StructuralModel m)
    {
        if (m.Kept is null) return;
        foreach (var k in m.Kept.Keys.Where(k => k.Equals(Order, StringComparison.OrdinalIgnoreCase) || k.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase) || k.StartsWith(BasePrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            m.Kept.Remove(k);
    }

    /// <summary>
    /// The tables to write for <paramref name="story"/>, given what the writer made of the model now (<paramref name="tables"/>,
    /// table text in order). Without a source, or for another floor than the file's, the writer's tables as they are.
    /// </summary>
    public static List<string> Merge(StructuralModel m, string story, List<string> tables)
    {
        if (reading || !Has(m) || !string.Equals(Story(m), story, StringComparison.OrdinalIgnoreCase)) return tables;
        var now = new List<(string Name, List<Dictionary<string, string>> Rows)>();
        foreach (var text in tables) now.AddRange(Tables(text));
        var nowByName = now.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.SelectMany(x => x.Rows).ToList(), StringComparer.OrdinalIgnoreCase);
        var order = m.Kept![Order].Select(r => r.GetValueOrDefault("Table") ?? "").Where(t => t.Length > 0).ToList();
        List<Dictionary<string, string>>? Base(string t) => m.Kept.GetValueOrDefault(BasePrefix + t);
        List<Dictionary<string, string>> Source(string t) => m.Kept.GetValueOrDefault(SourcePrefix + t) ?? [];

        // The tables the engineer changed: what the writer makes of them now is not what it made when the file was read.
        var baseNames = m.Kept.Keys.Where(k => k.StartsWith(BasePrefix, StringComparison.OrdinalIgnoreCase)).Select(k => k[BasePrefix.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = baseNames.Union(nowByName.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(t => Base(t) is not { } b || nowByName.GetValueOrDefault(t) is not { } w || !Same(b, w)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var defined = Defined(m);
        var output = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in order)
        {
            done.Add(t);
            var source = Source(t);
            var written = nowByName.GetValueOrDefault(t);
            var before = Base(t);
            List<Dictionary<string, string>> rows;
            // SAFE works these out from other tables: kept while those are as they were, else SAFE makes them again.
            if (written is null && before is null) { if (Derived(t) && changed.Any(c => DependsOn(t, c))) continue; rows = source; }
            else if (!changed.Contains(t)) rows = source;
            // Written when the file was read, and nothing now: all taken away, but for definitions the model still has
            // (the writer leaves out a material or section no object uses any more; the engineer did not delete it).
            else rows = Rows(source, before ?? [], written ?? [], Definition(t) ? defined : null);
            if (rows.Count > 0) output.Add(CsiTableText.Table(t, rows.Select(r => (IReadOnlyList<(string, string)>)r.Select(kv => (kv.Key, kv.Value)).ToList())));
        }
        // Tables the model has now that the file had none of.
        foreach (var (name, rows) in now.Where(t => !done.Contains(t.Name)))
            output.Add(CsiTableText.Table(name, rows.Select(r => (IReadOnlyList<(string, string)>)r.Select(kv => (kv.Key, kv.Value)).ToList())));
        return output;
    }

    /// <summary>A changed table: SAFE's rows with the engineer's changes put in, field by field.</summary>
    private static List<Dictionary<string, string>> Rows(List<Dictionary<string, string>> source, List<Dictionary<string, string>> before, List<Dictionary<string, string>> now, HashSet<string>? defined)
    {
        var srcList = Keyed(source);
        var src = srcList.ToDictionary(x => x.Key, x => x.Row, StringComparer.Ordinal);
        var bas = Keyed(before).ToDictionary(x => x.Key, x => x.Row, StringComparer.Ordinal);
        var place = srcList.Select((x, i) => (x.Key, i)).ToDictionary(x => x.Key, x => (double)x.i, StringComparer.Ordinal);
        var result = new List<(double At, Dictionary<string, string> Row)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var after = -1.0;
        var step = 0;
        foreach (var (key, row) in Keyed(now))
        {
            used.Add(key);
            var was = bas.GetValueOrDefault(key);
            if (src.GetValueOrDefault(key) is not { } kept)
            {
                // A new row goes after the row written before it, where SAFE's order has that one.
                result.Add((after + 0.5 + ++step * 1e-6, Copy(row)));
                continue;
            }
            var merged = Copy(kept);
            foreach (var (field, value) in row)
            {
                // A field the engineer changed (or one SAFE's row lacks) takes the new value; the rest stays SAFE's.
                var old = was?.GetValueOrDefault(field);
                if (was is null || old is null || !string.Equals(old, value, StringComparison.Ordinal) || !merged.ContainsKey(field)) merged[field] = value;
            }
            after = place[key];
            step = 0;
            result.Add((after, merged));
        }
        // SAFE's rows CSI ProMax never wrote (it has no place for them) stay; rows it wrote and no longer writes were taken away.
        foreach (var (key, row) in srcList)
            if (!used.Contains(key) && (!bas.ContainsKey(key) || defined?.Contains(key[..key.IndexOf('\u0001')]) == true)) result.Add((place[key], Copy(row)));
        return result.OrderBy(x => x.At).Select(x => x.Row).ToList();
    }

    /// <summary>Rows by their object: its name (carried down rows that leave it empty) and its place among rows of that name.</summary>
    private static List<(string Key, Dictionary<string, string> Row)> Keyed(List<Dictionary<string, string>> rows)
    {
        var list = new List<(string, Dictionary<string, string>)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var last = "";
        foreach (var r in rows)
        {
            var (field, name) = Identity(r);
            // A load is its object's and its pattern's: two loads on one slab are told apart by their pattern.
            if (name is { Length: > 0 } && field != "Load Pattern" && r.TryGetValue("Load Pattern", out var pattern)) name += "\u0002" + pattern;
            if (name is null) name = "#";
            else if (name.Length == 0) name = last;
            else last = name;
            var n = seen.GetValueOrDefault(name);
            seen[name] = n + 1;
            list.Add((name + "\u0001" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), r));
        }
        return list;
    }

    /// <summary>The fields that name a row's object in SAFE's tables, first found wins.</summary>
    private static readonly string[] Names = ["UniqueName", "Unique Name", "Name", "Material", "Strip Name", "Combo Name", "Group Name", "Point Name", "Load Pattern", "Case", "Pattern"];

    private static (string? Field, string? Value) Identity(Dictionary<string, string> r)
    {
        foreach (var n in Names) if (r.TryGetValue(n, out var v)) return (n, v);
        return (null, null);
    }

    /// <summary>What SAFE works out from the rest of the file each time it writes it.</summary>
    private static bool Derived(string table) =>
        table.EndsWith("SUMMARY", StringComparison.OrdinalIgnoreCase) || table.StartsWith("MATERIAL LIST", StringComparison.OrdinalIgnoreCase)
        || table.Equals("TENDON DISCRETIZED POINTS", StringComparison.OrdinalIgnoreCase) || table.Equals("TENDON - TOTAL ELONGATION", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tables of definitions (materials, sections, properties), which the writer writes only while something uses them.</summary>
    private static bool Definition(string table) =>
        table.StartsWith("MATERIAL PROPERTIES", StringComparison.OrdinalIgnoreCase) || table.Contains("SECTION PROPERT", StringComparison.OrdinalIgnoreCase)
        || table.Contains("PROPERTY DEFINITIONS", StringComparison.OrdinalIgnoreCase);

    /// <summary>The names of every material, section and property the model has.</summary>
    private static HashSet<string> Defined(StructuralModel m)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(m.Materials.Select(x => x.Name));
        names.UnionWith(m.FrameSections.Select(x => x.Name));
        names.UnionWith(m.ShellSections.Select(x => x.Name));
        names.UnionWith(SlabDefs.TendonSectionList(m).Select(x => x.Name));
        names.UnionWith(PropertyDefs.Springs(m).Select(x => x.Name));
        return names;
    }

    /// <summary>Whether SAFE works <paramref name="derived"/> out from <paramref name="table"/>.</summary>
    private static bool DependsOn(string derived, string table)
    {
        bool Has(string part) => table.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (derived.StartsWith("MATERIAL LIST", StringComparison.OrdinalIgnoreCase))
            return Has("MATERIAL") || Has("PROPERT") || Has("OBJECT CONNECTIVITY") || Has("SECTION");
        if (derived.StartsWith("TENDON", StringComparison.OrdinalIgnoreCase)) return Has("TENDON") || Has("POINT OBJECT") || Has("MATERIAL");
        var of = derived.EndsWith("SUMMARY", StringComparison.OrdinalIgnoreCase) ? derived[..^"SUMMARY".Length].TrimEnd(' ', '-') : derived;
        return table.StartsWith(of, StringComparison.OrdinalIgnoreCase) || (of.Contains("ASSIGNMENTS", StringComparison.OrdinalIgnoreCase) && Has("OBJECT CONNECTIVITY"))
            || (of.Contains("LOAD CASE", StringComparison.OrdinalIgnoreCase) && (Has("CASE DEFINITIONS") || Has("LOAD CASE")));
    }

    private static bool Same(List<Dictionary<string, string>> a, List<Dictionary<string, string>> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Count != b[i].Count) return false;
            foreach (var (k, v) in a[i]) if (!b[i].TryGetValue(k, out var w) || !string.Equals(v, w, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static Dictionary<string, string> Copy(Dictionary<string, string> r) => new(r, StringComparer.OrdinalIgnoreCase);

    /// <summary>Table text read back into tables, in the order written.</summary>
    private static List<(string Name, List<Dictionary<string, string>> Rows)> Tables(string text)
    {
        var parsed = CsiTableText.Parse(text);
        return CsiTableText.Names(text).Where(parsed.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (n.ToUpperInvariant(), parsed[n].Select(Copy).ToList())).ToList();
    }
}
