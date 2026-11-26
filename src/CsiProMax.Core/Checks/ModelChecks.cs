using CsiProMax.Model;

namespace CsiProMax.Checks;

public enum Severity { Error, Warning }

/// <summary>One thing wrong with the model: what, where (the object's label and story when it has them) and how bad.
/// An error is something ETABS would refuse or that makes the analysis wrong; a warning deserves a look.</summary>
public sealed record Finding(Severity Severity, string Check, string Message, string? Object = null, string? Story = null);

/// <summary>Checks that need no analysis: the model is complete and joined up. Plain code, so the same model always
/// gives the same findings.</summary>
public static class ModelChecks
{
    public static List<Finding> Run(StructuralModel m)
    {
        var findings = new List<Finding>();
        Stories(m, findings);
        Duplicates(m, findings);
        References(m, findings);
        Geometry(m, findings);
        Supports(m, findings);
        Loads(m, findings);
        return findings;
    }

    private static void Stories(StructuralModel m, List<Finding> f)
    {
        if (m.Stories.Count < 2) f.Add(new(Severity.Error, "stories", "The model has no stories above the base level."));
        foreach (var s in m.Stories.Skip(1).Where(s => s.Height <= 0))
            f.Add(new(Severity.Error, "stories", $"Story '{s.Name}' has no height.", Story: s.Name));
        var framed = m.LineAssigns.Select(a => a.Story).Concat(m.AreaAssigns.Select(a => a.Story)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in m.Stories.Skip(1).Where(s => !framed.Contains(s.Name)))
            f.Add(new(Severity.Warning, "stories", $"Story '{s.Name}' has no members.", Story: s.Name));
    }

    private static void Duplicates(StructuralModel m, List<Finding> f)
    {
        void Names(IEnumerable<string> names, string what)
        {
            foreach (var g in names.GroupBy(n => n).Where(g => g.Count() > 1))
                f.Add(new(Severity.Error, "duplicates", $"{g.Count()} {what} are named '{g.Key}'.", g.Key));
        }
        Names(m.Stories.Select(s => s.Name), "stories");
        Names(m.Points.Select(p => p.Name), "points");
        Names(m.Lines.Select(l => l.Label), "line objects");
        Names(m.Areas.Select(a => a.Label), "area objects");
        Names(m.Materials.Select(x => x.Name), "materials");
        Names(m.FrameSections.Select(x => x.Name), "frame sections");
        Names(m.ShellSections.Select(x => x.Name), "slab or wall sections");
        Names(m.LoadPatterns.Select(x => x.Name), "load patterns");

        // Two members in the same place on the same story (different labels, same ends).
        foreach (var g in Physical.Frames(m).GroupBy(x => (x.Story, Key(x.I, x.J))).Where(g => g.Count() > 1))
            f.Add(new(Severity.Warning, "duplicates", $"{g.Count()} frame members lie on top of each other: {string.Join(", ", g.Select(x => x.Label))}.", g.First().Label, g.Key.Story));
    }

    private static void References(StructuralModel m, List<Finding> f)
    {
        var materials = m.Materials.Select(x => x.Name).ToHashSet();
        var frameSections = m.FrameSections.Select(x => x.Name).ToHashSet();
        var shellSections = m.ShellSections.Select(x => x.Name).ToHashSet();
        var points = m.Points.Select(p => p.Name).ToHashSet();
        var lines = m.Lines.Select(l => l.Label).ToHashSet();
        var areas = m.Areas.Select(a => a.Label).ToHashSet();
        var patterns = m.LoadPatterns.Select(p => p.Name).ToHashSet();
        bool Story(string name) => m.StoryIndex(name) >= 0;

        foreach (var s in m.FrameSections.Where(s => s.Material.Length > 0 && !materials.Contains(s.Material)))
            f.Add(new(Severity.Error, "references", $"Frame section '{s.Name}' uses material '{s.Material}', which is not defined.", s.Name));
        foreach (var s in m.ShellSections.Where(s => s.Material.Length > 0 && !materials.Contains(s.Material)))
            f.Add(new(Severity.Error, "references", $"Section '{s.Name}' uses material '{s.Material}', which is not defined.", s.Name));
        foreach (var l in m.Lines)
            foreach (var p in new[] { l.PointI, l.PointJ }.Where(p => !points.Contains(p)))
                f.Add(new(Severity.Error, "references", $"Line '{l.Label}' uses point '{p}', which does not exist.", l.Label));
        foreach (var a in m.Areas)
            foreach (var p in a.PointNames.Where(p => !points.Contains(p)).Distinct())
                f.Add(new(Severity.Error, "references", $"Area '{a.Label}' uses point '{p}', which does not exist.", a.Label));

        foreach (var a in m.LineAssigns)
        {
            if (!lines.Contains(a.Label)) f.Add(new(Severity.Error, "references", $"An assignment names line '{a.Label}', which does not exist.", a.Label, a.Story));
            if (!Story(a.Story)) f.Add(new(Severity.Error, "references", $"Line '{a.Label}' is assigned to story '{a.Story}', which does not exist.", a.Label, a.Story));
            if (a.Section is null) f.Add(new(Severity.Error, "sections", $"Line '{a.Label}' has no section on '{a.Story}'.", a.Label, a.Story));
            else if (!frameSections.Contains(a.Section) && a.Section != "None")
                f.Add(new(Severity.Error, "sections", $"Line '{a.Label}' on '{a.Story}' uses section '{a.Section}', which is not defined.", a.Label, a.Story));
        }
        // An opening (Draw Floor/Wall Openings) is an AREA-type object ETABS keeps apart from floors and walls, with no section.
        var openings = SlabDefs.Openings(m).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var a in m.AreaAssigns)
        {
            if (openings.Contains(a.Label) && a.Section is null) continue;
            if (!areas.Contains(a.Label)) f.Add(new(Severity.Error, "references", $"An assignment names area '{a.Label}', which does not exist.", a.Label, a.Story));
            if (!Story(a.Story)) f.Add(new(Severity.Error, "references", $"Area '{a.Label}' is assigned to story '{a.Story}', which does not exist.", a.Label, a.Story));
            if (a.Section is null) f.Add(new(Severity.Error, "sections", $"Area '{a.Label}' has no section on '{a.Story}'.", a.Label, a.Story));
            else if (!shellSections.Contains(a.Section) && a.Section != "None")
                f.Add(new(Severity.Error, "sections", $"Area '{a.Label}' on '{a.Story}' uses section '{a.Section}', which is not defined.", a.Label, a.Story));
        }
        foreach (var a in m.PointAssigns)
        {
            if (!points.Contains(a.Point)) f.Add(new(Severity.Error, "references", $"An assignment names point '{a.Point}', which does not exist.", a.Point, a.Story));
            if (!Story(a.Story)) f.Add(new(Severity.Error, "references", $"Point '{a.Point}' is assigned to story '{a.Story}', which does not exist.", a.Point, a.Story));
        }

        var assignedLines = m.LineAssigns.Select(a => a.Label).ToHashSet();
        foreach (var l in m.Lines.Where(l => !assignedLines.Contains(l.Label)))
            f.Add(new(Severity.Warning, "references", $"Line '{l.Label}' is on no story.", l.Label));
        var assignedAreas = m.AreaAssigns.Select(a => a.Label).ToHashSet();
        foreach (var a in m.Areas.Where(a => !assignedAreas.Contains(a.Label)))
            f.Add(new(Severity.Warning, "references", $"Area '{a.Label}' is on no story.", a.Label));

        void LoadRefs(IEnumerable<ObjectLoad> loads, HashSet<string> objects, string what)
        {
            foreach (var l in loads)
            {
                if (!objects.Contains(l.Object)) f.Add(new(Severity.Error, "loads", $"A load is on {what} '{l.Object}', which does not exist.", l.Object, l.Story));
                if (!Story(l.Story)) f.Add(new(Severity.Error, "loads", $"A load on {what} '{l.Object}' names story '{l.Story}', which does not exist.", l.Object, l.Story));
                if (l.Pattern.Length > 0 && !patterns.Contains(l.Pattern))
                    f.Add(new(Severity.Error, "loads", $"A load on {what} '{l.Object}' uses load pattern '{l.Pattern}', which is not defined.", l.Object, l.Story));
            }
        }
        LoadRefs(m.PointLoads, points, "point");
        LoadRefs(m.LineLoads, lines, "line");
        LoadRefs(m.AreaLoads, areas, "area");
    }

    private static void Geometry(StructuralModel m, List<Finding> f)
    {
        foreach (var x in Physical.Frames(m).Where(x => x.Length < 1e-6))
            f.Add(new(Severity.Error, "geometry", $"{x.Kind} '{x.Label}' on '{x.Story}' has no length.", x.Label, x.Story));
        foreach (var x in Physical.Areas(m).Where(x => x.Area < 1e-9))
            f.Add(new(Severity.Error, "geometry", $"Area '{x.Label}' on '{x.Story}' has no area.", x.Label, x.Story));

        // A beam end that nothing else reaches: no column, wall, brace or other beam at that joint.
        var frames = Physical.Frames(m).ToList();
        var joints = new Dictionary<(long, long, long), int>();
        void Touch(Xyz p) => joints[Key(p)] = joints.GetValueOrDefault(Key(p)) + 1;
        foreach (var x in frames) { Touch(x.I); Touch(x.J); }
        foreach (var a in Physical.Areas(m).Where(a => a.Kind == AreaKind.Wall))
            foreach (var c in a.Corners) Touch(c);
        foreach (var x in frames.Where(x => x.Kind == LineKind.Beam))
            foreach (var end in new[] { x.I, x.J }.Where(end => joints[Key(end)] < 2))
                f.Add(new(Severity.Warning, "connectivity", $"Beam '{x.Label}' on '{x.Story}' has a free end at ({end.X:0.###}, {end.Y:0.###}): nothing else joins it there.", x.Label, x.Story));
    }

    private static void Supports(StructuralModel m, List<Finding> f)
    {
        var restrained = new HashSet<(long, long, long)>();
        var points = m.Points.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var a in m.PointAssigns.Where(a => a.Restraint is not null))
        {
            var story = m.StoryIndex(a.Story);
            if (story >= 0 && points.TryGetValue(a.Point, out var p)) restrained.Add(Key(Physical.At(m, p, story)));
        }
        if (restrained.Count == 0)
        {
            f.Add(new(Severity.Error, "supports", "The model has no supports."));
            return;
        }
        // The foot of a column or wall on the base level with no support under it.
        if (m.Stories.Count == 0) return;
        var baseZ = m.Elevation(0);
        foreach (var x in Physical.Frames(m).Where(x => x.Kind == LineKind.Column && Math.Abs(x.I.Z - baseZ) < 1e-6 && !restrained.Contains(Key(x.I))))
            f.Add(new(Severity.Warning, "supports", $"Column '{x.Label}' on '{x.Story}' stands on the base level with no support under it.", x.Label, x.Story));
        foreach (var a in Physical.Areas(m).Where(a => a.Kind == AreaKind.Wall))
            if (a.Corners.Any(c => Math.Abs(c.Z - baseZ) < 1e-6 && !restrained.Contains(Key(c))))
                f.Add(new(Severity.Warning, "supports", $"Wall '{a.Label}' on '{a.Story}' stands on the base level with an unsupported corner.", a.Label, a.Story));
    }

    private static void Loads(StructuralModel m, List<Finding> f)
    {
        if (m.LoadPatterns.Count == 0)
        {
            f.Add(new(Severity.Warning, "loads", "The model has no load patterns."));
            return;
        }
        foreach (var p in m.LoadPatterns.Where(p => !ModelEdit.LoadPatternTypes.Contains(p.Type, StringComparer.OrdinalIgnoreCase)))
            f.Add(new(Severity.Warning, "loads", $"Load pattern '{p.Name}' has type '{p.Type}', which ETABS 23 does not list: it imports as 'Other'.", p.Name));
        if (!m.LoadPatterns.Exists(p => p.SelfWeight != 0))
            f.Add(new(Severity.Warning, "loads", "No load pattern includes self weight."));
        var used = m.PointLoads.Cast<ObjectLoad>().Concat(m.LineLoads).Concat(m.AreaLoads).Select(l => l.Pattern).ToHashSet();
        foreach (var p in m.LoadPatterns.Where(p => p.SelfWeight == 0 && !used.Contains(p.Name) && p.Extra.Count == 0))
            f.Add(new(Severity.Warning, "loads", $"Load pattern '{p.Name}' has no loads.", p.Name));
    }

    /// <summary>A point snapped to a 0.1 mm-ish grid (1e-4 model units), to compare positions.</summary>
    private static (long, long, long) Key(Xyz p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));

    private static ((long, long, long), (long, long, long)) Key(Xyz a, Xyz b)
    {
        var ka = Key(a); var kb = Key(b);
        return ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
    }
}
