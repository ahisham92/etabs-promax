using System.Text;
using System.Text.Json.Nodes;
using CsiProMax;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

/// <summary>
/// CSI ProMax with and without its AI chat (Ahmed, 25 Nov 2026: "what if i need to work without AI can someone install
/// just the package without AI"): the choice every version reads, the 3D view's page with and without its Chat tab, the
/// page's chat address when the chat is off, and the window without the chat.
/// </summary>
static class AiModeTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("no-AI: the choice in ai.txt is read the same way the installer writes it", () =>
        {
            Check(!AiMode.Means("off") && !AiMode.Means(" OFF\r\n") && !AiMode.Means("no") && !AiMode.Means("0"), "off turns the chat off");
            Check(AiMode.Means("on") && AiMode.Means("") && AiMode.Means(null) && AiMode.Means("yes"), "on, or nothing, keeps it on");
            var before = AiMode.File;
            var folder = Path.Combine(Path.GetTempPath(), "cpm-ai-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                AiMode.File = Path.Combine(folder, "ai.txt");
                AiMode.Load([]);
                Check(AiMode.On && !AiMode.Forced, "no file: the chat is on, as before");
                AiMode.Save(false);
                Check(File.ReadAllText(AiMode.File) == "off", "the window writes the same word as install.ps1 -NoAI");
                AiMode.Load([]);
                Check(!AiMode.On, "a saved off is read at the next start (and by a newer version)");
                AiMode.Save(true);
                AiMode.Load(["--demo", "--no-ai"]);
                Check(!AiMode.On && AiMode.Forced, "--no-ai turns it off for that window only");
                Check(AiMode.Saved(), "and leaves the PC's choice as it was");
            }
            finally
            {
                AiMode.File = Path.Combine(folder, "missing", "ai.txt");
                AiMode.Load([]);
                AiMode.File = before;
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
            Check(AiMode.On && !AiMode.Forced, "the tests go on with the chat on");
        });

        yield return ("no-AI: the 3D view's page has its Chat tab only when the window's chat is on", () =>
        {
            var content = Pack.Build(new OfflineEtabs(), [], [], [], null, null, withTables: false).Content;
            var without = Pack.LiveViewerHtml(content, 3, chat: false);
            var with = Pack.LiveViewerHtml(content, 3, chat: true);
            Check(!without.Contains("window.cpmChat=true") && with.Contains("window.cpmChat=true"), "the page is told whether the chat is on");
            Check(without.Contains("window.cpmLive=true") && with.Contains("window.cpmLive=true"), "both are the live view");
            Check(Pack.ViewerHtml().Contains("<button data-tab=\"chat\" hidden>Chat</button>"), "the Chat tab is hidden until the window turns it on");
            Check(!Pack.ViewerHtml(content).Contains("window.cpmChat=true"), "a pack's own Viewer page (a file sent to a reviewer) has no chat");
            Check(with.Contains("fetch(\"chat\"") && with.Contains("\"X-CPM-View\": \"1\""), "the page talks to its own window, with the header another site cannot send unasked");
        });

        yield return ("no-AI: the page's chat address answers off and sends nothing when the chat is off", () =>
        {
            var answer = CsiProMax.Host.ModelView.ChatAsync(Encoding.UTF8.GetBytes("""{"after":0,"send":"hello"}"""), on: false).GetAwaiter().GetResult();
            Check(answer is { } a && a.ContentType == "application/json" && JsonNode.Parse(a.Body)?["off"]?.GetValue<bool>() == true, "it answers {\"off\":true}");
            AiMode.Use(false);
            try
            {
                var routed = CsiProMax.Host.ModelView.PostAsync("chat", Encoding.UTF8.GetBytes("{}")).GetAwaiter().GetResult();
                Check(routed is { } r && JsonNode.Parse(r.Body)?["off"]?.GetValue<bool>() == true, "POST chat reaches the chat address, which follows the window's choice");
            }
            finally { AiMode.Use(true); }
            Check(CsiProMax.Chat.ChatPane.ShownInViewer("assistant") && CsiProMax.Chat.ChatPane.ShownInViewer("tool") && !CsiProMax.Chat.ChatPane.ShownInViewer("brief") && !CsiProMax.Chat.ChatPane.ShownInViewer("job"),
                "the page shows the messages and steps; cards stay in the window");
        });

        yield return ("no-AI: the window without the chat offers modelling, the 3D view, packs and the Viewer", () =>
        {
            var modeler = CsiProMax.Host.NoAiPane.Actions(modeler: true, "ETABS");
            var beside = CsiProMax.Host.NoAiPane.Actions(modeler: false, "SAP2000");
            Check(modeler[0].Contains("model by hand") && modeler.Contains("Open the models folder") && modeler.Any(t => t.Contains("Viewer")), "the Modeler: " + string.Join(" | ", modeler));
            Check(beside.Contains("Make a review pack of this model") && beside.Contains("Open the Modeler (no SAP2000 needed)") && beside[0].Contains("3D view"), "beside a program: " + string.Join(" | ", beside));
            Check(modeler[^1] == "Turn the AI chat on" && beside[^1] == "Turn the AI chat on", "and the way to turn the chat on");
        });

        yield return ("no-AI: the field test checks both ways, and skips Claude Code without the chat", () =>
        {
            var root = SourceText.Root();
            var field = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "FieldTest.cs"));
            Check(field.Contains("AiModeFieldSteps.Run(ctx);"), "the field test runs the no-AI steps");
            var pc = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Field", "PcFieldSteps.cs"));
            Check(pc.Contains("if (!CsiProMax.AiMode.On)") && pc.Contains("no Claude Code is needed"), "a PC without the chat does not fail for a missing Claude Code");
            var ctx = new CsiProMax.Assistant.FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new OfflineEtabs());
            CsiProMax.Assistant.AiModeFieldSteps.Run(ctx);
            var bad = ctx.Steps.Where(s => !s.Ok).Select(s => s.Name + ": " + s.Detail).ToList();
            Check(bad.Count == 0, "the no-AI steps pass here: " + string.Join("; ", bad));
            Check(ctx.Steps.Count(s => s.Ok && !s.Skipped) >= 6, "they ran: " + string.Join(", ", ctx.Steps.Select(s => s.Name)));
        });
    }
}
