using System.Diagnostics;
using System.Text;

namespace CsiProMax.Assistant;

/// <summary>
/// Keeps "CSI ProMax" in the Tools menus when CSI ProMax itself is not open. A CSI program empties its plug-in list now
/// and then (started through its API it writes NumberPlugIns=0 back as it closes), and <see cref="ToolsMenu.Repair"/>
/// runs only once CSI ProMax is opened, which, with the menu entry gone, a tester could no longer do (2026-11-26). So
/// every start of CSI ProMax puts keep-plugins.ps1 (inside this program) in %LOCALAPPDATA%\CsiProMax, kept the same as
/// this version's, and when it is not set up yet for the installed folder, has it set itself up: a hidden task at
/// every Windows sign-in (or a Startup shortcut where tasks are not allowed) that looks at the lists once a minute and
/// adds CSI ProMax back to each closed program that lost it. A PC updated only by the updater gets it this way; the
/// install line sets it up too. Never throws.
/// </summary>
public static class MenuKeeper
{
    public const string Resource = "CsiProMax.keep-plugins.ps1";

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax");

    /// <summary>The copy the sign-in task runs.</summary>
    public static string Script => Path.Combine(Root, "keep-plugins.ps1");

    /// <summary>What keep-plugins.ps1 -Register wrote: "task|folder" or "startup|folder".</summary>
    private static string Mark => Path.Combine(Root, "keep-plugins.txt");

    /// <summary>The script inside this program, as the repository has it.</summary>
    public static string? ScriptText()
    {
        using var stream = typeof(MenuKeeper).Assembly.GetManifestResourceStream(Resource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    /// <summary>True when the keeper must be set up (again) for <paramref name="folder"/>: never set up, set up for another folder, or turned off by hand.</summary>
    internal static bool Due(string? mark, string folder, bool off)
    {
        if (off) return false;
        if (string.IsNullOrWhiteSpace(mark)) return true;
        var bar = mark.IndexOf('|');
        var marked = bar < 0 ? "" : mark[(bar + 1)..].Trim();
        return !string.Equals(marked.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Called at every start, off the window's thread.</summary>
    public static void Ensure()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var text = ScriptText();
            if (text is null) return;
            Directory.CreateDirectory(Root);
            // A running keeper sees its file change and starts the new one in its place.
            if (!File.Exists(Script) || File.ReadAllText(Script, Encoding.ASCII) != text) File.WriteAllText(Script, text, Encoding.ASCII);
            var folder = ToolsMenu.InstallFolder();
            if (folder is null) return;
            var off = File.Exists(Path.Combine(Root, "keep-plugins-off.txt"));
            if (!Due(File.Exists(Mark) ? File.ReadAllText(Mark) : null, folder, off)) return;
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(File.Exists(powershell) ? powershell : "powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", Script, "-Register", "-Quiet", "-Folder", folder })
                info.ArgumentList.Add(arg);
            using var started = Process.Start(info);
            started?.WaitForExit(60_000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
    }
}
