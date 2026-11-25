using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Host;

/// <summary>
/// The Modeler and Viewer shortcuts, on the desktop and in the Start menu (CSI ProMax folder). Made at the first
/// start with no question asked, and pointed at the new version after an update. A shortcut the user deleted stays
/// deleted. Both are .lnk files made by Windows' own shortcut object: the Modeler's runs the PC's dotnet with
/// CsiProMax.dll --modeler, the Viewer's opens the Viewer page in the browser. No .exe of ours.
/// </summary>
internal static class Shortcuts
{
    public const string ModelerName = "CSI ProMax Modeler", ViewerName = "CSI ProMax Viewer";

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax");
    private static string Mark => Path.Combine(Root, "shortcuts.txt");

    /// <summary>The Viewer page on this PC, kept the same as this version's.</summary>
    public static string ViewerPage => Path.Combine(Root, "CSI ProMax Viewer.html");

    public static IEnumerable<string> Folders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CSI ProMax");
    }

    /// <summary>What the mark file holds: the program folder the shortcuts were last made for.</summary>
    internal static bool Due(string? marked, string folder) => !string.Equals(marked?.Trim(), folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Called at every start, off the window's thread. Never throws: a PC that forbids shortcuts just has none.</summary>
    public static void Ensure()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var folder = AppContext.BaseDirectory;
            var first = !File.Exists(Mark);
            WriteViewerPage();
            if (!first && !Due(File.ReadAllText(Mark), folder)) return;
            Make(onlyExisting: !first);
            Directory.CreateDirectory(Root);
            File.WriteAllText(Mark, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException) { }
    }

    /// <summary>Makes all four again, whatever was deleted (the View menu's button). Gives back the files made.</summary>
    public static IReadOnlyList<string> MakeAll()
    {
        WriteViewerPage();
        var made = Make(onlyExisting: false);
        Directory.CreateDirectory(Root);
        File.WriteAllText(Mark, AppContext.BaseDirectory);
        return made;
    }

    private static void WriteViewerPage()
    {
        var html = Pack.ViewerHtml();
        Directory.CreateDirectory(Root);
        if (!File.Exists(ViewerPage) || File.ReadAllText(ViewerPage, Encoding.UTF8) != html)
            File.WriteAllText(ViewerPage, html, new UTF8Encoding(false));
    }

    private static List<string> Make(bool onlyExisting)
    {
        var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("This program's own path is not known.");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows' shortcut maker is not available on this PC.");
        var icon = Path.Combine(AppContext.BaseDirectory, "csi-promax.ico");
        var made = new List<string>();
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var folder in Folders())
            {
                if (folder.Length == 0) continue;
                Directory.CreateDirectory(folder);
                var modeler = Path.Combine(folder, ModelerName + ".lnk");
                if (!onlyExisting || File.Exists(modeler))
                {
                    dynamic link = shell.CreateShortcut(modeler);
                    link.TargetPath = dotnet;
                    link.Arguments = "\"" + Path.Combine(AppContext.BaseDirectory, "CsiProMax.dll") + "\" --modeler";
                    link.WorkingDirectory = AppContext.BaseDirectory;
                    link.WindowStyle = 7; // minimised: dotnet's own console window stays out of the way
                    link.Description = "CSI ProMax Modeler: build ETABS models with no ETABS open";
                    if (File.Exists(icon)) link.IconLocation = icon;
                    link.Save();
                    made.Add(modeler);
                }
                var viewer = Path.Combine(folder, ViewerName + ".lnk");
                if (!onlyExisting || File.Exists(viewer))
                {
                    dynamic link = shell.CreateShortcut(viewer);
                    link.TargetPath = ViewerPage;
                    link.Description = "CSI ProMax Viewer: open a review pack (.cpmx) with no ETABS";
                    if (File.Exists(icon)) link.IconLocation = icon;
                    link.Save();
                    made.Add(viewer);
                }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return made;
    }
}
