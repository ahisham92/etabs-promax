namespace EtabsProMax.E2k;

/// <summary>The section headings of an ETABS text file that this version reads and writes, in the order ETABS writes them.</summary>
internal static class E2kSections
{
    public const string Program = "PROGRAM INFORMATION";
    public const string Controls = "CONTROLS";
    public const string Stories = "STORIES - IN SEQUENCE FROM TOP";
    public const string Grids = "GRIDS";
    public const string Diaphragms = "DIAPHRAGM NAMES";
    public const string Materials = "MATERIAL PROPERTIES";
    public const string FrameSections = "FRAME SECTIONS";
    public const string SlabProperties = "SLAB PROPERTIES";
    public const string DeckProperties = "DECK PROPERTIES";
    public const string WallProperties = "WALL PROPERTIES";
    public const string Points = "POINT COORDINATES";
    public const string Lines = "LINE CONNECTIVITIES";
    public const string Areas = "AREA CONNECTIVITIES";
    public const string PointAssigns = "POINT ASSIGNS";
    public const string LineAssigns = "LINE ASSIGNS";
    public const string AreaAssigns = "AREA ASSIGNS";
    public const string LoadPatterns = "LOAD PATTERNS";
    public const string PointLoads = "POINT OBJECT LOADS";
    public const string FrameLoads = "FRAME OBJECT LOADS";
    public const string ShellLoads = "SHELL OBJECT LOADS";
    public const string LoadCases = "LOAD CASES";
    public const string Combos = "LOAD COMBINATIONS";
    public const string End = "END OF MODEL FILE";

    public static readonly string[] Order =
    [
        Program, Controls, Stories, Grids, Diaphragms, Materials, FrameSections, SlabProperties, DeckProperties,
        WallProperties, Points, Lines, Areas, PointAssigns, LineAssigns, AreaAssigns, LoadPatterns, PointLoads,
        FrameLoads, ShellLoads, LoadCases, Combos,
    ];

    /// <summary>Headings older ETABS versions used for the same section.</summary>
    public static string Canonical(string heading) => heading switch
    {
        "LINE OBJECT LOADS" => FrameLoads,
        "AREA OBJECT LOADS" => ShellLoads,
        "STORIES" => Stories,
        "STATIC LOADS" => LoadPatterns,
        _ => heading,
    };

    public static bool IsKnown(string heading) => Array.IndexOf(Order, heading) >= 0;
}
