using System.Diagnostics;
using System.Runtime.InteropServices;
using ETABSv1;

namespace CsiProMaxPlugin;

/// <summary>
/// What ETABS calls when the engineer picks CSI ProMax in the Tools menu. It opens the assistant's window and gives
/// ETABS back at once, so ETABS stays free to work in while the window is open. The assistant then talks to this
/// ETABS through ETABS's API, as before. The class and method names are the ones ETABS looks for.
/// A plain class on purpose: the program finds it by its name (CsiProMaxPlugin.cPlugin, the plug-in name and the class
/// name) and calls Main and Info by their names. It must not say ": cPluginContract". That interface is only in the
/// API file of the programs that run on .NET 8 (ETABS 22 and later); the older ones, which run on the
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
        // A newer copy of the plug-in that an update has downloaded does both instead, so a fix to the plug-in needs
        // no reinstall and no closing of the program (Newest).
        try
        {
            if (!Newest.Hand(SapModel, Path.GetDirectoryName(typeof(cPlugin).Assembly.Location) ?? ""))
            {
                ApiBridge.Serve(SapModel);
                Launcher.Start();
            }
        }
        catch (Exception ex)   // whatever it is: a plug-in must never throw into the program that loaded it
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
