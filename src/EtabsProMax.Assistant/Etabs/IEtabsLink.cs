namespace EtabsProMax.Assistant.Etabs;

/// <summary>
/// Everything the assistant asks of ETABS, in plain data. ComEtabs answers from the running ETABS; the tests answer
/// from memory. Lengths, coordinates and loads are in the model's present units (Info says which).
/// </summary>
public interface IEtabsLink
{
    ModelInfo Info();
    IReadOnlyList<StoryRow> Stories();
    IReadOnlyList<FrameRow> Frames();
    IReadOnlyList<AreaRow> Areas();
    IReadOnlyList<PointRow> Points();
    /// <summary>The restraint of one point: UX UY UZ RX RY RZ.</summary>
    bool[] Restraint(string point);
    IReadOnlyList<(string Kind, string Name)> Selected();
    void ClearSelection();
    /// <summary>Selects one object. Kind is frame, area or point.</summary>
    void Select(string kind, string name);

    IReadOnlyList<string> FrameSections();
    IReadOnlyList<string> AreaSections();
    IReadOnlyList<string> Materials();
    IReadOnlyList<LoadPatternRow> LoadPatterns();
    IReadOnlyList<string> LoadCases();
    IReadOnlyList<string> Combos();

    string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section);
    string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section);
    void SetFrameSection(string name, string section);
    void SetAreaSection(string name, string section);
    void Delete(string kind, string name);
    void SetRestraint(string point, bool[] restraint);
    void AddLoadPattern(string name, string type, double selfWeight);
    /// <summary>A uniform load on a frame, in the gravity direction (positive is down).</summary>
    void SetFrameLoad(string frame, string pattern, double value, bool replace);
    /// <summary>A uniform load on an area, in the gravity direction (positive is down).</summary>
    void SetAreaLoad(string area, string pattern, double value, bool replace);
    void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases);

    IReadOnlyList<TableName> Tables();
    TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos);

    void RunAnalysis();
    void Save();
    void Refresh();
}

public sealed record ModelInfo(string File, string Version, string Force, string Length, string Temperature, bool Locked);
public sealed record StoryRow(string Name, double Elevation, double Height);
public sealed record FrameRow(string Name, string Section, string Story, string Kind, double X1, double Y1, double Z1, double X2, double Y2, double Z2);
public sealed record AreaRow(string Name, string Section, string Kind, IReadOnlyList<(double X, double Y, double Z)> Corners);
public sealed record PointRow(string Name, double X, double Y, double Z);
public sealed record LoadPatternRow(string Name, string Type, double SelfWeight);
public sealed record TableName(string Key, string Name, bool Empty);
public sealed record TableData(IReadOnlyList<string> Fields, IReadOnlyList<string[]> Rows);

/// <summary>ETABS said no, or isn't there: the message is written for the engineer.</summary>
public sealed class EtabsException(string message) : Exception(message);

public static class EtabsWords
{
    /// <summary>Load pattern types by the names ETABS shows, with the API's numbers.</summary>
    public static readonly (string Name, int Value)[] PatternTypes =
    [
        ("Dead", 1), ("Super Dead", 2), ("Live", 3), ("Reducible Live", 4), ("Seismic", 5), ("Wind", 6), ("Snow", 7),
        ("Other", 8), ("Temperature", 10), ("Roof Live", 11), ("Notional", 12),
    ];

    /// <summary>Column, beam or brace from the two ends: vertical, level, or neither.</summary>
    public static string FrameKind(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        var plan = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        var rise = Math.Abs(z2 - z1);
        var tol = 1e-6 * Math.Max(1, plan + rise);
        return plan <= tol ? "column" : rise <= tol ? "beam" : "brace";
    }
}
