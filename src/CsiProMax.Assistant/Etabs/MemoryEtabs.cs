namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// A small model kept in memory that answers like ETABS: the tests run the tools on it, and --demo shows the window
/// on a PC without ETABS. It has no solver and no results: only what the tools add to it.
/// </summary>
public sealed class MemoryEtabs : ICsiLink
{
    private readonly List<StoryRow> _stories = [new("Base", 0, 0), new("Story1", 3.2, 3.2), new("Story2", 6.4, 3.2), new("Story3", 9.6, 3.2)];
    private readonly List<FrameRow> _frames = [];
    private readonly List<AreaRow> _areas = [];
    private readonly List<PointRow> _points = [];
    private readonly Dictionary<string, bool[]> _restraints = [];
    private readonly List<(string Kind, string Name)> _selected = [];
    private readonly List<LoadPatternRow> _patterns = [new("Dead", "Dead", 1), new("Live", "Live", 0)];
    private readonly List<string> _combos = [];
    private int _next = 1;

    public List<string> FrameSectionNames { get; } = ["C500x500", "B300x600"];
    public List<string> AreaSectionNames { get; } = ["S200", "W250"];
    public Dictionary<string, double> Loads { get; } = [];
    public bool Locked { get; set; }
    /// <summary>The program this stands in for. The tests give SAFE or SAP2000 to see what a tool says there.</summary>
    public CsiProgram Program { get; init; } = CsiProgram.Etabs;
    public int Saves { get; private set; }
    public int Runs { get; private set; }

    public ModelInfo Info() => new(@"C:\Models\demo.EDB", "23.2.0", "kN", "m", "C", Locked);
    public IReadOnlyList<StoryRow> Stories() => _stories;
    public IReadOnlyList<FrameRow> Frames() => _frames.ToList();
    public IReadOnlyList<AreaRow> Areas() => _areas.ToList();
    public IReadOnlyList<PointRow> Points() => _points.ToList();
    public bool[] Restraint(string point) => _restraints.TryGetValue(point, out var r) ? r : new bool[6];
    public IReadOnlyList<(string Kind, string Name)> Selected() => _selected.ToList();
    public void ClearSelection() => _selected.Clear();

    public void Select(string kind, string name)
    {
        var known = kind == "frame" ? _frames.Any(f => f.Name == name) : kind == "area" ? _areas.Any(a => a.Name == name) : _points.Any(p => p.Name == name);
        if (!known) throw new EtabsException($"{Program.Name} refused to select {kind} {name}.");
        _selected.Add((kind, name));
    }

    public IReadOnlyList<string> FrameSections() => FrameSectionNames;
    public IReadOnlyList<string> AreaSections() => AreaSectionNames;
    public IReadOnlyList<string> Materials() => ["C30/37", "A615Gr60"];
    public IReadOnlyList<LoadPatternRow> LoadPatterns() => _patterns.ToList();
    public IReadOnlyList<string> LoadCases() => _patterns.Select(p => p.Name).ToList();
    public IReadOnlyList<string> Combos() => _combos.ToList();

    private void Changing()
    {
        if (Locked) throw new EtabsException($"{Program.Name} refused: the model is locked. Unlock it in {Program.Name} first.");
    }

    private string Point(double x, double y, double z)
    {
        var at = _points.FirstOrDefault(p => Math.Abs(p.X - x) < 1e-6 && Math.Abs(p.Y - y) < 1e-6 && Math.Abs(p.Z - z) < 1e-6);
        if (at is not null) return at.Name;
        var name = (_next++).ToString();
        _points.Add(new PointRow(name, x, y, z));
        return name;
    }

    public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section)
    {
        Changing();
        Point(x1, y1, z1); Point(x2, y2, z2);
        var name = (_next++).ToString();
        var story = _stories.LastOrDefault(s => s.Elevation <= Math.Max(z1, z2) + 1e-6)?.Name ?? "";
        _frames.Add(new FrameRow(name, section, story, EtabsWords.FrameKind(x1, y1, z1, x2, y2, z2), x1, y1, z1, x2, y2, z2));
        return name;
    }

    public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section)
    {
        Changing();
        foreach (var c in corners) Point(c.X, c.Y, c.Z);
        var name = (_next++).ToString();
        _areas.Add(new AreaRow(name, section, corners.All(c => Math.Abs(c.Z - corners[0].Z) < 1e-6) ? "floor" : "wall", corners.ToList()));
        return name;
    }

    public void SetFrameSection(string name, string section)
    {
        Changing();
        var at = _frames.FindIndex(f => f.Name == name);
        if (at < 0) throw new EtabsException($"{Program.Name} refused to give frame {name} the section {section}.");
        _frames[at] = _frames[at] with { Section = section };
    }

    public void SetAreaSection(string name, string section)
    {
        Changing();
        var at = _areas.FindIndex(a => a.Name == name);
        if (at < 0) throw new EtabsException($"{Program.Name} refused to give area {name} the section {section}.");
        _areas[at] = _areas[at] with { Section = section };
    }

    public void Delete(string kind, string name)
    {
        Changing();
        var removed = kind == "frame" ? _frames.RemoveAll(f => f.Name == name) : _areas.RemoveAll(a => a.Name == name);
        if (removed == 0) throw new EtabsException($"{Program.Name} refused to delete {kind} {name}.");
    }

    public void SetRestraint(string point, bool[] restraint) { Changing(); _restraints[point] = restraint.ToArray(); }

    public void AddLoadPattern(string name, string type, double selfWeight)
    {
        Changing();
        if (!EtabsWords.PatternTypes.Any(t => string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase)))
            throw new EtabsException($"'{type}' is not a load pattern type. Use one of: {string.Join(", ", EtabsWords.PatternTypes.Select(t => t.Name))}.");
        _patterns.Add(new LoadPatternRow(name, type, selfWeight));
    }

    public void SetFrameLoad(string frame, string pattern, double value, bool replace) => Load("frame " + frame, pattern, value, replace);
    public void SetAreaLoad(string area, string pattern, double value, bool replace) => Load("area " + area, pattern, value, replace);

    private void Load(string on, string pattern, double value, bool replace)
    {
        Changing();
        var key = on + " / " + pattern;
        Loads[key] = replace ? value : Loads.GetValueOrDefault(key) + value;
    }

    public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) { Changing(); _combos.Add(name); }

    /// <summary>Result tables a test or the demo gives the stand-in model, by table name.</summary>
    public Dictionary<string, TableData> ResultTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TableName> Tables() =>
        [new("Story Definitions", "Story Definitions", false), new("Story Drifts", "Story Drifts", !ResultTables.ContainsKey("Story Drifts")), new("Frame Assignments - Section Properties", "Frame Assignments - Section Properties", _frames.Count == 0),
         .. ResultTables.Where(t => t.Key != "Story Drifts").Select(t => new TableName(t.Key, t.Key, t.Value.Rows.Count == 0))];

    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => ResultTables.TryGetValue(key, out var given) ? given : key switch
    {
        "Story Definitions" => new(["Tower", "Story", "Height"], _stories.Skip(1).Reverse().Select(s => new[] { "T1", s.Name, s.Height.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList()),
        "Frame Assignments - Section Properties" => new(["Story", "UniqueName", "Section Property"], _frames.Select(f => new[] { f.Story, f.Name, f.Section }).ToList()),
        _ => new(["Story", "Output Case", "Drift"], []),
    };

    public object? Raw(string what, Func<dynamic, object?> work) =>
        throw new EtabsException($"Code runs in {Program.Name} itself, and this is the demo model with no {Program.Name} behind it.");

    public void RunAnalysis() { Runs++; Locked = true; }
    public void Save() => Saves++;
    public void Refresh() { }
}
