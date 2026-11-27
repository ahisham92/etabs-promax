using System.Diagnostics;
using System.Windows.Automation;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// Runs a command from a CSI program's own main menu, as a person would pick it, through Windows UI Automation. For the
/// commands the program's API has no call for: ETABS 23 has no StartDesign for shear walls (reflection on ETABSv1.dll,
/// Ahmed's PC 2026-11-26), and its main menu is a WinForms MenuStrip ("msMain") that answers no Win32 menu message
/// (GetMenu gives 0). Ahmed: no "known gap" for an installed, licensed program; the workaround is built in.
/// </summary>
public static class MenuCommand
{
    /// <summary>A menu item's words as the program shows them, without its access key, "..." or shortcut, for comparing.</summary>
    public static string Norm(string text)
    {
        var t = text.Replace("&", "").Replace("...", "").Replace("…", "");
        var tab = t.IndexOf('\t');
        if (tab >= 0) t = t[..tab];
        return new string(t.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }

    /// <summary>True when a menu item's words are the ones asked for (the start is enough: "Start Design/Check" for
    /// "Start Design/Check of Structure").</summary>
    public static bool Matches(string shown, string wanted)
    {
        var s = Norm(shown); var w = Norm(wanted);
        return s.Length > 0 && w.Length > 0 && (s == w || s.StartsWith(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// Picks path (e.g. Design, Shear Wall Design, Start Design/Check) in the main menu of the process. Returns null when
    /// it was picked, else why not, in words for the report. It never closes, types into or picks anything else.
    /// </summary>
    /// <summary>A path's first word for a button in the program's status bar instead of its main menu.</summary>
    public const string StatusBar = "Status Bar";

    public static string? Pick(int processId, IReadOnlyList<string> path, TimeSpan? wait = null)
    {
        if (!OperatingSystem.IsWindows()) return "menus are picked on Windows only";
        if (path.Count == 0) return "no menu path given";
        var most = wait ?? TimeSpan.FromSeconds(10);
        try
        {
            using var p = Process.GetProcessById(processId);
            var window = p.MainWindowHandle;
            if (window == IntPtr.Zero) window = Hidden(processId);
            if (window == IntPtr.Zero) return "the program has no main window";
            // A hidden window (the Engine's program) opens no drop-down: it is shown, not brought forward, while the menu
            // is picked, then hidden again.
            var shown = !IsWindowVisible(window) && ShowWindow(window, 4 /* SW_SHOWNOACTIVATE */) is var _ && IsWindowVisible(window);
            try { return PickIn(window, path, processId, most); }
            finally { if (shown) ShowWindow(window, 0 /* SW_HIDE */); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"the menu could not be picked ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string? PickIn(IntPtr window, IReadOnlyList<string> path, int processId, TimeSpan most)
    {
        try
        {
            var main = AutomationElement.FromHandle(window);
            // "Status Bar", then a button there (ETABS's and SAFE's "Units..."), then the items of the menu it opens.
            if (path[0] == StatusBar)
            {
                if (path.Count < 2) return "no status bar button given";
                var button = FindIn(main, path[1], processId, most, ControlType.Button, ControlType.SplitButton, ControlType.MenuItem);
                if (button is null) return $"the status bar has no \"{path[1]}\"";
                if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var press)) ((InvokePattern)press).Invoke();
                else if (button.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var drop)) ((ExpandCollapsePattern)drop).Expand();
                else return $"\"{path[1]}\" cannot be pressed";
                for (var i = 2; i < path.Count; i++)
                {
                    var item = Find(main, path[i], processId, most);
                    if (item is null) return $"\"{path[1]}\" shows no \"{path[i]}\"";
                    if (i == path.Count - 1)
                    {
                        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return null; }
                        return $"\"{path[i]}\" cannot be picked";
                    }
                    if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec)) ((ExpandCollapsePattern)ec).Expand();
                }
                return null;
            }
            var menu = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "msMain"))
                ?? main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuBar));
            if (menu is null) return "no main menu was found in the program's window";
            AutomationElement under = menu;
            for (var i = 0; i < path.Count; i++)
            {
                var item = Find(under, path[i], processId, most);
                if (item is null) return $"the menu has no \"{path[i]}\"" + (i > 0 ? $" under \"{path[i - 1]}\"" : "");
                if (!item.Current.IsEnabled) return $"\"{string.Join(" > ", path.Take(i + 1))}\" is greyed out";
                if (i == path.Count - 1)
                {
                    if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return null; }
                    return $"\"{path[i]}\" cannot be picked";
                }
                if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec)) ((ExpandCollapsePattern)ec).Expand();
                else if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var open)) ((InvokePattern)open).Invoke();
                else return $"\"{path[i]}\" does not open";
                under = item;
            }
            return "the menu path ran out";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ElementNotAvailableException or ElementNotEnabledException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            return $"the menu could not be picked ({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>The largest top-level window of the process with no owner: a hidden program's main window.</summary>
    private static IntPtr Hidden(int processId)
    {
        var best = IntPtr.Zero; long most = 0;
        EnumWindows((w, _) =>
        {
            GetWindowThreadProcessId(w, out var owner);
            if (owner == (uint)processId && GetWindow(w, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowRect(w, out var r))
            {
                var area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > most) { most = area; best = w; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private delegate bool Each(IntPtr window, IntPtr data);
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(Each each, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int cmd);

    /// <summary>The item under a menu (its drop-down opens in a window of its own, so the program's windows are looked in
    /// too), looked for until it shows or the wait is over.</summary>
    private static AutomationElement? Find(AutomationElement under, string name, int processId, TimeSpan most) =>
        FindIn(under, name, processId, most, ControlType.MenuItem, ControlType.SplitButton);

    private static AutomationElement? FindIn(AutomationElement under, string name, int processId, TimeSpan most, params ControlType[] types)
    {
        var items = new OrCondition(types.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).Append(Condition.FalseCondition).ToArray());
        var until = DateTime.UtcNow + most;
        do
        {
            foreach (AutomationElement e in under.FindAll(TreeScope.Descendants, items))
                if (Matches(e.Current.Name, name)) return e;
            var mine = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, processId), items);
            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId)))
                foreach (AutomationElement e in top.FindAll(TreeScope.Descendants, mine))
                    if (Matches(e.Current.Name, name)) return e;
            Thread.Sleep(250);
        } while (DateTime.UtcNow < until);
        return null;
    }
}
