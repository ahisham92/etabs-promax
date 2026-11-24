using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace EtabsProMax.Assistant.Etabs;

/// <summary>
/// The running ETABS, through ETABS's own API (the same one ETABS plug-ins and spreadsheets use). ETABS must be open
/// and licensed: this attaches to it and never starts, unlocks or stands in for it. Calls are made by name at run
/// time, so nothing of ETABS is needed to build. Every call runs on one thread of its own, one at a time.
/// </summary>
public sealed class ComEtabs : IEtabsLink, IDisposable
{
    private const string ProgId = "CSI.ETABS.API.ETABSObject";
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private dynamic? _model;

    public ComEtabs()
    {
        _thread = new Thread(() => { foreach (var job in _work.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = "ETABS" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Dispose() => _work.CompleteAdding();

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    /// <summary>Runs one piece of work on the ETABS thread and waits for it.</summary>
    private T On<T>(string what, Func<dynamic, T> body)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { done.SetResult(body(Model())); }
            catch (EtabsException ex) { done.SetException(ex); }
            catch (COMException ex)
            {
                // ETABS was closed, or is closing: attach again on the next call.
                _model = null;
                done.SetException(new EtabsException($"ETABS did not answer ({what}). Is ETABS still open? {ex.Message}"));
            }
            catch (Exception ex) { done.SetException(new EtabsException($"ETABS could not {what}: {ex.Message}")); }
        });
        try { return done.Task.GetAwaiter().GetResult(); }
        catch (EtabsException) { throw; }
    }

    private void On(string what, Action<dynamic> body) => On<bool>(what, m => { body(m); return true; });

    private dynamic Model()
    {
        if (_model is not null) return _model;
        if (!OperatingSystem.IsWindows()) throw new EtabsException("ETABS runs on Windows only.");
        if (CLSIDFromProgID(ProgId, out var clsid) != 0)
            throw new EtabsException("ETABS is not installed on this PC, or its API is not registered. Open ETABS once as administrator to register it.");
        if (GetActiveObject(ref clsid, IntPtr.Zero, out var app) != 0 || app is null)
            throw new EtabsException("ETABS is not open. Open ETABS and a model, then try again.");
        dynamic etabs = app;
        _model = etabs.SapModel;
        return _model!;
    }

    /// <summary>ETABS's API answers 0 when a call worked.</summary>
    private static void Ok(object result, string what)
    {
        if (Convert.ToInt32(result) != 0) throw new EtabsException($"ETABS refused to {what}.");
    }

    private static string[] None => [];
    private static double[] NoNumbers => [];

    public ModelInfo Info() => On("read the model", m =>
    {
        string version = ""; double number = 0;
        try { m.GetVersion(ref version, ref number); } catch (Exception ex) when (ex is not COMException) { }
        int force = 0, length = 0, temperature = 0;
        try { m.GetPresentUnits_2(ref force, ref length, ref temperature); } catch (Exception ex) when (ex is not COMException) { }
        string file = Convert.ToString(m.GetModelFilename(true)) ?? "";
        bool locked = Convert.ToBoolean(m.GetModelIsLocked());
        return new ModelInfo(file, version, Name(Forces, force), Name(Lengths, length), Name(Temperatures, temperature), locked);
    });

    private static readonly string[] Forces = ["", "lb", "kip", "N", "kN", "kgf", "tonf"];
    private static readonly string[] Lengths = ["", "in", "ft", "micron", "mm", "cm", "m"];
    private static readonly string[] Temperatures = ["", "F", "C"];
    private static string Name(string[] names, int value) => value > 0 && value < names.Length ? names[value] : "";

    public IReadOnlyList<StoryRow> Stories() => On("read the stories", m =>
    {
        double baseElevation = 0; int n = 0;
        string[] names = None, similar = None; double[] elevations = NoNumbers, heights = NoNumbers, spliceHeight = NoNumbers;
        bool[] master = [], splice = []; int[] color = [];
        Ok(m.Story.GetStories_2(ref baseElevation, ref n, ref names, ref elevations, ref heights, ref master, ref similar, ref splice, ref spliceHeight, ref color), "list the stories");
        var rows = new List<StoryRow> { new("Base", baseElevation, 0) };
        for (var i = 0; i < n; i++) rows.Add(new StoryRow(names[i], elevations[i], heights[i]));
        return (IReadOnlyList<StoryRow>)rows.OrderBy(r => r.Elevation).ToList();
    });

    public IReadOnlyList<FrameRow> Frames() => On("read the frames", m =>
    {
        int n = 0; string[] names = None, sections = None, stories = None, p1 = None, p2 = None;
        double[] x1 = NoNumbers, y1 = NoNumbers, z1 = NoNumbers, x2 = NoNumbers, y2 = NoNumbers, z2 = NoNumbers, angle = NoNumbers;
        double[] o1x = NoNumbers, o2x = NoNumbers, o1y = NoNumbers, o2y = NoNumbers, o1z = NoNumbers, o2z = NoNumbers; int[] cardinal = [];
        Ok(m.FrameObj.GetAllFrames(ref n, ref names, ref sections, ref stories, ref p1, ref p2, ref x1, ref y1, ref z1, ref x2, ref y2, ref z2,
            ref angle, ref o1x, ref o2x, ref o1y, ref o2y, ref o1z, ref o2z, ref cardinal, "Global"), "list the frames");
        var rows = new List<FrameRow>(n);
        for (var i = 0; i < n; i++)
            rows.Add(new FrameRow(names[i], sections[i], stories[i], EtabsWords.FrameKind(x1[i], y1[i], z1[i], x2[i], y2[i], z2[i]), x1[i], y1[i], z1[i], x2[i], y2[i], z2[i]));
        return (IReadOnlyList<FrameRow>)rows;
    });

    public IReadOnlyList<AreaRow> Areas() => On("read the areas", m =>
    {
        int n = 0; string[] names = None;
        Ok(m.AreaObj.GetNameList(ref n, ref names), "list the areas");
        var points = new Dictionary<string, (double, double, double)>(StringComparer.Ordinal);
        {
            int count = 0; string[] pn = None; double[] x = NoNumbers, y = NoNumbers, z = NoNumbers;
            Ok(m.PointObj.GetAllPoints(ref count, ref pn, ref x, ref y, ref z, "Global"), "list the points");
            for (var i = 0; i < count; i++) points[pn[i]] = (x[i], y[i], z[i]);
        }
        var rows = new List<AreaRow>(n);
        for (var i = 0; i < n; i++)
        {
            int count = 0; string[] corners = None; string section = "";
            Ok(m.AreaObj.GetPoints(names[i], ref count, ref corners), "read an area's corners");
            m.AreaObj.GetProperty(names[i], ref section);
            var at = corners.Take(count).Select(c => points.TryGetValue(c, out var p) ? p : (0, 0, 0)).ToList();
            var level = at.Count > 0 && at.All(p => Math.Abs(p.Item3 - at[0].Item3) < 1e-6);
            rows.Add(new AreaRow(names[i], section, level ? "floor" : "wall", at));
        }
        return (IReadOnlyList<AreaRow>)rows;
    });

    public IReadOnlyList<PointRow> Points() => On("read the points", m =>
    {
        int n = 0; string[] names = None; double[] x = NoNumbers, y = NoNumbers, z = NoNumbers;
        Ok(m.PointObj.GetAllPoints(ref n, ref names, ref x, ref y, ref z, "Global"), "list the points");
        return (IReadOnlyList<PointRow>)Enumerable.Range(0, n).Select(i => new PointRow(names[i], x[i], y[i], z[i])).ToList();
    });

    public bool[] Restraint(string point) => On("read a support", m =>
    {
        bool[] value = new bool[6];
        Ok(m.PointObj.GetRestraint(point, ref value), $"read the support of point {point}");
        return value;
    });

    public IReadOnlyList<(string Kind, string Name)> Selected() => On("read the selection", m =>
    {
        int n = 0; int[] types = []; string[] names = None;
        Ok(m.SelectObj.GetSelected(ref n, ref types, ref names), "read the selection");
        return (IReadOnlyList<(string, string)>)Enumerable.Range(0, n)
            .Select(i => (types[i] switch { 1 => "point", 2 => "frame", 5 => "area", 7 => "link", _ => "other" }, names[i])).ToList();
    });

    public void ClearSelection() => On("clear the selection", m => Ok(m.SelectObj.ClearSelection(), "clear the selection"));

    public void Select(string kind, string name) => On("select", m =>
    {
        Ok(kind switch
        {
            "frame" => m.FrameObj.SetSelected(name, true, 0),
            "area" => m.AreaObj.SetSelected(name, true, 0),
            "point" => m.PointObj.SetSelected(name, true, 0),
            _ => throw new EtabsException($"'{kind}' can't be selected: use frame, area or point."),
        }, $"select {kind} {name}");
    });

    private IReadOnlyList<string> Names(string what, Func<dynamic, object> list) => On($"list the {what}", m =>
    {
        var names = (string[])list(m);
        return (IReadOnlyList<string>)names.ToList();
    });

    public IReadOnlyList<string> FrameSections() => Names("frame sections", m => { int n = 0; string[] a = None; Ok(m.PropFrame.GetNameList(ref n, ref a), "list the frame sections"); return a.Take(n).ToArray(); });
    public IReadOnlyList<string> AreaSections() => Names("slab and wall sections", m => { int n = 0; string[] a = None; Ok(m.PropArea.GetNameList(ref n, ref a), "list the slab and wall sections"); return a.Take(n).ToArray(); });
    public IReadOnlyList<string> Materials() => Names("materials", m => { int n = 0; string[] a = None; Ok(m.PropMaterial.GetNameList(ref n, ref a), "list the materials"); return a.Take(n).ToArray(); });
    public IReadOnlyList<string> LoadCases() => Names("load cases", m => { int n = 0; string[] a = None; Ok(m.LoadCases.GetNameList(ref n, ref a), "list the load cases"); return a.Take(n).ToArray(); });
    public IReadOnlyList<string> Combos() => Names("load combinations", m => { int n = 0; string[] a = None; Ok(m.RespCombo.GetNameList(ref n, ref a), "list the load combinations"); return a.Take(n).ToArray(); });

    public IReadOnlyList<LoadPatternRow> LoadPatterns() => On("read the load patterns", m =>
    {
        int n = 0; string[] names = None;
        Ok(m.LoadPatterns.GetNameList(ref n, ref names), "list the load patterns");
        var rows = new List<LoadPatternRow>(n);
        for (var i = 0; i < n; i++)
        {
            int type = 0; double self = 0;
            try { m.LoadPatterns.GetLoadType(names[i], ref type); m.LoadPatterns.GetSelfWTMultiplier(names[i], ref self); }
            catch (Exception ex) when (ex is not COMException) { }
            var name = EtabsWords.PatternTypes.FirstOrDefault(t => t.Value == type).Name ?? "Other";
            rows.Add(new LoadPatternRow(names[i], name, self));
        }
        return (IReadOnlyList<LoadPatternRow>)rows;
    });

    public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section) => On("add a frame", m =>
    {
        string name = "";
        Ok(m.FrameObj.AddByCoord(x1, y1, z1, x2, y2, z2, ref name, section, "", "Global"), $"add a frame with section {section}");
        return name;
    });

    public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => On("add an area", m =>
    {
        double[] x = corners.Select(c => c.X).ToArray(), y = corners.Select(c => c.Y).ToArray(), z = corners.Select(c => c.Z).ToArray();
        string name = "";
        Ok(m.AreaObj.AddByCoord(corners.Count, ref x, ref y, ref z, ref name, section, "", "Global"), $"add an area with section {section}");
        return name;
    });

    public void SetFrameSection(string name, string section) => On("change a section", m => Ok(m.FrameObj.SetSection(name, section, 0, 0.0, 0.0), $"give frame {name} the section {section}"));
    public void SetAreaSection(string name, string section) => On("change a section", m => Ok(m.AreaObj.SetProperty(name, section, 0), $"give area {name} the section {section}"));

    public void Delete(string kind, string name) => On("delete", m =>
    {
        Ok(kind switch
        {
            "frame" => m.FrameObj.Delete(name, 0),
            "area" => m.AreaObj.Delete(name, 0),
            _ => throw new EtabsException($"'{kind}' can't be deleted here: use frame or area."),
        }, $"delete {kind} {name}");
    });

    public void SetRestraint(string point, bool[] restraint) => On("set a support", m =>
    {
        bool[] value = restraint.ToArray();
        Ok(m.PointObj.SetRestraint(point, ref value, 0), $"set the support of point {point}");
    });

    public void AddLoadPattern(string name, string type, double selfWeight) => On("add a load pattern", m =>
    {
        var value = EtabsWords.PatternTypes.FirstOrDefault(t => string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase)).Value;
        if (value == 0) throw new EtabsException($"'{type}' is not a load pattern type. Use one of: {string.Join(", ", EtabsWords.PatternTypes.Select(t => t.Name))}.");
        Ok(m.LoadPatterns.Add(name, value, selfWeight, true), $"add the load pattern {name}");
    });

    // Direction 10 is gravity; type 1 is a force per length.
    public void SetFrameLoad(string frame, string pattern, double value, bool replace) => On("load a frame", m =>
        Ok(m.FrameObj.SetLoadDistributed(frame, pattern, 1, 10, 0.0, 1.0, value, value, "Global", true, replace, 0), $"put the load on frame {frame}"));

    public void SetAreaLoad(string area, string pattern, double value, bool replace) => On("load an area", m =>
        Ok(m.AreaObj.SetLoadUniform(area, pattern, value, 10, replace, "Global", 0), $"put the load on area {area}"));

    public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) => On("add a combination", m =>
    {
        Ok(m.RespCombo.Add(name, 0), $"add the combination {name}");
        foreach (var (item, factor) in cases)
        {
            int kind = 0; // a load case
            Ok(m.RespCombo.SetCaseList(name, ref kind, item, factor), $"add {item} to the combination {name}");
        }
    });

    public IReadOnlyList<TableName> Tables() => On("list the tables", m =>
    {
        int n = 0; string[] keys = None, names = None; int[] import = []; bool[] empty = [];
        Ok(m.DatabaseTables.GetAllTables(ref n, ref keys, ref names, ref import, ref empty), "list the tables");
        return (IReadOnlyList<TableName>)Enumerable.Range(0, n).Select(i => new TableName(keys[i], names[i], i < empty.Length && empty[i])).ToList();
    });

    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => On("read a table", m =>
    {
        if (cases is { Count: > 0 }) { string[] list = cases.ToArray(); m.DatabaseTables.SetLoadCasesSelectedForDisplay(ref list); }
        if (combos is { Count: > 0 }) { string[] list = combos.ToArray(); m.DatabaseTables.SetLoadCombinationsSelectedForDisplay(ref list); }
        string[] ask = None, fields = None, data = None; int version = 0, records = 0;
        Ok(m.DatabaseTables.GetTableForDisplayArray(key, ref ask, "", ref version, ref fields, ref records, ref data), $"read the table '{key}' (check its name with the table list)");
        var rows = new List<string[]>(records);
        var width = fields.Length;
        for (var r = 0; r < records && width > 0; r++) rows.Add(data.Skip(r * width).Take(width).Select(v => v ?? "").ToArray());
        return new TableData(fields, rows);
    });

    public void RunAnalysis() => On("run the analysis", m => Ok(m.Analyze.RunAnalysis(), "run the analysis (the model must be saved first)"));
    public void Save() => On("save the model", m => Ok(m.File.Save(""), "save the model"));
    public void Refresh() => On("refresh the view", m => { try { m.View.RefreshView(0, false); } catch (Exception ex) when (ex is not COMException) { } });
}
