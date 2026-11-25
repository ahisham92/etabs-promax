using System.Globalization;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// Lateral definitions in ETABS 23, whose API (read from its ETABSv1.dll and help, 0.4.17 field test) has no user
/// coefficient, no auto wind call, no mass source object and no user spectrum: those go through its editable tables.
/// Its SetASCE716 takes the directions 1-based (7 long), SetASCE716_1 0-based (6 long: X, Y, X+e, Y+e, X-e, Y-e).
/// </summary>
public static class EtabsLateral
{
    private static string N(double v) => EditableTables.Num(v);

    /// <summary>The auto seismic code ETABS has on a Seismic pattern; blank for none.</summary>
    public static string SeismicCode(ICsiLink e, string pattern) => (string)e.Raw($"read the auto seismic code of {pattern}", m =>
    {
        string code = "";
        try { m.LoadPatterns.GetAutoSeismicCode(pattern, ref code); }
        catch (Exception ex) when (ex is MissingMethodException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { code = ""; }
        return code ?? "";
    })!;

    private static (string Top, string Bottom, double TopZ, double BottomZ) Range(ICsiLink e)
    {
        var stories = e.Stories();
        return stories.Count >= 2 ? (stories[^1].Name, stories[0].Name, stories[^1].Elevation, stories[0].Elevation) : ("", "", 0, 0);
    }

    private static (string Field, string Value)[] Directions(string dir, double ecc)
    {
        bool x = dir.Equals("X", StringComparison.OrdinalIgnoreCase);
        return
        [
            ("XDir", EditableTables.YesNo(x)), ("XDirPlusE", "No"), ("XDirMinusE", "No"),
            ("YDir", EditableTables.YesNo(!x)), ("YDirPlusE", "No"), ("YDirMinusE", "No"),
            ("EccRatio", N(ecc)),
        ];
    }

    /// <summary>A user coefficient seismic load on an existing Seismic pattern, through ETABS's table; read back by its code.</summary>
    public static string UserCoefficient(ICsiLink e, string pattern, string dir, double ecc, double c, double k)
    {
        var (top, bottom, _, _) = Range(e);
        var row = new List<(string, string)> { ("Name", pattern) };
        row.AddRange(Directions(dir, ecc));
        if (top.Length > 0) { row.Add(("TopStory", top)); row.Add(("BotStory", bottom)); }
        row.Add(("C", N(c))); row.Add(("K", N(k)));
        const string table = "Load Pattern Definitions - Auto Seismic - User Coefficient";
        Exception? first = null;
        try { EditableTables.Put(e, table, "Name", [EditableTables.Row(row.ToArray())], $"give {pattern} its user coefficient"); }
        catch (EtabsException ex) { first = ex; }
        if (SeismicCode(e, pattern).Length > 0) return Said(c, k, dir, ecc);

        // The pattern may first need its auto load type in the patterns' table.
        foreach (var autoLoad in new[] { "User Coefficient", "User Coefficients", "User Coeff" })
        {
            try
            {
                EditableTables.Put(e, "Load Pattern Definitions", "Name", [EditableTables.Row(("Name", pattern), ("AutoLoad", autoLoad))], $"make {pattern} a user coefficient load");
                EditableTables.Put(e, table, "Name", [EditableTables.Row(row.ToArray())], $"give {pattern} its user coefficient");
            }
            catch (EtabsException ex) { first ??= ex; continue; }
            if (SeismicCode(e, pattern).Length > 0) return Said(c, k, dir, ecc);
        }
        throw first ?? new EtabsException($"{e.Program.Name} took the user coefficient table, but {pattern} has no auto seismic load when read back.");
    }

    private static string Said(double c, double k, string dir, double ecc) =>
        $"user coefficient C = {c.ToString(CultureInfo.InvariantCulture)}, K = {k.ToString(CultureInfo.InvariantCulture)}, direction {dir.ToUpperInvariant()}, eccentricity {ecc.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>ASCE 7-16 on an existing Seismic pattern: SetASCE716_1, then SetASCE716, then the table. Returns what took it.</summary>
    public static string Asce716(ICsiLink e, string pattern, string dir, double ecc, int periodFlag, int ctType, double userT,
        double r, double omega, double cd, double importance, double ss, double s1, double tl, int siteClass, double fa, double fv)
    {
        var (top, bottom, topZ, bottomZ) = Range(e);
        bool x = dir.Equals("X", StringComparison.OrdinalIgnoreCase);
        var answers = new List<string>();
        var took = (string?)e.Raw($"set {pattern} to ASCE 7-16", m =>
        {
            try
            {
                bool[] zero = [x, !x, false, false, false, false];
                int a1 = Convert.ToInt32(m.LoadPatterns.AutoSeismic.SetASCE716_1(pattern, ref zero, ecc, periodFlag, ctType, userT, false, topZ, bottomZ, r, omega, cd, importance, ss, s1, tl, siteClass, fa, fv));
                if (a1 == 0) return "SetASCE716_1";
                answers.Add($"SetASCE716_1 answered {a1}");
            }
            catch (Exception ex) when (ex is MissingMethodException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { answers.Add("no SetASCE716_1"); }
            try
            {
                bool[] one = [false, x, !x, false, false, false, false];
                int a0 = Convert.ToInt32(m.LoadPatterns.AutoSeismic.SetASCE716(pattern, ref one, ecc, periodFlag, ctType, userT, false, topZ, bottomZ, r, omega, cd, importance, ss, s1, tl, siteClass, fa, fv));
                if (a0 == 0) return "SetASCE716";
                answers.Add($"SetASCE716 answered {a0}");
            }
            catch (Exception ex) when (ex is MissingMethodException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { answers.Add("no SetASCE716"); }
            return null;
        });
        if (took is not null && SeismicCode(e, pattern).Length > 0) return took;

        var row = new List<(string, string)> { ("Name", pattern) };
        row.AddRange(Directions(dir, ecc));
        if (top.Length > 0) { row.Add(("TopStory", top)); row.Add(("BotStory", bottom)); }
        row.Add(("PeriodType", periodFlag switch { 1 => "Approximate", 3 => "User Specified", _ => "Program Calculated" }));
        if (periodFlag == 3) row.Add(("UserT", N(userT)));
        row.AddRange([("Ss", N(ss)), ("S1", N(s1)), ("TL", N(tl)), ("SiteClass", ((char)('A' + siteClass - 1)).ToString()), ("R", N(r)), ("Omega", N(omega)), ("Cd", N(cd)), ("I", N(importance))]);
        if (siteClass >= 5) { row.Add(("Fa", N(fa))); row.Add(("Fv", N(fv))); }
        try { EditableTables.Put(e, "Load Pattern Definitions - Auto Seismic - ASCE 7-16", "Name", [EditableTables.Row(row.ToArray())], $"set {pattern} to ASCE 7-16"); }
        catch (EtabsException ex) { throw new EtabsException($"{ex.Message} (the API first: {string.Join("; ", answers)})"); }
        if (SeismicCode(e, pattern).Length == 0)
            throw new EtabsException($"{e.Program.Name} took the ASCE 7-16 table, but {pattern} has no auto seismic load when read back (the API first: {string.Join("; ", answers)}).");
        return "the table Auto Seismic - ASCE 7-16";
    }

    /// <summary>ETABS's one mass source (its default), through PropMaterial.SetMassSource_1; read back.</summary>
    public static (bool Elements, bool Added, List<string> Patterns) MassSource(ICsiLink e, bool elements, bool added, IReadOnlyList<(string Pattern, double Factor)> loads)
    {
        return ((bool, bool, List<string>))e.Raw("set the mass source", m =>
        {
            bool inElements = elements, inAdded = added, inLoads = loads.Count > 0;
            string[] patterns = loads.Select(l => l.Pattern).ToArray();
            double[] factors = loads.Select(l => l.Factor).ToArray();
            int answer = Convert.ToInt32(m.PropMaterial.SetMassSource_1(ref inElements, ref inAdded, ref inLoads, patterns.Length, ref patterns, ref factors));
            if (answer != 0) throw new EtabsException($"{e.Program.Name} refused the mass source (PropMaterial.SetMassSource_1 answered {answer}).");
            bool bElements = false, bAdded = false, bLoads = false; int n = 0; string[] bp = []; double[] bf = [];
            m.PropMaterial.GetMassSource_1(ref bElements, ref bAdded, ref bLoads, ref n, ref bp, ref bf);
            return (bElements, bAdded, (bp ?? []).Take(n).ToList());
        })!;
    }

    /// <summary>A user response spectrum function, through ETABS's table; read back by the function names.</summary>
    public static void UserSpectrum(ICsiLink e, string function, double[] periods, double[] values, double damping)
    {
        var rows = periods.Select((t, i) => EditableTables.Row(("Name", function), ("Period", N(t)), ("Value", N(values[i])), ("DampRatio", N(damping)))).ToList();
        EditableTables.Put(e, "Functions - Response Spectrum - User Defined", "Name", rows, $"add the spectrum {function}");
        var names = (List<string>)e.Raw("list the functions", m =>
        {
            int n = 0; string[] list = [];
            m.Func.GetNameList(ref n, ref list);
            return (list ?? []).ToList();
        })!;
        if (!names.Contains(function, StringComparer.OrdinalIgnoreCase))
            throw new EtabsException($"{e.Program.Name} took the spectrum table, but has no function {function} when read back.");
    }

    /// <summary>ASCE 7-16 wind on an existing Wind pattern, through ETABS's table (cAutoWind has no methods in ETABS 23).</summary>
    public static void Wind716(ICsiLink e, string pattern, double angle, double speed, string exposure, double kzt, double gust, double kd)
    {
        var (top, bottom, _, _) = Range(e);
        var row = new List<(string, string)> { ("Name", pattern), ("Exposure", "Diaphragms") };
        if (top.Length > 0) { row.Add(("TopStory", top)); row.Add(("BotStory", bottom)); }
        row.AddRange([("Angle", N(angle)), ("WindSpeed", N(speed)), ("ExpType", exposure), ("kzt", N(kzt)), ("GustFact", N(gust)), ("Kd", N(kd))]);
        EditableTables.Put(e, "Load Pattern Definitions - Auto Wind - ASCE 7-16", "Name", [EditableTables.Row(row.ToArray())], $"set {pattern} to ASCE 7-16 wind");
    }
}
