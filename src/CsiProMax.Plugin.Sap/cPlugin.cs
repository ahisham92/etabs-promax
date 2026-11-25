using System.Runtime.InteropServices;
using CsiProMaxPlugin;
using SAP2000v1;

namespace CsiProMaxSapPlugin;

/// <summary>
/// What SAP2000 calls when the engineer picks CSI ProMax in its Tools menu. It opens the assistant's window for this
/// SAP2000 and gives SAP2000 back at once, so SAP2000 stays free to work in while the window is open. The assistant
/// then talks to SAP2000 through SAP2000's API. The class and method names are the ones SAP2000 looks for; the
/// plug-in name to register is this namespace, CsiProMaxSapPlugin.
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
        Text = "CSI ProMax: an AI assistant beside SAP2000. Ask about the model, read results and design from SAP2000, and add or change joints, frames, areas, supports and loads. It runs on Claude Code on your own Claude plan.";
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
