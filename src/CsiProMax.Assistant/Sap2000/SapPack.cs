using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>The SAP2000 tables a review pack takes when they have data, beside the shared ones.</summary>
public static class SapPack
{
    private static readonly string[][] Words =
    [
        ["joint", "displacement"], ["joint", "reaction"], ["base", "reaction"], ["modal", "participating"], ["modal", "period"],
        ["assembled", "joint", "mass"], ["element", "force", "frame"], ["steel", "design", "summary"], ["concrete", "design", "summary"],
    ];

    public static bool Takes(string table)
    {
        var n = ResultTools.Norm(table);
        return Words.Any(w => w.All(x => n.Contains(x, StringComparison.Ordinal)));
    }
}
