using System.Diagnostics;
using System.Runtime.InteropServices;
using ETABSv1;

namespace CsiProMaxPlugin;

/// <summary>
/// What ETABS calls when the engineer picks CSI ProMax in the Tools menu. It opens the assistant's window and gives
/// ETABS back at once, so ETABS stays free to work in while the window is open. The assistant then talks to this
/// ETABS through ETABS's API, as before. The class and method names are the ones ETABS looks for.
/// </summary>
public class cPlugin : cPluginContract
{
    public void Main(ref cSapModel SapModel, ref cPluginCallback ISapPlugin)
    {
        try { Launcher.Start(); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, "CSI ProMax could not start.\n\n" + ex.Message, "CSI ProMax", 0x30);
        }
        finally { ISapPlugin.Finish(0); }
    }

    public int Info(ref string Text)
    {
        Text = "CSI ProMax: an AI assistant beside ETABS. Ask about the model, read results from ETABS's tables, and add or change objects and loads. It runs on Claude Code on your own Claude plan.";
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}

/// <summary>Starts the assistant (CsiProMax.dll, beside this file) with the dotnet that ETABS itself is running on.</summary>
internal static class Launcher
{
    public static void Start() => Process.Start(StartInfo(
        Path.GetDirectoryName(typeof(Launcher).Assembly.Location) ?? "",
        Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "", Environment.ProcessId))?.Dispose();

    /// <param name="pluginFolder">Where this plug-in lies.</param>
    /// <param name="runtimeFolder">The running .NET's own folder, such as C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.29.</param>
    /// <param name="etabsPid">This ETABS window's process id: the assistant works on this ETABS, whichever others are open.</param>
    internal static ProcessStartInfo StartInfo(string pluginFolder, string runtimeFolder, int etabsPid)
    {
        var assistant = Path.Combine(pluginFolder, "CsiProMax.dll");
        if (!File.Exists(assistant)) throw new IOException($"CsiProMax.dll is missing from {pluginFolder}. Copy the whole CSI ProMax folder, not only the plug-in file.");
        // Three folders up from the runtime's own folder is the dotnet folder, with dotnet.exe.
        var root = Path.GetFullPath(Path.Combine(runtimeFolder, "..", "..", ".."));
        var dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnet)) throw new IOException($"The .NET that ETABS runs on was not found at {dotnet}.");
        var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = pluginFolder };
        info.ArgumentList.Add(assistant);
        info.ArgumentList.Add("--etabs-pid");
        info.ArgumentList.Add(etabsPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return info;
    }
}
