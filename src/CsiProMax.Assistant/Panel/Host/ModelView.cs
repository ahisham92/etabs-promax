using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Bridge;

namespace CsiProMax.Host;

/// <summary>
/// The 3D view of the open model in the PC's browser: the Viewer page, served on 127.0.0.1 by the chat's own server
/// under a secret address. In the Modeler it follows the model as the chat builds it. It only reads.
/// </summary>
internal static class ModelView
{
    public static void Attach(McpHttpServer server)
    {
        server.ViewHandler = Handle;
        server.ViewPost = PostAsync;
    }

    /// <summary>
    /// The Modeler's hand edits (HandModel): run on the window's thread, one at a time with the chat's own tool calls,
    /// so the two never change the model at once.
    /// </summary>
    internal static Task<(byte[] Body, string ContentType)?> PostAsync(string path, byte[] body) =>
        path == "chat" ? ChatAsync(body) : path == "edit" ? EditAsync(body) : Task.FromResult<(byte[] Body, string ContentType)?>(null);

    // Apart from PostAsync and ChatAsync, which are reached without WPF (the tests and the field test, chat off).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(byte[] Body, string ContentType)?> EditAsync(byte[] body)
    {
        var dispatcher = App.Dispatcher;
        using var request = System.Text.Json.JsonDocument.Parse(body);
        var element = request.RootElement.Clone();
        var answer = await dispatcher.InvokeAsync(app => HandModel.Handle(app.Link, element)).ConfigureAwait(false);
        return (Encoding.UTF8.GetBytes(answer.ToJsonString()), "application/json");
    }

    /// <summary>
    /// The chat inside the Viewer page: {"after":n,"send":"text"} reads the open conversation from item n on and, with
    /// send, sends a message first. With the AI chat off on this PC the answer is {"off":true} and the page hides its tab.
    /// </summary>
    internal static Task<(byte[] Body, string ContentType)?> ChatAsync(byte[] body) => ChatAsync(body, AiMode.On);

    /// <param name="on">Whether the AI chat is on in this window (AiMode.On; the field test asks with it off).</param>
    internal static Task<(byte[] Body, string ContentType)?> ChatAsync(byte[] body, bool on) =>
        on ? ChatOnAsync(body) : Task.FromResult<(byte[] Body, string ContentType)?>((Encoding.UTF8.GetBytes("{\"off\":true}"), "application/json"));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(byte[] Body, string ContentType)?> ChatOnAsync(byte[] body)
    {
        var after = 0;
        string? send = null;
        using (var request = System.Text.Json.JsonDocument.Parse(body.Length == 0 ? "{}"u8.ToArray() : body))
        {
            var root = request.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (root.TryGetProperty("after", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number && a.TryGetInt32(out var n)) after = n;
                if (root.TryGetProperty("send", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String) send = t.GetString()?.Trim();
            }
        }
        var ui = App.Ui ?? throw new InvalidOperationException("The CSI ProMax window is not open.");
        var answer = await ui.InvokeAsync(() => Chat.ChatPane.ForViewer(after, send));
        return (Encoding.UTF8.GetBytes(answer.ToJsonString()), "application/json");
    }

    private static int Revision(ICsiLink link) => link is OfflineEtabs offline ? offline.Revision : 0;

    /// <summary>The Modeler's own tables come along (they cost nothing); a CSI program's result tables do not.</summary>
    private static readonly string[] ModelerTables = ["Grid Definitions", "Frame Assignments - Section Properties", "Area Assignments - Section Properties", "Model Checks", "Quantities"];

    private static JsonObject Content(ICsiLink link) => link is OfflineEtabs
        ? Pack.Build(link, ModelerTables, [], [], null, null).Content
        : Pack.Build(link, [], [], [], null, null, withTables: false).Content;

    internal static (byte[] Body, string ContentType)? Handle(string path, string query)
    {
        if (App.Etabs?.Link is not { } link) return null;
        var revision = Revision(link);
        switch (path)
        {
            case "":
                return (Encoding.UTF8.GetBytes(Pack.LiveViewerHtml(Content(link), revision, chat: AiMode.On)), "text/html; charset=utf-8");
            case "model":
                if (query == "rev=" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) return ([], "application/json");
                return (Encoding.UTF8.GetBytes(new JsonObject { ["rev"] = revision, ["pack"] = Content(link) }.ToJsonString()), "application/json");
            default:
                return null;
        }
    }

    /// <summary>Opens the view in the default browser.</summary>
    public static void Open()
    {
        var server = ChatMcp.Ensure() ?? throw new InvalidOperationException("The view could not start on this PC (no local port).");
        Process.Start(new ProcessStartInfo(server.ViewUrl) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Starts the Modeler in a window of its own: the same program, with no ETABS behind it.</summary>
    public static void StartModeler()
    {
        var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("This program's own path is not known.");
        var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CsiProMax.dll"));
        info.ArgumentList.Add("--modeler");
        Process.Start(info)?.Dispose();
    }
}
