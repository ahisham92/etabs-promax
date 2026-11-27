using CsiProMax.E2k;

namespace CsiProMax.Model;

/// <summary>
/// The Story Data and Grid System Data fields beyond a story's height and a grid line's ordinate, as ETABS 23 shows
/// them. What the .e2k has keywords for is kept in the line's own words: MASTERSTORY and SIMILARTO on a story,
/// VISIBLE and BUBBLELOC on a grid line, BUBBLESIZE on the grid system. A story's splice and colour are kept as rows
/// of ETABS's "Story Definitions" table (no .e2k keyword), put into the program through that table.
/// </summary>
public static class StoryGridOptions
{
    public const string StoryTable = "Story Definitions";

    public static string? Get(IReadOnlyList<string> extra, string key) => E2kText.Value(extra, key);

    /// <summary>Sets KEY "value" in the tokens (null takes it off).</summary>
    public static void Set(List<string> extra, string key, string? value, bool quoted = true)
    {
        for (var i = 0; i < extra.Count; i++)
            if (extra[i].Equals(key, StringComparison.OrdinalIgnoreCase)) { extra.RemoveRange(i, Math.Min(2, extra.Count - i)); break; }
        if (value is not null) extra.AddRange([key, quoted ? E2kText.Quote(value) : value]);
    }

    private static bool Yes(string? v) => v is not null && (v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("True", StringComparison.OrdinalIgnoreCase));

    public static bool IsMaster(Story s) => Yes(Get(s.Extra, "MASTERSTORY"));
    public static string? SimilarTo(Story s) => Get(s.Extra, "SIMILARTO") is { Length: > 0 } v && !v.Equals("None", StringComparison.OrdinalIgnoreCase) ? v : null;

    public static void SetMaster(Story s, bool master, string? similarTo)
    {
        Set(s.Extra, "MASTERSTORY", master ? "Yes" : null);
        Set(s.Extra, "SIMILARTO", !master && similarTo is { Length: > 0 } ? similarTo : null);
    }

    private static Dictionary<string, string>? Row(StructuralModel m, string story) =>
        m.DefinitionTables?.GetValueOrDefault(StoryTable)?.Find(r => r.GetValueOrDefault("Story") == story);

    public static (bool Splice, double Height, string? Color) Splice(StructuralModel m, string story)
    {
        var r = Row(m, story);
        E2kText.TryNumber(r?.GetValueOrDefault("SpliceHt"), out var h);
        return (Yes(r?.GetValueOrDefault("IsSpliced")), h, r?.GetValueOrDefault("Color") is { Length: > 0 } c ? c : null);
    }

    /// <summary>Keeps each story's splice and colour, by the stories' names now (rows of stories no longer there go).</summary>
    public static void SetSplices(StructuralModel m, IEnumerable<(string Story, bool Splice, double Height, string? Color)> rows)
    {
        var list = rows.Where(r => r.Splice || r.Color is { Length: > 0 }).Select(r =>
        {
            var row = new Dictionary<string, string> { ["Story"] = r.Story, ["IsSpliced"] = r.Splice ? "Yes" : "No", ["SpliceHt"] = E2kText.Number(r.Splice ? r.Height : 0) };
            if (r.Color is { Length: > 0 }) row["Color"] = r.Color;
            return row;
        }).ToList();
        m.DefinitionTables?.Remove(StoryTable);
        if (list.Count > 0) (m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase))[StoryTable] = list;
        if (m.DefinitionTables is { Count: 0 }) m.DefinitionTables = null;
    }

    public static bool Visible(GridLine g) => Get(g.Extra, "VISIBLE") is not { } v || Yes(v);
    /// <summary>End for X lines, Start for Y lines when the file says nothing, as ETABS draws them.</summary>
    public static string Bubble(GridLine g) => Get(g.Extra, "BUBBLELOC") is { Length: > 0 } v ? v : g.Direction == "Y" ? "Start" : "End";

    public static void SetGrid(GridLine g, bool visible, string bubble)
    {
        Set(g.Extra, "VISIBLE", visible ? "Yes" : "No");
        Set(g.Extra, "BUBBLELOC", bubble.Equals("Start", StringComparison.OrdinalIgnoreCase) ? "Start" : "End");
    }

    public static double BubbleSize(StructuralModel m) => E2kText.TryNumber(Get(m.GridSystemExtra, "BUBBLESIZE"), out var v) ? v : 0;
}
