using System.Windows.Automation;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The words in a CSI program's status bar, read through Windows UI Automation. While ETABS builds its analysis model it
/// writes no log for many minutes, and its status bar alone says what it is on ("Creating Analysis Model - elements -
/// shells - LVL 02-TOC", Ahmed's real model, 2026-11-28): the Engine's run window shows it live.
/// </summary>
public static class StatusBarText
{
    private static Task<string>? _reading;
    private static string _last = "";

    /// <summary>
    /// The status bar's words now, or the last words read while a read is still out: the program answers UI Automation
    /// on its own thread, which may be busy, so a read is never waited on for more than 3 seconds and never stacked.
    /// </summary>
    public static string Of(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId == 0) return "";
        var reading = _reading;
        if (reading is null || reading.IsCompleted)
        {
            if (reading is { IsCompletedSuccessfully: true }) _last = reading.Result;
            reading = _reading = Task.Run(() => Read(processId));
        }
        if (reading.Wait(TimeSpan.FromSeconds(3))) { _last = reading.Result; }
        return _last;
    }

    private static string Read(int processId)
    {
        try
        {
            var window = ScratchDialogs.MainWindow(processId);
            if (window == IntPtr.Zero) return "";
            var main = AutomationElement.FromHandle(window);
            var bar = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
            if (bar is null) return "";
            var words = new List<string>();
            foreach (AutomationElement part in bar.FindAll(TreeScope.Children, Condition.TrueCondition))
                if (part.Current.Name is { Length: > 0 } name && part.Current.ControlType != ControlType.Button) words.Add(name.Trim());
            if (words.Count == 0 && bar.Current.Name is { Length: > 0 } own) words.Add(own.Trim());
            return Words(words);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

    /// <summary>The status bar's parts as one line: empty parts and repeats left out.</summary>
    public static string Words(IEnumerable<string> parts) =>
        string.Join("  |  ", parts.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct());
}
