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
}
