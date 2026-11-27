using CsiProMax.Checks.Stability;
using CsiProMax.Model;

/// <summary>
/// The checker that finds what stops an analysis from completing (StabilityCheck), on models broken on purpose. Ahmed
/// 2026-11-25: find the points that do not let the model complete its run, inside CSI ProMax, instead of running the
/// analysis to get the issue.
/// </summary>
static class StabilityTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Text(StabilityCheck.Report r) => string.Join(" | ", r.Findings.Select(f => $"{f.Level} {f.Check}: {f.Message}"));

    public static IEnumerable<(string Name, Action Body)> All()
    {
        foreach (var c in BrokenModels.All)
            yield return ($"stability: {c.Name} ({c.What})", () =>
            {
                var report = StabilityCheck.Run(FromStructural.Read(c.Build(), c.Program));
                if (c.Check.Length == 0)
                {
                    Check(report.Errors == 0, "the sound model has errors: " + Text(report));
                    Check(report.StiffnessRan, "the stiffness check ran: " + string.Join(" ", report.Notes));
                    return;
                }
                var hit = report.Findings.Where(f => f.Check == c.Check).ToList();
                Check(hit.Count > 0, $"no '{c.Check}' finding: " + Text(report));
                if (c.Object.Length > 0)
                    Check(hit.Any(f => f.Objects.Any(o => o.Name.Split('@')[0] == c.Object) || f.Message.Contains(c.Object)), $"'{c.Object}' is not named: " + Text(report));
                Check(hit.All(f => f.Fix.Length > 0), "every finding has a fix");
            });

        yield return ("stability: a column on a beam's span is joined in ETABS (no finding), not in SAP2000", () =>
        {
            var m = BrokenModels.Frame();
            m.AddColumn(3, 0, "Story2", "C500x500");
            var etabs = StabilityCheck.Run(FromStructural.Read(m, "etabs"));
            Check(etabs.Errors == 0 && !etabs.Findings.Any(f => f.Check == "connectivity"), "ETABS joins it: " + Text(etabs));
            var sap = StabilityCheck.Run(FromStructural.Read(m, "sap2000"));
            Check(sap.Findings.Any(f => f.Check == "connectivity"), "SAP2000 does not: " + Text(sap));
        });

        yield return ("stability: a pinned frame is a mechanism, the same frame with fixed bases is not", () =>
        {
            var m = BrokenModels.Frame(slabs: false, pinnedBase: false);
            Check(StabilityCheck.Run(FromStructural.Read(m)).Errors == 0, "a bare fixed frame is stable: " + Text(StabilityCheck.Run(FromStructural.Read(m))));
            var pinned = BrokenModels.Frame(slabs: false, pinnedBase: true);
            var r = StabilityCheck.Run(FromStructural.Read(pinned));
            Check(r.Errors == 0, "pinned bases with fixed beams still make a portal frame: " + Text(r));
        });

        yield return ("stability: a floor mechanism names the diaphragm and its story", () =>
        {
            var c = BrokenModels.All.First(x => x.Name == "floor-no-lateral");
            var r = StabilityCheck.Run(FromStructural.Read(c.Build()));
            Check(r.Findings.Any(f => f.Check == "mechanism" && f.Message.Contains("D1") && f.At.Count > 0), Text(r));
        });

        yield return ("stability: the stiffness check takes a large model in good time", () =>
        {
            var m = Templates.ConcreteFrame("Big", Enumerable.Repeat(6.0, 8).ToArray(), Enumerable.Repeat(6.0, 8).ToArray(), 12, 3);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = StabilityCheck.Run(FromStructural.Read(m));
            Check(r.StiffnessRan, string.Join(" ", r.Notes));
            Check(r.Errors == 0, Text(r));
            Check(clock.Elapsed.TotalSeconds < 20, $"took {clock.Elapsed.TotalSeconds:0.0} s");
        });

        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.e2k"))
            yield return ($"stability: ETABS's own file {Path.GetFileName(file)} has no errors", () =>
            {
                var r = StabilityCheck.Run(FromStructural.Read(CsiProMax.E2k.E2kReader.Read(File.ReadAllText(file)), "etabs"));
                Check(r.Errors == 0, Text(r));
            });

        // Ahmed 2026-11-27: his real ETABS model ran with no instability, and the check called point 1983 (a corner of
        // floor F11, a Shell-Thin slab two of whose corners are a hair apart) free in UZ, plus 40 sliver warnings.
        yield return ("stability: a slab with two corners a hair apart is not a mechanism, and ETABS meshes it (no sliver warning)", () =>
        {
            // F11: from the bay's edge out 10 m, with one corner 1.2 mm to 2 mm from another (the sliver's short side). Before
            // the fix the tie between those two corners was made as stiff as the whole slab and the check lost every
            // digit there: "E2 can move up and down (Z)".
            foreach (var gap in new[] { 0.0012, 0.002 })
                foreach (var diaphragm in new[] { true, false })
                {
                    var a = Bay("etabs");
                    foreach (var (n, x, y) in new[] { ("E1", 16.0, 6.0), ("E2", 16.0, gap), ("E3", 16.0, 0.0) })
                        a.Joints.Add(new Joint(n, n, "Story1", x, y, 3, new bool[6], Diaphragm: diaphragm ? "D1" : null));
                    a.Shells.Add(new Shell("F11", "F11", "Story1", "floor", ["J2", "J4", "E1", "E2", "E3"], "S250", Diaphragm: diaphragm ? "D1" : null));
                    var r = StabilityCheck.Run(a);
                    Check(r.StiffnessRan, string.Join(" ", r.Notes));
                    Check(r.Errors == 0, $"gap {gap}, diaphragm {diaphragm}: no error: " + Text(r));
                    Check(!r.Findings.Any(f => f.Message.Contains("sliver")), "ETABS meshes the floor, no sliver: " + Text(r));
                    // SAP2000 analyses the area as one element: the sliver is worth a warning there, still no error.
                    a.Program = "sap2000"; a.JoinsAtIntersections = false;
                    var sap = StabilityCheck.Run(a);
                    Check(sap.Errors == 0, "SAP2000: no error: " + Text(sap));
                    Check(sap.Findings.Any(f => f.Message.Contains("sliver") && f.Level == Level.Warning), "SAP2000 warns of the sliver: " + Text(sap));
                }
        });

        yield return ("stability: a model solved in the XZ plane only (active DOF UX UZ RY) is not free along Y", () =>
        {
            // ETABS's Verification "Example 15a7": one wall in the XZ plane under a rigid floor, analysed as a plane model.
            var a = new AnalysisModel { Program = "etabs" };
            a.Materials["C"] = new MaterialProp("C", 3e7, 25);
            a.ShellProps["W"] = new ShellProp("W", "C", 0.3, "Membrane", "Wall");
            a.RigidDiaphragms.Add("D1");
            a.Joints.Add(new Joint("1", "1", "Base", 0, 0, 0, [true, true, true, true, true, true]));
            a.Joints.Add(new Joint("2", "2", "Base", 6, 0, 0, [true, true, true, true, true, true]));
            a.Joints.Add(new Joint("3", "3", "1ST", 6, 0, 3, new bool[6], Diaphragm: "D1"));
            a.Joints.Add(new Joint("4", "4", "1ST", 0, 0, 3, new bool[6], Diaphragm: "D1"));
            a.Shells.Add(new Shell("W1", "W1", "1ST", "wall", ["1", "2", "3", "4"], "W"));
            a.Patterns.Add(new Pattern("Dead", "Dead", 1, true));
            a.Cases.Add(new Case("Dead", "Linear Static", ["Dead"], [], []));
            var all = StabilityCheck.Run(a);
            Check(all.Findings.Any(f => f.Check == "mechanism"), "in 3D the floor is free along Y: " + Text(all));
            a.ActiveDof = [true, false, true, false, true, false];
            var plane = StabilityCheck.Run(a);
            Check(plane.Errors == 0, "in the XZ plane nothing is free: " + Text(plane));
        });

        yield return ("stability: ACTIVEDOF of an .e2k is read", () =>
        {
            var file = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "etabs23-default.e2k")).Replace("ACTIVEDOF \"UX UY UZ RX RY RZ\"", "ACTIVEDOF \"UX UZ RY\"");
            var a = FromStructural.Read(CsiProMax.E2k.E2kReader.Read(file), "etabs");
            Check(a.ActiveDof is [true, false, true, false, true, false], "read: " + string.Join(",", a.ActiveDof ?? []));
        });

        yield return ("stability: a membrane floor's corner off the beams is held in ETABS and SAFE, reported in SAP2000", () =>
        {
            var a = Bay("etabs");
            a.ShellProps["Deck"] = new ShellProp("Deck", "C35", 0.15, "Membrane");
            AddJoint(a, "E1", 8, 3, 3);
            a.Shells.Add(new Shell("F12", "F12", "Story1", "floor", ["J2", "E1", "J4"], "Deck", Diaphragm: "D1"));
            var r = StabilityCheck.Run(a);
            Check(r.Errors == 0, "ETABS carries a membrane's load to its beams: " + Text(r));
            a.Program = "sap2000"; a.JoinsAtIntersections = false;
            var sap = StabilityCheck.Run(a);
            Check(sap.Findings.Any(f => f.Check == "mechanism" && f.Objects.Any(o => o.Name == "E1")), "SAP2000 has the joint free in UZ: " + Text(sap));
        });

        yield return ("stability: what a link joins is not floating, and a one-joint link holds its joint", () =>
        {
            var a = Bay("etabs");
            // A beam hung off the bay by a link at one end and a one-joint link (to the ground) at the other.
            a.Joints.Add(new Joint("K1", "K1", "Story1", 0, -2, 3, new bool[6]));
            a.Joints.Add(new Joint("K2", "K2", "Story1", 6, -2, 3, new bool[6]));
            a.Frames.Add(new Frame("BX", "BX", "Story1", "beam", "K1", "K2", "B300", new bool[12]));
            var before = StabilityCheck.Run(a);
            Check(before.Findings.Any(f => f.Check == "floating"), "without the links it floats: " + Text(before));
            a.Links.Add(new Link("L1", "J1", "K1"));
            a.Links.Add(new Link("L2", "K2", null));
            var r = StabilityCheck.Run(a);
            Check(r.Errors == 0, "the links join it: " + Text(r));
        });

        yield return ("stability: torsion released at both ends of a beam the slab meshes along its length is no error in ETABS", () =>
        {
            var a = Bay("etabs");
            AddJoint(a, "M", 3, 0, 3);
            a.Shells.Add(new Shell("F13", "F13", "Story1", "floor", ["J1", "M", "J2", "J4", "J3"], "S250", Diaphragm: "D1"));
            var released = new bool[12]; released[3] = released[9] = true;
            var b = a.Frames.First(f => f.Name == "B1");
            a.Frames[a.Frames.IndexOf(b)] = b with { Releases = released };
            var r = StabilityCheck.Run(a);
            Check(!r.Findings.Any(f => f.Check == "releases"), "the slab holds it along its length: " + Text(r));
            // The same beam on its own (no joint along it) is still found.
            a.Shells.RemoveAll(s => s.Name == "F13");
            var bare = StabilityCheck.Run(a);
            Check(bare.Findings.Any(f => f.Check == "releases"), "a beam held only at its ends is: " + Text(bare));
        });

        yield return ("stability: the 12x12 member stiffness is symmetric and a free member has its six rigid motions", () =>
        {
            var bar = new StiffnessProbe.Bar(0, 1, 1e6, 1e4, 2e4, 5e3, new bool[12], "x");
            var k = StiffnessProbe.BarStiffness((0, 0, 0), (3, 4, 5), bar)!;
            for (var i = 0; i < 12; i++) for (var j = 0; j < 12; j++) Check(Math.Abs(k[i, j] - k[j, i]) < 1e-6 * (Math.Abs(k[i, j]) + 1), "symmetric");
            // Free in space: 6 free directions found.
            var r = StiffnessProbe.Run([(0, 0, 0), (3, 4, 5)], [new bool[6], new bool[6]], [new bool[6], new bool[6]], [bar], [], [true, true, true, true, true, true]);
            Check(r.Free.Count == 6, $"6 rigid motions, found {r.Free.Count}");
            // Fixed at one end: none.
            var held = StiffnessProbe.Run([(0, 0, 0), (3, 4, 5)], [[true, true, true, true, true, true], new bool[6]], [new bool[6], new bool[6]], [bar], [], [true, true, true, true, true, true]);
            Check(held.Free.Count == 0, $"a cantilever is stable, found {held.Free.Count}");
        });
    }

    static void AddJoint(AnalysisModel a, string name, double x, double y, double z, bool fixedBase = false) =>
        a.Joints.Add(new Joint(name, name, z == 0 ? "Base" : "Story1", x, y, z, fixedBase ? [true, true, true, true, true, true] : new bool[6], Diaphragm: z > 0 ? "D1" : null));

    /// <summary>One 6 x 6 m bay, 3 m high: four fixed columns, four beams (B1 along y = 0), a rigid diaphragm, a dead and a modal case.</summary>
    static AnalysisModel Bay(string program)
    {
        var a = new AnalysisModel { Program = program, JoinsAtIntersections = program != "sap2000" };
        a.Materials["C35"] = new MaterialProp("C35", 3e7, 25);
        a.FrameProps["C500"] = new FrameProp("C500", "C35", 0.25, 5.2e-3, 5.2e-3, 8.8e-3);
        a.FrameProps["B300"] = new FrameProp("B300", "C35", 0.18, 1.35e-3, 5.4e-3, 3.7e-3);
        a.ShellProps["S250"] = new ShellProp("S250", "C35", 0.25);
        a.RigidDiaphragms.Add("D1");
        (double X, double Y)[] at = [(0, 0), (6, 0), (0, 6), (6, 6)];
        for (var k = 0; k < 4; k++)
        {
            AddJoint(a, $"G{k + 1}", at[k].X, at[k].Y, 0, fixedBase: true);
            AddJoint(a, $"J{k + 1}", at[k].X, at[k].Y, 3);
            a.Frames.Add(new Frame($"C{k + 1}", $"C{k + 1}", "Story1", "column", $"G{k + 1}", $"J{k + 1}", "C500", new bool[12]));
        }
        a.Frames.Add(new Frame("B1", "B1", "Story1", "beam", "J1", "J2", "B300", new bool[12]));
        a.Frames.Add(new Frame("B2", "B2", "Story1", "beam", "J3", "J4", "B300", new bool[12]));
        a.Frames.Add(new Frame("B3", "B3", "Story1", "beam", "J1", "J3", "B300", new bool[12]));
        a.Frames.Add(new Frame("B4", "B4", "Story1", "beam", "J2", "J4", "B300", new bool[12]));
        a.Patterns.Add(new Pattern("Dead", "Dead", 1, true));
        a.Cases.Add(new Case("Dead", "Linear Static", ["Dead"], [], []));
        a.Cases.Add(new Case("Modal", "Modal", [], [], []));
        return a;
    }
}
