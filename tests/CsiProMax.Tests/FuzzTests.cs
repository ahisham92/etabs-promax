using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

/// <summary>The Modeler's tools called with bent input never crash, never corrupt the model (ModelFuzz).</summary>
static class FuzzTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("robustness: every Modeler tool, 40 calls each with bent input, refuses in words or leaves a model that still saves and writes", () =>
        {
            var tools = HandModel.Tools.Values;
            // CPM_FUZZ_CALLS and CPM_FUZZ_SEED run it harder or otherwise (while hunting).
            var calls = int.TryParse(Environment.GetEnvironmentVariable("CPM_FUZZ_CALLS"), out var c) ? c : 40;
            var seed = int.TryParse(Environment.GetEnvironmentVariable("CPM_FUZZ_SEED"), out var sd) ? sd : 20261126;
            var findings = ModelFuzz.Run(() => new OfflineEtabs(Templates.ConcreteFrame("Fuzz", [6, 5], [4], 2, 3.2)), tools, calls, seed, TimeSpan.FromMinutes(4));
            Console.WriteLine($"      {tools.Count(ModelFuzz.Fuzzed)} tools, {calls} calls each");
            if (findings.Count > 0)
                throw new Exception($"{findings.Count} findings:\n" + string.Join("\n", findings.Take(40).Select(f => $"  {f.Tool} {f.Args} -> {f.What}")));
        });

        yield return ("robustness: Modeler tools picked at random, one after the other on one model, never crash it or leave it broken", () =>
        {
            var calls = int.TryParse(Environment.GetEnvironmentVariable("CPM_FUZZ_CALLS"), out var c) ? c * 10 : 1500;
            var seed = int.TryParse(Environment.GetEnvironmentVariable("CPM_FUZZ_SEED"), out var sd) ? sd : 20261126;
            var findings = ModelFuzz.RunMixed(() => new OfflineEtabs(Templates.ConcreteFrame("Fuzz", [6, 5], [4], 2, 3.2)), HandModel.Tools.Values, calls, seed, TimeSpan.FromMinutes(4));
            if (findings.Count > 0)
                throw new Exception($"{findings.Count} findings:\n" + string.Join("\n", findings.Take(40).Select(f => $"  {f.Tool} {f.Args} -> {f.What}")));
        });
    }
}
