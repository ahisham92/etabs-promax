using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CsiProMax.Host;

/// <summary>
/// A panel of a CSI ProMax page in a window of its own, so it can go on another screen (the page's popOut: a panel
/// dropped outside the window, or Window Position > Separate Window). The page asks for it with window.open; this window
/// hosts the new page on the same browser as the page that asked, so the panel's elements, moved into it, keep working.
/// It belongs to the window it came from. Closing it docks the panel back: the page sees it close.
/// </summary>
internal sealed class PanelWindow : Window
{
    private readonly WebView2 _web = new();
    private bool _asking, _released;

    private PanelWindow(Window? owner)
    {
        Owner = owner;
        ShowInTaskbar = false;
        Title = "CSI ProMax";
        if (CsiProMax.UI.Brand.Icon is { } icon) Icon = icon;
        CsiProMax.UI.Brand.UseTheme(this);
        Content = _web;
        Closing += TakeBackFirst;
        Closed += (_, _) =>
        {
            try { _web.Dispose(); } catch (InvalidOperationException) { }
        };
    }

    /// <summary>
    /// Before the window closes, the page takes its panel back (cpmBeforeClose): a page that closes takes the handlers of
    /// every element still in it, so the panel must have left it first. A page that doesn't answer within a second does
    /// not keep the window open.
    /// </summary>
    private async void TakeBackFirst(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_released || _web.CoreWebView2 is not { } core) return;
        e.Cancel = true;
        if (_asking) return;
        _asking = true;
        try { await Task.WhenAny(core.ExecuteScriptAsync("window.cpmBeforeClose && window.cpmBeforeClose()"), Task.Delay(1000)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _released = true;
        try { Close(); } catch (InvalidOperationException) { /* closed already, with the window it belongs to */ }
    }

    /// <summary>Opens the window the page asked for, at the place and size it gave, and hands its browser to the page.</summary>
    internal static async void Open(CoreWebView2NewWindowRequestedEventArgs request, CoreWebView2Environment environment, Window? owner)
    {
        var deferral = request.GetDeferral();
        PanelWindow? window = null;
        try
        {
            window = new PanelWindow(owner);
            // The size the page gave is its content's: the frame and title bar go around it.
            var features = request.WindowFeatures;
            window.Width = features.HasSize ? features.Width + 16 : 340;
            window.Height = features.HasSize ? features.Height + 39 : 520;
            if (features.HasPosition)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = features.Left;
                window.Top = features.Top;
            }
            else window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show();
            await window._web.EnsureCoreWebView2Async(environment);
            var core = window._web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var shown = window;
            core.DocumentTitleChanged += (_, _) => shown.Title = core.DocumentTitle is { Length: > 0 } title ? title : "CSI ProMax";
            // The page closes it when the panel docks back from its Window Position menu.
            core.WindowCloseRequested += (_, _) => shown.Close();
            // Its own page has loaded: the page that opened it moves the panel in now (cpmPopReady), not into a first
            // page that this load may have replaced.
            var name = System.Text.Json.JsonSerializer.Serialize(request.Name);
            core.NavigationCompleted += async (_, _) =>
            {
                try { await core.ExecuteScriptAsync($"window.opener && window.opener.cpmPopReady && window.opener.cpmPopReady({name})"); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            };
            request.NewWindow = core;
            request.Handled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The page gets no window, and its panel stays where it was.
            CsiProMax.Assistant.Program.NoteFrom("panel window", ex);
            request.Handled = true;
            window?.Close();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
