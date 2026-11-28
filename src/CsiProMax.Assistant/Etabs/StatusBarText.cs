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
    private static DateTime _readAt = DateTime.MinValue;
    // The status bar found once and kept: a search of ETABS's whole window tree every second ran on ETABS's own busy
    // thread while it built the analysis model (0.4.77 on Ahmed's real model: 22 minutes before the analysis began,
    // about 3 in his own ETABS). Only the bar's own parts are read after the first find.
    private static (int Pid, IntPtr Window, AutomationElement Bar)? _bar;

    /// <summary>How often the status bar is read at most.</summary>
    public static TimeSpan Every { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The status bar's words now, or the last words read while a read is still out: the program answers UI Automation
    /// on its own thread, which may be busy, so a read is never waited on for more than 3 seconds and never stacked.
    /// </summary>
    public static string Of(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId == 0) return "";
        var reading = _reading;
        if (reading is { IsCompleted: true } && DateTime.UtcNow - _readAt < Every) return _last;
        if (reading is null || reading.IsCompleted)
        {
            _readAt = DateTime.UtcNow;
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
            AutomationElement? bar = _bar is { } kept && kept.Pid == processId && kept.Window == window ? kept.Bar : null;
            if (bar is null)
            {
                var main = AutomationElement.FromHandle(window);
                bar = main.FindFirst(TreeScope.Children, StatusBar) ?? main.FindFirst(TreeScope.Descendants, StatusBar);
                if (bar is null) return "";
                _bar = (processId, window, bar);
            }
            var words = new List<string>();
            foreach (AutomationElement part in bar.FindAll(TreeScope.Children, Condition.TrueCondition))
                if (part.Current.Name is { Length: > 0 } name && part.Current.ControlType != ControlType.Button) words.Add(name.Trim());
            if (words.Count == 0 && bar.Current.Name is { Length: > 0 } own) words.Add(own.Trim());
            return Words(words);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _bar = null; return ""; }
    }

    private static readonly PropertyCondition StatusBar = new(AutomationElement.ControlTypeProperty, ControlType.StatusBar);

    /// <summary>The status bar's parts as one line: empty parts and repeats left out.</summary>
    public static string Words(IEnumerable<string> parts) =>
        string.Join("  |  ", parts.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct());
}
