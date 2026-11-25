using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Safe;

/// <summary>The SAFE tables a review pack takes when they have data, so the Viewer can show SAFE's results.</summary>
public static class SafePack
{
    private static readonly string[][] Words =
    [
        ["punch"], ["strip", "force"], ["soil", "pressure"], ["joint", "displacement"], ["joint", "reaction"], ["base", "reaction"],
    ];

    public static bool Takes(string table)
    {
        var n = ResultTools.Norm(table);
        return Words.Any(w => w.All(x => n.Contains(x, StringComparison.Ordinal))) || SafeTools.IsDesignTable(table);
    }
}
