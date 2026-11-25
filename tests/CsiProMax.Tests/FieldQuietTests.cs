using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;

/// <summary>
/// A part of the field test in which nothing moves is given up on after a few quiet minutes, not at its full limit.
/// Seen on the field-test PC on 2026-11-25: one call to the scratch SAP2000 never came back while SAP2000 sat idle with
/// nothing on its screen, and the test would have waited the whole 45 minutes of that part.
/// </summary>
static class FieldQuietTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("field test: a part where nothing moves is given up on after the quiet time, and says why", () =>
        {
            var ctx = new FieldContext(CsiProgram.Sap2000, Path.GetTempPath(), new MemoryEtabs()) { Quiet = TimeSpan.FromSeconds(1) };
            using var hold = new ManualResetEventSlim();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var finished = ctx.Within(TimeSpan.FromMinutes(5), "SAP2000 on a scratch model", () =>
            {
                ctx.Step("tool run_analysis", () => { hold.Wait(TimeSpan.FromSeconds(30)); return "never"; });
            });
            hold.Set();
            Check(!finished, "the quiet part was not given up on");
            Check(clock.Elapsed < TimeSpan.FromSeconds(20), $"it took {clock.Elapsed.TotalSeconds:0} s, not about the quiet time");
            var failed = ctx.Steps.LastOrDefault(s => !s.Ok);
            Check(failed is not null && failed.Detail.Contains("Nothing moved in it", StringComparison.Ordinal) && failed.Detail.Contains("tool run_analysis", StringComparison.Ordinal),
                "the report does not say it went quiet, and where: " + failed?.Detail);
        });

        yield return ("field test: a part that keeps moving is not given up on for being long", () =>
        {
            var ctx = new FieldContext(CsiProgram.Sap2000, Path.GetTempPath(), new MemoryEtabs()) { Quiet = TimeSpan.FromSeconds(1) };
            var finished = ctx.Within(TimeSpan.FromMinutes(1), "SAP2000 on a scratch model", () =>
            {
                for (var i = 0; i < 10; i++) ctx.Step($"step {i}", () => { Thread.Sleep(300); return i; });
            });
            Check(finished, "a part with a step every 0.3 s was given up on");
            Check(ctx.Steps.Count(s => s.Ok && s.Name.StartsWith("step ", StringComparison.Ordinal)) == 10, "not every step was recorded");
        });
    }
}
