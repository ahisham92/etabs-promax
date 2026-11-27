using System.Globalization;
using System.Text.RegularExpressions;

namespace CsiProMax.Tools;

/// <summary>
/// Reading a drawing page (read_drawing) without ETABS: the labelled grid laid over a page, a cell or fractions as a
/// region of it, and where a zoom of the page falls in pixels.
/// </summary>
internal static class AsBuiltMath
{
    // ---- Reading a drawing page --------------------------------------------------------------------------------

    /// <summary>A part of a page as fractions of its width and height, from the top left corner.</summary>
    public readonly record struct Region(double Left, double Top, double Right, double Bottom)
    {
        public double W => Right - Left;
        public double H => Bottom - Top;
        public static Region Whole => new(0, 0, 1, 1);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[{0:0.###}, {1:0.###}, {2:0.###}, {3:0.###}]", Left, Top, Right, Bottom);
    }

    /// <summary>Columns and rows of the grid laid over a whole page, chosen so the cells are about square.</summary>
    public static (int Cols, int Rows) GridFor(double pageW, double pageH)
    {
        if (pageW <= 0 || pageH <= 0) return (8, 6);
        return pageW >= pageH ? (8, Math.Max(3, (int)Math.Round(8 * pageH / pageW))) : (Math.Max(3, (int)Math.Round(8 * pageW / pageH)), 8);
    }

    /// <summary>Column letters: A..Z, then AA...</summary>
    public static string Column(int i) => i < 26 ? ((char)('A' + i)).ToString() : Column(i / 26 - 1) + (char)('A' + i % 26);

    private static readonly Regex Cell = new(@"^\s*([A-Za-z]{1,2})\s*(\d{1,2})\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The region for "C3" (one grid cell) or "B2:D4" (a block of cells), on a grid of cols x rows; null with the reason
    /// when it isn't a cell name on that grid.
    /// </summary>
    public static Region? CellRegion(string text, int cols, int rows, out string error)
    {
        error = "";
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 2) { error = $"\"{text}\" is not a cell (C3) or a block of cells (B2:D4)."; return null; }
        var cells = new List<(int c, int r)>();
        foreach (var p in parts)
        {
            var m = Cell.Match(p);
            if (!m.Success) { error = $"\"{p.Trim()}\" is not a cell name like C3."; return null; }
            var letters = m.Groups[1].Value.ToUpperInvariant();
            var c = letters.Length == 1 ? letters[0] - 'A' : (letters[0] - 'A' + 1) * 26 + letters[1] - 'A';
            var r = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
            if (c < 0 || c >= cols || r < 0 || r >= rows) { error = $"{p.Trim().ToUpperInvariant()} is off the grid (A1 to {Column(cols - 1)}{rows})."; return null; }
            cells.Add((c, r));
        }
        int c0 = cells.Min(x => x.c), c1 = cells.Max(x => x.c), r0 = cells.Min(x => x.r), r1 = cells.Max(x => x.r);
        return new Region(c0 / (double)cols, r0 / (double)rows, (c1 + 1) / (double)cols, (r1 + 1) / (double)rows);
    }

    /// <summary>A region from [left, top, right, bottom] fractions, put in order and kept on the page; null with the reason.</summary>
    public static Region? Fractions(IReadOnlyList<double> v, out string error)
    {
        error = "";
        if (v.Count != 4) { error = "region takes four numbers: [left, top, right, bottom], as fractions of the page from its top left."; return null; }
        double l = Clamp(Math.Min(v[0], v[2])), r = Clamp(Math.Max(v[0], v[2])), t = Clamp(Math.Min(v[1], v[3])), b = Clamp(Math.Max(v[1], v[3]));
        if (r - l < 0.005 || b - t < 0.005) { error = "The region is too small: give at least half a percent of the page each way."; return null; }
        return new Region(l, t, r, b);
        static double Clamp(double x) => Math.Max(0, Math.Min(1, x));
    }

    /// <summary>
    /// The picture for a region of a page of pageW x pageH points: its size in pixels (longest side maxPixels), and the
    /// full page size and the offset to draw the whole page at so that the region fills the picture. Dpi is the scan
    /// resolution the picture has on the printed page.
    /// </summary>
    public static (int Width, int Height, int FullWidth, int FullHeight, int OffsetX, int OffsetY, double Dpi) Zoom(double pageW, double pageH, Region region, int maxPixels)
    {
        double rw = pageW * region.W, rh = pageH * region.H;
        var scale = maxPixels / Math.Max(rw, rh);
        // A picture past ~25 000 pixels across the whole page is far past any scan's own resolution; cap it so the page fits in memory maths.
        scale = Math.Min(scale, 30000 / Math.Max(pageW, pageH));
        int w = Math.Max(1, (int)Math.Round(rw * scale)), h = Math.Max(1, (int)Math.Round(rh * scale));
        int fw = Math.Max(1, (int)Math.Round(pageW * scale)), fh = Math.Max(1, (int)Math.Round(pageH * scale));
        return (w, h, fw, fh, (int)Math.Round(region.Left * fw), (int)Math.Round(region.Top * fh), scale * 72);
    }

    /// <summary>A region of the whole page from a region of a zoomed picture (both as fractions), so a zoom can be zoomed again.</summary>
    public static Region Within(Region outer, Region inner) =>
        new(outer.Left + inner.Left * outer.W, outer.Top + inner.Top * outer.H, outer.Left + inner.Right * outer.W, outer.Top + inner.Bottom * outer.H);
}
