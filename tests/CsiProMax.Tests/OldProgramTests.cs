using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.F2k;
using CsiProMax.Model;

/// <summary>
/// The SAFE 20 field test of a tester's PC (CSI ProMax 0.4.40, 2026-11-26; SAFE 20, ETABS 20 and SAP2000 24 there, all
/// on the .NET Framework). Every read of the open SAFE model said "The SAFE window this was started from is closed",
/// a second window of each program "gave no application object", Tools > CSI ProMax in SAFE 20 was failed although
/// SAFE 20's own Tools menu had opened the window, and the chat refused to open E:\Pro max\LEVEL 29.FDB ("only .rvt").
/// The cause of the first two: those programs' API files reach the program through .NET Remoting, which the window's
/// .NET 8 does not have. One test for each bug and one for its kind, for all three programs.
/// </summary>
static class OldProgramTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    /// <summary>A fresh pipe served by the plug-in's bridge in this process, as SAFE 20's Tools menu now serves its model.</summary>
    static ApiPipe Served(object model)
    {
        CsiProMaxPlugin.ApiBridge.Serve(model);
        return ApiPipe.ToPlugin(Environment.ProcessId, "SAFE 20", 5000) ?? throw new Exception("the plug-in's pipe did not answer");
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("ETABS 23 joint mass: a field blank in every row is left out of the import (MMI X blank skipped the record, 0.4.57)", () =>
        {
            var (link, api, _) = CsiToolsTests.Model(CsiProgram.Etabs);
            const string key = "Joint Assignments - Additional Mass";
            api.Tables[key] = (["Story", "Label", "UniqueName", "MassXY", "MassZ", "MMIX", "MMIY", "MMIZ"], []);
            EditableTables.Put(link, key, "Story|Label", [new Dictionary<string, string> { ["Story"] = "Base", ["Label"] = "1", ["UniqueName"] = "3", ["MassXY"] = "1.5", ["MassZ"] = "0", ["MMIX"] = "", ["MMIY"] = "", ["MMIZ"] = "" }], "put the joint mass in");
            var (fields, rows) = api.Tables[key];
            Check(!fields.Contains("MMIX") && fields.Contains("MassXY") && fields.Contains("UniqueName"), "blank MMI fields left out: " + string.Join(",", fields));
            Check(rows.Single()[Array.IndexOf(fields, "MassXY")] == "1.5", "the mass went in");
        });

        yield return ("ETABS 23 shear wall design: no StartDesign in its API, so its own menu Design > Shear Wall Design > Start Design/Check is picked", () =>
        {
            Check(MenuCommand.Matches("&Start Design/Check...", "Start Design/Check") && MenuCommand.Matches("Shear &Wall Design", "Shear Wall Design") && !MenuCommand.Matches("Steel Frame Design", "Shear Wall Design"), "menu words without access keys or dots");
            var (link, _, inner) = CsiToolsTests.Model(CsiProgram.Etabs);
            inner.Locked = true;
            var (oldPid, oldPick) = (DesignResults.ProcessOf, DesignResults.PickMenu);
            var picked = new List<string>();
            try
            {
                DesignResults.ProcessOf = _ => 4242;
                DesignResults.PickMenu = (pid, path) => { picked.Add(pid + ": " + string.Join(" > ", path)); return null; };
                var r = DesignResults.RunDesign(link, ["shear_wall"]);
                Check(r["ran"]!.AsArray().Any(k => k!.GetValue<string>() == "shear_wall") && r["run_in_program"] is null, "ran by the menu: " + r.ToJsonString());
                Equal("4242: Design > Shear Wall Design > Start Design/Check", picked.Single(), "the program's own menu, in its own window");
                DesignResults.PickMenu = (_, _) => "the menu has no \"Shear Wall Design\" under \"Design\"";
                var no = DesignResults.RunDesign(link, ["shear_wall"]);
                Check(no["run_in_program"]!.ToJsonString().Contains("could not be picked either"), "why it could not: " + no.ToJsonString());
                DesignResults.ProcessOf = _ => null;
                Check(DesignResults.RunDesign(link, ["shear_wall"])["run_in_program"] is not null, "a stand-in has no window: listed with the menu");
            }
            finally { (DesignResults.ProcessOf, DesignResults.PickMenu) = (oldPid, oldPick); }
        });

        yield return ("ETABS 23 pack: step by step output through SetOutputOptionsForDisplay (no GetTableOutputOptionsForDisplay there), and set back", () =>
        {
            var (link, api, _) = CsiToolsTests.Model(CsiProgram.Etabs);
            var notes = new List<string>();
            var known = new List<TableName> { new("Base Reactions", "Base Reactions", false) };
            var status = PackTables.AddSteps(link, known, ["CPM-PUSH"], [], new JsonObject(), 100, notes, (n, d, r) => new JsonObject());
            var outcome = status?["outcome"]?.GetValue<string>() ?? "";
            Check(!outcome.StartsWith("not taken"), "taken: " + outcome);
            Check(api.Log.Contains("output options nonlinear static 2"), "nonlinear static set to step by step: " + string.Join(" | ", api.Log.TakeLast(4)));
            Check(api.DatabaseTables.NonlinearStaticOutput == 1, "and set back to envelopes");
        });

        yield return ("SAFE 20 tester: the model of a .NET Framework program is read through the plug-in's pipe, ref lists and enums included", () =>
        {
            var model = new CSiAPIv1.FakeModel();
            using var pipe = Served(model);
            Equal("cSapModel", pipe.RootType.Name, "the first object is the program's model");
            dynamic m = pipe.Root();
            Equal(@"E:\Pro max\LEVEL 29.FDB", (string)m.GetModelFilename(true), "the model's file, as SAFE 20 has it open");
            int n = 0; string[] names = [];
            Equal(0, (int)m.FrameObj.GetNameList(ref n, ref names), "the program's answer");
            Equal(3, n, "how many frames came back through the ref value");
            Equal("B1,B2,C1", string.Join(",", names), "the names came back through the ref list");
            // An enum the program takes and one it gives back, as numbers on CSI ProMax's side.
            int count = 0; int[] types = []; string[] selected = [];
            m.FrameObj.SetSelected("B2", true, 0);
            m.SelectObj.GetSelected(ref count, ref types, ref selected);
            Equal("B2", string.Join(",", selected), "what was selected through the pipe");
            Equal(2, types.Single(), "the program's enum came back as its number");
            // An optional parameter left out takes the program's own default.
            Equal(0, (int)m.FrameObj.SetSelected("C1", true), "a call with an optional parameter left out");
            Equal("C1", model.Frames.LastSelected, "it reached the program");
        });

        yield return ("SAFE 20 tester: the program's own error comes back as an error of that call, and the next call still works", () =>
        {
            using var pipe = Served(new CSiAPIv1.FakeModel());
            var link = new ApiLinkProbe(pipe);
            var call = ApiCall.Invoke(link.Root, "FrameObj.GetNameList");
            Check(ApiCall.Worked(call) && ApiCall.Value(call, "MyName") is JsonArray { Count: 3 }, "ApiCall reaches the program through the pipe: " + call.ToJsonString());
            try { ApiCall.Invoke(link.Root, "FrameObj.Delete", CsiTools.D(("Name", "no such frame"))); throw new Exception("no error came back"); }
            catch (EtabsException ex) { Check(ex.Message.Contains("There is no frame named no such frame", StringComparison.Ordinal), ex.Message); }
            Check(ApiCall.Worked(ApiCall.Invoke(link.Root, "FrameObj.GetNameList")), "the pipe still works after the program's error");
        });

        yield return ("SAFE 20 tester: a program that has gone is said as gone (the link attaches again), not as a hang", () =>
        {
            var pipe = Served(new CSiAPIv1.FakeModel());
            dynamic m = pipe.Root();
            pipe.Dispose();
            try { m.GetModelFilename(true); throw new Exception("a call on a closed pipe answered"); }
            catch (System.Runtime.InteropServices.COMException) { /* as COM says a closed program */ }
        });

        yield return ("kind: an API file built for the .NET Framework is told apart, and is never called through its own Helper", () =>
        {
            Check(!EtabsApi.RunsOnFramework(typeof(EtabsApi).Assembly.Location), "CsiProMax.dll (.NET 8) is not a .NET Framework file");
            Check(!EtabsApi.RunsOnFramework(typeof(CsiProMaxPlugin.ApiBridge).Assembly.Location), "the plug-in (.NET Standard) is not one either");
            Check(!EtabsApi.RunsOnFramework(Path.Combine(Path.GetTempPath(), "no such api.dll")), "a file that is not there is not one");
            var com = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/ComEtabs.cs"));
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(com.IndexOf("RunsOnFramework(apiFile)", StringComparison.Ordinal) is > 0 and var at && at < com.IndexOf("EtabsApi.Attach(apiFile", StringComparison.Ordinal),
                "the link asks whether the program runs on the .NET Framework before it calls the program's Helper");
            Check(scratch.IndexOf("RunsOnFramework(ApiFile)", StringComparison.Ordinal) is > 0 and var s && s < scratch.IndexOf("CreateObjectProgID", StringComparison.Ordinal),
                "the field test's scratch window does the same before CreateObjectProgID");
        });

        yield return ("SAFE plug-in still 0.4.5 under a 0.4.40 window: the loaded plug-in hands its work to the newest downloaded copy, on a Tools click and when a window asks", () =>
        {
            var built = typeof(CsiProMaxPlugin.Newest).Assembly.Location;
            var root = Path.Combine(Path.GetTempPath(), "cpm-newest-" + Guid.NewGuid().ToString("N"));
            string versions = Path.Combine(root, "versions"), installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(versions); Directory.CreateDirectory(installed);
            void Downloaded(string version, bool complete = true)
            {
                var folder = Path.Combine(versions, version);
                Directory.CreateDirectory(folder);
                File.Copy(built, Path.Combine(folder, CsiProMaxPlugin.Newest.FileName), true);
                if (complete) File.WriteAllText(Path.Combine(folder, ".complete"), version);
            }
            int Copies() => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "CsiProMaxPlugin");
            try
            {
                CsiProMaxPlugin.Newest.VersionsFolder = versions;
                CsiProMaxPlugin.Newest.Reset();
                var model = new CSiAPIv1.FakeModel();

                // Nothing newer downloaded: the installed plug-in does its own work.
                Downloaded("0.0.1"); Downloaded("98.0.0", complete: false); Directory.CreateDirectory(Path.Combine(versions, "97.0.0.del"));
                Check(CsiProMaxPlugin.Newest.Find(versions, new Version(0, 4, 0)) is null, "an older version, a half unpacked one and a folder being deleted are not taken");
                var before = Copies();
                Check(!CsiProMaxPlugin.Newest.Hand(model, installed), "with nothing newer the installed plug-in serves and starts the window itself");
                CsiProMaxPlugin.ApiBridge.Serve(model);
                using (var own = ApiPipe.ToPlugin(Environment.ProcessId, "SAFE 20", 5000) ?? throw new Exception("the installed plug-in's pipe did not answer"))
                    Equal(@"E:\Pro max\LEVEL 29.FDB", (string)((dynamic)own.Root()).GetModelFilename(true), "read through the installed plug-in");
                Equal(before, Copies(), "no copy was loaded");

                // An update arrives while the program is open and the window has switched to it: the window asks, the
                // plug-in hands the pipe to the new copy, the window connects again. No Tools click, nothing closed.
                Downloaded("99.0.0");
                Equal(new Version(99, 0, 0), CsiProMaxPlugin.Newest.Find(versions, new Version(0, 4, 43))!.Value.Version, "the newest complete version is found");
                using (var renewed = ApiPipe.ToPlugin(Environment.ProcessId, "SAFE 20", 5000) ?? throw new Exception("the renewed plug-in's pipe did not answer"))
                    Equal(@"E:\Pro max\LEVEL 29.FDB", (string)((dynamic)renewed.Root()).GetModelFilename(true), "read through the newer copy");
                Equal(before + 1, Copies(), "the newer copy of the plug-in was loaded into the open program");
                using (var again = ApiPipe.ToPlugin(Environment.ProcessId, "SAFE 20", 5000) ?? throw new Exception("the pipe did not answer the second window"))
                {
                    int n = 0; string[] frames = [];
                    Equal(0, (int)((dynamic)again.Root()).FrameObj.GetNameList(ref n, ref frames), "a second window is served by the same copy");
                }
                Equal(before + 1, Copies(), "the same version is not loaded twice");

                // A newer one still, on the next Tools click: the copy serves and opens the window from the installed folder.
                Downloaded("99.1.0");
                string? said = null;
                try { CsiProMaxPlugin.Newest.Hand(model, installed); } catch (IOException ex) { said = ex.Message; }
                Check(said is not null && said.Contains("CsiProMax.dll is missing from " + installed, StringComparison.Ordinal), "the copy starts the window from the installed folder, and what goes wrong is said: " + said);
                Equal(before + 2, Copies(), "the newest copy took over");
                // The installed copy was 0.4.7 and handed over as 0.4.7 knew how: the plug-in now starts the newest
                // downloaded version itself, and tells it where the installed files are.
                File.WriteAllText(Path.Combine(installed, "CsiProMax.dll"), "not an assembly: as old as can be");
                Check(CsiProMaxPlugin.Launcher.NewestApp(installed, versions) is null, "a version folder without CsiProMax.dll is not started");
                File.WriteAllText(Path.Combine(versions, "99.1.0", "CsiProMax.dll"), "");
                File.WriteAllText(Path.Combine(versions, "99.0.0", "CsiProMax.dll"), "");
                var app = CsiProMaxPlugin.Launcher.NewestApp(installed, versions);
                Equal(Path.Combine(versions, "99.1.0"), app, "the newest complete downloaded version is the one started");
                var dotnet = Path.Combine(root, "dotnet");
                Directory.CreateDirectory(dotnet);
                File.WriteAllText(Path.Combine(dotnet, CsiProMaxPlugin.Launcher.Windows ? "dotnet.exe" : "dotnet"), "");
                var start = CsiProMaxPlugin.Launcher.StartInfo(installed, "/x", 7, "SAFE", [dotnet], app);
                Check(start.Arguments.StartsWith("\"" + Path.Combine(versions, "99.1.0", "CsiProMax.dll") + "\" --etabs-pid 7 --program safe", StringComparison.Ordinal), "the downloaded version's file is started: " + start.Arguments);
                Check(start.Arguments.EndsWith(" --this-version --installed-in \"" + installed + "\"", StringComparison.Ordinal), "it is told it is the one to run, and where the installed files are: " + start.Arguments);
                var plain = CsiProMaxPlugin.Launcher.StartInfo(installed, "/x", 7, "SAFE", [dotnet]);
                Check(plain.Arguments.StartsWith("\"" + Path.Combine(installed, "CsiProMax.dll") + "\"", StringComparison.Ordinal) && !plain.Arguments.Contains("--this-version", StringComparison.Ordinal), "with nothing downloaded the installed copy starts as before");
                using var last = ApiPipe.ToPlugin(Environment.ProcessId, "SAFE 20", 5000) ?? throw new Exception("the newest plug-in's pipe did not answer");
                Equal(@"E:\Pro max\LEVEL 29.FDB", (string)((dynamic)last.Root()).GetModelFilename(true), "read through the newest copy");
            }
            finally
            {
                CsiProMaxPlugin.Newest.Reset();
                CsiProMaxPlugin.Newest.VersionsFolder = Path.Combine(root, "none");
                CsiProMaxPlugin.ApiBridge.Renewer = null;
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        });

        yield return ("SAFE plug-in still 0.4.5 under a 0.4.40 window: the installed plug-in files follow the running version, also while a program has them loaded", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cpm-refresh-" + Guid.NewGuid().ToString("N"));
            string from = Path.Combine(root, "versions", "0.4.43"), into = Path.Combine(root, "CSI ProMax");
            Directory.CreateDirectory(from); Directory.CreateDirectory(into);
            try
            {
                string[] names = ["CsiProMaxPlugin.dll", "CsiProMaxSafePlugin.dll", "CsiProMaxSapPlugin.dll"];
                foreach (var n in names) { File.WriteAllText(Path.Combine(from, n), "new " + n); File.WriteAllText(Path.Combine(into, n), "old " + n); }
                File.WriteAllText(Path.Combine(into, "CsiProMax.dll"), "installed window");
                File.WriteAllText(Path.Combine(from, "CsiProMax.dll"), "new window");
                Version Of(string file) => file.StartsWith(from, StringComparison.Ordinal) ? new Version(0, 4, 43) : new Version(0, 4, 5);
                IReadOnlyList<string> said;
                // As a program holds its plug-in file: readable, renamable, not writable.
                using (new FileStream(Path.Combine(into, names[1]), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    said = CsiProMax.Update.PlugInRefresh.Run(from, into, names, Of);
                Equal(3, said.Count(s => s.Contains("0.4.5 replaced by 0.4.43", StringComparison.Ordinal)), "all three are replaced: " + string.Join(" | ", said));
                foreach (var n in names) Equal("new " + n, File.ReadAllText(Path.Combine(into, n)), n + " is the new one");
                Equal("installed window", File.ReadAllText(Path.Combine(into, "CsiProMax.dll")), "nothing else in the installed folder is touched");
                if (OperatingSystem.IsWindows()) Check(Directory.GetFiles(into, "*.old-*").Length == 1, "the loaded file was moved aside, not overwritten");
                Equal(0, CsiProMax.Update.PlugInRefresh.Run(from, into, names, _ => new Version(0, 4, 43)).Count, "the same version is left alone");
                Equal(0, Directory.GetFiles(into, "*.old-*").Length, "the file moved aside is removed once the program let go of it");
                Equal(0, CsiProMax.Update.PlugInRefresh.Run(into, into, names, Of).Count, "the installed folder is never copied onto itself");
                var app = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Host/App.cs"));
                Check(app.Contains("RunsDownloadedVersion && CsiProMax.Assistant.ToolsMenu.InstallFolder()", StringComparison.Ordinal) && app.Contains("PlugInRefresh.Run(", StringComparison.Ordinal),
                    "the window does it at its start, and only a downloaded version does");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        });

        yield return ("one update, every part on one version: the installed copy the Modeler, the Viewer and the shortcuts start follows the running version, CsiProMax.dll last", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cpm-align-" + Guid.NewGuid().ToString("N"));
            string from = Path.Combine(root, "versions", "0.4.48"), into = Path.Combine(root, "CSI ProMax");
            Directory.CreateDirectory(Path.Combine(from, "web")); Directory.CreateDirectory(into);
            try
            {
                string[] plugIns = ["CsiProMaxPlugin.dll", "CsiProMaxSafePlugin.dll", "CsiProMaxSapPlugin.dll"];
                foreach (var n in plugIns.Concat(["CsiProMax.dll", "CsiProMax.deps.json", "Same.dll"]))
                {
                    File.WriteAllText(Path.Combine(from, n), n == "Same.dll" ? "same" : "new " + n);
                    File.WriteAllText(Path.Combine(into, n), n == "Same.dll" ? "same" : "old " + n);
                }
                File.WriteAllText(Path.Combine(from, "web", "viewer.html"), "new viewer");
                File.WriteAllText(Path.Combine(from, ".complete"), "0.4.48");
                File.WriteAllText(Path.Combine(into, "settings-of-mine.txt"), "kept");
                Version Of(string file) => file.StartsWith(from, StringComparison.Ordinal) || File.ReadAllText(file).StartsWith("new", StringComparison.Ordinal) ? new Version(0, 4, 48) : new Version(0, 4, 45);
                var before = CsiProMax.Update.PlugInRefresh.Aligned("0.4.48", into, null, plugIns, Of);
                Check(!before.Same && before.Text.Contains("this run 0.4.48", StringComparison.Ordinal) && before.Text.Contains("installed CsiProMax.dll 0.4.45", StringComparison.Ordinal), "an installed copy left behind is seen: " + before.Text);
                var stamp = File.GetLastWriteTimeUtc(Path.Combine(into, "Same.dll"));
                var said = CsiProMax.Update.PlugInRefresh.Everything(from, into, Of);
                Check(said is not null && said.Contains("is now 0.4.48 (was 0.4.45): 6 files written", StringComparison.Ordinal), "what was done is said: " + said);
                foreach (var n in plugIns.Concat(["CsiProMax.dll", "CsiProMax.deps.json"])) Equal("new " + n, File.ReadAllText(Path.Combine(into, n)), n);
                Equal("new viewer", File.ReadAllText(Path.Combine(into, "web", "viewer.html")), "files in folders too");
                Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(into, "Same.dll")), "a file with the same content is not written again");
                Check(!File.Exists(Path.Combine(into, ".complete")) && File.ReadAllText(Path.Combine(into, "settings-of-mine.txt")) == "kept", "the download mark is not copied, and what else is in the installed folder is kept");
                var after = CsiProMax.Update.PlugInRefresh.Aligned("0.4.48", into, "0.4.48", plugIns, Of);
                Check(after.Same, "every part is one version afterwards: " + after.Text);
                Check(CsiProMax.Update.PlugInRefresh.Everything(from, into, Of) is null, "a second time there is nothing to do");
                Check(CsiProMax.Update.PlugInRefresh.Everything(from, into, f => f.StartsWith(from, StringComparison.Ordinal) ? new Version(0, 4, 40) : new Version(0, 4, 48)) is null, "a newer installed copy is never put back");
                Check(!CsiProMax.Update.PlugInRefresh.Aligned("0.4.48", into, "0.4.49", plugIns, Of).Same, "a newer version downloaded and not yet in use is seen");
                var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Update/PlugInRefresh.cs"));
                Check(source.Contains("private static readonly string[] Last = [\"CsiProMax.deps.json\", \"CsiProMax.runtimeconfig.json\", \"CsiProMax.dll\"]", StringComparison.Ordinal), "CsiProMax.dll is copied last");
                var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/PcFieldSteps.cs"));
                Check(steps.Contains("every part of CSI ProMax on this PC is one version", StringComparison.Ordinal) && steps.Contains("PlugInRefresh.Aligned(", StringComparison.Ordinal), "the field test checks it on the PC");
                var program = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs"));
                Check(program[program.IndexOf("private static int RunViewer", StringComparison.Ordinal)..].Contains("Updater.CheckAsync(force: false)", StringComparison.Ordinal), "the Viewer on its own looks for updates too");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        });

        yield return ("kind: every plug-in serves its program's model on the pipe before it opens the window", () =>
        {
            foreach (var folder in new[] { "CsiProMax.Plugin", "CsiProMax.Plugin.Safe", "CsiProMax.Plugin.Sap" })
            {
                var text = File.ReadAllText(Path.Combine(SourceText.Root(), "src", folder, "cPlugin.cs"));
                var serve = text.IndexOf("ApiBridge.Serve(SapModel)", StringComparison.Ordinal);
                Check(serve > 0 && serve < text.IndexOf("Launcher.Start()", StringComparison.Ordinal), $"{folder}: ApiBridge.Serve(SapModel) before Launcher.Start()");
                var hand = text.IndexOf("Newest.Hand(SapModel", StringComparison.Ordinal);
                Check(hand > 0 && hand < serve, $"{folder}: a newer downloaded copy of the plug-in is asked first (Newest.Hand)");
                if (folder != "CsiProMax.Plugin")
                {
                    var project = File.ReadAllText(Path.Combine(SourceText.Root(), "src", folder, folder + ".csproj"));
                    Check(project.Contains("ApiBridge.cs", StringComparison.Ordinal) && project.Contains("ApiWire.cs", StringComparison.Ordinal), $"{folder} compiles the bridge in");
                    Check(project.Contains("Newest.cs", StringComparison.Ordinal) && project.Contains("Live.cs", StringComparison.Ordinal), $"{folder} compiles the hand-over in");
                }
            }
        });

        yield return ("kind: Windows PowerShell's line loads the plug-in by its bytes and quotes every path", () =>
        {
            var line = ApiPipe.HostCommand(@"C:\Users\O'Neil\CSI ProMax\CsiProMaxPlugin.dll", @"C:\Program Files\Computers and Structures\SAFE 20\SAFEv1.dll", "CSI.SAFE.API.ETABSObject", "CsiProMax.Api.host.x", "start", 0);
            Check(line.Contains("ReadAllBytes('C:\\Users\\O''Neil\\CSI ProMax\\CsiProMaxPlugin.dll')", StringComparison.Ordinal), "the plug-in's path, with its quote doubled: " + line);
            Check(line.Contains("'C:\\Program Files\\Computers and Structures\\SAFE 20\\SAFEv1.dll'", StringComparison.Ordinal), "the API file's path");
            Check(line.Contains("GetType('CsiProMaxPlugin.ApiBridge').GetMethod('Host')", StringComparison.Ordinal), "the bridge's Host");
            Check(!line.Contains("Add-Type", StringComparison.Ordinal), "Add-Type -Path loads every type of the file, and the plug-in's need the program's API: never used");
        });

        yield return ("SAFE 20 tester: Tools > CSI ProMax is not failed in the very program whose Tools menu opened the window", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cpm-menu-" + Guid.NewGuid().ToString("N")[..8]);
            var install = Path.Combine(root, "CSI ProMax");
            var settings = Path.Combine(root, "Computers and Structures");
            Directory.CreateDirectory(install);
            var before = ToolsMenu.OpenedFrom;
            try
            {
                foreach (var plugIn in ToolsMenu.PlugIns) File.WriteAllText(Path.Combine(install, plugIn.Dll), "");
                const string none = "[PlugIn]\r\n NumberPlugIns=0\r\n";
                Directory.CreateDirectory(Path.Combine(settings, "SAFE 20"));
                Directory.CreateDirectory(Path.Combine(settings, "ETABS 20"));
                File.WriteAllText(Path.Combine(settings, "SAFE 20", "SAFE.ini"), none);
                File.WriteAllText(Path.Combine(settings, "ETABS 20", "ETABS.ini"), none);
                Equal("SAFE 20", ToolsMenu.LabelOfFolder(@"C:\Program Files\Computers and Structures\SAFE 20"), "the program's folder names its version");
                Equal("SAP2000 24", ToolsMenu.LabelOfFolder(@"C:\Program Files\Computers and Structures\sap2000 24\"), "any case");
                Equal(null, ToolsMenu.LabelOfFolder(@"C:\Program Files\Something Else"), "another folder");
                ToolsMenu.OpenedFrom = "SAFE 20";
                var entries = ToolsMenu.Repair(install, settings, (_, _) => true);
                var safe = entries.Single(e => e.Label == "SAFE 20");
                Equal(ToolsMenu.State.Running, safe.State, "SAFE 20, whose Tools menu opened this window");
                Check(safe.Listed && safe.Says.Contains("this window was opened from it", StringComparison.Ordinal), safe.Says);
                Equal(none, File.ReadAllText(Path.Combine(settings, "SAFE 20", "SAFE.ini")), "its file is left alone while it is open");
                Equal(ToolsMenu.State.Open, entries.Single(e => e.Label == "ETABS 20").State, "another open program that does not list it is still said");
            }
            finally { ToolsMenu.OpenedFrom = before; Directory.Delete(root, true); }
        });

        yield return ("SAFE 20 tester: the chat opens a SAFE model (.FDB) in SAFE, asks first, and refuses another program's file", () =>
        {
            var tool = CsiTools.For(CsiProgram.Safe).Single(t => t.Name == "open_model");
            Equal(Ask.Always, tool.Ask, "opening closes the model open now, so the engineer is always asked");
            Check(CsiTools.For(CsiProgram.Etabs).Any(t => t.Name == "open_model") && CsiTools.For(CsiProgram.Sap2000).Any(t => t.Name == "open_model"), "every program has it");
            Check(CsiTools.ModelFiles(CsiProgram.Safe).Contains(".fdb") && CsiTools.ModelFiles(CsiProgram.Etabs).Contains(".edb") && CsiTools.ModelFiles(CsiProgram.Sap2000).Contains(".sdb"), "each program's own model file");
            var folder = Path.Combine(Path.GetTempPath(), "cpm-open-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            try
            {
                var fdb = Path.Combine(folder, "LEVEL 29.FDB");
                File.WriteAllText(fdb, "");
                var (link, api) = OpenProbe(CsiProgram.Safe, @"C:\Models\Untitled");
                var answer = tool.Invoke(JsonDocument.Parse(JsonSerializer.Serialize(new { path = fdb })).RootElement, link);
                Equal(fdb, api.Opened, "SAFE was asked to open the .FDB, upper case and all");
                Equal("LEVEL 29.FDB", answer["opened"]?.GetValue<string>(), "the answer names it");
                Check(tool.Summary!(JsonDocument.Parse(JsonSerializer.Serialize(new { path = fdb })).RootElement).Contains("changes not saved are lost", StringComparison.Ordinal), "the question says what is lost");
                var edb = Path.Combine(folder, "Tower.edb");
                File.WriteAllText(edb, "");
                try { tool.Invoke(JsonDocument.Parse(JsonSerializer.Serialize(new { path = edb })).RootElement, OpenProbe(CsiProgram.Safe).Link); throw new Exception(".edb was opened in SAFE"); }
                catch (ToolInputException ex) { Check(ex.Message.Contains("open it in ETABS", StringComparison.Ordinal), ex.Message); } // ETABS itself
                var (again, againApi) = OpenProbe(CsiProgram.Safe, Path.ChangeExtension(fdb, ".fdb"));
                Check(tool.Invoke(JsonDocument.Parse(JsonSerializer.Serialize(new { path = fdb })).RootElement, again)["note"] is not null && againApi.Opened is null, "a model that is already open is not opened again");
            }
            finally { Directory.Delete(folder, true); }
            var connector = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Bridge/RevitTargets.cs"));
            Check(!connector.Contains("EndsWith(\".rvt\"", StringComparison.Ordinal), "the chat's open request takes the program's own model files, not Revit's");
        });

        yield return ("SAFE 20 tester (0.4.51): SAFE 20 builds the Modeler's model from the .f2k through the API in a blank model, so its editing tools are tested too", () =>
        {
            var m = Templates.ConcreteFrame("T", [6, 6], [5], 3, 3.5);
            m.AddCombo("1.2D+1.6L", ("Dead", 1.2), ("Live", 1.6));
            var written = F2kWriter.Write(m, new F2kOptions(Story: "Story2"));
            Equal(6, CsiProMax.Assistant.Safe.F2kRebuild.UnitsOf(written.Text), "the blank model takes the file's units (kN, m)");
            var api = new ApiRecorder();
            var memory = new MemoryEtabs { Program = CsiProgram.Safe };
            var link = new SafeMemory(memory, program: CsiProgram.Safe) { Api = api };
            var built = CsiProMax.Assistant.Safe.F2kRebuild.Build(link, written.Text);
            Equal(written.Slabs, built["slabs"]!.GetValue<int>(), "every slab of the file");
            Equal(written.Beams, built["beams"]!.GetValue<int>(), "every beam");
            Equal(written.Columns, built["columns"]!.GetValue<int>(), "every column below and above");
            Equal(written.Slabs, link.Areas().Count(a => a.Kind == "floor"), "the slabs are floors in SAFE");
            Equal(written.Beams, link.Frames().Count(f => f.Kind == "beam"), "the beams are beams in SAFE");
            Equal(written.Supports, built["supports"]!.GetValue<int>(), "the supports at the columns' far ends");
            Check(link.Points().Count(p => link.Restraint(p.Name).Take(3).All(r => r)) == written.Supports, "the restrained points are held");
            Check(link.LoadPatterns().Any(p => p.Name == "Dead") && link.LoadPatterns().Any(p => p.Name == "Live"), "Dead and Live");
            Check(link.Combos().Contains("1.2D+1.6L"), "the combination");
            Equal(written.Loads, built["loads"]!.GetValue<int>(), "every surface load");
            Check(memory.Loads.Count > 0, "the loads reached the slabs");
            Check(api.Calls.Any(c => c.StartsWith("SetSlab(", StringComparison.Ordinal)) && api.Calls.Any(c => c.StartsWith("SetRectangle(", StringComparison.Ordinal)) && api.Calls.Any(c => c.StartsWith("SetMaterial(", StringComparison.Ordinal)),
                "the materials, slab and frame properties are defined first: " + string.Join(", ", api.Calls.Take(8)));
            Equal(written.Strips, built["strips_in_file"]!.GetValue<int>(), "the strips of the file are counted, to go in through the table");
            Equal("Super Dead", CsiProMax.Assistant.Safe.F2kRebuild.PatternType("SUPER DEAD"), "a .f2k pattern type");
            Equal("Other", CsiProMax.Assistant.Safe.F2kRebuild.PatternType("SOMETHING"), "an unknown one");

            Check(CsiProMax.Assistant.ScratchProgram.BuildsThroughApi(CsiProgram.Safe, @"C:\t\cpm-field-test.F2K"), "SAFE builds a .f2k");
            Check(!CsiProMax.Assistant.ScratchProgram.BuildsThroughApi(CsiProgram.Etabs, @"C:\t\cpm-field-test.e2k"), "only SAFE's file is built");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.Contains("F2kRebuild.Build(Link, text)", StringComparison.Ordinal) && scratch.IndexOf("F2kRebuild.Build(Link, text)", StringComparison.Ordinal) > scratch.IndexOf("the link reads the scratch model, and no other", StringComparison.Ordinal),
                "the model is built only once the link is checked to be the scratch model's");
            var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Safe/SafeFieldSteps.cs"));
            Check(steps.Contains("if (built && !ctx.Facts.ContainsKey(\"safe.strips_built\"))", StringComparison.Ordinal), "the strips are compared when they went in, and said as not tested when they did not");
        });

        yield return ("SAFE 23.1 tester (0.4.56): an .f2k SAFE says it opened but holds nothing of is built through the API; tables of an unsaved model; Viewer items SAFE has none of; chat_questions; failure kinds", () =>
        {
            var m = Templates.ConcreteFrame("T", [6, 6], [5], 3, 3.5);
            var written = F2kWriter.Write(m, new F2kOptions(Story: "Story2"));
            Check(CsiProMax.Assistant.Safe.F2kRebuild.ObjectCount(written.Text) > 0, "the Modeler's .f2k holds objects");
            Check(CsiProMax.Assistant.ScratchProgram.OpenedEmpty(written.Text, 0), "SAFE holding none of them is an empty open");
            var control = CsiTableText.Parse(written.Text)["PROGRAM CONTROL"].Single();
            Equal("Standard", control["ProgLevel"], "a model with no tendons asks for SAFE Standard, as SAFE 23.1's own export does");
            Equal("Post Tensioning", CsiProMax.F2k.F2kWriter.ProgLevel(1), "one with tendons for Post Tensioning");
            Check(!written.Text.Contains("OBJECT GEOMETRY", StringComparison.Ordinal), "none of SAFE 16's tables, which SAFE 23.1 opened as an empty model");
            Check(!CsiProMax.Assistant.ScratchProgram.OpenedEmpty(written.Text, 7), "SAFE holding some is not");
            Check(!CsiProMax.Assistant.ScratchProgram.OpenedEmpty("TABLE:  \"PROGRAM CONTROL\"\n   ProgramName=SAFE\n", 0), "a file with no objects is not");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.Contains("if (openedEmpty)", StringComparison.Ordinal) && scratch.IndexOf("if (openedEmpty)", StringComparison.Ordinal) < scratch.IndexOf("F2kRebuild.Build(Link, text)", StringComparison.Ordinal),
                "an empty open goes on to the blank model and the build");

            // GetAllTables refused (an unsaved SAFE 23.1 model): the tables with data come from GetAvailableTables.
            var listed = CsiProMax.Assistant.Etabs.ComEtabs.ListTables(new CpmFakes.TableListApi(1, 0), "SAFE");
            Equal(2, listed.Count, "the tables with data are listed");
            Equal("Joint Reactions", listed[1].Name, "by their names");
            Check(listed.All(t => !t.Empty), "and they have data");
            Equal(3, CsiProMax.Assistant.Etabs.ComEtabs.ListTables(new CpmFakes.TableListApi(0, 1), "SAFE").Count, "GetAllTables is used when it works");
            try { CsiProMax.Assistant.Etabs.ComEtabs.ListTables(new CpmFakes.TableListApi(1, 2), "SAFE"); throw new Exception("no table list was refused"); }
            catch (CsiProMax.Assistant.Etabs.EtabsException ex) { Check(ex.Message.Contains("GetAllTables answered 1, GetAvailableTables 2", StringComparison.Ordinal), ex.Message); }

            // By the program's own table list (SAFE 23.1's, as the 0.4.56 run listed it): modal and section cuts are there, so they are tested.
            string[] safe23 = ["Modal Case Definitions - Eigen", "Modal Periods And Frequencies", "Section Cut Definitions", "Section Cut Forces - Analysis", "Pier Section Properties", "Spandrel Section Properties", "Load Case Definitions - Nonlinear Static", "Imported Load Cases - Response Spectrum", "Joint Reactions"];
            string? Not(string item, string[] tables) => CsiProMax.Assistant.ViewerCheck.NotInProgram(tables, "SAFE", item);
            Check(Not("menu: Hinge Results...", safe23) is not null && Not("menu: Pier and Spandrel Forces...", safe23) is not null && Not("menu: Static Pushover Curve...", safe23) is not null, "SAFE 23 lists no hinge, pier force or pushover tables");
            Check(Not("menu: Mode Shapes...", safe23) is null && Not("menu: Modal Results Plot...", safe23) is null && Not("menu: Section Cut Forces...", safe23) is null && Not("menu: Response Spectrum Curves...", safe23) is null, "SAFE 23's modal, section cut and spectrum tables are tested, never taken as absent");
            Check(Not("menu: Frame Forces and Moments...", safe23) is null && Not("button: Show design ratios", safe23) is null, "what SAFE has is tested");
            Check(Not("menu: Hinge Results...", []) is null, "with no table list nothing is taken as absent");
            var extras = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Safe/SafeFieldSteps.cs"));
            Check(extras.Contains("ScratchExtras.AddSafe(ctx)", StringComparison.Ordinal) && extras.Contains("ScratchExtras.CheckSafe(ctx)", StringComparison.Ordinal), "SAFE's scratch model gets a modal case and a section cut, checked after the analysis");
            Check(scratch.Contains("opens the Modeler's {Path.GetExtension(modelFile)} with all its objects", StringComparison.Ordinal), "an empty open is a failure of its own, not hidden by the build");

            var once = CsiProMax.Assistant.SkipAudit.Failure("the selection has only one kind of object");
            Equal(CsiProMax.Assistant.SkipAudit.Why.Unjustified, CsiProMax.Assistant.SkipAudit.Of(once), "a failure is not taken for a licence one by its closing words");
            Equal(CsiProMax.Assistant.SkipAudit.Why.Licence, CsiProMax.Assistant.SkipAudit.Of("no licence for SAFE"), "a real licence reason still holds");

            var panel = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/PanelFieldSteps.cs"));
            Check(panel.Contains("ChatQuestionsTool.Answer(Args(json), fromChat: true", StringComparison.Ordinal), "chat_questions is asked the chat's way when a window is open");
        });

        yield return ("SAFE 20 (0.4.51): the design strips its API cannot draw go in through its 'Strip Object Connectivity' table, in the fields SAFE lists", () =>
        {
            var m = Templates.ConcreteFrame("T", [6, 6], [5], 3, 3.5);
            var written = F2kWriter.Write(m, new F2kOptions(Story: "Story2"));
            var points = CsiProMax.Assistant.Safe.F2kRebuild.StripPoints(CsiTableText.Parse(written.Text));
            Check(points.Count >= 2, "the file has strips");
            string[] safe = ["Name", "Layer", "Point", "GlobalX", "GlobalY", "WB Left", "WB Right", "WA Left", "WA Right", "AutoWiden", "GUID"];
            var rows = CsiProMax.Assistant.Safe.F2kRebuild.StripRows(written.Text, safe);
            Equal(points.Count, rows.Count, "one row per strip point");
            Equal(points[0]["Strip"], rows[0]["Name"], "the strip's name");
            Equal(points[0]["GlobalX"], rows[0]["GlobalX"], "its place");
            Equal(points[0]["WBLeft"], rows[0]["WB Left"], "a width, to a field named with a space");
            Check(!rows[0].ContainsKey("GUID"), "a field the file has nothing for is left to SAFE");
            var other = CsiProMax.Assistant.Safe.F2kRebuild.StripRows(written.Text, ["Strip", "X", "Y"]);
            Equal(points[0]["GlobalY"], other[0]["Y"], "other names for the same fields");
            try { CsiProMax.Assistant.Safe.F2kRebuild.StripRows(written.Text, ["Name", "Layer"]); throw new Exception("strips were written with no place"); }
            catch (EtabsException ex) { Check(ex.Message.Contains("Its fields: Name, Layer", StringComparison.Ordinal), "SAFE's fields are named, for the next round: " + ex.Message); }

            var api = new CpmFakes.StripTableApi(safe);
            var link = new SafeMemory(new MemoryEtabs { Program = CsiProgram.Safe }, program: CsiProgram.Safe) { Api = api };
            var listed = CsiProMax.Assistant.Safe.F2kRebuild.BuildStrips(link, written.Text);
            Equal(points.Count, api.Written.Count, "every strip point went into the table");
            Equal(points.Select(p => p["Strip"]).Distinct().Count(), listed, "SAFE lists each strip once");
            Check(api.Applied, "the table edit was applied");
            Equal(safe.Length, api.Fields.Length, "the rows carry SAFE's own fields");
        });

        yield return ("Ahmed 2026-11-26: SAFE's part leaves nothing as not tested: strips, table add and update, slab design and soil are checked or failed", () =>
        {
            var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Safe/SafeFieldSteps.cs"));
            Check(!steps.Contains("ctx.Skip(", StringComparison.Ordinal) && !steps.Contains("FieldSkipException", StringComparison.Ordinal), "no step of SAFE's part is said as not tested");
            Check(steps.Contains("TableEdits(ctx, link, slabTable)", StringComparison.Ordinal) && steps.Contains("\"action\"] = \"update\"", StringComparison.Ordinal) && steps.Contains("\"action\":\"add\"", StringComparison.Ordinal), "edit_safe_table's add and update are run");
            Check(steps.Contains("Spring Property Definitions - Area Springs", StringComparison.Ordinal), "the soil is read from SAFE's area spring table when its API does not list it");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.Contains("ctx.Fail($\"start a second {name} window\"", StringComparison.Ordinal), "a file the program can neither open nor have built is a failure, not a skip");
        });

        yield return ("tester (0.4.53): an ETABS-only field test started from the SAFE window is named and headed ETABS, not SAFE; for all three programs", () =>
        {
            foreach (var window in CsiProgram.All)
            {
                foreach (var only in CsiProgram.All)
                {
                    Equal(only.Name, FieldTest.ReportName(window, only), $"{only.Name} alone from the {window.Name} window");
                    var line = FieldTest.TestedLine(window, only)!;
                    Check(line.StartsWith($"Program tested: {only.Name} alone", StringComparison.Ordinal), line);
                    Check(window.Key == only.Key || line.Contains($"started from the CSI ProMax window of {window.Name}", StringComparison.Ordinal), "the window it ran from is said: " + line);
                }
                Equal(window.Name, FieldTest.ReportName(window, null), "every program tested: named for the window");
                Equal(null, FieldTest.TestedLine(window, null), "no line when every program was tested");
            }
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(!source.Contains("$\"Field test {link.Program.Name}", StringComparison.Ordinal) && source.Contains("$\"Field test {ReportName(link.Program, o.OnlyProgram)}", StringComparison.Ordinal), "the report file is named by ReportName");
            Check(source.Contains("\"Field test\", $\"{ReportName(link.Program, o.OnlyProgram)}", StringComparison.Ordinal), "the work folder too");
        });

        yield return ("ETABS 20 tester (0.4.53): results tools answer 'no analysis' on a model that is not analysed, in every program", () =>
        {
            foreach (var program in CsiProgram.All)
            {
                var link = new MemoryEtabs { Program = program };
                foreach (var (tool, key) in new[] { ("joint_displacements", "list"), ("frame_forces", "list"), ("base_reactions", "base_reactions"), ("modal_results", "modes") })
                {
                    var t = CsiProMax.Assistant.Sap2000.SapTools.All.Single(x => x.Name == tool);
                    var answer = t.Invoke(JsonDocument.Parse("{}").RootElement, link);
                    Check(answer["analysed"]?.GetValue<bool>() == false && answer[key] is JsonArray { Count: 0 } && answer["note"]!.GetValue<string>().Contains("run_analysis", StringComparison.Ordinal), $"{program.Name} {tool}: " + answer.ToJsonString());
                }
            }
        });

        yield return ("ETABS 20 tester (0.4.53): an analysis the program did not finish is a failure that quotes its analysis log", () =>
        {
            var lines = new[] { "BEGIN ANALYSIS", "  NUMBER OF JOINTS = 40", "* * * E R R O R * * *", "STRUCTURE IS UNSTABLE OR ILL-CONDITIONED", "ANALYSIS TERMINATED", "" };
            var said = CsiProMax.Assistant.Tools.EtabsTools.AnalysisTrouble(lines);
            Check(said.Contains("STRUCTURE IS UNSTABLE OR ILL-CONDITIONED") && said.Contains("ANALYSIS TERMINATED") && !said.Contains("BEGIN ANALYSIS"), string.Join(" | ", said));
            Equal(2, CsiProMax.Assistant.Tools.EtabsTools.AnalysisTrouble(["a", "b"]).Count, "a log with no problem gives its last lines");
            var folder = Path.Combine(Path.GetTempPath(), "cpm-log-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllLines(Path.Combine(folder, "scratch.LOG"), lines);
                var text = CsiProMax.Assistant.Tools.EtabsTools.AnalysisLog(Path.Combine(folder, "scratch.EDB"));
                Check(text.Contains("scratch.LOG says", StringComparison.Ordinal) && text.Contains("UNSTABLE", StringComparison.Ordinal), text);
                Check(CsiProMax.Assistant.Tools.EtabsTools.AnalysisLog(Path.Combine(folder, "other.EDB")).Contains("No analysis log", StringComparison.Ordinal), "no log is said");
            }
            finally { Directory.Delete(folder, true); }
            var tools = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/EtabsTools.cs"));
            Check(tools.Contains("(_, e) => RunAnalysis(e)", StringComparison.Ordinal), "run_analysis checks that the model is analysed");
        });

        yield return ("ETABS 20 tester (0.4.53): a table import's log is quoted from its error lines, not cut off in its options", () =>
        {
            var log = "********** DATABASE IMPORT DEFINITION DATA **********\nETABS Version 20.0.0 Interactive Database Import\nImport Type and Options:\n" + string.Concat(Enumerable.Repeat("  Elements Deleted from DB:  Delete object from model\n", 20))
                + "TABLE:  \"Load Pattern Definitions - Auto Seismic - UBC 97\"\n  ERROR: Field 'Ct' is not valid for this table\nNumber of errors: 1\n";
            var cut = CsiProMax.Assistant.Etabs.EditableTables.LogCut(log);
            Check(cut.StartsWith("ERROR: Field 'Ct' is not valid", StringComparison.Ordinal), cut);
            Equal("", CsiProMax.Assistant.Etabs.EditableTables.Trouble("all fine\nNumber of errors: 0"), "a clean log has no trouble lines");
            // 0.4.55 ETABS run: the reason is on the lines after "*** Error Message ***", and was cut off.
            var etabs = "********** DATABASE IMPORT DEFINITION DATA **********\nETABS Version 23.2.0 Interactive Database Import\n" + string.Concat(Enumerable.Repeat("  Option line that says nothing\n", 30))
                + "*** Error Message ***\n  Table: Load Pattern Definitions - Auto Seismic - KDS 41 17 00:2019, Field name: SITE CLASS, Record: 1\n  Error reading field SiteClass; field ignored\n*** Summary ***\nNumber of errors: 1\n";
            var etabsCut = CsiProMax.Assistant.Etabs.EditableTables.LogCut(etabs);
            Check(etabsCut.StartsWith("Table: Load Pattern Definitions - Auto Seismic - KDS 41 17 00:2019, Field name: SITE CLASS, Record: 1 Error reading field SiteClass", StringComparison.Ordinal), etabsCut);
        });

        yield return ("ETABS 23 (0.4.55 run): a code's table goes back without the fields ETABS works out, User Loads has its load set, and Loads to Diaphragms/Stories are no codes", () =>
        {
            var tables = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/EditableTables.cs"));
            Check(tables.Contains("!s.ok", StringComparison.Ordinal) && tables.Contains("importable.Any(x => x)", StringComparison.Ordinal), "Put leaves out the fields the table does not import (TL, Fa, Fv, SDS, SD1 of KDS)");
            var lateral = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/LateralTools.cs"));
            Check(lateral.Contains("(\"NumberSets\", \"1\"), (\"SetNumber\", \"1\")", StringComparison.Ordinal), "User Loads gets one load set");
            Check(lateral.Contains("x.t.Import != 0", StringComparison.Ordinal) && lateral.Contains("\"Loads to Diaphragms\"", StringComparison.Ordinal) && lateral.Contains("\"Loads to Stories\"", StringComparison.Ordinal), "the read-only tables are not offered as codes");
        });

        yield return ("ETABS 20 tester (0.4.53): no SetNumberModes and no SectCut in its API: the modal case and section cut tables take them", () =>
        {
            var api = new CpmFakes.OldEtabsApi();
            var link = new SafeMemory(new MemoryEtabs { Program = CsiProgram.Etabs }, program: CsiProgram.Etabs)
            {
                Api = api,
                Listed = [new("Modal Case Definitions - Eigen", "Modal Case Definitions - Eigen", false), new("Section Cut Definitions", "Section Cut Definitions", false), new("Modal Participating Mass Ratios", "Modal Participating Mass Ratios", true)],
            };
            var how = CsiProMax.Assistant.Tools.ApiWorkarounds.SetNumberModes(link, "ModalEigen", "CPM-MODAL", 12, 1);
            Check(how.Contains("Modal Case Definitions - Eigen", StringComparison.Ordinal), how);
            var row = api.Rows["Modal Case Definitions - Eigen"].Single();
            Check(row.Contains("CPM-MODAL") && row.Contains("12") && row.Contains("1"), "the modes went into the table: " + string.Join(",", row));
            how = CsiProMax.Assistant.Tools.ApiWorkarounds.SectionCutByGroup(link, "CPM-CUT", "CPM-GROUP");
            Check(how.Contains("Section Cut Definitions", StringComparison.Ordinal), how);
            var cut = api.Rows["Section Cut Definitions"].Single();
            Check(cut.Contains("CPM-CUT") && cut.Contains("Group") && cut.Contains("CPM-GROUP"), string.Join(",", cut));
            Check(CsiProMax.Assistant.Tools.ApiWorkarounds.IsMissing(new EtabsException("ETABS could not define a section cut by the group: cSapModel has no 'SectCut'. Its parts: ...")), "ETABS 20's own words for a missing part");
            Check(!CsiProMax.Assistant.Tools.ApiWorkarounds.IsMissing(new EtabsException("ETABS refused to set the modes (answered 1).")), "a refusal is not a missing call");
            var grab = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Tools/ScreenGrab.cs"));
            Check(grab.Contains("WhileShown(main", StringComparison.Ordinal) && !grab.Contains("restore its window first.\");", StringComparison.Ordinal), "a minimized window is shown for its picture, not refused");
        });

        yield return ("SAFE 20 tester (0.4.47): SAFE 20 is never given the Modeler's .f2k, which it read as a packed model ('Error decompressing file')", () =>
        {
            Check(!CsiProMax.Assistant.ScratchProgram.OpensThroughApi(CsiProgram.Safe, @"C:\t\cpm-field-test.f2k", onFramework: true), "SAFE 20/21 and a .f2k: it is not opened (the model is built instead)");
            Check(CsiProMax.Assistant.ScratchProgram.OpensThroughApi(CsiProgram.Safe, @"C:\t\cpm-field-test.FDB", onFramework: true), "SAFE 20/21 and its own .fdb");
            Check(CsiProMax.Assistant.ScratchProgram.OpensThroughApi(CsiProgram.Safe, @"C:\t\cpm-field-test.f2k", onFramework: false), "SAFE 22 and later keep opening the .f2k as before");
            foreach (var p in new[] { CsiProgram.Etabs, CsiProgram.Sap2000 })
                Check(CsiProMax.Assistant.ScratchProgram.OpensThroughApi(p, @"C:\t\cpm-field-test" + (p == CsiProgram.Etabs ? ".e2k" : ".s2k"), onFramework: true), $"{p.Name} on the .NET Framework still opens its text file");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.IndexOf("OpensThroughApi(_program, modelFile", StringComparison.Ordinal) is > 0 and var at && at < scratch.IndexOf("ApiPipe.ToPowerShell(_program, ApiFile, \"start\"", StringComparison.Ordinal),
                "the check comes before any window is started");
        });

        yield return ("SAP2000 26 field test (0.4.54, experiment N): the Modeler's .s2k is turned into an .sdb in one SAP2000, and the test works in a new one (an .s2k opened in a process makes it refuse every table import)", () =>
        {
            var own = CsiProMax.Assistant.ScratchProgram.OpensTextInOwnProcess;
            Check(own(CsiProgram.Sap2000, @"C:\t\cpm-field-test.s2k") && own(CsiProgram.Sap2000, @"C:\t\cpm-field-test.S2K") && own(CsiProgram.Sap2000, @"C:\t\cpm-field-test.$2k"), "SAP2000 and its text files");
            Check(!own(CsiProgram.Sap2000, @"C:\t\cpm-field-test-1a2b3c.sdb"), "SAP2000's own .sdb opens in the window the test works in");
            Check(!own(CsiProgram.Etabs, @"C:\t\cpm-field-test.e2k") && !own(CsiProgram.Safe, @"C:\t\cpm-field-test.f2k"), "ETABS and SAFE take table imports after their text files (their field tests' table steps pass)");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            var start = scratch.IndexOf("public static IScratchWindow? Start(", StringComparison.Ordinal);
            var turn = scratch.IndexOf("OpensTextInOwnProcess(program, modelFile)", start, StringComparison.Ordinal);
            var second = scratch.IndexOf("var scratch = new ScratchProgram(program)", start, StringComparison.Ordinal);
            Check(start > 0 && turn > start && second > turn, "the text file is turned into a model file before the test's own window starts");
            Check(scratch.IndexOf("convertOnly: true", turn, StringComparison.Ordinal) is var c and > 0 && c < second && scratch.IndexOf("first.Close()", turn, StringComparison.Ordinal) is var closed and > 0 && closed < second
                  && scratch.IndexOf("modelFile = converted;", turn, StringComparison.Ordinal) is var swap and > 0 && swap < second, "the first SAP2000 only saves the .sdb, is closed, and the second opens that .sdb");
            Check(scratch.IndexOf("ModelPath = target;\r\n        if (convertOnly) return true;", StringComparison.Ordinal) > 0 || scratch.IndexOf("ModelPath = target;\n        if (convertOnly) return true;", StringComparison.Ordinal) > 0, "the first window stops once the .sdb is saved: no change is made in it");
            // The engineer's own SAP2000: nothing to do for them (Ahmed: "the user does not feel anything"). Only when the
            // second SAP2000 cannot do it either is the refusal said, with its cause and the way out.
            Check(EditableTables.NoImportAfterText.Contains(".s2k", StringComparison.Ordinal) && EditableTables.NoImportAfterText.Contains("close SAP2000", StringComparison.Ordinal), "a refusal that could not be got round says the cause and the way out");
            Check(SapFreshProcess.Said(@"C:\m\Tower.sdb").Contains("nothing is needed from the engineer", StringComparison.Ordinal), "a change made in a second SAP2000 asks nothing of the engineer");
            var tables = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/EditableTables.cs"));
            Check(tables.Contains("with no log), so it cannot {what}. {NoImportAfterText}", StringComparison.Ordinal), "set_lateral_load's refused import says it");
            var tools = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/CsiTools.cs"));
            Check(tools.Contains("EditableTables.NoImportAfterText", StringComparison.Ordinal), "edit_table's refused import says it");

            // The tools get round it themselves: the same import in a second SAP2000, on the file saved just before.
            Check(SapFreshProcess.IsSessionRefusal(CsiProgram.Sap2000, "SAP2000 did not run the import of X's ASCE 7-22 (it answered 1, with no log), so it cannot give X it. " + EditableTables.NoImportAfterText + " Patterns after: 0 of 10"), "set_lateral_load's refusal is the one a second SAP2000 gets round");
            Check(SapFreshProcess.IsSessionRefusal(CsiProgram.Sap2000, "SAP2000 refused to apply the edit of T (answer 1). " + EditableTables.NoImportAfterText.TrimEnd('.') + ". The model was opened again as it was saved just before the edit (m.sdb)"), "edit_table's refusal too");
            Check(!SapFreshProcess.IsSessionRefusal(CsiProgram.Sap2000, "SAP2000 did not apply X's KDS (0 fatal, 2 errors). Its import log: ..."), "a refusal with a log is the rows' fault: no second SAP2000");
            Check(!SapFreshProcess.IsSessionRefusal(CsiProgram.Etabs, EditableTables.NoImportAfterText), "never for ETABS");
            var lateral = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/LateralTools.cs"));
            Check(lateral.IndexOf("SapFreshProcess.IsSessionRefusal(e.Program, ex.Message)", StringComparison.Ordinal) is var r and > 0
                  && lateral.IndexOf("SapFreshProcess.Apply(e, saved, fresh => EditableTables.PutAutoLoad(fresh,", r, StringComparison.Ordinal) > r
                  && lateral.Contains("[\"second_sap2000\"] = second", StringComparison.Ordinal), "set_lateral_load makes the change in a second SAP2000, after opening the saved file again, and says so");
            Check(tools.IndexOf("SapFreshProcess.IsSessionRefusal(e.Program, ex.Message)", StringComparison.Ordinal) is var t and > 0
                  && tools.IndexOf("SapFreshProcess.Apply(e, savedAs,", t, StringComparison.Ordinal) > t && tools.Contains("log[\"second_sap2000\"] = second", StringComparison.Ordinal), "edit_table too");
            var fresh = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/SapFreshProcess.cs"));
            Check(fresh.IndexOf("m.File.NewBlank()", StringComparison.Ordinal) is var blank and > 0 && fresh.IndexOf("return Run(program, file, work);", StringComparison.Ordinal) > blank
                  && fresh.IndexOf("m.File.OpenFile(file)) == 0", fresh.IndexOf("finally", blank, StringComparison.Ordinal), StringComparison.Ordinal) > 0, "the engineer's window lets go of the file first and always opens it again");
            Check(fresh.Contains("KeptPlugIns.Take(program)", StringComparison.Ordinal) && fresh.Contains("KeptPlugIns.PutBack(plugIns)", StringComparison.Ordinal) && fresh.Contains("ApplicationExit(false)", StringComparison.Ordinal),
                "the second SAP2000 leaves the plug-in lists as they were and closes without saving over the change");
            Check(fresh.Contains("app.ApplicationStart(6, false, \"\")", StringComparison.Ordinal), "the second SAP2000 is started hidden: the engineer sees nothing of it");
            // The field test with the fix (2026-11-26): ASCE 7-22 took CPM-ELFY's API-set ASCE 7-16 off; IS 1893:2016, KDS,
            // IRC 6-2017 and Live Load were taken but GetAutoSeismicCode / GetAutoWindCode name them "".
            Check(tables.IndexOf("Tools.SapLateral.Hold(e, other,", StringComparison.Ordinal) is var hold and > 0 && tables.IndexOf("ApplyEditedTables(true", hold, StringComparison.Ordinal) > hold
                  && tables.IndexOf("Tools.SapLateral.Reapply(e, held[lost])", hold, StringComparison.Ordinal) > tables.IndexOf("ApplyEditedTables(true", hold, StringComparison.Ordinal),
                "every other API-set code is held before the import and given back by its Set call when the import took it off");
            Check(tables.IndexOf("TableCode(m, pattern)", StringComparison.Ordinal) > 0, "a code the API names \"\" is read from the patterns' table");
            var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Sap2000/SapFieldSteps.cs"));
            Check(steps.Contains("the change is made in a second SAP2000", StringComparison.Ordinal) && steps.IndexOf("CsiFieldSteps.Run(ctx);", StringComparison.Ordinal) < steps.IndexOf("TinyS2k);", StringComparison.Ordinal),
                "the field test tries it on a real SAP2000, last, after every other table step");
        });

        yield return ("ETABS 23 (main 3cdc64d run): a field left at its default while the record is read is not a refusal; a skipped record is; wind User Loads gets no LoadAtCM", () =>
        {
            const string tl = "Table name:  LOAD PATTERN DEFINITIONS - AUTO SEISMIC - KDS 2019 Field name:  TL Record:  1 Error reading field TL; field ignored and default values will be used 1 of 1 records successfully read";
            Check(CsiProMax.Assistant.Etabs.EditableTables.OnlyFieldsIgnored(tl, 1), "KDS TL");
            Check(!CsiProMax.Assistant.Etabs.EditableTables.OnlyFieldsIgnored("Field name:  STORY Record:  1 Error reading field; record skipped", 1), "a record skipped");
            Check(!CsiProMax.Assistant.Etabs.EditableTables.OnlyFieldsIgnored(tl, 2), "more errors than fields ignored");
            var lateral = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/LateralTools.cs"));
            Check(lateral.Contains("table.Key.Contains(\"Seismic\", StringComparison.OrdinalIgnoreCase) ? [(\"LoadAtCM\", \"Yes\")]"), "LoadAtCM only for the seismic User Loads table");
        });

        yield return ("field test (Ahmed, 2026-11-26: one run brings everything): a refused table import puts its rows and whole log in the failing step's detail", () =>
        {
            var before = CsiProMax.Assistant.Etabs.EditableTables.Refusals;
            var log = string.Join("\n", Enumerable.Range(1, 80).Select(i => $"line {i} of the import log")) + "\nField name:  STORY Record:  1 Error reading field; record skipped";
            CsiProMax.Assistant.Etabs.EditableTables.Refused("ApplyEditedTables answered 1 (0 fatal, 1 errors, 0 warnings)", log,
                [("Load Pattern Definitions - Auto Seismic - User Loads", ["Name", "NumberSets", "Story"], [["EQX", "1", "Story1"]])]);
            Check(CsiProMax.Assistant.Etabs.EditableTables.Refusals == before + 1, "counted");
            var text = CsiProMax.Assistant.Etabs.EditableTables.LastRefused;
            Check(text.Contains("fields Name | NumberSets | Story") && text.Contains("[EQX | 1 | Story1]") && text.Contains("line 80 of the import log") && text.Contains("record skipped"), "rows and the uncut log: " + text);
            var ctx = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/FieldContext.cs"));
            Check(ctx.Contains("Import sent and its whole log: ") && ctx.Contains("EditableTables.Refusals != refusedBefore"), "the failing step's detail carries it");
        });

        yield return ("SAP2000 26 (experiment M, 2026-11-26): IS 1893:2016, IRC 6-2017 and the wind on live load go in by SAP2000's own AutoLoad names and pattern type", () =>
        {
            Check(LateralTools.SapAutoLoad("IS 1893:2016") == "IS 1893-2016", "IS 1893:2016 and IS1893:2016 left the pattern with no code; IS 1893-2016 gave it");
            Check(LateralTools.SapAutoLoad("IRC 6-2017") == "IRC:6-2017", "SAP2000's own log: 'is not a IRC:6-2017 auto wind load'");
            Check(LateralTools.SapAutoLoad("KDS 41 17 00-2019") == "KDS 41 17 00-2019" && LateralTools.SapAutoLoad("Dominican Republic R-001") == "Dominican Republic R-001", "the codes that took their table's name keep it");
            Check(LateralTools.SapDesignType(CsiProMax.Lateral.LateralCodes.Wind, "Live Load") == "Wind - Live Load", "'Load pattern CPM-AUTO-W is not a Wind-Live Load type load pattern; record skipped'");
            Check(LateralTools.SapDesignType(CsiProMax.Lateral.LateralCodes.Wind, "IRC 6-2017") == "Wind" && LateralTools.SapDesignType(CsiProMax.Lateral.LateralCodes.Seismic, "IS 1893:2016") == "Quake", "every other code keeps its pattern's type");
            Check(LateralTools.IsWindOnLiveLoad("Wind - Live Load") && LateralTools.IsWindOnLiveLoad("WindOnLiveLoad") && !LateralTools.IsWindOnLiveLoad("Wind"), "a wind on live load pattern is still a wind pattern for the next wind code");
            var tables = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/EditableTables.cs"));
            Check(tables.Contains("HasRow(m, codeKey, pattern)) took = code;", StringComparison.Ordinal), "a pattern with no AutoLoad name (wind on live load) took the code when the code's table has its row");
            // KDS straight after KBC 2016: SAP2000 took the rows, then deleted "1 KBC 2016 auto seismic loads" and left None.
            Check(tables.IndexOf("m.LoadPatterns.AutoSeismic.SetNone(pattern)", StringComparison.Ordinal) is var none and > 0
                  && none < tables.IndexOf("SetTableForEditingArray(\"Load Pattern Definitions\"", tables.IndexOf("public static string PutAutoLoad(", StringComparison.Ordinal), StringComparison.Ordinal)
                  && tables.Contains("m.LoadPatterns.AutoWind.SetNone(pattern)", StringComparison.Ordinal), "the pattern's own other code is taken off before the import");
            var lateral = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/LateralTools.cs"));
            Check(!lateral.Contains("PutAutoLoad(e, name, kind == LateralCodes.Wind ? \"Wind\" : \"Quake\", table.KeyCode", StringComparison.Ordinal)
                  && lateral.Contains("PutAutoLoad(e, name, SapDesignType(kind, table.Code), SapAutoLoad(table)", StringComparison.Ordinal)
                  && lateral.Contains("PutAutoLoad(fresh, name, SapDesignType(kind, table.Code), SapAutoLoad(table)", StringComparison.Ordinal), "both imports (here and in a second SAP2000) send SAP2000's names");
        });

        yield return ("ETABS run on main 4eabf46: the Engine's hidden run offers run_analysis; shell loads come first among the pack's inputs; a Modeler already open counts as answered; a section cut's group holds joints", () =>
        {
            string Src(string f) => File.ReadAllText(Path.Combine(SourceText.Root(), f));
            var engine = Src("src/CsiProMax.Assistant/Tools/EngineRun.cs");
            Check(engine.Contains("ctx.Offer(ProgramTools.Chat(program))"), "the chat's whole tool set, shared tools included");
            Check(ProgramTools.Chat(CsiProgram.Sap2000).Any(t => t.Name == "run_analysis") && ProgramTools.Chat(CsiProgram.Sap2000).Any(t => t.Name == "run_design"), "run_analysis and run_design offered");
            Check(EtabsTools.All.Any(t => t.Name == "run_analysis"), "run_analysis is a shared tool");
            Check(CsiProMax.Assistant.Tools.PackTables.IsLoadAssignment("Shell Loads - Uniform") && CsiProMax.Assistant.Tools.PackTables.IsLoadAssignment("Frame Loads Assignments - Distributed")
                && CsiProMax.Assistant.Tools.PackTables.IsLoadAssignment("Joint Loads Assignments - Force") && !CsiProMax.Assistant.Tools.PackTables.IsLoadAssignment("Load Combination Definitions"), "load assignment tables");
            TableName T(string n) => new(n, n, false);
            var order = CsiProMax.Assistant.Tools.PackTables.More([T("Load Combination Definitions"), T("Frame Loads Assignments - Open Structure Wind Parameters"), T("Shell Loads - Uniform")], []).Select(x => x.Name).ToList();
            Check(order.IndexOf("Shell Loads - Uniform") < order.IndexOf("Load Combination Definitions"), "shell loads before the combinations: " + string.Join(", ", order));
            Check(Src("src/CsiProMax.Assistant/Field/ModelerStartFieldSteps.cs").Contains("first.ExitCode == 0 && OpenElsewhere() is { } other"), "a start handed to the open Modeler passes");
            Check(Src("src/CsiProMax.Assistant/Field/ScratchExtras.cs").Contains("\"PointObj.SetGroupAssign\", (\"Name\", bottom), (\"GroupName\", Group)"), "the cut's group holds the columns' bottom joints");
        });

        yield return ("Ahmed 2026-11-26, 'please handle the pop ups': every box of the program in a window CSI ProMax started is answered OK and said, never a licence box", () =>
        {
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            var notices = scratch[scratch.IndexOf("public static List<string> CloseNotices", StringComparison.Ordinal)..scratch.IndexOf("public static bool IsProgramBox", StringComparison.Ordinal)];
            var box = notices[notices.IndexOf("IsProgramBox(title, program)", StringComparison.Ordinal)..];
            Check(box.IndexOf("\"licen\"", StringComparison.Ordinal) is var lic and > 0 && lic < box.IndexOf("PostMessageW(fine", StringComparison.Ordinal), "a licence message is never answered");
            // SAP2000's main window is titled "SAP2000" alone while it starts or opens a file: only a dialog box is answered.
            Check(box.IndexOf("IsDialog(window)", StringComparison.Ordinal) is var dlg and > 0 && dlg < box.IndexOf("PostMessageW(fine", StringComparison.Ordinal), "the program's main window could be taken for its message box");
            if (OperatingSystem.IsWindows()) Check(!ScratchDialogs.IsDialog(IntPtr.Zero), "no window is no dialog");
            Check(!box[..box.IndexOf("PostMessageW(fine", StringComparison.Ordinal)].Contains("FirstOrDefault(IsFileRefused) is { } said", StringComparison.Ordinal), "only 'Error decompressing file' boxes were answered: 'Error getting text file version!' waited for Ahmed");
            var fresh = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/SapFreshProcess.cs"));
            Check(fresh.Contains("ScratchDialogs.CloseNotices(id, program)", StringComparison.Ordinal), "the hidden second SAP2000's pop-ups are answered too");
        });

        yield return ("ETABS Plus (Ahmed's PC, 2026-11-26): a new Modeler model does not ask for Model Hinges in Links, an Ultimate-only option that stops every open on a notice", () =>
        {
            var text = CsiProMax.E2k.E2kWriter.Write(CsiProMax.Model.Seed.Etabs23());
            Check(text.Contains("MODELHINGESINLINKS \"No\"") && !text.Contains("MODELHINGESINLINKS \"Yes\""), "the seed's analysis options");
        });

        yield return ("kind: a program's 'could not read this file' box in a scratch window is answered OK and said, never a licence box", () =>
        {
            var box = CsiProMax.Assistant.ScratchDialogs.IsFileRefused;
            Check(box("Error decompressing file. May not be a valid ETABS .EDB file."), "SAFE 20's words"); // ETABS itself
            Check(!box("Error decompressing file: no licence was found"), "a licence message is never answered");
            Check(!box("Analysis Complete"), "another box");
            Check(box("Error in performing miOpen"), "ETABS 23's words, Ahmed's PC 2026-11-26");
            Check(CsiProMax.Assistant.ScratchDialogs.IsProgramBox("Warning", CsiProgram.Etabs), "ETABS 23 titles that box Warning");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsProgramBox("Warning: licence", CsiProgram.Etabs), "never another title");
            Check(CsiProMax.Assistant.ScratchDialogs.IsOpenFileBox(" Open Model File ") && !CsiProMax.Assistant.ScratchDialogs.IsOpenFileBox("Open Model File - licence"), "ETABS 23's Open Model File picker, by its title");
            Check(box("Open Model File: ETABS asked for a file to open (Cancel)"), "a cancelled file picker fails its step");
            Check(CsiProMax.Assistant.ScratchDialogs.IsAnalysisMessages(" Analysis Messages ") && !CsiProMax.Assistant.ScratchDialogs.IsAnalysisMessages("Analysis Messages - licence"), "ETABS's Analysis Messages window, by its title (0.4.57: it held the test 10 minutes)");
            Check(CsiProMax.Assistant.ScratchDialogs.IsLineLog(" Log ") && !CsiProMax.Assistant.ScratchDialogs.IsLineLog("Import Log") && !CsiProMax.Assistant.ScratchDialogs.IsLineLog("Show Log"), "ETABS's plain Log window on opening a .e2k, by its title (0.4.58: duplicated lines held both scratch ETABS)");
            foreach (var p in CsiProgram.All)
                Check(CsiProMax.Assistant.ScratchDialogs.IsProgramBox(" " + p.Name + " ", p) && !CsiProMax.Assistant.ScratchDialogs.IsProgramBox(p.Name + " licence", p), $"{p.Name}'s own message box, by its title");
        });

        yield return ("SAFE 20 tester (0.4.47): the chat does not hand SAFE a .f2k to open; it says to import it", () =>
        {
            var tool = CsiTools.For(CsiProgram.Safe).Single(t => t.Name == "open_model");
            var folder = Path.Combine(Path.GetTempPath(), "cpm-f2k-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            try
            {
                var f2k = Path.Combine(folder, "LEVEL 29.f2k");
                File.WriteAllText(f2k, "");
                var (link, api) = OpenProbe(CsiProgram.Safe, @"C:\Models\Untitled");
                try { tool.Invoke(JsonDocument.Parse(JsonSerializer.Serialize(new { path = f2k })).RootElement, link); throw new Exception("the .f2k was handed to SAFE"); }
                catch (ToolInputException ex) { Check(ex.Message.Contains("File > Import", StringComparison.Ordinal), ex.Message); }
                Equal(null, api.Opened, "SAFE was asked to open nothing");
            }
            finally { Directory.Delete(folder, true); }
        });
    }

    /// <summary>SAFE's API as the rebuild calls it: every property is itself, every call is recorded and answers 0 (a rename answers 1, as a program that keeps its own names).</summary>
    public sealed class ApiRecorder : System.Dynamic.DynamicObject
    {
        public List<string> Calls { get; } = [];
        public override bool TryGetMember(System.Dynamic.GetMemberBinder binder, out object? result) { result = this; return true; }
        public override bool TryInvokeMember(System.Dynamic.InvokeMemberBinder binder, object?[]? args, out object? result)
        {
            Calls.Add($"{binder.Name}({string.Join(", ", args ?? [])})");
            result = binder.Name == "ChangeName" ? 1 : 0;
            return true;
        }
    }

    /// <summary>A link whose only work is the calls made by path, on the pipe's model.</summary>
    sealed class ApiLinkProbe(ApiPipe pipe)
    {
        public object Root { get; } = pipe.Root();
    }

    /// <summary>SAFE's File.OpenFile, recording what it was asked to open.</summary>
    sealed class OpenRecorder
    {
        public string? Opened;
        public string Current = "";
        public FileApi File => new(this);
        public sealed class FileApi(OpenRecorder r)
        {
            public int OpenFile(string FileName) { r.Opened = FileName; r.Current = FileName; return 0; }
        }
    }

    /// <summary>A SAFE link whose model file is what the recorder last opened.</summary>
    static (ICsiLink Link, OpenRecorder Api) OpenProbe(CsiProgram program, string current = "")
    {
        var api = new OpenRecorder { Current = current };
        return (new FileModel(new SafeMemory(new MemoryEtabs { Program = program }, program: program) { Api = api }, () => api.Current), api);
    }
}

namespace CSiAPIv1
{
    // A stand-in for a CSI program's API on the program's side of the pipe: its interfaces, as the API file has them.
    public enum eItemType { Objects = 0, Group = 1, SelectedObjects = 2 }
    public enum eObjType { Point = 1, Frame = 2, Area = 5 }

    public interface cSapModel
    {
        cFrameObj FrameObj { get; }
        cSelectObj SelectObj { get; }
        string GetModelFilename(bool IncludePath = true);
    }

    public interface cFrameObj
    {
        int GetNameList(ref int NumberNames, ref string[] MyName);
        int SetSelected(string Name, bool Selected, eItemType ItemType = eItemType.Objects);
        int Delete(string Name, eItemType ItemType = eItemType.Objects);
    }

    public interface cSelectObj
    {
        int GetSelected(ref int NumberItems, ref eObjType[] ObjectType, ref string[] ObjectName);
    }

    public sealed class FakeModel : cSapModel
    {
        public FakeFrames Frames { get; } = new();
        public cFrameObj FrameObj => Frames;
        public cSelectObj SelectObj => new FakeSelect(Frames);
        // Explicit, as the API's own classes are: reached only through the interface.
        string cSapModel.GetModelFilename(bool IncludePath) => IncludePath ? @"E:\Pro max\LEVEL 29.FDB" : "LEVEL 29.FDB";
    }

    public sealed class FakeFrames : cFrameObj
    {
        public string? LastSelected;
        public int GetNameList(ref int NumberNames, ref string[] MyName) { MyName = ["B1", "B2", "C1"]; NumberNames = 3; return 0; }
        public int SetSelected(string Name, bool Selected, eItemType ItemType = eItemType.Objects) { LastSelected = Name; return ItemType == eItemType.Objects ? 0 : 1; }
        public int Delete(string Name, eItemType ItemType = eItemType.Objects) => throw new InvalidOperationException($"There is no frame named {Name}.");
    }

    public sealed class FakeSelect(FakeFrames frames) : cSelectObj
    {
        public int GetSelected(ref int NumberItems, ref eObjType[] ObjectType, ref string[] ObjectName)
        {
            var picked = frames.LastSelected is null ? Array.Empty<string>() : [frames.LastSelected];
            NumberItems = picked.Length; ObjectName = picked; ObjectType = picked.Select(_ => eObjType.Frame).ToArray();
            return 0;
        }
    }
}

namespace CpmFakes
{
        /// <summary>SAFE's database tables for the strip table: lists its fields, takes the rows and counts the strips they name.</summary>
        public sealed class StripTableApi(string[] fields)
        {
            public List<string[]> Written { get; } = [];
            public string[] Listed { get; } = fields;
            public string[] Fields = [];
            public bool Applied;
            public TablesApi DatabaseTables => new(this);
            public SlabApi DesignConcreteSlab => new(this);
            public sealed class TablesApi(StripTableApi a)
            {
                public int GetAllFieldsInTable(string TableKey, ref int TableVersion, ref int NumberFields, ref string[] FieldKey, ref string[] FieldName, ref string[] Description, ref string[] UnitsString, ref bool[] IsImportable)
                { FieldKey = a.Listed; FieldName = a.Listed; NumberFields = a.Listed.Length; return TableKey == CsiProMax.Assistant.Safe.F2kRebuild.StripTable ? 0 : 1; }
                public int GetTableForEditingArray(string TableKey, string GroupName, ref int TableVersion, ref string[] FieldsKeysIncluded, ref int NumberRecords, ref string[] TableData)
                { FieldsKeysIncluded = a.Listed; NumberRecords = 0; TableData = []; return 0; }
                public int SetTableForEditingArray(string TableKey, ref int TableVersion, ref string[] FieldsKeysIncluded, int NumberRecords, ref string[] TableData)
                {
                    a.Fields = FieldsKeysIncluded;
                    for (var r = 0; r < NumberRecords; r++) a.Written.Add(TableData.Skip(r * FieldsKeysIncluded.Length).Take(FieldsKeysIncluded.Length).ToArray());
                    return 0;
                }
                public int ApplyEditedTables(bool FillImportLog, ref int NumFatalErrors, ref int NumErrorMsgs, ref int NumWarnMsgs, ref int NumInfoMsgs, ref string ImportLog) { a.Applied = true; ImportLog = ""; return 0; }
                public int CancelTableEditing() => 0;
            }
            public sealed class SlabApi(StripTableApi a)
            {
                public StripApi DesignStrip => new(a);
            }
            public sealed class StripApi(StripTableApi a)
            {
                public int GetNameList(ref int NumberNames, ref string[] MyName)
                {
                    var at = Array.FindIndex(a.Fields, f => f == "Name");
                    MyName = a.Written.Select(r => r[at]).Distinct().ToArray();
                    NumberNames = MyName.Length;
                    return 0;
                }
            }
        }

    /// <summary>ETABS 20's API as the tester's run showed it: no SectCut, a ModalEigen with no SetNumberModes; its tables take rows.</summary>
    public sealed class OldEtabsApi
    {
        public Dictionary<string, List<string[]>> Rows { get; } = [];
        public Cases LoadCases { get; } = new();
        public Tables DatabaseTables => new(this);
        public sealed class Cases { public Eigen ModalEigen { get; } = new(); }
        public sealed class Eigen { public int SetCase(string Name) => 0; }
        public sealed class Tables(OldEtabsApi a)
        {
            private static string[] FieldsOf(string key) => key.StartsWith("Modal", StringComparison.Ordinal) ? ["Name", "MassSource", "MaxModes", "MinModes", "GUID"] : ["Name", "DefinedBy", "Group", "ResultType", "GUID"];
            public int GetTableForEditingArray(string TableKey, string GroupName, ref int TableVersion, ref string[] FieldsKeysIncluded, ref int NumberRecords, ref string[] TableData)
            { FieldsKeysIncluded = FieldsOf(TableKey); NumberRecords = 0; TableData = []; return 0; }
            public int GetAllFieldsInTable(string TableKey, ref int TableVersion, ref int NumberFields, ref string[] FieldKey, ref string[] FieldName, ref string[] Description, ref string[] UnitsString, ref bool[] IsImportable)
            { FieldKey = FieldsOf(TableKey); FieldName = FieldKey; NumberFields = FieldKey.Length; return 0; }
            public int SetTableForEditingArray(string TableKey, ref int TableVersion, ref string[] FieldsKeysIncluded, int NumberRecords, ref string[] TableData)
            {
                var f = FieldsKeysIncluded.Length;
                var data = TableData;
                a.Rows[TableKey] = Enumerable.Range(0, NumberRecords).Select(r => data.Skip(r * f).Take(f).ToArray()).ToList();
                return 0;
            }
            public int ApplyEditedTables(bool FillImportLog, ref int NumFatalErrors, ref int NumErrorMsgs, ref int NumWarnMsgs, ref int NumInfoMsgs, ref string ImportLog) { ImportLog = ""; return 0; }
            public int CancelTableEditing() => 0;
        }
    }
}

namespace CpmFakes
{
    /// <summary>DatabaseTables that answer GetAllTables and GetAvailableTables with the codes given.</summary>
    public sealed class TableListApi(int all, int available)
    {
        public int GetAllTables(ref int NumberTables, ref string[] TableKey, ref string[] TableName, ref int[] ImportType, ref bool[] IsEmpty)
        {
            if (all != 0) return all;
            TableKey = ["A", "B", "C"]; TableName = ["Analysis Messages", "Base Reactions", "Joint Reactions"]; ImportType = [0, 0, 0]; IsEmpty = [true, false, false]; NumberTables = 3; return 0;
        }
        public int GetAvailableTables(ref int NumberTables, ref string[] TableKey, ref string[] TableName, ref int[] ImportType)
        {
            if (available != 0) return available;
            TableKey = ["B", "C"]; TableName = ["Base Reactions", "Joint Reactions"]; ImportType = [0, 0]; NumberTables = 2; return 0;
        }
    }
}
