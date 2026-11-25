namespace CsiProMax.Model;

public readonly record struct Xyz(double X, double Y, double Z);

/// <summary>One member where it really is in space: a line or area object on one story, with its section.</summary>
public sealed record PhysicalFrame(string Label, string Story, LineKind Kind, string? Section, Xyz I, Xyz J)
{
    public double Length => Math.Sqrt(Math.Pow(J.X - I.X, 2) + Math.Pow(J.Y - I.Y, 2) + Math.Pow(J.Z - I.Z, 2));
}

public sealed record PhysicalArea(string Label, string Story, AreaKind Kind, string? Section, IReadOnlyList<Xyz> Corners)
{
    /// <summary>The surface area of the (flat) polygon, by its vector area.</summary>
    public double Area
    {
        get
        {
            double x = 0, y = 0, z = 0;
            for (var i = 0; i < Corners.Count; i++)
            {
                var a = Corners[i]; var b = Corners[(i + 1) % Corners.Count];
                x += a.Y * b.Z - a.Z * b.Y;
                y += a.Z * b.X - a.X * b.Z;
                z += a.X * b.Y - a.Y * b.X;
            }
            return Math.Sqrt(x * x + y * y + z * z) / 2;
        }
    }
}

/// <summary>Turns the ETABS-style model (plan points, objects, per-story assignments) into members in 3D, for
/// drawing, checking and measuring. Assignments that point at something missing are skipped here; the checks report them.</summary>
public static class Physical
{
    public static IEnumerable<PhysicalFrame> Frames(StructuralModel m)
    {
        var points = PointMap(m);
        var lines = m.Lines.GroupBy(l => l.Label).ToDictionary(g => g.Key, g => g.First());
        foreach (var a in m.LineAssigns)
        {
            var story = m.StoryIndex(a.Story);
            if (story < 0 || !lines.TryGetValue(a.Label, out var line)) continue;
            if (!points.TryGetValue(line.PointI, out var pi) || !points.TryGetValue(line.PointJ, out var pj)) continue;
            if (story - line.StoriesBelowI < 0) continue;
            yield return new PhysicalFrame(a.Label, a.Story, line.Kind, a.Section,
                At(m, pi, story - line.StoriesBelowI), At(m, pj, story));
        }
    }

    public static IEnumerable<PhysicalArea> Areas(StructuralModel m)
    {
        var points = PointMap(m);
        var areas = m.Areas.GroupBy(a => a.Label).ToDictionary(g => g.Key, g => g.First());
        foreach (var a in m.AreaAssigns)
        {
            var story = m.StoryIndex(a.Story);
            if (story < 0 || !areas.TryGetValue(a.Label, out var area)) continue;
            var corners = new List<Xyz>();
            for (var i = 0; i < area.PointNames.Count; i++)
            {
                var below = i < area.StoriesBelow.Count ? area.StoriesBelow[i] : 0;
                if (!points.TryGetValue(area.PointNames[i], out var p) || story - below < 0) { corners.Clear(); break; }
                corners.Add(At(m, p, story - below));
            }
            if (corners.Count >= 3) yield return new PhysicalArea(a.Label, a.Story, area.Kind, a.Section, corners);
        }
    }

    public static Xyz At(StructuralModel m, PlanPoint p, int storyIndex) => new(p.X, p.Y, m.Elevation(storyIndex) - p.Below);

    private static Dictionary<string, PlanPoint> PointMap(StructuralModel m) =>
        m.Points.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
}
