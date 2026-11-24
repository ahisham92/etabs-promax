using System.Text.Json.Serialization;

namespace EtabsProMax.Model;

/// <summary>
/// A building model the way ETABS itself stores it in its text file: plan points, line and area objects that join
/// those points, and what each object is on each story (its assignments). Keeping ETABS's own shape means a file
/// read from ETABS and written back loses nothing, and what we write is what ETABS expects to import.
/// Numbers are in the model's own units (<see cref="Units"/>); nothing is converted.
/// </summary>
public sealed class StructuralModel
{
    public string Title { get; set; } = "";
    /// <summary>The ETABS version written into the file's header. ETABS imports files from its own version and older.</summary>
    public string ProgramVersion { get; set; } = "23.0.0";
    public Units Units { get; set; } = new();
    /// <summary>The other lines of the file's CONTROLS section (preferences), as read.</summary>
    public List<string> Controls { get; set; } = [];
    /// <summary>Bottom first: the base level (height 0, its elevation set), then each story going up.</summary>
    public List<Story> Stories { get; set; } = [];
    public string GridSystem { get; set; } = "G1";
    public List<string> GridSystemExtra { get; set; } = ["TYPE", "\"CARTESIAN\"", "BUBBLESIZE", "1.25"];
    public List<GridLine> Grids { get; set; } = [];
    public List<Diaphragm> Diaphragms { get; set; } = [];
    public List<Material> Materials { get; set; } = [];
    public List<FrameSection> FrameSections { get; set; } = [];
    public List<ShellSection> ShellSections { get; set; } = [];
    public List<PlanPoint> Points { get; set; } = [];
    public List<LineObject> Lines { get; set; } = [];
    public List<AreaObject> Areas { get; set; } = [];
    public List<PointAssign> PointAssigns { get; set; } = [];
    public List<LineAssign> LineAssigns { get; set; } = [];
    public List<AreaAssign> AreaAssigns { get; set; } = [];
    public List<LoadPattern> LoadPatterns { get; set; } = [];
    public List<PointLoad> PointLoads { get; set; } = [];
    public List<LineLoad> LineLoads { get; set; } = [];
    public List<AreaLoad> AreaLoads { get; set; } = [];
    public List<LoadCase> LoadCases { get; set; } = [];
    public List<LoadCombo> Combos { get; set; } = [];
    /// <summary>Sections of an ETABS file this version doesn't understand, kept word for word and written back.</summary>
    public List<RawSection> Passthrough { get; set; } = [];
    /// <summary>Lines this version doesn't understand inside sections it does, by section heading, as read.</summary>
    public Dictionary<string, List<string>> UnparsedLines { get; set; } = [];

    [JsonIgnore] public Story Base => Stories[0];

    public int StoryIndex(string name) => Stories.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The elevation of the top of a story (the base level's own elevation for index 0).</summary>
    public double Elevation(int storyIndex)
    {
        var z = Stories[0].Elevation;
        for (var i = 1; i <= storyIndex; i++) z += Stories[i].Height;
        return z;
    }

    public PlanPoint? Point(string name) => Points.Find(p => p.Name == name);
    public LineObject? Line(string label) => Lines.Find(l => l.Label == label);
    public AreaObject? Area(string label) => Areas.Find(a => a.Label == label);
}

public sealed class Units
{
    public string Force { get; set; } = "KN";
    public string Length { get; set; } = "M";
    public string Temperature { get; set; } = "C";
}

public sealed class Story
{
    public string Name { get; set; } = "";
    /// <summary>Story height. 0 for the base level.</summary>
    public double Height { get; set; }
    /// <summary>Only the base level carries an elevation; the others follow from the heights.</summary>
    public double Elevation { get; set; }
    public List<string> Extra { get; set; } = [];
}

public sealed class GridLine
{
    public string Label { get; set; } = "";
    /// <summary>"X": a line at x = Coordinate (lettered grids). "Y": a line at y = Coordinate (numbered grids).</summary>
    public string Direction { get; set; } = "X";
    public double Coordinate { get; set; }
    public List<string> Extra { get; set; } = [];
}

public sealed class Diaphragm
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "RIGID";
}

/// <summary>A material, a frame section or a slab/wall section. ETABS spreads one over several lines of keywords;
/// the ones this version reasons about are named properties, and every line is kept as read in <see cref="Lines"/>.</summary>
public abstract class Definition
{
    public string Name { get; set; } = "";
    /// <summary>Each line's keywords and values after the name, as ETABS writes them.</summary>
    public List<List<string>> Lines { get; set; } = [];

    public string? Get(string key)
    {
        foreach (var line in Lines)
            for (var i = 0; i + 1 < line.Count; i += 2)
                if (line[i] == key) return E2k.E2kText.Unquote(line[i + 1]);
        return null;
    }

    public double? Number(string key) => E2k.E2kText.TryNumber(Get(key), out var d) ? d : null;
}

public sealed class Material : Definition
{
    [JsonIgnore] public string Type => Get("TYPE") ?? "";
    [JsonIgnore] public bool IsConcrete => Type.Equals("Concrete", StringComparison.OrdinalIgnoreCase);
}

public sealed class FrameSection : Definition
{
    [JsonIgnore] public string Material => Get("MATERIAL") ?? "";
    [JsonIgnore] public string Shape => Get("SHAPE") ?? "";
    /// <summary>Cross-section area for the shapes whose size is in the file (rectangles and circles); null otherwise.</summary>
    [JsonIgnore] public double? Area
    {
        get
        {
            var d = Number("D"); var b = Number("B");
            if (Shape.Contains("Rectangular", StringComparison.OrdinalIgnoreCase) && d is not null && b is not null) return d * b;
            if (Shape.Contains("Circle", StringComparison.OrdinalIgnoreCase) && d is not null) return Math.PI * d * d / 4;
            return null;
        }
    }
}

public sealed class ShellSection : Definition
{
    [JsonIgnore] public string Material => Get("MATERIAL") ?? "";
    [JsonIgnore] public bool IsWall => string.Equals(Get("PROPTYPE"), "Wall", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public double? Thickness => Number("SLABTHICKNESS") ?? Number("WALLTHICKNESS") ?? Number("DECKSLABDEPTH");
}

public sealed class PlanPoint
{
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>How far below its story level the point sits (ETABS's third number); 0 for almost every point.</summary>
    public double Below { get; set; }
}

public enum LineKind { Column, Beam, Brace }

public sealed class LineObject
{
    public string Label { get; set; } = "";
    public LineKind Kind { get; set; }
    public string PointI { get; set; } = "";
    public string PointJ { get; set; } = "";
    /// <summary>How many stories below the assigned story end I is: 1 for a column or brace, 0 for a beam.</summary>
    public int StoriesBelowI { get; set; }
}

public enum AreaKind { Floor, Wall, Ramp }

public sealed class AreaObject
{
    public string Label { get; set; } = "";
    public AreaKind Kind { get; set; }
    public List<string> PointNames { get; set; } = [];
    /// <summary>Per corner: how many stories below the assigned story it is (a wall's bottom corners are 1).</summary>
    public List<int> StoriesBelow { get; set; } = [];
}

public abstract class Assign
{
    public string Story { get; set; } = "";
    /// <summary>The rest of each assignment line, as read (releases, meshing, modifiers, ...). The first goes on
    /// the same line as the named properties when written.</summary>
    public List<List<string>> Extra { get; set; } = [];
}

public sealed class PointAssign : Assign
{
    public string Point { get; set; } = "";
    /// <summary>Restrained directions, e.g. "UX UY UZ RX RY RZ" (fixed) or "UX UY UZ" (pinned). Null: free.</summary>
    public string? Restraint { get; set; }
    public string? Diaphragm { get; set; }
}

public sealed class LineAssign : Assign
{
    public string Label { get; set; } = "";
    public string? Section { get; set; }
}

public sealed class AreaAssign : Assign
{
    public string Label { get; set; } = "";
    public string? Section { get; set; }
    public string? Diaphragm { get; set; }
}

public sealed class LoadPattern
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Dead";
    public double SelfWeight { get; set; }
    public List<string> Extra { get; set; } = [];
}

/// <summary>A load on one object on one story. <see cref="Tokens"/> is the line after the story, as ETABS writes
/// it (TYPE, DIR, LC, FVAL, ...), so every load type ETABS has survives a read and write.</summary>
public abstract class ObjectLoad
{
    public string Object { get; set; } = "";
    public string Story { get; set; } = "";
    public List<string> Tokens { get; set; } = [];

    [JsonIgnore] public string Pattern => E2k.E2kText.Value(Tokens, "LC") ?? "";
}

public sealed class PointLoad : ObjectLoad;
public sealed class LineLoad : ObjectLoad;
public sealed class AreaLoad : ObjectLoad;

public sealed class LoadCase
{
    public string Name { get; set; } = "";
    public List<List<string>> Lines { get; set; } = [];
}

public sealed class LoadCombo
{
    public string Name { get; set; } = "";
    public List<List<string>> Lines { get; set; } = [];
}

public sealed class RawSection
{
    /// <summary>The heading without its "$ ", e.g. "CONCRETE SECTIONS".</summary>
    public string Heading { get; set; } = "";
    /// <summary>The heading of the section before it in the file it came from ("" for the first), to put it back in place.</summary>
    public string After { get; set; } = "";
    public List<string> Lines { get; set; } = [];
}
