using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CsiProMax.UI;

/// <summary>
/// The Update bar: one wide button in the accent colour across the top of every CSI ProMax window (the chat beside a
/// CSI program, the Modeler, the Viewer), there only while a newer version is out or downloaded. One click moves the
/// window to that version with nothing else closed (Ahmed, 2026-11-26: "I need the button of update in the chat at the
/// top very clear so that it is a one click").
/// </summary>
internal static class UpdateBar
{
    /// <summary>The bar's text, or null when the running version is the newest known (no bar).</summary>
    public static string? Text(Version current, string? latest, string? downloaded) =>
        UpdateBadge.TopButton(current, latest, downloaded) is { } text
            ? (text.EndsWith(" now", StringComparison.Ordinal) ? text : text + " now") + "  (one click, nothing is closed)"
            : null;

    /// <summary>The bar's text for this PC now: the newest release seen, and the newest version downloaded.</summary>
    public static string? Now()
    {
        try
        {
            return Text(Update.Updater.Current, Update.Updater.Latest,
                Update.Updater.Staged ?? (Update.Updater.NewestDownloaded(Update.Updater.Current) is { } folder ? Path.GetFileName(folder) : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly Brush Accent = Frozen(0x1C, 0x63, 0xBC), Hover = Frozen(0x19, 0x58, 0xA8);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The bar for a window: hidden until a newer version is known (looked at every 15 seconds while the window is
    /// open). Its colours are its own, so it looks the same in a window without the chat's theme (the Viewer).
    /// </summary>
    public static Button Build(Action click)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        var bar = new Button
        {
            Template = template, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Accent, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand, MinHeight = 36, Focusable = true,
        };
        AutomationProperties.SetName(bar, "Update CSI ProMax");
        AutomationProperties.SetAutomationId(bar, "CsiProMaxUpdateBar");
        bar.MouseEnter += (_, _) => bar.Background = Hover;
        bar.MouseLeave += (_, _) => bar.Background = Accent;
        var busyUntil = DateTime.MinValue;
        void Show()
        {
            if (DateTime.UtcNow < busyUntil) return;
            var text = Now();
            bar.Content = text;
            bar.IsEnabled = true;
            bar.ToolTip = text is null ? null : $"This window runs CSI ProMax {Update.Updater.CurrentText}. One click moves it to the newer version; your CSI program stays open and your model is not touched.";
            bar.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
        bar.Click += (_, _) =>
        {
            // Said at once, so one click is enough: the download and the switch follow by themselves.
            bar.Content = "Updating now... this window moves to the new version by itself";
            bar.IsEnabled = false;
            busyUntil = DateTime.UtcNow.AddMinutes(3);
            click();
        };
        var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        watch.Tick += (_, _) => Show();
        bar.Loaded += (_, _) => { Show(); watch.Start(); };
        bar.Unloaded += (_, _) => watch.Stop();
        bar.Tag = (Action)Show;
        return bar;
    }

    /// <summary>What a window's bar shows now, read again first (the field test compares it with what is known).</summary>
    public static (bool Shown, string Text) State(Button bar)
    {
        (bar.Tag as Action)?.Invoke();
        return (bar.Visibility == Visibility.Visible, bar.Content as string ?? "");
    }
}
