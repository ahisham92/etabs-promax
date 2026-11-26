using System.Runtime.InteropServices;
using CsiProMaxPlugin;
using SAFEv1;

namespace CsiProMaxSafePlugin;

/// <summary>
/// What SAFE calls when the engineer picks CSI ProMax in its Tools menu (Tools > Add/Show Plugins, plug-in name
/// CsiProMaxSafePlugin). It opens the assistant's window, the same one as in ETABS, told it works in SAFE, and gives
/// SAFE back at once. The class and method names are the ones SAFE looks for.
/// A plain class on purpose: the program finds it by its name (CsiProMaxSafePlugin.cPlugin, the plug-in name and the class
/// name) and calls Main and Info by their names. It must not say ": cPluginContract". That interface is only in the
/// API file of the programs that run on .NET 8 (SAFE 22 and later); the older ones, which run on the
/// .NET Framework, have none, so there a class that names it cannot be created at all: the program says "Cannot create
/// plugin!" and shows "Not Found" in its plug-in list. That was version 0.4.4 in ETABS 20. CSI's own example says the
/// interface is not required. The stand-in API files leave it out, so naming it here does not compile.
/// </summary>
public class cPlugin
{
    public void Main(ref cSapModel SapModel, ref cPluginCallback ISapPlugin)
    {
        // First the pipe the window reads this program's model through: a program on the .NET Framework (ETABS 21,
        // SAP2000 25, SAFE 21 and older) can be reached no other way from the window's .NET 8 (ApiBridge).
        ApiBridge.Serve(SapModel);
        try { Launcher.Start(); }
        catch (Exception ex)   // whatever it is: a plug-in must never throw into the program that loaded it
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
