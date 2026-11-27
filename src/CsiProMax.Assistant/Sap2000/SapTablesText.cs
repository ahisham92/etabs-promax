using CsiProMax.Assistant.Etabs;
using CsiProMax.CsiTables;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// A model open in SAP2000 (an .sdb with no .$2k beside it) as the .s2k SAP2000 would export: every table it can
/// import, read through its database tables (the same tables and fields its text file has), in the present units.
/// CSI ProMax then opens it as any SAP2000 file, so every real model can go through the Engine with nothing lost.
/// </summary>
public static class SapTablesText
{
    public static string Read(ICsiLink link)
    {
        var read = new List<(string, TableData)>();
        foreach (var t in link.Tables().Where(t => t.Import > 0 && !t.Empty))
            try { read.Add((t.Key, link.ReadTable(t.Key, null, null))); }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { }
        return Write(link.Info(), read);
    }

    /// <summary>The tables as SAP2000's text file: its Program Control first, with the units they were read in.</summary>
    public static string Write(ModelInfo info, IEnumerable<(string Key, TableData Data)> read)
    {
        var tables = new List<string>();
        var units = $"{info.Force}, {info.Length}, {info.Temperature}";
        var control = new List<(string, string)> { ("ProgramName", "SAP2000"), ("Version", info.Version), ("CurrUnits", units) };
        foreach (var (key, data) in read)
        {
            if (key.Equals("Program Control", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Rows.FirstOrDefault() is { } row)
                    control = data.Fields.Select((f, i) => (f, f.Equals("CurrUnits", StringComparison.OrdinalIgnoreCase) ? units : i < row.Length ? row[i] ?? "" : "")).Where(p => p.Item2.Length > 0).ToList();
                continue;
            }
            if (data.Rows.Count == 0) continue;
            tables.Add(CsiTableText.Table(key, data.Rows.Select(r => (IReadOnlyList<(string, string)>)data.Fields.Select((f, i) => (f, i < r.Length ? r[i] ?? "" : "")).Where(p => p.Item2.Length > 0).ToList())));
        }
        tables.Insert(0, CsiTableText.Table("Program Control", [control]));
        return CsiTableText.Document($"File {Path.GetFileName(info.File)} read from SAP2000's tables by CSI ProMax", tables);
    }
}
