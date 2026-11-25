using System.Runtime.InteropServices;
using CsiProMaxPlugin;
using SAFEv1;

namespace CsiProMaxSafePlugin;

/// <summary>
/// What SAFE calls when the engineer picks CSI ProMax in its Tools menu (Tools > Add/Show Plugins, plug-in name
/// CsiProMaxSafePlugin). It opens the assistant's window, the same one as in ETABS, told it works in SAFE, and gives
/// SAFE back at once. The class and method names are the ones SAFE looks for.
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
        Text = "CSI ProMax: an AI assistant beside SAFE. Ask about the slab or mat, check punching shear, deflection, strip forces, soil pressure and slab design from SAFE's own tables, and add or change slabs, supports, soil and loads. It runs on Claude Code on your own Claude plan.";
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
