using System.Globalization;

namespace CsiProMax.Model;

/// <summary>
/// SAP2000 26.3's own function tables (Define > Functions: Response Spectrum by each code, From File and User; Time
/// History of each type; Power Spectral Density; Steady State), every field by SAP2000's table and field names. A
/// function made in the Engine's SAP2000 forms keeps its rows here, in <see cref="StructuralModel.DefinitionTables"/>,
/// as SAP2000 exports them: the first row of a function has its parameters and first point, the next rows one point
/// each. The .s2k reader keeps these rows as the file has them and the writer writes them back the same, so a code
/// spectrum goes back to SAP2000 as that code with its parameters, not as user points. A spectrum or time history is
/// also an ordinary function of the model (its points), so load cases and the other programs see it; power spectral
/// density and steady state functions are SAP2000's only and live in these rows alone.
/// </summary>
public static class SapFunctions
{
    public const string Prefix = "Function - ";

    /// <summary>Every SAP2000 26.3 function table and its fields (table and field keys read on Ahmed's PC).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Tables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Function - Power Spectral Density - From File"] = ["Name", "FreqUnits", "Frequency", "Value", "FileName", "HeaderLines", "PrefixChars", "PtsPerLine", "DataType", "FormatType", "FixedLength", "Interval"],
        ["Function - Power Spectral Density - User"] = ["Name", "Frequency", "Value"],
        ["Function - Response Spectrum - AASHTO 2006"] = ["Name", "Period", "Accel", "FuncDamp", "A", "SoilType"],
        ["Function - Response Spectrum - AASHTO 2007"] = ["Name", "Period", "Accel", "FuncDamp", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "PGA", "SiteClass", "Fa", "Fv", "Fpga", "SDS", "SD1"],
        ["Function - Response Spectrum - AASHTO 2012"] = ["Name", "Period", "Accel", "FuncDamp", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "PGA", "SiteClass", "Fa", "Fv", "Fpga", "SDS", "SD1"],
        ["Function - Response Spectrum - AS 1170 2007"] = ["Name", "Period", "Accel", "FuncDamp", "SiteClass", "ProbFact", "HazFact", "StPerFact", "StDucFact"],
        ["Function - Response Spectrum - BOCA96"] = ["Name", "Period", "Accel", "FuncDamp", "Aa", "Av", "R", "S"],
        ["Function - Response Spectrum - Chinese 2010"] = ["Name", "Period", "Accel", "FuncDamp", "AlphaMax", "SI", "Tg", "PTDF"],
        ["Function - Response Spectrum - CJJ1662011"] = ["Name", "Period", "Accel", "FuncDamp", "A", "Tg", "Direction"],
        ["Function - Response Spectrum - GB501112006"] = ["Name", "Period", "Accel", "FuncDamp", "A", "SI", "Tg"],
        ["Function - Response Spectrum - JTGTB02-2013"] = ["Name", "Period", "Accel", "FuncDamp", "A", "Tg", "Direction", "Ci", "Cs"],
        ["Function - Response Spectrum - JTGT2231-01-2020"] = ["Name", "Period", "Accel", "FuncDamp", "A", "Tg", "Ci", "Cs"],
        ["Function - Response Spectrum - EuroCode8"] = ["Name", "Period", "Accel", "FuncDamp", "Ag", "SoilClass", "DampFactor"],
        ["Function - Response Spectrum - EuroCode8 2004"] = ["Name", "Period", "Accel", "FuncDamp", "Country", "Direction", "Ag", "SpecType", "GroundType", "SoilFact", "AvgOverAg", "Tb", "Tc", "Td", "LBFact", "BehaveFact"],
        ["Function - Response Spectrum - From File"] = ["Name", "Frequency", "Period", "Accel", "FuncDamp", "FileName", "HeaderLines", "DataType"],
        ["Function - Response Spectrum - IBC2003"] = ["Name", "Period", "Accel", "FuncDamp", "SDS", "SD1"],
        ["Function - Response Spectrum - IBC2006"] = ["Name", "Period", "Accel", "FuncDamp", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1"],
        ["Function - Response Spectrum - IBC2009"] = ["Name", "Period", "Accel", "FuncDamp", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1"],
        ["Function - Response Spectrum - IBC2012"] = ["Name", "Period", "Accel", "FuncDamp", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1"],
        ["Function - Response Spectrum - ASCE 7-16"] = ["Name", "Period", "Accel", "FuncDamp", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1"],
        ["Function - Response Spectrum - ASCE 7-22"] = ["Name", "Period", "Accel", "FuncDamp", "Direction", "SpecType", "SiteClass", "Sds", "Sd1", "Sms", "Sm1", "TL"],
        ["Function - Response Spectrum - IS1893:2002"] = ["Name", "Period", "Accel", "FuncDamp", "Z", "SoilType"],
        ["Function - Response Spectrum - IS 1893:2016"] = ["Name", "Period", "Accel", "FuncDamp", "SeismicZone", "Z", "I", "SoilType", "R"],
        ["Function - Response Spectrum - Italian 3274"] = ["Name", "Period", "Accel", "FuncDamp", "PGA", "SoilType", "StructFact", "SpecLevel", "Direction", "StructType"],
        ["Function - Response Spectrum - Italian NTC 2018"] = ["Name", "FuncDamp", "Period", "Accel", "AccOption", "Latitude", "Longitude", "Island", "agOverg", "F0", "Tcs", "LimitState", "UsageClass", "NominalLife", "SpectrumType", "SoilType", "Topography", "hoverH", "Tb", "Tc", "Td", "Damping", "BehaviorFactor"],
        ["Function - Response Spectrum - Italian NTC 2008"] = ["Name", "FuncDamp", "Period", "Accel", "AccOption", "Latitude", "Longitude", "Island", "agOverg", "F0", "Tcs", "LimitState", "UsageClass", "NominalLife", "SpectrumType", "SoilType", "Topography", "hoverH", "Tb", "Tc", "Td", "Damping", "BehaviorFactor"],
        ["Function - Response Spectrum - TCVN 9386-2012"] = ["Name", "Period", "Accel", "FuncDamp", "Direction", "Ag", "GroundType", "SoilFact", "AvgOverAg", "Tb", "Tc", "Td", "LBFact", "BehaveFact"],
        ["Function - Response Spectrum - NBCC 2020"] = ["Name", "Period", "Accel", "FuncDamp", "Sa02", "Sa05", "Sa1", "Sa2", "Sa5", "Sa10"],
        ["Function - Response Spectrum - NBCC 2015"] = ["Name", "Period", "Accel", "FuncDamp", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "Sa5", "Sa10", "SiteClass", "F02", "F05", "F1", "F2", "F5", "F10"],
        ["Function - Response Spectrum - NBCC 2010"] = ["Name", "Period", "Accel", "FuncDamp", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "SiteClass", "Fa", "Fv"],
        ["Function - Response Spectrum - NBCC 2005"] = ["Name", "Period", "Accel", "FuncDamp", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "SiteClass", "Fa", "Fv"],
        ["Function - Response Spectrum - NBCC95"] = ["Name", "Period", "Accel", "FuncDamp", "V", "Za", "Zv"],
        ["Function - Response Spectrum - NCHRP 20-07"] = ["Name", "Period", "Accel", "FuncDamp", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "SiteClass", "Fa", "Fv", "SDS", "SD1"],
        ["Function - Response Spectrum - NEHRP97"] = ["Name", "Period", "Accel", "FuncDamp", "SDS", "SD1"],
        ["Function - Response Spectrum - NZS 1170.5-2016"] = ["Name", "Period", "Accel", "FuncDamp", "SiteClass", "Z", "R", "D", "ConsTSite", "TSite", "SpecType"],
        ["Function - Response Spectrum - NZS4203"] = ["Name", "Period", "Accel", "FuncDamp", "ScaleFactor", "SoilCat"],
        ["Function - Response Spectrum - TSC-2007"] = ["Name", "Period", "Accel", "FuncDamp", "SeismicZone", "SiteClass", "TSC07A0", "TSC07I", "TSC07R"],
        ["Function - Response Spectrum - TSC-2018"] = ["Name", "Period", "Accel", "FuncDamp", "SpecDir", "Ss", "S1", "TL", "SiteClass", "Fs", "F1", "SDS", "SD1", "R", "D", "I"],
        ["Function - Response Spectrum - UBC94"] = ["Name", "Period", "Accel", "FuncDamp", "Z", "SoilType"],
        ["Function - Response Spectrum - UBC97"] = ["Name", "Period", "Accel", "FuncDamp", "Ca", "Cv"],
        ["Function - Response Spectrum - SI 413(1995)"] = ["Name", "Period", "Accel", "FuncDamp", "Z", "I", "K", "SoilType"],
        ["Function - Response Spectrum - Argentina INPRES-CIRSOC 103"] = ["Name", "Period", "Accel", "FuncDamp", "Z", "SoilType", "Category", "R"],
        ["Function - Response Spectrum - Chile Norma NCh433+DS61"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "SoilType", "Category"],
        ["Function - Response Spectrum - Chile Norma NCh2369-2003"] = ["Name", "Period", "Accel", "FuncDamp", "R", "Si", "Category", "Zone", "SoilType"],
        ["Function - Response Spectrum - Colombia NSR-10"] = ["Name", "Period", "Accel", "FuncDamp", "Aa", "Av", "Ae", "Ad", "GroupUse", "Fa", "Fv"],
        ["Function - Response Spectrum - Ecuador NEC-11 Capitulo 2"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Coeff", "Fa", "Fd", "SoilType", "FS", "I", "R"],
        ["Function - Response Spectrum - Guatemala AGIES NSE 2-10"] = ["Name", "Period", "Accel", "FuncDamp", "I0", "Scr", "S1r", "FS", "Dh", "TS", "Fa", "Fv", "R"],
        ["Function - Response Spectrum - Mexico NTC 2004"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Group", "Behavior", "Correction"],
        ["Function - Response Spectrum - Peru Norma E.030"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Category", "SoilType", "Configuration", "R"],
        ["Function - Response Spectrum - Dominican Republic R-001"] = ["Name", "Period", "Accel", "FuncDamp", "Ss", "S1", "Fa", "Fv", "Location", "Classification", "R"],
        ["Function - Response Spectrum - Venezuela COVENIN 1756-2:2001"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Form", "Correction", "Classification", "R"],
        ["Function - Response Spectrum - Korean KBC 2009"] = ["Name", "Period", "Accel", "FuncDamp", "Ss", "S1", "SiteClass", "Fa", "Fv", "SDS", "SD1"],
        ["Function - Response Spectrum - KBC 2016"] = ["Name", "Period", "Accel", "FuncDamp", "SoilDepth", "Ss", "S1", "SiteClass", "Fa", "Fv", "SDS", "SD1"],
        ["Function - Response Spectrum - KDS 41 17 00-2019"] = ["Name", "Period", "Accel", "FuncDamp", "SeismicZone", "SWave", "S", "SiteClass", "Fa", "Fv", "TL", "SDS", "SD1"],
        ["Function - Response Spectrum - Mexico CFE-93"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Classification", "SoilType", "BehaviorFactor", "CorrectionFactor"],
        ["Function - Response Spectrum - Peru NTE E.030 2014"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Category", "SoilType", "Ia", "Ip", "R0"],
        ["Function - Response Spectrum - Mexico CFE-2008"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Classification", "SoilType", "BehaviorFactor", "CorrectionFactor"],
        ["Function - Response Spectrum - Ecuador Norma NEC-SE-DS 2015"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "Coeff", "Fa", "Fd", "SoilType", "FS", "I", "R"],
        ["Function - Response Spectrum - Costa Rica Seismic Code 2010"] = ["Name", "Period", "Accel", "FuncDamp", "Zone", "SoilType", "Occupancy", "Overstrength", "OSFactor"],
        ["Function - Response Spectrum - SP 14_13330_2014"] = ["Name", "Period", "Accel", "FuncDamp", "StructDir", "Seismicity", "SoilCat", "K0Factor", "K1Factor", "KPsiFactor", "NonlinSoil", "ASoil"],
        ["Function - Response Spectrum - User"] = ["Name", "Period", "Accel", "FuncDamp"],
        ["Function - Steady State - From File"] = ["Name", "FreqUnits", "Frequency", "Value", "FileName", "HeaderLines", "PrefixChars", "PtsPerLine", "DataType", "FormatType", "FixedLength", "Interval"],
        ["Function - Steady State - User"] = ["Name", "Frequency", "Value"],
        ["Function - Time History - Cosine"] = ["Name", "Time", "Value", "Period", "StepsPerCyc", "NumCycles", "Amplitude"],
        ["Function - Time History - From File"] = ["Name", "Time", "Value", "FileName", "HeaderLines", "PrefixChars", "PtsPerLine", "DataType", "FormatType", "FixedLength", "Interval"],
        ["Function - Time History - Matched to Response Spectrum"] = ["Name", "Time", "Value", "TargetRS", "RSAccUnits", "RefAccTH", "RefTHAccUnits", "SpecMatchMethod", "FreqRangeOption", "FreqF1", "FreqF2", "nRecur", "RecurFact"],
        ["Function - Time History - Ramp"] = ["Name", "Time", "Value", "RampTime", "Amplitude", "MaxTime"],
        ["Function - Time History - Sawtooth"] = ["Name", "Time", "Value", "Period", "RampTime", "NumCycles", "Amplitude"],
        ["Function - Time History - Sine"] = ["Name", "Time", "Value", "Period", "StepsPerCyc", "NumCycles", "Amplitude"],
        ["Function - Time History - Triangular"] = ["Name", "Time", "Value", "Period", "NumCycles", "Amplitude"],
        ["Function - Time History - User"] = ["Name", "Time", "Value"],
        ["Function - Time History - User Periodic"] = ["Name", "Time", "Value", "NumCycles"],
    };

    /// <summary>The full table name as <see cref="Tables"/> spells it, or null when it is no SAP2000 function table.</summary>
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
        "spectrum" => ("Period", "Accel"),
        "history" => ("Time", "Value"),
        _ => ("Frequency", "Value"),
    };

    /// <summary>The type name in the form's "Choose Function Type to Add" sense: the table name after its kind.</summary>
    public static string TypeOf(string table)
    {
        var parts = table.Split(" - ", 3);
        return parts.Length == 3 ? parts[2] : table;
    }

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

    /// <summary>Takes a function out of every SAP2000 function table; true when it was in one.</summary>
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
    /// the first point, each other point on a row of its own, as SAP2000 exports them. A field the table does not have
    /// is refused.</summary>
    public static void Set(StructuralModel m, string table, string name, IReadOnlyDictionary<string, string> parameters, IReadOnlyList<(double X, double Y)> points)
    {
        var full = Table(table) ?? throw new ArgumentException($"There is no SAP2000 function table '{table}'.");
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
            // SAP2000's exports carry the spectrum damping on every row.
            if (i > 0 && parameters.TryGetValue("FuncDamp", out var damp) && damp.Length > 0) r["FuncDamp"] = damp;
            rows.Add(fields.Where(r.ContainsKey).ToDictionary(f => f, f => r[f], StringComparer.Ordinal));
        }
        m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
        var list = m.DefinitionTables.TryGetValue(full, out var have) ? have : m.DefinitionTables[full] = [];
        list.AddRange(rows);
    }

    /// <summary>The power spectral density and steady state functions (SAP2000's only), as functions of the model.</summary>
    public static List<FunctionDef> Others(StructuralModel m) =>
        (m.DefinitionTables ?? []).Where(t => IsTable(t.Key) && KindOf(t.Key) is "psd" or "steady")
            .SelectMany(t => t.Value.Select(r => r.GetValueOrDefault("Name") ?? "").Distinct().Where(n => n.Length > 0).Select(n => new FunctionDef(n, KindOf(t.Key), Points(m, t.Key, n))))
            .ToList();

    private static double Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
