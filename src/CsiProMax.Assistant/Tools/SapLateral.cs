using System.Reflection;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// SAP2000's own calls that give a load pattern its auto lateral code. SAP2000 26 never takes a code through its
/// tables: the patterns' table wipes the load patterns (0.4.22 and 0.4.34 field tests), and a code's own table
/// ("Auto Seismic - IBC2006") takes no row for a pattern that does not have that code yet. It reports 0 errors and
/// keeps nothing (0.4.40 field test: 70 codes). So the code is set by its Set call first, with plain starting
/// values, and the inputs given then go into the code's table row. A code with no Set call (ASCE 7-22, KDS, AASHTO...)
/// can only be chosen in SAP2000's Define > Load Patterns.
/// </summary>
internal static class SapLateral
{
    /// <summary>The Set calls for a code, newest first, by the code as SAP2000 26 names its table.</summary>
    public static IReadOnlyList<string> Setters(string kind, string code)
    {
        var map = kind == CsiProMax.Lateral.LateralCodes.Wind ? Wind : Seismic;
        return map.TryGetValue(Norm(code), out var methods) ? methods : [];
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static Dictionary<string, string[]> Build(params (string Code, string[] Methods)[] items) =>
        items.ToDictionary(i => Norm(i.Code), i => i.Methods);

    private static readonly Dictionary<string, string[]> Seismic = Build(
        ("AS 1170 2007", ["SetAS11702007"]), ("BOCA96", ["SetBOCA96"]), ("Chinese 2010", ["SetChinese2010"]), ("Chinese 2002", ["SetChinese2002"]),
        ("Eurocode8 2004", ["SetEurocode82004_1", "SetEurocode82004"]), ("IBC2003", ["SetIBC2003"]), ("IBC2006", ["SetIBC2006"]),
        ("IBC2009", ["SetIBC2009"]), ("IBC2012", ["SetIBC2012"]), ("ASCE 7-16", ["SetASCE716_1", "SetASCE716"]), ("IS1893:2002", ["SetIS1893_2002"]),
        ("ITALIAN NTC 2018", ["SetNTC2018"]), ("ITALIAN NTC 2008", ["SetNTC2008"]), ("NBCC 2015", ["SetNBCC2015"]), ("NBCC 2010", ["SetNBCC2010"]),
        ("NBCC 2005", ["SetNBCC2005"]), ("NBCC95", ["SetNBCC95"]), ("NEHRP97", ["SetNEHRP97"]),
        ("NZS 1170 2004", ["SetNZS11702004_2", "SetNZS11702004_1", "SetNZS11702004"]), ("UBC94", ["SetUBC94"]), ("UBC97", ["SetUBC97"]),
        ("UBC97 Isolated", ["SetUBC97Iso"]), ("User Coefficient", ["SetUserCoefficient"]), ("User Loads", ["SetUserLoad"]));

    private static readonly Dictionary<string, string[]> Wind = Build(
        ("API 4F", ["SetAPI4F2008_1", "SetAPI4F2008"]), ("API 4F 2013", ["SetAPI4F2013"]), ("ASCE7-88", ["SetASCE788"]), ("ASCE7-95", ["SetASCE795"]),
        ("ASCE7-02", ["SetASCE702"]), ("ASCE7-05", ["SetASCE705"]), ("ASCE7-10", ["SetASCE710"]), ("ASCE 7-16", ["SetASCE716"]),
        ("AS-NZS 1170 2 2002", ["SetASNZS117022002"]), ("AS-NZS 1170 2 2011", ["SetASNZS117022011"]), ("BOCA96", ["SetBOCA96"]),
        ("BS6399-95", ["SetBS639995"]), ("Chinese 2010", ["SetChinese2010"]), ("Chinese 2002", ["SetChinese2002_1", "SetChinese2002"]),
        ("Eurocode1 2005", ["SetEurocode12005_1", "SetEurocode12005"]), ("IS875-1987", ["SetIS8751987"]), ("IS 875-2015", ["SetIS8752015"]),
        ("ITALIAN NTC 2018", ["SetNTC2018", "SetNTC2018_1"]), ("ITALIAN NTC 2008", ["SetNTC2008", "SetNTC2008_1"]), ("Mexican", ["SetMexican"]),
        ("NBCC 2015", ["SetNBCC2015"]), ("NBCC 2010", ["SetNBCC2010_1", "SetNBCC2010"]), ("NBCC 2005", ["SetNBCC2005"]), ("NBCC95", ["SetNBCC95"]),
        ("SP 20.13330.2016", ["SetSP20133302016"]), ("UBC94", ["SetUBC94"]), ("UBC97", ["SetUBC97"]));

    /// <summary>
    /// Plain starting values by parameter name (SAP2000 26's help and its forms' defaults); a parameter not named
    /// here takes 1 (a whole number: the first choice of a list; a factor), false, or "".
    /// </summary>
    internal static readonly Dictionary<string, object> Seeds = new(StringComparer.OrdinalIgnoreCase)
    {
        // directions, period, heights
        ["DirFlag"] = 1, ["Eccen"] = 0.05, ["PeriodFlag"] = 1, ["PFlag"] = 1, ["Ct"] = 0.035, ["CtType"] = 2, ["C1Type"] = 1, ["UserT"] = 0.0,
        ["UserZ"] = false, ["TopZ"] = 0.0, ["BottomZ"] = 0.0, ["MyType"] = 1,
        // ASCE 7 / IBC / NEHRP
        ["R"] = 8.0, ["Omega"] = 3.0, ["Cd"] = 5.5, ["I"] = 1.0, ["Ss"] = 1.0, ["S1"] = 0.4, ["TL"] = 8.0, ["SiteClass"] = 4, ["Fa"] = 1.1, ["Fv"] = 1.9,
        ["Latitude"] = 0.0, ["Longitude"] = 0.0, ["ZipCode"] = "", ["Sg"] = 1, ["SeismicCoeffFlag"] = 1, ["Site"] = 4,
        ["NEHRP97Sg"] = 1, ["NEHRP97SeismicCoeffFlag"] = 1, ["NEHRP97Site"] = 4, ["NEHRP97Ss"] = 1.0, ["NEHRP97S1"] = 0.4, ["NEHRP97Fa"] = 1.1, ["NEHRP97Fv"] = 1.9, ["NEHRP97R"] = 8.0,
        // AS 1170, NZS 1170
        ["AS2007SiteClass"] = 3, ["AS2007Kp"] = 1.0, ["AS2007Z"] = 0.08, ["AS2007Sp"] = 0.77, ["AS2007Mu"] = 2.0,
        ["NZS2004SiteClass"] = 3, ["NZS2004Z"] = 0.4, ["NZS2004R"] = 1.0, ["NZS2004Dist"] = 20.0, ["NZS2004Sp"] = 0.7, ["NZS2004Mu"] = 4.0,
        ["NZS2004ConsiderTSite"] = false, ["NZS2004TSite"] = 0.0, ["NZS2004ConsiderSingleStory"] = false,
        // BOCA, UBC
        ["BOCA96Aa"] = 0.1, ["BOCA96Av"] = 0.1, ["BOCA96S"] = 1.0, ["BOCA96R"] = 8.0,
        ["UBC94ZFlag"] = 1, ["UBC94Z"] = 0.4, ["UBC94S"] = 1.2, ["UBC94I"] = 1.0, ["UBC94Rw"] = 8.0,
        ["UBC97SeismicCoeffFlag"] = 1, ["UBC97SoilProfileType"] = 4, ["UBC97Z"] = 0.4, ["UBC97Ca"] = 0.44, ["UBC97Cv"] = 0.64, ["UBC97NearSourceFlag"] = 1,
        ["UBC97SourceType"] = 1, ["UBC97Dist"] = 10.0, ["UBC97Na"] = 1.0, ["UBC97Nv"] = 1.0, ["UBC97I"] = 1.0, ["UBC97R"] = 8.5,
        ["UBC97IsoSeismicCoeffFlag"] = 1, ["UBC97IsoSoilProfileType"] = 4, ["UBC97IsoZ"] = 0.4, ["UBC97IsoCv"] = 0.64, ["UBC97IsoNearSourceFlag"] = 1,
        ["UBC97IsoSourceType"] = 1, ["UBC97IsoDist"] = 10.0, ["UBC97IsoNv"] = 1.0, ["UBC97IsoRi"] = 2.0, ["UBC97IsoBd"] = 1.0, ["UBC97IsoKDmax"] = 1000.0, ["UBC97IsoKDmin"] = 500.0,
        // Chinese, Eurocode 8, IS 1893
        ["JGJ32002AlphaMax"] = 0.08, ["JGJ32002SI"] = 1, ["JGJ32002DampRatio"] = 0.05, ["JGJ32002Tg"] = 0.35, ["JGJ32002PTDF"] = 1.0, ["EnhancementFactor"] = 1.0,
        ["EURO2004Country"] = 1, ["EURO2004GroundType"] = 1, ["EURO2004SpectrumType"] = 1, ["EURO2004Ag"] = 0.25, ["EURO2004S"] = 1.0,
        ["EURO2004Tb"] = 0.15, ["EURO2004Tc"] = 0.4, ["EURO2004Td"] = 2.0, ["EURO2004Beta"] = 0.2, ["EURO2004Q"] = 4.0, ["EURO2004Lambda"] = 1.0,
        ["INZFlag"] = 1, ["INZ"] = 0.24, ["INS"] = 2, ["INI"] = 1.0, ["INR"] = 5.0,
        // NBCC
        ["PGA"] = 0.3, ["S02"] = 0.6, ["S05"] = 0.4, ["S2"] = 0.1, ["S4"] = 0.05, ["S5"] = 0.04, ["S10"] = 0.02, ["F02"] = 1.0, ["F05"] = 1.0, ["F1"] = 1.0,
        ["F2"] = 1.0, ["F5"] = 1.0, ["F10"] = 1.0, ["Mv"] = 1.0, ["Rd"] = 4.0, ["Ro"] = 1.7, ["Ds"] = 10.0, ["Za"] = 4, ["Zv"] = 4, ["ZvFlag"] = 1, ["Zvr"] = 0.2, ["F"] = 1.0,
        // NTC
        ["ParamsOption"] = 2, ["Island"] = 1, ["LimitState"] = 2, ["UsageClass"] = 2, ["NomLife"] = 50.0, ["PeakAccel"] = 0.25, ["F0"] = 2.5, ["Tcs"] = 0.3,
        ["SpecType"] = 1, ["SoilType"] = 1, ["Topography"] = 1, ["hRatio"] = 1.0, ["Damping"] = 5.0, ["q"] = 4.0, ["lambda"] = 1.0,
        // user
        ["C"] = 0.1, ["K"] = 1.0,
        // wind
        ["ExposureFrom"] = 2, ["DirAngle"] = 0.0, ["Cpw"] = 0.8, ["Cpl"] = 0.5, ["Cp"] = 1.4, ["ASCECase"] = 1, ["ASCEe1"] = 0.15, ["ASCEe2"] = 0.15,
        ["WindSpeed"] = 100.0, ["ExposureType"] = 2, ["ImportanceFactor"] = 1.0, ["Kzt"] = 1.0, ["GustFactor"] = 0.85, ["Kd"] = 0.85, ["SolidGrossRatio"] = 0.2,
        ["UserExposure"] = false, ["UserGust"] = false, ["SSLFactor"] = 1.0, ["ShieldingFactor"] = 1.0, ["Ka"] = 1.0, ["Kc"] = 1.0, ["Kl"] = 1.0, ["Kp"] = 1.0,
        ["Cat"] = 2, ["CycloneRegion"] = false, ["Md"] = 1.0, ["Ms"] = 1.0, ["Mt"] = 1.0, ["Cdyn"] = 1.0, ["Ve"] = 30.0, ["Ca"] = 1.0, ["Cr"] = 1.0,
        ["BuildingWidth"] = 10.0, ["BuildingDepth"] = 10.0, ["Us"] = 1.3, ["UniformTaper"] = false, ["BHoverB0"] = 1.0, ["WZero"] = 0.5, ["Rt"] = 2, ["PhiZOpt"] = 1,
        ["T1Opt"] = 1, ["DampRatio"] = 0.05, ["Terrain"] = 2, ["TerrainCategory"] = 2, ["Orography"] = 1.0, ["K1"] = 1.0, ["K3"] = 1.0, ["CsCd"] = 1.0, ["Rho"] = 1.25,
        ["Class"] = 1, ["Q"] = 0.5, ["TopographicFactor"] = 1.0, ["TerrainType"] = 1, ["CeWindward"] = 1.0, ["CeLeeward"] = 1.0, ["NBCCCase"] = 1, ["e1"] = 0.15, ["e2"] = 0.15,
        ["Vb"] = 27.0, ["vr"] = 27.0, ["ExposureCategory"] = 2, ["ct"] = 1.0, ["cd"] = 1.0, ["cp"] = 1.0,
        ["LoadingType"] = 1, ["StructureType"] = 1, ["LogDecrement"] = 1, ["Zeq"] = 10.0, ["WindDistrict"] = 2, ["WindPressure"] = 0.3, ["LimitFreq"] = 1.0,
        ["ModalCase"] = "MODAL", ["FirstValMode"] = 1,
    };

    /// <summary>
    /// Starting values one Set call needs other than the common ones, from SAP2000 26's API help and the calls a hidden
    /// SAP2000 26 on Ahmed's PC took (2026-11-26): IBC's seismic coefficients are user defined (Option 2, not a zip code);
    /// the Chinese seismic and NZS periods are program calculated (2); the Chinese wind's T1 is user defined.
    /// </summary>
    internal static readonly Dictionary<string, Dictionary<string, object>> MethodSeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SetIBC2006"] = new(StringComparer.OrdinalIgnoreCase) { ["IBC2006Option"] = 2, ["CtType"] = 1, ["Ss"] = 1.5, ["S1"] = 0.6, ["Tl"] = 8.0, ["SiteClass"] = 4 },
        ["SetIBC2009"] = new(StringComparer.OrdinalIgnoreCase) { ["IBC2009Option"] = 2, ["CtType"] = 1, ["Ss"] = 1.5, ["S1"] = 0.6, ["Tl"] = 8.0, ["SiteClass"] = 4 },
        ["SetIBC2012"] = new(StringComparer.OrdinalIgnoreCase) { ["IBC2012Option"] = 2, ["CtType"] = 1, ["Ss"] = 1.5, ["S1"] = 0.6, ["Tl"] = 8.0, ["SiteClass"] = 4 },
        ["SetChinese2010"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PeriodFlag"] = 2, ["JGJ32002AlphaMax"] = 0.16, ["JGJ32002SI"] = 4, ["JGJ32002DampRatio"] = 0.05, ["JGJ32002Tg"] = 0.4,
            // the wind call of the same name
            ["ExposureFrom"] = 1, ["BuildingWidth"] = 12.0, ["Us"] = 0.5, ["BHoverB0"] = 1.0, ["wzero"] = 0.48, ["Rt"] = 3, ["PhiZOpt"] = 1, ["T1Opt"] = 1, ["UserT"] = 0.6, ["DampRatio"] = 0.04,
        },
        ["SetChinese2002"] = new(StringComparer.OrdinalIgnoreCase) { ["PeriodFlag"] = 2, ["JGJ32002AlphaMax"] = 0.16, ["JGJ32002SI"] = 4, ["JGJ32002DampRatio"] = 0.05, ["JGJ32002Tg"] = 0.4, ["T1Opt"] = 1, ["UserT"] = 0.6 },
        ["SetChinese2002_1"] = new(StringComparer.OrdinalIgnoreCase) { ["T1Opt"] = 1, ["UserT"] = 0.6, ["PhiZOpt"] = 1, ["Rt"] = 3 },
        ["SetNZS11702004"] = new(StringComparer.OrdinalIgnoreCase) { ["PeriodFlag"] = 2, ["NZS2004R"] = 1.3, ["NZS2004Mu"] = 3.0 },
        ["SetNZS11702004_1"] = new(StringComparer.OrdinalIgnoreCase) { ["PeriodFlag"] = 2, ["NZS2004R"] = 1.3, ["NZS2004Mu"] = 3.0 },
        ["SetNZS11702004_2"] = new(StringComparer.OrdinalIgnoreCase) { ["PeriodFlag"] = 2, ["NZS2004R"] = 1.3, ["NZS2004Mu"] = 3.0 },
        ["SetASCE716"] = new(StringComparer.OrdinalIgnoreCase) { ["ExposureFrom"] = 1, ["ASCECase"] = 1 },
    };

    /// <summary>The arguments for one Set call: the seeds, and 1 / false / "" for anything not named.</summary>
    internal static Dictionary<string, object?> Arguments(ParameterInfo[] parameters, string pattern, bool[]? directions, string? method = null)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters)
        {
            var name = p.Name ?? "";
            var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
            if (name.Equals("Name", StringComparison.OrdinalIgnoreCase)) args[name] = pattern;
            else if (t == typeof(bool[])) args[name] = directions ?? [true, false];
            else if (method is not null && MethodSeeds.TryGetValue(method, out var own) && own.TryGetValue(name, out var mine)) args[name] = mine;
            else if (Seeds.TryGetValue(name, out var seed)) args[name] = seed;
            else if (p.HasDefaultValue && p.DefaultValue is not DBNull and not null) continue;
            else args[name] = t == typeof(bool) ? false : t == typeof(string) ? "" : t == typeof(double) ? 1.0 : t.IsEnum ? Enum.GetValues(t).GetValue(0) : 1;
        }
        return args;
    }

    /// <summary>
    /// Gives the pattern the code by SAP2000's own Set call, with the inputs given set through the same call (never
    /// through the code's table: SAP2000 26 left 0 of 2 rows in "Auto Seismic - ASCE 7-16" after an import of one
    /// field, 0.4.42 field test). What the pattern has now (its Get call) is kept, then plain starting values, then the
    /// inputs given by the call's parameter names. Returns the call that took it and the Get call's values, or null
    /// when the code has no Set call in this API. Throws when every Set call there is was refused.
    /// </summary>
    public static (string Method, System.Text.Json.Nodes.JsonObject? Back)? SetCode(ICsiLink e, string pattern, string kind, string code, IReadOnlyDictionary<string, string>? values = null)
    {
        var holder = kind == CsiProMax.Lateral.LateralCodes.Wind ? "LoadPatterns.AutoWind." : "LoadPatterns.AutoSeismic.";
        var tried = new List<string>();
        foreach (var method in Setters(kind, code))
        {
            var path = holder + method;
            ParameterInfo[] parameters;
            try { parameters = (ParameterInfo[])e.Raw("read " + path, m => ApiCall.Parameters((object)m, path))!; }
            catch (Exception ex) when (ex is MissingMethodException or ToolInputException or EtabsException) { tried.Add($"{method}: not in this version"); continue; }
            var getter = holder + "Get" + method[3..];
            var had = CsiTools.Try(e, $"read {pattern}'s {code}", getter, CsiTools.D(("Name", pattern)));
            var given = Given(parameters, values ?? new Dictionary<string, string>(), code);
            var hasDirs = parameters.Any(p => (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) == typeof(bool[]));
            bool[][] directions = hasDirs ? [[true, false], [true, false, false, false, false, false], [false, true, false]] : [[true, false]];
            foreach (var args in Variants(parameters, pattern, directions, had, given, method))
            {
                if (CsiTools.Try(e, $"give {pattern} the code {code}", path, args) is null) continue;
                var back = CsiTools.Try(e, $"read {pattern}'s {code}", getter, CsiTools.D(("Name", pattern)));
                return (method, back?["values"] as System.Text.Json.Nodes.JsonObject);
            }
            tried.Add($"{method} refused");
        }
        if (tried.Count == 0) return null;
        throw new EtabsException($"{e.Program.Name} refused to give {pattern} the {kind.ToLowerInvariant()} code {code} ({string.Join("; ", tried)}). In {e.Program.Name}: Define > Load Patterns, choose {code} as {pattern}'s Auto Lateral Load.");
    }

    /// <summary>The inputs given, as the Set call's arguments by its parameter names (the table's names where they differ).</summary>
    internal static Dictionary<string, object?> Given(ParameterInfo[] parameters, IReadOnlyDictionary<string, string> values, string code)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var (key, text) in values)
        {
            var k = key.Trim();
            var named = parameters.Where(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase)).ToList();
            // UBC97Z, NZS2004R, EURO2004Ag...: the code's own prefix in front of the table's name.
            if (named.Count == 0) named = parameters.Where(p => p.Name is { } n && n.Length > k.Length && n.EndsWith(k, StringComparison.OrdinalIgnoreCase) && char.IsDigit(n[n.Length - k.Length - 1])).ToList();
            if (k.Equals("Dir", StringComparison.OrdinalIgnoreCase) && parameters.FirstOrDefault(p => p.Name is "DirFlag") is { } dir)
            {
                args[dir.Name!] = text.Trim().ToUpperInvariant().StartsWith('Y') ? 2 : 1; continue;
            }
            if (named.Count != 1) { unknown.Add(k); continue; }
            var p = named[0];
            var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
            object? v = null;
            if (t == typeof(double) && double.TryParse(text, System.Globalization.NumberStyles.Float, inv, out var d)) v = d;
            else if (t == typeof(int) && int.TryParse(text, System.Globalization.NumberStyles.Integer, inv, out var i)) v = i;
            else if (t == typeof(int) && p.Name!.Contains("Site", StringComparison.OrdinalIgnoreCase) && text.Trim().Length == 1 && char.IsLetter(text.Trim()[0])) v = char.ToUpperInvariant(text.Trim()[0]) - 'A' + 1;
            else if (t == typeof(bool)) v = text.Trim().ToLowerInvariant() is "yes" or "true" or "1";
            else if (t == typeof(string)) v = text;
            if (v is null) throw new ToolInputException($"{code}'s input {k} is a {(t == typeof(int) ? "whole number" : t == typeof(double) ? "number" : t.Name)}, not '{text}'.");
            args[p.Name!] = v;
        }
        if (unknown.Count > 0)
            throw new ToolInputException($"SAP2000's call for {code} has no input {string.Join(", ", unknown)}. Its inputs: {string.Join(", ", parameters.Select(p => p.Name).Where(n => n is not "Name"))}.");
        return args;
    }

    /// <summary>
    /// The argument lists to try, best first: what the pattern has (or the starting values) with the inputs given;
    /// then, when SAP2000 refuses, each whole-number choice the inputs did not give, tried at 0, 2, 3 and 4 in turn
    /// (an option such as IBC2006Option or JGJ32002SI whose 1 SAP2000 does not take: 0.4.42 field test).
    /// </summary>
    internal static IEnumerable<Dictionary<string, object?>> Variants(ParameterInfo[] parameters, string pattern, bool[][] directions, System.Text.Json.Nodes.JsonObject? had, Dictionary<string, object?> given, string? method = null)
    {
        foreach (var dirs in directions)
        {
            var args = Arguments(parameters, pattern, dirs, method);
            if (had?["values"] is System.Text.Json.Nodes.JsonObject now)
                foreach (var p in parameters)
                    if (p.Name is { } n && now[n] is System.Text.Json.Nodes.JsonValue v && Convert(v, p) is { } value) args[n] = value;
            foreach (var (k, v) in given) args[k] = v;
            yield return args;
            foreach (var p in parameters.Where(p => p.Name is { } n && !given.ContainsKey(n) && n is not ("Name" or "DirFlag") && (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) == typeof(int)))
                foreach (var choice in new[] { 0, 2, 3, 4 })
                {
                    if (Equals(args[p.Name!], choice)) continue;
                    var other = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase) { [p.Name!] = choice };
                    yield return other;
                }
        }
    }

    private static object? Convert(System.Text.Json.Nodes.JsonValue v, ParameterInfo p)
    {
        var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        if (t == typeof(double)) return v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : null;
        if (t == typeof(int)) return v.TryGetValue<int>(out var i2) ? i2 : v.TryGetValue<double>(out var d2) && d2 == Math.Floor(d2) ? (int)d2 : null;
        if (t == typeof(bool)) return v.TryGetValue<bool>(out var b) ? b : null;
        if (t == typeof(string)) return v.TryGetValue<string>(out var s) ? s : null;
        return null;
    }

    /// <summary>Takes the code load off the pattern by SAP2000's SetNone.</summary>
    public static void SetNone(ICsiLink e, string pattern, string kind)
    {
        var path = kind == CsiProMax.Lateral.LateralCodes.Wind ? "LoadPatterns.AutoWind.SetNone" : "LoadPatterns.AutoSeismic.SetNone";
        CsiTools.Call(e, $"take the code load off {pattern}", path, CsiTools.D(("Name", pattern)));
    }
}
