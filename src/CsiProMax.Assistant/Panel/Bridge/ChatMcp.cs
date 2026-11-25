using System.Net.Sockets;
using System.Text.Json;
using CsiProMax.Settings;
using CsiProMax.Tools;

namespace CsiProMax.Bridge;

/// <summary>
/// The chat's connection to the ETABS tools, inside ETABS: an MCP server over HTTP on 127.0.0.1 (McpHttpServer) that the
/// chat's Claude Code reaches without starting a helper program. A locked-down PC may refuse to start the connector
/// program (CsiProMax.Mcp.exe), which isn't signed; ETABS already runs this DLL. It offers what the connector offers
/// the chat: the same tool list (ConnectorTools, from the running registry), the same calls (ETABSTargets, this window
/// reached through BridgeServer.Handle directly, others over their pipes), read_pdf_part, and each parallel job's key.
/// The desktop app keeps using the connector program.
///
/// Started with the core and stopped with it: a core switched to while ETABS runs starts its own, on a new port with a
/// new token, and the chat starts Claude Code again (resuming the conversation) when the address changed.
/// </summary>
internal static class ChatMcp
{
    private static readonly object Gate = new();
    private static BridgeServer? _bridge;

    /// <summary>The running server, or null when it couldn't start (the chat then uses the connector program).</summary>
    public static McpHttpServer? Current { get; private set; }

    /// <summary>Why the server couldn't start, for the field test; null when it runs or wasn't started.</summary>
    public static string? StartError { get; private set; }

    /// <summary>The Claude Code the chat runs, for read_pdf_part (set when the chat writes its config).</summary>
    public static string? Claude { get; set; }

    public static void Start(BridgeServer bridge)
    {
        lock (Gate)
        {
            _bridge = bridge;
            StartLocked();
        }
    }

    /// <summary>The server's address and token, starting it again first if it isn't running (a port refused at start).</summary>
    public static McpHttpServer? Ensure()
    {
        lock (Gate)
        {
            if (Current is null && _bridge is not null) StartLocked();
            return Current;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            Current?.Dispose();
            Current = null;
            _bridge = null;
        }
    }

    private static void StartLocked()
    {
        if (Current is not null || _bridge is null) return;
        var server = Create(_bridge);
        try
        {
            server.Start();
            Current = server;
            StartError = null;
        }
        catch (SocketException ex)
        {
            server.Dispose();
            StartError = ex.Message;
        }
    }

    /// <summary>A server for <paramref name="bridge"/>'s tools, not started (the field test starts one of its own).</summary>
    internal static McpHttpServer Create(BridgeServer bridge)
    {
        var registry = bridge.Registry;
        // ETABS's own tools don't change while this core runs; the learned ones are read again at each listing.
        var etabsTools = new Lazy<List<ConnectorTools.Def>>(() => registry.All.Select(t => new ConnectorTools.Def(
            t.Name, ToolSchema.Description(t), JsonSerializer.SerializeToElement(ToolSchema.InputSchema(t)))).ToList());
        var endpoint = new McpEndpoint("csi-promax", Update.Updater.CurrentText,
            () => Agent.SystemPrompt.Build(AgentSettings.Load().FirmStandards),
            _ => ConnectorTools.Compose(etabsTools.Value, LearnedTools.Folder),
            (session, name, args, ct) => Targets(session, bridge).CallAsync(name, args, ct));
        var server = new McpHttpServer(endpoint);
        Host.ModelView.Attach(server);
        return server;
    }

    /// <summary>The session's way to the ETABS windows: it keeps which window and model Claude works on, as a connector does.</summary>
    private static ETABSTargets Targets(McpSession session, BridgeServer bridge)
    {
        lock (session)
        {
            if (session.State is ETABSTargets known) return known;
            // Always the chat (ETABS asks before code, recipes and file writes), on this window unless Claude chooses another.
            var targets = new ETABSTargets(new ConnectorCaller(FromChat: true, session.Job, BridgeServer.SessionId, Claude, session.Model),
                (BridgeServer.PipeName, bridge.Handle));
            session.State = targets;
            return targets;
        }
    }
}
