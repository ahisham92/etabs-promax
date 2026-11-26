using System.Text.Json;

namespace CsiProMax.Model;

/// <summary>
/// The Modeler's Edit menu on the model itself: Replicate (linear, or to other stories), Move and Divide Frames, as
/// ETABS's Edit menu does them. A copy keeps everything the original has on its story: section, assignments (releases,
/// modifiers, springs, groups, pier labels, ...), loads and hinges. Pure, so the tests run it.
/// </summary>
public static class ModelOps
{
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
        PlanPoint Moved(string name)
        {
            var p = m.Point(name) ?? throw new ArgumentException($"{o} has a point {name} the model does not have.");
            if (same) return p;
            if (p.Below != 0) throw new ArgumentException($"{o} has a point set below its story ({name}); move it in the program.");
            return m.PointAt(p.X + dx, p.Y + dy);
        }
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
        var points = Enumerable.Range(0, parts + 1).Select(k => k == 0 ? i : k == parts ? j : m.PointAt(i.X + (j.X - i.X) * k / parts, i.Y + (j.Y - i.Y) * k / parts)).ToList();
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
}
