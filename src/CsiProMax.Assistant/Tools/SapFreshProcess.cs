using System.Diagnostics;
using System.Reflection;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// SAP2000 26 refuses every table import in a process that has opened an .s2k: ApplyEditedTables answers 1, with no log,
/// and every load pattern is gone, even after the .sdb is opened again; a new SAP2000 opening the same .sdb takes the same
/// rows (experiment N on Ahmed's PC, 2026-11-26). The engineer's SAP2000 cannot be restarted from inside it, so the
/// import is made in a second SAP2000 that CSI ProMax starts for itself: the engineer's window lets go of the saved .sdb
/// (File.NewBlank), the second SAP2000 opens it, takes the rows, saves it and closes, and the engineer's window opens the
/// .sdb again, with the change. Only for SAP2000 on .NET 8 (26 and later), whose API this process can start.
/// </summary>
internal static class SapFreshProcess
{
    /// <summary>True when the refusal is the one a second SAP2000 gets round (its message names the cause).</summary>
    public static bool IsSessionRefusal(CsiProgram program, string message) =>
        program.Key == CsiProgram.Sap2000.Key && message.Contains(EditableTables.NoImportAfterText.TrimEnd('.'), StringComparison.Ordinal);

    /// <summary>What the engineer is told after the change was made in a second SAP2000.</summary>
    public static string Said(string file) =>
        $"Done. This SAP2000 session could not take the change itself (it had opened an .s2k), so CSI ProMax made it in a hidden second SAP2000 on {Path.GetFileName(file)} and opened the model again here; nothing is needed from the engineer. The file was saved, as they agreed to before.";

    /// <summary>
    /// In <paramref name="link"/>'s own window: lets go of <paramref name="file"/>, has a second SAP2000 open it and do
    /// <paramref name="work"/> on its own link, save it and close, then opens <paramref name="file"/> again. When the
    /// second SAP2000 cannot do it, the file is opened again as it was and the reason is thrown.
    /// </summary>
    public static T Apply<T>(ICsiLink link, string file, Func<ICsiLink, T> work)
    {
        var program = link.Program;
        try { link.Raw($"let go of {Path.GetFileName(file)} for a second {program.Name}", m => m.File.NewBlank()); }
        catch (EtabsException) { /* the second SAP2000 may still open it */ }
        try { return Run(program, file, work); }
        finally
        {
            var back = (bool)link.Raw($"open {Path.GetFileName(file)} again", m => { try { return Convert.ToInt32(m.File.OpenFile(file)) == 0; } catch (Exception) { return false; } })!;
            try { link.Refresh(); } catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { /* the next call reads it */ }
            if (!back) throw new EtabsException($"{program.Name} did not open {Path.GetFileName(file)} again: open it yourself (File > Open), without saving the model first.");
        }
    }

    /// <summary>Starts a second SAP2000, opens <paramref name="file"/> in it, runs <paramref name="work"/>, saves and closes it.</summary>
    public static T Run<T>(CsiProgram program, string file, Func<ICsiLink, T> work)
    {
        var name = program.Name;
        var apiFile = EtabsApi.FindApiFile(program);
        if (!File.Exists(apiFile) || EtabsApi.RunsOnFramework(apiFile))
            throw new EtabsException($"A second {name} could not be started from here ({(File.Exists(apiFile) ? "its API runs on the .NET Framework" : "its API file was not found")}).");
        var api = Assembly.LoadFrom(apiFile);
        var ns = EtabsApi.NamespaceOf(apiFile);
        var plugIns = KeptPlugIns.Take(program);
        var before = Process.GetProcessesByName(program.ProcessName).Select(p => p.Id).ToHashSet();
        var asked = DateTime.Now;
        dynamic? app = null;
        var com = new ComEtabs(program, () =>
        {
            var helper = Activator.CreateInstance(api.GetType(ns + ".Helper", true)!)!;
            object raw;
            try
            {
                raw = api.GetType(ns + ".cHelper", true)!.GetMethod("CreateObjectProgID")!.Invoke(helper, [program.ProgId])
                    ?? throw new EtabsException($"A second {name} gave no application object.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new EtabsException($"A second {name} could not be started ({inner.GetType().Name}: {inner.Message})."); }
            app = new EtabsApi(raw, api.GetType(ns + ".cOAPI", true)!);
            // Hidden: the engineer sees nothing of it (Ahmed, 2026-11-26: "the user does not feel anything").
            int answer;
            try { answer = Convert.ToInt32(app.ApplicationStart(6, false, "")); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { answer = Convert.ToInt32(app.ApplicationStart()); }
            if (answer != 0) throw new EtabsException($"A second {name} did not start (it answered {answer}): its licence may allow one {name} at a time.");
            return app.SapModel;
        });
        // The maintenance reminder of the new process is answered as the field test's is; a licence message never is.
        using var reminder = new Timer(_ =>
        {
            try { ScratchDialogs.AnswerMaintenanceReminder(Process.GetProcessesByName(program.ProcessName).Select(p => p.Id).Where(id => !before.Contains(id)).ToList()); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next look tries again */ }
        }, null, 1000, 1000);
        try
        {
            if (Convert.ToInt32(com.Raw($"open {Path.GetFileName(file)} in a second {name}", m => m.File.OpenFile(file))) != 0)
                throw new EtabsException($"A second {name} did not open {Path.GetFileName(file)}.");
            var fresh = ProgramTools.Live(com);
            var result = work(fresh);
            if (Convert.ToInt32(com.Raw($"save {Path.GetFileName(file)} in the second {name}", m => m.File.Save(file))) != 0)
                throw new EtabsException($"The second {name} did not save {Path.GetFileName(file)}, so the change is not in it.");
            return result;
        }
        finally
        {
            try { com.Raw($"close the second {name}", _ => { app?.ApplicationExit(false); return null; }); } catch (Exception ex) when (ex is not OutOfMemoryException) { /* ended below */ }
            com.Dispose();
            // Its process must go, or it keeps a licence with no window: only a process this call started, on this file.
            foreach (var p in Process.GetProcessesByName(program.ProcessName).Where(p => !before.Contains(p.Id)))
                try
                {
                    if (p.WaitForExit(30000)) continue;
                    var titles = ScratchDialogs.Of(p.Id).Select(w => w.Title).ToList();
                    if (p.StartTime >= asked.AddSeconds(-2) && titles.All(t => t.Contains(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase) || t.Contains("Untitled", StringComparison.OrdinalIgnoreCase))) p.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                finally { p.Dispose(); }
            try { KeptPlugIns.PutBack(plugIns); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }
}
