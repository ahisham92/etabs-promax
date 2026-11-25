using CsiProMax;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Host;

/// <summary>
/// The Modeler and the Viewer in CSI ProMax windows of their own (Ahmed, 25 Nov 2026: "why did the modeller open on
/// edge?", then "let both have their own windows so it feels like a real program"). Until then the 3D view's address
/// and the Viewer page were handed to Windows to open, which means the default browser.
/// </summary>
static class WebWindowTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Source(params string[] path) => File.ReadAllText(Path.Combine([SourceText.Root(), "src", "CsiProMax.Assistant", .. path]));

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("own windows: nothing hands the 3D view or the Viewer page to the browser any more", () =>
        {
            var view = Source("Panel", "Host", "ModelView.cs");
            Check(!view.Contains("ViewUrl) { UseShellExecute = true }", StringComparison.Ordinal), "the 3D view is opened in the default browser again (it opened in Edge)");
            Check(view.Contains("WebPages.Open(server.ViewUrl, Kind)", StringComparison.Ordinal), "the 3D view does not open in its CSI ProMax window");
            var pane = Source("Panel", "Host", "NoAiPane.cs");
            Check(!pane.Contains("WriteViewerPage()) { UseShellExecute = true }", StringComparison.Ordinal) && !pane.Contains("Process.Start(new ProcessStartInfo(page)", StringComparison.Ordinal), "the no-AI pane opens the Viewer in the browser again");
            Check(!pane.Contains("opens in your browser", StringComparison.Ordinal), "the no-AI pane still says the 3D view opens in the browser");
            Check(Source("Panel", "Host", "MainWindow.cs").Contains("Open the _Viewer", StringComparison.Ordinal), "the View menu (Tools > CSI ProMax in each program) has no Viewer");
            Check(Source("Program.cs").Contains("CsiProMax.Host.WebWindow.Install();", StringComparison.Ordinal), "the window never makes WebPages show pages in a CSI ProMax window");
            Check(Source("Panel", "Chat", "ChatUi.cs").Contains("WebPages.IsViewerPage(target)", StringComparison.Ordinal), "a review pack's page from the chat opens in the browser");
        });

        yield return ("own windows: only CSI ProMax's page loads inside them, other links go to the browser", () =>
        {
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new OfflineEtabs());
            WebWindowFieldSteps.Run(ctx);
            var bad = ctx.Steps.Where(s => !s.Ok && !s.Skipped).Select(s => s.Name + ": " + s.Detail).ToList();
            Check(bad.Count == 0, "the window steps pass here: " + string.Join("; ", bad));
            Check(ctx.Steps.Count(s => s.Ok && !s.Skipped) >= 4, "they ran: " + string.Join(", ", ctx.Steps.Select(s => s.Name)));
            Check(File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "FieldTest.cs")).Contains("WebWindowFieldSteps.Run(ctx);", StringComparison.Ordinal), "the field test does not check the windows");
            Check(WebPages.Address("http://127.0.0.1:5/view/x/") == "http://127.0.0.1:5/view/x/", "a served address is changed");
        });

        yield return ("own windows: the Viewer shortcut starts CsiProMax.dll --viewer, with no .exe and no browser", () =>
        {
            var arguments = Shortcuts.ViewerArguments("C:/CSI ProMax/");
            Check(arguments == "\"" + Path.Combine("C:/CSI ProMax/", "CsiProMax.dll") + "\" --viewer", arguments);
            var shortcuts = Source("Panel", "Host", "Shortcuts.cs");
            Check(!shortcuts.Contains("link.TargetPath = ViewerPage;", StringComparison.Ordinal), "the Viewer shortcut points at the page again (it opens in the browser)");
            Check(WindowFieldSteps.ViewerArguments("x.dll").SequenceEqual(["x.dll", "--viewer", "--this-version", "--no-update"]), "the field test does not start the Viewer as its shortcut does");
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new OfflineEtabs());
            WindowFieldSteps.Run(ctx, _ => new WindowFieldSteps.Seen(true, "'CSI ProMax' opened"), () => new WindowFieldSteps.Seen(false, "it ended with no window of its own"));
            Check(ctx.Steps.Count == 3 && !ctx.Steps[2].Ok && ctx.Steps[2].Name.Contains("Viewer", StringComparison.Ordinal), "a Viewer that does not open its own window is a failure of its own");
            var csproj = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "CsiProMax.Assistant.csproj"));
            Check(csproj.Contains("Microsoft.Web.WebView2", StringComparison.Ordinal) && csproj.Contains("<UseAppHost>false</UseAppHost>", StringComparison.Ordinal), "the window's WebView2 is missing, or an .exe is built");
        });
    }
}
