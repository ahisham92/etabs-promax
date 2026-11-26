using System.Globalization;

namespace CsiProMax.Model;

/// <summary>
/// The program's own database tables behind a definition's forms (Define > Material Properties and its Design, Nonlinear,
/// Damping and Time Dependent forms): ETABS 23's table and field names, read from its "Table and Field Keys" (2026-11-26),
/// so what the Modeler's forms hold goes into ETABS, SAP2000 and SAFE under the names each program has. A definition
/// keeps its rows in <see cref="Definition.Tables"/>, by the table's name after "Material Properties - ".
/// </summary>
public static class ProgramTables
{
    public const string MaterialGroup = "Material Properties - ";

    /// <summary>ETABS 23's material tables and their fields (the Material field left out): every material form's data.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> MaterialTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Acceptance Criteria"] = ["IOTens", "LSTens", "CPTens", "IOComp", "LSComp", "CPComp"],
        ["Aluminum Data"] = ["AlumType", "Alloy", "Fcy", "Fty", "Ftu", "Fsu", "SSHysType"],
        ["Basic Mechanical Properties"] = ["DensityType", "UnitWeight", "UnitMass", "E1", "E2", "E3", "G12", "G13", "G23", "U12", "U13", "U23", "A1", "A2", "A3"],
        ["Coldformed Data"] = ["Fy", "Fu", "SSHysType"],
        ["Concrete Data"] = ["Fc", "LtWtConc", "LtWtFact", "IsUserFr", "UserFr", "SSCurveOpt", "SSHysType", "SFc", "SCap", "FinalSlope", "FAngle", "DAngle", "TimeType", "TimeE", "EFact", "TimeCreep", "CreepFact", "TimeShrink", "ShrinkFact", "CreepType", "CreepTerms"],
        ["Damping Parameters"] = ["ModalRatio", "VisMass", "VisStiff"],
        ["General"] = ["Type", "SymType", "Grade", "Color", "GUID", "Notes"],
        ["Hysteresis Parameters - BRB Hardening"] = ["IsSymmetric", "TensHardFac", "TensMaxPDef", "TensAccPDef", "TensProAcc", "CompHardFac", "CompMaxPDef", "CompAccPDef", "CompProAcc"],
        ["Hysteresis Parameters - Concrete"] = ["EnrgyDegFac"],
        ["Hysteresis Parameters - Degrading"] = ["IsSymmetric", "Tensf0", "Tensf1", "Tensf2", "Tensx1", "Tensx2", "TensADWtFac", "Compf0", "Compf1", "Compf2", "Compx1", "Compx2", "CompADWtFac", "StiffWtFac", "LrgSmlWtFac"],
        ["Hysteresis Parameters - Pivot"] = ["Alpha1", "Alpha2", "Beta1", "Beta2", "Eta"],
        ["Masonry Data"] = ["Fm", "SSCurveOpt", "SSHysType", "SFm", "SCap", "FinalSlope", "FAngle", "DAngle"],
        ["Other Data"] = ["SSHysType", "FAngle", "DAngle"],
        ["Rebar Data"] = ["Fy", "Fu", "Fye", "Fue", "SSCurveOpt", "SSHysType", "SHard", "SCap", "FinalSlope"],
        ["Steel Data"] = ["Fy", "Fu", "Fye", "Fue", "SSCurveOpt", "SSHysType", "SHard", "SMax", "SRup", "FinalSlope"],
        ["Tendon Data"] = ["Fy", "Fu", "SSCurveOpt", "SSHysType", "FinalSlope"],
        ["Time Dependence - ACI 209R-92"] = ["RelHumid", "ShrinkStart", "A", "Beta", "CuringType", "Slump", "FineAgg", "AirContent", "CementCont"],
        ["Time Dependence - AS 3600-2009"] = ["CompFactA", "CompFactB", "CreepCoeff", "Environment", "DryShrink"],
        ["Time Dependence - AS 3600-2018"] = ["CompFactA", "CompFactB", "CreepCoeff", "Environment", "DryShrink"],
        ["Time Dependence - CEB-FIP 2010"] = ["RelHumid", "ShrinkStart", "CementType", "LtwtGrade", "LtwtDensity"],
        ["Time Dependence - CEB-FIP 90"] = ["CementCoeff", "RelHumid", "BetaSC", "ShrinkStart"],
        ["Time Dependence - Eurocode 2-2004"] = ["RelHumid", "ShrinkStart", "CementType", "LtwtGrade", "LtwtDensity"],
        ["Time Dependence - GL2000"] = ["RelHumid", "ShrinkStart", "CementType", "StrDevParam", "Correction"],
        ["Time Dependence - NZS 3101-2006"] = ["DeltaF", "CementType", "RelHumid", "CreepCoeff", "AggTypeFact", "DryShrink"],
        ["Time Dependence - User Creep"] = ["A", "B", "Ho", "AgeAtLoad", "Days", "CreepCoeff"],
        ["Time Dependence - User Shrinkage"] = ["A", "B", "Ho", "Age", "ShrinkStrn"],
        ["Time Dependence - User Stiffness"] = ["Age", "Multiplier"],
        ["User Stress-Strain Curves"] = ["Point", "Strain", "Stress", "PointID"],
    };

    /// <summary>The tables a material may hold many rows in (a curve, a time series); the others hold one.</summary>
    public static readonly string[] ManyRows = ["User Stress-Strain Curves", "Time Dependence - User Creep", "Time Dependence - User Shrinkage", "Time Dependence - User Stiffness"];

    /// <summary>SAP2000 26's name for an ETABS 23 material table (its numbered tables, one temperature), or null when it has none.</summary>
    public static string? SapMaterialTable(string table) => table.ToUpperInvariant() switch
    {
        "GENERAL" => "MATERIAL PROPERTIES 01 - GENERAL",
        "BASIC MECHANICAL PROPERTIES" => "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES",
        "STEEL DATA" => "MATERIAL PROPERTIES 03A - STEEL DATA",
        "CONCRETE DATA" => "MATERIAL PROPERTIES 03B - CONCRETE DATA",
        "ALUMINUM DATA" => "MATERIAL PROPERTIES 03C - ALUMINUM DATA",
        "COLDFORMED DATA" => "MATERIAL PROPERTIES 03D - COLD FORMED DATA",
        "REBAR DATA" => "MATERIAL PROPERTIES 03E - REBAR DATA",
        "TENDON DATA" => "MATERIAL PROPERTIES 03F - TENDON DATA",
        "OTHER DATA" => "MATERIAL PROPERTIES 03G - OTHER DATA",
        "USER STRESS-STRAIN CURVES" => "MATERIAL PROPERTIES 04 - USER STRESS-STRAIN CURVES",
        "TIME DEPENDENCE - CEB-FIP 90" => "MATERIAL PROPERTIES 05A - TIME DEPENDENCE - CEB-FIP 90",
        "TIME DEPENDENCE - CEB-FIP 2010" => "MATERIAL PROPERTIES 05B - TIME DEPENDENCE - CEB-FIP 2010",
        "TIME DEPENDENCE - ACI 209R-92" => "MATERIAL PROPERTIES 05C - TIME DEPENDENCE - ACI 209R-92",
        "TIME DEPENDENCE - USER STIFFNESS" => "MATERIAL PROPERTIES 05D - TIME DEPENDENCE - USER STIFFNESS",
        "TIME DEPENDENCE - USER CREEP" => "MATERIAL PROPERTIES 05E - TIME DEPENDENCE - USER CREEP",
        "TIME DEPENDENCE - USER SHRINKAGE" => "MATERIAL PROPERTIES 05F - TIME DEPENDENCE - USER SHRINKAGE",
        "TIME DEPENDENCE - EUROCODE 2-2004" => "MATERIAL PROPERTIES 05H - TIME DEPENDENCE - EUROCODE 2-2004",
        "TIME DEPENDENCE - AS 3600-2009" => "MATERIAL PROPERTIES 05I - TIME DEPENDENCE - AS 3600-2009",
        "TIME DEPENDENCE - AS 3600-2018" => "MATERIAL PROPERTIES 05I - TIME DEPENDENCE - AS 3600-2018",
        "TIME DEPENDENCE - GL2000" => "MATERIAL PROPERTIES 05J - TIME DEPENDENCE - GL2000",
        "TIME DEPENDENCE - NZS 3101-2006" => "MATERIAL PROPERTIES 05K - TIME DEPENDENCE - NZS 3101-2006",
        "DAMPING PARAMETERS" => "MATERIAL PROPERTIES 06 - DAMPING PARAMETERS",
        "HYSTERESIS PARAMETERS - PIVOT" => "MATERIAL PROPERTIES 08A - HYSTERESIS PARAMETERS - PIVOT",
        "HYSTERESIS PARAMETERS - CONCRETE" => "MATERIAL PROPERTIES 08B - HYSTERESIS PARAMETERS - CONCRETE",
        "HYSTERESIS PARAMETERS - BRB HARDENING" => "MATERIAL PROPERTIES 08C - HYSTERESIS PARAMETERS - BRB HARDENING",
        "HYSTERESIS PARAMETERS - DEGRADING" => "MATERIAL PROPERTIES 08D - HYSTERESIS PARAMETERS - DEGRADING",
        "ACCEPTANCE CRITERIA" => "MATERIAL PROPERTIES 09 - ACCEPTANCE CRITERIA",
        _ => null,
    };

    /// <summary>SAP2000 26's name for an ETABS 23 field where they differ (its expected strengths).</summary>
    public static string SapField(string field) => field switch { "Fye" => "EffFy", "Fue" => "EffFu", _ => field };

    /// <summary>
    /// Checks and sets the rows a material holds for its forms: each table must be one of ETABS 23's material tables
    /// and each field one of that table's; a table given with no rows is taken off. Values are kept as text, as the
    /// programs' tables hold them (numbers in the model's units, invariant culture).
    /// </summary>
    public static void SetMaterialTables(Material m, IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables)
    {
        foreach (var (table, rows) in tables)
        {
            if (!MaterialTables.TryGetValue(table, out var fields))
                throw new ArgumentException($"There is no material table '{table}'. The tables: {string.Join(", ", MaterialTables.Keys)}.");
            var name = MaterialTables.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));
            if (rows.Count > 1 && !ManyRows.Contains(name)) throw new ArgumentException($"'{name}' holds one row for a material, not {rows.Count}.");
            foreach (var row in rows)
                foreach (var f in row.Keys)
                    if (!fields.Contains(f, StringComparer.OrdinalIgnoreCase))
                        throw new ArgumentException($"'{name}' has no field '{f}'. Its fields: {string.Join(", ", fields)}.");
            m.Tables ??= new(StringComparer.OrdinalIgnoreCase);
            var clean = rows.Select(r => r.Where(kv => kv.Value is not null).ToDictionary(kv => fields.First(x => x.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), kv => kv.Value.Trim(), StringComparer.Ordinal))
                .Where(r => r.Values.Any(v => v.Length > 0)).ToList();
            if (clean.Count == 0) m.Tables.Remove(name);
            else m.Tables[name] = clean;
        }
        if (m.Tables is { Count: 0 }) m.Tables = null;
    }

    /// <summary>The first row of a material's table, or an empty one.</summary>
    public static IReadOnlyDictionary<string, string> Row(Definition d, string table) =>
        d.Tables is not null && d.Tables.TryGetValue(table, out var rows) && rows.Count > 0 ? rows[0] : new Dictionary<string, string>();

    /// <summary>Every material's rows, by ETABS 23's full table name, each row with its Material field first.</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> MaterialRows(StructuralModel model)
    {
        var all = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mat in model.Materials)
            foreach (var (table, rows) in mat.Tables ?? [])
                foreach (var row in rows)
                {
                    var full = new Dictionary<string, string>(StringComparer.Ordinal) { ["Material"] = mat.Name };
                    foreach (var (k, v) in row) full[k] = v;
                    (all.TryGetValue(MaterialGroup + table, out var list) ? list : all[MaterialGroup + table] = []).Add(full);
                }
        return all;
    }

    /// <summary>
    /// A material's data as the programs' tables hold it, by table name after "Material Properties - ": what its .e2k
    /// keywords say (the nonlinear curve, strains and acceptance criteria), with its own table rows over them.
    /// </summary>
    public static Dictionary<string, List<Dictionary<string, string>>> MaterialData(Material mat)
    {
        var o = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        void From(string table, params (string Field, string Key)[] map)
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, key) in map) if (mat.Get(key) is { } v) row[field] = v;
            if (row.Count > 0) o[table] = [row];
        }
        switch (mat.Type)
        {
            case "Steel": From("Steel Data", ("SSCurveOpt", "SSTYPE"), ("SSHysType", "HYSTYPE"), ("SHard", "STRAINATHARDENING"), ("SMax", "STRAINATMAXSTRESS"), ("SRup", "STRAINATRUPTURE"), ("FinalSlope", "FINALSLOPE")); break;
            case "Concrete":
                From("Concrete Data", ("SSCurveOpt", "SSTYPE"), ("SSHysType", "HYSTYPE"), ("SFc", "STRAINATFC"), ("SCap", "STRAINATULTIMATE"), ("FinalSlope", "FINALSLOPE"));
                From("Hysteresis Parameters - Concrete", ("EnrgyDegFac", "ENERGYDEGFACT"));
                break;
            case "Rebar": From("Rebar Data", ("SSCurveOpt", "SSTYPE"), ("SSHysType", "HYSTYPE"), ("SHard", "STRAINATHARDENING"), ("SCap", "STRAINATULTIMATE"), ("FinalSlope", "FINALSLOPE")); break;
            case "Tendon": From("Tendon Data", ("SSCurveOpt", "SSTYPE"), ("SSHysType", "HYSTYPE"), ("FinalSlope", "FINALSLOPE")); break;
        }
        // The .e2k writes a tendon curve "270ksi"; the tables "270 ksi" (SAFE's own .f2k).
        foreach (var r in o.Values.SelectMany(x => x))
            if (r.TryGetValue("SSCurveOpt", out var c) && c is "250ksi" or "270ksi") r["SSCurveOpt"] = c[..3] + " ksi";
        From("Acceptance Criteria", ("IOTens", "IOTENSION"), ("LSTens", "LSTENSION"), ("CPTens", "CPTENSION"), ("IOComp", "IOCOMPRESSION"), ("LSComp", "LSCOMPRESSION"), ("CPComp", "CPCOMPRESSION"));
        foreach (var (table, rows) in mat.Tables ?? [])
        {
            if (ManyRows.Contains(table, StringComparer.OrdinalIgnoreCase) || !o.TryGetValue(table, out var have)) { o[table] = rows.Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList(); continue; }
            foreach (var (k, v) in rows[0]) have[0][k] = v;
        }
        return o;
    }

    /// <summary>A row of a writer's table with the fields of <paramref name="extra"/> put in (renamed by <paramref name="rename"/>).</summary>
    public static List<(string, string)> With(IEnumerable<(string, string)> row, IReadOnlyDictionary<string, string>? extra, Func<string, string>? rename = null)
    {
        var list = row.ToList();
        foreach (var (k0, v) in extra ?? new Dictionary<string, string>())
        {
            var k = rename?.Invoke(k0) ?? k0;
            var at = list.FindIndex(x => x.Item1.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) list[at] = (list[at].Item1, v); else list.Add((k, v));
        }
        return list;
    }

    public static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
