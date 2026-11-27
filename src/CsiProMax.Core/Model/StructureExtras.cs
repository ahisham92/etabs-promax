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

    /// <summary>Link/Support Property Data's Total Mass and Weight, Factors For Line and Area Springs and notes.</summary>
    public static readonly string[] LinkFields = ["Mass", "Weight", "RotInert1", "RotInert2", "RotInert3", "DefLength", "DefArea", "Notes"];
    // Declared before Fields, which uses it: statics start in the order they are written.

    /// <summary>The fields each table keeps, by the program's field names.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [GroupTable] = ["Steel", "Concrete", "Composite", "Selection", "SectionCut", "Aluminum", "ColdFormed", "Stage", "Bridge", "AutoSeismic", "AutoWind", "MassWeight", "ConcShell"],
        [CutTable] = ["ResultType", "ResultLoc", "LocX", "LocY", "LocZ", "RotAboutZ", "RotAboutY", "RotAboutX", "DefinedBy", "DesignType", "DesignAngle", "ElemSide", "AdvanceAxes", "Quads"],
        [SpandrelTable] = ["MultiStory"],
        ["Spring Property Definitions - Point Springs"] = ["NonlinOpt", "Color", "Notes"],
        ["Spring Property Definitions - Line Springs"] = ["NonlinOpt2", "NonlinOpt3", "Color", "Notes"],
        ["Spring Property Definitions - Area Springs"] = ["NonlinOpt3", "Color", "Notes"],
        ["Link Property Definitions - Linear"] = LinkFields,
        ["Link Property Definitions - Damper - Exponential"] = LinkFields,
        ["Link Property Definitions - Gap"] = LinkFields,
        ["Link Property Definitions - Hook"] = LinkFields,
        ["Link Property Definitions - Plastic Wen"] = LinkFields,
        ["Link Property Definitions - Isolator - Rubber"] = LinkFields,
        ["Link Property Definitions - Isolator - Friction"] = LinkFields,
        ["Link Property Definitions - Multilinear - Elastic"] = LinkFields,
        ["Link Property Definitions - Multilinear - Plastic"] = LinkFields,
    };


    public static string SpringTable(string kind) => "Spring Property Definitions - " + kind.ToLowerInvariant() switch { "point" => "Point Springs", "line" => "Line Springs", _ => "Area Springs" };

    /// <summary>The program's table for a link type (the Modeler's names: LINEAR, DAMPER, GAP, ...).</summary>
    public static string LinkTable(string type) => "Link Property Definitions - " + type.ToUpperInvariant() switch
    {
        "DAMPER" => "Damper - Exponential", "GAP" => "Gap", "HOOK" => "Hook", "PLASTIC (WEN)" => "Plastic Wen", "RUBBER ISOLATOR" => "Isolator - Rubber",
        "FRICTION ISOLATOR" => "Isolator - Friction", "MULTILINEAR ELASTIC" => "Multilinear - Elastic", "MULTILINEAR PLASTIC" => "Multilinear - Plastic", _ => "Linear",
    };

    /// <summary>A nonlinear link's table has a row per direction: its rows are put in by name and direction.</summary>
    public static bool PerDirection(string table) => table.StartsWith("Link Property Definitions - ", StringComparison.OrdinalIgnoreCase) && !table.EndsWith("- Linear", StringComparison.OrdinalIgnoreCase);

    public static bool IsLinkTable(string table) => table.StartsWith("Link Property Definitions - ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Takes a link's row out of every link table (its type may have changed).</summary>
    public static void RemoveLink(StructuralModel m, string name)
    {
        foreach (var t in Fields.Keys.Where(k => k.StartsWith("Link Property Definitions - ", StringComparison.OrdinalIgnoreCase)).ToList()) Remove(m, t, name);
    }

    /// <summary>Fields only SAP2000's table has; left out of the row put into ETABS.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SapOnly = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [GroupTable] = ["Selection", "SectionCut", "Aluminum", "ColdFormed", "Stage", "Bridge", "AutoSeismic", "AutoWind", "MassWeight", "ConcShell"],
        // SAP2000's Section Cut Data: quadrilateral cutting planes, the design axes (Wall/Spandrel/Slab) and the side reported.
        [CutTable] = ["DefinedBy", "DesignType", "DesignAngle", "ElemSide", "AdvanceAxes", "Quads"],
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
