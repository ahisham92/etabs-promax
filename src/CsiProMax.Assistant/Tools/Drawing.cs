using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>What a link can say for drawing the model the way an engineer expects to see it. Optional: a link without it is asked the general way.</summary>
public interface IModelDrawing
{
    IReadOnlyList<GridRow> Grids();
    /// <summary>Frame sections whose size is known: depth (along local 3) and width (along local 2). Circles have width 0.
    /// Steel shapes may add flange and web thickness, and any section its material.</summary>
    IReadOnlyList<SectionSize> FrameSizes();
    /// <summary>Slab and wall sections with their thickness.</summary>
    IReadOnlyList<(string Name, double Thickness)> AreaSizes();
    IReadOnlyList<SupportRow> Supports();
}

/// <param name="Direction">"X": a line at x = Coordinate. "Y": a line at y = Coordinate.</param>
public sealed record GridRow(string Label, string Direction, double Coordinate);
public sealed record SectionSize(string Name, string Shape, double Depth, double Width, double Flange = 0, double Web = 0, string Material = "");
public sealed record SupportRow(double X, double Y, double Z, bool[] Restraint);

/// <summary>
/// Grid lines, section sizes and supports for the view. From the link itself when it knows them (the Modeler);
/// otherwise from the CSI program's tables and calls, each tried on its own and left out when it does not answer.
/// </summary>
public static class Drawing
{
    public const int MostSupports = 2000, MostSections = 400;

    public static void AddTo(JsonObject model, ICsiLink e, IReadOnlyList<PointRow> points)
    {
        var own = e as IModelDrawing;
        var grids = Try(() => own?.Grids() ?? GridsFromTables(e));
        var sizes = Try(() => own?.FrameSizes() ?? FrameSizesFromProgram(e));
        var slabs = Try(() => own?.AreaSizes() ?? []);
        var supports = Try(() => own?.Supports() ?? SupportsAtBase(e, points));
        if (grids is { Count: > 0 })
            model["grids"] = new JsonArray(grids.Select(g => (JsonNode)new JsonArray(g.Label, g.Direction, R(g.Coordinate))).ToArray());
        if (sizes is { Count: > 0 })
        {
            // [shape, depth, width] and, for steel shapes, flange and web thickness: the Viewer draws the true outline.
            model["frame_sizes"] = new JsonObject(sizes.DistinctBy(s => s.Name).Select(s => KeyValuePair.Create(s.Name, (JsonNode?)(s.Flange > 0 || s.Web > 0
                ? new JsonArray(s.Shape, R(s.Depth), R(s.Width), R(s.Flange), R(s.Web))
                : new JsonArray(s.Shape, R(s.Depth), R(s.Width))))));
            var materials = sizes.Where(s => s.Material.Length > 0).DistinctBy(s => s.Name).ToList();
            if (materials.Count > 0)
                model["section_materials"] = new JsonObject(materials.Select(s => KeyValuePair.Create(s.Name, (JsonNode?)JsonValue.Create(s.Material))));
        }
        if (slabs is { Count: > 0 })
            model["area_sizes"] = new JsonObject(slabs.DistinctBy(s => s.Name).Select(s => KeyValuePair.Create(s.Name, (JsonNode?)JsonValue.Create(R(s.Thickness)))));
        // Post-tensioning tendons in 3D and design strips in plan, as the programs draw them over the slab.
        var tendons = Try(() => e is OfflineEtabs o ? o.Model is var mm ? CsiProMax.Model.SlabDefs.Tendons(mm).Select(t => (t.Name, t.Story, t.Shape())).ToList() : null : TendonsFromTables(e));
        if (tendons is { Count: > 0 })
            model["tendons"] = new JsonArray(tendons.Take(MostSupports).Select(t => (JsonNode)new JsonArray(t.Name, t.Story,
                new JsonArray(t.Item3.Select(p => (JsonNode)new JsonArray(R(p[0]), R(p[1]), R(p[2]))).ToArray()))).ToArray());
        var strips = Try(() => e is OfflineEtabs o ? o.Model is var mm ? CsiProMax.Model.SlabDefs.Strips(mm).Select(s => (s.Name, s.Layer, s.WidthLeft, s.WidthRight, Z: StripZ(mm, s.Story), s.Plan)).ToList() : null : null);
        if (strips is { Count: > 0 })
            model["strips"] = new JsonArray(strips.Select(s => (JsonNode)new JsonArray(s.Name, s.Layer, R(s.WidthLeft), R(s.WidthRight),
                new JsonArray(s.Plan.Select(p => (JsonNode)new JsonArray(R(p[0]), R(p[1]), R(s.Z))).ToArray()))).ToArray());
        var openings = Try(() => e is OfflineEtabs o ? o.Model is var mm ? CsiProMax.Model.SlabDefs.Openings(mm).Select(x => (x.Name, Z: StripZ(mm, x.Story), x.Plan)).ToList() : null : null);
        if (openings is { Count: > 0 })
            model["openings"] = new JsonArray(openings.Select(x => (JsonNode)new JsonArray(x.Name, new JsonArray(x.Plan.Select(p => (JsonNode)new JsonArray(R(p[0]), R(p[1]), R(x.Z))).ToArray()))).ToArray());
        if (supports is { Count: > 0 })
            model["supports"] = new JsonArray(supports.Take(MostSupports).Select(s => (JsonNode)new JsonArray(R(s.X), R(s.Y), R(s.Z), Kind(s.Restraint))).ToArray());
    }

    private static double StripZ(CsiProMax.Model.StructuralModel m, string story)
    {
        var i = story.Length == 0 ? -1 : m.StoryIndex(story);
        if (i < 0 && m.Areas.Exists(a => a.Kind == CsiProMax.Model.AreaKind.Floor)) i = m.StoryIndex(F2k.F2kWriter.BusiestStory(m));
        return i < 0 ? 0 : m.Elevation(i);
    }

    /// <summary>A live program's tendons from its "Tendon Discretized Points" table (ETABS and SAFE: Name, Story, PointNum, GlobalX/Y/Z).</summary>
    private static List<(string Name, string Story, List<double[]>)> TendonsFromTables(ICsiLink e)
    {
        var table = e.Tables().FirstOrDefault(t => !t.Empty && ResultTools.Norm(t.Name) == "tendondiscretizedpoints");
        if (table is null) return [];
        var data = e.ReadTable(table.Key, null, null);
        int name = ResultTools.Col(data.Fields, "Name", "Tendon"), story = ResultTools.Col(data.Fields, "Story"), n = ResultTools.Col(data.Fields, "PointNum", "Point Number"),
            x = ResultTools.Col(data.Fields, "GlobalX", "Global X"), y = ResultTools.Col(data.Fields, "GlobalY", "Global Y"), z = ResultTools.Col(data.Fields, "GlobalZ", "Global Z");
        if (name < 0 || x < 0 || y < 0 || z < 0) return [];
        static double D(string v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        return data.Rows.GroupBy(r => (r[name], story < 0 ? "" : r[story]))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.OrderBy(r => n < 0 ? 0 : D(r[n])).Select(r => new[] { D(r[x]), D(r[y]), D(r[z]) }).ToList())).ToList();
    }

    /// <summary>fixed, pinned, roller or other: what the support symbol shows.</summary>
    public static string Kind(bool[] r)
    {
        if (r.Length < 6) return "other";
        if (r.All(v => v)) return "fixed";
        if (r[0] && r[1] && r[2] && !r[3] && !r[4] && !r[5]) return "pinned";
        if (r[2] && !r[0] && !r[1] && !r[3] && !r[4] && !r[5]) return "roller";
        return "other";
    }

    private static double R(double v) => Math.Round(v, 5);

    private static T? Try<T>(Func<T> read) where T : class
    {
        try { return read(); }
        catch (Exception ex) when (ex is EtabsException or InvalidOperationException or FormatException or IndexOutOfRangeException or InvalidCastException) { return null; }
    }

    /// <summary>The program's grid table, read by loose names: a label, a line type that starts with X or Y, and an ordinate.</summary>
    private static IReadOnlyList<GridRow> GridsFromTables(ICsiLink e)
    {
        var table = e.Tables().FirstOrDefault(t => !t.Empty && ResultTools.Norm(t.Name).StartsWith("griddefinitionsgridlines", StringComparison.Ordinal))
            ?? e.Tables().FirstOrDefault(t => !t.Empty && ResultTools.Norm(t.Name) == "griddefinitions")
            // SAP2000's own: "Grid Lines" (CoordSys, AxisDir, GridID, XRYZCoord).
            ?? e.Tables().FirstOrDefault(t => !t.Empty && ResultTools.Norm(t.Name) == "gridlines");
        if (table is null) return [];
        var data = e.ReadTable(table.Key, null, null);
        int label = ResultTools.Col(data.Fields, "ID", "Label", "GridID", "Grid ID"), type = ResultTools.Col(data.Fields, "LineType", "Grid Line Type", "Direction", "AxisDir"),
            at = ResultTools.Col(data.Fields, "Ordinate", "Coordinate", "XRYZCoord");
        if (label < 0 || type < 0 || at < 0) return [];
        var rows = new List<GridRow>();
        foreach (var r in data.Rows)
        {
            var d = r[type].TrimStart().ToUpperInvariant();
            if (d.Length == 0 || (d[0] != 'X' && d[0] != 'Y')) continue;
            if (double.TryParse(r[at], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c))
                rows.Add(new GridRow(r[label], d[..1], c));
        }
        return rows;
    }

    /// <summary>
    /// Frame sections asked of the program one by one: rectangles, I shapes, pipes, tubes, circles, channels, tees and
    /// angles, each by its own call. A section none of them answers (a general or designer section) is left out and
    /// drawn as a line.
    /// </summary>
    private static IReadOnlyList<SectionSize> FrameSizesFromProgram(ICsiLink e)
    {
        if (e is not ComEtabs) return [];
        var rows = new List<SectionSize>();
        foreach (var name in e.FrameSections().Take(MostSections))
            if (e.Raw("read a section's size", m => SizeOf((object)m, name)) is SectionSize size) rows.Add(size);
        return rows;
    }

    private static SectionSize? SizeOf(object program, string name)
    {
        dynamic m = program;
        string file = "", mat = "", notes = "", guid = ""; double t3 = 0, t2 = 0, tf = 0, tw = 0, t2b = 0, tfb = 0; int colour = 0;
        bool Ok(object result) => Convert.ToInt32(result) == 0 && t3 > 0;
        try { if (Ok((object)m.PropFrame.GetRectangle(name, ref file, ref mat, ref t3, ref t2, ref colour, ref notes, ref guid)) && t2 > 0) return new SectionSize(name, "Rectangular", t3, t2, 0, 0, mat); } catch (Exception) { }
        try { if (Ok((object)m.PropFrame.GetISection(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref t2b, ref tfb, ref colour, ref notes, ref guid))) return new SectionSize(name, "I/Wide Flange", t3, Math.Max(t2, t2b), Math.Max(tf, tfb), tw, mat); } catch (Exception) { }
        try { if (Ok((object)m.PropFrame.GetTube(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref colour, ref notes, ref guid))) return new SectionSize(name, "Box/Tube", t3, t2, tf, tw, mat); } catch (Exception) { }
        try { if (Ok((object)m.PropFrame.GetPipe(name, ref file, ref mat, ref t3, ref tw, ref colour, ref notes, ref guid))) return new SectionSize(name, "Pipe", t3, 0, 0, tw, mat); } catch (Exception) { }
        try { if (Ok((object)m.PropFrame.GetCircle(name, ref file, ref mat, ref t3, ref colour, ref notes, ref guid))) return new SectionSize(name, "Circle", t3, 0, 0, 0, mat); } catch (Exception) { }
        try { if (Ok((object)m.PropFrame.GetChannel(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref colour, ref notes, ref guid))) return new SectionSize(name, "Channel", t3, t2, tf, tw, mat); } catch (Exception) { }
        try { if (Ok((object)m.PropFrame.GetTee(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref colour, ref notes, ref guid))) return new SectionSize(name, "Tee", t3, t2, tf, tw, mat); } catch (Exception) { }
        try { if (Ok((object)m.PropFrame.GetAngle(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref colour, ref notes, ref guid))) return new SectionSize(name, "Angle", t3, t2, tf, tw, mat); } catch (Exception) { }
        return null;
    }

    /// <summary>The restrained points on the lowest level: where a building's supports are.</summary>
    private static IReadOnlyList<SupportRow> SupportsAtBase(ICsiLink e, IReadOnlyList<PointRow> points)
    {
        if (points.Count == 0) return [];
        var low = points.Min(p => p.Z);
        var rows = new List<SupportRow>();
        foreach (var p in points.Where(p => Math.Abs(p.Z - low) < 1e-6).Take(MostSupports))
        {
            var r = e.Restraint(p.Name);
            if (r.Any(v => v)) rows.Add(new SupportRow(p.X, p.Y, p.Z, r));
        }
        return rows;
    }
}
