using System.Globalization;
using System.IO;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace EtabsProMax.Tools;

/// <summary>
/// Reads an AutoCAD drawing (DWG or DXF) attached to the chat as data: its layers with what they hold, its text, and
/// its lines, polylines, circles, arcs and block inserts with coordinates, so grids, column positions, slab outlines
/// and wall lines can be taken from an architectural or structural drawing and built in ETABS. Reads the file only.
/// </summary>
public sealed class ReadCadDrawingTool : IEtabsTool
{
    public const int DefaultMost = 300, HardMost = 2000;

    public string Name => "read_cad_drawing";
    public string Description =>
        "Read an AutoCAD drawing (.dwg or .dxf) on this PC as data. Without 'layers' it gives the overview: the drawing's " +
        "extents, every layer with its counts of lines, polylines, circles, arcs, text and block inserts, and the block " +
        "names with how often each is inserted. With 'layers' (and optionally 'kinds') it lists the objects on those " +
        "layers with coordinates in the drawing's own units: lines (x1,y1,x2,y2), polylines (points, closed), circles " +
        "(x,y,r), arcs, text (x,y,text) and inserts (block,x,y,rotation). Read the overview first, pick the layers that " +
        "hold grids, columns, walls and slab edges by their names and counts, then read those. Drawings are often in mm: " +
        "compare a known dimension with the model's units before building from it. Not a model change.";
    public string PropertiesJson => """
    {
      "path": {"type": "string", "description": "The drawing's full path."},
      "layers": {"type": "array", "items": {"type": "string"}, "description": "Layer names to list objects from (as in the overview). Leave out for the overview."},
      "kinds": {"type": "array", "items": {"type": "string", "enum": ["line", "polyline", "circle", "arc", "text", "insert"]}, "description": "Only these kinds. Default all."},
      "offset": {"type": "integer", "description": "Skip this many objects (for the next page)."},
      "limit": {"type": "integer", "description": "At most this many objects (default 300, up to 2000)."}
    }
    """;
    public string[] Required => ["path"];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args) =>
        Read(args.OptString("path") ?? "", args.StringList("layers"), args.StringList("kinds"), (int)(args.OptLong("offset") ?? 0), (int)(args.OptLong("limit") ?? DefaultMost));

    internal static ToolResult Read(string path, List<string> layers, List<string> kinds, int offset, int limit)
    {
        path = path.Trim().Trim('"');
        if (!File.Exists(path)) return ToolResult.Error($"There is no file at {path}.");
        CadDocument doc;
        try { doc = Open(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ToolResult.Error($"The drawing could not be read ({ex.GetType().Name}: {ex.Message}). Ask the user to save it as DXF, or as an older DWG version (AutoCAD 2018 or 2013), and attach that; a PDF of it can be read with read_drawing.");
        }
        var all = doc.Entities.Select(Describe).Where(d => d is not null).Select(d => d!).ToList();
        if (layers.Count == 0)
        {
            var box = Extents(all);
            return ToolResult.Ok(new
            {
                file = path,
                version = doc.Header.Version.ToString(),
                objects = all.Count,
                extents = box is null ? null : new { min_x = R(box.Value.MinX), min_y = R(box.Value.MinY), max_x = R(box.Value.MaxX), max_y = R(box.Value.MaxY), width = R(box.Value.MaxX - box.Value.MinX), height = R(box.Value.MaxY - box.Value.MinY) },
                layers = all.GroupBy(d => d.Layer, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count())
                    .Select(g => new { layer = g.Key, counts = g.GroupBy(d => d.Kind).ToDictionary(k => k.Key, k => k.Count()) }).Take(200),
                blocks = all.Where(d => d.Kind == "insert").GroupBy(d => d.Text ?? "").OrderByDescending(g => g.Count()).Take(60).Select(g => new { block = g.Key, inserts = g.Count() }),
                sample_text = all.Where(d => d.Kind == "text" && !string.IsNullOrWhiteSpace(d.Text)).Select(d => d.Text!).Distinct().Take(60),
                next = "Call again with 'layers' to list the objects on the layers you need.",
            });
        }
        var wantLayers = new HashSet<string>(layers.Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
        var wantKinds = new HashSet<string>(kinds.Select(k => k.Trim().ToLowerInvariant()));
        var unknown = wantLayers.Where(l => !all.Any(d => d.Layer.Equals(l, StringComparison.OrdinalIgnoreCase))).ToList();
        var picked = all.Where(d => wantLayers.Contains(d.Layer) && (wantKinds.Count == 0 || wantKinds.Contains(d.Kind))).ToList();
        limit = Math.Clamp(limit <= 0 ? DefaultMost : limit, 1, HardMost);
        offset = Math.Max(0, offset);
        var page = picked.Skip(offset).Take(limit).ToList();
        return ToolResult.Ok(new
        {
            file = path,
            total = picked.Count,
            offset,
            returned = page.Count,
            more = offset + page.Count < picked.Count ? $"Call again with offset {offset + page.Count} for the next ones." : null,
            layers_not_found = unknown.Count > 0 ? unknown : null,
            objects = page.Select(d => d.Json),
        });
    }

    private static CadDocument Open(string path)
    {
        if (Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            using var dxf = new DxfReader(path);
            return dxf.Read();
        }
        using var dwg = new DwgReader(path);
        return dwg.Read();
    }

    internal sealed record Item(string Kind, string Layer, string? Text, double[] Points, object Json);

    private static double R(double v) => Math.Round(v, 3);

    private static Item? Describe(Entity e)
    {
        var layer = e.Layer?.Name ?? "0";
        switch (e)
        {
            case Line l:
                return new Item("line", layer, null, [l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y],
                    new { kind = "line", layer, x1 = R(l.StartPoint.X), y1 = R(l.StartPoint.Y), x2 = R(l.EndPoint.X), y2 = R(l.EndPoint.Y) });
            case LwPolyline p:
            {
                var pts = p.Vertices.Select(v => new[] { R(v.Location.X), R(v.Location.Y) }).ToList();
                return new Item("polyline", layer, null, pts.SelectMany(v => v).ToArray(), new { kind = "polyline", layer, closed = p.IsClosed, points = pts });
            }
            case Polyline p:
            {
                var pts = p.Vertices.Select(v => new[] { R(v.Location.X), R(v.Location.Y) }).ToList();
                return new Item("polyline", layer, null, pts.SelectMany(v => v).ToArray(), new { kind = "polyline", layer, closed = p.IsClosed, points = pts });
            }
            case Arc a:
                return new Item("arc", layer, null, [a.Center.X - a.Radius, a.Center.Y - a.Radius, a.Center.X + a.Radius, a.Center.Y + a.Radius],
                    new { kind = "arc", layer, x = R(a.Center.X), y = R(a.Center.Y), r = R(a.Radius), start_deg = R(a.StartAngle * 180 / Math.PI), end_deg = R(a.EndAngle * 180 / Math.PI) });
            case Circle c:
                return new Item("circle", layer, null, [c.Center.X - c.Radius, c.Center.Y - c.Radius, c.Center.X + c.Radius, c.Center.Y + c.Radius],
                    new { kind = "circle", layer, x = R(c.Center.X), y = R(c.Center.Y), r = R(c.Radius) });
            case MText m:
                return new Item("text", layer, Plain(m.Value), [m.InsertPoint.X, m.InsertPoint.Y],
                    new { kind = "text", layer, x = R(m.InsertPoint.X), y = R(m.InsertPoint.Y), height = R(m.Height), text = Plain(m.Value) });
            case TextEntity t:
                return new Item("text", layer, t.Value, [t.InsertPoint.X, t.InsertPoint.Y],
                    new { kind = "text", layer, x = R(t.InsertPoint.X), y = R(t.InsertPoint.Y), height = R(t.Height), text = t.Value });
            case Insert i:
                return new Item("insert", layer, i.Block?.Name ?? "", [i.InsertPoint.X, i.InsertPoint.Y],
                    new { kind = "insert", layer, block = i.Block?.Name ?? "", x = R(i.InsertPoint.X), y = R(i.InsertPoint.Y), rotation_deg = R(i.Rotation * 180 / Math.PI), scale = R(i.XScale) });
            default:
                return null;
        }
    }

    /// <summary>MText without its formatting codes ({\fArial|b0;Text}, \P line breaks).</summary>
    internal static string Plain(string? value)
    {
        var s = value ?? "";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[fFHhCcQqTtWwAaLlOoKk][^;\\]*;", "");
        s = s.Replace("\\P", " ").Replace("\\~", " ").Replace("{", "").Replace("}", "");
        return s.Trim();
    }

    private static (double MinX, double MinY, double MaxX, double MaxY)? Extents(List<Item> items)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var item in items)
            for (var i = 0; i + 1 < item.Points.Length; i += 2)
            {
                var (x, y) = (item.Points[i], item.Points[i + 1]);
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return minX > maxX ? null : (minX, minY, maxX, maxY);
    }
}

/// <summary>Writes the chat's drawing ('entities': lines, polylines, circles and text on named layers) as an AutoCAD DWG or DXF file.</summary>
internal static class CadOut
{
    /// <summary>A layer name CAD programs take: letters, digits, - and _ (others become _), 31 characters at most.</summary>
    internal static string Layer(string? name)
    {
        var clean = new string((name ?? "").Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').Take(31).ToArray());
        return clean.Length == 0 ? "0" : clean.ToUpperInvariant();
    }

    /// <summary>Writes a .dwg, or a .dxf when the path ends so. Nothing is written when nothing could be drawn.</summary>
    public static (int Drawn, int Skipped) Write(string path, IEnumerable<JsonElement> entities)
    {
        var doc = new CadDocument(ACadVersion.AC1027);
        var layers = new Dictionary<string, ACadSharp.Tables.Layer>(StringComparer.OrdinalIgnoreCase);
        ACadSharp.Tables.Layer LayerOf(string? name)
        {
            var clean = Layer(name);
            if (layers.TryGetValue(clean, out var known)) return known;
            if (!doc.Layers.TryGetValue(clean, out var layer))
            {
                layer = new ACadSharp.Tables.Layer(clean);
                doc.Layers.Add(layer);
            }
            return layers[clean] = layer;
        }
        int drawn = 0, skipped = 0;
        foreach (var e in entities)
        {
            if (e.ValueKind != JsonValueKind.Object) { skipped++; continue; }
            Entity? made = null;
            switch ((Str(e, "type") ?? "").Trim().ToLowerInvariant())
            {
                case "line" when Num(e, "x1") is { } x1 && Num(e, "y1") is { } y1 && Num(e, "x2") is { } x2 && Num(e, "y2") is { } y2:
                    made = new Line(new XYZ(x1, y1, Num(e, "z1") ?? 0), new XYZ(x2, y2, Num(e, "z2") ?? 0));
                    break;
                case "polyline" when e.TryGetProperty("points", out var ps) && ps.ValueKind == JsonValueKind.Array:
                {
                    var poly = new LwPolyline();
                    foreach (var p in ps.EnumerateArray())
                        if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 && p[0].TryGetDouble(out var x) && p[1].TryGetDouble(out var y))
                            poly.Vertices.Add(new LwPolyline.Vertex(new XY(x, y)));
                    if (poly.Vertices.Count < 2) break;
                    poly.IsClosed = e.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True;
                    made = poly;
                    break;
                }
                case "circle" when Num(e, "x") is { } cx && Num(e, "y") is { } cy && Num(e, "r") is { } r and > 0:
                    made = new Circle { Center = new XYZ(cx, cy, 0), Radius = r };
                    break;
                case "text" when Num(e, "x") is { } tx && Num(e, "y") is { } ty && Str(e, "text") is { Length: > 0 } text:
                    made = new TextEntity { InsertPoint = new XYZ(tx, ty, 0), Height = Num(e, "height") is { } h and > 0 ? h : 0.25, Value = text.Replace('\r', ' ').Replace('\n', ' ') };
                    break;
            }
            if (made is null) { skipped++; continue; }
            made.Layer = LayerOf(Str(e, "layer"));
            doc.Entities.Add(made);
            drawn++;
        }
        if (drawn == 0) return (0, skipped);
        if (Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            using var dxf = new DxfWriter(path, doc, false);
            dxf.Write();
        }
        else
        {
            using var dwg = new DwgWriter(path, doc);
            dwg.Write();
        }
        return (drawn, skipped);
    }

    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
}
