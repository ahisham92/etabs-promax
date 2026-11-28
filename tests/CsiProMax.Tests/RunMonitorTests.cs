using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Checks.Stability;

/// <summary>
/// CSI ProMax Engine's Run Monitor and Run Report (Ahmed 2026-11-27: a live view of the run, like ETABS's Analysis
/// Monitor, and never stuck without knowing why when the model has a problem in ETABS, SAP2000 or SAFE).
/// </summary>
static class RunMonitorTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    // SAPFire's own words, as ETABS 23 and SAP2000 26 write them in the model's .LOG.
    static readonly string[] Unstable =
    [
        " B E G I N   A N A L Y S I S                           2026/11/27  12:40:01",
        " E L E M E N T   F O R M A T I O N                     12:40:01",
        " L I N E A R   E Q U A T I O N   S O L U T I O N       12:40:02",
        "  FORMING STIFFNESS AT ZERO (UNSTRESSED) INITIAL CONDITIONS",
        " TOTAL NUMBER OF WARNINGS = 0",
        " CONVERGENCE TOLERANCE, RELATIVE  =  1.000E-04",
        " LINEAR STATIC CASES                                    12:40:02",
        "     CASE: DEAD",
        "     CASE: LIVE",
        " WARNING: NUMERICAL PROBLEMS ENCOUNTERED. LOSS OF ACCURACY AT JOINT 23",
        " *** WARNING: STRUCTURE IS UNSTABLE OR ILL-CONDITIONED !!",
        "     CHECK THE MODEL CAREFULLY FOR:",
        "     - INADEQUATE SUPPORT CONDITIONS, OR",
        "     - ONE OR MORE INTERNAL MECHANISMS, OR",
        " E I G E N   M O D A L   A N A L Y S I S               12:40:03",
        "     CASE: MODAL",
        " A N A L Y S I S   C O M P L E T E                     2026/11/27  12:40:05",
    ];

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("run monitor: the program's log of an unstable model is said in plain words, with what to do and the joint it names", () =>
        {
            var t = RunLog.Trouble(Unstable);
            var kinds = t.Select(x => x!["kind"]!.GetValue<string>()).ToList();
            Check(kinds.Contains("unstable") && kinds.Contains("accuracy"), "the unstable and accuracy messages were not found: " + string.Join(", ", kinds));
            Check(!kinds.Contains("error"), "a line saying 0 warnings, or a tolerance, was taken for an error: " + t.ToJsonString());
            Check(kinds.IndexOf("unstable") < kinds.IndexOf("accuracy"), "errors are not listed before warnings");
            var unstable = t.First(x => x!["kind"]!.GetValue<string>() == "unstable")!;
            Check((unstable["todo"]?.GetValue<string>() ?? "").Contains("support", StringComparison.Ordinal), "no advice on what to do");
            var accuracy = t.First(x => x!["kind"]!.GetValue<string>() == "accuracy")!;
            Check(accuracy["objects"]!.AsArray().Any(o => o!["kind"]!.GetValue<string>() == "joint" && o["name"]!.GetValue<string>() == "23"), "the joint the log names was not taken for Show on model: " + accuracy.ToJsonString());
            Check(RunLog.Complete(Unstable), "ANALYSIS COMPLETE was not seen");
            Check(RunLog.Trouble(Unstable.Where(l => !l.Contains("WARNING", StringComparison.Ordinal) && !l.TrimStart().StartsWith('-') && !l.Contains("CHECK THE MODEL", StringComparison.Ordinal)).ToList()).Count == 0, "a clean log has trouble");
        });

        yield return ("run monitor: the case the solver is on, one count per case, and the steps with their times", () =>
        {
            Check(RunLog.CaseOf("     CASE: DEAD") == "DEAD" && RunLog.CaseOf("  ITERATING FOR CASE PDELTA") == "PDELTA" && RunLog.CaseOf(" LINEAR STATIC CASES") is null, "case lines are not read");
            var now = new DateTime(2026, 11, 27, 12, 0, 0);
            var mon = new RunMonitor(CsiProgram.Etabs, () => now) { CaseCount = 3 };
            mon.Begin("check"); now = now.AddSeconds(2); mon.End("check");
            mon.Begin("start"); now = now.AddSeconds(20); mon.End("start");
            mon.Begin("analysis");
            mon.Log(Unstable.Take(9));
            mon.Log(["     CASE: DEAD"]);
            Check(mon.Case == "LIVE" && mon.CaseNumber == 2, $"the case is {mon.Case} ({mon.CaseNumber})");
            now = now.AddSeconds(7);
            var j = mon.Json();
            Check(j["elapsed"]!.GetValue<double>() == 29 && j["last_heard"]!.GetValue<double>() == 7, "elapsed or last heard: " + j.ToJsonString());
            var steps = j["steps"]!.AsArray();
            Check(steps[0]!["state"]!.GetValue<string>() == "done" && steps[0]!["seconds"]!.GetValue<double>() == 2 && steps[2]!["state"]!.GetValue<string>() == "running", "steps: " + steps.ToJsonString());
            Check(steps[1]!["name"]!.GetValue<string>().Contains("ETABS", StringComparison.Ordinal), "the program is not named in its step");
            mon.Stop("the analysis did not run");
            var after = mon.All;
            Check(after.First(s => s.Key == "analysis").State == "failed" && after.First(s => s.Key == "design").State == "skipped" && after.First(s => s.Key == "close").State == "waiting", "a stop does not mark the step failed and the rest not run (closing still to come)");
        });

        yield return ("run monitor: the log is read as the program writes it, a part line kept until its end, a new log read from its start", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "cpm-tail-" + Guid.NewGuid().ToString("N")[..6] + ".LOG");
            try
            {
                long offset = 0; var part = "";
                Check(RunLog.Tail(file, ref offset, ref part).Count == 0, "a log not there yet gave lines");
                File.WriteAllText(file, "LINE 1\r\nLINE 2\r\nLI");
                var a = RunLog.Tail(file, ref offset, ref part);
                Check(a.SequenceEqual(["LINE 1", "LINE 2"]) && part == "LI", "first read: " + string.Join("|", a) + " / " + part);
                File.AppendAllText(file, "NE 3\r\n");
                var b = RunLog.Tail(file, ref offset, ref part);
                Check(b.SequenceEqual(["LINE 3"]), "second read: " + string.Join("|", b));
                File.WriteAllText(file, "NEW\n");
                var c = RunLog.Tail(file, ref offset, ref part);
                Check(c.SequenceEqual(["NEW"]), "a new, shorter log was not read from its start: " + string.Join("|", c));
                Check(RunLog.FileOf(Path.ChangeExtension(file, ".EDB")) == file, "the log beside the model is not found");
            }
            finally { File.Delete(file); }
        });

        yield return ("run monitor: the design's members that do not pass, and their messages, are in the Run Report", () =>
        {
            var tables = new JsonObject
            {
                ["Steel Frame Design Summary - AISC 360-16"] = new JsonObject
                {
                    ["fields"] = new JsonArray("Story", "Label", "Status", "Ratio", "ErrMsg", "WarnMsg"),
                    ["rows"] = new JsonArray(new JsonArray("Story1", "B1", "No Messages", "0.5", "No Messages", "No Messages"),
                        new JsonArray("Story1", "B2", "Overstressed", "1.3", "No Messages", "No Messages"),
                        new JsonArray("Story1", "C1", "No Messages", "0.7", "Section is slender", "No Messages")),
                },
                ["Joint Displacements"] = new JsonObject { ["fields"] = new JsonArray("Status"), ["rows"] = new JsonArray(new JsonArray("Error")) },
            };
            var d = RunLog.DesignTrouble(tables);
            Check(d.Count == 1, "not one design table with trouble: " + d.ToJsonString());
            Check(d[0]!["failing"]!.GetValue<int>() == 1 && d[0]!["errors"]!.GetValue<int>() == 1, "counts: " + d.ToJsonString());
            var objects = d[0]!["examples"]!.AsArray().Select(x => x!["object"]!.GetValue<string>()).ToList();
            Check(objects.SequenceEqual(["B2", "C1"]), "the members named: " + string.Join(", ", objects));
        });

        yield return ("run monitor: CSI ProMax's own failures (licence, program hung, not finished) say what to do", () =>
        {
            Check(RunLog.Plain("ETABS did not start within 4 minutes")?["kind"]?.GetValue<string>() == "licence", "a program that did not start is not told as a licence wait");
            Check(RunLog.Plain("the scratch ETABS window stopped answering earlier")?["kind"]?.GetValue<string>() == "hung", "a hung program is not told");
            Check(RunLog.Plain("ETABS was asked to run the analysis and answered, but did not finish it: the model is not analysed (not locked).")?["kind"]?.GetValue<string>() == "not-finished", "an analysis that did not finish is not told");
            Check(RunLog.Plain("") is null, "an empty error has a meaning");
        });

        yield return ("run monitor: Open in the program opens a copy of the model as it ran, with its results files, never the run's own", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-inspect-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var ext in new[] { ".EDB", ".LOG", ".Y$$" }) File.WriteAllText(Path.Combine(folder, "cpm-field-test-abc" + ext), ext);
                File.WriteAllText(Path.Combine(folder, "other.EDB"), "x");
                var copy = EngineRun.CopyForInspection(Path.Combine(folder, "cpm-field-test-abc.EDB"));
                var dir = Path.GetDirectoryName(copy)!;
                Check(dir != folder && Path.GetFileName(dir).StartsWith("inspect ", StringComparison.Ordinal), "the copy is not in an inspect folder: " + copy);
                Check(File.Exists(copy) && File.Exists(Path.Combine(dir, "cpm-field-test-abc.LOG")) && File.Exists(Path.Combine(dir, "cpm-field-test-abc.Y$$")), "the results files were not copied with the model");
                Check(!File.Exists(Path.Combine(dir, "other.EDB")), "another model was copied too");
            }
            finally { Directory.Delete(folder, true); }
        });

        yield return ("run monitor: a model the instability check finds errors in is stopped before the program starts, with the findings in the Run Report", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-stop-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            try
            {
                var broken = BrokenModels.All.First(c => c.Name == "no-supports");
                var model = new OfflineEtabs(broken.Build(), "");
                model.SaveAs(Path.Combine(folder, "Broken.e2k"));
                var api = Path.Combine(folder, "ETABSv1.dll");
                File.WriteAllText(api, "");
                var started = EngineRun.Start(model, analyse: true, kinds: [], apiFile: _ => api, stopOnErrors: true);
                Check(started["ok"]?.GetValue<bool>() == true, "the run did not start: " + started.ToJsonString());
                var until = DateTime.UtcNow.AddSeconds(60);
                while (EngineRun.Status(false)["done"]?.GetValue<bool>() != true && DateTime.UtcNow < until) Thread.Sleep(100);
                var st = EngineRun.Status(true);
                Check(st["done"]?.GetValue<bool>() == true, "the run did not end: " + st.ToJsonString());
                Check((st["error"]?.GetValue<string>() ?? "").Contains("instability check found", StringComparison.Ordinal), "the run was not stopped by the check: " + st["error"]);
                var mon = st["monitor"]!.AsObject();
                var steps = mon["steps"]!.AsArray().ToDictionary(s => s!["key"]!.GetValue<string>(), s => s!["state"]!.GetValue<string>());
                Check(steps["check"] == "failed" && steps["start"] == "skipped" && steps["close"] == "skipped", "steps: " + string.Join(", ", steps));
                var report = mon["report"]!.AsObject();
                Check(report["ok"]?.GetValue<bool>() == false && (report["headline"]?.GetValue<string>() ?? "").Contains("Check the model for instabilities", StringComparison.Ordinal), "the report does not name the step: " + report["headline"]);
                Check(report["instabilities"]?["findings"]?.AsArray().Any(f => f!["check"]!.GetValue<string>() == "supports") == true, "the report does not list the missing supports");
                Check(st["closed"]?.GetValue<bool>() == true, "no program was started, yet the licence is not said to be free");
                Check(EngineRun.Cancel()["ok"]?.GetValue<bool>() == false, "Cancel with no run going answered yes");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });
        yield return ("run window: ETABS's own Analyzing window is told apart from its boxes, and its log box is read line by line (Ahmed 2026-11-28)", () =>
        {
            Check(CsiProMax.Assistant.ScratchDialogs.IsRunWindow("Analyzing ( 95%):  Case ( 51%) MODAL"), "ETABS's run window");
            Check(CsiProMax.Assistant.ScratchDialogs.IsRunWindow("Analysis Complete"), "the window after the run");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsRunWindow("Analysis Messages"), "the messages box is not the run window");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsRunWindow("ETABS Ultimate 23.2.0 - cpm-field-test-8362c3"), "the main window is not the run window");
            var lines = CsiProMax.Assistant.ScratchDialogs.LogLines("  8212  1.22E-14\r\n\r\n     CASE: MODAL\r\n  8213  -9.68E-14\n");
            Check(lines.Count == 3 && lines[1].Trim() == "CASE: MODAL", "log lines: " + string.Join("|", lines));
        });
        yield return ("run window: what the program's run window shows is in the monitor, with the case it is on and the engineer's own file", () =>
        {
            var mon = new RunMonitor(CsiProgram.Etabs) { SourceFile = @"C:\m\FIXED 05.cpme", ModelFile = @"C:\m\run\cpm-field-test-1.EDB" };
            mon.Screen("Analyzing ( 40%): Case ( 10%) MODAL", [" B E G I N   A N A L Y S I S", "     CASE: MODAL"]);
            mon.Windows("ETABS Ultimate 23.2.0");
            var j = mon.Json();
            Check(j["screen_title"]!.GetValue<string>().StartsWith("Analyzing"), "title");
            Check(j["screen"]!.AsArray().Count == 2, "screen lines");
            Check(j["source_file"]!.GetValue<string>().EndsWith("FIXED 05.cpme"), "own file");
            Check(j["windows"]!.GetValue<string>() == "ETABS Ultimate 23.2.0", "windows");
            Check(mon.Case == "MODAL", "case from the screen: " + mon.Case);
        });
        yield return ("run copy: an Engine run's copy is named after the engineer's file and is still the run's own; a label is never taken for the log (Ahmed 2026-11-28)", () =>
        {
            var name = CsiProMax.Assistant.ScratchProgram.RunCopyName("FIXED 05-CHECK COLUMNS-00 1");
            Check(name == "FIXED 05-CHECK COLUMNS-00 1 (CSI ProMax run)", name);
            Check(CsiProMax.Assistant.ScratchProgram.IsOwnCopy("ETABS Plus v23.2.0 - " + name), "the run's ETABS window is its own");
            Check(CsiProMax.Assistant.ScratchProgram.IsOwnCopy("ETABS Plus v23.2.0 - cpm-field-test-da0d92"), "a field test's window is its own");
            Check(!CsiProMax.Assistant.ScratchProgram.IsOwnCopy("ETABS Plus v23.2.0 - FIXED 05-CHECK COLUMNS-00 1"), "the engineer's own ETABS is never the run's");
            Check(CsiProMax.Assistant.ScratchProgram.MayEnd(DateTime.Now, DateTime.Now.AddSeconds(-5), ["ETABS Plus v23.2.0 - " + name]), "the run's copy may be ended by the run");
            Check(CsiProMax.Assistant.ScratchDialogs.IsLogBox("WindowsForms10.EDIT.app.0.141b42a_r6_ad1", 0x0004 | 0x00200000), "a multi-line text box is the log");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsLogBox("WindowsForms10.EDIT.app.0.141b42a_r6_ad1", 0x0080), "a one-line field is not");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsLogBox("WindowsForms10.STATIC.app.0.141b42a_r6_ad1", 0x0004), "a label is not");
            var folder = Path.Combine(Path.GetTempPath(), "cpm-run-copy-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var f in new[] { name + ".EDB", name + ".$et", name + ".LOG", "FIXED 05-CHECK COLUMNS-00 1.e2k", "FIXED 05-CHECK COLUMNS-00 1.cpme" }) File.WriteAllText(Path.Combine(folder, f), "x");
                EngineRun.ClearCopy(folder, name);
                var left = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(x => x).ToList();
                Check(left.SequenceEqual(new[] { "FIXED 05-CHECK COLUMNS-00 1.cpme", "FIXED 05-CHECK COLUMNS-00 1.e2k" }), "only the old copy's files go: " + string.Join(", ", left));
                EngineRun.ClearCopy(folder, "FIXED 05-CHECK COLUMNS-00 1");
                Check(Directory.GetFiles(folder).Length == 2, "a name without the run's mark is never cleared");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });
        yield return ("run window: the program's status bar is shown and counts as hearing from it, before any log (Ahmed's real model, 2026-11-28)", () =>
        {
            var at = new DateTime(2026, 11, 28, 12, 0, 0);
            var mon = new RunMonitor(CsiProgram.Etabs, () => at);
            at = at.AddMinutes(5);
            mon.Status("Creating Analysis Model - elements - shells - LVL 02-TOC");
            var j = mon.Json();
            Check(j["status_text"]!.GetValue<string>().StartsWith("Creating Analysis Model"), "status text");
            Check(j["last_heard"]!.GetValue<double>() == 0, "heard when the status bar changes: " + j["last_heard"]);
            Check(CsiProMax.Assistant.Etabs.StatusBarText.Words(["Creating Analysis Model - elements", "", "Creating Analysis Model - elements", " Global "]) == "Creating Analysis Model - elements  |  Global", "status words");
        });
    }
}