using System.Diagnostics;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The review pack in minutes (Ahmed 2026-11-27: "a minute or two or 3 maximum", not 30 to 60 minutes locking the PC).
/// Each program's part writes its pack with the checks in one read session and fails when it takes more than
/// <see cref="Pack.MostTime"/>; then it measures the program's ways of giving a table (one table at a time as an
/// array, as CSV or XML text, and, in ETABS, every table at once into a file) so the fastest way is known from the
/// program itself.
/// </summary>
public static class PackSpeedField
{
    /// <summary>The checks and the pack, read in one session: what the step returns, and the time check after it.</summary>
    public static (JsonObject Checks, Pack.Built Pack) Build(ICsiLink link, Func<JsonObject> checks, Func<JsonObject, Pack.Built> build)
    {
        using var session = PackSession.Start(link);
        var c = checks();
        return (c, build(c));
    }

    /// <summary>Fails when the pack took longer than <see cref="Pack.MostTime"/>; names the slowest tables either way.</summary>
    public static void Timed(FieldContext ctx, Pack.Built? pack, TimeSpan took, string program)
    {
        if (pack is null) return;
        ctx.Step($"the {program} review pack is read and written within {Pack.MostTime.TotalMinutes:0} minutes", () =>
        {
            var timing = pack.Content["manifest"]?["timing"];
            FieldContext.Expect(took <= Pack.MostTime,
                $"the checks and the pack took {took.TotalSeconds:0} s, more than {Pack.MostTime.TotalSeconds:0} s. Slowest: {timing?["slowest"]?.ToJsonString()}; steps: {timing?["steps_ms"]?.ToJsonString()}");
            return new { seconds = Math.Round(took.TotalSeconds, 1), timing = timing?.ToJsonString() };
        });
    }

    /// <summary>
    /// The pack as the program writes it by itself after a run: read from a copy of the saved scratch model in a hidden
    /// program of its own, which is closed after (Ahmed 2026-11-27: read, convert and load the results and close the
    /// program, within 3 minutes). Fails when the copy could not be read or the whole round took longer than
    /// <see cref="Pack.MostTime"/>. Gives the pack back, or null.
    /// </summary>
    public static Pack.Built? FromCopy(FieldContext ctx, ICsiLink link, string? name = null)
    {
        if (!FieldTest.IsLive(link)) return null;
        Pack.Built? got = null;
        ctx.Step(name ?? $"the review pack is read from a copy of the scratch model in a hidden {link.Program.Name}, which is closed after, within {Pack.MostTime.TotalMinutes:0} minutes", () =>
        {
            var file = link.Info().File;
            FieldContext.Expect(file.Length > 0 && File.Exists(file), $"the scratch model has no saved file to copy ({file})");
            var copy = Packs.PackCopy.Read(link.Program, file, from =>
            {
                using var session = PackSession.Start(from);
                var checks = ProgramTools.Report(System.Text.Json.JsonDocument.Parse("{}").RootElement, from).AsObject();
                return Pack.Build(from, [], from.LoadCases(), from.Combos(), checks, null, by: "CSI ProMax field test, from a copy", fileAs: file);
            });
            FieldContext.Expect(copy.Value is not null, $"the pack was not read from a copy: {copy.Said}");
            got = copy.Value!;
            var timing = got.Content["manifest"]?["timing"];
            FieldContext.Expect(copy.Took <= Pack.MostTime,
                $"copying, opening in a hidden {link.Program.Name}, reading the pack and closing took {copy.Took.TotalSeconds:0} s, more than {Pack.MostTime.TotalSeconds:0} s (reading alone {timing?["seconds"]} s). Slowest: {timing?["slowest"]?.ToJsonString()}");
            var cut = Cut(got.Content);
            FieldContext.Expect(cut.Count == 0, "the pack is not whole: " + string.Join(", ", cut));
            var target = Path.Combine(ctx.Folder, ModelerFieldSteps.ModelName + $" {link.Program.Name} from a copy.cpmx");
            File.WriteAllBytes(target, got.Zip);
            return new { seconds = Math.Round(copy.Took.TotalSeconds, 1), reading = timing?["seconds"]?.ToJsonString(), file = Path.GetFileName(target), frames = got.Frames, areas = got.Areas, tables = got.Tables.Count, notes = got.Notes };
        });
        return got;
    }

    /// <summary>The tables a pack cut short (lossless: none may be).</summary>
    internal static List<string> Cut(JsonObject content) =>
        (content["tables"] as JsonObject ?? []).Where(t => t.Value?["left_out"] is not null).Select(t => $"{t.Key} ({t.Value!["left_out"]} rows left out)").ToList();

    /// <summary>The large model's size per program: bays each way and stories (SAFE takes one floor, so a wider one).</summary>
    internal static (int Bays, int Stories) LargeSize(CsiProgram program) => program.Key == CsiProgram.Safe.Key ? (20, 1) : (10, 15);

    /// <summary>The large model the round is timed on: a concrete frame of <see cref="LargeSize"/>, for <paramref name="program"/>.</summary>
    internal static OfflineEtabs LargeModel(CsiProgram program, string folder)
    {
        var (bays, stories) = LargeSize(program);
        var spans = Enumerable.Repeat(6.0, bays).ToArray();
        var model = new OfflineEtabs(CsiProMax.Model.Templates.ConcreteFrame("cpm-large-pack", spans, spans, stories, 3.2), "");
        model.Model.Target = program.Key;
        model.SaveAs(Path.Combine(folder, "large", "cpm-large-pack.e2k"));
        return model;
    }

    /// <summary>
    /// Ahmed 2026-11-27: "however how big it is it needs not to exceed 2-3 minutes". The Engine's Run on a large model
    /// (thousands of frames and slabs): the program starts hidden, analyses, every result comes back and the program
    /// is closed. Reading the results, writing the pack and closing the program must take no more than
    /// <see cref="Pack.LargeMostTime"/> (Ahmed: more than 3 minutes for such a model "is fine but not much"); the analysis itself is the solver's and is reported beside it.
    /// </summary>
    public static void Large(FieldContext ctx, CsiProgram program)
    {
        var name = $"a large model in {program.Name}: its results are read, the pack written and {program.Name} closed within {Pack.LargeMostTime.TotalMinutes:0} minutes";
        if (ctx.NoPack) { ctx.LeftOutOfRerun(name); return; }
        ctx.Step(name, () =>
        {
            var model = LargeModel(program, ctx.Folder);
            var frames = model.Frames().Count;
            var areas = model.Areas().Count;
            var started = EngineRun.Start(model, analyse: true, kinds: [], keepOpen: false);
            FieldContext.Expect(started["ok"]?.GetValue<bool>() == true, "the run did not start: " + started["error"]);
            var until = DateTime.UtcNow.AddMinutes(40);
            while (EngineRun.Status(false)["done"]?.GetValue<bool>() != true && DateTime.UtcNow < until) Thread.Sleep(2000);
            var status = EngineRun.Status(true);
            FieldContext.Expect(status["done"]?.GetValue<bool>() == true, "the run did not finish within 40 minutes: " + status["phase"]);
            FieldContext.Expect(string.IsNullOrEmpty(status["error"]?.GetValue<string>()), "the run stopped: " + status["error"]);
            var steps = (status["monitor"]?["steps"] as JsonArray ?? []).Where(x => x is not null).ToDictionary(x => x!["key"]!.GetValue<string>(), x => x!["seconds"]?.GetValue<double>() ?? 0);
            double At(string key) => steps.GetValueOrDefault(key);
            var pack = status["pack"] as JsonObject;
            FieldContext.Expect(pack is not null, "no results came back to the model");
            var w = Stopwatch.StartNew();
            var text = pack!.ToJsonString();
            var load = w.Elapsed.TotalSeconds;
            var tables = (pack["tables"] as JsonObject)?.Count ?? 0;
            var rows = (pack["tables"] as JsonObject)?.Sum(t => (t.Value?["rows"] as JsonArray)?.Count ?? 0) ?? 0;
            var cut = Cut(pack);
            FieldContext.Expect(cut.Count == 0, "the results are not whole: " + string.Join(", ", cut));
            var left = pack["manifest"]?["left_out_tables"] as JsonArray ?? [];
            FieldContext.Expect(left.Count == 0, "tables were left out of the pack: " + left.ToJsonString());
            var unread = (pack["manifest"]?["notes"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").Where(n => n.Contains("could not be read", StringComparison.Ordinal)).ToList();
            FieldContext.Expect(unread.Count == 0, "tables could not be read: " + string.Join(" | ", unread));
            var round = At("results") + At("close") + load;
            var slowest = pack["manifest"]?["timing"]?["slowest"]?.ToJsonString();
            FieldContext.Expect(status["closed"]?.GetValue<bool>() == true, $"{program.Name} was not closed after the run, so its licence is still held");
            FieldContext.Expect(round <= Pack.LargeMostTime.TotalSeconds,
                $"reading the results ({At("results"):0} s), closing {program.Name} ({At("close"):0} s) and giving them to the model ({load:0.0} s) took {round:0} s, more than {Pack.LargeMostTime.TotalSeconds:0} s, for {frames} frames and {areas} areas. Slowest tables: {slowest}");
            return new
            {
                frames, areas, tables, rows, left_out = pack["manifest"]?["left_out_tables"]?.ToJsonString(), megabytes = Math.Round(text.Length / 1e6, 1),
                results_seconds = Math.Round(At("results"), 1), close_seconds = Math.Round(At("close"), 1), load_seconds = Math.Round(load, 1),
                start_seconds = Math.Round(At("start"), 1), analysis_seconds = Math.Round(At("analysis"), 1), slowest,
            };
        });
    }

    /// <summary>The tables measured: a small input table, a story result and an element result, the first with data of each.</summary>
    private static readonly string[][] Probes =
    [
        ["Load Pattern Definitions", "Load Patterns"],
        ["Base Reactions", "Story Drifts", "Modal Periods And Frequencies"],
        ["Element Forces - Beams", "Element Forces - Frames", "Joint Displacements"],
        ["Joint Assignments - Springs", "Joint Spring Assignments - 1 - Uncoupled"],
    ];

    /// <summary>
    /// How long the program takes to give a table each way, read inside its own call so the time is the program's.
    /// Never fails on a way the program does not have: that is a finding.
    /// </summary>
    public static void Probe(FieldContext ctx, ICsiLink link, string program)
    {
        if (!FieldTest.IsLive(link)) return;
        ctx.Step($"how fast {program} gives a table, each way (array, CSV, XML{(link.Program.Key == CsiProgram.Etabs.Key ? ", every table into one file" : "")})", () =>
        {
            var known = link.Tables();
            var found = new JsonArray();
            var baseline = (long)(link.Raw("time a plain call", m =>
            {
                var w = Stopwatch.StartNew();
                for (var i = 0; i < 3; i++) { int n = 0; string[] names = []; m.LoadPatterns.GetNameList(ref n, ref names); }
                return w.ElapsedMilliseconds / 3;
            }) ?? -1L);
            foreach (var group in Probes)
            {
                var t = group.Select(g => known.FirstOrDefault(k => !k.Empty && (string.Equals(k.Name, g, StringComparison.OrdinalIgnoreCase) || string.Equals(k.Key, g, StringComparison.OrdinalIgnoreCase)))).FirstOrDefault(k => k is not null);
                if (t is null) continue;
                var row = new JsonObject { ["table"] = t.Name };
                foreach (var way in new[] { "array", "array again", "csv", "xml" })
                    row[way] = (string?)link.Raw($"time a table read ({way})", m =>
                    {
                        var w = Stopwatch.StartNew();
                        try
                        {
                            string[] ask = [], fields = [], data = []; int version = 0, records = 0; string text = "";
                            int ret = way.StartsWith("array", StringComparison.Ordinal)
                                ? Convert.ToInt32(m.DatabaseTables.GetTableForDisplayArray(t.Key, ref ask, "", ref version, ref fields, ref records, ref data))
                                : way == "csv"
                                    ? Convert.ToInt32(m.DatabaseTables.GetTableForDisplayCSVString(t.Key, ref ask, "", ref version, ref text, ","))
                                    : Convert.ToInt32(m.DatabaseTables.GetTableForDisplayXMLString(t.Key, ref ask, "", false, ref version, ref text));
                            return $"{w.ElapsedMilliseconds} ms, answer {ret}, " + (way.StartsWith("array", StringComparison.Ordinal) ? $"{records} rows" : $"{(text ?? "").Length} characters");
                        }
                        catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { return $"{w.ElapsedMilliseconds} ms, not offered ({ex.GetType().Name}: {ex.Message})"; }
                    });
                found.Add(row);
            }
            var o = new JsonObject { ["plain_call_ms"] = baseline, ["tables"] = found };
            if (link.Program.Key == CsiProgram.Etabs.Key) o["one_file"] = OneFile(ctx, link);
            return o.ToJsonString();
        });
    }

    /// <summary>ETABS's File.ExportFile with each kind of table file, timed, with the file's size and its first lines.</summary>
    private static JsonArray OneFile(FieldContext ctx, ICsiLink link)
    {
        var all = new JsonArray();
        var kinds = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => { try { return a.GetType("ETABSv1.eFileTypeIO"); } catch (Exception) { return null; } }).FirstOrDefault(t => t is not null);
        if (kinds is null) { all.Add((JsonNode)"ETABSv1.eFileTypeIO is not loaded in this process: not measured"); return all; }
        foreach (var (kind, ext) in new[] { ("DBTablesXML", ".xml"), ("DBTablesText", ".txt"), ("DBTablesAccess", ".mdb") })
        {
            if (!Enum.TryParse(kinds, kind, out var value)) continue;
            var file = Path.Combine(ctx.Folder, "every table" + ext);
            var said = (string?)link.Raw("export every table into one file", m =>
            {
                var w = Stopwatch.StartNew();
                try { int ret = Convert.ToInt32(m.File.ExportFile(file, value)); return $"{w.ElapsedMilliseconds} ms, answer {ret}"; }
                catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { return $"{w.ElapsedMilliseconds} ms, refused ({ex.GetType().Name}: {ex.Message})"; }
            });
            var head = "";
            try
            {
                if (File.Exists(file))
                {
                    using var reader = new StreamReader(file);
                    var buffer = new char[3000];
                    head = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { head = "unreadable: " + ex.Message; }
            all.Add((JsonNode)new JsonObject
            {
                ["kind"] = kind, ["said"] = said, ["bytes"] = File.Exists(file) ? new FileInfo(file).Length : -1, ["head"] = kind == "DBTablesAccess" ? "" : head,
            });
        }
        return all;
    }
}
