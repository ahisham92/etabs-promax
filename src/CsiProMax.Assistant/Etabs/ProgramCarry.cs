using CsiProMax.Model;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The step after a text file that makes the program's model whole (<see cref="CarriedTables"/>): measured when a model
/// comes from the program, put in through the program's tables every time the model goes back into it.
/// </summary>
public static class ProgramCarry
{
    /// <summary>The program's model tables as <see cref="TableCarry"/> takes them.</summary>
    public static Dictionary<string, TableCarry.Table> AsTables(IReadOnlyDictionary<string, TableData> tables) =>
        tables.ToDictionary(t => t.Key, t => new TableCarry.Table(t.Value.Fields, t.Value.Rows), StringComparer.OrdinalIgnoreCase);

    /// <summary>What <paramref name="before"/> (the program's tables of its own model) has that <paramref name="after"/>
    /// (the same, once the program opened CSI ProMax's text file of it) lost; null when nothing was.</summary>
    public static CarriedTables? Measure(CsiProgram program, IReadOnlyDictionary<string, TableData> before, IReadOnlyDictionary<string, TableData> after)
    {
        var carried = TableCarry.Measure(program.Key, AsTables(before), AsTables(after));
        return carried.Count > 0 ? carried : null;
    }

    /// <summary>True when the model has rows to put into <paramref name="program"/>.</summary>
    public static bool Has(StructuralModel model, CsiProgram program) =>
        model.Carried is { Count: > 0 } c && string.Equals(c.Program, program.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the carried rows into the program behind <paramref name="e"/>, table by table, definitions first; says what
    /// went in and what the program refused, without stopping at the first refusal. Rows for another program are not put.
    /// </summary>
    public static (List<string> Done, List<string> Refused) Apply(ICsiLink e, StructuralModel model)
    {
        var done = new List<string>(); var refused = new List<string>();
        if (!Has(model, e.Program)) return (done, refused);
        var carried = model.Carried!;
        foreach (var (table, rows) in carried.Rows.OrderBy(t => TableCarry.Rank(t.Key)).ThenBy(t => t.Key, StringComparer.Ordinal))
        {
            if (rows.Count == 0) continue;
            try
            {
                EditableTables.Put(e, table, carried.Keys.GetValueOrDefault(table) ?? "", rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList(),
                    $"put back what the text file does not carry of '{table}'");
                done.Add($"{table}: {rows.Count} row{(rows.Count == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{table}: {ex.Message}"); }
        }
        return (done, refused);
    }
}
