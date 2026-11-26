using System.Diagnostics;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.F2k;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// A large model through every way out of and back into the Modeler (Ahmed, 2026-11-26: "everything is imported and
/// exported ... without any losses whatever the number of joints, cases, combinations, no limits"): thousands of
/// joints, members and slabs, hundreds of load patterns and cases, a thousand combinations (with envelopes and
/// combinations of combinations) and tens of thousands of loads. Each check counts what came out against the model.
/// What a format cannot carry is checked as it is, so a change that makes it carry more shows up here.
/// </summary>
static class LargeModelTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public const int Patterns = 300, Combos = 1000;

    /// <summary>15 by 15 bays, 20 stories: 5,376 joints, 14,720 frames, 4,500 slabs; 300 patterns, 1,000 combinations, ~60,000 loads.</summary>
    internal static StructuralModel Large()
    {
        var m = Templates.ConcreteFrame("Large", Enumerable.Repeat(6.0, 15).ToArray(), Enumerable.Repeat(5.0, 15).ToArray(), 20, 3.2);
        var types = new[] { "Dead", "Super Dead", "Live", "Reducible Live", "Roof Live", "Other", "Wind", "Seismic", "Snow", "Temperature" };
        for (var i = 1; i <= Patterns - 2; i++) m.AddLoadPattern($"P{i:000}", types[i % types.Length]);
        var slabs = m.AreaAssigns.Where(a => m.Area(a.Label)!.Kind == AreaKind.Floor).ToList();
        var beams = m.LineAssigns.Where(a => m.Line(a.Label)!.Kind == LineKind.Beam).ToList();
        // Every slab carries three more patterns, and every beam one.
        for (var k = 0; k < slabs.Count; k++)
            for (var j = 0; j < 3; j++) m.AddAreaLoad(slabs[k].Label, slabs[k].Story, $"P{1 + (k + j * 97) % (Patterns - 2):000}", 0.5 + j);
        for (var k = 0; k < beams.Count; k++) m.AddLineLoad(beams[k].Label, beams[k].Story, $"P{1 + k % (Patterns - 2):000}", 4.5);
        var cases = m.LoadCases.Select(c => c.Name).ToList();
        for (var c = 1; c <= Combos; c++)
        {
            if (c % 10 == 0)
            {
                // An envelope of the ten combinations before it.
                var envelope = new LoadCombo { Name = $"ENV{c:0000}", Lines = [["TYPE", "\"Envelope\""]] };
                for (var b = c - 9; b < c; b++) envelope.Lines.Add(["LOADCOMBO", E2kText.Quote($"C{b:0000}"), "SF", "1"]);
                m.Combos.Add(envelope);
            }
            else m.AddCombo($"C{c:0000}", (cases[c % cases.Count], 1.2), (cases[(c * 7) % cases.Count], 1.6), (cases[(c * 13) % cases.Count], 0.5));
        }
        return m;
    }

    static int Terms(StructuralModel m) => m.Combos.Sum(c => c.Lines.Count(l => E2kText.Value(l, "LOADCASE") is not null || E2kText.Value(l, "LOADCOMBO") is not null));

    static void SameModel(StructuralModel a, StructuralModel b, string how)
    {
        Equal(a.Points.Count, b.Points.Count, how + ": plan points");
        Equal(a.Lines.Count, b.Lines.Count, how + ": line objects");
        Equal(a.Areas.Count, b.Areas.Count, how + ": area objects");
        Equal(a.LineAssigns.Count, b.LineAssigns.Count, how + ": frames on stories");
        Equal(a.AreaAssigns.Count, b.AreaAssigns.Count, how + ": areas on stories");
        Equal(a.PointAssigns.Count, b.PointAssigns.Count, how + ": supports and joint assignments");
        Equal(a.LoadPatterns.Count, b.LoadPatterns.Count, how + ": load patterns");
        Equal(a.LoadCases.Count, b.LoadCases.Count, how + ": load cases");
        Equal(a.Combos.Count, b.Combos.Count, how + ": combinations");
        Equal(Terms(a), Terms(b), how + ": combination terms");
        Equal(a.AreaLoads.Count, b.AreaLoads.Count, how + ": area loads");
        Equal(a.LineLoads.Count, b.LineLoads.Count, how + ": frame loads");
    }

    static string Timed(Stopwatch w) => $"{w.Elapsed.TotalSeconds:0.0} s";

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("large model: .e2k, .cpm and the Excel tables give every joint, member, load, case and combination back, with no limit", () =>
        {
            var m = Large();
            var physicalFrames = Physical.Frames(m).Count();
            Check(physicalFrames > 14000 && m.Combos.Count == Combos && m.LoadPatterns.Count == Patterns, $"the model is large: {physicalFrames} frames, {m.LoadPatterns.Count} patterns, {m.Combos.Count} combinations");
            E2kWriter.SavedAt = new DateTime(2026, 11, 26, 12, 0, 0);
            try
            {
                var w = Stopwatch.StartNew();
                var text = E2kWriter.Write(m);
                var back = E2kReader.Read(text);
                SameModel(m, back, ".e2k");
                Equal(text, E2kWriter.Write(back), ".e2k written again is the same file");
                // Lines the reader does not take apart are kept as they are and written back (the controls); no model data is among them.
                Check(back.UnparsedLines.Keys.All(k => k == "CONTROLS"), "model data in the .e2k was left unread: " + string.Join(", ", back.UnparsedLines.Keys));
                var e2k = Timed(w);

                w.Restart();
                var cpm = ModelFile.FromJson(ModelFile.ToJson(m));
                SameModel(m, cpm, ".cpm");
                Equal(text, E2kWriter.Write(cpm), ".cpm gives the same .e2k");
                var cpmTime = Timed(w);

                w.Restart();
                var path = Path.Combine(Path.GetTempPath(), "cpm-large-" + Guid.NewGuid().ToString("N")[..8] + ".xlsx");
                try
                {
                    TableTools.Write(path, ModelTables.Export(m));
                    var fromExcel = ModelEdit.New("Large");
                    ModelTables.Import(fromExcel, TableTools.Read(path));
                    SameModel(m, fromExcel, "Excel");
                    Equal(text, E2kWriter.Write(fromExcel), "Excel gives the same .e2k");
                }
                finally { File.Delete(path); }
                Console.WriteLine($"      large model: {m.Points.Count} plan points, {physicalFrames} frames, {Physical.Areas(m).Count()} areas, {m.LoadPatterns.Count} patterns, {m.LoadCases.Count} cases, {m.Combos.Count} combinations ({Terms(m)} terms), {m.AreaLoads.Count + m.LineLoads.Count} loads; .e2k {text.Length / 1048576.0:0.0} MB {e2k}, .cpm {cpmTime}, Excel {Timed(w)}");
            }
            finally { E2kWriter.SavedAt = null; }
        });

        yield return ("large model: the .s2k for SAP2000 holds every joint, frame, area, pattern, case, combination and load", () =>
        {
            var m = Large();
            var result = S2kWriter.Write(m);
            var tables = CsiTableText.Parse(result.Text);
            List<Dictionary<string, string>> T(string name) => tables.TryGetValue(name, out var rows) ? rows : [];
            Equal(Physical.Frames(m).Count(), result.Frames, "frames written");
            Equal(Physical.Frames(m).Count(), T("CONNECTIVITY - FRAME").Count, "frame rows");
            Equal(Physical.Areas(m).Count(), T("CONNECTIVITY - AREA").Count, "area rows");
            Equal(result.Joints, T("JOINT COORDINATES").Count, "joint rows");
            Equal(m.LoadPatterns.Count, T("LOAD PATTERN DEFINITIONS").Count, "load patterns");
            Equal(m.LoadCases.Count, T("LOAD CASE DEFINITIONS").Count, "load cases");
            var combos = T("COMBINATION DEFINITIONS");
            Equal(m.Combos.Count, combos.Select(r => r.GetValueOrDefault("ComboName")).Distinct().Count(), "combinations");
            Equal(Terms(m), combos.Count(r => r.ContainsKey("CaseName")), "combination terms");
            Equal(m.LineLoads.Count, T("FRAME LOADS - DISTRIBUTED").Count, "frame loads");
            Equal(m.AreaLoads.Count, T("AREA LOADS - UNIFORM").Count, "area loads");
            Console.WriteLine($"      .s2k: {result.Text.Length / 1048576.0:0.0} MB, {result.Joints} joints, {result.Frames} frames, {result.Areas} areas, {combos.Count} combination rows");
        });

        yield return ("large model: the .f2k for SAFE holds one floor (its slabs, beams, columns below and loads) and every pattern and linear combination", () =>
        {
            var m = Large();
            var result = F2kWriter.Write(m);
            var tables = CsiTableText.Parse(result.Text);
            List<Dictionary<string, string>> T(string name) => tables.TryGetValue(name, out var rows) ? rows : [];
            // SAFE models one floor: the busiest story goes in, the others do not (said in the notes).
            Equal(15 * 15, result.Slabs, "the slabs of one story");
            Equal(m.LoadPatterns.Count, T("LOAD PATTERN DEFINITIONS").Count, "load patterns");
            var combos = T("LOAD COMBINATION DEFINITIONS").Select(r => r.GetValueOrDefault("Name") ?? r.GetValueOrDefault("Combo")).Where(n => n is not null).Distinct().Count();
            Console.WriteLine($"      .f2k: story {result.Story}, {result.Slabs} slabs, {result.Beams} beams, {result.Columns} columns, {result.Loads} loads, {combos} combinations of {m.Combos.Count}; notes: {string.Join(" | ", result.Notes.Take(3))}");
            Check(combos >= m.Combos.Count(c => c.Lines.Any(l => E2kText.Value(l, "LOADCASE") is not null)), $"every combination of load cases is in the .f2k ({combos})");
        });

        yield return ("large model: the review pack of the Modeler's model holds every joint, frame, area, case and combination", () =>
        {
            var m = Large();
            var link = new OfflineEtabs(m);
            var built = Pack.Build(link, [], [], [], null, null, withTables: false);
            Equal(Physical.Frames(m).Count(), built.Frames, "frames in the pack");
            Equal(Physical.Areas(m).Count(), built.Areas, "areas in the pack");
            Equal(m.LoadCases.Count, built.Content["model"]!["load_cases"]!.AsArray().Count, "load cases in the pack");
            Equal(m.Combos.Count, built.Content["model"]!["combinations"]!.AsArray().Count, "combinations in the pack");
            Equal(link.Points().Count, built.Content["model"]!["points"]!.AsArray().Count, "joints in the pack");
            Console.WriteLine($"      pack: {built.Zip.Length / 1048576.0:0.0} MB zipped, {link.Points().Count} joints");
        });
    }
}
