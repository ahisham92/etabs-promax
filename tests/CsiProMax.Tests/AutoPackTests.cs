using System.Text.Json.Nodes;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Packs;
using CsiProMax.Assistant.Tools;

/// <summary>
/// The review pack written by itself after every run and design (Ahmed, 2026-11-26): the rule, the files beside the
/// model, the reviewer's comments carried, and the field test's models left alone.
/// </summary>
static class AutoPackTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static readonly DateTime T0 = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    const string Model = @"C:\Jobs\Tower\Tower.EDB";

    static MemoryEtabs Analysed(string file)
    {
        var m = new MemoryEtabs { ModelFile = file };
        m.AddFrame(0, 0, 0, 0, 0, 3.2, "C500x500");
        m.AddFrame(0, 0, 3.2, 6, 0, 3.2, "B300x600");
        m.Locked = true;
        m.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Drift"], [["Story1", "Dead", "0.001"]]);
        return m;
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("auto pack: the rule writes after a run and a design, and not when nothing changed or there are no results", () =>
        {
            var run = T0.AddMinutes(-5);
            var now = new PackLook(Model, true, run, "");
            Equal("analysis", AutoPack.Decide(null, now, null, T0), "results and no pack beside the model");
            Equal<string?>(null, AutoPack.Decide(null, now, new PackMade(run, ""), T0), "the pack is from this run");
            Equal("analysis", AutoPack.Decide(now, now with { RunStamp = T0.AddMinutes(-1) }, new PackMade(run, ""), T0), "a new run wrote the log again");
            Equal<string?>(null, AutoPack.Decide(now, now with { RunStamp = T0.AddSeconds(-3) }, new PackMade(run, ""), T0), "the analysis is still writing its log");
            Equal<string?>(null, AutoPack.Decide(null, now with { Locked = false }, null, T0), "no results: nothing to pack");
            Equal<string?>(null, AutoPack.Decide(null, now with { File = "" }, null, T0), "a model never saved has no folder");
            Equal<string?>(null, AutoPack.Decide(null, now with { File = "Untitled" }, null, T0), "a bare name has no folder");
            Equal("analysis", AutoPack.Decide(now with { Locked = false }, now with { RunStamp = null }, new PackMade(null, ""), T0), "with no log beside the model, the model getting its results is the run");
            Equal("analysis", AutoPack.Decide(null, now, new PackMade(null, ""), T0), "a pack made by hand is replaced after the next run");
            var designed = now with { Design = "Concrete Frame Design 1 - Beam Summary" };
            Equal("design", AutoPack.Decide(now, designed, new PackMade(run, ""), T0), "a design ran");
            Equal<string?>(null, AutoPack.Decide(designed, designed, new PackMade(run, designed.Design), T0), "the design is in the pack");
            Equal("design", AutoPack.Decide(now, designed, new PackMade(run, designed.Design), T0), "the design was deleted (an overwrite changed) and run again");
            Equal<string?>(null, AutoPack.Decide(designed, now, new PackMade(run, designed.Design), T0), "a design deleted keeps the last pack");
        });

        yield return ("auto pack: the log beside the model is the run's time, and design inputs are not a design", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-autopack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "Tower.EDB");
                Equal<DateTime?>(null, AutoPack.RunStamp(file), "no log yet");
                File.WriteAllText(Path.Combine(folder, "Tower.LOG"), "A N A L Y S I S   C O M P L E T E");
                File.SetLastWriteTimeUtc(Path.Combine(folder, "Tower.LOG"), T0);
                File.WriteAllText(Path.Combine(folder, "Tower.OUT"), "");
                File.SetLastWriteTimeUtc(Path.Combine(folder, "Tower.OUT"), T0.AddMinutes(1));
                Equal<DateTime?>(T0.AddMinutes(1), AutoPack.RunStamp(file), "the newer of .LOG and .OUT");
                Equal(Path.Combine(folder, "Tower.cpmx"), AutoPack.PackFile(file), "the pack is named after the model, beside it");
                var tables = new[]
                {
                    new TableName("k1", "Concrete Frame Design Preferences - ACI 318-19", false),
                    new TableName("k2", "Concrete Beam Design Summary - ACI 318-19", false),
                    new TableName("k3", "Steel Frame Design Summary - AISC 360-16", true),
                    new TableName("k4", "Story Drifts", false),
                };
                Equal("k2", AutoPack.DesignOf(tables), "only design results with data count");
            }
            finally { Directory.Delete(folder, true); }
        });

        yield return ("auto pack: written beside the model, once, with the reviewer's comments kept and the Viewer page beside it", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-autopack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "Tower.EDB");
                var link = Analysed(file);
                File.WriteAllText(Path.Combine(folder, "Tower.LOG"), "");
                File.SetLastWriteTimeUtc(Path.Combine(folder, "Tower.LOG"), DateTime.UtcNow.AddMinutes(-2));
                PackLook? before = null;
                var said = AutoPack.Once(link, ref before);
                Check(said is not null && said.Contains("after the analysis"), "the first look writes the pack: " + said);
                var pack = Path.Combine(folder, "Tower.cpmx");
                Check(File.Exists(pack) && File.Exists(Path.Combine(folder, "Tower viewer.html")), "the pack and the Viewer page are beside the model");
                var content = Pack.Read(File.ReadAllBytes(pack));
                Equal("analysis", content["manifest"]!["auto"]!["why"]!.GetValue<string>(), "the pack says why it was written");
                Equal(2, (content["model"]!["frames"] as JsonArray)!.Count, "the model is in it");
                Check(AutoPack.Once(link, ref before) is null, "a second look writes nothing");
                Check(!Directory.GetFiles(folder).Any(f => f.EndsWith(".writing", StringComparison.Ordinal)), "nothing half written is left");

                AutoPackFieldSteps.AddComment(pack, "check the beam at grid B");
                link.ResultTables["Concrete Beam Design Summary - ACI 318-19"] = new(["Story", "Label", "Ratio"], [["Story1", "B1", "0.8"]]);
                said = AutoPack.Once(link, ref before);
                Check(said is not null && said.Contains("after the design") && said.Contains("1 comments kept"), "a design writes it again, keeping the comment: " + said);
                content = Pack.Read(File.ReadAllBytes(pack));
                Equal(1, (content["comments"] as JsonArray)!.Count, "the reviewer's comment is in the new pack");
                Check(content["tables"]!.AsObject().ContainsKey("Concrete Beam Design Summary - ACI 318-19"), "the design results are in the new pack");
                Check(AutoPack.Once(link, ref before) is null, "and nothing again after that");

                // The field test's own models are left to the field test.
                var scratch = Analysed(Path.Combine(folder, "cpm-field-test.EDB"));
                PackLook? none = null;
                Check(AutoPack.Once(scratch, ref none) is null && !File.Exists(Path.Combine(folder, "cpm-field-test.cpmx")), "the field test's model is left alone");
            }
            finally { Directory.Delete(folder, true); }
        });

        yield return ("auto pack: the pack follows the model's lock: taken away when it is unlocked (comments kept), made again by the next run, kept while another window still has results", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-autopack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "Tower.EDB");
                var log = Path.Combine(folder, "Tower.LOG");
                File.WriteAllText(log, "");
                File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(-2));
                var a = Analysed(file);
                var b = Analysed(file);
                PackLook? beforeA = null, beforeB = null;
                Check(AutoPack.Once(a, ref beforeA, window: "a") is not null, "the run makes the pack");
                Check(AutoPack.Once(b, ref beforeB, window: "b") is null, "a second window on the same model finds it made");
                var pack = Path.Combine(folder, "Tower.cpmx");
                var page = Path.Combine(folder, "Tower viewer.html");
                AutoPackFieldSteps.AddComment(pack, "check the beam at grid B");

                // Unlocked in one window while the other still has the results: the pack stays.
                a.Locked = false;
                Check(AutoPack.Once(a, ref beforeA, window: "a") is null && File.Exists(pack), "the other window still shows the results: the pack stays");
                // Unlocked in both: it goes, and the comment is kept beside the model.
                b.Locked = false;
                var said = AutoPack.Once(b, ref beforeB, window: "b");
                Check(said is not null && said.Contains("taken away") && !File.Exists(pack) && !File.Exists(page), "no window has results: the pack and its page are taken away: " + said);
                var kept = Path.Combine(folder, "Tower comments.json"); // PackLife's, shared with the Engine
                Check(File.Exists(kept) && File.ReadAllText(kept).Contains("grid B"), "the reviewer's comment is kept beside the model");
                Check(AutoPack.Once(b, ref beforeB, window: "b") is null, "and nothing again after that");

                // The next run makes it again, with the comment back in it.
                a.Locked = true;
                File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(-1));
                said = AutoPack.Once(a, ref beforeA, window: "a");
                Check(said is not null && said.Contains("1 comments kept"), "the next run makes the pack again, with the comment: " + said);
                Equal(1, (Pack.Read(File.ReadAllBytes(pack))["comments"] as JsonArray)!.Count, "the comment once, not twice");

                // Comments kept under the old name ("<model> review comments.json") are read once into the shared file.
                var old = Path.Combine(folder, "Tower review comments.json");
                File.WriteAllText(old, new JsonObject { ["model"] = "Tower", ["comments"] = new JsonArray(new JsonObject { ["who"] = "R", ["at"] = "2026-11-26T10:00:00Z", ["text"] = "old note" }) }.ToJsonString());
                var all = AutoPack.KeepComments(Path.Combine(folder, "Tower.EDB"));
                Check(all.Count == 2 && !File.Exists(old) && File.ReadAllText(kept).Contains("old note"), "the old comments file is taken into the shared one and removed");

                // A window that closed no longer holds the pack.
                AutoPack.Forget("a");
                b.Locked = false;
                Check(AutoPack.Once(b, ref beforeB, window: "b") is not null && !File.Exists(pack), "with the window that had results closed, unlocking takes the pack away");

                // A pack made by hand is the engineer's: it stays.
                File.WriteAllBytes(pack, Pack.Build(Analysed(file), [], [], [], null, null).Zip);
                Check(AutoPack.Once(b, ref beforeB, window: "b") is null && File.Exists(pack), "a pack made by hand is never taken away");
                AutoPack.Forget("b");
            }
            finally { Directory.Delete(folder, true); }
        });

        yield return ("auto pack: the watcher is started by the window, the keeper and the install line, and every program is watched", () =>
        {
            var program = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs"));
            Check(program.Contains("PackWatcher.Run(args)") && program.Contains("PackWatcher.EnsureRunning()"), "Program starts the watcher (--packs) and every window makes sure it runs");
            Check(program.Contains("FollowPack("), "the Viewer follows the pack it shows");
            var keeper = File.ReadAllText(Path.Combine(SourceText.Root(), "keep-plugins.ps1"));
            Check(keeper.Contains("packs.txt") && keeper.Contains("CsiProMax.Packs"), "the keeper starts the watcher at sign-in when it is on");
            Check(keeper.Contains("packs-on.txt"), "the keeper starts the watcher only when the engineer turned it on (packs-on.txt)");
            var install = File.ReadAllText(Path.Combine(SourceText.Root(), "install.ps1"));
            Check(install.Contains("-NoPacks") && install.Contains("packs.txt"), "install.ps1 turns the watcher on (and -NoPacks off)");
            Check(install.Contains("packs-on.txt") && System.Text.RegularExpressions.Regex.IsMatch(install, @"elseif \(\$Packs\) \{\s*\[IO\.File\]::WriteAllText\(\$chosenFile"), "install.ps1 turns the watcher on only with -Packs");
            var watcher = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Packs/PackWatcher.cs"));
            Check(watcher.Contains("CsiProgram.All"), "every CSI program is looked for");
            Check(watcher.Contains("Responding(f.Pid)"), "a program whose window does not answer is not asked anything");
            Check(!watcher.Contains(".Save(") && !File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Packs/AutoPack.cs")).Contains(".Save("), "the watcher never saves the model");
        });

        yield return ("auto pack: the watcher is off until the engineer turns it on, and packs-off.txt still wins", () =>
        {
            var keep = PackWatcher.StateRoot;
            var folder = Path.Combine(Path.GetTempPath(), "cpm-packs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                PackWatcher.StateRoot = folder;
                // Only installed (an older version may have left packs.txt saying where it is): off.
                File.WriteAllText(Path.Combine(folder, "packs.txt"), "on|dotnet|folder");
                Check(PackWatcher.Off, "an installed CSI ProMax that nobody turned on leaves the programs alone");
                Equal(0, PackWatcher.Run([]), "the watcher started without packs-on.txt ends at once");
                File.WriteAllText(PackWatcher.ChosenFile, "on");
                Check(!PackWatcher.Off, "packs-on.txt turns it on");
                File.WriteAllText(PackWatcher.OffFile, "off");
                Check(PackWatcher.Off, "packs-off.txt turns it off even when it was turned on");
            }
            finally { PackWatcher.StateRoot = keep; Directory.Delete(folder, true); }
        });
    }
}
