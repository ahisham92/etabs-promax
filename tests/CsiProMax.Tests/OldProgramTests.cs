using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

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

        yield return ("SAFE 20 tester (0.4.47): SAFE 20 is never given the Modeler's .f2k, which it read as a packed model ('Error decompressing file')", () =>
        {
            Check(!CsiProMax.Assistant.ScratchProgram.OpensThroughApi(CsiProgram.Safe, @"C:\t\cpm-field-test.f2k", onFramework: true), "SAFE 20/21 and a .f2k: no window is started");
            Check(CsiProMax.Assistant.ScratchProgram.OpensThroughApi(CsiProgram.Safe, @"C:\t\cpm-field-test.FDB", onFramework: true), "SAFE 20/21 and its own .fdb");
            Check(CsiProMax.Assistant.ScratchProgram.OpensThroughApi(CsiProgram.Safe, @"C:\t\cpm-field-test.f2k", onFramework: false), "SAFE 22 and later keep opening the .f2k as before");
            foreach (var p in new[] { CsiProgram.Etabs, CsiProgram.Sap2000 })
                Check(CsiProMax.Assistant.ScratchProgram.OpensThroughApi(p, @"C:\t\cpm-field-test" + (p == CsiProgram.Etabs ? ".e2k" : ".s2k"), onFramework: true), $"{p.Name} on the .NET Framework still opens its text file");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.IndexOf("OpensThroughApi(_program, modelFile", StringComparison.Ordinal) is > 0 and var at && at < scratch.IndexOf("ApiPipe.ToPowerShell(_program, ApiFile, \"start\"", StringComparison.Ordinal),
                "the check comes before any window is started");
        });

        yield return ("kind: a program's 'could not read this file' box in a scratch window is answered OK and said, never a licence box", () =>
        {
            var box = CsiProMax.Assistant.ScratchDialogs.IsFileRefused;
            Check(box("Error decompressing file. May not be a valid ETABS .EDB file."), "SAFE 20's words"); // ETABS itself
            Check(!box("Error decompressing file: no licence was found"), "a licence message is never answered");
            Check(!box("Analysis Complete"), "another box");
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
