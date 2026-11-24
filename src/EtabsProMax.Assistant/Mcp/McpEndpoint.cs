using System.Text.Json;
using System.Text.Json.Nodes;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Assistant.Tools;

namespace EtabsProMax.Assistant.Mcp;

/// <summary>
/// The MCP server the chat's Claude Code talks to: one JSON-RPC message in, one out. It lists the ETABS tools and runs
/// them, asking the engineer first where a tool says so. No HTTP here, so the tests run it as it is.
/// </summary>
public sealed class McpEndpoint(IEtabsLink etabs, IReadOnlyList<Tool> tools, Func<Tool, string, Task<bool>> approve, Func<bool> askBeforeChanges)
{
    public const string ServerName = "etabs-promax";
    private const string Protocol = "2025-03-26";

    /// <summary>Raised around each tool call, for the chat window's activity line.</summary>
    public event Action<string, bool>? ToolRan;

    /// <summary>Answers one message. Null for a notification, which gets no answer.</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement message)
    {
        var method = message.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        if (!message.TryGetProperty("id", out var id)) return null;
        var idNode = JsonNode.Parse(id.GetRawText());
        message.TryGetProperty("params", out var p);
        switch (method)
        {
            case "initialize":
                var asked = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
                return Result(idNode, new JsonObject
                {
                    ["protocolVersion"] = asked ?? Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = typeof(McpEndpoint).Assembly.GetName().Version?.ToString(3) ?? "0" },
                });
            case "ping":
                return Result(idNode, new JsonObject());
            case "tools/list":
                return Result(idNode, new JsonObject
                {
                    ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = JsonNode.Parse(t.Schema),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = t.Ask == Ask.Never },
                    }).ToArray()),
                });
            case "tools/call":
                var name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var args = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("arguments", out var a) ? a : default;
                var (text, failed) = await CallAsync(name, args).ConfigureAwait(false);
                return Result(idNode, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = failed,
                });
            default:
                return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method } };
        }
    }

    /// <summary>Runs one tool. The text is its result as JSON, or what went wrong in words Claude can act on.</summary>
    public async Task<(string Text, bool Failed)> CallAsync(string name, JsonElement args)
    {
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return ($"There is no tool '{name}'.", true);
        if (args.ValueKind != JsonValueKind.Object) args = JsonDocument.Parse("{}").RootElement;
        try
        {
            if (tool.Ask == Ask.Always || tool.Ask == Ask.Changes && askBeforeChanges())
            {
                var summary = tool.Summary?.Invoke(args) ?? tool.Name;
                if (!await approve(tool, summary).ConfigureAwait(false))
                    return ("The engineer declined this. Nothing was changed. Ask what they want instead.", true);
            }
            var result = await Task.Run(() => tool.Run(args, etabs)).ConfigureAwait(false);
            ToolRan?.Invoke(name, true);
            return (result.ToJsonString(), false);
        }
        catch (Exception ex) when (ex is ToolInputException or EtabsException)
        {
            ToolRan?.Invoke(name, false);
            return (ex.Message, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        {
            ToolRan?.Invoke(name, false);
            return ($"The input could not be read ({ex.Message}). Check it against the tool's description.", true);
        }
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
}
