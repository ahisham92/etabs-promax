using System.Text.Json;

namespace CsiProMax.Model;

/// <summary>
/// The Modeler's Edit menu on the model itself: Replicate (linear, or to other stories), Move and Divide Frames, as
/// ETABS's Edit menu does them. A copy keeps everything the original has on its story: section, assignments (releases,
/// modifiers, springs, groups, pier labels, ...), loads and hinges. Pure, so the tests run it.
/// </summary>
public static class ModelOps
{
    static double Hyp(double a, double b) => Math.Sqrt(a * a + b * b);
    /// <summary>An object of the model on one story: a frame (line) or a slab or wall (area).</summary>
    public sealed record Obj(bool Line, string Label, string Story)
    {
        public override string ToString() => Label + "@" + Story;
    }

    /// <summary>Finds label@story among the frames, then the slabs and walls.</summary>
    public static Obj Find(StructuralModel m, string name)
    {
        var at = name.LastIndexOf('@');
        if (at <= 0) throw new ArgumentException($"'{name}' is not label@story.");
        var (label, story) = (name[..at], name[(at + 1)..]);
        if (m.LineAssigns.Exists(a => a.Label == label && a.Story == story)) return new Obj(true, label, story);
        if (m.AreaAssigns.Exists(a => a.Label == label && a.Story == story)) return new Obj(false, label, story);
        throw new ArgumentException($"There is no frame, slab or wall {name}.");
    }

    private static T Clone<T>(T x) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(x))!;

    /// <summary>
    /// Copies an object by (dx, dy) in plan onto <paramref name="toStory"/> (its own story when null), with its
    /// assignments, loads and hinges. Says no where the copy would land on the object itself or on one already there.
    /// </summary>
    public static Obj Copy(StructuralModel m, Obj o, double dx, double dy, string? toStory = null)
    {
        var story = toStory ?? o.Story;
        if (m.StoryIndex(story) < 0) throw new ArgumentException($"There is no story '{story}'.");
        var same = Math.Abs(dx) < ModelEdit.Tolerance && Math.Abs(dy) < ModelEdit.Tolerance;
        if (same && story == o.Story) throw new ArgumentException($"{o} would be copied onto itself: give dx, dy or another story.");
        return CopyMapped(m, o, story, same, p =>
        {
            if (same) return p;
            if (p.Below != 0) throw new ArgumentException($"{o} has a point set below its story ({p.Name}); move it in the program.");
            return m.PointAt(p.X + dx, p.Y + dy);
        });
    }

    /// <summary>
    /// Draw > Reshape Object: moves the corners or ends of one object that stand at (fromX, fromY) in plan to
    /// (toX, toY), on its own story only, keeping its section, assignments, loads and hinges. A column moves whole
    /// (both its ends are at the one plan point).
    /// </summary>
    public static Obj Reshape(StructuralModel m, Obj o, double fromX, double fromY, double toX, double toY)
    {
        if (Math.Abs(fromX - toX) < ModelEdit.Tolerance && Math.Abs(fromY - toY) < ModelEdit.Tolerance) throw new ArgumentException("Give a new place for the point: it is where it was.");
        var names = o.Line ? new List<string> { m.Line(o.Label)!.PointI, m.Line(o.Label)!.PointJ } : m.Area(o.Label)!.PointNames;
        bool At(PlanPoint p) => Math.Abs(p.X - fromX) <= ModelEdit.Tolerance * 10 && Math.Abs(p.Y - fromY) <= ModelEdit.Tolerance * 10;
        if (!names.Select(n => m.Point(n)).Any(p => p is not null && At(p))) throw new ArgumentException($"{o} has no end or corner at ({fromX}, {fromY}).");
        var made = CopyMapped(m, o, o.Story, false, p =>
        {
            if (!At(p)) return p;
            if (p.Below != 0) throw new ArgumentException($"{o} has a point set below its story ({p.Name}); reshape it in the program.");
            return m.PointAt(toX, toY);
        });
        if (made.Line) { var l = m.Line(made.Label)!; if (l.Kind != LineKind.Column && l.PointI == l.PointJ && l.StoriesBelowI == 0) { Remove(m, made); throw new ArgumentException($"{o} would have both its ends at one point."); } }
        else { var a = m.Area(made.Label)!; if (a.PointNames.Zip(a.StoriesBelow).Distinct().Count() < 3) { Remove(m, made); throw new ArgumentException($"{o} would have fewer than three corners."); } }
        Remove(m, o);
        return made;
    }

    private static Obj CopyMapped(StructuralModel m, Obj o, string story, bool same, Func<PlanPoint, PlanPoint> map)
    {
        PlanPoint Moved(string name) => map(m.Point(name) ?? throw new ArgumentException($"{o} has a point {name} the model does not have."));
        if (o.Line)
        {
            var line = m.Line(o.Label)!;
            if (line.StoriesBelowI > 0 && m.StoryIndex(story) < line.StoriesBelowI) throw new ArgumentException($"{o} runs from the story below; {story} has none below it.");
            var (i, j) = (Moved(line.PointI), Moved(line.PointJ));
            var target = same ? line : m.Lines.Find(l => l.Kind == line.Kind && l.StoriesBelowI == line.StoriesBelowI && l.PointI == i.Name && l.PointJ == j.Name);
            if (target is null)
            {
                target = new LineObject { Label = ModelEdit.NextName(m.Lines.Select(l => l.Label), Prefix(line.Label, line.Kind == LineKind.Beam ? "B" : line.Kind == LineKind.Column ? "C" : "D")), Kind = line.Kind, PointI = i.Name, PointJ = j.Name, StoriesBelowI = line.StoriesBelowI };
                m.Lines.Add(target);
            }
            if (m.LineAssigns.Exists(a => a.Label == target.Label && a.Story == story)) throw new ArgumentException($"There is already a frame {target.Label}@{story} where {o} would be copied to.");
            var assign = Clone(m.LineAssigns.First(a => a.Label == o.Label && a.Story == o.Story));
            assign.Label = target.Label; assign.Story = story;
            m.LineAssigns.Add(assign);
            foreach (var l in m.LineLoads.Where(l => l.Object == o.Label && l.Story == o.Story).ToList()) { var c = Clone(l); c.Object = target.Label; c.Story = story; m.LineLoads.Add(c); }
            var hinges = PropertyDefs.HingesOf(m, o.Label, o.Story);
            if (hinges.Count > 0) PropertyDefs.SetHinges(m, target.Label, story, hinges);
            return new Obj(true, target.Label, story);
        }
        var area = m.Area(o.Label)!;
        if (area.StoriesBelow.Any(b => b > 0) && m.StoryIndex(story) < area.StoriesBelow.Max()) throw new ArgumentException($"{o} runs from the story below; {story} has none below it.");
        var names = area.PointNames.Select(p => Moved(p).Name).ToList();
        var a2 = same ? area : m.Areas.Find(a => a.Kind == area.Kind && a.PointNames.SequenceEqual(names) && a.StoriesBelow.SequenceEqual(area.StoriesBelow));
        if (a2 is null)
        {
            a2 = new AreaObject { Label = ModelEdit.NextName(m.Areas.Select(a => a.Label), Prefix(area.Label, area.Kind == AreaKind.Wall ? "W" : "F")), Kind = area.Kind, PointNames = names, StoriesBelow = [.. area.StoriesBelow] };
            m.Areas.Add(a2);
        }
        if (m.AreaAssigns.Exists(a => a.Label == a2.Label && a.Story == story)) throw new ArgumentException($"There is already a slab or wall {a2.Label}@{story} where {o} would be copied to.");
        var aa = Clone(m.AreaAssigns.First(a => a.Label == o.Label && a.Story == o.Story));
        aa.Label = a2.Label; aa.Story = story;
        m.AreaAssigns.Add(aa);
        foreach (var l in m.AreaLoads.Where(l => l.Object == o.Label && l.Story == o.Story).ToList()) { var c = Clone(l); c.Object = a2.Label; c.Story = story; m.AreaLoads.Add(c); }
        return new Obj(false, a2.Label, story);
    }

    /// <summary>The letters a label starts with (B12 gives B), or the kind's own when it has none.</summary>
    private static string Prefix(string label, string fallback)
    {
        var p = new string(label.TakeWhile(char.IsLetter).ToArray());
        return p.Length > 0 ? p : fallback;
    }

    /// <summary>Takes an object off its story: its assignment, loads and hinges; the object itself when no story has it any more.</summary>
    public static void Remove(StructuralModel m, Obj o)
    {
        if (o.Line)
        {
            PropertyDefs.SetHinges(m, o.Label, o.Story, []);
            m.LineAssigns.RemoveAll(a => a.Label == o.Label && a.Story == o.Story);
            m.LineLoads.RemoveAll(l => l.Object == o.Label && l.Story == o.Story);
            if (!m.LineAssigns.Exists(a => a.Label == o.Label)) m.Lines.RemoveAll(l => l.Label == o.Label);
        }
        else
        {
            m.AreaAssigns.RemoveAll(a => a.Label == o.Label && a.Story == o.Story);
            m.AreaLoads.RemoveAll(l => l.Object == o.Label && l.Story == o.Story);
            if (!m.AreaAssigns.Exists(a => a.Label == o.Label)) m.Areas.RemoveAll(a => a.Label == o.Label);
        }
    }

    /// <summary>Edit > Replicate, Linear: <paramref name="number"/> copies of each object, each (dx, dy) on from the last.</summary>
    public static List<Obj> ReplicateLinear(StructuralModel m, IReadOnlyList<Obj> objects, double dx, double dy, int number)
    {
        if (number is < 1 or > 100) throw new ArgumentException("The number of copies is from 1 to 100.");
        if (Math.Abs(dx) < ModelEdit.Tolerance && Math.Abs(dy) < ModelEdit.Tolerance) throw new ArgumentException("Give dx or dy: copies in the same place would lie on the objects.");
        var made = new List<Obj>();
        for (var n = 1; n <= number; n++)
            foreach (var o in objects) made.Add(Copy(m, o, dx * n, dy * n));
        return made;
    }

    /// <summary>Edit > Replicate, Story: each object onto each story given, in the same place in plan.</summary>
    public static List<Obj> ReplicateStories(StructuralModel m, IReadOnlyList<Obj> objects, IReadOnlyList<string> stories)
    {
        if (stories.Count == 0) throw new ArgumentException("Give the stories to replicate to.");
        var made = new List<Obj>();
        foreach (var s in stories)
            foreach (var o in objects.Where(o => o.Story != s)) made.Add(Copy(m, o, 0, 0, s));
        if (made.Count == 0) throw new ArgumentException("Each object is already on the stories given: name other stories to replicate to.");
        return made;
    }

    /// <summary>Edit > Move: each object (dx, dy) in plan, keeping what it has.</summary>
    public static List<Obj> Move(StructuralModel m, IReadOnlyList<Obj> objects, double dx, double dy)
    {
        if (Math.Abs(dx) < ModelEdit.Tolerance && Math.Abs(dy) < ModelEdit.Tolerance) throw new ArgumentException("Give dx or dy to move by.");
        var made = objects.Select(o => Copy(m, o, dx, dy)).ToList();
        foreach (var o in objects) Remove(m, o);
        return made;
    }

    /// <summary>
    /// Edit > Edit Frames > Divide Frames: a beam into <paramref name="parts"/> equal beams, each keeping the section and
    /// assignments; uniform loads go on every part. A load at a place along the beam (a point or trapezoidal load) cannot
    /// be shared out, so a beam with one is left whole and said.
    /// </summary>
    public static List<Obj> Divide(StructuralModel m, Obj o, int parts)
    {
        if (parts is < 2 or > 20) throw new ArgumentException("A frame is divided into 2 to 20 parts.");
        return DivideAt(m, o, Enumerable.Range(1, parts - 1).Select(k => (double)k / parts).ToList());
    }

    /// <summary>
    /// Edit > Edit Frames > Divide Frames at places along a beam, each a fraction of its length from its I end (0 to 1,
    /// ends left out): Break at Intersections with Selected Frames and Joints, or with Visible Grid Lines.
    /// </summary>
    public static List<Obj> DivideAt(StructuralModel m, Obj o, IReadOnlyList<double> at)
    {
        var cuts = at.Where(t => t > 1e-6 && t < 1 - 1e-6).OrderBy(t => t).Aggregate(new List<double>(), (l, t) => { if (l.Count == 0 || t - l[^1] > 1e-6) l.Add(t); return l; });
        if (cuts.Count == 0) throw new ArgumentException($"Nothing crosses {o} between its ends: it stays whole.");
        var parts = cuts.Count + 1;
        if (!o.Line) throw new ArgumentException($"{o} is a slab or wall; Divide Frames divides frames.");
        var line = m.Line(o.Label)!;
        if (line.Kind != LineKind.Beam) throw new ArgumentException($"{o} is a {line.Kind.ToString().ToLowerInvariant()}; the Modeler divides beams (a column or brace is divided at its stories).");
        var loads = m.LineLoads.Where(l => l.Object == o.Label && l.Story == o.Story).ToList();
        if (loads.FirstOrDefault(l => !E2k.E2kText.Unquote(E2k.E2kText.Value(l.Tokens, "TYPE") ?? "").Equals("UNIFF", StringComparison.OrdinalIgnoreCase)
                                    && !E2k.E2kText.Unquote(E2k.E2kText.Value(l.Tokens, "TYPE") ?? "").Equals("TEMPERATURE", StringComparison.OrdinalIgnoreCase)) is { } placed)
            throw new ArgumentException($"{o} has a {E2k.E2kText.Unquote(E2k.E2kText.Value(placed.Tokens, "TYPE") ?? "")} load at a place along it, which cannot be shared out between the parts: take it off first, or divide it in the program.");
        if (PropertyDefs.HingesOf(m, o.Label, o.Story).Count > 0) throw new ArgumentException($"{o} has hinges at places along it: take them off first.");
        var (i, j) = (m.Point(line.PointI)!, m.Point(line.PointJ)!);
        var assign = m.LineAssigns.First(a => a.Label == o.Label && a.Story == o.Story);
        var points = Enumerable.Range(0, parts + 1).Select(k => k == 0 ? i : k == parts ? j : m.PointAt(i.X + (j.X - i.X) * cuts[k - 1], i.Y + (j.Y - i.Y) * cuts[k - 1])).ToList();
        var (ri, rj) = PropertyDefs.ReleasesOf(assign);
        var made = new List<Obj>();
        for (var k = 0; k < parts; k++)
        {
            var part = new LineObject { Label = ModelEdit.NextName(m.Lines.Select(l => l.Label), Prefix(line.Label, "B")), Kind = LineKind.Beam, PointI = points[k].Name, PointJ = points[k + 1].Name };
            m.Lines.Add(part);
            var a = Clone(assign); a.Label = part.Label;
            // The end releases stay at the beam's own ends.
            PropertyDefs.SetReleases(a, k == 0 ? ri : [], k == parts - 1 ? rj : []);
            if (k > 0) PropertyDefs.Remove(a.Extra, "LENGTHOFFI");
            if (k < parts - 1) PropertyDefs.Remove(a.Extra, "LENGTHOFFJ");
            m.LineAssigns.Add(a);
            foreach (var l in loads) { var c = Clone(l); c.Object = part.Label; m.LineLoads.Add(c); }
            made.Add(new Obj(true, part.Label, o.Story));
        }
        Remove(m, o);
        return made;
    }

    /// <summary>
    /// Edit > Edit Frames > Divide Frames, Break at Intersections with Story Levels: a column or brace that spans more
    /// than one story becomes one per story, each from a level to the next, keeping the section and assignments;
    /// uniform loads go on every part, end releases and offsets stay at the frame's own ends. A part takes the object
    /// already standing on its story with the same ends and kind, as ETABS keeps one object over many stories.
    /// </summary>
    public static List<Obj> DivideAtLevels(StructuralModel m, Obj o)
    {
        if (!o.Line) throw new ArgumentException($"{o} is a slab or wall; Divide Frames divides frames.");
        var line = m.Line(o.Label)!;
        var n = line.StoriesBelowI;
        if (n < 2) throw new ArgumentException($"{o} spans one story: no story level lies between its ends.");
        var loads = m.LineLoads.Where(l => l.Object == o.Label && l.Story == o.Story).ToList();
        if (loads.FirstOrDefault(l => !E2k.E2kText.Unquote(E2k.E2kText.Value(l.Tokens, "TYPE") ?? "").Equals("UNIFF", StringComparison.OrdinalIgnoreCase)
                                    && !E2k.E2kText.Unquote(E2k.E2kText.Value(l.Tokens, "TYPE") ?? "").Equals("TEMPERATURE", StringComparison.OrdinalIgnoreCase)) is { } placed)
            throw new ArgumentException($"{o} has a {E2k.E2kText.Unquote(E2k.E2kText.Value(placed.Tokens, "TYPE") ?? "")} load at a place along it, which cannot be shared out between the parts: take it off first, or divide it in the program.");
        if (PropertyDefs.HingesOf(m, o.Label, o.Story).Count > 0) throw new ArgumentException($"{o} has hinges at places along it: take them off first.");
        var top = m.StoryIndex(o.Story);
        var (i, j) = (m.Point(line.PointI)!, m.Point(line.PointJ)!);
        double zb = m.Elevation(top - n), zt = m.Elevation(top);
        PlanPoint At(int level)
        {
            if (level == top - n) return i;
            if (level == top) return j;
            var t = (m.Elevation(level) - zb) / (zt - zb);
            return m.PointAt(i.X + (j.X - i.X) * t, i.Y + (j.Y - i.Y) * t);
        }
        var assign = m.LineAssigns.First(a => a.Label == o.Label && a.Story == o.Story);
        var (ri, rj) = PropertyDefs.ReleasesOf(assign);
        var made = new List<Obj>();
        for (var k = 0; k < n; k++)
        {
            var (pi, pj, story) = (At(top - n + k), At(top - n + k + 1), m.Stories[top - n + k + 1].Name);
            var part = m.Lines.Find(l => l.Kind == line.Kind && l.StoriesBelowI == 1 && l.PointI == pi.Name && l.PointJ == pj.Name);
            if (part is null)
            {
                part = new LineObject { Label = ModelEdit.NextName(m.Lines.Select(l => l.Label), Prefix(line.Label, line.Kind == LineKind.Column ? "C" : "D")), Kind = line.Kind, PointI = pi.Name, PointJ = pj.Name, StoriesBelowI = 1, Extra = [.. line.Extra] };
                m.Lines.Add(part);
            }
            if (m.LineAssigns.Exists(a => a.Label == part.Label && a.Story == story)) throw new ArgumentException($"{part.Label}@{story} already stands where a part of {o} would go.");
            var a = Clone(assign); a.Label = part.Label; a.Story = story;
            // The end releases and offsets stay at the frame's own ends.
            PropertyDefs.SetReleases(a, k == 0 ? ri : [], k == n - 1 ? rj : []);
            if (k > 0) PropertyDefs.Remove(a.Extra, "LENGTHOFFI");
            if (k < n - 1) PropertyDefs.Remove(a.Extra, "LENGTHOFFJ");
            m.LineAssigns.Add(a);
            foreach (var l in loads) { var c = Clone(l); c.Object = part.Label; c.Story = story; m.LineLoads.Add(c); }
            made.Add(new Obj(true, part.Label, story));
        }
        Remove(m, o);
        return made;
    }

    /// <summary>
    /// Edit > Edit Shells > Divide Shells, Walls and Ramps at Story Levels: a wall or ramp that spans more than one
    /// story becomes one per story, the part of it between each level and the next (its corners on the levels),
    /// keeping the section, assignments and loads. A part takes the object already standing on its story with the
    /// same corners, as ETABS keeps one object over many stories.
    /// </summary>
    public static List<Obj> DivideAreaAtLevels(StructuralModel m, Obj o)
    {
        if (o.Line) throw new ArgumentException($"{o} is a frame; Divide Shells divides walls and ramps at the levels.");
        var area = m.Area(o.Label)!;
        var top = m.StoryIndex(o.Story);
        var levels = area.StoriesBelow.Select(b => top - b).ToList();
        int lo = levels.Min(), hi = levels.Max();
        if (hi - lo < 2) throw new ArgumentException($"{o} spans one story: no story level lies between its corners.");
        var corners = area.PointNames.Select((n, k) => { var p = m.Point(n)!; return (p.X, p.Y, Z: m.Elevation(levels[k])); }).ToList();
        var assign = m.AreaAssigns.First(a => a.Label == o.Label && a.Story == o.Story);
        var loads = m.AreaLoads.Where(l => l.Object == o.Label && l.Story == o.Story).ToList();
        // The outline's part on one side of a level (Sutherland-Hodgman), a new corner where an edge crosses it.
        static List<(double X, double Y, double Z)> Side(List<(double X, double Y, double Z)> poly, double z, bool above)
        {
            var kept = new List<(double X, double Y, double Z)>();
            for (var i = 0; i < poly.Count; i++)
            {
                var (a, b) = (poly[i], poly[(i + 1) % poly.Count]);
                bool ina = above ? a.Z >= z - 1e-6 : a.Z <= z + 1e-6, inb = above ? b.Z >= z - 1e-6 : b.Z <= z + 1e-6;
                if (ina) kept.Add(a);
                if (ina != inb) { var t = (z - a.Z) / (b.Z - a.Z); kept.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, z)); }
            }
            return kept.Where((c, i) => { var n = kept[(i + 1) % kept.Count]; return Math.Abs(c.X - n.X) + Math.Abs(c.Y - n.Y) + Math.Abs(c.Z - n.Z) > 1e-6; }).ToList();
        }
        var made = new List<Obj>();
        for (var k = lo; k < hi; k++)
        {
            var (z0, z1) = (m.Elevation(k), m.Elevation(k + 1));
            var part = Side(Side(corners, z0, true), z1, false);
            if (part.Count < 3) continue;
            // Each part starts at the first corner of its bottom edge, as a wall's outline does (i j j i, 1 1 0 0).
            bool Low(int i) => Math.Abs(part[(i + part.Count) % part.Count].Z - z1) > 1e-6;
            var first = Enumerable.Range(0, part.Count).FirstOrDefault(i => Low(i) && !Low(i - 1));
            part = [.. part.Skip(first), .. part.Take(first)];
            var names = part.Select(c => m.PointAt(c.X, c.Y).Name).ToList();
            var below = part.Select(c => Math.Abs(c.Z - z1) <= 1e-6 ? 0 : 1).ToList();
            var story = m.Stories[k + 1].Name;
            var obj = m.Areas.Find(a => a.Kind == area.Kind && a.PointNames.SequenceEqual(names) && a.StoriesBelow.SequenceEqual(below));
            if (obj is null)
            {
                obj = new AreaObject { Label = ModelEdit.NextName(m.Areas.Select(a => a.Label), Prefix(area.Label, area.Kind == AreaKind.Wall ? "W" : "F")), Kind = area.Kind, PointNames = names, StoriesBelow = below, Extra = [.. area.Extra] };
                m.Areas.Add(obj);
            }
            if (m.AreaAssigns.Exists(a => a.Label == obj.Label && a.Story == story)) throw new ArgumentException($"{obj.Label}@{story} already stands where a part of {o} would go.");
            var aa = Clone(assign); aa.Label = obj.Label; aa.Story = story;
            m.AreaAssigns.Add(aa);
            foreach (var l in loads) { var c = Clone(l); c.Object = obj.Label; c.Story = story; m.AreaLoads.Add(c); }
            made.Add(new Obj(false, obj.Label, story));
        }
        if (made.Count < 2) throw new ArgumentException($"{o} has no part on more than one story: it stays whole.");
        Remove(m, o);
        return made;
    }

    /// <summary>A copy of an object with each plan point put where <paramref name="f"/> says, onto <paramref name="story"/>.</summary>
    public static Obj CopyTo(StructuralModel m, Obj o, Func<double, double, (double X, double Y)> f, string story)
    {
        if (m.StoryIndex(story) < 0) throw new ArgumentException($"There is no story '{story}'.");
        return CopyMapped(m, o, story, false, p =>
        {
            if (p.Below != 0) throw new ArgumentException($"{o} has a point set below its story ({p.Name}); copy it in the program.");
            var (x, y) = f(p.X, p.Y);
            return m.PointAt(x, y);
        });
    }

    /// <summary>Edit > Replicate, Radial: <paramref name="number"/> copies turned about (cx, cy), each <paramref name="angle"/> degrees on from the last.</summary>
    public static List<Obj> ReplicateRadial(StructuralModel m, IReadOnlyList<Obj> objects, double cx, double cy, double angle, int number)
    {
        if (number is < 1 or > 100) throw new ArgumentException("The number of copies is from 1 to 100.");
        if (Math.Abs(angle % 360) < 1e-9) throw new ArgumentException("Give an angle that is not a whole turn: copies in the same place would lie on the objects.");
        var made = new List<Obj>();
        for (var n = 1; n <= number; n++)
        {
            var t = angle * n * Math.PI / 180; var (c, s) = (Math.Cos(t), Math.Sin(t));
            foreach (var o in objects) made.Add(CopyTo(m, o, (x, y) => (cx + (x - cx) * c - (y - cy) * s, cy + (x - cx) * s + (y - cy) * c), o.Story));
        }
        return made;
    }

    /// <summary>Edit > Replicate, Mirror: a copy of each object mirrored about the plan line through (x1, y1) and (x2, y2).</summary>
    public static List<Obj> ReplicateMirror(StructuralModel m, IReadOnlyList<Obj> objects, double x1, double y1, double x2, double y2)
    {
        var (dx, dy) = (x2 - x1, y2 - y1); var l2 = dx * dx + dy * dy;
        if (l2 < ModelEdit.Tolerance * ModelEdit.Tolerance) throw new ArgumentException("The mirror line needs two points apart.");
        (double, double) F(double x, double y) { var t = ((x - x1) * dx + (y - y1) * dy) / l2; var (px, py) = (x1 + t * dx, y1 + t * dy); return (2 * px - x, 2 * py - y); }
        var made = objects.Select(o => CopyTo(m, o, F, o.Story)).ToList();
        // A slab or wall mirrored runs its corners the other way round; turn them back so its local 3 axis keeps its side.
        foreach (var o in made.Where(o => !o.Line)) { var a = m.Area(o.Label)!; if (m.AreaAssigns.Count(x => x.Label == o.Label) == 1) { a.PointNames.Reverse(); a.StoriesBelow.Reverse(); } }
        return made;
    }

    /// <summary>
    /// Edit > Move Joints/Frames/Shells: each object (dx, dy) in plan and dz up, keeping what it has. The model's objects
    /// stand on stories, so dz must take each object to another story's level.
    /// </summary>
    public static List<Obj> Move(StructuralModel m, IReadOnlyList<Obj> objects, double dx, double dy, double dz)
    {
        if (Math.Abs(dz) < ModelEdit.Tolerance) return Move(m, objects, dx, dy);
        string To(Obj o)
        {
            var z = m.Elevation(m.StoryIndex(o.Story)) + dz;
            var i = Enumerable.Range(0, m.Stories.Count).FirstOrDefault(k => Math.Abs(m.Elevation(k) - z) <= ModelEdit.Tolerance * 10, -1);
            if (i < 0) throw new ArgumentException($"{o} moved up by {dz} would stand at Z = {z}, where the model has no story: add a story there first.");
            return m.Stories[i].Name;
        }
        var targets = objects.Select(To).ToList();
        var made = objects.Select((o, k) => Copy(m, o, dx, dy, targets[k])).ToList();
        foreach (var o in objects) Remove(m, o);
        return made;
    }

    /// <summary>
    /// Edit > Merge Joints: plan points of <paramref name="names"/> within <paramref name="tolerance"/> of each other become
    /// one (the first named); frames, slabs, walls, joint assignments and joint loads follow it. A frame left with both
    /// ends at one point is taken off, as the program does.
    /// </summary>
    public static (int Merged, List<Obj> Removed) MergePoints(StructuralModel m, IReadOnlyList<string> names, double tolerance)
    {
        if (!(tolerance > 0)) throw new ArgumentException("The merge tolerance is more than zero.");
        var pts = names.Distinct().Select(n => m.Point(n) ?? throw new ArgumentException($"There is no joint {n}.")).Where(p => p.Below == 0).ToList();
        var map = new Dictionary<string, string>();
        for (var i = 0; i < pts.Count; i++)
        {
            if (map.ContainsKey(pts[i].Name)) continue;
            for (var j = i + 1; j < pts.Count; j++)
                if (!map.ContainsKey(pts[j].Name) && Hyp(pts[i].X - pts[j].X, pts[i].Y - pts[j].Y) <= tolerance) map[pts[j].Name] = pts[i].Name;
        }
        if (map.Count == 0) return (0, []);
        string To(string n) => map.TryGetValue(n, out var t) ? t : n;
        foreach (var l in m.Lines) { l.PointI = To(l.PointI); l.PointJ = To(l.PointJ); }
        foreach (var a in m.Areas) a.PointNames = a.PointNames.Select(To).ToList();
        foreach (var g in m.PointAssigns.Where(x => map.ContainsKey(x.Point)).ToList())
        {
            if (m.PointAssigns.Exists(x => x.Point == map[g.Point] && x.Story == g.Story)) m.PointAssigns.Remove(g); else g.Point = map[g.Point];
        }
        foreach (var l in m.PointLoads) if (map.TryGetValue(l.Object, out var t)) l.Object = t;
        var removed = new List<Obj>();
        foreach (var l in m.Lines.Where(l => l.PointI == l.PointJ && l.StoriesBelowI == 0).ToList())
            foreach (var a in m.LineAssigns.Where(a => a.Label == l.Label).ToList()) { var o = new Obj(true, l.Label, a.Story); Remove(m, o); removed.Add(o); }
        foreach (var a in m.Areas.Where(a => a.PointNames.Zip(a.StoriesBelow).Distinct().Count() < 3).ToList())
            foreach (var g in m.AreaAssigns.Where(g => g.Label == a.Label).ToList()) { var o = new Obj(false, a.Label, g.Story); Remove(m, o); removed.Add(o); }
        m.Points.RemoveAll(p => map.ContainsKey(p.Name));
        return (map.Count, removed);
    }

    /// <summary>
    /// Edit > Replicate > Modify/Show Replicate Options for Assigns: what a copy leaves behind, by "Frames:item" or
    /// "Shells:item" as the form names them. The assignment words each item takes off are those ETABS writes in the
    /// .e2k (a guess for some until the PC round trip reads them back).
    /// </summary>
    public static readonly Dictionary<string, string[]> CopyItemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Frames:Property Modifiers"] = ["PROPMOD"], ["Frames:Releases and Partial Fixity"] = ["RELEASE", "PARTIALFIX"], ["Frames:End Length Offsets"] = ["LENGTHOFF", "RIGIDZONE"],
        ["Frames:Insertion Point"] = ["CARDINALPT", "MIRROR2", "MIRROR3", "NODESTIFF", "OFFSETX", "OFFSETY", "OFFSETZ"], ["Frames:Local Axes"] = ["ANG"], ["Frames:OutputStations"] = ["MAXSTASPC", "MINNUMSTA"],
        ["Frames:Tension and Compression Limits"] = ["TCLIMIT", "TENSIONLIMIT", "COMPLIMIT"], ["Frames:Line Springs"] = ["LINESPRING"], ["Frames:Additional Mass"] = ["MASS", "LINEMASS"],
        ["Frames:Pier Labels"] = ["PIER"], ["Frames:Spandrel Labels"] = ["SPANDREL"], ["Frames:Automatic Frame Subdivide"] = ["AUTOMESH", "AUTOMESHATBEAMS", "AUTOMESHATSTORIES", "AUTOMESHATINTERSECTIONS", "MESH"],
        ["Frames:Floor Meshing Options"] = ["MESHATINTERSECTIONS", "FLOORMESH"], ["Frames:Moment Frame Beam Types"] = ["MOMENTFRAMEBEAMTYPE", "RBS"], ["Frames:Column Splice Overwrites"] = ["SPLICE"], ["Frames:Material Overwrites"] = ["MATOVERWRITE"],
        ["Shells:Stiffness Modifiers"] = ["PROPMOD", "F11MOD", "F22MOD", "F12MOD", "M11MOD", "M22MOD", "M12MOD", "V13MOD", "V23MOD", "MMOD", "WMOD"], ["Shells:Local Axes"] = ["ANG"],
        ["Shells:Area Springs"] = ["SPRINGPROP"], ["Shells:Additional Mass"] = ["MASS", "MASSPERAREA"], ["Shells:Pier Labels"] = ["PIER"], ["Shells:Spandrel Labels"] = ["SPANDREL"],
        ["Shells:Floor Auto Mesh Options"] = ["MESHTYPE", "MAXMESHSIZE", "FLOORMESH", "AUTOMESH"], ["Shells:Wall Auto Mesh Options"] = ["WALLMESH", "AUTOMESH"], ["Shells:Wall Openings"] = ["OPENING"],
        ["Shells:Auto Edge Constraints"] = ["EDGECONSTRAINT"], ["Shells:Material Overwrites"] = ["MATOVERWRITE"], ["Shells:Edge Releases"] = ["EDGERELEASE"],
    };

    private static readonly Dictionary<string, string[]> CopyLoadTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Frames:Point"] = ["POINTF", "POINT"], ["Frames:Distributed"] = ["UNIFF", "TRAPF", "UNIFMOMENT", "TRAPMOMENT"], ["Frames:Temperature"] = ["TEMPERATURE"], ["Frames:Open Structure Wind"] = ["OPENSTRUCTUREWIND", "WIND"],
        ["Shells:Uniform Load Sets"] = ["UNIFLOADSET", "UNIFORMLOADSET"], ["Shells:Uniform"] = ["UNIFF"], ["Shells:Non-Uniform"] = ["NONUNIFF", "NONUNIF"], ["Shells:Temperature"] = ["TEMPERATURE"], ["Shells:Wind Pressure Coefficients"] = ["WINDPRESSURE", "WINDCOEFF"],
    };

    /// <summary>Takes off a copy what the Replicate Assigns options leave behind (<paramref name="skip"/>: "Frames:Hinges", "Shells:Diaphragms", ...).</summary>
    public static void StripCopy(StructuralModel m, Obj o, IReadOnlyCollection<string> skip)
    {
        if (skip.Count == 0) return;
        var kind = o.Line ? "Frames:" : "Shells:";
        Assign assign = o.Line ? m.LineAssigns.First(a => a.Label == o.Label && a.Story == o.Story) : m.AreaAssigns.First(a => a.Label == o.Label && a.Story == o.Story);
        var keys = skip.Where(x => x.StartsWith(kind, StringComparison.OrdinalIgnoreCase)).SelectMany(x => CopyItemKeys.GetValueOrDefault(x) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count > 0)
        {
            foreach (var l in assign.Extra)
                for (var i = 0; i + 1 < l.Count;)
                    if (keys.Any(k => l[i].StartsWith(k, StringComparison.OrdinalIgnoreCase))) l.RemoveRange(i, 2); else i += 2;
            assign.Extra.RemoveAll(l => l.Count == 0);
        }
        if (o.Line && skip.Contains("Frames:Hinges", StringComparer.OrdinalIgnoreCase)) PropertyDefs.SetHinges(m, o.Label, o.Story, []);
        if (!o.Line && skip.Contains("Shells:Diaphragms", StringComparer.OrdinalIgnoreCase)) ((AreaAssign)assign).Diaphragm = null;
        var types = skip.Where(x => x.StartsWith(kind, StringComparison.OrdinalIgnoreCase)).SelectMany(x => CopyLoadTypes.GetValueOrDefault(x) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Off(ObjectLoad l) => l.Object == o.Label && l.Story == o.Story && types.Contains(E2k.E2kText.Unquote(E2k.E2kText.Value(l.Tokens, "TYPE") ?? ""));
        if (types.Count > 0) { if (o.Line) m.LineLoads.RemoveAll(Off); else m.AreaLoads.RemoveAll(Off); }
    }

    /// <summary>
    /// Edit > Edit Frames > Reverse Frame Connectivity: the frame runs from its J end to its I end; its end releases,
    /// end offsets and hinges change ends with it. A frame object other stories share gets a new label on this story.
    /// A load at a place along it (not uniform) is refused, as Divide does.
    /// </summary>
    public static Obj Reverse(StructuralModel m, Obj o)
    {
        if (!o.Line) throw new ArgumentException($"{o} is a slab or wall; Reverse Frame Connectivity reverses frames.");
        var line = m.Line(o.Label)!;
        if (line.StoriesBelowI != 0 || line.Kind == LineKind.Column) throw new ArgumentException($"{o} is a {(line.Kind == LineKind.Column ? "column" : "frame from the story below")}; the Modeler keeps it running upward.");
        if (m.LineLoads.Any(l => l.Object == o.Label && l.Story == o.Story && !(E2k.E2kText.Unquote(E2k.E2kText.Value(l.Tokens, "TYPE") ?? "") is var t && (t.Equals("UNIFF", StringComparison.OrdinalIgnoreCase) || t.Equals("TEMPERATURE", StringComparison.OrdinalIgnoreCase)))))
            throw new ArgumentException($"{o} has a load at a place along it: take it off first, or reverse it in the program.");
        var assign = m.LineAssigns.First(a => a.Label == o.Label && a.Story == o.Story);
        var hinges = PropertyDefs.HingesOf(m, o.Label, o.Story).Select(h => h with { At = 1 - h.At }).ToList();
        var shared = m.LineAssigns.Count(a => a.Label == o.Label) > 1;
        var target = line;
        if (shared)
        {
            target = new LineObject { Label = ModelEdit.NextName(m.Lines.Select(l => l.Label), Prefix(line.Label, "B")), Kind = line.Kind, PointI = line.PointJ, PointJ = line.PointI };
            m.Lines.Add(target);
            PropertyDefs.SetHinges(m, o.Label, o.Story, []);
            assign.Label = target.Label;
            foreach (var l in m.LineLoads.Where(l => l.Object == o.Label && l.Story == o.Story)) l.Object = target.Label;
        }
        else (line.PointI, line.PointJ) = (line.PointJ, line.PointI);
        var (ri, rj) = PropertyDefs.ReleasesOf(assign);
        PropertyDefs.SetReleases(assign, rj, ri);
        foreach (var l in assign.Extra)
            for (var k = 0; k + 1 < l.Count; k += 2)
                l[k] = l[k] switch { "LENGTHOFFI" => "LENGTHOFFJ", "LENGTHOFFJ" => "LENGTHOFFI", _ => l[k] };
        PropertyDefs.SetHinges(m, target.Label, o.Story, hinges);
        return new Obj(true, target.Label, o.Story);
    }

    /// <summary>
    /// Edit > Edit Frames > Join Frames: beams on one story that run on in a straight line, end to end, become one beam
    /// from the first's far end to the last's, keeping the first's section and assignments and their uniform loads.
    /// </summary>
    public static List<Obj> Join(StructuralModel m, IReadOnlyList<Obj> objects)
    {
        var beams = objects.Where(o => o.Line && m.Line(o.Label)!.Kind == LineKind.Beam).ToList();
        var made = new List<Obj>();
        foreach (var story in beams.Select(b => b.Story).Distinct().ToList())
        {
            var left = beams.Where(b => b.Story == story).ToList();
            var changed = true;
            while (changed && left.Count > 1)
            {
                changed = false;
                for (var x = 0; x < left.Count && !changed; x++)
                    for (var y = 0; y < left.Count && !changed; y++)
                    {
                        if (x == y) continue;
                        var (a, b) = (m.Line(left[x].Label)!, m.Line(left[y].Label)!);
                        var shared = new[] { a.PointI, a.PointJ }.Intersect(new[] { b.PointI, b.PointJ }).ToList();
                        if (shared.Count != 1 || a.StoriesBelowI != 0 || b.StoriesBelowI != 0) continue;
                        var far1 = a.PointI == shared[0] ? a.PointJ : a.PointI; var far2 = b.PointI == shared[0] ? b.PointJ : b.PointI;
                        var (p1, p0, p2) = (m.Point(far1)!, m.Point(shared[0])!, m.Point(far2)!);
                        var cross = (p0.X - p1.X) * (p2.Y - p1.Y) - (p0.Y - p1.Y) * (p2.X - p1.X);
                        var len = Hyp(p2.X - p1.X, p2.Y - p1.Y);
                        if (len <= 0 || Math.Abs(cross) / len > ModelEdit.Tolerance * 10) continue;
                        if (((p0.X - p1.X) * (p2.X - p0.X) + (p0.Y - p1.Y) * (p2.Y - p0.Y)) <= 0) continue;
                        // Another frame or a slab ending at the shared joint on this story keeps it: ETABS would not lose that connection.
                        bool Uses(string n) => m.LineAssigns.Any(g => g.Story == story && g.Label != a.Label && g.Label != b.Label && m.Line(g.Label) is { } l && (l.PointI == n || l.PointJ == n));
                        if (Uses(shared[0])) continue;
                        foreach (var l in m.LineLoads.Where(l => (l.Object == a.Label || l.Object == b.Label) && l.Story == story))
                            if (!(E2k.E2kText.Unquote(E2k.E2kText.Value(l.Tokens, "TYPE") ?? "") is var t && (t.Equals("UNIFF", StringComparison.OrdinalIgnoreCase) || t.Equals("TEMPERATURE", StringComparison.OrdinalIgnoreCase))))
                                throw new ArgumentException($"{(l.Object == a.Label ? left[x] : left[y])} has a load at a place along it: take it off first, or join them in the program.");
                        string Loads(string label) => string.Join("|", m.LineLoads.Where(l => l.Object == label && l.Story == story).Select(l => string.Join(" ", l.Tokens)).OrderBy(z => z, StringComparer.Ordinal));
                        if (Loads(a.Label) != Loads(b.Label)) throw new ArgumentException($"{left[x]} and {left[y]} carry different loads: one beam cannot keep both. Make their loads the same first.");
                        if (PropertyDefs.HingesOf(m, a.Label, story).Count + PropertyDefs.HingesOf(m, b.Label, story).Count > 0) throw new ArgumentException($"{left[x]} or {left[y]} has hinges at places along it: take them off first.");
                        var ga0 = m.LineAssigns.First(g => g.Label == a.Label && g.Story == story); var gb = m.LineAssigns.First(g => g.Label == b.Label && g.Story == story);
                        var (ri, rj) = PropertyDefs.ReleasesOf(ga0); var (bi, bj) = PropertyDefs.ReleasesOf(gb);
                        var farB = b.PointI == far2 ? bi : bj;
                        var nl = new LineObject { Label = ModelEdit.NextName(m.Lines.Select(l => l.Label), Prefix(a.Label, "B")), Kind = LineKind.Beam, PointI = a.PointJ == shared[0] ? a.PointI : far2, PointJ = a.PointJ == shared[0] ? far2 : a.PointJ };
                        m.Lines.Add(nl);
                        var ga = Clone(ga0); ga.Label = nl.Label; m.LineAssigns.Add(ga);
                        if (nl.PointJ == far2) { PropertyDefs.SetReleases(ga, ri, farB); PropertyDefs.Remove(ga.Extra, "LENGTHOFFJ"); } else { PropertyDefs.SetReleases(ga, farB, rj); PropertyDefs.Remove(ga.Extra, "LENGTHOFFI"); }
                        foreach (var l in m.LineLoads.Where(l => l.Object == a.Label && l.Story == story).ToList()) { var c = Clone(l); c.Object = nl.Label; m.LineLoads.Add(c); }
                        var copy = new Obj(true, nl.Label, story);
                        var (lx, ly) = (left[x], left[y]);
                        Remove(m, lx); Remove(m, ly);
                        left.Remove(lx); left.Remove(ly); left.Add(copy);
                        changed = true;
                    }
            }
            made.AddRange(left);
        }
        return made;
    }

    /// <summary>Edit > Paste (and Replicate, Linear, with dz): copies each (dx, dy) on in plan and dz up, onto the story at that level.</summary>
    public static List<Obj> ReplicateLinear(StructuralModel m, IReadOnlyList<Obj> objects, double dx, double dy, double dz, int number)
    {
        if (number is < 1 or > 100) throw new ArgumentException("The number of copies is from 1 to 100.");
        var made = new List<Obj>();
        for (var n = 1; n <= number; n++)
            foreach (var o in objects)
            {
                var z = m.Elevation(m.StoryIndex(o.Story)) + dz * n;
                var i = Enumerable.Range(0, m.Stories.Count).FirstOrDefault(k => Math.Abs(m.Elevation(k) - z) <= ModelEdit.Tolerance * 10, -1);
                if (i < 0) throw new ArgumentException($"{o} copied up by {dz * n} would stand at Z = {z}, where the model has no story: add a story there first.");
                made.Add(Copy(m, o, dx * n, dy * n, m.Stories[i].Name));
            }
        return made;
    }

    /// <summary>The objects on <paramref name="story"/> with an end or corner at plan point (x, y).</summary>
    public static List<Obj> At(StructuralModel m, string story, double x, double y)
    {
        bool Here(string n) => m.Point(n) is { } p && p.Below == 0 && Math.Abs(p.X - x) <= ModelEdit.Tolerance * 10 && Math.Abs(p.Y - y) <= ModelEdit.Tolerance * 10;
        var list = m.LineAssigns.Where(a => a.Story == story && m.Line(a.Label) is { } l && (Here(l.PointI) || Here(l.PointJ))).Select(a => new Obj(true, a.Label, story)).ToList();
        list.AddRange(m.AreaAssigns.Where(a => a.Story == story && m.Area(a.Label) is { } r && r.PointNames.Any(Here)).Select(a => new Obj(false, a.Label, story)));
        return list;
    }

    /// <summary>
    /// Edit > Align Joints/Frames/Edges, to an X or Y ordinate: each joint (x, y on a story) goes to the ordinate, and the
    /// frames, slabs and walls on that story ending there follow it (Draw > Reshape Object for each).
    /// </summary>
    public static int AlignJoints(StructuralModel m, IReadOnlyList<(double X, double Y, string Story)> joints, double? toX, double? toY)
    {
        var n = 0;
        foreach (var (x, y, story) in joints)
        {
            var (nx, ny) = (toX ?? x, toY ?? y);
            if (Math.Abs(nx - x) <= ModelEdit.Tolerance && Math.Abs(ny - y) <= ModelEdit.Tolerance) continue;
            var objs = At(m, story, x, y);
            if (objs.Count == 0) { var p = m.PointAt(x, y); var g = m.PointAssigns.Find(a => a.Point == p.Name && a.Story == story); if (g is null) continue; var q = m.PointAt(nx, ny); g.Point = q.Name; n++; continue; }
            foreach (var o in objs) Reshape(m, o, x, y, nx, ny);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Edit > Align Joints/Frames/Edges, Trim or Extend: the end of each beam nearest the boundary beam goes to where the
    /// beam's line crosses the boundary's line (trim only shortens, extend only lengthens).
    /// </summary>
    public static int TrimExtend(StructuralModel m, IReadOnlyList<Obj> beams, Obj boundary, bool extend)
    {
        var b = m.Line(boundary.Label)!; var (b1, b2) = (m.Point(b.PointI)!, m.Point(b.PointJ)!);
        var n = 0;
        foreach (var o in beams.Where(o => o.Line && o != boundary))
        {
            var l = m.Line(o.Label)!; if (l.Kind == LineKind.Column) continue;
            var (p, q) = (m.Point(l.PointI)!, m.Point(l.PointJ)!);
            var (rx, ry, sx, sy) = (q.X - p.X, q.Y - p.Y, b2.X - b1.X, b2.Y - b1.Y); var den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-12) continue;
            var t = ((b1.X - p.X) * sy - (b1.Y - p.Y) * sx) / den;
            var (ix, iy) = (p.X + rx * t, p.Y + ry * t);
            var nearI = Math.Abs(t) < Math.Abs(t - 1);
            if (extend ? (t > 0 && t < 1) : !(t > 1e-6 && t < 1 - 1e-6)) continue;
            var end = nearI ? p : q;
            if (Math.Abs(end.X - ix) <= ModelEdit.Tolerance && Math.Abs(end.Y - iy) <= ModelEdit.Tolerance) continue;
            Reshape(m, o, end.X, end.Y, ix, iy); n++;
        }
        return n;
    }

    /// <summary>
    /// Edit > Edit Shells (Divide, Merge, Expand/Shrink, Split Edges, Remove Joints, Chamfer, Slab Edge Type): floors on
    /// one story become the new outlines, each keeping the first floor's section, assignments and loads. Walls keep their
    /// own shape (they stand between stories).
    /// </summary>
    public static List<Obj> ReplaceFloors(StructuralModel m, IReadOnlyList<Obj> objects, IReadOnlyList<IReadOnlyList<(double X, double Y)>> outlines)
    {
        if (objects.Count == 0) throw new ArgumentException("Select the floors first.");
        if (outlines.Count == 0) throw new ArgumentException("Nothing to make: give the new outlines.");
        var story = objects[0].Story;
        foreach (var o in objects)
        {
            if (o.Line) throw new ArgumentException($"{o} is a frame; this works on floors.");
            var a = m.Area(o.Label)!;
            if (a.Kind == AreaKind.Wall) throw new ArgumentException($"{o} is a wall; this works on floors.");
            if (a.StoriesBelow.Any(b => b != 0)) throw new ArgumentException($"{o} has corners on another story; reshape it in the program.");
            if (o.Story != story) throw new ArgumentException("The floors are on different stories: work on one story at a time.");
        }
        foreach (var ol in outlines)
        {
            if (ol.Count < 3) throw new ArgumentException("Each new floor has three corners or more.");
            double area = 0; for (var i = 0; i < ol.Count; i++) { var (p, q) = (ol[i], ol[(i + 1) % ol.Count]); area += p.X * q.Y - q.X * p.Y; }
            if (Math.Abs(area) / 2 <= ModelEdit.Tolerance * ModelEdit.Tolerance) throw new ArgumentException("A new floor would have no area.");
        }
        var first = m.Area(objects[0].Label)!;
        var assign = m.AreaAssigns.First(a => a.Label == objects[0].Label && a.Story == story);
        var loads = m.AreaLoads.Where(l => l.Object == objects[0].Label && l.Story == story).ToList();
        var made = new List<Obj>();
        foreach (var ol in outlines)
        {
            var names = ol.Select(p => m.PointAt(p.X, p.Y).Name).ToList();
            var na = new AreaObject { Label = ModelEdit.NextName(m.Areas.Select(a => a.Label), Prefix(first.Label, "F")), Kind = first.Kind, PointNames = names, StoriesBelow = names.Select(_ => 0).ToList(), Extra = [.. first.Extra] };
            m.Areas.Add(na);
            var g = Clone(assign); g.Label = na.Label; m.AreaAssigns.Add(g);
            foreach (var l in loads) { var c = Clone(l); c.Object = na.Label; m.AreaLoads.Add(c); }
            made.Add(new Obj(false, na.Label, story));
        }
        foreach (var o in objects) Remove(m, o);
        return made;
    }

    /// <summary>
    /// Edit > Auto Relabel All: frames and areas get the program's labels again (B, C, D, F, W and the rest by kind, numbered
    /// from the lowest plan point up, then left to right). An object something else names by its label (a group, a design
    /// overwrite, a table row the Modeler keeps as read) keeps its label, so nothing loses its link; the count is returned.
    /// </summary>
    public static (int Relabelled, List<string> Kept) Relabel(StructuralModel m)
    {
        string Other() => JsonSerializer.Serialize(new object?[] { m.Passthrough.Where(r => r.Heading != PropertyDefs.HingeHeading).ToList(), m.DefinitionTables, m.Carried, m.Kept, m.Source, m.Extras,
            m.LineAssigns.Select(a => a.Tables), m.AreaAssigns.Select(a => a.Tables), m.PointAssigns, m.PointLoads }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var other = Other();
        bool Named(string label) => other.Contains("\\\"" + label + "\\\"", StringComparison.Ordinal) || other.Contains("\"" + label + "\"", StringComparison.Ordinal);
        var kept = new List<string>();
        (double, double) Key(string p) { var q = m.Point(p); return q is null ? (0, 0) : (q.Y, q.X); }
        var lineMap = new Dictionary<string, string>(); var areaMap = new Dictionary<string, string>();
        foreach (var g in m.Lines.GroupBy(l => l.Kind))
        {
            var prefix = g.Key == LineKind.Beam ? "B" : g.Key == LineKind.Column ? "C" : "D";
            var n = 0;
            kept.AddRange(g.Where(l => Named(l.Label)).Select(l => l.Label));
            foreach (var l in g.OrderBy(l => Key(l.PointI)).ThenBy(l => Key(l.PointJ)))
            {
                if (kept.Contains(l.Label)) continue;
                string next; do next = prefix + ++n; while (m.Lines.Any(x => x.Label == next && !g.Contains(x)) || kept.Contains(next));
                lineMap[l.Label] = next;
            }
        }
        foreach (var g in m.Areas.GroupBy(a => a.Kind))
        {
            var prefix = g.Key == AreaKind.Wall ? "W" : "F";
            var n = 0;
            kept.AddRange(g.Where(a => Named(a.Label)).Select(a => a.Label));
            foreach (var a in g.OrderBy(a => a.PointNames.Select(Key).Min()))
            {
                if (kept.Contains(a.Label)) continue;
                string next; do next = prefix + ++n; while (m.Areas.Any(x => x.Label == next && !g.Contains(x)) || kept.Contains(next));
                areaMap[a.Label] = next;
            }
        }
        // Hinges go by label: read them all first, put them back under the new labels.
        var hinges = m.LineAssigns.Select(a => (a.Label, a.Story, H: PropertyDefs.HingesOf(m, a.Label, a.Story))).Where(x => x.H.Count > 0).ToList();
        foreach (var (l, st, _) in hinges) PropertyDefs.SetHinges(m, l, st, []);
        string L(string x) => lineMap.TryGetValue(x, out var v) ? v : x; string A(string x) => areaMap.TryGetValue(x, out var v) ? v : x;
        foreach (var l in m.Lines) l.Label = L(l.Label);
        foreach (var a in m.LineAssigns) a.Label = L(a.Label);
        foreach (var l in m.LineLoads) l.Object = L(l.Object);
        foreach (var a in m.Areas) a.Label = A(a.Label);
        foreach (var a in m.AreaAssigns) a.Label = A(a.Label);
        foreach (var l in m.AreaLoads) l.Object = A(l.Object);
        foreach (var (l, st, h) in hinges) PropertyDefs.SetHinges(m, L(l), st, h);
        return (lineMap.Count(x => x.Key != x.Value) + areaMap.Count(x => x.Key != x.Value), kept);
    }
}
