using CsiProMax.Model;

namespace CsiProMax.CsiTables;

/// <summary>
/// The rows the Modeler keeps of the programs' own tables, beside the tables <see cref="ModelTables"/> builds from the
/// model: a material's other data (<see cref="ProgramTables"/>), the objects' assignments the .e2k has no keyword for
/// (<see cref="AssignTables"/>) and the definitions kept as table rows (<see cref="DefineTables"/>). They go to Excel
/// as tables of their own, and a table read back replaces those rows in the model, so Excel out and in loses nothing.
/// </summary>
public static class ExtraTables
{
    /// <summary>Every table name this can read, with its key fields and its other fields.</summary>
    public static IEnumerable<(string Name, string[] Keys, string[] Fields)> Catalogue()
    {
        foreach (var (t, fields) in ProgramTables.MaterialTables)
            if (!ModelTables.Names.Contains(ProgramTables.MaterialGroup + t, StringComparer.OrdinalIgnoreCase))
                yield return (ProgramTables.MaterialGroup + t, ["Material"], fields);
        foreach (var (t, def) in AssignTables.Tables) yield return (t, ["Story", "Label"], def.Fields.Select(f => f.Key).ToArray());
        foreach (var (t, fields) in DefineTables.Tables) yield return (t, [fields[0].Key], fields.Skip(1).Select(f => f.Key).ToArray());
    }

    public static string? Known(string name) => Catalogue().Select(c => c.Name).FirstOrDefault(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The tables the model has rows of.</summary>
    public static List<ModelTable> Export(StructuralModel m, ISet<string>? wanted)
    {
        var rows = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, r) in ProgramTables.MaterialRows(m)) rows[t] = r;
        foreach (var (t, r) in AssignTables.Rows(m)) rows[t] = r;
        foreach (var (t, r) in m.DefinitionTables ?? []) rows[t] = r;
        var tables = new List<ModelTable>();
        foreach (var (name, keys, fields) in Catalogue())
        {
            if (!rows.TryGetValue(name, out var list) || list.Count == 0) continue;
            if (wanted is { Count: > 0 } && !wanted.Contains(name)) continue;
            var all = keys.Concat(fields).ToList();
            tables.Add(new ModelTable(name, all, all.Select(_ => "").ToList(), list.Select(r => all.Select(f => r.GetValueOrDefault(f) ?? "").ToList()).ToList()));
        }
        return tables;
    }

    /// <summary>Reads one table back: its rows replace that table's rows in the model. Says what was read; a row for an
    /// object or material the model does not have is refused, naming the row.</summary>
    public static int Import(StructuralModel m, ModelTable t, List<string> notes)
    {
        var (name, keys, fields) = Catalogue().First(c => c.Name.Equals(t.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        foreach (var k in keys)
            if (t.Field(k) < 0) throw new ArgumentException($"{name}: the field {k} is missing (row 2 holds the field names).");
        foreach (var f in t.Fields.Where(f => f.Length > 0 && !keys.Contains(f, StringComparer.OrdinalIgnoreCase) && !fields.Contains(f, StringComparer.OrdinalIgnoreCase)))
            notes.Add($"{name}: the field {f} is not one of the table's, so it was left out.");
        var read = t.Rows.Select((r, i) => (Row: t.FirstRow + i,
                Values: fields.Where(f => t.Field(f) >= 0 && Cell(r, t.Field(f)).Length > 0).ToDictionary(f => f, f => Cell(r, t.Field(f)), StringComparer.Ordinal),
                Key: keys.Select(k => Cell(r, t.Field(k))).ToArray()))
            .Where(x => x.Key.Any(k => k.Length > 0) || x.Values.Values.Any(v => v.Length > 0)).ToList();
        foreach (var x in read)
            if (x.Key.Any(k => k.Length == 0)) throw new ArgumentException($"{name}, row {x.Row}: {string.Join(" and ", keys)} {(keys.Length > 1 ? "are" : "is")} needed.");

        if (keys[0] == "Material")
        {
            var table = name[ProgramTables.MaterialGroup.Length..];
            foreach (var x in read)
                if (!m.Materials.Exists(y => y.Name == x.Key[0])) throw new ArgumentException($"{name}, row {x.Row}: there is no material '{x.Key[0]}'.");
            foreach (var mat in m.Materials) ProgramTables.SetMaterialTables(mat, new Dictionary<string, List<Dictionary<string, string>>> { [table] = read.Where(x => x.Key[0] == mat.Name).Select(x => x.Values).ToList() });
        }
        else if (keys.Length == 2)
        {
            var kind = AssignTables.Tables[name].Kind;
            IEnumerable<Assign> Of() => kind switch { "frame" => m.LineAssigns, "area" => m.AreaAssigns, _ => m.PointAssigns };
            string LabelOf(Assign a) => a is PointAssign p ? p.Point : a is LineAssign l ? l.Label : ((AreaAssign)a).Label;
            var byKey = new Dictionary<(string, string), Assign>();
            foreach (var a in Of()) byKey.TryAdd((a.Story, LabelOf(a)), a);
            foreach (var x in read)
                if (!byKey.ContainsKey((x.Key[0], x.Key[1]))) throw new ArgumentException($"{name}, row {x.Row}: there is no {(kind == "area" ? "slab or wall" : kind)} {x.Key[1]} on {x.Key[0]}.");
            foreach (var a in Of())
            {
                var mine = read.Where(x => x.Key[0] == a.Story && x.Key[1] == LabelOf(a)).Select(x => x.Values).ToList();
                if (mine.Count > 0 || a.Tables?.ContainsKey(name) == true) AssignTables.Set(a, new Dictionary<string, List<Dictionary<string, string>>> { [name] = mine });
            }
        }
        else
        {
            foreach (var n in (m.DefinitionTables?.GetValueOrDefault(name) ?? []).Select(r => r.GetValueOrDefault(keys[0]) ?? "").Distinct().ToList())
                DefineTables.Set(m, name, n, []);
            foreach (var g in read.GroupBy(x => x.Key[0]))
                DefineTables.Set(m, name, g.Key, g.Select(x => x.Values).ToList());
        }
        return read.Count;
    }

    private static string Cell(List<string> row, int at) => at >= 0 && at < row.Count ? (row[at] ?? "").Trim() : "";
}
