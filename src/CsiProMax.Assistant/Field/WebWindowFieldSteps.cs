using System.Text;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Host;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test's steps for the Modeler's and the Viewer's own windows (Ahmed, 2026-11-25: the Modeler opened in
/// Edge; "let both have their own windows so it feels like a real program"). The 3D view, the Viewer and a review
/// pack's page open in a CSI ProMax window (WebWindow, the WebView2 that Windows has), never in a browser tab; links
/// that leave CSI ProMax's page go to the browser; the Viewer shortcut starts CSI ProMax itself (no .exe of ours), and
/// a pack given to it opens at once. Whether this PC has the WebView2 Runtime is said; the window itself is opened by
/// WindowFieldSteps in the full test.
/// </summary>
public static class WebWindowFieldSteps
{
    public static void Run(FieldContext ctx)
    {
        ctx.Step("the 3D view and the Viewer keep to their own window: only CSI ProMax's page loads in it", () =>
        {
            var view = new Uri("http://127.0.0.1:51234/view/secret/");
            FieldContext.Expect(WebPages.StaysInWindow(view, new Uri("http://127.0.0.1:51234/view/secret/model?rev=3")), "the 3D view's own address is sent to the browser");
            FieldContext.Expect(!WebPages.StaysInWindow(view, new Uri("https://example.com/")), "a link to another site loads inside the CSI ProMax window");
            FieldContext.Expect(!WebPages.StaysInWindow(view, new Uri("http://127.0.0.1:9999/")), "another program's local address loads inside the CSI ProMax window");
            var page = new Uri(Path.Combine(Path.GetTempPath(), "Model viewer.html"));
            FieldContext.Expect(WebPages.StaysInWindow(page, new Uri(page.AbsoluteUri + "#tables")), "the Viewer page's own parts are sent to the browser");
            FieldContext.Expect(!WebPages.StaysInWindow(page, new Uri(Path.Combine(Path.GetTempPath(), "other.html"))), "another file loads inside the Viewer window");
            FieldContext.Expect(WebPages.StaysInWindow(view, new Uri("blob:http://127.0.0.1:51234/1")), "a file the page saves (Excel, comments) is stopped");
            return "the window loads only its own page; other links open in the browser";
        });

        ctx.Step("the Modeler, the 3D view and the Viewer are named as CSI ProMax windows", () =>
        {
            var names = Enum.GetValues<WebPages.Kind>().Select(k => WebPages.Title(k, Csi.Name)).ToList();
            FieldContext.Expect(names.All(n => n.StartsWith("CSI ProMax", StringComparison.Ordinal)), "a window is not named CSI ProMax: " + string.Join(", ", names));
            FieldContext.Expect(WebPages.Maximised(WebPages.Kind.ModelerView) && WebPages.Maximised(WebPages.Kind.Viewer), "the Modeler and the Viewer do not open full size, as a program does");
            return string.Join("; ", names);
        });

        ctx.Step("the Viewer shortcut starts CSI ProMax's own Viewer window, not a page in the browser", () =>
        {
            var arguments = Shortcuts.ViewerArguments(AppContext.BaseDirectory);
            FieldContext.Expect(arguments.Contains("CsiProMax.dll", StringComparison.Ordinal) && arguments.EndsWith("--viewer", StringComparison.Ordinal), "the Viewer shortcut does not start CsiProMax.dll --viewer: " + arguments);
            FieldContext.Expect(Shortcuts.Mark2 == "|3", "the shortcuts made by an older version are not made again once");
            return "dotnet " + arguments;
        });

        ctx.Step("a review pack given to the Viewer opens with the pack inside", () =>
        {
            var content = Pack.Build(new OfflineEtabs(), [], [], [], null, null, withTables: false).Content;
            var folder = Path.Combine(Path.GetTempPath(), "CsiProMax-field-viewer-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            try
            {
                var pack = Path.Combine(folder, "Field test pack.json");
                File.WriteAllText(pack, content.ToJsonString(), new UTF8Encoding(false));
                var page = WebPages.ViewerPageFor(pack);
                try
                {
                    FieldContext.Expect(WebPages.IsViewerPage(page), "the page made for the pack is not a Viewer page");
                    FieldContext.Expect(File.ReadAllText(page).Contains("<script id=\"pack\" type=\"application/json\">{", StringComparison.Ordinal), "the pack is not inside the page");
                    FieldContext.Expect(WebPages.Address(page).StartsWith("file:///", StringComparison.Ordinal), "the page's address is not a local file");
                    FieldContext.Expect(WebPages.ViewerPageFor(page) == Path.GetFullPath(page), "a Viewer page given to the Viewer is not shown as it is");
                    return "the pack opens at once, with nothing to pick";
                }
                finally { try { File.Delete(page); } catch (IOException) { } }
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });

        ctx.Step("every close (X) button of the 3D view and the Viewer closes its panel", () =>
        {
            var html = Pack.ViewerHtml();
            var closes = System.Text.RegularExpressions.Regex.Matches(html, "data-close=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
            FieldContext.Expect(closes.Count >= 2, "the page's close buttons were not found");
            var dead = closes.Where(c => !html.Contains("case \"" + c + "\":", StringComparison.Ordinal)).ToList();
            // 2026-11-26: the X beside the Chat tab named "right", which the page has no action for, so it did nothing.
            FieldContext.Expect(dead.Count == 0, "a close button does nothing (the page has no action for it): " + string.Join(", ", dead));
            return "closes: " + string.Join(", ", closes);
        });

        if (!OperatingSystem.IsWindows()) { ctx.Skip("this PC can draw CSI ProMax's own windows (WebView2 Runtime)", "Windows only"); return; }
        ctx.Step("this PC can draw CSI ProMax's own windows (WebView2 Runtime)", () => Runtime());
    }

    // Apart, so that WebView2's types are only reached on Windows.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string Runtime()
    {
        var version = WebWindow.RuntimeVersion();
        if (version.Length > 0) return "WebView2 Runtime " + version + ": the Modeler and the Viewer open in their own windows";
        // No runtime: the pages go to Edge's app window (no tabs, no address bar). That is still not a browser tab, but say it.
        FieldContext.Expect(WebPages.Edge() is not null, "this PC has neither the WebView2 Runtime nor Edge, so the Modeler and the Viewer open in the browser. Install the WebView2 Runtime (Evergreen) from Microsoft.");
        throw new FieldSkipException("this PC has no WebView2 Runtime: the Modeler and the Viewer open in Edge's app window (no tabs, no address bar) instead. The WebView2 Runtime (Evergreen, from Microsoft) gives them their own window.");
    }
}
