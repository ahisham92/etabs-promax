using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EtabsProMax.UI;

namespace EtabsProMax.Host;

/// <summary>
/// The ETABS ProMax window: Sketch ProMax's chat pane in a window of its own beside ETABS (ETABS has no dockable panes
/// for plug-ins). It can be resized and maximised, remembers where it was, and opens narrow on the right of the screen
/// the first time, like the pane in Revit. Closing it ends ETABS ProMax; ETABS and the model are not touched.
/// </summary>
internal sealed class MainWindow : Window
{
    private static MainWindow? _open;

    /// <summary>This window's handle (zero before it is shown): the owner of ETABS ProMax's dialogs and tool windows.</summary>
    public static IntPtr Handle { get; private set; }

    public static MainWindow? Current => _open;

    private static string PlaceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtabsProMax", "window.txt");

    public MainWindow()
    {
        _open = this;
        Title = "ETABS ProMax";
        MinWidth = 340;
        MinHeight = 420;
        if (Brand.Icon is { } icon) Icon = icon;
        Brand.UseTheme(this);
        Place();
        Content = Chat.ChatPane.Get();
        SourceInitialized += (_, _) => Handle = new WindowInteropHelper(this).Handle;
        Closing += (_, _) => Remember();
        Closed += (_, _) => { Handle = IntPtr.Zero; _open = null; };
    }

    /// <summary>Brings the window forward (ETABS's menu was used again while it is open).</summary>
    public void Surface()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        try
        {
            if (File.Exists(PlaceFile))
            {
                var f = File.ReadAllText(PlaceFile).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 5 && double.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l)
                    && double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)
                    && double.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
                    && double.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
                    && w >= MinWidth && h >= MinHeight
                    // Still on a screen that is there (a second monitor may be gone).
                    && l + 80 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && l + w > SystemParameters.VirtualScreenLeft + 80
                    && t >= SystemParameters.VirtualScreenTop - 10 && t + 60 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    (Left, Top, Width, Height) = (l, t, w, h);
                    if (f[4] == "max") WindowState = WindowState.Maximized;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // The first time: a narrow column on the right of the screen, full height, like the pane docked in Revit.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Min(480, area.Width * 0.4);
        Height = area.Height;
        Left = area.Right - Width;
        Top = area.Top;
    }

    private void Remember()
    {
        try
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (b.IsEmpty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(PlaceFile)!);
            var c = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllText(PlaceFile, string.Join(";", b.Left.ToString("0", c), b.Top.ToString("0", c), b.Width.ToString("0", c), b.Height.ToString("0", c),
                WindowState == WindowState.Maximized ? "max" : "normal"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
