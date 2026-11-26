namespace CsiProMax.Model;

/// <summary>
/// What each program offers in the forms the Modeler copies (Ahmed 2026-11-26: modelling for SAP2000 shows only
/// SAP2000's options, for SAFE only SAFE's, for ETABS only ETABS's). The model's program is
/// <see cref="StructuralModel.Target"/>; the lists here are the shared ones less what that program does not have, so a
/// value already in the model is kept (the forms still show it), and nothing is lost when the program changes.
/// </summary>
public static class ProgramOptions
{
    public const string Etabs = "etabs", Sap2000 = "sap2000", Safe = "safe";

    /// <summary>Load pattern types. SAP2000 has no auto pattern live or drift seismic patterns; SAFE no notional or
    /// drift seismic patterns.</summary>
    public static IReadOnlyList<string> PatternTypes(string target) => target switch
    {
        Sap2000 => ModelEdit.LoadPatternTypes.Where(t => t is not ("Pattern Live - Auto" or "Seismic (Drift)")).ToList(),
        Safe => ModelEdit.LoadPatternTypes.Where(t => t is not ("Pattern Live - Auto" or "Seismic (Drift)" or "Notional")).ToList(),
        _ => ModelEdit.LoadPatternTypes,
    };

    /// <summary>Load case types. SAFE: static, modal and response spectrum only.</summary>
    public static IReadOnlyList<string> CaseTypes(string target) => target == Safe
        ? AnalysisDefs.CaseTypes.Where(t => t is "Linear Static" or "Nonlinear Static" or "Modal - Eigen" or "Modal - Ritz" or "Response Spectrum").ToList()
        : AnalysisDefs.CaseTypes;

    /// <summary>Material types. SAFE: concrete, rebar, tendon, steel and other.</summary>
    public static IReadOnlyList<string> MaterialTypes(string target) => target == Safe
        ? PropertyDefs.MaterialTypes.Where(t => t is "Concrete" or "Rebar" or "Tendon" or "Steel" or "Other").ToList()
        : PropertyDefs.MaterialTypes;

    /// <summary>Frame section shapes. SAFE's beams and columns are concrete.</summary>
    public static bool HasShape(string target, ShapeDef shape) => target != Safe || shape.Concrete;

    /// <summary>Slab types (Slab, Drop, Ribbed, ...): ETABS and SAFE; SAP2000's areas are shells by modelling type only.</summary>
    public static IReadOnlyList<string> SlabTypes(string target) => target == Sap2000 ? [] : PropertyDefs.SlabTypes;

    /// <summary>Shell modelling types. SAFE: thin and thick plates; layered and membrane shells are ETABS and SAP2000.</summary>
    public static IReadOnlyList<string> ModelingTypes(string target) => target == Safe
        ? PropertyDefs.ModelingTypes.Where(t => t is "ShellThin" or "ShellThick").ToList()
        : PropertyDefs.ModelingTypes;

    /// <summary>The assignment tables (<see cref="AssignTables"/>) each program takes: ETABS all; SAP2000 those its
    /// .s2k or its API carries; SAFE none of them.</summary>
    public static bool HasAssignTable(string target, string table) => target switch
    {
        Sap2000 => SapAssignTables.Contains(table) || SapOwnAssignTables.Contains(table),
        Safe => false,
        _ => !SapOwnAssignTables.Contains(table),
    };

    /// <summary>SAP2000's own assignment tables (no ETABS table has their shape).</summary>
    public static readonly string[] SapOwnAssignTables = ["Area Edge Release Assignments", "Joint Panel Zone Assignments"];

    public static readonly string[] SapAssignTables =
    [
        "Frame Assignments - Additional Mass", "Frame Assignments - Output Stations", "Frame Assignments - Frame Auto Mesh Options",
        "Frame Assignments - Tension and Compression Limits", "Frame Assignments - Material Overwrites",
        "Area Assignments - Local Axes", "Area Assignments - Insertion Point", "Area Assignments - Additional Mass", "Area Assignments - Auto Edge Constraints",
        "Area Assignments - Floor Auto Mesh Options", "Area Assignments - Wall Auto Mesh Options", "Area Assignments - Thickness Overwrites", "Area Assignments - Material Overwrites",
        "Joint Assignments - Additional Mass", "Area Assignments - Edge Releases", "Joint Assignments - Panel Zone",
    ];

    /// <summary>The definition tables (<see cref="DefineTables"/>) each program takes: generalized displacements and layered
    /// shells go into SAP2000's own tables too; the rest are ETABS's own; SAFE has none of them.</summary>
    public static bool HasDefinitionTable(string target, string table) => target switch
    {
        Etabs => !DefineTables.IsSap(table),
        // SAP2000's hinges are its own tables ("Hinges Def ..."); the layers and generalized displacements are written into its tables.
        Sap2000 => DefineTables.IsSap(table) || table.EndsWith("- Layers", StringComparison.OrdinalIgnoreCase) || table.Equals("Generalized Displacement Definitions", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
