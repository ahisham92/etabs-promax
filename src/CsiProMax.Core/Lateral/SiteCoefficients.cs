namespace CsiProMax.Lateral;

/// <summary>
/// The site coefficients Fa and Fv of ASCE 7 (tables 11.4-1 and 11.4-2), by the edition a code follows: the Modeler,
/// the response spectrum and the live program tools all read them from here, so a table is fixed in one place.
/// ASCE 7-05 and 7-10 (and IBC 2006 to 2015, which follow them) have five columns and their own values; ASCE 7-16
/// (IBC 2018, 2021, SBC 301-2018, KBC 2016) six. ASCE 7-22 has no such tables: SDS and SD1 come from the USGS
/// geodatabase for the site class, so <see cref="Edition"/> says so and no value is made up for it.
/// </summary>
public static class SiteCoefficients
{
    public enum Table { Asce710, Asce716, None }

    /// <summary>The table a code's name follows. ASCE 7-22 has none.</summary>
    public static Table Edition(string code)
    {
        var c = new string(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (c.Contains("722")) return Table.None;
        if (c.Contains("705") || c.Contains("710") || c is "ibc2006" or "ibc2009" or "ibc2012" or "ibc2015") return Table.Asce710;
        return Table.Asce716;
    }

    // ASCE 7-05 and 7-10, tables 11.4-1 and 11.4-2: Ss 0.25 to 1.25, S1 0.1 to 0.5.
    private static readonly double[] SsAt10 = [0.25, 0.5, 0.75, 1.0, 1.25];
    private static readonly double[] S1At10 = [0.1, 0.2, 0.3, 0.4, 0.5];
    private static readonly Dictionary<string, double?[]> Fa10 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [1.0, 1.0, 1.0, 1.0, 1.0], ["C"] = [1.2, 1.2, 1.1, 1.0, 1.0],
        ["D"] = [1.6, 1.4, 1.2, 1.1, 1.0], ["E"] = [2.5, 1.7, 1.2, 0.9, 0.9],
    };
    private static readonly Dictionary<string, double?[]> Fv10 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [1.0, 1.0, 1.0, 1.0, 1.0], ["C"] = [1.7, 1.6, 1.5, 1.4, 1.3],
        ["D"] = [2.4, 2.0, 1.8, 1.6, 1.5], ["E"] = [3.5, 3.2, 2.8, 2.4, 2.4],
    };

    // ASCE 7-16, tables 11.4-1 and 11.4-2: Ss 0.25 to 1.5, S1 0.1 to 0.6.
    private static readonly double[] SsAt16 = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5];
    private static readonly double[] S1At16 = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6];
    private static readonly Dictionary<string, double?[]> Fa16 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [0.9, 0.9, 0.9, 0.9, 0.9, 0.9], ["C"] = [1.3, 1.3, 1.2, 1.2, 1.2, 1.2],
        ["D"] = [1.6, 1.4, 1.2, 1.1, 1.0, 1.0], ["E"] = [2.4, 1.7, 1.3, null, null, null],
    };
    private static readonly Dictionary<string, double?[]> Fv16 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["C"] = [1.5, 1.5, 1.5, 1.5, 1.5, 1.4],
        // Site class E from S1 0.2 up as Supplement 1 to ASCE 7-16 gives it back; 11.4.8 then asks for a site-specific
        // study unless an exception applies (the tools say so).
        ["D"] = [2.4, 2.2, 2.0, 1.9, 1.8, 1.7], ["E"] = [4.2, 3.3, 2.8, 2.4, 2.2, 2.0],
    };

    public static double Fa(string code, string site, double ss) => Edition(code) switch
    {
        Table.Asce710 => Look(Fa10, SsAt10, site, ss, "Fa", code),
        Table.None => throw NoTable(code),
        _ => Look(Fa16, SsAt16, site, ss, "Fa", code),
    };

    public static double Fv(string code, string site, double s1) => Edition(code) switch
    {
        Table.Asce710 => Look(Fv10, S1At10, site, s1, "Fv", code),
        Table.None => throw NoTable(code),
        _ => Look(Fv16, S1At16, site, s1, "Fv", code),
    };

    private static ArgumentException NoTable(string code) =>
        new($"{code} has no Fa and Fv tables: give SDS and SD1 (from the USGS Seismic Design Geodatabase for the site class).");

    private static double Look(Dictionary<string, double?[]> table, double[] at, string site, double value, string what, string code)
    {
        if (!table.TryGetValue(site, out var row)) throw new ArgumentException($"Site class {site} has no {what} in the {code} table (F needs a site-specific study): give {what}.");
        if (value <= at[0]) return row[0] ?? throw Needs(site, what, code);
        for (var i = 1; i < at.Length; i++)
            if (value <= at[i])
            {
                if (row[i - 1] is not { } lo || row[i] is not { } hi) throw Needs(site, what, code);
                return lo + (hi - lo) * (value - at[i - 1]) / (at[i] - at[i - 1]);
            }
        return row[^1] ?? throw Needs(site, what, code);
    }

    private static ArgumentException Needs(string site, string what, string code) =>
        new($"{code} gives no {what} for site class {site} at this value (a site-specific study, 11.4.8): give {what}.");
}
