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
