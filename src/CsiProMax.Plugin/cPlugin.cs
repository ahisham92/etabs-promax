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
            if (Launcher.Windows) MessageBoxW(IntPtr.Zero, "CSI ProMax could not start.\n\n" + ex.Message, "CSI ProMax", 0x30);
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
