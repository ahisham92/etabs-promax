namespace CsiProMax.Model;

/// <summary>
/// ETABS 23.2's Detailing > Drawing Management > Drawing Format Properties and Drawing Sheet Setup (pictured in
/// field/reference-forms/etabs/Detailing). ETABS keeps these in its detailing data, which neither its .e2k nor its
/// database tables carry, so the Engine keeps them as rows of tables of its own in
/// <see cref="StructuralModel.DefinitionTables"/> (saved with the Engine's model, to and from Excel), and never puts
/// them into the program's tables.
/// Drawing Format Properties: one row per item the form changed from ETABS's default (Group = General, Tables,
/// Imported Drawings, Slab/Mat or Beams; Tab = Line, Fill, Text or Arrowheads; then that tab's columns). Sheet Setup:
/// one row, its lengths in inches. The rows' key (Set) is the form's name.
/// </summary>
public static class EtabsDetailingTables
{
    public const string DrawingFormat = "Engine - Detailing Drawing Format Properties";
    public const string SheetSetup = "Engine - Detailing Drawing Sheet Setup";

    public static readonly IReadOnlyDictionary<string, (string Key, string Label)[]> Tables = new Dictionary<string, (string, string)[]>(StringComparer.OrdinalIgnoreCase)
    {
        [DrawingFormat] =
        [
            ("Set", "Set"), ("Group", "Group"), ("Tab", "Tab"), ("Item", "Item"), ("Color", "Color"), ("Thickness", "Thickness"), ("Style", "Style"),
            ("Visible", "Visible"), ("Transparency", "Transparency (0-255)"), ("Filled", "Filled"), ("Texture", "Texture"), ("Font", "Font"),
            ("Size", "Size"), ("Alignment", "Alignment"), ("Bold", "Bold"), ("AllUpper", "All Upper"), ("LeftStyle", "Left Arrow Style"),
            ("LeftSize", "Left Size"), ("LeftFilled", "Left Filled"), ("LeftLeader", "Left Leader"), ("RightStyle", "Right Style"),
            ("RightSize", "Right Size"), ("RightFilled", "Right Filled"), ("RightLeader", "Right Leader"),
        ],
        [SheetSetup] =
        [
            ("Set", "Set"), ("SheetType", "Sheet Type"), ("SheetSize", "Sheet Size"), ("SheetWidth", "Sheet Width"), ("SheetHeight", "Sheet Height"),
            ("MarginTop", "Margin Top"), ("MarginLeft", "Margin Left"), ("MarginRight", "Margin Right"), ("MarginBottom", "Margin Bottom"),
            ("Plans", "Plans"), ("Tables", "Tables"), ("Elevations", "Elevations"), ("BeamElevNewLine", "Beam Elevation on New Line"),
            ("Border", "Border and Title Block"), ("Position", "Predefined Position"), ("TitleBlockSize", "Title Block Size"), ("UserFile", "User Defined File"),
            ("SheetNoPrefix", "Sheet No. Prefix"), ("SheetTitlePrefix", "Sheet Title Prefix"), ("TextHeight", "Basic Text Height"),
            ("SymbolSize", "Basic Symbol Size"), ("LineThickness", "Line Thickness"), ("ViewGapDx", "View Gap - Dx"), ("ViewGapDy", "View Gap - Dy"),
        ],
    };

    /// <summary>True for a table the Engine keeps for itself (the program has no table of it).</summary>
    public static bool IsEngineOnly(string table) => Tables.ContainsKey(table);
}
