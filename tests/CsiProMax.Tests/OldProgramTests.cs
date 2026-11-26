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

        yield return ("kind: every plug-in serves its program's model on the pipe before it opens the window", () =>
        {
            foreach (var folder in new[] { "CsiProMax.Plugin", "CsiProMax.Plugin.Safe", "CsiProMax.Plugin.Sap" })
            {
                var text = File.ReadAllText(Path.Combine(SourceText.Root(), "src", folder, "cPlugin.cs"));
                var serve = text.IndexOf("ApiBridge.Serve(SapModel)", StringComparison.Ordinal);
                Check(serve > 0 && serve < text.IndexOf("Launcher.Start()", StringComparison.Ordinal), $"{folder}: ApiBridge.Serve(SapModel) before Launcher.Start()");
                if (folder != "CsiProMax.Plugin")
                {
                    var project = File.ReadAllText(Path.Combine(SourceText.Root(), "src", folder, folder + ".csproj"));
                    Check(project.Contains("ApiBridge.cs", StringComparison.Ordinal) && project.Contains("ApiWire.cs", StringComparison.Ordinal), $"{folder} compiles the bridge in");
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
