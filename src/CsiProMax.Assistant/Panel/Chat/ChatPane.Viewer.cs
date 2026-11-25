using System.Text.Json.Nodes;

namespace CsiProMax.Chat;

/// <summary>
/// The chat inside the Viewer page (the 3D view this window serves on 127.0.0.1): the same conversation as this pane,
/// not a second chat. The page asks for what is new and sends what the engineer types through ModelView (POST chat);
/// what it sends is handled exactly as if it had been typed here (<see cref="Ask"/>). Call on the window's thread.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>True once the chat pane was made in this window (never, with the AI chat off).</summary>
    internal static bool Made => _instance is not null;

    /// <summary>The kinds of item the page shows: the messages, the steps, notes and errors (cards stay in this pane).</summary>
    internal static bool ShownInViewer(string kind) => kind is "user" or "assistant" or "tool" or "note" or "error";

    /// <summary>
    /// The open conversation from item <paramref name="after"/> on (that one again, since an answer grows while it is
    /// written), whether a turn runs, and the status line. <paramref name="send"/>, when given, is sent first.
    /// </summary>
    internal static JsonObject ForViewer(int after, string? send)
    {
        var pane = (ChatPane)Get();
        var answer = new JsonObject();
        if (send is { Length: > 0 } text)
        {
            // What the engineer was typing in this pane stays there: the page's message goes in its place and out.
            var draft = pane._input.Text;
            var refused = Ask(text);
            if (refused.Length == 0 && pane._input.Text.Length == 0 && draft.Trim().Length > 0) pane._input.Text = draft;
            answer["sent"] = refused.Length == 0;
            if (refused.Length > 0) answer["said"] = refused;
        }
        var conv = pane._conv;
        var items = new JsonArray();
        var count = conv.Items.Count;
        for (var i = Math.Clamp(after, 0, count); i < count; i++)
        {
            var item = conv.Items[i];
            if (!ShownInViewer(item.Kind)) continue;
            items.Add(new JsonObject
            {
                ["i"] = i,
                ["kind"] = item.Kind,
                ["text"] = item.Kind == "tool" ? (item.Name.Length > 0 ? item.Name.Replace('_', ' ') : item.Text) : item.Text,
                ["state"] = item.State,
            });
        }
        answer["conv"] = conv.Id.Length > 0 ? conv.Id : conv.Key;
        answer["count"] = count;
        answer["items"] = items;
        answer["busy"] = pane._turn is not null;
        answer["ready"] = pane._setup == Setup.Ready;
        answer["status"] = pane._turn is not null ? pane._status.Text : "";
        return answer;
    }
}
