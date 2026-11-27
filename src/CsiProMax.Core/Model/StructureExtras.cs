using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Model;

/// <summary>
/// The fields of Group Definition, Section Cut Data and Spandrel Labels that the .e2k has no keyword for (a group's
/// design use, a section cut's result type, location and axes, a multi-story spandrel), kept as rows of ETABS 23's
/// own tables by name in <see cref="StructuralModel.DefinitionTables"/>, put into ETABS through those tables and
/// written into SAP2000's .s2k. The fields only SAP2000 has (a group's other uses) are kept in the same row and left
/// out when the row goes into ETABS.
/// </summary>
public static class StructureExtras
{
    public const string GroupTable = "Group Definitions", CutTable = "Section Cut Definitions", SpandrelTable = "Spandrel Label Definitions";

    /// <summary>The fields each table keeps, by the program's field names.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [GroupTable] = ["Steel", "Concrete", "Composite", "Selection", "SectionCut", "Aluminum", "ColdFormed", "Stage", "Bridge", "AutoSeismic", "AutoWind", "MassWeight"],
        [CutTable] = ["ResultType", "ResultLoc", "LocX", "LocY", "LocZ", "RotAboutZ", "RotAboutY", "RotAboutX"],
        [SpandrelTable] = ["MultiStory"],
    };

    /// <summary>Fields only SAP2000's table has; left out of the row put into ETABS.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SapOnly = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [GroupTable] = ["Selection", "SectionCut", "Aluminum", "ColdFormed", "Stage", "Bridge", "AutoSeismic", "AutoWind", "MassWeight"],
    };

    public static IReadOnlyDictionary<string, string> Get(StructuralModel m, string table, string name) =>
        m.DefinitionTables?.GetValueOrDefault(table)?.Find(r => r.GetValueOrDefault("Name") == name) is { } r
            ? r.Where(kv => kv.Key != "Name").ToDictionary(kv => kv.Key, kv => kv.Value) : new Dictionary<string, string>();

    /// <summary>Sets the fields of one definition (merged into what it had); unknown fields are refused.</summary>
    public static void Set(StructuralModel m, string table, string name, IReadOnlyDictionary<string, string> values)
    {
        var known = Fields[table];
        if (values.Keys.FirstOrDefault(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } bad)
            throw new ArgumentException($"'{bad}' is not a field of {table}. Its fields: {string.Join(", ", known)}.");
        m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
        var list = m.DefinitionTables.TryGetValue(table, out var l) ? l : m.DefinitionTables[table] = [];
        var row = list.Find(r => r.GetValueOrDefault("Name") == name);
        if (row is null) { row = new Dictionary<string, string> { ["Name"] = name }; list.Add(row); }
        foreach (var (k, v) in values) row[known.First(x => x.Equals(k, StringComparison.OrdinalIgnoreCase))] = v;
    }

    /// <summary>Takes a definition out (deleted or renamed away).</summary>
    public static void Remove(StructuralModel m, string table, string name)
    {
        if (m.DefinitionTables?.GetValueOrDefault(table) is not { } list) return;
        list.RemoveAll(r => r.GetValueOrDefault("Name") == name);
        if (list.Count == 0) m.DefinitionTables.Remove(table);
        if (m.DefinitionTables.Count == 0) m.DefinitionTables = null;
    }

    /// <summary>The row as it goes into ETABS: without the fields only SAP2000 has.</summary>
    public static Dictionary<string, string> ForEtabs(string table, IReadOnlyDictionary<string, string> row) =>
        row.Where(kv => !(SapOnly.TryGetValue(table, out var s) && s.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))).ToDictionary(kv => kv.Key, kv => kv.Value);

    public static bool Yes(IReadOnlyDictionary<string, string> row, string key, bool fallback) =>
        row.TryGetValue(key, out var v) ? v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("True", StringComparison.OrdinalIgnoreCase) : fallback;

    /// <summary>The section cuts the .e2k keeps (SECTIONCUT "name" GROUP "group").</summary>
    public static List<(string Name, string Group)> Cuts(StructuralModel m) =>
        m.Passthrough.Where(r => r.Heading == "SECTION CUTS").SelectMany(r => r.Lines).Select(Tokenize)
            .Where(t => t.Count >= 2 && t[0] == "SECTIONCUT").Select(t => (Unquote(t[1]), Value(t, "GROUP") ?? "")).ToList();
}
