using System.Diagnostics;
using System.Reflection;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// The PC check for SAP2000: dotnet CsiProMax.dll --test-sap2000 model.s2k [folder]. It starts a new SAP2000 window of
/// its own (licensed as usual, never one the engineer has open), imports the .s2k, saves it as a scratch model in the
/// folder, runs the analysis and the frame design, runs the self test with changes against it, and closes that SAP2000
/// without saving. It also checks that the plug-in binds to this SAP2000's API file.
/// </summary>
public static class SapScratchTest
{
    public static int Run(string s2k, string folder)
    {
        var program = CsiProgram.Sap2000;
        var bad = 0;
        void Say(bool ok, string what) { Console.WriteLine($"{(ok ? "ok   " : "FAIL ")} {what}"); if (!ok) bad++; }

        var apiFile = EtabsApi.FindApiFile(program);
        Say(File.Exists(apiFile), "API file " + apiFile);
        if (!File.Exists(apiFile)) return 1;
        var api = EtabsApi.Load(apiFile);
        Console.WriteLine("     " + api.FullName);

        // The plug-in, as SAP2000 loads it.
        try
        {
            var plugin = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "CsiProMaxSapPlugin.dll"));
            var type = plugin.GetType("CsiProMaxSapPlugin.cPlugin", true)!;
            var contract = api.GetType("SAP2000v1.cPluginContract", true)!;
            Say(contract.IsAssignableFrom(type), "the plug-in's cPlugin is SAP2000's cPluginContract");
            var info = type.GetMethod("Info")!;
            var args = new object?[] { "" };
            info.Invoke(Activator.CreateInstance(type), args);
            Say(((string)args[0]!).Length > 0, "plug-in Info: " + args[0]);
        }
        catch (Exception ex) { Say(false, "plug-in: " + ex.GetBaseException().Message); }

        if (!File.Exists(s2k)) { Say(false, "no file " + s2k); return 1; }
        Directory.CreateDirectory(folder);
        var before = Process.GetProcessesByName(program.ProcessName).Select(p => p.Id).ToHashSet();
        // SAP2000 started through its API writes its settings back with an empty plug-in list, so Tools > CSI ProMax
        // was gone after this check ran (the field test keeps the lists since 0.4.15; this check did not, 2026-11-26).
        var plugIns = KeptPlugIns.Take(program);
        var scratchPid = 0;
        dynamic? sap = null;
        try
        {
            var helper = Activator.CreateInstance(api.GetType("SAP2000v1.Helper", true)!)!;
            var app = api.GetType("SAP2000v1.cHelper", true)!.GetMethod("CreateObjectProgID")!.Invoke(helper, [program.ProgId]);
            sap = new EtabsApi(app!, api.GetType("SAP2000v1.cOAPI", true)!);
            Say(Convert.ToInt32(sap.ApplicationStart()) == 0, "start a new SAP2000");
            var pid = Process.GetProcessesByName(program.ProcessName).Select(p => p.Id).FirstOrDefault(id => !before.Contains(id));
            scratchPid = pid;
            Console.WriteLine("     scratch SAP2000 process " + pid);
            dynamic m = sap.SapModel;
            Say(Convert.ToInt32(m.File.OpenFile(Path.GetFullPath(s2k))) == 0, "import " + s2k);
            int n = 0; string[] names = [];
            m.PointObj.GetNameList(ref n, ref names); var joints = n;
            m.FrameObj.GetNameList(ref n, ref names); var frames = n;
            m.AreaObj.GetNameList(ref n, ref names); var areas = n;
            Say(joints > 0 && frames > 0, $"imported {joints} joints, {frames} frames, {areas} areas");
            var file = Path.Combine(Path.GetFullPath(folder), "cpm-sap-scratch.sdb");
            Say(Convert.ToInt32(m.File.Save(file)) == 0, "save the scratch model " + file);
            Say(Convert.ToInt32(m.Analyze.RunAnalysis()) == 0, "run the analysis");
            try { Say(Convert.ToInt32(m.DesignConcrete.StartDesign()) == 0, "run concrete frame design"); }
            catch (Exception ex) { Say(false, "concrete design: " + ex.Message); }

            using var com = new ComEtabs(pid == 0 ? null : pid, program);
            var link = new SapLink(com);
            Console.WriteLine("---- self test, with changes, on the scratch model");
            bad += SelfTest.Run(link, true);
        }
        catch (Exception ex) { Say(false, ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message); }
        finally
        {
            try { if (sap is not null) sap.ApplicationExit(false); Console.WriteLine("     scratch SAP2000 closed without saving"); }
            catch (Exception ex) { Console.WriteLine("     could not close the scratch SAP2000: " + ex.Message); }
            // The program writes its settings as its process ends: the lists are put back after that.
            try { if (scratchPid != 0) using (var gone = Process.GetProcessById(scratchPid)) gone.WaitForExit(60_000); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var back = KeptPlugIns.PutBack(plugIns);
            if (back.Count > 0) Console.WriteLine("     the scratch SAP2000 had emptied the plug-in list; put back: " + string.Join(", ", back.Select(Path.GetFileName)));
        }
        Console.WriteLine(bad == 0 ? "SAP2000 PC check passed." : $"SAP2000 PC check: {bad} failed.");
        return bad == 0 ? 0 : 1;
    }
}
