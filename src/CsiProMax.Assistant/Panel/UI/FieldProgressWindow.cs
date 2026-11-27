using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CsiProMax.UI;

/// <summary>
/// A small window that shows a running field test live: the part and step it is at, a progress bar, step X of about N,
/// passed / failed / not tested so far, and the time it has run. Ahmed, 2026-11-26: the field test "needs urgently to
/// show progress". It opens when the test starts (the Help menu or the chat), in ETABS, SAP2000, SAFE and the Modeler,
/// and closes itself when the test is done, when the summary message takes over. Closing it does not stop the test.
/// </summary>
internal sealed class FieldProgressWindow : Window
{
    private readonly CsiProMax.Assistant.FieldTest.Running _run;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _part = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _step = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 8) };
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _count = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _tally = new() { Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _time = new() { Margin = new Thickness(0, 2, 0, 0), Foreground = Brushes.DimGray };

    private static FieldProgressWindow? _open;

    private FieldProgressWindow(CsiProMax.Assistant.FieldTest.Running run)
    {
        _run = run;
        Title = "CSI ProMax field test";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = run.Only is { } only ? $"Field test: {only.Name} only" + (run.Retest ? " (only what is new, changed or failed)" : "")
                : run.Retest ? "Field test: only what is new, changed or failed" : run.Full ? "Field test: everything" : "Field test, quick",
            FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(_part);
        panel.Children.Add(_step);
        panel.Children.Add(_bar);
        panel.Children.Add(_count);
        panel.Children.Add(_tally);
        panel.Children.Add(_time);
        panel.Children.Add(new TextBlock
        {
            Text = "Leave the windows it opens alone. Closing this window does not stop the test; a message says when the report is ready. "
                 + (run.LiveFile is { } live ? $"Each step is also written as it ends to: {live}" : "You can also ask the chat how the field test is going."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
        });
        Content = panel;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => { _timer.Stop(); if (_open == this) _open = null; };
        Refresh();
        _timer.Start();
    }

    /// <summary>Shows the window for a run that just started. Any thread; nothing when there is no CSI ProMax window.</summary>
    public static void Show(CsiProMax.Assistant.FieldTest.Running run)
    {
        var ui = App.Ui;
        if (ui is null) return;
        ui.BeginInvoke(new Action(() =>
        {
            try
            {
                _open?.Close();
                _open = new FieldProgressWindow(run);
                _open.Show();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the summary message still says when it is done */ }
        }));
    }

    /// <summary>
    /// The same window for a test with no CSI ProMax window (the command line, a test started for Ahmed from a session
    /// on his PC): Ahmed 2026-11-27, "why when we do a full test we don't see the progress". It runs on a thread of its
    /// own, opens without taking the focus from what the engineer is doing, and closes when the test is done.
    /// </summary>
    public static void ShowAlone(CsiProMax.Assistant.FieldTest.Running run)
    {
        if (!OperatingSystem.IsWindows()) return;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new FieldProgressWindow(run) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                var area = SystemParameters.WorkArea;
                window.Left = area.Right - window.Width - 16;
                window.Top = area.Bottom - 320;
                window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the console still says each step */ }
        }) { IsBackground = true, Name = "field test progress" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Refresh()
    {
        var p = CsiProMax.Assistant.FieldTest.ProgressLines.Of(_run);
        _part.Text = p.Part;
        _step.Text = p.Step;
        _bar.IsIndeterminate = p.Percent is null;
        if (p.Percent is { } percent) _bar.Value = percent;
        _count.Text = p.Count;
        _tally.Text = p.Tally;
        _tally.Foreground = _run.Failed > 0 ? Brushes.Firebrick : Brushes.ForestGreen;
        _time.Text = p.Time;
        if (_run.Task?.IsCompleted == true) { _timer.Stop(); Close(); }
    }
}
