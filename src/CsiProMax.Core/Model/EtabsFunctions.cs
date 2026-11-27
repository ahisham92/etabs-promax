using System.Globalization;

namespace CsiProMax.Model;

/// <summary>
/// ETABS 23.2's own function tables (Define > Functions: Response Spectrum by each code, From File and User Defined; Time
/// History of each type; Power Spectral Density and Steady State), every field by ETABS's table and field names (read on
/// Ahmed's PC, table-keys/etabs-23.2-table-keys.txt). A function made in the Engine's ETABS forms keeps its rows here, in
/// <see cref="StructuralModel.DefinitionTables"/>, as ETABS's tables hold them: the first row of a function has its
/// parameters and first point, the next rows one point each (a spectrum's damping ratio on every row). The .e2k has no
/// line for a code's parameters, so these rows go to ETABS through its tables after the file opens (ModelerTables): the
/// function goes back to ETABS as that code or type with every parameter, not as user points. The function stays an
/// ordinary function of the model too (its points), so load cases and the other programs see it.
/// </summary>
public static class EtabsFunctions
{
    public const string Prefix = "Functions - ";

    /// <summary>Every ETABS 23.2 function table and its fields (GUID left out: ETABS makes it).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Tables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Functions - Power Spectral Density - From File"] = ["Name", "Frequency", "Value", "FreqUnits", "Filename", "HeaderSkip", "PrefixSkip", "NumPoints", "ValueType", "EqualInt", "FormatType", "CharPerItem"],
        ["Functions - Power Spectral Density - User Defined"] = ["Name", "Frequency", "Value"],
        ["Functions - Response Spectrum - Argentina INPRES-CIRSOC 103"] = ["Name", "Period", "Value", "Zone", "SoilType", "OccCat", "R", "DampRatio"],
        ["Functions - Response Spectrum - AS1170-2007"] = ["Name", "Period", "Value", "SoilClass", "kp", "Z", "Sp", "u", "DampRatio"],
        ["Functions - Response Spectrum - AS1170-2024"] = ["Name", "Period", "Value", "SoilClass", "kp", "Z", "Sp", "u", "DampRatio"],
        ["Functions - Response Spectrum - ASCE7-02"] = ["Name", "Period", "Value", "Sds", "Sd1", "DampRatio"],
        ["Functions - Response Spectrum - ASCE7-05"] = ["Name", "Period", "Value", "SsAndS1From", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - ASCE7-10"] = ["Name", "Period", "Value", "SsAndS1From", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - ASCE7-16"] = ["Name", "Period", "Value", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - ASCE7-22"] = ["Name", "Period", "Value", "Direction", "Type", "SDS", "SD1", "SMS", "SM1", "TL", "SiteClass", "DampRatio"],
        ["Functions - Response Spectrum - Chile Norma NCh2369-2003"] = ["Name", "Period", "Value", "R", "DampParam", "BuildCat", "Zone", "SoilType", "DampRatio"],
        ["Functions - Response Spectrum - Chile Norma NCh433 DS61"] = ["Name", "Period", "Value", "Zone", "SoilType", "OccCat", "DampRatio"],
        ["Functions - Response Spectrum - Chinese GB50011-2010"] = ["Name", "Period", "Value", "AlphaMax", "SI", "Tg", "PTDF", "DampRatio"],
        ["Functions - Response Spectrum - Columbia NSR-10"] = ["Name", "Period", "Value", "Aa", "Av", "Ae", "Ad", "GroupOfUse", "Fa", "Fv", "DampRatio"],
        ["Functions - Response Spectrum - Costa Rica Seismic Code 2010"] = ["Name", "Period", "Value", "Zone", "SoilType", "Occupancy", "DuctOver", "OverFactor", "DampRatio"],
        ["Functions - Response Spectrum - Dominican Republic R-001"] = ["Name", "Period", "Value", "Ss", "S1", "Fa", "Fv", "NearField", "Class", "R", "DampRatio"],
        ["Functions - Response Spectrum - Ecuador NEC-11 Capitulo 2"] = ["Name", "Period", "Value", "Z", "Eta", "Fa", "Fd", "SoilType", "Fs", "I", "R", "DampRatio"],
        ["Functions - Response Spectrum - Ecuador Norma NEC-SE-DS 2015"] = ["Name", "Period", "Value", "Z", "Eta", "Fa", "Fd", "SoilType", "Fs", "I", "R", "DampRatio"],
        ["Functions - Response Spectrum - Eurocode8"] = ["Name", "Period", "Value", "Ag", "SoilClass", "n", "DampRatio"],
        ["Functions - Response Spectrum - Eurocode8-2004"] = ["Name", "Period", "Value", "Country", "Direction", "Ag", "SpecType", "MSpecType", "GroundType", "SGroundType", "S", "AvgOverAg", "Tb", "Tc", "Td", "Beta", "q", "DampRatio"],
        ["Functions - Response Spectrum - From File"] = ["Name", "Period", "Value", "Filename", "HeaderSkip", "ValueType", "DampRatio"],
        ["Functions - Response Spectrum - Guatemala AGIES NSE 2-10"] = ["Name", "Period", "Value", "IO", "Scr", "S1r", "Fs", "Dh", "TS", "Fa", "Fv", "R", "DampRatio"],
        ["Functions - Response Spectrum - IS1893 2002"] = ["Name", "Period", "Value", "Z", "SoilType", "DampRatio"],
        ["Functions - Response Spectrum - IS1893 2016"] = ["Name", "Period", "Value", "Zone", "I", "SoilType", "R", "DampRatio"],
        ["Functions - Response Spectrum - Italian 3274"] = ["Name", "Period", "Value", "PGA", "SoilType", "StructFact", "LevDirStr", "DampRatio"],
        ["Functions - Response Spectrum - Italian NTC2008"] = ["Name", "Period", "Value", "Source", "Latitude", "Longitude", "IslandName", "LimitState", "UsageClass", "NominalLife", "PGA", "F0", "TcStar", "SpecType", "SoilType", "Topography", "hOverH", "Tb", "Tc", "Td", "Xi", "q", "DampRatio"],
        ["Functions - Response Spectrum - Italian NTC2018"] = ["Name", "Period", "Value", "Source", "Latitude", "Longitude", "IslandName", "LimitState", "UsageClass", "NominalLife", "PGA", "F0", "TcStar", "SpecType", "SoilType", "Topography", "hOverH", "Tb", "Tc", "Td", "Xi", "q", "DampRatio"],
        ["Functions - Response Spectrum - KDS 41 17 00-2019"] = ["Name", "Period", "Value", "Zone", "S", "S-Wave", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - KDS 41 17 00-2024"] = ["Name", "Period", "Value", "Zone", "S", "S-Wave", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - Korean KBC 2009"] = ["Name", "Period", "Value", "Ss", "S1", "SiteClass", "Fa", "Fv", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - Korean KBC 2016"] = ["Name", "Period", "Value", "Ss", "S1", "Depth", "SiteClass", "Fa", "Fv", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - Mexico CFE-2008"] = ["Name", "Period", "Value", "Z", "Class", "S", "Q", "I", "DampRatio"],
        ["Functions - Response Spectrum - Mexico CFE-93"] = ["Name", "Period", "Value", "Z", "Class", "S", "Q", "I", "DampRatio"],
        ["Functions - Response Spectrum - Mexico NTC-2004"] = ["Name", "Period", "Value", "Zone", "Group", "Behavior", "Correction", "DampRatio"],
        ["Functions - Response Spectrum - NBCC2005"] = ["Name", "Period", "Value", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "SiteClass", "Fa", "Fv", "DampRatio"],
        ["Functions - Response Spectrum - NBCC2010"] = ["Name", "Period", "Value", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "SiteClass", "Fa", "Fv", "DampRatio"],
        ["Functions - Response Spectrum - NBCC2015"] = ["Name", "Period", "Value", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "Sa5", "Sa10", "SiteClass", "F02", "F05", "F1", "F2", "F5", "F10", "DampRatio"],
        ["Functions - Response Spectrum - NBCC2020"] = ["Name", "Period", "Value", "Sa02", "Sa05", "Sa1", "Sa2", "Sa5", "Sa10", "DampRatio"],
        ["Functions - Response Spectrum - NBCC2025"] = ["Name", "Period", "Value", "Sa02", "Sa05", "Sa1", "Sa2", "Sa5", "Sa10", "DampRatio"],
        ["Functions - Response Spectrum - NBCC95"] = ["Name", "Period", "Value", "V", "Za", "Zv", "DampRatio"],
        ["Functions - Response Spectrum - NEHRP97"] = ["Name", "Period", "Value", "Sds", "Sd1", "DampRatio"],
        ["Functions - Response Spectrum - NZS1170-2004"] = ["Name", "Period", "Value", "SoilClass", "Z", "R", "D", "ConsTSite", "SitePeriod", "DampRatio"],
        ["Functions - Response Spectrum - NZS4203"] = ["Name", "Period", "Value", "SF", "SoilCat", "DampRatio"],
        ["Functions - Response Spectrum - Peru Norma E030"] = ["Name", "Period", "Value", "Zone", "OccCat", "SoilType", "Config", "R", "DampRatio"],
        ["Functions - Response Spectrum - Peru NTE E030 2014"] = ["Name", "Period", "Value", "Zone", "OccCat", "SoilType", "Ia", "Ip", "R0", "DampRatio"],
        ["Functions - Response Spectrum - SI 413"] = ["Name", "Period", "Value", "Z", "SoilType", "I", "K", "DampRatio"],
        ["Functions - Response Spectrum - SNIP KR 20-02:2018"] = ["Name", "Period", "Value", "Direction", "AgOverg", "AgvOverg", "k", "SoilType", "Tc", "Beta", "q", "DampRatio"],
        ["Functions - Response Spectrum - SP 14 13330 2014"] = ["Name", "Period", "Value", "Direction", "Seismicity", "SoilCat", "K0", "K1", "Kpsi", "NonlinSoil", "Asoil", "DampRatio"],
        ["Functions - Response Spectrum - TCVN 9386 2012"] = ["Name", "Period", "Value", "Direction", "Ag", "GroundType", "S", "AvgOverAg", "Tb", "Tc", "Td", "Beta", "q", "DampRatio"],
        ["Functions - Response Spectrum - TSC-2007"] = ["Name", "Period", "Value", "Zone", "Ao", "I", "SiteClass", "R", "DampRatio"],
        ["Functions - Response Spectrum - TSC-2018"] = ["Name", "Period", "Value", "Ss", "S1", "TL", "R", "D", "I", "SiteClass", "Fs", "F1", "Direction", "SDS", "SD1", "DampRatio"],
        ["Functions - Response Spectrum - UBC 97"] = ["Name", "Period", "Value", "Ca", "Cv", "DampRatio"],
        ["Functions - Response Spectrum - User Defined"] = ["Name", "Period", "Value", "DampRatio"],
        ["Functions - Response Spectrum - Venezuela COVENIN 1756-2 2001"] = ["Name", "Period", "Value", "Zone", "SpecForm", "CorrFactor", "Class", "R", "DampRatio"],
        ["Functions - Steady State - From File"] = ["Name", "Frequency", "Value", "FreqUnits", "Filename", "HeaderSkip", "PrefixSkip", "NumPoints", "ValueType", "EqualInt", "FormatType", "CharPerItem"],
        ["Functions - Steady State - User Defined"] = ["Name", "Frequency", "Value"],
        ["Functions - Time History - Cosine"] = ["Name", "Time", "Value", "Period", "StepsPerCyc", "NumCycles", "Amplitude"],
        ["Functions - Time History - From File"] = ["Name", "Time", "Value", "Filename", "HeaderSkip", "PrefixSkip", "NumPoints", "ValueType", "EqualInt", "FormatType", "CharPerItem"],
        ["Functions - Time History - Matched to Response Spectrum"] = ["Name", "Time", "Value", "SpecMatchMethod", "TgtRS", "TgtRSUnits", "RefTH", "RefTHUnits", "FreqRangeOpt", "f1", "f2", "nRecursions", "RecurFactor"],
        ["Functions - Time History - Ramp"] = ["Name", "Time", "Value", "RampTime", "Amplitude", "MaxTime"],
        ["Functions - Time History - Sawtooth"] = ["Name", "Time", "Value", "Period", "RampTime", "NumCycles", "Amplitude"],
        ["Functions - Time History - Sine"] = ["Name", "Time", "Value", "Period", "StepsPerCyc", "NumCycles", "Amplitude"],
        ["Functions - Time History - Triangular"] = ["Name", "Time", "Value", "Period", "NumCycles", "Amplitude"],
        ["Functions - Time History - User Defined"] = ["Name", "Time", "Value"],
        ["Functions - Time History - User Periodic"] = ["Name", "Time", "Value", "NumCycles"],
    };

    /// <summary>The full table name as <see cref="Tables"/> spells it, or null when it is no ETABS function table.</summary>
    public static string? Table(string name) => Tables.Keys.FirstOrDefault(k => k.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsTable(string name) => Table(name) is not null;

    /// <summary>spectrum, history, psd or steady.</summary>
    public static string KindOf(string table) =>
        table.Contains("Response Spectrum", StringComparison.OrdinalIgnoreCase) ? "spectrum"
        : table.Contains("Time History", StringComparison.OrdinalIgnoreCase) ? "history"
        : table.Contains("Power Spectral", StringComparison.OrdinalIgnoreCase) ? "psd" : "steady";

    /// <summary>The table's two point fields (x, y).</summary>
    public static (string X, string Y) PointFields(string table) => KindOf(table) switch
    {
        "spectrum" => ("Period", "Value"),
        "history" => ("Time", "Value"),
        _ => ("Frequency", "Value"),
    };

    /// <summary>The table a function's rows are in, or null.</summary>
    public static string? Find(StructuralModel m, string name) =>
        (m.DefinitionTables ?? []).Where(t => IsTable(t.Key)).FirstOrDefault(t => t.Value.Exists(r => r.GetValueOrDefault("Name") == name)).Key;

    /// <summary>A function's rows (its table's), or empty.</summary>
    public static List<Dictionary<string, string>> Rows(StructuralModel m, string table, string name) =>
        (m.DefinitionTables?.GetValueOrDefault(table) ?? []).Where(r => r.GetValueOrDefault("Name") == name).ToList();

    /// <summary>The function's parameters: its first row less the point fields.</summary>
    public static Dictionary<string, string> Parameters(StructuralModel m, string table, string name)
    {
        var (x, y) = PointFields(table);
        return (Rows(m, table, name).FirstOrDefault() ?? []).Where(kv => kv.Key != "Name" && kv.Key != x && kv.Key != y).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>The points of a function's rows.</summary>
    public static List<(double X, double Y)> Points(StructuralModel m, string table, string name)
    {
        var (x, y) = PointFields(table);
        return Rows(m, table, name).Select(r => (Num(r.GetValueOrDefault(x)), Num(r.GetValueOrDefault(y)))).Where(p => !double.IsNaN(p.Item1) && !double.IsNaN(p.Item2)).ToList();
    }

    /// <summary>Takes a function out of every ETABS function table; true when it was in one.</summary>
    public static bool Remove(StructuralModel m, string name)
    {
        if (m.DefinitionTables is null) return false;
        var any = false;
        foreach (var t in m.DefinitionTables.Keys.Where(IsTable).ToList())
        {
            any |= m.DefinitionTables[t].RemoveAll(r => r.GetValueOrDefault("Name") == name) > 0;
            if (m.DefinitionTables[t].Count == 0) m.DefinitionTables.Remove(t);
        }
        if (m.DefinitionTables.Count == 0) m.DefinitionTables = null;
        return any;
    }

    /// <summary>Sets a function's rows in a table (taking it out of the others): the parameters go on the first row with
    /// the first point, each other point on a row of its own; a spectrum's damping ratio goes on every row. A field the
    /// table does not have is refused.</summary>
    public static void Set(StructuralModel m, string table, string name, IReadOnlyDictionary<string, string> parameters, IReadOnlyList<(double X, double Y)> points)
    {
        var full = Table(table) ?? throw new ArgumentException($"There is no ETABS function table '{table}'.");
        var fields = Tables[full];
        var (x, y) = PointFields(full);
        foreach (var k in parameters.Keys)
            if (!fields.Contains(k, StringComparer.Ordinal) || k == "Name" || k == x || k == y) throw new ArgumentException($"The table \"{full}\" has no field \"{k}\" to set (its fields: {string.Join(", ", fields)}).");
        if (points.Count == 0) throw new ArgumentException($"The function {name} has no points.");
        Remove(m, name);
        var rows = new List<Dictionary<string, string>>();
        for (var i = 0; i < points.Count; i++)
        {
            var r = new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = name, [x] = N(points[i].X), [y] = N(points[i].Y) };
            if (i == 0) foreach (var (k, v) in parameters) if (v.Length > 0) r[k] = v;
            if (i > 0 && parameters.TryGetValue("DampRatio", out var damp) && damp.Length > 0) r["DampRatio"] = damp;
            rows.Add(fields.Where(r.ContainsKey).ToDictionary(f => f, f => r[f], StringComparer.Ordinal));
        }
        m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
        var list = m.DefinitionTables.TryGetValue(full, out var have) ? have : m.DefinitionTables[full] = [];
        list.AddRange(rows);
    }

    private static double Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
