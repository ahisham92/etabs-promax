using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Packs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The review pack written by itself after a run and a design (AutoPack, PackWatcher), on each program's scratch model
/// while it has its results: the same steps for ETABS, SAP2000 and SAFE. The watcher's own rule leaves the field
/// test's models alone, so these steps call it themselves and say what it would do on the engineer's model.
/// </summary>
public static class AutoPackFieldSteps
{
    public static void FieldSteps(FieldContext ctx, ICsiLink link, string program)
    {
        PackLook? look = null;
        var file = "";
        var wrote = ctx.Step($"the {program} model's review pack is written by itself after its analysis", () =>
        {
            look = AutoPack.Look(link);
            file = look.File;
            // A stand-in for a program (the tests) cannot analyse. A real program's model with no results is a failure
            // (its analysis step says why).
            if (!look.Locked && link is OfflineEtabs or MemoryEtabs) throw new FieldSkipException($"the stand-in has no analysis, so its {program} model has no results to pack");
            FieldContext.Expect(look.Locked, $"{program} says the scratch model has no results (it is not locked), so there was no run to pack; see the run_analysis step");
            FieldContext.Expect(AutoPack.Saved(file), $"{program} gives the scratch model's file as '{file}', which has no folder to put the pack beside");
            var pack = AutoPack.PackFile(file);
            if (File.Exists(pack)) File.Delete(pack);
            PackLook? before = null;
            // Past the settling time, as the watcher's next look would be.
            var said = AutoPack.Once(link, ref before, fieldTestModels: true, nowUtc: DateTime.UtcNow + AutoPack.Settle + TimeSpan.FromSeconds(1));
            FieldContext.Expect(said is not null && File.Exists(pack) && File.Exists(AutoPack.PageFile(file)), $"no pack was written beside {file} ({said ?? "the rule said nothing to do"})");
            var made = AutoPack.MadeOf(pack);
            FieldContext.Expect(made is not null, "the pack written cannot be read back");
            return new
            {
                pack = Path.GetFileName(pack), said,
                analysis_log = look.RunStamp is { } r ? $"{program} wrote the analysis log beside the model at {r.ToLocalTime():HH:mm:ss}" : $"{program} wrote no .LOG or .OUT beside the model: a run again is seen only when the model gets its results again",
                design_tables = look.Design.Length == 0 ? "none with data" : look.Design.Replace("|", ", "),
            };
        }) is not null;
        if (!wrote || look is null) return;

        ctx.Step($"the {program} watcher writes nothing again when nothing changed", () =>
        {
            var pack = AutoPack.PackFile(file);
            var at = File.GetLastWriteTimeUtc(pack);
            PackLook? before = look;
            var said = AutoPack.Once(link, ref before, fieldTestModels: true, nowUtc: DateTime.UtcNow + AutoPack.Settle + TimeSpan.FromSeconds(1));
            FieldContext.Expect(said is null && File.GetLastWriteTimeUtc(pack) == at, "the pack was written again with nothing changed: " + said);
            return "nothing written";
        });

        ctx.Step($"the {program} pack is written again after a design, and keeps the reviewer's comments", () =>
        {
            var pack = AutoPack.PackFile(file);
            var designed = (Pack.Read(File.ReadAllBytes(pack))["manifest"]?["design"] as JsonObject)?
                .Where(kv => kv.Key != "not_run").Select(kv => kv.Key).ToList() ?? [];
            // The pack has design results: the watcher must see a design in the tables, or a design would never update it.
            FieldContext.Expect(designed.Count == 0 || look.Design.Length > 0,
                $"the pack has {string.Join(", ", designed)} design results, but no design table of {program} says it has data, so a design would not update the pack");
            AddComment(pack, "CSI ProMax field test: a comment that must stay");
            // As after a design that was deleted (a design overwrite changed) and run again while the watcher looked.
            PackLook? before = look with { Design = "" };
            var now = look.Design.Length > 0 ? look : look with { Design = "field-test-design" };
            var why = AutoPack.Decide(before, now, AutoPack.MadeOf(pack), DateTime.UtcNow + AutoPack.Settle + TimeSpan.FromSeconds(1));
            FieldContext.Expect(why == "design", $"the rule said '{why ?? "nothing"}' after a design, not 'design'");
            var written = AutoPack.Write(link, now, "design");
            var content = Pack.Read(File.ReadAllBytes(written.Pack));
            FieldContext.Expect(content["manifest"]?["auto"]?["why"]?.GetValue<string>() == "design", "the pack does not say it was written after the design");
            FieldContext.Expect(written.Comments == 1 && content["comments"] is JsonArray { Count: 1 }, $"the reviewer's comment was not kept ({written.Comments} carried)");
            return new { design_tables = look.Design.Length == 0 ? "none with data (the step used a stand-in)" : look.Design.Replace("|", ", "), seconds = Math.Round(written.Took.TotalSeconds, 1), tables = written.Tables };
        });

        ctx.Step($"the watcher leaves the field test's own {program} model to the field test", () =>
        {
            FieldContext.Expect(AutoPack.FieldTestModel(file), $"the scratch model {Path.GetFileName(file)} is not named cpm-..., so a watcher on this PC would make packs of it while the test works in it");
            PackLook? before = null;
            FieldContext.Expect(AutoPack.Once(link, ref before) is null, "the watcher wrote a pack of the field test's model");
            return "left alone";
        });
    }

    /// <summary>A reviewer's comment put into the pack, as the Viewer saves one.</summary>
    internal static void AddComment(string pack, string text)
    {
        using var zip = ZipFile.Open(pack, ZipArchiveMode.Update);
        var list = new JsonArray();
        if (zip.GetEntry("comments.json") is { } old)
        {
            using (var reader = new StreamReader(old.Open(), Encoding.UTF8)) if (JsonNode.Parse(reader.ReadToEnd()) is JsonArray had) list = had;
            old.Delete();
        }
        list.Add(new JsonObject { ["who"] = "field test", ["at"] = DateTime.UtcNow.ToString("o"), ["text"] = text });
        using var s = zip.CreateEntry("comments.json").Open();
        var bytes = Encoding.UTF8.GetBytes(list.ToJsonString());
        s.Write(bytes, 0, bytes.Length);
    }
}
