using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// The running SAP2000, through its own API (SAP2000v1.dll, the same cSapModel shape ETABS has). Most calls are the
/// same in both programs, so they go to the ETABS link as they are, on its one thread. What differs is handled here:
/// SAP2000 has no stories, and lists its joints, frames and areas one by one rather than all at once. Every message
/// names SAP2000. SAP2000 must be open and licensed; this attaches to it and never starts or unlocks it.
/// </summary>
public sealed class SapLink : ICsiLink, IDisposable
{
    private readonly ICsiLink _inner;

    /// <param name="inner">The link that makes the calls: the ETABS link set to SAP2000 (or a stand-in in the tests).</param>
    public SapLink(ICsiLink inner) => _inner = inner;

    public CsiProgram Program => CsiProgram.Sap2000;

    public void Dispose() => (_inner as IDisposable)?.Dispose();

    /// <summary>The ETABS link's messages, in SAP2000's words.</summary>
    internal static string Reword(string message) => message
        .Replace("ETABS's Tools menu", "SAP2000's Tools menu", StringComparison.Ordinal)
        .Replace("ETABSv1", "SAP2000v1", StringComparison.Ordinal)
        .Replace("ETABS", "SAP2000", StringComparison.Ordinal);

    private static T Say<T>(Func<T> call)
    {
        try { return call(); }
        catch (EtabsException ex) { throw new EtabsException(Reword(ex.Message)); }
    }

    private static void Say(Action call) => Say(() => { call(); return true; });

    private static string[] None => [];
    private static double[] NoNumbers => [];

    private static void Ok(object result, string what)
    {
        if (Convert.ToInt32(result) != 0) throw new EtabsException($"SAP2000 refused to {what}.");
    }

    public ModelInfo Info() => Say(_inner.Info);

    /// <summary>SAP2000 has no stories.</summary>
    public IReadOnlyList<StoryRow> Stories() => [];

    public IReadOnlyList<PointRow> Points() => Say(() => (IReadOnlyList<PointRow>)_inner.Raw("read the joints", m => ReadPoints(m))!);

    private static List<PointRow> ReadPoints(dynamic m)
    {
        // Newer versions answer all joints in one call; older ones joint by joint.
        try
        {
            int count = 0; string[] names = None; double[] x = NoNumbers, y = NoNumbers, z = NoNumbers;
            if (Convert.ToInt32(m.PointObj.GetAllPoints(ref count, ref names, ref x, ref y, ref z, "Global")) == 0 && names.Length >= count)
                return Enumerable.Range(0, count).Select(i => new PointRow(names[i], x[i], y[i], z[i])).ToList();
        }
        catch (MissingMethodException) { }
        catch (MissingMemberException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        int n = 0; string[] list = None;
        Ok(m.PointObj.GetNameList(ref n, ref list), "list the joints");
        var rows = new List<PointRow>(n);
        for (var i = 0; i < n; i++)
        {
            double px = 0, py = 0, pz = 0;
            Ok(m.PointObj.GetCoordCartesian(list[i], ref px, ref py, ref pz, "Global"), $"read joint {list[i]}");
            rows.Add(new PointRow(list[i], px, py, pz));
        }
        return rows;
    }

    public IReadOnlyList<FrameRow> Frames() => Say(() => (IReadOnlyList<FrameRow>)_inner.Raw("read the frames", m =>
    {
        List<PointRow> all = ReadPoints(m);
        var at = all.ToDictionary(p => p.Name, StringComparer.Ordinal);
        int n = 0; string[] names = None;
        Ok(m.FrameObj.GetNameList(ref n, ref names), "list the frames");
        var rows = new List<FrameRow>(n);
        for (var i = 0; i < n; i++)
        {
            string p1 = "", p2 = "", section = "", auto = "";
            Ok(m.FrameObj.GetPoints(names[i], ref p1, ref p2), $"read frame {names[i]}");
            m.FrameObj.GetSection(names[i], ref section, ref auto);
            if (!at.TryGetValue(p1, out var a) || !at.TryGetValue(p2, out var b)) continue;
            rows.Add(new FrameRow(names[i], section, "", EtabsWords.FrameKind(a.X, a.Y, a.Z, b.X, b.Y, b.Z), a.X, a.Y, a.Z, b.X, b.Y, b.Z));
        }
        return rows;
    })!);

    public IReadOnlyList<AreaRow> Areas() => Say(() => (IReadOnlyList<AreaRow>)_inner.Raw("read the areas", m =>
    {
        List<PointRow> all = ReadPoints(m);
        var at = all.ToDictionary(p => p.Name, StringComparer.Ordinal);
        int n = 0; string[] names = None;
        Ok(m.AreaObj.GetNameList(ref n, ref names), "list the areas");
        var rows = new List<AreaRow>(n);
        for (var i = 0; i < n; i++)
        {
            int count = 0; string[] corners = None; string section = "";
            Ok(m.AreaObj.GetPoints(names[i], ref count, ref corners), $"read area {names[i]}");
            m.AreaObj.GetProperty(names[i], ref section);
            var xyz = corners.Take(count).Where(at.ContainsKey).Select(c => (at[c].X, at[c].Y, at[c].Z)).ToList();
            rows.Add(new AreaRow(names[i], section, AreaKind(xyz), xyz));
        }
        return rows;
    })!);

    /// <summary>Floor when level, wall when upright, shell otherwise (a roof slope, a tank wall at an angle).</summary>
    internal static string AreaKind(IReadOnlyList<(double X, double Y, double Z)> c)
    {
        if (c.Count < 3) return "shell";
        if (c.All(p => Math.Abs(p.Z - c[0].Z) < 1e-6)) return "floor";
        // The normal of the first three corners: upright when it has no Z part.
        double ux = c[1].X - c[0].X, uy = c[1].Y - c[0].Y, uz = c[1].Z - c[0].Z;
        double vx = c[2].X - c[0].X, vy = c[2].Y - c[0].Y, vz = c[2].Z - c[0].Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return len > 0 && Math.Abs(nz) / len < 1e-3 ? "wall" : "shell";
    }

    public bool[] Restraint(string point) => Say(() => _inner.Restraint(point));
    public IReadOnlyList<(string Kind, string Name)> Selected() => Say(_inner.Selected);
    public void ClearSelection() => Say(_inner.ClearSelection);
    public void Select(string kind, string name) => Say(() => _inner.Select(kind, name));
    public IReadOnlyList<string> FrameSections() => Say(_inner.FrameSections);
    public IReadOnlyList<string> AreaSections() => Say(_inner.AreaSections);
    public IReadOnlyList<string> Materials() => Say(_inner.Materials);
    public IReadOnlyList<LoadPatternRow> LoadPatterns() => Say(_inner.LoadPatterns);
    public IReadOnlyList<string> LoadCases() => Say(_inner.LoadCases);
    public IReadOnlyList<string> Combos() => Say(_inner.Combos);
    public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section) => Say(() => _inner.AddFrame(x1, y1, z1, x2, y2, z2, section));
    public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => Say(() => _inner.AddArea(corners, section));
    public void SetFrameSection(string name, string section) => Say(() => _inner.SetFrameSection(name, section));
    public void SetAreaSection(string name, string section) => Say(() => _inner.SetAreaSection(name, section));

    public void Delete(string kind, string name)
    {
        if (kind == "point")
        {
            Say(() => _inner.Raw("delete a joint", m => { Ok(m.PointObj.DeleteSpecialPoint(name, 0), $"delete joint {name} (a joint that frames or areas use cannot be deleted)"); return null; }));
            return;
        }
        Say(() => _inner.Delete(kind, name));
    }

    public void SetRestraint(string point, bool[] restraint) => Say(() => _inner.SetRestraint(point, restraint));
    public void AddLoadPattern(string name, string type, double selfWeight) => Say(() => _inner.AddLoadPattern(name, type, selfWeight));
    public void SetFrameLoad(string frame, string pattern, double value, bool replace) => Say(() => _inner.SetFrameLoad(frame, pattern, value, replace));
    public void SetAreaLoad(string area, string pattern, double value, bool replace) => Say(() => _inner.SetAreaLoad(area, pattern, value, replace));
    public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) => Say(() => _inner.AddCombo(name, cases));
    public IReadOnlyList<TableName> Tables() => Say(_inner.Tables);
    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => Say(() => _inner.ReadTable(key, cases, combos));
    public object? Raw(string what, Func<dynamic, object?> work) => Say(() => _inner.Raw(what, work));
    public void RunAnalysis() => Say(_inner.RunAnalysis);
    public void Save() => Say(_inner.Save);
    public void Refresh() => Say(_inner.Refresh);
}
