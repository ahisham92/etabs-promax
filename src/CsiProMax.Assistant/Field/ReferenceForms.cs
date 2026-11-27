using System.Net;
using System.Text;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// The program's half of "Engine forms side by side" (Ahmed 2026-11-27: the Engine's forms beside the programs' own,
/// gathered on the PC by the test, never by hand). In the scratch window of each program, each form the Engine's
/// window check pictured is opened from the program's own main menu (Windows UI Automation, as a person would pick
/// it), pictured, and closed with its window's close box (Cancel: nothing is changed). The pictures go into
/// "Modeler window\Reference forms\program" and the side-by-side page is written again with them. A form whose menu
/// path the program does not have is named in the list with the reason.
/// </summary>
public static class ReferenceForms
{
    /// <summary>Where the Engine's menu has an item in another place than the program: the program's path for it.</summary>
    public static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["etabs|Define > Frame Sections"] = ["Define", "Section Properties", "Frame Sections"],
        ["etabs|Define > Slab and Wall Sections"] = ["Define", "Section Properties", "Slab Sections"],
        ["etabs|Define > Story Data"] = ["Edit", "Edit Stories and Grid Systems"],
        ["etabs|Define > Grid Data"] = ["Edit", "Edit Stories and Grid Systems"],
        ["sap2000|Define > Frame Sections"] = ["Define", "Section Properties", "Frame Sections"],
        ["sap2000|Define > Area Sections"] = ["Define", "Section Properties", "Area Sections"],
        ["sap2000|Define > Slab and Wall Sections"] = ["Define", "Section Properties", "Area Sections"],
        ["sap2000|Define > Grid Data"] = ["Define", "Coordinate Systems/Grids"],
        ["safe|Define > Frame Sections"] = ["Define", "Section Properties", "Frame Sections"],
        ["safe|Define > Slab and Wall Sections"] = ["Define", "Section Properties", "Slab Properties"],
    };

    /// <summary>The program's menu path for an Engine path ("Define > Load Patterns..."): an alias, else the same words.</summary>
    public static string[] PathFor(string program, string enginePath)
    {
        var clean = enginePath.Trim().TrimEnd('.').Trim();
        return Aliases.TryGetValue(program + "|" + clean, out var a) ? a : clean.Split(" > ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The file name of a form's picture: the Engine's path with " - " between its parts.</summary>
    public static string FileName(string path) =>
        string.Concat(path.Trim().TrimEnd('.').Trim().Replace(" > ", " - ", StringComparison.Ordinal).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>ReferenceData's item: pictures the program's own forms in its scratch window.</summary>
    public static string Gather(ReferenceData.Gather g)
    {
        var forms = ModelerCheck.LastForms.Where(f => f.Target == g.Program.Key).ToList();
        if (forms.Count == 0) return "not gathered: the Engine's forms were not pictured in this run (only a full run pictures them)";
        if (g.ProcessId == 0 || !OperatingSystem.IsWindows()) return "not gathered: the scratch window's process is not known";
        var work = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(g.Folder)))!;
        var folder = Path.Combine(work, "Modeler window", "Reference forms", g.Program.Key);
        Directory.CreateDirectory(folder);
        var notes = new List<string>();
        var made = 0;
        var stop = DateTime.UtcNow.AddMinutes(12); // a full run's share for this; what is left is named
        foreach (var (path, _, _) in forms)
        {
            if (DateTime.UtcNow > stop) { notes.Add($"{path}: not tried, the 12 minutes for the forms were used up"); continue; }
            var said = One(g.ProcessId, PathFor(g.Program.Key, path), Path.Combine(folder, FileName(path) + ".png"));
            if (said is null) made++; else notes.Add($"{path}: {said}");
        }
        File.WriteAllText(Path.Combine(folder, "not pictured.md"), notes.Count == 0 ? "Every form was pictured.\n" : string.Join("\n", notes.Select(n => "- " + n)) + "\n", new UTF8Encoding(false));
        ModelerCheck.WritePage(Path.Combine(work, "Modeler window", "Form pictures"));
        return $"{made} of {forms.Count} forms pictured in Modeler window\\Reference forms\\{g.Program.Key}" + (notes.Count > 0 ? $"; {notes.Count} not (see not pictured.md there)" : "");
    }

    /// <summary>Opens one form from the program's menu, pictures it and closes it. Null when pictured, else why not.</summary>
    private static string? One(int processId, string[] path, string picture)
    {
        var before = ScratchDialogs.Of(processId).Select(w => w.Window).ToHashSet();
        // The pick runs on its own: picking an item that opens a modal form may not come back until the form closes.
        var pick = Task.Run(() => MenuCommand.Pick(processId, path, TimeSpan.FromSeconds(4)));
        IntPtr form = IntPtr.Zero;
        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until)
        {
            form = ScratchDialogs.Of(processId).Select(w => w.Window).FirstOrDefault(w => !before.Contains(w));
            if (form != IntPtr.Zero) break;
            if (pick.IsCompleted && pick.Result is { } refused) return refused;
            Thread.Sleep(250);
        }
        if (form == IntPtr.Zero) { pick.Wait(TimeSpan.FromSeconds(5)); return pick.IsCompleted && pick.Result is { } why ? why : "no form opened"; }
        try
        {
            Thread.Sleep(600); // it draws its controls
            File.WriteAllBytes(picture, CsiProMax.Tools.ScreenGrab.Capture(form, 1400));
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "it opened, and could not be pictured: " + ex.Message;
        }
        finally
        {
            // Closed as its close box closes it (Cancel), and any form it opened in turn.
            for (var i = 0; i < 5; i++)
            {
                var open = ScratchDialogs.Of(processId).Select(w => w.Window).Where(w => !before.Contains(w)).ToList();
                if (open.Count == 0) break;
                foreach (var w in open) PostMessage(w, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(500);
            }
            pick.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
