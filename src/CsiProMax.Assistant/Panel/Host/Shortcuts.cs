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
    internal static bool Due(string? marked, string folder) => !string.Equals(marked?.Trim(), folder + Mark2, StringComparison.OrdinalIgnoreCase);

    /// <summary>Added to the mark when the shortcuts themselves change (2: they have the icon), so they are made again once.</summary>
    internal const string Mark2 = "|2";

    /// <summary>Called at every start, off the window's thread. Never throws: a PC that forbids shortcuts just has none.</summary>
    public static void Ensure()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var folder = ProgramFolder();
            var first = !File.Exists(Mark);
            WriteViewerPage();
            if (!first && !Due(File.ReadAllText(Mark), folder)) return;
            Make(onlyExisting: !first);
            Directory.CreateDirectory(Root);
            File.WriteAllText(Mark, folder + Mark2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException) { }
    }

    /// <summary>
    /// The CSI ProMax the shortcuts start: the installed copy (Documents\CSI ProMax, as the Tools menus name it), and the
    /// folder this copy runs from only when none is installed. A copy unpacked elsewhere to try a version (as the field
    /// test runs are) once pointed the desktop and Start menu Modeler at itself, and the engineer then opened that test
    /// copy from the desktop (2026-11-25).
    /// </summary>
    internal static string ProgramFolder()
    {
        string? installed = null;
        try { installed = CsiProMax.Assistant.ToolsMenu.InstallFolder(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var folder = installed ?? AppContext.BaseDirectory;
        return folder.EndsWith(Path.DirectorySeparatorChar) || folder.EndsWith(Path.AltDirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
    }

    /// <summary>Makes all four again, whatever was deleted (the View menu's button). Gives back the files made.</summary>
    public static IReadOnlyList<string> MakeAll()
    {
        WriteViewerPage();
        var made = Make(onlyExisting: false);
        Directory.CreateDirectory(Root);
        File.WriteAllText(Mark, ProgramFolder() + Mark2);
        return made;
    }

    /// <summary>The icon as a file the shortcuts can point at: it is inside the program, so it is written out once.</summary>
    private static string IconFile()
    {
        var file = Path.Combine(Root, "csi-promax.ico");
        try
        {
            using var stream = typeof(Shortcuts).Assembly.GetManifestResourceStream("CsiProMax.csi-promax.ico");
            if (stream is null) return file;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            Directory.CreateDirectory(Root);
            if (!File.Exists(file) || new FileInfo(file).Length != bytes.Length) File.WriteAllBytes(file, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return file;
    }

    /// <summary>Writes the Viewer page when it is missing or another version's, and gives back its file.</summary>
    internal static string WriteViewerPage()
    {
        var html = Pack.ViewerHtml();
        Directory.CreateDirectory(Root);
        if (!File.Exists(ViewerPage) || File.ReadAllText(ViewerPage, Encoding.UTF8) != html)
            File.WriteAllText(ViewerPage, html, new UTF8Encoding(false));
        return ViewerPage;
    }

    private static List<string> Make(bool onlyExisting)
    {
        var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("This program's own path is not known.");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows' shortcut maker is not available on this PC.");
        var icon = IconFile();
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
                    link.Arguments = "\"" + Path.Combine(ProgramFolder(), "CsiProMax.dll") + "\" --modeler";
                    link.WorkingDirectory = ProgramFolder();
                    link.WindowStyle = 7; // minimised: dotnet's own console window stays out of the way
                    link.Description = "CSI ProMax Modeler: build ETABS models with no ETABS open"; // ETABS itself
                    if (File.Exists(icon)) link.IconLocation = icon;
                    link.Save();
                    made.Add(modeler);
                }
                var viewer = Path.Combine(folder, ViewerName + ".lnk");
                if (!onlyExisting || File.Exists(viewer))
                {
                    dynamic link = shell.CreateShortcut(viewer);
                    link.TargetPath = ViewerPage;
                    link.Description = "CSI ProMax Viewer: open a review pack (.cpmx) with no ETABS"; // ETABS itself
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
