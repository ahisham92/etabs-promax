using CsiProMax.Model;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// What the Modeler's forms hold that ETABS's .e2k has no keyword for (a material's damping, notes, time dependence,
/// user stress-strain curve, ...) is kept in the .cpm beside the .e2k as rows of ETABS's own tables. When CSI ProMax
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
        return (done, refused);
    }
}
