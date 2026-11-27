using System.Diagnostics;
using System.Text;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// The values SAP2000 itself writes in its definition tables (link types, hinge kinds, area and frame shapes), so the
/// Engine's SAP2000 forms write exactly SAP2000's words, never a guess (Ahmed 2026-11-27):
/// dotnet CsiProMax.dll --sap2000-table-values out.txt [model.sdb|.s2k ...]. It starts a SAP2000 of its own (never the
/// engineer's), builds a scratch model with one link property of each type, one area section of each kind and one frame
/// section of each shape the API can make (each call that SAP2000 refuses is listed, the rest go on), and writes every
/// row of the link, hinge, area and frame section tables to the file. Each model named after it is opened in turn and its
/// tables written too (SAP2000's own example models, or a model with user hinges). That SAP2000 closes without saving.
/// </summary>
public static class SapTableValues
{
    private static readonly string[] Prefixes = ["Link Property Definitions", "Link Frequency Dependent", "Hinges Def", "Area Section Properties", "Frame Section Properties", "Solid Property", "Cable Section", "Tendon Section", "Joint Pattern"];

    public static int Run(string outFile, IReadOnlyList<string> models)
    {
        var program = CsiProgram.Sap2000;
        var log = new StringBuilder();
        void Say(string line) { Console.WriteLine(line); log.AppendLine(line); }
        var apiFile = EtabsApi.FindApiFile(program);
        if (!File.Exists(apiFile)) { Console.WriteLine("no SAP2000 API file " + apiFile); return 1; }
        var api = EtabsApi.Load(apiFile);
        Say("SAP2000 table values, " + api.FullName + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        var before = Process.GetProcessesByName(program.ProcessName).Select(p => p.Id).ToHashSet();
        var plugIns = KeptPlugIns.Take(program);
        var scratchPid = 0;
        dynamic? sap = null;
        try
        {
            var helper = Activator.CreateInstance(api.GetType("SAP2000v1.Helper", true)!)!;
            var app = api.GetType("SAP2000v1.cHelper", true)!.GetMethod("CreateObjectProgID")!.Invoke(helper, [program.ProgId]);
            sap = new EtabsApi(app!, api.GetType("SAP2000v1.cOAPI", true)!);
            if (Convert.ToInt32(sap.ApplicationStart()) != 0) { Say("SAP2000 did not start"); return 1; }
            scratchPid = Process.GetProcessesByName(program.ProcessName).Select(p => p.Id).FirstOrDefault(id => !before.Contains(id));
            dynamic m = sap.SapModel;
            m.InitializeNewModel(6); // kN, m, C
            m.File.NewBlank();
            Build((object)m, Say);
            using (var com = new ComEtabs(scratchPid == 0 ? null : scratchPid, program)) Dump(new SapLink(com), "scratch model", Say);
            foreach (var model in models)
            {
                if (!File.Exists(model)) { Say("no file " + model); continue; }
                var opened = Convert.ToInt32(m.File.OpenFile(Path.GetFullPath(model)));
                if (opened != 0) { Say($"SAP2000 did not open {model} ({opened})"); continue; }
                using var com = new ComEtabs(scratchPid == 0 ? null : scratchPid, program);
                Dump(new SapLink(com), model, Say);
            }
        }
        catch (Exception ex) { Say("stopped: " + ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message); }
        finally
        {
            try { if (sap is not null) sap.ApplicationExit(false); }
            catch (Exception ex) { Console.WriteLine("could not close the scratch SAP2000: " + ex.Message); }
            try { if (scratchPid != 0) using (var gone = Process.GetProcessById(scratchPid)) gone.WaitForExit(60_000); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            KeptPlugIns.PutBack(plugIns);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, log.ToString());
            Console.WriteLine("written " + Path.GetFullPath(outFile));
        }
        return 0;
    }

    private static void Build(dynamic m, Action<string> say)
    {
        void Try(string what, Func<object> call)
        {
            try { var r = Convert.ToInt32(call()); say(r == 0 ? $"made  {what}" : $"SAP2000 refused {what} ({r})"); }
            catch (Exception ex) { say($"could not make {what}: {ex.GetBaseException().Message}"); }
        }
        // Materials: steel 1, concrete 2, aluminum 4, cold formed 5, tendon 7 (eMatType).
        foreach (var (name, type) in new[] { ("CPV-S", 1), ("CPV-C", 2), ("CPV-AL", 4), ("CPV-CF", 5), ("CPV-TEN", 7) })
            Try("material " + name, () => m.PropMaterial.SetMaterial(name, type));

        // One link property of each type, U1 and U2 active, U2 nonlinear.
        bool[] dof = [true, true, false, false, false, false], fix = new bool[6], nl = [false, true, false, false, false, false];
        double[] ke = [1000, 100, 0, 0, 0, 0], ce = new double[6], k = [1000, 100, 0, 0, 0, 0];
        double[] c = [0, 10, 0, 0, 0, 0], cexp = [0, 0.5, 0, 0, 0, 0], yld = [0, 50, 0, 0, 0, 0], ratio = [0, 0.1, 0, 0, 0, 0], ex = [0, 2, 0, 0, 0, 0], open = [0, 0.01, 0, 0, 0, 0];
        double[] slow = [0, 0.04, 0, 0, 0, 0], fast = [0, 0.06, 0, 0, 0, 0], rate = [0, 20, 0, 0, 0, 0], radius = [0, 2, 0, 0, 0, 0];
        Try("link Linear", () => m.PropLink.SetLinear("CPV-LIN", ref dof, ref fix, ref ke, ref ce, 0.0, 0.0, false, false, "", ""));
        Try("link MultiLinear Elastic", () => m.PropLink.SetMultiLinearElastic("CPV-MLE", ref dof, ref fix, ref nl, ref ke, ref ce, 0.0, 0.0, "", ""));
        Try("link MultiLinear Plastic", () => m.PropLink.SetMultiLinearPlastic("CPV-MLP", ref dof, ref fix, ref nl, ref ke, ref ce, 0.0, 0.0, "", ""));
        Try("link Damper - Exponential", () => m.PropLink.SetDamper("CPV-DMP", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref c, ref cexp, 0.0, 0.0, "", ""));
        Try("link Damper - Bilinear", () => m.PropLink.SetDamperBilinear("CPV-DBL", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref c, ref c, ref yld, 0.0, 0.0, "", ""));
        Try("link Damper - Friction Spring", () => m.PropLink.SetDamperFrictionSpring("CPV-DFS", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref k, ref k, ref open, ref open, 0, 0.0, 0.0, "", ""));
        Try("link Gap", () => m.PropLink.SetGap("CPV-GAP", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref open, 0.0, 0.0, "", ""));
        Try("link Hook", () => m.PropLink.SetHook("CPV-HOOK", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref open, 0.0, 0.0, "", ""));
        Try("link Plastic (Wen)", () => m.PropLink.SetPlasticWen("CPV-WEN", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref yld, ref ratio, ref ex, 0.0, 0.0, "", ""));
        Try("link Rubber Isolator", () => m.PropLink.SetRubberIsolator("CPV-RUB", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref yld, ref ratio, 0.0, 0.0, "", ""));
        Try("link Friction Isolator", () => m.PropLink.SetFrictionIsolator("CPV-FRI", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref slow, ref fast, ref rate, ref radius, 0.0, 0.0, 0.0, "", ""));
        Try("link T/C Friction Isolator", () => m.PropLink.SetTCFrictionIsolator("CPV-TCF", ref dof, ref fix, ref nl, ref ke, ref ce, ref k, ref slow, ref fast, ref rate, ref radius, ref slow, ref fast, ref rate, 1000.0, 0.0, 0.0, 0.0, 0.0, 0.0, "", ""));
        Try("link Triple Pendulum Isolator", () => m.PropLink.SetTriplePendulumIsolator("CPV-TPI", ref dof, ref fix, ref nl, ref ke, ref ce, 1000.0, 0.0, 0.2, 0.1, true,
            ref k, ref slow, ref fast, ref rate, ref radius, ref open, 0.0, 0.0, "", ""));
        Try("link High Damping Rubber Isolator", () => m.PropLink.SetHighDampingRubberIsolator("CPV-HDR", ref dof, ref fix, ref nl, ref ke, ref ce, 0.0, 0.0, "", ""));

        // Area sections: shell types 1 to 6 (thin, thick, plate thin, plate thick, membrane, layered), plane and asolid.
        foreach (var (name, type) in new[] { ("CPV-SH1", 1), ("CPV-SH2", 2), ("CPV-PL3", 3), ("CPV-PL4", 4), ("CPV-MEM", 5) })
            Try("area " + name, () => m.PropArea.SetShell_1(name, type, false, "CPV-C", 0.0, 0.2, 0.2, -1, "", ""));
        Try("area plane stress", () => m.PropArea.SetPlane("CPV-PLS", 1, "CPV-C", 0.0, 0.2, true, -1, "", ""));
        Try("area plane strain", () => m.PropArea.SetPlane("CPV-PLN", 2, "CPV-C", 0.0, 0.2, true, -1, "", ""));
        Try("area asolid", () => m.PropArea.SetAsolid("CPV-ASO", "CPV-C", 0.0, 0.0, true, "Global", -1, "", ""));

        // Frame sections the Engine has no shape for, by the API calls SAP2000 has for them.
        Try("frame steel rectangle", () => m.PropFrame.SetRectangle("CPV-SRECT", "CPV-S", 0.3, 0.2, -1, "", ""));
        Try("frame concrete pipe", () => m.PropFrame.SetPipe("CPV-CPIPE", "CPV-C", 0.5, 0.1, -1, "", ""));
        Try("frame concrete tube", () => m.PropFrame.SetTube("CPV-CTUBE", "CPV-C", 0.5, 0.4, 0.1, 0.1, -1, "", ""));
        Try("frame aluminum I", () => m.PropFrame.SetISection("CPV-AI", "CPV-AL", 0.3, 0.15, 0.01, 0.006, 0.15, 0.01, -1, "", ""));
        Try("frame aluminum channel", () => m.PropFrame.SetChannel("CPV-AC", "CPV-AL", 0.3, 0.1, 0.01, 0.006, -1, "", ""));
        Try("frame aluminum tee", () => m.PropFrame.SetTee("CPV-AT", "CPV-AL", 0.3, 0.15, 0.01, 0.006, -1, "", ""));
        Try("frame aluminum angle", () => m.PropFrame.SetAngle("CPV-AA", "CPV-AL", 0.1, 0.1, 0.01, 0.01, -1, "", ""));
        Try("frame aluminum tube", () => m.PropFrame.SetTube("CPV-ATU", "CPV-AL", 0.2, 0.1, 0.006, 0.006, -1, "", ""));
        Try("frame cold formed C", () => m.PropFrame.SetColdC("CPV-CFC", "CPV-CF", 0.2, 0.07, 0.002, 0.003, 0.02, -1, "", ""));
        Try("frame cold formed Z", () => m.PropFrame.SetColdZ("CPV-CFZ", "CPV-CF", 0.2, 0.07, 0.002, 0.003, 0.02, 45.0, -1, "", ""));
        Try("frame cold formed hat", () => m.PropFrame.SetColdHat("CPV-CFH", "CPV-CF", 0.1, 0.1, 0.002, 0.003, 0.02, -1, "", ""));
        Try("frame cold formed I", () => m.PropFrame.SetISection("CPV-CFI", "CPV-CF", 0.2, 0.1, 0.003, 0.002, 0.1, 0.003, -1, "", ""));
        Try("frame cold formed tee", () => m.PropFrame.SetTee("CPV-CFT", "CPV-CF", 0.2, 0.1, 0.003, 0.002, -1, "", ""));
        Try("frame cold formed angle", () => m.PropFrame.SetAngle("CPV-CFA", "CPV-CF", 0.08, 0.08, 0.002, 0.002, -1, "", ""));
        Try("frame cold formed box", () => m.PropFrame.SetTube("CPV-CFB", "CPV-CF", 0.1, 0.1, 0.002, 0.002, -1, "", ""));
        Try("frame cold formed pipe", () => m.PropFrame.SetPipe("CPV-CFP", "CPV-CF", 0.1, 0.002, -1, "", ""));
        double[] bb = [0.6, 0.15, 0.1, 0.2], dd = [1.5, 0.1, 0.05, 0.05, 0.1, 0.1, 0.2], bu = [2.0, 0.3, 0.3, 0.2, 0.2, 0.2], du = [1.5, 0.2, 0.2, 0.1, 0.1, 0.1, 0.1];
        Try("frame precast I", () => m.PropFrame.SetPrecastI_1("CPV-PCI", "CPV-C", ref bb, ref dd, ref bb, 0.0, -1, "", ""));
        Try("frame precast U", () => m.PropFrame.SetPrecastU("CPV-PCU", "CPV-C", ref bu, ref du, -1, "", ""));
        Try("frame concrete trapezoidal", () => m.PropFrame.SetTrapezoidal("CPV-TRAP", "CPV-C", 0.5, 0.3, 0.2, -1, "", ""));
        Try("frame steel joist", () => m.PropFrame.SetSteelJoist("CPV-SJ", "CPV-S", 0.6, -1, "", ""));
        Try("frame hybrid I", () => m.PropFrame.SetHybridISection("CPV-HI", "CPV-S", "CPV-S", "CPV-S", 0.6, 0.25, 0.02, 0.01, 0.25, 0.02, -1, "", ""));
        Try("frame hybrid U", () => m.PropFrame.SetHybridUSection("CPV-HU", "CPV-S", "CPV-S", "CPV-S", 1.5, 2.0, 0.3, 0.02, 0.02, 0.02, -1, "", ""));
        Try("frame general", () => m.PropFrame.SetGeneral("CPV-GEN", "CPV-S", 0.3, 0.2, 0.01, 1e-4, 2e-4, 5e-5, 0.005, 0.005, 1e-3, 5e-4, 1e-3, 5e-4, 0.1, 0.05, -1, "", ""));
    }

    private static void Dump(ICsiLink link, string source, Action<string> say)
    {
        say("");
        say("==== " + source);
        foreach (var table in link.Tables().Select(t => t.Key).Where(n => Prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))).OrderBy(n => n, StringComparer.Ordinal))
        {
            try
            {
                var data = link.ReadTable(table, null, null);
                say($"-- {table} :: {string.Join("|", data.Fields)}");
                foreach (var row in data.Rows) say("   " + string.Join("|", row));
            }
            catch (Exception ex) { say($"-- {table}: not read ({ex.GetBaseException().Message})"); }
        }
    }
}
