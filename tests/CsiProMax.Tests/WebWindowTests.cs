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
        yield return ("Modeler shortcut: a double-click opens it, also when the copy holding its name has no window (2026-11-26)", () =>
        {
            // "When I double click the modeller it does not open": a second start only asked the holder of
            // CsiProMax.modeler to come forward, and a holder with no window (closing after an update) answered nothing.
            var program = Source("Program.cs");
            Check(program.Contains("if (!first && !Handover(one, show, shown, name, args)) return 0;", StringComparison.Ordinal), "a second start ends again without checking that the open one answered");
            Check(program.Contains("shown.Set();", StringComparison.Ordinal) && program.Contains("CsiProMax.Host.ModelView.Open();", StringComparison.Ordinal), "the open Modeler does not answer, or does not bring its 3D view back");
            Check(program.Contains("stopped.Wait(TimeSpan.FromSeconds(15))", StringComparison.Ordinal) && program.Contains("Environment.Exit(0);", StringComparison.Ordinal), "a closed window can stay behind as a process with no window");
            var now = DateTime.UtcNow;
            Check(CsiProMax.Assistant.Program.Holder.Parse(CsiProMax.Assistant.Program.Holder.Line(77, now)) is { Pid: 77 } h && h.Ticks == now.Ticks, "the holder file is not read back");
            Check(CsiProMax.Assistant.Program.Holder.Parse("garbage") is null, "a broken holder file is read as a process");
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new OfflineEtabs());
            ModelerStartFieldSteps.Run(ctx, full: true, () => (new WindowFieldSteps.Seen(true, "'CSI ProMax Modeler' opened"), new WindowFieldSteps.Seen(false, "the second start did not end within 30 seconds")));
            var step = ctx.Steps.Single(s => s.Name.Contains("second double-click", StringComparison.Ordinal));
            Check(!step.Ok && !step.Skipped, "a second double-click nobody answers is a failure");
            Check(File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "FieldTest.cs")).Contains("ModelerStartFieldSteps.Run(ctx,", StringComparison.Ordinal), "the field test does not start the Modeler");
        });

        yield return ("Modeler and Viewer: the X beside the Chat tab closes the side panel, and the chat's expand button fills the screen (2026-11-26)", () =>
        {
            var html = CsiProMax.Assistant.Tools.Pack.ViewerHtml();
            Check(!html.Contains("data-close=\"right\"", StringComparison.Ordinal) && html.Contains("data-close=\"side\"", StringComparison.Ordinal), "the side panel's X names an action the page does not have");
            var big = Source("Panel", "Chat", "ChatPane.Big.cs");
            Check(big.Contains("Parent is not ContentControl && Window.GetWindow(this) is { } own", StringComparison.Ordinal), "the expand button does nothing in CSI ProMax's own window (it only knew a dock)");
        });

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
