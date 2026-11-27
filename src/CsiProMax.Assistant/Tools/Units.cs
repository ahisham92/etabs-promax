using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The model's units as factors to kN and m, so a tool that knows a value in SI (a concrete strength in MPa, a unit
/// weight in kN/m3, g) can give it to the program in the model's own units. The names are the ones the link reads
/// (ModelInfo.Force and Length, from the program's eUnits: kN_m_C and so on).
/// </summary>
public static class Units
{
    public static double ForceToKn(string force) => force.ToLowerInvariant() switch
    {
        "kn" => 1, "n" => 0.001, "kip" => 4.4482216, "lb" => 0.0044482216, "kgf" => 0.00980665, "tonf" or "ton" => 9.80665,
        _ => throw new EtabsException($"The model's force unit '{force}' is not one CSI ProMax knows."),
    };

    public static double LengthToM(string length) => length.ToLowerInvariant() switch
    {
        "m" => 1, "mm" => 0.001, "cm" => 0.01, "in" => 0.0254, "ft" => 0.3048, "micron" => 1e-6,
        _ => throw new EtabsException($"The model's length unit '{length}' is not one CSI ProMax knows."),
    };

    /// <summary>A stress in MPa in the model's units (force per length squared).</summary>
    public static double FromMpa(ModelInfo info, double mpa) => mpa * 1000 / ForceToKn(info.Force) * Math.Pow(LengthToM(info.Length), 2);

    /// <summary>A pressure in kN/m2 in the model's units.</summary>
    public static double FromKpa(ModelInfo info, double kpa) => kpa / ForceToKn(info.Force) * Math.Pow(LengthToM(info.Length), 2);

    /// <summary>A unit weight in kN/m3 in the model's units.</summary>
    public static double FromKnPerM3(ModelInfo info, double value) => value / ForceToKn(info.Force) * Math.Pow(LengthToM(info.Length), 3);

    /// <summary>The acceleration of gravity in the model's length unit per second squared.</summary>
    public static double Gravity(ModelInfo info) => 9.80665 / LengthToM(info.Length);

    /// <summary>The eUnits number (kN_m_C is 6) of a force and length, with the temperature that goes with them.</summary>
    public static int Code(string force, string length)
    {
        var f = force.Equals("tonf", StringComparison.OrdinalIgnoreCase) ? "Ton" : force;
        for (var i = 0; i < ComEtabs.UnitNames.Length; i++)
        {
            var parts = ComEtabs.UnitNames[i].Split('_');
            if (parts[0].Equals(f, StringComparison.OrdinalIgnoreCase) && parts[1].Equals(length, StringComparison.OrdinalIgnoreCase)) return i + 1;
        }
        throw new ToolInputException($"There is no unit set of {force} and {length}. The sets: {string.Join(", ", ComEtabs.UnitNames)}.");
    }
}

/// <summary>
/// The ASCE 7-16 design response spectrum (11.4): site coefficients Fa and Fv from tables 11.4-1 and 11.4-2, SDS and
/// SD1, and Sa(T) as a list of points the program takes as its own response spectrum function. The program's own
/// ASCE 7-16 function takes its arguments in another order in each program, so the points are given instead: the
/// same spectrum in ETABS and SAP2000.
/// </summary>
public static class Spectrum
{
    private static readonly double[] SsAt = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5];
    private static readonly double[] S1At = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6];

    private static readonly Dictionary<string, double?[]> FaTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [0.9, 0.9, 0.9, 0.9, 0.9, 0.9], ["C"] = [1.3, 1.3, 1.2, 1.2, 1.2, 1.2],
        ["D"] = [1.6, 1.4, 1.2, 1.1, 1.0, 1.0], ["E"] = [2.4, 1.7, 1.3, null, null, null],
    };

    private static readonly Dictionary<string, double?[]> FvTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["B"] = [0.8, 0.8, 0.8, 0.8, 0.8, 0.8], ["C"] = [1.5, 1.5, 1.5, 1.5, 1.5, 1.4],
        // Site class E from S1 0.2 up as Supplement 1 to ASCE 7-16 gives it back; 11.4.8 then asks for a site-specific
        // study unless an exception applies (SiteSpecificNote says so).
        ["D"] = [2.4, 2.2, 2.0, 1.9, 1.8, 1.7], ["E"] = [4.2, 3.3, 2.8, 2.4, 2.2, 2.0],
    };

    public static double Fa(string site, double ss) => Interpolate(FaTable, SsAt, site, ss, "Fa");
    public static double Fv(string site, double s1) => Interpolate(FvTable, S1At, site, s1, "Fv");

    private static double Interpolate(Dictionary<string, double?[]> table, double[] at, string site, double value, string what)
    {
        if (!table.TryGetValue(site, out var row)) throw new ToolInputException($"The site class is A, B, C, D or E (F needs a site-specific study); give {what} yourself for any other.");
        if (value <= at[0]) return row[0] ?? throw Needs(site, what);
        for (var i = 1; i < at.Length; i++)
            if (value <= at[i])
            {
                if (row[i - 1] is not { } lo || row[i] is not { } hi) throw Needs(site, what);
                return lo + (hi - lo) * (value - at[i - 1]) / (at[i] - at[i - 1]);
            }
        return row[^1] ?? throw Needs(site, what);
    }

    private static ToolInputException Needs(string site, string what) =>
        new($"ASCE 7-16 gives no {what} for site class {site} at this value (a site-specific study, 11.4.8): give {what.ToLowerInvariant()} yourself.");

    /// <summary>
    /// What ASCE 7-16 11.4.8 says about this site, or null: a ground motion hazard analysis is required for site class D
    /// and E with S1 at 0.2 or more, and E with Ss at 1.0 or more, unless one of the section's exceptions applies.
    /// </summary>
    public static string? SiteSpecificNote(string site, double ss, double s1)
    {
        var e = site.Equals("E", StringComparison.OrdinalIgnoreCase);
        var d = site.Equals("D", StringComparison.OrdinalIgnoreCase);
        var reasons = new List<string>();
        if ((d || e) && s1 >= 0.2) reasons.Add($"site class {site.ToUpperInvariant()} with S1 = {s1.ToString(System.Globalization.CultureInfo.InvariantCulture)} (0.2 or more)");
        if (e && ss >= 1.0) reasons.Add($"site class E with Ss = {ss.ToString(System.Globalization.CultureInfo.InvariantCulture)} (1.0 or more)");
        if (reasons.Count == 0) return null;
        return $"ASCE 7-16 11.4.8 requires a site-specific ground motion hazard analysis for {string.Join(" and ", reasons)}, unless one of its exceptions applies " +
               "(for site class D, ELF with Cs by Eq. 12.8-2 up to T = 1.5 Ts and 1.5 times Eq. 12.8-3 or 12.8-4 above; Supplement 3 instead raises SM1 by 50%). " +
               "The table's Fa and Fv are then only for Ts and the exceptions: confirm with the engineer which applies before using these loads.";
    }

    /// <summary>SDS and SD1 (11.4.4): two thirds of the site-adjusted MCE values.</summary>
    public static (double Sds, double Sd1) Design(double ss, double s1, double fa, double fv) => (2.0 / 3 * fa * ss, 2.0 / 3 * fv * s1);

    /// <summary>Sa at period T (11.4.6), in g.</summary>
    public static double Sa(double t, double sds, double sd1, double tl)
    {
        var ts = sd1 / sds;
        var t0 = 0.2 * ts;
        if (t < t0) return sds * (0.4 + 0.6 * t / t0);
        if (t <= ts) return sds;
        if (t <= tl) return sd1 / t;
        return sd1 * tl / (t * t);
    }

    /// <summary>The spectrum as points from 0 to <paramref name="last"/> seconds, close together where it bends.</summary>
    public static (double[] Periods, double[] Values) Points(double sds, double sd1, double tl, double last = 10)
    {
        var ts = sd1 / sds;
        var t0 = 0.2 * ts;
        var periods = new SortedSet<double> { 0, t0, ts, tl, last };
        for (var t = 0.05; t < last; t += t < 1 ? 0.05 : t < 4 ? 0.1 : 0.5) periods.Add(Math.Round(t, 4));
        var list = periods.Where(t => t <= last).ToArray();
        return (list, list.Select(t => Math.Round(Sa(t, sds, sd1, tl), 6)).ToArray());
    }
}
