using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CsiProMax.Host;

/// <summary>
/// A CSI ProMax window that shows one of its pages (the Modeler's 3D view, the 3D view beside a CSI program, the
/// Viewer) with the Edge WebView2 that Windows already has: no tabs, no address bar, CSI ProMax's name and icon in the
/// title bar and the taskbar, so it is a program of its own and not a browser. One window per page: asked again, the
/// open one comes forward. Links that leave CSI ProMax's page open in the browser. If the page's drawing process
/// fails, the page is loaded again (the Modeler's model is in CSI ProMax, not in the page, so nothing is lost).
/// </summary>
internal sealed class WebWindow : Window
{
    private static readonly Dictionary<string, WebWindow> Open = new(StringComparer.OrdinalIgnoreCase);
    private static bool? _available;

    /// <summary>What a click on the Update bar of a page's window does in this process; null for no bar.</summary>
    internal static Action? UpdateClick { get; set; }

    private readonly string _address;
    private readonly WebPages.Kind _kind;

    /// <summary>Makes WebPages show pages here. Called once at start, on the window's thread.</summary>
    public static void Install()
    {
        if (!OperatingSystem.IsWindows()) return;
        WebPages.Show = ShowOnUi;
    }

    /// <summary>True when this PC has the WebView2 Runtime (it comes with Windows 10 and 11, and with Edge).</summary>
    internal static bool Available()
    {
        if (_available is { } known) return known;
        _available = RuntimeVersion().Length > 0;
        return _available.Value;
    }

    /// <summary>Set when a page could not be drawn on this PC after all: later pages go outside at once.</summary>
    internal static void NotAvailable() => _available = false;

    /// <summary>The WebView2 Runtime's version on this PC, or "" (the field test says it).</summary>
    internal static string RuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? ""; }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException or System.Runtime.InteropServices.COMException or TypeLoadException or System.IO.FileNotFoundException) { return ""; }
    }

    private static WebPages.Way ShowOnUi(string address, WebPages.Kind kind)
    {
        var ui = Application.Current?.Dispatcher;
        if (ui is null || !Available()) return WebPages.OpenOutside(address);
        if (ui.CheckAccess()) ShowHere(address, kind);
        else ui.Invoke(() => ShowHere(address, kind));
        return WebPages.Way.Window;
    }

    /// <summary>The open window of a page, if there is one (the field test looks at it).</summary>
    internal static WebWindow? Find(string address) => Open.TryGetValue(address, out var window) ? window : null;

    internal static Window ShowHere(string address, WebPages.Kind kind)
    {
        // The Modeler's 3D view is inside the Modeler's own window: that window comes forward.
        if (kind == WebPages.Kind.ModelerView && MainWindow.Current is { HasModelView: true } main)
        {
            main.Surface();
            return main;
        }
        if (Open.TryGetValue(address, out var open))
        {
            open.Surface();
            return open;
        }
        var window = new WebWindow(address, kind);
        Open[address] = window;
        window.Show();
        if (WebPages.Maximised(kind)) window.WindowState = WindowState.Maximized;
        window.Activate();
        return window;
    }

    private WebWindow(string address, WebPages.Kind kind)
    {
        _address = address;
        _kind = kind;
        Title = WebPages.Title(kind, Csi.Name);
        if (CsiProMax.UI.Brand.Icon is { } icon) Icon = icon;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1400, area.Width * 0.9);
        Height = Math.Min(900, area.Height * 0.9);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        var pane = new WebPane(address, kind);
        pane.Failed += () => Close();
        // The same Update bar as the chat's window, across the top: one click, and the page's window is not lost.
        if (UpdateClick is { } click)
        {
            var layout = new DockPanel();
            var bar = CsiProMax.UI.UpdateBar.Build(click);
            DockPanel.SetDock(bar, Dock.Top);
            layout.Children.Add(bar);
            layout.Children.Add(pane);
            Content = layout;
        }
        else Content = pane;
        Closed += (_, _) =>
        {
            Open.Remove(_address);
            pane.Dispose();
        };
    }

    private void Surface()
    {
        if (WindowState == WindowState.Minimized) WindowState = WebPages.Maximised(_kind) ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}

/// <summary>
/// One CSI ProMax page drawn by WebView2, as a part of a window: the whole of a WebWindow, or the Modeler window's
/// model view beside its chat. Only CSI ProMax's page loads in it (other links go to the browser); a failed drawing
/// process loads the page again; when WebView2 cannot start here the page goes to Edge's app window and Failed is
/// raised. When the page has loaded, a note is left for the field test (WebPages.NoteShown).
/// </summary>
internal sealed class WebPane : Border, IDisposable
{
    private readonly string _address;
    private readonly WebPages.Kind _kind;
    private readonly WebView2 _web = new();
    private int _restarts;

    /// <summary>The page could not be drawn here (it was opened outside instead).</summary>
    public event Action? Failed;

    public string Address => _address;

    public WebPane(string address, WebPages.Kind kind)
    {
        _address = address;
        _kind = kind;
        // The page's own colour (ETABS's forms), so it never flashes white or black while it loads.
        Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF0, 0xF0, 0xF0);
        _web.CoreWebView2InitializationCompleted += Ready;
        Child = _web;
        var started = false;
        Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            await StartAsync();
        };
    }

    public void Dispose()
    {
        try { _web.Dispose(); } catch (InvalidOperationException) { }
    }

    /// <summary>Loads the page again (View > Reload, or after its process failed).</summary>
    public void Reload()
    {
        try { _web.Reload(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private async Task StartAsync()
    {
        try
        {
            System.IO.Directory.CreateDirectory(WebPages.DataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, WebPages.DataFolder);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GiveUp(ex);
        }
    }

    /// <summary>The page could not be drawn on this PC: it opens in Edge's app window instead.</summary>
    private void GiveUp(Exception ex)
    {
        CsiProMax.Assistant.Program.NoteFrom("web window", ex);
        WebWindow.NotAvailable();
        try { WebPages.OpenOutside(_address); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        Failed?.Invoke();
    }

    private void Ready(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _web.CoreWebView2 is not { } core)
        {
            GiveUp(e.InitializationException ?? new InvalidOperationException("WebView2 did not start."));
            return;
        }
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        var page = new Uri(_address);
        core.NavigationStarting += (_, n) =>
        {
            if (!Uri.TryCreate(n.Uri, UriKind.Absolute, out var target) || WebPages.StaysInWindow(page, target)) return;
            n.Cancel = true;
            WebPages.OpenLink(n.Uri);
        };
        core.NavigationCompleted += (_, n) =>
        {
            if (n.IsSuccess) WebPages.NoteShown(_address, _kind);
        };
        core.NewWindowRequested += (_, n) =>
        {
            n.Handled = true;
            if (Uri.TryCreate(n.Uri, UriKind.Absolute, out var target) && WebPages.StaysInWindow(page, target) && target.Scheme is "http" or "https" or "file")
                WebWindow.ShowHere(n.Uri, _kind == WebPages.Kind.ModelerView ? WebPages.Kind.ProgramView : _kind);
            else WebPages.OpenLink(n.Uri);
        };
        core.ProcessFailed += (_, f) =>
        {
            // The page's drawing process ended or hung: load it again, a few times at most, and say so in the notes.
            CsiProMax.Assistant.Program.NoteFrom("web window", new InvalidOperationException($"WebView2 {f.ProcessFailedKind} ({f.Reason}) on {_address}"));
            if (_restarts++ >= 3) return;
            Reload();
        };
        core.Navigate(_address);
    }
}
