using System.Text;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test's steps for CSI ProMax with and without its AI chat (AiMode; install-no-ai.ps1). Whichever way this
/// PC has it, both ways are checked: that the no-AI window offers the Modeler, the 3D view, review packs and the Viewer,
/// that the 3D view's page has no Chat tab and its chat address says "off" without the chat, that the page has the tab
/// with it, and that a window without the chat started none of it. Nothing here changes the choice on this PC.
/// </summary>
public static class AiModeFieldSteps
{
    public static void Run(FieldContext ctx)
    {
        ctx.Step("the AI chat on this PC: on or off", () =>
        {
            var saved = CsiProMax.AiMode.Saved();
            return new
            {
                this_window = CsiProMax.AiMode.On ? "on" : "off",
                saved_for_this_pc = saved ? "on" : "off",
                started_with_no_ai = CsiProMax.AiMode.Forced,
                file = CsiProMax.AiMode.File,
                how_to_switch = "the CSI ProMax menu: Turn the AI chat on/off (or install with install-no-ai.ps1)",
            };
        });

        ctx.Step("the install choice is read the same way by every version", () =>
        {
            FieldContext.Expect(!CsiProMax.AiMode.Means("off") && !CsiProMax.AiMode.Means("OFF\r\n") && !CsiProMax.AiMode.Means("no"), "\"off\" does not turn the chat off");
            FieldContext.Expect(CsiProMax.AiMode.Means("on") && CsiProMax.AiMode.Means("") && CsiProMax.AiMode.Means(null), "\"on\", or no file, does not keep the chat on");
            return "off, no, false and 0 turn the chat off; on, anything else or no file keep it on";
        });

        // The 3D view's page, as the window serves it: one small model, with the chat and without it.
        JsonObject? content = null;
        try { content = Pack.Build(new OfflineEtabs(), [], [], [], null, null, withTables: false).Content; }
        catch (Exception ex) when (ex is InvalidOperationException or EtabsException) { ctx.Fail("the 3D view's page could be made for the checks below", ex.Message); }
        if (content is not null)
        {
            ctx.Step("without the AI chat: the 3D view's page has no Chat tab", () =>
            {
                var page = Pack.LiveViewerHtml(content, 1, chat: false);
                FieldContext.Expect(!page.Contains("window.cpmChat=true", StringComparison.Ordinal), "the page turns its chat on although the window has none");
                FieldContext.Expect(page.Contains("<button data-tab=\"chat\" hidden>", StringComparison.Ordinal), "the page's Chat tab is not hidden until the window turns it on");
                return "the Chat tab stays hidden; Draw, Assign, Define and Edit are there as with the chat";
            });
            ctx.Step("with the AI chat: the 3D view's page has the Chat tab", () =>
            {
                var page = Pack.LiveViewerHtml(content, 1, chat: true);
                FieldContext.Expect(page.Contains("window.cpmChat=true", StringComparison.Ordinal), "the page served with the chat on does not turn its Chat tab on");
                FieldContext.Expect(page.Contains("function chatStart()", StringComparison.Ordinal), "the page has no chat code");
                return "the page shows the window's own conversation in its Chat tab";
            });
        }

        // Ahmed 2026-11-26: the Engine works fully without the AI. Its menus, forms, Run and review pack are CSI ProMax's
        // own code, the same code the chat's tools run (HandModel, EngineRun, Pack), never something only the chat can do.
        if (content is not null)
            ctx.Step("without the AI chat: the Engine's Run, forms and results are there, as with the chat", () =>
            {
                var page = Pack.LiveViewerHtml(content, 1, chat: false);
                foreach (var (marker, what) in new[] { ("\"eng:run\"", "Analyze > Run Analysis"), ("\"eng:runDesign\"", "Run Analysis and Design"), ("\"eng:import\"", "Import > Results from a Review Pack"), ("function hmPost(", "the Define/Assign/Draw forms") })
                    FieldContext.Expect(page.Contains(marker, StringComparison.Ordinal), $"the page without the chat has no {what}");
                var status = EngineRun.Status(false);
                FieldContext.Expect(status["running"] is not null, "the Engine's Run does not answer without the chat");
                return "Run, Run and Design, results from a pack and the forms are on the page without the chat; the Run answers (" + ((bool?)status["running"] == true ? "running" : "idle") + ")";
            });

        ctx.Step("without the AI chat: the chat address of the 3D view says the chat is off", () =>
        {
            var answer = CsiProMax.Host.ModelView.ChatAsync(Encoding.UTF8.GetBytes("""{"after":0,"send":"field test: never sent"}"""), on: false).GetAwaiter().GetResult();
            FieldContext.Expect(answer is { } a && JsonNode.Parse(a.Body)?["off"]?.GetValue<bool>() == true, "it did not answer {\"off\":true}");
            return "answers {\"off\":true}, and the page hides its tab; nothing was sent";
        });

        ctx.Step("without the AI chat: the window offers modelling, the 3D view, review packs and the Viewer", () =>
        {
            var modeler = CsiProMax.Host.NoAiPane.Actions(modeler: true, Csi.Name);
            var beside = CsiProMax.Host.NoAiPane.Actions(modeler: false, Csi.Name);
            FieldContext.Expect(modeler.Any(t => t.Contains("model by hand", StringComparison.Ordinal)) && modeler.Any(t => t.Contains("Viewer", StringComparison.Ordinal)), "the Modeler without the chat does not offer modelling by hand and the Viewer: " + string.Join(" | ", modeler));
            FieldContext.Expect(beside.Any(t => t.Contains("review pack", StringComparison.Ordinal)) && beside.Any(t => t.Contains("3D view", StringComparison.Ordinal)) && beside.Any(t => t.Contains("Modeler", StringComparison.Ordinal)),
                $"the window beside {Csi.Name} without the chat does not offer the 3D view, the review pack and the Modeler: " + string.Join(" | ", beside));
            return "Modeler: " + string.Join(", ", modeler) + ". Beside the program: " + string.Join(", ", beside);
        });

        const string noChat = "without the AI chat: this window started no chat and no Claude Code";
        if (CsiProMax.AiMode.On) ctx.Skip(noChat, "the AI chat is on in this window; a window without it (install-no-ai.ps1, or Turn the AI chat off) checks this");
        else
            ctx.Step(noChat, () =>
            {
                FieldContext.Expect(!CsiProMax.Chat.ChatPane.Made, "the chat pane was made although the AI chat is off");
                return "no chat pane, no Claude Code looked for or started";
            });
    }
}
