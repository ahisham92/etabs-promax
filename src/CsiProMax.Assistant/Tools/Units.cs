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
    // The tables are CsiProMax.Lateral.SiteCoefficients', shared with the Modeler's loads so they agree.
    public static double Fa(string site, double ss) => Site(() => CsiProMax.Lateral.SiteCoefficients.Fa("ASCE 7-16", site, ss));
    public static double Fv(string site, double s1) => Site(() => CsiProMax.Lateral.SiteCoefficients.Fv("ASCE 7-16", site, s1));

    private static double Site(Func<double> look)
    {
        try { return look(); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
    }

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
