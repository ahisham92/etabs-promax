using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EtabsProMax.ETABS;

public enum TaskDialogCommandLinkId { CommandLink1 = 1001, CommandLink2, CommandLink3, CommandLink4 }

public enum TaskDialogResult { None = 0, Ok = 1, Cancel = 2, Yes = 6, No = 7, Close = 8, CommandLink1 = 1001, CommandLink2, CommandLink3, CommandLink4 }

[Flags]
public enum TaskDialogCommonButtons { None = 0, Ok = 1, Yes = 2, No = 4, Cancel = 8, Close = 32 }

/// <summary>
/// The question dialog Sketch ProMax asks through Revit (an instruction, a line under it, details that open, up to four
/// choices each with a note, common buttons and a tick box), drawn here in ETABS ProMax's own window style. Same members,
/// so the approvals and every "are you sure" read the same in both products. Shown over the ETABS ProMax window, on top,
/// so a question never hides behind ETABS.
/// </summary>
public sealed class TaskDialog(string title)
{
    private readonly List<(TaskDialogCommandLinkId Id, string Text, string Note)> _links = [];
    private bool _ticked;

    public string Title { get; set; } = title;
    public string MainInstruction { get; set; } = "";
    public string MainContent { get; set; } = "";
    public string ExpandedContent { get; set; } = "";
    public string FooterText { get; set; } = "";
    public string VerificationText { get; set; } = "";
    public bool AllowCancellation { get; set; } = true;
    public bool TitleAutoPrefix { get; set; }
    public TaskDialogCommonButtons CommonButtons { get; set; }
    public TaskDialogResult DefaultButton { get; set; }

    public void AddCommandLink(TaskDialogCommandLinkId id, string text, string note = "") => _links.Add((id, text, note));

    public bool WasVerificationChecked() => _ticked;

    public static TaskDialogResult Show(string title, string text)
    {
        var dialog = new TaskDialog(title) { MainContent = text, CommonButtons = TaskDialogCommonButtons.Close };
        return dialog.Show();
    }

    public static TaskDialogResult Show(string title, string text, TaskDialogCommonButtons buttons, TaskDialogResult fallback = TaskDialogResult.None)
    {
        var dialog = new TaskDialog(title) { MainContent = text, CommonButtons = buttons, DefaultButton = fallback };
        return dialog.Show();
    }

    public TaskDialogResult Show()
    {
        // Off the window's thread (a tool on a background thread): ask there and wait.
        var ui = EtabsProMax.App.Ui;
        if (ui is not null && !ui.CheckAccess()) return ui.Invoke(Show);

        var result = AllowCancellation ? TaskDialogResult.Cancel : TaskDialogResult.None;
        var window = new Window
        {
            Title = Title, SizeToContent = SizeToContent.Height, Width = 520, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Topmost = true,
        };
        UI.Brand.UseTheme(window);
        if (UI.Brand.Icon is { } icon) window.Icon = icon;
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is not null && owner.IsLoaded && owner != window) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        if (MainInstruction.Length > 0)
        {
            var head = UI.Brand.Text(MainInstruction, UI.Brand.K.Text, 16);
            head.FontWeight = FontWeights.SemiBold;
            head.TextWrapping = TextWrapping.Wrap;
            head.Margin = new Thickness(0, 0, 0, 8);
            root.Children.Add(head);
        }
        if (MainContent.Length > 0)
        {
            var body = UI.Brand.Text(MainContent, UI.Brand.K.Text, 13);
            body.TextWrapping = TextWrapping.Wrap;
            body.Margin = new Thickness(0, 0, 0, 10);
            root.Children.Add(body);
        }
        if (ExpandedContent.Length > 0)
        {
            var details = new TextBox
            {
                Text = ExpandedContent, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"),
                Background = Brushes.Transparent,
            };
            details.SetResourceReference(Control.ForegroundProperty, UI.Brand.K.Muted);
            var expander = new Expander { Header = "Details", Content = details, Margin = new Thickness(0, 0, 0, 10) };
            expander.SetResourceReference(Control.ForegroundProperty, UI.Brand.K.Muted);
            root.Children.Add(expander);
        }
        Button? first = null;
        void Close(TaskDialogResult r) { result = r; window.Close(); }
        foreach (var (id, text, note) in _links)
        {
            var content = new StackPanel();
            var line = UI.Brand.Text(text, UI.Brand.K.Text, 13.5);
            line.FontWeight = FontWeights.SemiBold;
            line.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(line);
            if (note.Length > 0)
            {
                var sub = UI.Brand.Text(note, UI.Brand.K.Muted, 12);
                sub.TextWrapping = TextWrapping.Wrap;
                content.Children.Add(sub);
            }
            var button = new Button
            {
                Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, text);
            if (note.Length > 0) System.Windows.Automation.AutomationProperties.SetHelpText(button, note);
            var answer = (TaskDialogResult)(int)id;
            button.Click += (_, _) => Close(answer);
            if (DefaultButton == answer) { button.IsDefault = true; first = button; }
            root.Children.Add(button);
        }
        CheckBox? tick = null;
        if (VerificationText.Length > 0)
        {
            tick = new CheckBox { Margin = new Thickness(0, 6, 0, 0) };
            var label = UI.Brand.Text(VerificationText, UI.Brand.K.Text, 12);
            label.TextWrapping = TextWrapping.Wrap;
            tick.Content = label;
            root.Children.Add(tick);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        void Common(TaskDialogCommonButtons flag, string text, TaskDialogResult answer)
        {
            if ((CommonButtons & flag) == 0) return;
            var button = UI.Brand.Button(text, answer is TaskDialogResult.Ok or TaskDialogResult.Yes ? UI.Brand.Weight.Primary : UI.Brand.Weight.Secondary);
            button.Click += (_, _) => Close(answer);
            if (DefaultButton == answer || (DefaultButton == TaskDialogResult.None && first is null)) { button.IsDefault = true; first ??= button; }
            if (answer is TaskDialogResult.Cancel or TaskDialogResult.No or TaskDialogResult.Close) button.IsCancel = AllowCancellation;
            row.Children.Add(button);
        }
        var buttons = CommonButtons == TaskDialogCommonButtons.None && _links.Count == 0 ? TaskDialogCommonButtons.Close : CommonButtons;
        CommonButtons = buttons;
        Common(TaskDialogCommonButtons.Ok, "OK", TaskDialogResult.Ok);
        Common(TaskDialogCommonButtons.Yes, "Yes", TaskDialogResult.Yes);
        Common(TaskDialogCommonButtons.No, "No", TaskDialogResult.No);
        Common(TaskDialogCommonButtons.Cancel, "Cancel", TaskDialogResult.Cancel);
        Common(TaskDialogCommonButtons.Close, "Close", TaskDialogResult.Close);
        if (row.Children.Count > 0) root.Children.Add(row);
        if (FooterText.Length > 0)
        {
            var foot = UI.Brand.Text(FooterText, UI.Brand.K.Muted, 11.5);
            foot.TextWrapping = TextWrapping.Wrap;
            foot.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(foot);
        }
        window.Content = root;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && AllowCancellation) Close(TaskDialogResult.Cancel); };
        window.Loaded += (_, _) => { first?.Focus(); window.Activate(); };
        window.ShowDialog();
        _ticked = tick?.IsChecked == true;
        return result;
    }
}
