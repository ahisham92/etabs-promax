using System.Globalization;
using CsiProMax.Checks;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The Modeler's model: CSI ProMax's own model (the shared core's StructuralModel) behind the same link the tools use
/// on a live ETABS, so the chat builds a model with no ETABS on the PC. It saves ETABS text files (.e2k) that licensed
/// ETABS imports and analyses. It does not run, emulate or unlock ETABS: there is no analysis and no design here.
/// </summary>
public sealed class OfflineEtabs : ICsiLink, CsiProMax.Assistant.Tools.IModelDrawing
{
    public const string NoAnalysis =
        "The Modeler has no ETABS behind it, so it cannot analyse or design. Save the model (save_model), import the .e2k into licensed ETABS (File > Import > ETABS .e2k text file) and run it there.";

    private readonly List<(string Kind, string Name)> _selected = [];

    public OfflineEtabs(StructuralModel? model = null, string path = "")
    {
        Model = model ?? ModelEdit.New("Untitled");
        Path = path;
    }

    public StructuralModel Model { get; private set; }

    /// <summary>The .e2k file the model is saved to ("" until it has been saved or opened).</summary>
    public string Path { get; private set; }

    /// <summary>Goes up with every change, so a view of the model knows when to draw again.</summary>
    public int Revision { get; private set; }

    public bool Unsaved { get; private set; }

    /// <summary>Where models go when no folder is given. Tests point it at a folder of their own.</summary>
    public static string DefaultFolder { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CSI ProMax", "Models");

    public void Replace(StructuralModel model, string path)
    {
        Model = model; Path = path; _selected.Clear(); Revision++; Unsaved = false;
    }

    private void Changed() { Revision++; Unsaved = true; }

    private static T Try<T>(Func<T> work)
    {
        try { return work(); }
        catch (ArgumentException ex) { throw new EtabsException(ex.Message); }
    }

    public T Edit<T>(Func<StructuralModel, T> work)
    {
        var result = Try(() => work(Model));
        Changed();
        return result;
    }

    // ---- reading ----

    public ModelInfo Info() => new(Path.Length > 0 ? Path : Model.Title, "Modeler (no ETABS)", Unit(Model.Units.Force), Unit(Model.Units.Length), Model.Units.Temperature, false);

    private static string Unit(string u) => u.ToUpperInvariant() switch { "KN" => "kN", "M" => "m", "MM" => "mm", "CM" => "cm", "N" => "N", "KIP" => "kip", "LB" => "lb", "FT" => "ft", "IN" => "in", _ => u };

    public IReadOnlyList<StoryRow> Stories() => Model.Stories.Select((s, i) => new StoryRow(s.Name, Model.Elevation(i), s.Height)).ToList();

    private static string Name(string label, string story) => label + "@" + story;

    private static (string Label, string Story) Split(string name)
    {
        var at = name.LastIndexOf('@');
        return at > 0 ? (name[..at], name[(at + 1)..]) : throw new EtabsException($"There is no object '{name}'. Names look like B3@Story2 (label, then the story).");
    }

    public IReadOnlyList<FrameRow> Frames() => Physical.Frames(Model)
        .Select(f => new FrameRow(Name(f.Label, f.Story), f.Section ?? "", f.Story, f.Kind.ToString().ToLowerInvariant(), f.I.X, f.I.Y, f.I.Z, f.J.X, f.J.Y, f.J.Z)).ToList();

    public IReadOnlyList<AreaRow> Areas() => Physical.Areas(Model)
        .Select(a => new AreaRow(Name(a.Label, a.Story), a.Section ?? "", a.Kind == AreaKind.Wall ? "wall" : "floor", a.Corners.Select(c => (c.X, c.Y, c.Z)).ToList())).ToList();

    public IReadOnlyList<PointRow> Points()
    {
        var seen = new Dictionary<string, PointRow>();
        void Add(double x, double y, double z)
        {
            var point = Model.Points.Find(p => Math.Abs(p.X - x) <= ModelEdit.Tolerance && Math.Abs(p.Y - y) <= ModelEdit.Tolerance);
            var story = StoryAt(z);
            if (point is null || story is null) return;
            seen.TryAdd(Name(point.Name, story), new PointRow(Name(point.Name, story), x, y, z));
        }
        foreach (var f in Physical.Frames(Model)) { Add(f.I.X, f.I.Y, f.I.Z); Add(f.J.X, f.J.Y, f.J.Z); }
        foreach (var a in Physical.Areas(Model)) foreach (var c in a.Corners) Add(c.X, c.Y, c.Z);
        foreach (var s in Model.PointAssigns)
            if (Model.Point(s.Point) is { } p && Model.StoryIndex(s.Story) is >= 0 and var i) seen.TryAdd(Name(p.Name, s.Story), new PointRow(Name(p.Name, s.Story), p.X, p.Y, Model.Elevation(i) - p.Below));
        return seen.Values.ToList();
    }

    private string? StoryAt(double z)
    {
        for (var i = 0; i < Model.Stories.Count; i++)
            if (Math.Abs(Model.Elevation(i) - z) <= 1e-4) return Model.Stories[i].Name;
        return null;
    }

    private string NeedStory(double z) => StoryAt(z)
        ?? throw new EtabsException($"Elevation {z.ToString("0.###", CultureInfo.InvariantCulture)} is not a story level. The levels are: {string.Join(", ", Model.Stories.Select((s, i) => $"{s.Name} {Model.Elevation(i).ToString("0.###", CultureInfo.InvariantCulture)}"))}.");

    public bool[] Restraint(string point)
    {
        var (name, story) = Split(point);
        var set = Model.PointAssigns.Find(a => a.Point == name && a.Story == story)?.Restraint ?? "";
        return new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Select(d => set.Split(' ').Contains(d)).ToArray();
    }

    public IReadOnlyList<CsiProMax.Assistant.Tools.GridRow> Grids() =>
        Model.Grids.Select(g => new CsiProMax.Assistant.Tools.GridRow(g.Label, g.Direction.StartsWith("Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "X", g.Coordinate)).ToList();

    public IReadOnlyList<CsiProMax.Assistant.Tools.SectionSize> FrameSizes() => Model.FrameSections
        .Where(s => s.Number("D") is > 0)
        .Select(s => new CsiProMax.Assistant.Tools.SectionSize(s.Name, s.Shape, s.Number("D") ?? 0, s.Number("B") ?? 0)).ToList();

    public IReadOnlyList<(string Name, double Thickness)> AreaSizes() =>
        Model.ShellSections.Where(s => s.Thickness is > 0).Select(s => (s.Name, s.Thickness ?? 0)).ToList();

    public IReadOnlyList<CsiProMax.Assistant.Tools.SupportRow> Supports()
    {
        var rows = new List<CsiProMax.Assistant.Tools.SupportRow>();
        foreach (var a in Model.PointAssigns)
        {
            if (string.IsNullOrWhiteSpace(a.Restraint) || Model.Point(a.Point) is not { } p || Model.StoryIndex(a.Story) is not (>= 0 and var i)) continue;
            var set = a.Restraint.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rows.Add(new CsiProMax.Assistant.Tools.SupportRow(p.X, p.Y, Model.Elevation(i) - p.Below, new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Select(d => set.Contains(d)).ToArray()));
        }
        return rows;
    }

    public IReadOnlyList<(string Kind, string Name)> Selected() => _selected.ToList();
    public void ClearSelection() { _selected.Clear(); Revision++; }

    public void Select(string kind, string name)
    {
        var known = kind == "frame" ? Frames().Any(f => f.Name == name) : kind == "area" ? Areas().Any(a => a.Name == name) : Points().Any(p => p.Name == name);
        if (!known) throw new EtabsException($"There is no {kind} named {name}.");
        _selected.Add((kind, name)); Revision++;
    }

    public IReadOnlyList<string> FrameSections() => Model.FrameSections.Select(s => s.Name).ToList();
    public IReadOnlyList<string> AreaSections() => Model.ShellSections.Select(s => s.Name).ToList();
    public IReadOnlyList<string> Materials() => Model.Materials.Select(s => s.Name).ToList();
    public IReadOnlyList<LoadPatternRow> LoadPatterns() => Model.LoadPatterns.Select(p => new LoadPatternRow(p.Name, p.Type, p.SelfWeight)).ToList();
    public IReadOnlyList<string> LoadCases() => Model.LoadCases.Select(c => c.Name).ToList();
    public IReadOnlyList<string> Combos() => Model.Combos.Select(c => c.Name).ToList();

    // ---- changing ----

    public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section) => Edit(m =>
    {
        var same = Math.Abs(x1 - x2) <= ModelEdit.Tolerance && Math.Abs(y1 - y2) <= ModelEdit.Tolerance;
        var top = NeedStory(Math.Max(z1, z2));
        if (Math.Abs(z1 - z2) <= 1e-4)
        {
            if (same) throw new EtabsException("A frame needs two different ends.");
            return Name(m.AddBeam(x1, y1, x2, y2, top, section).Label, top);
        }
        var index = m.StoryIndex(top);
        if (index < 1 || Math.Abs(m.Elevation(index - 1) - Math.Min(z1, z2)) > 1e-4)
            throw new EtabsException("A column or brace runs from one story level to the next one above. Add it story by story.");
        if (same) return Name(m.AddColumn(x1, y1, top, section).Label, top);
        return z1 < z2 ? Name(m.AddBrace(x1, y1, x2, y2, top, section).Label, top) : Name(m.AddBrace(x2, y2, x1, y1, top, section).Label, top);
    });

    public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => Edit(m =>
    {
        if (corners.Count < 3) throw new EtabsException("An area needs three corners or more.");
        var high = corners.Max(c => c.Z);
        var top = NeedStory(high);
        if (corners.All(c => Math.Abs(c.Z - high) <= 1e-4))
        {
            var diaphragm = m.Diaphragms.FirstOrDefault()?.Name ?? "D1";
            return Name(m.AddSlab(corners.Select(c => (c.X, c.Y)).ToList(), top, section, diaphragm).Label, top);
        }
        var upper = corners.Where(c => Math.Abs(c.Z - high) <= 1e-4).ToList();
        var index = m.StoryIndex(top);
        if (corners.Count != 4 || upper.Count != 2 || index < 1 || corners.Any(c => Math.Abs(c.Z - high) > 1e-4 && Math.Abs(c.Z - m.Elevation(index - 1)) > 1e-4))
            throw new EtabsException("A wall panel has four corners: two on a story level and two on the level below. Add it story by story.");
        return Name(m.AddWall(upper[0].X, upper[0].Y, upper[1].X, upper[1].Y, top, section).Label, top);
    });

    public void SetFrameSection(string name, string section)
    {
        var (label, story) = Split(name);
        var assign = Model.LineAssigns.Find(a => a.Label == label && a.Story == story) ?? throw new EtabsException($"There is no frame {name}.");
        assign.Section = section; Changed();
    }

    public void SetAreaSection(string name, string section)
    {
        var (label, story) = Split(name);
        var assign = Model.AreaAssigns.Find(a => a.Label == label && a.Story == story) ?? throw new EtabsException($"There is no area {name}.");
        assign.Section = section; Changed();
    }

    public void Delete(string kind, string name)
    {
        var (label, story) = Split(name);
        int removed;
        if (kind == "frame")
        {
            removed = Model.LineAssigns.RemoveAll(a => a.Label == label && a.Story == story);
            Model.LineLoads.RemoveAll(l => l.Object == label && l.Story == story);
            if (!Model.LineAssigns.Exists(a => a.Label == label)) Model.Lines.RemoveAll(l => l.Label == label);
        }
        else
        {
            removed = Model.AreaAssigns.RemoveAll(a => a.Label == label && a.Story == story);
            Model.AreaLoads.RemoveAll(l => l.Object == label && l.Story == story);
            if (!Model.AreaAssigns.Exists(a => a.Label == label)) Model.Areas.RemoveAll(a => a.Label == label);
        }
        if (removed == 0) throw new EtabsException($"There is no {kind} {name}.");
        _selected.RemoveAll(s => s.Name == name);
        Changed();
    }

    public void SetRestraint(string point, bool[] restraint)
    {
        var (name, story) = Split(point);
        if (Model.Point(name) is null || Model.StoryIndex(story) < 0) throw new EtabsException($"There is no point {point}.");
        var text = string.Join(" ", new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Where((_, i) => i < restraint.Length && restraint[i]));
        var assign = Model.PointAssigns.Find(a => a.Point == name && a.Story == story);
        if (assign is null && text.Length > 0) Model.PointAssigns.Add(assign = new PointAssign { Point = name, Story = story });
        if (assign is not null) assign.Restraint = text.Length > 0 ? text : null;
        Changed();
    }

    public void AddLoadPattern(string name, string type, double selfWeight) => Edit(m => m.AddLoadPattern(name, type, selfWeight));

    public void SetFrameLoad(string frame, string pattern, double value, bool replace)
    {
        var (label, story) = Split(frame);
        if (!Model.LineAssigns.Exists(a => a.Label == label && a.Story == story)) throw new EtabsException($"There is no frame {frame}.");
        if (replace) Model.LineLoads.RemoveAll(l => l.Object == label && l.Story == story && l.Pattern == pattern);
        Model.AddLineLoad(label, story, pattern, value); Changed();
    }

    public void SetAreaLoad(string area, string pattern, double value, bool replace)
    {
        var (label, story) = Split(area);
        if (!Model.AreaAssigns.Exists(a => a.Label == label && a.Story == story)) throw new EtabsException($"There is no area {area}.");
        if (replace) Model.AreaLoads.RemoveAll(l => l.Object == label && l.Story == story && l.Pattern == pattern);
        Model.AddAreaLoad(label, story, pattern, value); Changed();
    }

    public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) => Edit(m => m.AddCombo(name, cases.ToArray()));

    // ---- tables: the model's own, none of them results ----

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public IReadOnlyList<TableName> Tables() =>
    [
        new("Story Definitions", "Story Definitions", Model.Stories.Count < 2),
        new("Grid Definitions", "Grid Definitions", Model.Grids.Count == 0),
        new("Frame Assignments - Section Properties", "Frame Assignments - Section Properties", Model.LineAssigns.Count == 0),
        new("Area Assignments - Section Properties", "Area Assignments - Section Properties", Model.AreaAssigns.Count == 0),
        new("Model Checks", "Model Checks", false),
        new("Quantities", "Quantities", Model.LineAssigns.Count + Model.AreaAssigns.Count == 0),
    ];

    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => key switch
    {
        "Story Definitions" => new(["Story", "Height", "Elevation"], Model.Stories.Select((s, i) => new[] { s.Name, N(s.Height), N(Model.Elevation(i)) }).Reverse().ToList()),
        "Grid Definitions" => new(["Label", "Direction", "Coordinate"], Model.Grids.Select(g => new[] { g.Label, g.Direction, N(g.Coordinate) }).ToList()),
        "Frame Assignments - Section Properties" => new(["Story", "UniqueName", "Kind", "Section Property", "Length"],
            Physical.Frames(Model).Select(f => new[] { f.Story, Name(f.Label, f.Story), f.Kind.ToString(), f.Section ?? "", N(f.Length) }).ToList()),
        "Area Assignments - Section Properties" => new(["Story", "UniqueName", "Kind", "Section Property", "Area"],
            Physical.Areas(Model).Select(a => new[] { a.Story, Name(a.Label, a.Story), a.Kind.ToString(), a.Section ?? "", N(a.Area) }).ToList()),
        "Model Checks" => new(["Severity", "Check", "Message", "Object", "Story"],
            ModelChecks.Run(Model).Select(f => new[] { f.Severity.ToString(), f.Check, f.Message, f.Object ?? "", f.Story ?? "" }).ToList()),
        "Quantities" => new(["Kind", "Section", "Material", "Count", "Length", "Area", "Volume"],
            Quantities.Run(Model).Select(q => new[] { q.Kind, q.Section, q.Material, q.Count.ToString(CultureInfo.InvariantCulture), N(q.Length), N(q.Area), q.Volume is { } v ? N(v) : "" }).ToList()),
        _ => throw new EtabsException("The Modeler has no such table. Result tables exist only in ETABS after its analysis. " + NoAnalysis),
    };

    public object? Raw(string what, Func<dynamic, object?> work) =>
        throw new EtabsException("Code runs against ETABS's own API, and the Modeler has no ETABS behind it. Use the modelling tools here; run code after the model is open in ETABS.");

    public void RunAnalysis() => throw new EtabsException(NoAnalysis);

    /// <summary>Saves the .e2k (what ETABS imports) and the .cpm beside it (the Modeler's own file).</summary>
    public void Save() => SaveAs(Path.Length > 0 ? Path : System.IO.Path.Combine(DefaultFolder, FileName(Model.Title) + ".e2k"));

    public string SaveAs(string path)
    {
        if (!path.EndsWith(".e2k", StringComparison.OrdinalIgnoreCase)) path = System.IO.Path.ChangeExtension(path, ".e2k");
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            E2kWriter.WriteFile(Model, path);
            ModelFile.Save(Model, System.IO.Path.ChangeExtension(path, ModelFile.Extension));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { throw new EtabsException($"The model could not be saved to {path}: {ex.Message}"); }
        Path = path; Unsaved = false; Revision++;
        return path;
    }

    private static string FileName(string title)
    {
        var clean = new string(title.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '-' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : "Untitled";
    }

    /// <summary>Opens an ETABS text file (.e2k, .$et) or a Modeler file (.cpm).</summary>
    public static StructuralModel Load(string path) =>
        path.EndsWith(ModelFile.Extension, StringComparison.OrdinalIgnoreCase) ? ModelFile.Load(path) : E2kReader.ReadFile(path);

    public void Refresh() { }
}
