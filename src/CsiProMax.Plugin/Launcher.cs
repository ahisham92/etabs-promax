using System.Diagnostics;

namespace CsiProMaxPlugin;

/// <summary>Starts the assistant (CsiProMax.dll, beside this file) with the PC's .NET 8: the one this CSI program runs on when it has one.</summary>
internal static class Launcher
{
    internal static bool Windows => Path.DirectorySeparatorChar == '\\';

    public static void Start()
    {
        using var me = Process.GetCurrentProcess();
        Process.Start(StartInfo(
            Path.GetDirectoryName(typeof(Launcher).Assembly.Location) ?? "",
            Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "", me.Id, me.ProcessName))?.Dispose();
    }

    /// <summary>
    /// Where dotnet may be, best first: three folders up from the running .NET's own folder (a CSI program that runs
    /// on .NET 8, such as ETABS 23), then DOTNET_ROOT, Program Files, and the per-user installs. An older CSI program
    /// runs on .NET Framework and has no dotnet of its own, so the PC's .NET 8 is looked for.
    /// </summary>
    internal static IEnumerable<string> DotnetFolders(string runtimeFolder)
    {
        if (runtimeFolder.IndexOf("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase) >= 0)
            yield return Path.GetFullPath(Path.Combine(runtimeFolder, "..", "..", ".."));
        foreach (var name in new[] { "DOTNET_ROOT", "ProgramW6432", "ProgramFiles", "LOCALAPPDATA", "USERPROFILE" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) continue;
            yield return name switch
            {
                "DOTNET_ROOT" => value,
                "LOCALAPPDATA" => Path.Combine(value, "Microsoft", "dotnet"),
                "USERPROFILE" => Path.Combine(value, ".dotnet"),
                _ => Path.Combine(value, "dotnet"),
            };
        }
    }

    /// <param name="pluginFolder">Where this plug-in lies.</param>
    /// <param name="runtimeFolder">The running .NET's own folder, such as C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.29.</param>
    /// <param name="pid">This program window's process id: the assistant works on this window, whichever others are open.</param>
    /// <param name="program">The process name of the CSI program (ETABS, SAP2000, SAFE), which the assistant is told.</param>
    /// <param name="folders">Where to look for dotnet. Left out: <see cref="DotnetFolders"/>.</param>
    internal static ProcessStartInfo StartInfo(string pluginFolder, string runtimeFolder, int pid, string program = "ETABS", IEnumerable<string>? folders = null)
    {
        var assistant = Path.Combine(pluginFolder, "CsiProMax.dll");
        if (!File.Exists(assistant)) throw new IOException($"CsiProMax.dll is missing from {pluginFolder}. Copy the whole CSI ProMax folder, not only the plug-in file.");
        var file = Windows ? "dotnet.exe" : "dotnet";
        var tried = (folders ?? DotnetFolders(runtimeFolder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // The first dotnet that has the Desktop Runtime 8 wins: a PC can have an older dotnet in Program Files and the
        // Desktop Runtime 8 in the user's own folder (the one-line install puts it there when the PC has none).
        var found = tried.Where(f => File.Exists(Path.Combine(f, file))).ToList();
        var dotnet = (found.FirstOrDefault(HasDesktopRuntime8) ?? found.FirstOrDefault()) is { } home ? Path.Combine(home, file)
            : throw new IOException("CSI ProMax needs the .NET 8 Desktop Runtime, and it was not found on this PC (looked in " + string.Join(", ", tried) + "). " +
                                     "It is free from Microsoft: install \".NET Desktop Runtime 8\" (x64) once, then start CSI ProMax again.");
        return new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = pluginFolder,
            Arguments = Quote(assistant) + " --etabs-pid " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " --program " + program.ToLowerInvariant(),
        };
    }

    /// <summary>Whether this dotnet folder holds a .NET 8 Desktop Runtime (shared\Microsoft.WindowsDesktop.App\8.x), which the window needs.</summary>
    internal static bool HasDesktopRuntime8(string dotnetFolder)
    {
        try
        {
            var shared = Path.Combine(dotnetFolder, "shared", "Microsoft.WindowsDesktop.App");
            return Directory.Exists(shared) && Directory.GetDirectories(shared, "8.*").Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Quote(string path) => "\"" + path.TrimEnd('\\') + "\"";
}
