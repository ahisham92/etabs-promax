using System.Globalization;

namespace CsiProMax.Tools;

/// <summary>The two spreadsheet helpers XlsxBook borrows from Sketch ProMax's checklist and cost sheets.</summary>
internal static class ChecklistMath
{
    /// <summary>"C12" is column 2 (zero-based).</summary>
    internal static int Column(string reference)
    {
        var col = 0;
        var any = false;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') { col = col * 26 + (ch - 'A' + 1); any = true; }
            else if (ch is >= 'a' and <= 'z') { col = col * 26 + (ch - 'a' + 1); any = true; }
            else break;
        }
        return any ? col - 1 : -1;
    }
}

internal static class BerthCostMath
{
    public static double? Num(string? text) =>
        double.TryParse((text ?? "").Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
