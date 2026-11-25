using System.Runtime.InteropServices;
using System.Text;
using CsiProMax.Tools;

namespace CsiProMax.ETABS;

/// <summary>
/// Why ETABS isn't taking a request, worked out from outside ETABS's UI thread: a modal dialog (ETABS's main window is
/// disabled while one is open; its title is read too), a UI thread that has stopped answering (a long operation of
/// ETABS's own), an edit mode (only when its Finish button is seen on the ribbon, see EditModeProbe), or none of those
/// that can be told (a command of ETABS's own still running, most often; Esc ends it).
/// Only Win32 calls that never send a message to ETABS's thread are used (no GetWindowText), so this can't hang when
/// ETABS is the thing that is stuck. The wording (<see cref="Explain"/>) is pure, so the tests compile it.
/// </summary>
internal static class ETABSBlock
{
    public enum Kind { None, Dialog, NotResponding, Unknown, EditMode }

    public readonly record struct State(Kind Kind, string? Title);

    /// <summary>Looks at ETABS's main window. Safe on any thread; never throws.</summary>
    public static State Read(IntPtr mainWindow)
    {
        if (mainWindow == IntPtr.Zero) return new State(Kind.Unknown, null);
        try
        {
            if (IsHungAppWindow(mainWindow)) return new State(Kind.NotResponding, null);
            if (IsWindowEnabled(mainWindow)) return new State(Kind.None, null);
            return new State(Kind.Dialog, DialogTitle(mainWindow));
        }
        catch (Exception) { return new State(Kind.Unknown, null); }
    }

    /// <summary>
    /// The message for a request ETABS didn't start, for Claude. <paramref name="what"/> names the request ("The request",
    /// "Switching models"); <paramref name="seconds"/> is how long it waited.
    /// </summary>
    public static string Explain(State state, string what, int seconds)
    {
        var lead = $"{what} could not start: {Csi.Name} didn't take it within {seconds} s, so it was dropped and nothing was changed. ";
        return state.Kind switch
        {
            Kind.Dialog => lead + (string.IsNullOrWhiteSpace(state.Title)
                               ? $"{Csi.Name} has a dialog or message box open. "
                               : $"{Csi.Name} has a dialog open: '{state.Title!.Trim()}'. ") +
                           $"Ask the user to answer or close it in {Csi.Name} (it may be behind another window), then try again.",
            Kind.NotResponding => lead + $"{Csi.Name} is busy with a long operation of its own and isn't responding right now (for example a " +
                                  $"regeneration, sync with central, loading a model or printing). Ask the user to wait until {Csi.Name} " +
                                  $"responds again, or to cancel that operation in {Csi.Name}, then try again. Don't send other work meanwhile.",
            Kind.EditMode => lead + $"{Csi.Name} is in an edit mode (its Finish button is on the ribbon: a sketch, a group or an in-place family " +
                             $"is being edited). Ask the user to finish or cancel it in {Csi.Name}, then try again.",
            // Nothing that can be seen from outside: say so plainly rather than guess at an edit mode.
            _ => lead + $"{Csi.Name} is busy with something of its own, most often a command that is still running. Ask the user, in one " +
                 $"short sentence, to press Esc in {Csi.Name} once or twice and say when done, then try again.",
        };
    }

    /// <summary>How long ETABS may take no work, with nobody at it, before Esc ends the command left open.</summary>
    public static readonly TimeSpan EndCommandAfter = TimeSpan.FromMinutes(2);

    /// <summary>How long ETABS must show no activity at all (processor, memory, files, journal) to count as waiting for a click.</summary>
    public static readonly TimeSpan IdleFor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether to press Esc in ETABS now for jobs that wait: only while nobody is at ETABS (away mode or an Autopilot
    /// run); only when ETABS answers and shows no dialog and no edit mode; only when the command is doing nothing:
    /// no processor, memory, file or journal activity for <see cref="IdleFor"/> (<paramref name="quiet"/>, FreezeCause.Judge),
    /// which a command waiting for a click (Spot Elevation) shows and a working one (a sync, an export, a regeneration)
    /// never does; never during ETABS's known long work (<paramref name="longWork"/>: synchronizing, opening, saving,
    /// loading links), whatever the figures. After <see cref="EndCommandAfter"/>, at most three times, a minute apart.
    /// Otherwise nothing is touched (standing rule: never block or cancel an ETABS command; the owner, 19 Nov 2026: "make
    /// sure it doesn't end a command working, just commands that are doing nothing").
    /// </summary>
    public static bool EndCommandNow(Kind kind, TimeSpan held, int tries, bool nobodyThere, TimeSpan quiet, bool longWork) =>
        nobodyThere && kind == Kind.Unknown && !longWork && quiet >= IdleFor && tries < 3 && held >= EndCommandAfter + TimeSpan.FromMinutes(tries);

    /// <summary>What the chat says once when ETABS takes no work from the jobs: what holds it and how to free it.</summary>
    public static string HeldLine(State state, string busy)
    {
        var named = busy.Length > 0 && !busy.Equals("something of its own", StringComparison.OrdinalIgnoreCase);
        var tail = $" The jobs wait and carry on by themselves as soon as {Csi.Name} is free; nothing is lost.";
        return state.Kind switch
        {
            Kind.Dialog => $"{Csi.Name} isn't taking any work: it has a dialog open" +
                                      (string.IsNullOrWhiteSpace(state.Title) ? "" : $" ('{state.Title!.Trim()}')") +
                                      ". Answer or close it (it may be behind another window)." + tail,
            Kind.EditMode => $"{Csi.Name} isn't taking any work while an edit mode is open. Finish or cancel it (the green tick or red cross on the Modify tab)." + tail,
            Kind.NotResponding => $"{Csi.Name} is busy with a long task of its own" + (named ? $" ({busy})" : "") + ". Let it finish." + tail,
            _ => $"{Csi.Name} isn't taking any work right now" + (named ? $", most likely because {busy} is still running" : ", most likely a command still running") +
                 $". Press Esc in {Csi.Name} once or twice." + tail,
        };
    }

    /// <summary>
    /// The tool, as the user would say it ("reading your model"), for the card: never the tool's id. Reads that aren't
    /// named here are "reading your model"; changes are "the change you asked for".
    /// </summary>
    public static string Friendly(string tool, bool changes) => tool switch
    {
        "get_model_context" or "get_elements" or "get_element_parameters" or "list_types" or "list_levels" or "list_views" or "list_sheets"
            or "run_etabs_query_code" or "check_model" => "reading your model",
        "capture_view_image" => "taking a picture of the view",
        "list_etabs_models" or "use_etabs_model" => "finding the open models",
        "open_etabs_model" => "opening the model",
        "export_pdf" or "export_files" => "exporting",
        "run_etabs_code" => "running the code",
        _ => changes ? "the change you asked for" : "reading your model",
    };

    /// <summary>
    /// The card the user sees for a call ETABS didn't start: one short line each for what to do, where, and why. Says
    /// "edit mode" only when one was seen (<see cref="Kind.EditMode"/>); otherwise it doesn't guess.
    /// </summary>
    public static HandoffStep Card(State state, string friendly)
    {
        var nothing = " Nothing was changed.";
        return state.Kind switch
        {
            Kind.Dialog => new HandoffStep
            {
                Kind = "dialog",
                What = string.IsNullOrWhiteSpace(state.Title) ? $"Close the dialog in {Csi.Name}" : $"Close the dialog '{state.Title!.Trim()}' in {Csi.Name}",
                Where = $"{Csi.Name}'s window (the dialog may be behind another window), then click Continue",
                Why = $"{Csi.Name} can't start {friendly} while a dialog is open." + nothing,
            },
            Kind.NotResponding => new HandoffStep
            {
                Kind = "busy", What = $"{Csi.Name} is busy with a long task",
                Where = $"Wait until {Csi.Name} responds, then click Continue",
                Why = $"{Csi.Name} can't start {friendly} until it's done." + nothing,
            },
            Kind.EditMode => new HandoffStep
            {
                Kind = "edit_mode", What = $"Finish or cancel the edit in {Csi.Name}",
                Where = "Modify tab: the green tick (Finish) or the red cross (Cancel), then click Continue",
                Why = $"{Csi.Name} can't start {friendly} while you edit." + nothing,
            },
            _ => new HandoffStep
            {
                Kind = "busy", What = $"{Csi.Name} is busy",
                Where = $"Press Esc in {Csi.Name} once or twice, then click Continue",
                Why = $"{Csi.Name} didn't start {friendly} yet." + nothing,
            },
        };
    }

    /// <summary>True for a ribbon button that ends an edit mode: Finish Edit Mode, Finish (a group), Finish Model (in place).</summary>
    public static bool IsFinishButton(string? id, string? text)
    {
        if (id is { Length: > 0 } && id.IndexOf("FINISH", StringComparison.OrdinalIgnoreCase) >= 0
            && id.IndexOf("FINISHES", StringComparison.OrdinalIgnoreCase) < 0) return true;
        var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Equals("Finish", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Finish Edit", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Finish Model", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The title of the topmost visible window of this process owned by <paramref name="main"/> (the modal dialog).</summary>
    private static string? DialogTitle(IntPtr main)
    {
        string? found = null, fallback = null;
        var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        // EnumWindows goes top of the Z order first, so the first match is the dialog in front.
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == main || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid) return true;
            var title = Title(hwnd);
            if (OwnedBy(hwnd, main))
            {
                if (IsWindowEnabled(hwnd) && title.Length > 0) { found = title; return false; }
                fallback ??= title.Length > 0 ? title : null;
            }
            return true;
        }, IntPtr.Zero);
        return found ?? fallback;
    }

    private static bool OwnedBy(IntPtr hwnd, IntPtr main)
    {
        var h = GetWindow(hwnd, 4 /* GW_OWNER */);
        for (var depth = 0; h != IntPtr.Zero && depth < 8; depth++)
        {
            if (h == main) return true;
            h = GetWindow(h, 4);
        }
        return false;
    }

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        // InternalGetWindowText reads the title the window manager keeps: no message to the (possibly busy) owner thread.
        return InternalGetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// The window class that has the keyboard on ETABS's UI thread ("HwndWrapper..." is a WPF pane such as the chat;
    /// "AfxFrameOrView..." an ETABS view), for the log of a request ETABS didn't take. Sends ETABS no message.
    /// </summary>
    public static string FocusClass(IntPtr mainWindow)
    {
        try
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (mainWindow == IntPtr.Zero || !GetGUIThreadInfo(GetWindowThreadProcessId(mainWindow, out _), ref info)) return "?";
            if (info.Focus == IntPtr.Zero) return "nothing";
            var sb = new StringBuilder(128);
            return GetClassName(info.Focus, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
        }
        catch (Exception) { return "?"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
