using CsiProMax.Model;

namespace CsiProMax.Checks.Stability;

/// <summary>
/// Finds what stops a model's analysis from completing, or makes the program call it unstable, without running it:
/// definitions that are missing or have no stiffness, members too short or on top of each other, parts that touch
/// nothing that holds them, releases that make a mechanism, members that pass a joint without joining it, shells that
/// will not mesh, load cases that name what is not there and modal cases with no mass. Then a light stiffness check
/// (StiffnessProbe) for the mechanisms rules cannot see. Every finding names its objects, where they are, and a fix.
/// The same model always gives the same findings.
/// </summary>
public static class StabilityCheck
{
    public sealed record Options(bool Stiffness = true, int MostFindings = 400, int MostEquations = 60000);

    public sealed record Report(IReadOnlyList<Instability> Findings, IReadOnlyList<string> Notes, int Joints, int Frames, int Shells,
        int Parts, int Equations, bool StiffnessRan)
    {
        public int Errors => Findings.Count(f => f.Level == Level.Error);
        public int Warnings => Findings.Count(f => f.Level == Level.Warning);
    }

    private static readonly string[] DofNames = ["UX", "UY", "UZ", "RX", "RY", "RZ"];
    private static readonly string[] DofWords = ["along X", "along Y", "up and down (Z)", "about X", "about Y", "about Z"];

    public static Report Run(AnalysisModel a, Options? options = null)
    {
        options ??= new Options();
        var c = new Context(a);
        Definitions(c);
        Geometry(c);
        Duplicates(c);
        var parts = Connect(c);
        Releases(c);
        var (equations, ran) = options.Stiffness ? Stiffness(c, options) : (0, false);
        if (!options.Stiffness) c.Notes.Add("The stiffness check was not asked for.");
        LoadsAndCases(c);
        var ordered = c.Found.OrderBy(f => f.Level).ThenBy(f => Order(f.Check)).ToList();
        if (ordered.Count > options.MostFindings)
        {
            c.Notes.Add($"{ordered.Count} findings; the first {options.MostFindings} are listed, errors first.");
            ordered = ordered.Take(options.MostFindings).ToList();
        }
        return new Report(ordered, c.Notes, a.Joints.Count, a.Frames.Count, a.Shells.Count, parts, equations, ran);
    }

    private static int Order(string check) => Array.IndexOf(Checks, check) is var i and >= 0 ? i : Checks.Length;

    /// <summary>The checks, in the order they are listed.</summary>
    public static readonly string[] Checks =
        ["supports", "floating", "definitions", "mechanism", "releases", "geometry", "duplicates", "connectivity", "meshing", "cases", "mass", "loads"];

    private sealed class Context(AnalysisModel a)
    {
        public AnalysisModel M { get; } = a;
        public List<Instability> Found { get; } = [];
        public List<string> Notes { get; } = [.. a.Notes];
        public Dictionary<string, int> Index { get; } = a.Joints.Select((j, i) => (j.Name, i)).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        /// <summary>Frames that carry stiffness, as joint pieces after splitting at the joints they pass (ETABS).</summary>
        public List<(int I, int J, Frame F)> Pieces { get; } = [];
        /// <summary>Shells that carry stiffness: their corners and the joints meshed into them.</summary>
        public List<(int[] Joints, Shell S, ShellProp P)> Plates { get; } = [];
        /// <summary>The objects at each joint, for the findings.</summary>
        public List<ObjectRef>[] At { get; } = a.Joints.Select(_ => new List<ObjectRef>()).ToArray();
        public bool[] Floating { get; set; } = new bool[a.Joints.Count];
        public HashSet<string> Stiffless { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> StifflessShells { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(Level level, string check, string message, string fix, IEnumerable<ObjectRef> objects, IEnumerable<Xyz> at) =>
            Found.Add(new Instability(level, check, message, fix, objects.Take(200).ToList(), at.Take(200).ToList()));

        public Joint J(int i) => M.Joints[i];
        public Xyz Xyz(int i) => M.Joints[i].At;
        public static ObjectRef Ref(Joint j) => new("joint", j.Name, j.Story);
        public static ObjectRef Ref(Frame f) => new("frame", f.Name, f.Story);
        public static ObjectRef Ref(Shell s) => new("shell", s.Name, s.Story);
    }

    private static string Show(Joint j) => j.Story is { } s && j.Label != j.Name ? $"point {j.Label} on {s}" : $"joint {j.Name}";
    private static string Show(Frame f) => f.Story is { } s && f.Label != f.Name ? $"{Kind(f.Kind)} {f.Label} on {s}" : $"frame {f.Name}";
    private static string Show(Shell s) => s.Story is { } st && s.Label != s.Name ? $"{(s.Kind == "wall" ? "wall" : s.Kind == "floor" ? "floor" : "area")} {s.Label} on {st}" : $"area {s.Name}";
    private static string Kind(string k) => k is "column" or "beam" or "brace" ? k : "frame";
    private static string List(IEnumerable<string> names, int most = 8)
    {
        var all = names.ToList();
        return all.Count <= most ? string.Join(", ", all) : string.Join(", ", all.Take(most)) + $" and {all.Count - most} more";
    }

    // ==============================================================================================================
    // Definitions: what the members are made of
    // ==============================================================================================================

    private static void Definitions(Context c)
    {
        var a = c.M;
        bool NoneSection(string? s) => s is null || s.Length == 0 || s.Equals("None", StringComparison.OrdinalIgnoreCase);

        var badMaterial = a.Materials.Values.Where(m => m.E is <= 0).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in a.Frames.GroupBy(f => f.Section ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var frames = group.ToList();
            var section = group.Key;
            var refs = frames.Select(Context.Ref);
            var at = frames.Select(f => Mid(c, f)).OfType<Xyz>();
            if (NoneSection(section))
            {
                foreach (var f in frames) c.Stiffless.Add(f.Name);
                if (section.Length == 0)
                    c.Add(Level.Error, "definitions", $"{frames.Count} frame(s) have no section: {List(frames.Select(Show))}.",
                        "Assign a frame section to them (Assign > Frame > Section), or delete them.", refs, at);
                continue;
            }
            if (!a.FrameProps.TryGetValue(section, out var p))
            {
                foreach (var f in frames) c.Stiffless.Add(f.Name);
                c.Add(Level.Error, "definitions", $"Frame section '{section}' is not defined, and {frames.Count} frame(s) use it: {List(frames.Select(Show))}.",
                    $"Define '{section}' (Define > Section Properties > Frame Sections), or assign the frames a section that exists.", refs, at);
                continue;
            }
            if (p.Material is { Length: > 0 } mat && a.Materials.Count > 0 && !a.Materials.ContainsKey(mat))
            {
                foreach (var f in frames) c.Stiffless.Add(f.Name);
                c.Add(Level.Error, "definitions", $"Frame section '{section}' uses material '{mat}', which is not defined ({frames.Count} frame(s) use the section).",
                    $"Define material '{mat}' or give '{section}' a material that exists.", refs, at);
                continue;
            }
            if (p.Material is { } m2 && badMaterial.Contains(m2))
            {
                foreach (var f in frames) c.Stiffless.Add(f.Name);
                c.Add(Level.Error, "definitions", $"Frame section '{section}' is made of '{m2}', whose modulus of elasticity is 0: its {frames.Count} frame(s) have no stiffness.",
                    $"Give '{m2}' its modulus of elasticity (Define > Material Properties).", refs, at);
                continue;
            }
            if (p.Area is <= 0 || p.I33 is <= 0 && p.I22 is <= 0 || p.Modifier <= 0)
            {
                foreach (var f in frames) c.Stiffless.Add(f.Name);
                c.Add(Level.Error, "definitions", $"Frame section '{section}' has no stiffness (its area or moments of inertia are 0); {frames.Count} frame(s) use it: {List(frames.Select(Show))}.",
                    $"Give '{section}' its real dimensions, or check its property modifiers are not 0.", refs, at);
            }
        }

        foreach (var group in a.Shells.GroupBy(s => s.Section ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var shells = group.ToList();
            var section = group.Key;
            var refs = shells.Select(Context.Ref);
            var at = shells.Select(s => Centre(c, s)).OfType<Xyz>();
            if (NoneSection(section))
            {
                foreach (var s in shells) c.StifflessShells.Add(s.Name);
                if (section.Length == 0)
                    c.Add(Level.Error, "definitions", $"{shells.Count} area(s) have no section: {List(shells.Select(Show))}.",
                        "Assign them a slab, wall or shell section, or delete them.", refs, at);
                continue;
            }
            if (!a.ShellProps.TryGetValue(section, out var p))
            {
                foreach (var s in shells) c.StifflessShells.Add(s.Name);
                c.Add(Level.Error, "definitions", $"Section '{section}' is not defined, and {shells.Count} area(s) use it: {List(shells.Select(Show))}.",
                    $"Define '{section}', or assign the areas a section that exists.", refs, at);
                continue;
            }
            if (p.Material is { Length: > 0 } mat && a.Materials.Count > 0 && !a.Materials.ContainsKey(mat))
            {
                foreach (var s in shells) c.StifflessShells.Add(s.Name);
                c.Add(Level.Error, "definitions", $"Section '{section}' uses material '{mat}', which is not defined.", $"Define material '{mat}' or give '{section}' a material that exists.", refs, at);
                continue;
            }
            if (p.Material is { } m2 && badMaterial.Contains(m2) || p.Thickness is <= 0)
            {
                foreach (var s in shells) c.StifflessShells.Add(s.Name);
                c.Add(Level.Error, "definitions", $"Section '{section}' has no stiffness ({(p.Thickness is <= 0 ? "its thickness is 0" : $"material '{p.Material}' has a modulus of 0")}); {shells.Count} area(s) use it.",
                    $"Give '{section}' its thickness and a material with a modulus of elasticity.", refs, at);
            }
        }
        if (a.Frames.Count == 0 && a.Shells.Count == 0)
            c.Add(Level.Error, "definitions", "The model has no members: no frames and no areas.", "Draw the structure first.", [], []);
    }

    private static Xyz? Mid(Context c, Frame f) =>
        c.Index.TryGetValue(f.I, out var i) && c.Index.TryGetValue(f.J, out var j) ? new Xyz((c.Xyz(i).X + c.Xyz(j).X) / 2, (c.Xyz(i).Y + c.Xyz(j).Y) / 2, (c.Xyz(i).Z + c.Xyz(j).Z) / 2) : null;

    private static Xyz? Centre(Context c, Shell s)
    {
        var pts = s.Joints.Where(c.Index.ContainsKey).Select(n => c.Xyz(c.Index[n])).ToList();
        return pts.Count == 0 ? null : new Xyz(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
    }

    // ==============================================================================================================
    // Geometry: members too short, areas that are not areas
    // ==============================================================================================================

    private static void Geometry(Context c)
    {
        var a = c.M;
        var tol = a.Tolerance;
        foreach (var f in a.Frames)
        {
            if (!c.Index.TryGetValue(f.I, out var i) || !c.Index.TryGetValue(f.J, out var j))
            {
                c.Add(Level.Error, "geometry", $"{Show(f)} joins a joint that does not exist ({(c.Index.ContainsKey(f.I) ? f.J : f.I)}).", "Delete the member and draw it again between joints that exist.", [Context.Ref(f)], []);
                c.Stiffless.Add(f.Name);
                continue;
            }
            var len = Distance(c.Xyz(i), c.Xyz(j));
            if (i == j || len <= tol)
            {
                c.Stiffless.Add(f.Name);
                c.Add(Level.Error, "geometry", $"{Show(f)} is {(len == 0 ? "of zero length" : $"only {len:0.####} {a.LengthUnit} long")}: both its ends are in the same place.",
                    "Delete it, or move one end to where the member really goes. A member this short makes the stiffness huge or undefined.", [Context.Ref(f)], [c.Xyz(i)]);
            }
            else if (len <= 10 * tol)
                c.Add(Level.Warning, "geometry", $"{Show(f)} is only {len:0.####} {a.LengthUnit} long.",
                    "Check it is meant to be there: a very short member is usually a drawing slip, and its stiffness swamps its neighbours'.", [Context.Ref(f)], [c.Xyz(i)]);
        }
        foreach (var s in a.Shells)
        {
            var idx = s.Joints.Where(c.Index.ContainsKey).Select(n => c.Index[n]).ToList();
            var distinct = idx.Distinct().ToList();
            var pts = distinct.Select(c.Xyz).ToList();
            if (idx.Count < s.Joints.Count)
            {
                c.StifflessShells.Add(s.Name);
                c.Add(Level.Error, "geometry", $"{Show(s)} uses a corner joint that does not exist.", "Delete the area and draw it again.", [Context.Ref(s)], pts);
                continue;
            }
            if (distinct.Count < 3 || PolygonArea(pts) <= tol * tol)
            {
                c.StifflessShells.Add(s.Name);
                c.Add(Level.Error, "geometry", $"{Show(s)} has no area ({(distinct.Count < 3 ? "fewer than 3 different corners" : "its corners are on one line")}).",
                    "Delete it, or redraw it with its real corners.", [Context.Ref(s)], pts);
                continue;
            }
            if (distinct.Count < idx.Count)
                c.Add(Level.Warning, "geometry", $"{Show(s)} uses the same corner twice.", "Redraw it with each corner once.", [Context.Ref(s)], pts);
            // Warped: a corner off the plane of the others.
            if (pts.Count >= 4 && Warp(pts) is var w && w > Math.Max(10 * tol, 1e-3 * Size(pts)))
                c.Add(Level.Warning, "meshing", $"{Show(s)} is warped: a corner is {w:0.###} {a.LengthUnit} off the plane of the others.",
                    "Split it into flat areas (triangles or flat quadrilaterals), or move the corner into the plane.", [Context.Ref(s)], pts);
            var (shortest, longest) = Sides(pts);
            if (shortest > 0 && longest / shortest > 20)
                c.Add(Level.Warning, "meshing", $"{Show(s)} is a sliver: its longest side is {longest / shortest:0} times its shortest.",
                    "Divide it or merge it with its neighbour: slivers mesh badly and give poor results.", [Context.Ref(s)], pts);
            if (s.Joints.Count > 4 && a.Program == "sap2000")
                c.Add(Level.Warning, "meshing", $"{Show(s)} has {s.Joints.Count} corners. SAP2000 analyses areas of 3 or 4 corners; one with more must be meshed.",
                    "Assign > Area > Automatic Area Mesh on it, or divide it into 4-corner areas.", [Context.Ref(s)], pts);
        }
    }

    // ==============================================================================================================
    // Duplicates: joints, members and areas on top of each other
    // ==============================================================================================================

    private static void Duplicates(Context c)
    {
        var a = c.M;
        var tol = a.Tolerance;
        // Joints at the same place under different names: members on one do not join members on the other.
        var cells = new Dictionary<(long, long, long), List<int>>();
        for (var i = 0; i < a.Joints.Count; i++)
        {
            var k = Cell(c.Xyz(i), tol);
            if (!cells.TryGetValue(k, out var l)) cells[k] = l = [];
            l.Add(i);
        }
        var reported = new HashSet<int>();
        for (var i = 0; i < a.Joints.Count; i++)
        {
            if (reported.Contains(i)) continue;
            var same = Near(cells, c.Xyz(i), tol).Where(j => j != i && Distance(c.Xyz(i), c.Xyz(j)) <= tol).ToList();
            if (same.Count == 0) continue;
            same.Insert(0, i);
            foreach (var s in same) reported.Add(s);
            var joints = same.Select(c.J).ToList();
            c.Add(Level.Warning, "duplicates", $"{joints.Count} joints are in the same place: {List(joints.Select(Show))}. Members on one do not join members on the other.",
                "Merge them (Edit > Merge Joints in SAP2000; in ETABS, Edit > Merge Points), so the members meet at one joint.", joints.Select(Context.Ref), [c.Xyz(i)]);
        }

        // Members with the same two ends.
        foreach (var g in a.Frames.Where(f => c.Index.ContainsKey(f.I) && c.Index.ContainsKey(f.J) && f.I != f.J)
                     .GroupBy(f => string.CompareOrdinal(f.I, f.J) < 0 ? (f.I, f.J) : (f.J, f.I)).Where(g => g.Count() > 1))
        {
            var frames = g.ToList();
            c.Add(Level.Warning, "duplicates", $"{frames.Count} members lie on top of each other: {List(frames.Select(Show))}.",
                "Delete all but one. Doubled members double the stiffness and the weight.", frames.Select(Context.Ref), frames.Select(f => Mid(c, f)).OfType<Xyz>().Take(1));
        }
        // Areas with the same corners.
        foreach (var g in a.Shells.GroupBy(s => string.Join("|", s.Joints.Distinct().OrderBy(x => x, StringComparer.Ordinal))).Where(g => g.Count() > 1 && g.Key.Length > 0))
        {
            var shells = g.ToList();
            c.Add(Level.Warning, "duplicates", $"{shells.Count} areas lie on top of each other: {List(shells.Select(Show))}.",
                "Delete all but one.", shells.Select(Context.Ref), shells.Select(s => Centre(c, s)).OfType<Xyz>().Take(1));
        }
        // Members that overlap along part of their length (one on the same line as another, sharing part of it).
        var lines = a.Frames.Where(f => c.Index.ContainsKey(f.I) && c.Index.ContainsKey(f.J) && !c.Stiffless.Contains(f.Name))
            .Select(f => (F: f, A: c.Xyz(c.Index[f.I]), B: c.Xyz(c.Index[f.J]))).Where(x => Distance(x.A, x.B) > tol).ToList();
        var byDirection = lines.GroupBy(x => DirectionKey(x.A, x.B)).Where(g => g.Count() > 1);
        var pairs = 0;
        foreach (var g in byDirection)
        {
            var list = g.ToList();
            if (list.Count > 3000) continue;
            for (var i = 0; i < list.Count && pairs < 50; i++)
                for (var j = i + 1; j < list.Count && pairs < 50; j++)
                {
                    var (f1, a1, b1) = list[i]; var (f2, a2, b2) = list[j];
                    if (SameEnds(a1, b1, a2, b2, tol)) continue;
                    if (DistanceToLine(a2, a1, b1) > tol || DistanceToLine(b2, a1, b1) > tol) continue;
                    var overlap = Overlap(a1, b1, a2, b2);
                    if (overlap <= tol) continue;
                    pairs++;
                    c.Add(Level.Warning, "duplicates", $"{Show(f1)} and {Show(f2)} overlap along {overlap:0.###} {a.LengthUnit}.",
                        "Delete the overlapping part: split the longer member where the shorter one ends, and keep one member on each stretch.",
                        [Context.Ref(f1), Context.Ref(f2)], [Lerp(a1, b1, 0.5)]);
                }
        }
    }

    // ==============================================================================================================
    // Connectivity: what is joined to what, what holds it, and the parts that float
    // ==============================================================================================================

    private static int Connect(Context c)
    {
        var a = c.M;
        var tol = a.Tolerance;
        var n = a.Joints.Count;
        var cells = new Dictionary<(long, long, long), List<int>>();
        var size = CellSize(c);
        for (var i = 0; i < n; i++)
        {
            var k = Cell(c.Xyz(i), size);
            if (!cells.TryGetValue(k, out var l)) cells[k] = l = [];
            l.Add(i);
        }
        var used = new bool[n];
        foreach (var f in a.Frames)
        {
            if (!c.Index.TryGetValue(f.I, out var i) || !c.Index.TryGetValue(f.J, out var j) || i == j) continue;
            c.At[i].Add(Context.Ref(f)); c.At[j].Add(Context.Ref(f));
            used[i] = used[j] = true;
        }
        foreach (var s in a.Shells)
            foreach (var jn in s.Joints.Where(c.Index.ContainsKey).Distinct())
            {
                c.At[c.Index[jn]].Add(Context.Ref(s));
                used[c.Index[jn]] = true;
            }

        // Frames: split at the joints they pass through when the program joins them there (ETABS, a meshed SAP2000
        // frame); otherwise a joint on a member's span is not joined to it, which is said.
        var passing = 0;
        foreach (var f in a.Frames)
        {
            if (c.Stiffless.Contains(f.Name) || !c.Index.TryGetValue(f.I, out var i) || !c.Index.TryGetValue(f.J, out var j) || i == j) continue;
            var A = c.Xyz(i); var B = c.Xyz(j);
            var inside = JointsOnSegment(cells, size, A, B, tol, c).Where(k => k != i && k != j && used[k]).ToList();
            if (inside.Count == 0) { c.Pieces.Add((i, j, f)); continue; }
            if (a.JoinsAtIntersections || f.Meshed)
            {
                var chain = inside.OrderBy(k => Along(A, B, c.Xyz(k))).ToList();
                var prev = i;
                foreach (var k in chain) { if (Distance(c.Xyz(prev), c.Xyz(k)) > tol) c.Pieces.Add((prev, k, f)); prev = k; c.At[k].Add(Context.Ref(f)); }
                if (Distance(c.Xyz(prev), B) > tol) c.Pieces.Add((prev, j, f));
            }
            else
            {
                c.Pieces.Add((i, j, f));
                if (passing++ < 60)
                {
                    var joints = inside.Select(c.J).ToList();
                    c.Add(Level.Warning, "connectivity", $"{Show(f)} passes through {List(joints.Select(Show), 4)} without being joined there: what meets it at that joint is not held by it.",
                        "Divide the frame at the joint (Edit > Edit Lines > Divide Frames), or assign it Automatic Frame Mesh at intersections.",
                        [Context.Ref(f), .. joints.Select(Context.Ref)], inside.Select(c.Xyz));
                }
            }
        }
        if (passing > 60) c.Notes.Add($"{passing} frames pass through joints without being joined; the first 60 are listed.");

        // Shells: their corners, and when the program meshes them (ETABS), the joints that lie on them.
        foreach (var s in a.Shells)
        {
            if (c.StifflessShells.Contains(s.Name) || s.Section is null || !a.ShellProps.TryGetValue(s.Section, out var p)) continue;
            var corners = s.Joints.Where(c.Index.ContainsKey).Select(x => c.Index[x]).Distinct().ToList();
            if (corners.Count < 3) continue;
            var pts = corners.Select(c.Xyz).ToList();
            var all = new List<int>(corners);
            var onIt = JointsOnPolygon(cells, size, pts, tol, c).Where(k => !corners.Contains(k) && used[k]).ToList();
            if (onIt.Count > 0)
            {
                if (a.JoinsAtIntersections && !s.MeshNone)
                {
                    all.AddRange(onIt);
                    foreach (var k in onIt) c.At[k].Add(Context.Ref(s));
                }
                else
                {
                    var edge = onIt.Where(k => OnEdge(pts, c.Xyz(k), tol)).ToList();
                    if (edge.Count > 0)
                        c.Add(Level.Warning, "connectivity", $"{Show(s)} has {List(edge.Select(k => Show(c.J(k))), 4)} on its edge, not at a corner: the area is not joined there unless it is meshed.",
                            a.Program == "sap2000" ? "Assign > Area > Automatic Area Mesh (mesh at the joints on its edges), or divide the area at the joint."
                                : "Mesh the area (Assign > Shell > Wall/Floor Auto Mesh Options), or divide it at the joint.",
                            [Context.Ref(s), .. edge.Select(k => Context.Ref(c.J(k)))], edge.Select(c.Xyz));
                }
            }
            c.Plates.Add((all.ToArray(), s, p));
        }

        // Parts: joints joined by members, shells and rigid floors.
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[x] = y; }
        foreach (var (i, j, _) in c.Pieces) Union(i, j);
        foreach (var (joints, _, _) in c.Plates) for (var k = 1; k < joints.Length; k++) Union(joints[0], joints[k]);
        foreach (var g in Floors(c)) for (var k = 1; k < g.Count; k++) Union(g[0], g[k]);

        var supported = new bool[n];
        for (var i = 0; i < n; i++) supported[i] = c.J(i).Supported;
        foreach (var (i, j, f) in c.Pieces) if (f.Spring) supported[i] = supported[j] = true;
        foreach (var (joints, s, _) in c.Plates) if (s.Spring) foreach (var k in joints) supported[k] = true;

        var structural = new bool[n];
        foreach (var (i, j, _) in c.Pieces) structural[i] = structural[j] = true;
        foreach (var (joints, _, _) in c.Plates) foreach (var k in joints) structural[k] = true;

        if (!Enumerable.Range(0, n).Any(i => supported[i]))
        {
            c.Add(Level.Error, "supports", "The model has no supports: nothing holds it to the ground, so it floats as a whole.",
                a.PlateOnly ? "Add point supports, columns or walls below, or soil springs under the slab (Assign > Support Data)."
                    : "Restrain the joints at the base (Assign > Joint > Restraints: fixed or pinned), or give them springs.", [], []);
            for (var i = 0; i < n; i++) c.Floating[i] = structural[i];
            return Enumerable.Range(0, n).Where(i => structural[i]).Select(Find).Distinct().Count();
        }

        var groups = Enumerable.Range(0, n).Where(i => structural[i] || supported[i]).GroupBy(Find).ToList();
        var floatingParts = 0;
        foreach (var g in groups)
        {
            var joints = g.ToList();
            if (joints.Any(i => supported[i])) continue;
            floatingParts++;
            foreach (var k in joints) c.Floating[k] = true;
            var objects = joints.SelectMany(k => c.At[k]).Distinct().ToList();
            var shown = objects.Select(o => o.Story is { } st ? $"{o.Name.Split('@')[0]} on {st}" : o.Name);
            if (floatingParts <= 40)
                c.Add(Level.Error, "floating", $"A part of the model ({objects.Count} object(s): {List(shown, 6)}) touches nothing that is supported: it floats free of the rest.",
                    "Join it to the structure (share a joint with a member that is held, or draw the member that connects it), or support it, or delete it if it is left over.",
                    objects, joints.Select(c.Xyz));
        }
        if (floatingParts > 40) c.Notes.Add($"{floatingParts} parts float free; the first 40 are listed.");

        // Joints with a load and nothing to carry it.
        var lost = Enumerable.Range(0, n).Where(i => !structural[i] && c.J(i).Loaded && !supported[i]).ToList();
        if (lost.Count > 0)
            c.Add(Level.Warning, "loads", $"{lost.Count} joint(s) carry a load and no member: {List(lost.Select(i => Show(c.J(i))))}. The load goes nowhere.",
                "Move the load onto a joint of the structure, or draw the member that carries it.", lost.Select(i => Context.Ref(c.J(i))), lost.Select(c.Xyz));
        return groups.Count;
    }

    /// <summary>The joints of each rigid floor: one per diaphragm name and level.</summary>
    private static List<List<int>> Floors(Context c)
    {
        var tol = c.M.Tolerance;
        return Enumerable.Range(0, c.M.Joints.Count).Where(i => c.J(i).Diaphragm is { } d && c.M.RigidDiaphragms.Contains(d))
            .GroupBy(i => (c.J(i).Diaphragm!.ToUpperInvariant(), (long)Math.Round(c.J(i).Z / Math.Max(tol * 10, 1e-9))))
            .Select(g => g.ToList()).Where(g => g.Count > 1).ToList();
    }

    // ==============================================================================================================
    // Releases that leave a member free to move on its own
    // ==============================================================================================================

    private static void Releases(Context c)
    {
        string[] words = ["axial force (P)", "shear V2", "shear V3", "torsion (T)"];
        var found = new Dictionary<int, List<Frame>>();
        foreach (var f in c.M.Frames.Where(f => f.Releases.Length >= 12))
        {
            for (var d = 0; d < 4; d++)
                if (f.Releases[d] && f.Releases[d + 6])
                {
                    if (!found.TryGetValue(d, out var l)) found[d] = l = [];
                    l.Add(f);
                }
            // Moment M2 or M3 released at both ends together with the matching shear at one end.
            if (f.Releases[4] && f.Releases[10] && (f.Releases[2] || f.Releases[8]) || f.Releases[5] && f.Releases[11] && (f.Releases[1] || f.Releases[7]))
            {
                if (!found.TryGetValue(9, out var l)) found[9] = l = [];
                l.Add(f);
            }
        }
        foreach (var (d, frames) in found)
        {
            var what = d == 9 ? "a moment at both ends and the shear in the same plane" : words[d] + " at both ends";
            c.Add(Level.Error, "releases", $"{frames.Count} frame(s) are released for {what}: {List(frames.Select(Show))}. Each is free to {(d == 3 ? "spin about its own axis" : d == 0 ? "slide along its own axis" : "move sideways")} with nothing to hold it.",
                d == 3 ? "Release torsion at one end only (Assign > Frame > Releases/Partial Fixity)." : "Keep the release at one end only, or take it off.",
                frames.Select(Context.Ref), frames.Select(f => Mid(c, f)).OfType<Xyz>());
        }
    }

    // ==============================================================================================================
    // The stiffness check
    // ==============================================================================================================

    private static (int Equations, bool Ran) Stiffness(Context c, Options o)
    {
        var a = c.M;
        var n = a.Joints.Count;
        if (n == 0) return (0, false);
        var xyz = a.Joints.Select(j => (j.X, j.Y, j.Z)).ToList();
        var fixedDof = new bool[n][];
        var spring = new bool[n][];
        for (var i = 0; i < n; i++)
        {
            // A floating part is already reported: held here so it does not hide what else is free.
            fixedDof[i] = c.Floating[i] ? [true, true, true, true, true, true] : (bool[])c.J(i).Restraint.Clone();
            spring[i] = c.J(i).Spring && !c.Floating[i] ? [true, true, true, false, false, false] : new bool[6];
        }
        foreach (var (i, j, f) in c.Pieces.Where(p => p.F.Spring))
            foreach (var k in new[] { i, j }) if (!c.Floating[k]) spring[k] = [true, true, true, false, false, false];
        foreach (var (joints, s, _) in c.Plates.Where(p => p.S.Spring))
            foreach (var k in joints) if (!c.Floating[k]) spring[k][2] = true;

        // Stiffness numbers: what the program gave, else of the order of the model's own.
        var moduli = a.Materials.Values.Where(m => m.E is > 0).Select(m => m.E!.Value).OrderBy(x => x).ToList();
        var typicalE = moduli.Count > 0 ? moduli[moduli.Count / 2] : 1.0;
        double E(string? material) => material is not null && a.Materials.TryGetValue(material, out var m) && m.E is > 0 ? m.E.Value : typicalE;
        var lengths = c.Pieces.Select(p => Distance(c.Xyz(p.I), c.Xyz(p.J))).Where(l => l > 0).OrderBy(l => l).ToList();
        var typicalL = lengths.Count > 0 ? lengths[lengths.Count / 2] : 1.0;
        var defaultArea = Math.Pow(typicalL / 15, 2);
        var defaultI = defaultArea * defaultArea / 12;

        var bars = new List<StiffnessProbe.Bar>();
        var guessed = 0;
        foreach (var (i, j, f) in c.Pieces)
        {
            if (c.Floating[i] || c.Floating[j]) continue;
            var p = f.Section is not null && a.FrameProps.TryGetValue(f.Section, out var fp) ? fp : null;
            if (p?.Area is null || p.I33 is null) guessed++;
            var e = E(p?.Material);
            double A = p?.Area is > 0 ? p.Area.Value : defaultArea, I3 = p?.I33 is > 0 ? p.I33.Value : defaultI, I2 = p?.I22 is > 0 ? p.I22.Value : defaultI;
            var J = p?.Torsion is > 0 ? p.Torsion.Value : Math.Min(I2, I3);
            // Releases at the ends of the whole member only; the pieces between are joined fully.
            var releases = new bool[12];
            if (f.Releases.Length >= 12)
            {
                var fi = c.Index[f.I]; var fj = c.Index[f.J];
                if (i == fi) Array.Copy(f.Releases, 0, releases, 0, 6);
                if (j == fj) Array.Copy(f.Releases, 6, releases, 6, 6);
            }
            bars.Add(new StiffnessProbe.Bar(i, j, e * A, e * I2, e * I3, e / 2.4 * J, releases, f.Name));
        }
        if (guessed > 0) c.Notes.Add($"The stiffness check took typical sizes for {guessed} member piece(s) whose section the program did not give; only whether the model is stable is judged, not how stiff.");
        foreach (var (joints, s, p) in c.Plates)
        {
            if (joints.Any(k => c.Floating[k])) continue;
            var e = E(p.Material);
            var t = p.Thickness is > 0 ? p.Thickness.Value : typicalL / 30;
            var pts = joints.Select(c.Xyz).ToList();
            var w = Math.Sqrt(Math.Max(PolygonArea(pts), 1e-12));
            for (var x = 0; x < joints.Length; x++)
                for (var y = x + 1; y < joints.Length; y++)
                {
                    var membrane = p.Type != "Plate";
                    var bending = p.Type != "Membrane";
                    var ea = membrane ? e * t * w / 2 : 0;
                    var inPlane = membrane ? e * t * w * w * w / 24 : 0;
                    var outOfPlane = bending ? e * w * t * t * t / 12 : 0;
                    var gj = bending ? e / 2.4 * w * t * t * t / 3 : 0;
                    // Shell: in-plane bending of the tie gives the corner its drilling stiffness. Which local axis is
                    // in plane is not known here, so both bending stiffnesses take the larger of the two for shells.
                    double ei2 = p.Type == "Shell" ? Math.Max(inPlane, outOfPlane) : p.Type == "Plate" ? outOfPlane : 0;
                    double ei3 = p.Type == "Shell" ? Math.Max(inPlane, outOfPlane) : p.Type == "Plate" ? outOfPlane : 0;
                    var releases = p.Type == "Membrane" ? new bool[] { false, false, false, true, true, true, false, false, false, false, true, true } : new bool[12];
                    bars.Add(new StiffnessProbe.Bar(joints[x], joints[y], ea, ei2, ei3, p.Type == "Membrane" ? 0 : gj, releases, s.Name));
                }
        }

        // A joint no member with stiffness reaches (only a member with no section, say) is reported elsewhere: held here.
        var reached = new bool[n];
        foreach (var b in bars) reached[b.I] = reached[b.J] = true;
        for (var i = 0; i < n; i++) if (!reached[i]) fixedDof[i] = [true, true, true, true, true, true];
        var floors = new List<StiffnessProbe.Floor>();
        foreach (var g in Floors(c))
        {
            var live = g.Where(k => !c.Floating[k] && reached[k]).ToList();
            if (live.Count < 2) continue;
            floors.Add(new StiffnessProbe.Floor(live, live.Average(k => c.Xyz(k).X), live.Average(k => c.Xyz(k).Y)));
        }
        bool[] active = a.PlateOnly ? [false, false, true, true, true, false] : [true, true, true, true, true, true];
        StiffnessProbe.Result r;
        try { r = StiffnessProbe.Run(xyz, fixedDof, spring, bars, floors, active, o.MostEquations); }
        catch (OutOfMemoryException) { c.Notes.Add("The stiffness check needed more memory than the PC had; the other checks ran."); return (0, false); }
        if (!r.Ran) { c.Notes.Add($"The stiffness check was left out: {r.Why}. The other checks ran."); return (r.Equations, false); }
        if (r.IllConditioned) c.Notes.Add("The stiffness is badly conditioned in places (some pivots lost 7 or more digits): look for very stiff members next to very soft ones.");
        ReportFree(c, r, floors);
        return (r.Equations, true);
    }

    private static void ReportFree(Context c, StiffnessProbe.Result r, List<StiffnessProbe.Floor> floors)
    {
        var a = c.M;
        // Floors first: a whole level that can slide or turn.
        foreach (var g in r.Free.Where(f => f.Floor >= 0).GroupBy(f => f.Floor))
        {
            var floor = floors[g.Key];
            var any = c.J(floor.Joints[0]);
            var level = any.Story is { } st ? st : $"Z = {any.Z:0.###}";
            var how = string.Join(" and ", g.Select(f => f.Dof == 5 ? "turn in plan" : f.Dof == 0 ? "slide along X" : "slide along Y"));
            var objects = floor.Joints.SelectMany(k => c.At[k]).Distinct().ToList();
            c.Add(Level.Error, "mechanism", $"The rigid floor (diaphragm {any.Diaphragm}) at {level} can {how} with nothing resisting it: no column, wall or brace below or above takes it sideways.",
                "Give the level a lateral system: columns fixed at the base (not pinned at both ends), walls or braces in both directions, spread so they also resist turning. If the level is meant to be free, take the diaphragm off.",
                objects, floor.Joints.Select(c.Xyz));
        }
        // Then joints, one finding per joint with every direction it is free in.
        var byJoint = r.Free.Where(f => f.Joint >= 0).GroupBy(f => f.Joint).ToList();
        var listed = 0;
        foreach (var g in byJoint)
        {
            if (listed++ >= 60) break;
            var j = c.J(g.Key);
            var dofs = g.Select(f => f.Dof).Distinct().OrderBy(d => d).ToList();
            var objects = c.At[g.Key].Distinct().ToList();
            var frames = objects.Where(o => o.Kind == "frame").Select(o => a.Frames.First(f => f.Name == o.Name)).ToList();
            var pinned = frames.Count > 0 && frames.All(f => ReleasedAt(f, j.Name));
            string fix;
            if (dofs.All(d => d >= 3))
                fix = "A member that meets this joint is held only by its releases: take a moment or torsion release off one of them, or restrain the joint's rotation.";
            else if (pinned)
                fix = $"Every member at this joint is pinned there, so the joint and its members form a mechanism. Keep one of them fixed (take its moment releases off: {List(frames.Select(f => f.Label), 4)}), add a brace, or support the joint.";
            else if (objects.Count <= 1)
                fix = "Only one member holds this joint and it cannot hold it in this direction (a member pinned at both ends, a membrane edge). Connect the joint to the structure, support it, or change the member.";
            else
                fix = "The members around this joint form a mechanism in this direction: add a brace or a wall, fix a pinned end, or support the joint.";
            c.Add(Level.Error, "mechanism", $"{Show(j)} can move {string.Join(", ", dofs.Select(d => DofWords[d]))} ({string.Join(" ", dofs.Select(d => DofNames[d]))}) with nothing to stop it: the analysis would call the model unstable here.",
                fix, [Context.Ref(j), .. objects], [j.At]);
        }
        if (byJoint.Count > 60) c.Notes.Add($"{byJoint.Count} joints are free to move; the first 60 are listed. Fixing those often fixes the rest.");
    }

    private static bool ReleasedAt(Frame f, string joint) =>
        f.Releases.Length >= 12 && (f.I == joint ? f.Releases[4] && f.Releases[5] : f.J == joint && f.Releases[10] && f.Releases[11]);

    // ==============================================================================================================
    // Load cases, combinations and mass
    // ==============================================================================================================

    private static void LoadsAndCases(Context c)
    {
        var a = c.M;
        var patterns = a.Patterns.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cases = a.Cases.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combos = a.Combos.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (a.Patterns.Count == 0 && a.Cases.Count == 0)
            c.Add(Level.Error, "cases", "The model has no load patterns and no load cases: the analysis has nothing to run.",
                "Define the load patterns (Define > Load Patterns): Dead with self weight 1, Live, and the lateral ones.", [], []);
        else if (a.Cases.Count == 0 && a.Program == "sap2000")
            c.Notes.Add("No load cases were read; SAP2000 makes one per load pattern when it is defined.");

        foreach (var lc in a.Cases)
        {
            foreach (var p in lc.Patterns.Where(p => !patterns.Contains(p)))
                c.Add(Level.Error, "cases", $"Load case '{lc.Name}' applies load pattern '{p}', which is not defined.", $"Define '{p}', or take it out of '{lc.Name}' (Define > Load Cases).", [], []);
            foreach (var u in lc.Uses.Where(u => !cases.Contains(u)))
                c.Add(Level.Error, "cases", $"Load case '{lc.Name}' builds on case '{u}', which is not defined.", $"Define '{u}' (a modal case for a response spectrum), or choose a case that exists in '{lc.Name}'.", [], []);
            if (a.Functions is { } functions)
                foreach (var fn in lc.Functions.Where(fn => !functions.Contains(fn)))
                    c.Add(Level.Error, "cases", $"Load case '{lc.Name}' uses function '{fn}', which is not defined.", $"Define '{fn}' (Define > Functions), or choose a function that exists.", [], []);
            if (lc.Type.Contains("Spectrum", StringComparison.OrdinalIgnoreCase) && lc.Uses.Count == 0 && !a.Cases.Any(x => x.Type.Contains("Modal", StringComparison.OrdinalIgnoreCase)))
                c.Add(Level.Error, "cases", $"Response spectrum case '{lc.Name}' has no modal case to build on, and the model has none.", "Define a modal case (Define > Load Cases > Modal) and choose it in the response spectrum case.", [], []);
        }
        foreach (var combo in a.Combos)
            foreach (var item in combo.Items.Where(i => !cases.Contains(i) && !combos.Contains(i) && !patterns.Contains(i)))
                c.Add(Level.Error, "cases", $"Load combination '{combo.Name}' adds '{item}', which is neither a load case nor a combination.", $"Take '{item}' out of '{combo.Name}', or define it.", [], []);
        foreach (var combo in a.Combos.Where(x => x.Items.Count == 0))
            c.Add(Level.Warning, "cases", $"Load combination '{combo.Name}' adds nothing.", "Add its load cases and factors, or delete it.", [], []);
        foreach (var combo in a.Combos.Where(x => x.Items.Contains(x.Name, StringComparer.OrdinalIgnoreCase)))
            c.Add(Level.Error, "cases", $"Load combination '{combo.Name}' adds itself.", $"Take '{combo.Name}' out of its own list.", [], []);

        // Mass: a modal, spectrum or time history case needs some.
        var needing = a.Cases.Where(x => x.NeedsMass).Select(x => x.Name).ToList();
        if (needing.Count == 0 || a.Frames.Count + a.Shells.Count == 0) return;
        var source = a.Mass ?? (a.Program is "etabs" or "modeler" or "sap2000" ? new MassSource("(the program's default)", true, true, []) : null);
        if (source is null) { c.Notes.Add("The mass source was not read, so the mass for the modal cases was not checked."); return; }
        var weighty = a.Materials.Values.Where(m => m.Weight is > 0 || m.Mass is > 0).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materialsKnown = a.Materials.Values.Any(m => m.Weight is not null || m.Mass is not null);
        var elementMass = source.FromElements && (!materialsKnown
            || a.FrameProps.Values.Any(p => p.Material is { } m && weighty.Contains(m)) || a.ShellProps.Values.Any(p => p.Material is { } m && weighty.Contains(m)));
        var loadMass = source.Loads.Any(l => l.Factor > 0 && a.Patterns.FirstOrDefault(p => p.Name.Equals(l.Pattern, StringComparison.OrdinalIgnoreCase)) is { } p && (p.HasLoads || p.SelfWeight > 0));
        var addedMass = source.FromAddedMass && a.Mass is null;
        foreach (var l in source.Loads.Where(l => !patterns.Contains(l.Pattern)))
            c.Add(Level.Error, "mass", $"The mass source '{source.Name}' takes load pattern '{l.Pattern}', which is not defined.", "Define > Mass Source: choose load patterns that exist.", [], []);
        if (!elementMass && !loadMass && !addedMass)
            c.Add(Level.Error, "mass", $"The model has no mass, and {List(needing.Select(n => $"'{n}'"), 4)} need{(needing.Count == 1 ? "s" : "")} it: the modal analysis stops with no mass to work with.",
                source.FromElements ? "Give the materials their weight per volume, or take mass from the dead and superimposed dead loads (Define > Mass Source)."
                    : "Define > Mass Source: include the element self mass, or take mass from the dead and superimposed dead load patterns.", [], []);
    }

    // ==============================================================================================================
    // Geometry helpers
    // ==============================================================================================================

    private static double Distance(Xyz a, Xyz b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    private static Xyz Lerp(Xyz a, Xyz b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    private static (long, long, long) Cell(Xyz p, double size) => ((long)Math.Floor(p.X / size), (long)Math.Floor(p.Y / size), (long)Math.Floor(p.Z / size));

    private static IEnumerable<int> Near(Dictionary<(long, long, long), List<int>> cells, Xyz p, double size)
    {
        var (cx, cy, cz) = Cell(p, size);
        for (var x = cx - 1; x <= cx + 1; x++)
            for (var y = cy - 1; y <= cy + 1; y++)
                for (var z = cz - 1; z <= cz + 1; z++)
                    if (cells.TryGetValue((x, y, z), out var l)) foreach (var i in l) yield return i;
    }

    private static double CellSize(Context c)
    {
        var lengths = c.M.Frames.Where(f => c.Index.ContainsKey(f.I) && c.Index.ContainsKey(f.J)).Select(f => Distance(c.Xyz(c.Index[f.I]), c.Xyz(c.Index[f.J]))).Where(l => l > 0).OrderBy(l => l).ToList();
        var typical = lengths.Count > 0 ? lengths[lengths.Count / 2] : 1;
        return Math.Max(typical / 2, c.M.Tolerance * 10);
    }

    /// <summary>The joints within tolerance of a segment, strictly between its ends.</summary>
    private static IEnumerable<int> JointsOnSegment(Dictionary<(long, long, long), List<int>> cells, double size, Xyz a, Xyz b, double tol, Context c)
    {
        var (x0, y0, z0) = Cell(new Xyz(Math.Min(a.X, b.X) - tol, Math.Min(a.Y, b.Y) - tol, Math.Min(a.Z, b.Z) - tol), size);
        var (x1, y1, z1) = Cell(new Xyz(Math.Max(a.X, b.X) + tol, Math.Max(a.Y, b.Y) + tol, Math.Max(a.Z, b.Z) + tol), size);
        if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 20000) yield break;
        var len = Distance(a, b);
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                for (var z = z0; z <= z1; z++)
                {
                    if (!cells.TryGetValue((x, y, z), out var l)) continue;
                    foreach (var k in l)
                    {
                        var p = c.Xyz(k);
                        var t = Along(a, b, p) * len;
                        if (t <= tol || t >= len - tol) continue;
                        if (DistanceToLine(p, a, b) <= tol) yield return k;
                    }
                }
    }

    /// <summary>The joints within tolerance of a flat polygon's plane and inside it or on its edges.</summary>
    private static IEnumerable<int> JointsOnPolygon(Dictionary<(long, long, long), List<int>> cells, double size, List<Xyz> pts, double tol, Context c)
    {
        var (x0, y0, z0) = Cell(new Xyz(pts.Min(p => p.X) - tol, pts.Min(p => p.Y) - tol, pts.Min(p => p.Z) - tol), size);
        var (x1, y1, z1) = Cell(new Xyz(pts.Max(p => p.X) + tol, pts.Max(p => p.Y) + tol, pts.Max(p => p.Z) + tol), size);
        if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 20000) yield break;
        var normal = Normal(pts);
        var nl = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
        if (nl <= 0) yield break;
        normal = new Xyz(normal.X / nl, normal.Y / nl, normal.Z / nl);
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                for (var z = z0; z <= z1; z++)
                {
                    if (!cells.TryGetValue((x, y, z), out var l)) continue;
                    foreach (var k in l)
                    {
                        var p = c.Xyz(k);
                        var off = (p.X - pts[0].X) * normal.X + (p.Y - pts[0].Y) * normal.Y + (p.Z - pts[0].Z) * normal.Z;
                        if (Math.Abs(off) > tol) continue;
                        if (OnEdge(pts, p, tol) || Inside(pts, p, normal)) yield return k;
                    }
                }
    }

    private static bool OnEdge(List<Xyz> pts, Xyz p, double tol)
    {
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            var len = Distance(a, b);
            if (len <= 0) continue;
            var t = Along(a, b, p) * len;
            if (t >= -tol && t <= len + tol && DistanceToLine(p, a, b) <= tol) return true;
        }
        return false;
    }

    /// <summary>Inside a flat polygon: by its projection on the plane its normal points most along.</summary>
    private static bool Inside(List<Xyz> pts, Xyz p, Xyz normal)
    {
        var ax = Math.Abs(normal.X); var ay = Math.Abs(normal.Y); var az = Math.Abs(normal.Z);
        Func<Xyz, (double, double)> plane = az >= ax && az >= ay ? q => (q.X, q.Y) : ay >= ax ? q => (q.X, q.Z) : q => (q.Y, q.Z);
        var (px, py) = plane(p);
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            var (xi, yi) = plane(pts[i]); var (xj, yj) = plane(pts[j]);
            if (yi > py != yj > py && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    private static double Along(Xyz a, Xyz b, Xyz p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        var l2 = dx * dx + dy * dy + dz * dz;
        return l2 <= 0 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / l2;
    }

    private static double DistanceToLine(Xyz p, Xyz a, Xyz b)
    {
        var t = Along(a, b, p);
        return Distance(p, Lerp(a, b, t));
    }

    private static bool SameEnds(Xyz a1, Xyz b1, Xyz a2, Xyz b2, double tol) =>
        Distance(a1, a2) <= tol && Distance(b1, b2) <= tol || Distance(a1, b2) <= tol && Distance(b1, a2) <= tol;

    private static double Overlap(Xyz a1, Xyz b1, Xyz a2, Xyz b2)
    {
        var len = Distance(a1, b1);
        var t1 = Along(a1, b1, a2) * len; var t2 = Along(a1, b1, b2) * len;
        var lo = Math.Max(0, Math.Min(t1, t2)); var hi = Math.Min(len, Math.Max(t1, t2));
        return hi - lo;
    }

    /// <summary>A key shared by members along the same direction (either way), to compare only those.</summary>
    private static (long, long, long) DirectionKey(Xyz a, Xyz b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        var l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        dx /= l; dy /= l; dz /= l;
        if (dx < -1e-9 || Math.Abs(dx) <= 1e-9 && (dy < -1e-9 || Math.Abs(dy) <= 1e-9 && dz < 0)) { dx = -dx; dy = -dy; dz = -dz; }
        return ((long)Math.Round(dx * 1000), (long)Math.Round(dy * 1000), (long)Math.Round(dz * 1000));
    }

    private static Xyz Normal(List<Xyz> pts)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }
        return new Xyz(x, y, z);
    }

    private static double PolygonArea(List<Xyz> pts)
    {
        if (pts.Count < 3) return 0;
        var n = Normal(pts);
        return Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z) / 2;
    }

    /// <summary>How far the farthest corner is from the plane of the polygon (by its average normal).</summary>
    private static double Warp(List<Xyz> pts)
    {
        var n = Normal(pts);
        var l = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        if (l <= 0) return 0;
        var cx = pts.Average(p => p.X); var cy = pts.Average(p => p.Y); var cz = pts.Average(p => p.Z);
        return pts.Max(p => Math.Abs(((p.X - cx) * n.X + (p.Y - cy) * n.Y + (p.Z - cz) * n.Z) / l));
    }

    private static double Size(List<Xyz> pts) => pts.SelectMany(p => pts.Select(q => Distance(p, q))).Max();

    private static (double Shortest, double Longest) Sides(List<Xyz> pts)
    {
        var sides = pts.Select((p, i) => Distance(p, pts[(i + 1) % pts.Count])).Where(s => s > 0).ToList();
        return sides.Count == 0 ? (0, 0) : (sides.Min(), sides.Max());
    }
}
