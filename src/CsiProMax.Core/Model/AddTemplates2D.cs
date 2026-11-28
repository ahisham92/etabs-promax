namespace CsiProMax.Model;

/// <summary>
/// ETABS 23.2's Edit > Add to Model from Template > 2D Structure: a 2D Frame (Portal, Braced (Concentric), Braced (Eccentric)),
/// a 2D Truss (Sloped, Vertical, Pratt) or a 2D Wall (without or with openings), laid out along a line in plan from
/// (X, Y) at an angle about Z, as its forms ask (read on Ahmed's PC 2026-11-27). Lengths are in the model's units.
/// Where the brace and truss patterns follow the form's pictures, not a run of the program, it says so.
/// </summary>
public static class AddTemplates2D
{
    public sealed class Place
    {
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>Rotation Angle About Z, degrees from global X.</summary>
        public double Angle { get; set; }
        /// <summary>Where a distance u along the structure's line lands in plan.</summary>
        public (double X, double Y) At(double u)
        {
            var a = Angle * Math.PI / 180;
            return (Clean(X + u * Math.Cos(a)), Clean(Y + u * Math.Sin(a)));
        }
        private static double Clean(double v) => Math.Round(v, 9);
    }

    /// <summary>What it made: the labels of the frames and walls, each label@story.</summary>
    public sealed record Made(List<string> Frames, List<string> Walls, int Supports);

    /// <summary>2D Frame: type portal, concentric or eccentric; bays from left to right; stories bottom to top (lowest first).</summary>
    public static Made Frame(StructuralModel m, string type, IReadOnlyList<string> stories, int bays, double bayWidth, double gap,
        string columns, string beams, string? braces, string restraints, Place at)
    {
        type = type.ToLowerInvariant();
        if (type is not ("portal" or "concentric" or "eccentric")) throw new ArgumentException("The 2D frame type is portal, concentric or eccentric.");
        Check(m, stories, bays, bayWidth);
        Has(m, columns); Has(m, beams);
        if (type != "portal") Has(m, braces ?? "");
        if (type == "eccentric" && !(gap >= 0 && gap < bayWidth)) throw new ArgumentException("The gap width is 0 or more and less than the bay width.");
        var made = new Made([], [], 0);
        foreach (var story in stories)
        {
            for (var i = 0; i <= bays; i++) { var (x, y) = at.At(i * bayWidth); Add(made.Frames, m.AddColumn(x, y, story, columns)); }
            for (var i = 0; i < bays; i++)
            {
                var (x1, y1) = at.At(i * bayWidth); var (x2, y2) = at.At((i + 1) * bayWidth);
                Add(made.Frames, m.AddBeam(x1, y1, x2, y2, story, beams));
                if (type == "portal") continue;
                // Chevron braces from the bay's bottom corners up to the beam: to its middle (concentric), or to either
                // side of a gap there (eccentric), as the form's pictures draw them.
                var mid = (i + 0.5) * bayWidth; var half = type == "eccentric" ? gap / 2 : 0;
                var (lx, ly) = at.At(mid - half); var (rx, ry) = at.At(mid + half);
                Add(made.Frames, Release(m.AddBrace(x1, y1, lx, ly, story, braces!)));
                Add(made.Frames, Release(m.AddBrace(x2, y2, rx, ry, story, braces!)));
            }
        }
        return made with { Supports = Supports(m, stories[0], Enumerable.Range(0, bays + 1).Select(i => at.At(i * bayWidth)), restraints) };
    }

    /// <summary>2D Truss on one story: sloped, vertical or pratt, top chord at the story level, bottom chord the height below.</summary>
    public static Made Truss(StructuralModel m, string type, string story, int divisions, double length, double height,
        string chords, string diagonals, string? verticals, Place at)
    {
        type = type.ToLowerInvariant();
        if (type is not ("sloped" or "vertical" or "pratt")) throw new ArgumentException("The 2D truss type is sloped, vertical or pratt.");
        var si = m.StoryIndex(story);
        if (si < 1) throw new ArgumentException($"'{story}' is not a story above the base.");
        if (divisions < 1) throw new ArgumentException("The number of divisions is 1 or more.");
        if (!(length > 0)) throw new ArgumentException("The division length must be above 0.");
        if (!(height > 0) || height > m.Stories[si].Height + ModelEdit.Tolerance) throw new ArgumentException("The height is above 0 and no more than the story's height.");
        Has(m, chords); Has(m, diagonals);
        if (type != "sloped") Has(m, verticals ?? "");
        var made = new Made([], [], 0);
        PlanPoint Top(double u) { var (x, y) = at.At(u); return Pt(m, x, y, 0); }
        PlanPoint Bot(double u) { var (x, y) = at.At(u); return Pt(m, x, y, height); }
        if (type == "sloped")
        {
            // A Warren truss with sloped ends: the bottom chord's points sit midway between the top chord's.
            for (var k = 0; k < divisions; k++)
            {
                made.Frames.Add(Line(m, LineKind.Beam, Top(k * length), Top((k + 1) * length), story, chords));
                var b = Bot((k + 0.5) * length);
                made.Frames.Add(Line(m, LineKind.Brace, Top(k * length), b, story, diagonals, true));
                made.Frames.Add(Line(m, LineKind.Brace, b, Top((k + 1) * length), story, diagonals, true));
                if (k + 1 < divisions) made.Frames.Add(Line(m, LineKind.Beam, b, Bot((k + 1.5) * length), story, chords));
            }
            return made;
        }
        for (var k = 0; k < divisions; k++)
        {
            made.Frames.Add(Line(m, LineKind.Beam, Top(k * length), Top((k + 1) * length), story, chords));
            made.Frames.Add(Line(m, LineKind.Beam, Bot(k * length), Bot((k + 1) * length), story, chords));
            // Vertical: the diagonals alternate; Pratt: they slope down toward the middle.
            var down = type == "pratt" ? (k + 0.5) * length < divisions * length / 2 : k % 2 == 0;
            made.Frames.Add(down ? Line(m, LineKind.Brace, Top(k * length), Bot((k + 1) * length), story, diagonals, true)
                                 : Line(m, LineKind.Brace, Bot(k * length), Top((k + 1) * length), story, diagonals, true));
        }
        for (var k = 0; k <= divisions; k++) made.Frames.Add(Line(m, LineKind.Brace, Bot(k * length), Top(k * length), story, verticals!, true));
        return made;
    }

    /// <summary>One opening in each wall panel of each story: L from the panel's left, H up from its floor, B wide, D high.</summary>
    public sealed record Opening(double L, double H, double B, double D);

    /// <summary>2D Wall: one panel per bay per story, from the bottom story's floor to the top story; with openings, each
    /// panel is made of the pieces around its opening.</summary>
    public static Made Wall(StructuralModel m, IReadOnlyList<string> stories, int bays, double bayWidth, string section, Opening? opening, string restraints, Place at)
    {
        Check(m, stories, bays, bayWidth);
        if (!m.ShellSections.Exists(s => s.Name == section)) throw new ArgumentException($"The model has no wall section '{section}'.");
        var made = new Made([], [], 0);
        foreach (var story in stories)
        {
            var h = m.Stories[m.StoryIndex(story)].Height;
            if (opening is { } o && (o.L < 0 || o.H < 0 || !(o.B > 0) || !(o.D > 0) || o.L + o.B > bayWidth + ModelEdit.Tolerance || o.H + o.D > h + ModelEdit.Tolerance))
                throw new ArgumentException($"The opening does not fit in a {bayWidth} wide panel of {story} ({h} high).");
            for (var i = 0; i < bays; i++)
            {
                double u0 = i * bayWidth, u1 = u0 + bayWidth;
                if (opening is not { } op) { made.Walls.Add(Piece(m, at, u0, u1, 0, h, h, story, section)); continue; }
                double a = u0 + op.L, b = a + op.B, z0 = op.H, z1 = op.H + op.D;
                if (a > u0 + ModelEdit.Tolerance) made.Walls.Add(Piece(m, at, u0, a, 0, h, h, story, section));
                if (u1 > b + ModelEdit.Tolerance) made.Walls.Add(Piece(m, at, b, u1, 0, h, h, story, section));
                if (z0 > ModelEdit.Tolerance) made.Walls.Add(Piece(m, at, a, b, 0, z0, h, story, section));
                if (h > z1 + ModelEdit.Tolerance) made.Walls.Add(Piece(m, at, a, b, z1, h, h, story, section));
            }
        }
        return made with { Supports = Supports(m, stories[0], Enumerable.Range(0, bays + 1).Select(i => at.At(i * bayWidth)), restraints) };
    }

    // ---- shared ----

    private static void Check(StructuralModel m, IReadOnlyList<string> stories, int bays, double bayWidth)
    {
        if (stories.Count == 0) throw new ArgumentException("Pick the top and bottom stories.");
        foreach (var s in stories) if (m.StoryIndex(s) < 1) throw new ArgumentException($"'{s}' is not a story above the base.");
        if (bays < 1) throw new ArgumentException("The number of bays is 1 or more.");
        if (!(bayWidth > 0)) throw new ArgumentException("The bay width must be above 0.");
    }

    private static void Has(StructuralModel m, string section)
    {
        if (!m.FrameSections.Exists(f => f.Name == section)) throw new ArgumentException($"The model has no frame section '{section}'.");
    }

    private static void Add(List<string> to, LineAssign a) => to.Add(a.Label + "@" + a.Story);

    private static LineAssign Release(LineAssign a) { PropertyDefs.SetReleases(a, ["M2", "M3"], ["M2", "M3"]); return a; }

    /// <summary>The point at (x, y) this far below its story level, made if there is none.</summary>
    private static PlanPoint Pt(StructuralModel m, double x, double y, double below)
    {
        if (Math.Abs(below) <= ModelEdit.Tolerance) return m.PointAt(x, y);
        var p = m.Points.Find(p => Math.Abs(p.Below - below) <= ModelEdit.Tolerance && Math.Abs(p.X - x) <= ModelEdit.Tolerance && Math.Abs(p.Y - y) <= ModelEdit.Tolerance);
        if (p is not null) return p;
        p = new PlanPoint { Name = ModelEdit.NextName(m.Points.Select(q => q.Name), ""), X = x, Y = y, Below = below };
        m.Points.Add(p);
        return p;
    }

    /// <summary>A frame with both ends on the story (either may sit below its level).</summary>
    private static string Line(StructuralModel m, LineKind kind, PlanPoint i, PlanPoint j, string story, string section, bool pinned = false)
    {
        var line = m.Lines.Find(l => l.Kind == kind && l.StoriesBelowI == 0 && (l.PointI == i.Name && l.PointJ == j.Name || l.PointI == j.Name && l.PointJ == i.Name));
        if (line is null)
        {
            line = new LineObject { Label = ModelEdit.NextName(m.Lines.Select(l => l.Label), kind == LineKind.Beam ? "B" : "D"), Kind = kind, PointI = i.Name, PointJ = j.Name };
            m.Lines.Add(line);
        }
        var a = m.LineAssigns.Find(x => x.Label == line.Label && x.Story == story);
        if (a is null) m.LineAssigns.Add(a = new LineAssign { Label = line.Label, Story = story, Extra = [ModelEdit.LineAssignDefaults(kind)] });
        a.Section = section;
        if (pinned) PropertyDefs.SetReleases(a, ["M2", "M3"], ["M2", "M3"]);
        return a.Label + "@" + story;
    }

    /// <summary>A wall piece from u0 to u1 along the line, z0 to z1 up from the story's floor (h high).</summary>
    private static string Piece(StructuralModel m, Place at, double u0, double u1, double z0, double z1, double h, string story, string section)
    {
        if (z0 <= ModelEdit.Tolerance && z1 >= h - ModelEdit.Tolerance)
        {
            var (x1, y1) = at.At(u0); var (x2, y2) = at.At(u1);
            var w = m.AddWall(x1, y1, x2, y2, story, section);
            return w.Label + "@" + story;
        }
        var (ax, ay) = at.At(u0); var (bx, by) = at.At(u1);
        var pts = new[] { Pt(m, ax, ay, h - z0), Pt(m, bx, by, h - z0), Pt(m, bx, by, h - z1), Pt(m, ax, ay, h - z1) };
        var names = pts.Select(p => p.Name).ToList(); var below = names.Select(_ => 0).ToList();
        var area = m.Areas.Find(a => a.Kind == AreaKind.Wall && a.PointNames.SequenceEqual(names) && a.StoriesBelow.SequenceEqual(below));
        if (area is null)
        {
            area = new AreaObject { Label = ModelEdit.NextName(m.Areas.Select(a => a.Label), "W"), Kind = AreaKind.Wall, PointNames = names, StoriesBelow = below };
            m.Areas.Add(area);
        }
        var asg = m.AreaAssigns.Find(a => a.Label == area.Label && a.Story == story);
        if (asg is null) m.AreaAssigns.Add(asg = new AreaAssign { Label = area.Label, Story = story, Extra = [ModelEdit.AreaAssignDefaults(AreaKind.Wall)] });
        asg.Section = section;
        return area.Label + "@" + story;
    }

    private static int Supports(StructuralModel m, string bottomStory, IEnumerable<(double X, double Y)> at, string restraints)
    {
        var r = restraints.ToLowerInvariant();
        if (r == "none") return 0;
        if (r is not ("pinned" or "fixed")) throw new ArgumentException("The restraints at the bottom are none, pinned or fixed.");
        var level = m.Stories[m.StoryIndex(bottomStory) - 1].Name;
        var n = 0;
        foreach (var (x, y) in at.Distinct()) { m.AddSupport(x, y, level, r == "fixed"); n++; }
        return n;
    }
}
