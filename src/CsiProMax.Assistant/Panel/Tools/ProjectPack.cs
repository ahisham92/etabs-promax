using System.IO;
using System.Text;

namespace CsiProMax.Tools;

/// <summary>What Sketch ProMax's project pack lends the chat: a safe file name and a saved chat as readable Markdown.</summary>
internal static class ProjectPack
{
    private const int ChatFileCap = 400_000;

    internal static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) || c is '#' or '%' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 80 ? clean.Substring(0, 80).Trim() : clean.Length == 0 ? "unnamed" : clean;
    }

    /// <summary>A saved in-ETABS chat as readable Markdown, tool calls and errors included; a long one keeps its end.</summary>
    internal static string RenderChat(CsiProMax.Chat.Conversation conv)
    {
        var sb = new StringBuilder();
        sb.AppendLine(conv.Name.Length > 0 ? $"# In-{Csi.Name} chat: {conv.Name} ({conv.Title})" : $"# In-{Csi.Name} chat: {conv.Title}");
        if (conv.Updated != default) sb.AppendLine($"Last updated {conv.Updated.ToLocalTime():yyyy-MM-dd HH:mm}");
        foreach (var item in conv.Items)
        {
            sb.AppendLine();
            switch (item.Kind)
            {
                case "user":
                    sb.AppendLine("## User");
                    if (item.Name.Length > 0) sb.AppendLine($"(attached: {item.Name})");
                    sb.AppendLine(item.Text);
                    break;
                case "assistant":
                    sb.AppendLine("## CSI ProMax");
                    sb.AppendLine(item.Text);
                    break;
                case "tool":
                    sb.AppendLine($"- Tool {item.Name}: {(item.State.Length > 0 ? item.State : "run")}" + (item.Text.Length > 0 ? $" ({item.Text})" : ""));
                    break;
                default:
                    sb.AppendLine($"_{item.Kind}: {item.Text}_");
                    break;
            }
        }
        var text = sb.ToString();
        if (text.Length > ChatFileCap)
        {
            var tail = text.Substring(text.Length - ChatFileCap);
            var nl = tail.IndexOf('\n');
            text = "… (earlier part left out to keep the pack small)\n" + (nl >= 0 ? tail.Substring(nl + 1) : tail);
        }
        return text;
    }
}
