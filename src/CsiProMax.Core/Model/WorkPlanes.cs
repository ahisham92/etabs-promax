namespace CsiProMax.Model;

/// <summary>
/// A plane to draw on in the Modeler's 3-D view (Define > Work Planes): three points of it, not on one line, as
/// [x, y, z] in the model's units. The Modeler's own: kept in its file, not written to the programs' text files.
/// </summary>
public sealed class WorkPlane
{
    public string Name { get; set; } = "";
    public List<double[]> Points { get; set; } = [];
}

/// <summary>
/// The geometry of work planes: a plane from three points, from two frames in one plane, from a frame and a point off
/// it, or from a flat area, each refused with the reason when it makes no plane (points on one line, frames on one
/// line or skew, a point on the frame's line, an area not flat).
/// </summary>
public static class WorkPlanes
{
    private static double[] Sub(Xyz a, Xyz b) => [a.X - b.X, a.Y - b.Y, a.Z - b.Z];
    private static double[] Cross(double[] u, double[] v) => [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
    private static double Dot(double[] u, double[] v) => u[0] * v[0] + u[1] * v[1] + u[2] * v[2];
    private static double Len(double[] u) => Math.Sqrt(Dot(u, u));
    private static double[] Arr(Xyz p) => [p.X, p.Y, p.Z];
    private static Xyz P(double[] p) => new(p[0], p[1], p[2]);

    // Lengths are compared with the model's size, so a plane is judged the same in metres and in millimetres.
    private static double Scale(IEnumerable<Xyz> points) => Math.Max(1, points.Max(p => Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)))));
    private static bool OnLine(Xyz a, Xyz b, Xyz c, double scale) => Len(Cross(Sub(b, a), Sub(c, a))) <= 1e-9 * scale * scale;

    /// <summary>The plane's unit normal.</summary>
    public static double[] Normal(WorkPlane w)
    {
        var n = Cross(Sub(P(w.Points[1]), P(w.Points[0])), Sub(P(w.Points[2]), P(w.Points[0])));
        var l = Len(n);
        return [n[0] / l, n[1] / l, n[2] / l];
    }

    /// <summary>How far a point is from the plane.</summary>
    public static double Distance(WorkPlane w, Xyz q) => Math.Abs(Dot(Normal(w), Sub(q, P(w.Points[0]))));

    /// <summary>A plane through three points, not on one line.</summary>
    public static List<double[]> FromPoints(IReadOnlyList<Xyz> points)
    {
        if (points.Count != 3) throw new ArgumentException("A work plane takes three points.");
        if (OnLine(points[0], points[1], points[2], Scale(points))) throw new ArgumentException("The three points are on one line, so they make no plane.");
        return points.Select(Arr).ToList();
    }

    /// <summary>
    /// A plane through two frames that lie in it: crossing or meeting, or parallel and apart. Two frames on one line
    /// make no plane, and two skew frames (not parallel and never meeting) lie in no one plane.
    /// </summary>
    public static List<double[]> FromFrames(Xyz a1, Xyz a2, Xyz b1, Xyz b2)
    {
        var scale = Scale([a1, a2, b1, b2]);
        var u = Sub(a2, a1);
        if (Len(u) <= 1e-9 * scale || Len(Sub(b2, b1)) <= 1e-9 * scale) throw new ArgumentException("A frame with no length makes no plane.");
        // A point of the second frame off the first one's line: with the first frame, the plane (if there is one).
        var off = !OnLine(a1, a2, b1, scale) ? b1 : !OnLine(a1, a2, b2, scale) ? b2 : (Xyz?)null;
        if (off is not { } c) throw new ArgumentException("The two frames are on one line, so they make no plane.");
        var plane = new WorkPlane { Points = [Arr(a1), Arr(a2), Arr(c)] };
        var other = c.Equals(b1) ? b2 : b1;
        if (Distance(plane, other) > 1e-6 * scale) throw new ArgumentException("The two frames are skew (not parallel and never meeting), so no one plane holds them both.");
        return plane.Points;
    }

    /// <summary>A plane through a frame and a point off its line.</summary>
    public static List<double[]> FromPointAndFrame(Xyz point, Xyz a, Xyz b)
    {
        var scale = Scale([point, a, b]);
        if (Len(Sub(b, a)) <= 1e-9 * scale) throw new ArgumentException("A frame with no length makes no plane.");
        if (OnLine(a, b, point, scale)) throw new ArgumentException("The point is on the frame's line, so they make no plane.");
        return [Arr(a), Arr(b), Arr(point)];
    }

    /// <summary>The plane of a flat area: three of its corners as far from one line as they go. A warped area is refused.</summary>
    public static List<double[]> FromCorners(IReadOnlyList<Xyz> corners)
    {
        if (corners.Count < 3) throw new ArgumentException("An area needs three corners or more.");
        var scale = Scale(corners);
        var (best, bi, bj) = (0.0, -1, -1);
        for (var i = 1; i < corners.Count; i++)
            for (var j = i + 1; j < corners.Count; j++)
            {
                var size = Len(Cross(Sub(corners[i], corners[0]), Sub(corners[j], corners[0])));
                if (size > best) (best, bi, bj) = (size, i, j);
            }
        if (bi < 0 || best <= 1e-9 * scale * scale) throw new ArgumentException("The area's corners are on one line, so they make no plane.");
        var plane = new WorkPlane { Points = [Arr(corners[0]), Arr(corners[bi]), Arr(corners[bj])] };
        if (corners.Any(q => Distance(plane, q) > 1e-6 * scale)) throw new ArgumentException("The area is not flat (its corners are not in one plane), so it makes no work plane.");
        return plane.Points;
    }
}
