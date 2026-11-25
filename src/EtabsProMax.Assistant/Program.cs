using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtabsProMax.Assistant;
using EtabsProMax.Assistant.Chat;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Assistant.Mcp;
using EtabsProMax.Assistant.Tools;

// ETABS ProMax, the assistant: a small window beside ETABS. This program serves that window and the ETABS tools on
// this PC only (127.0.0.1, a free port, a secret made at each start) and runs the chat on the Claude Code installed here.
// It runs on the .NET 8 that ETABS 23 itself needs: dotnet EtabsProMax.dll, or from ETABS's Tools menu (the plug-in).

// --demo: a small model in memory instead of ETABS, to see the window on a PC without ETABS.
using var com = new ComEtabs();
IEtabsLink etabs = args.Contains("--demo") ? new MemoryEtabs() : com;
if (args.Contains("--selftest")) return SelfTest.Run(etabs, args.Contains("--changes"));

// Already running (started again from ETABS's menu): show its window again and leave.
if (!args.Contains("--no-window") && Running.Find() is { } running) { AppWindow.Open(running); return 0; }

var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var address = "";
ChatHub? hub = null;
hub = new ChatHub(etabs, () => (address + "/mcp", token));
var endpoint = new McpEndpoint(etabs, EtabsTools.All, (tool, summary) => hub.ApproveAsync(tool, summary), () => hub.AskBeforeChanges);
endpoint.ToolRan += hub.ToolRan;
var ended = new TaskCompletionSource();

using var server = new MiniHttp(async call =>
{
    // Every request carries the secret: the window in its address or a header, Claude Code as a bearer token.
    if (call.Query("k") != token && call.Header("X-EtabsProMax") != token && call.Header("Authorization") != "Bearer " + token)
    {
        await call.SendAsync(401);
        return;
    }
    JsonElement Body() => call.Body.Length == 0 ? JsonDocument.Parse("{}").RootElement : JsonDocument.Parse(call.Body).RootElement;
    try
    {
        switch (call.Method + " " + call.Path)
        {
            case "GET /":
                await using (var page = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!)
                {
                    using var copy = new MemoryStream();
                    await page.CopyToAsync(copy);
                    await call.SendAsync(200, "text/html; charset=utf-8", copy.ToArray());
                }
                break;
            case "GET /api/ping": await call.JsonAsync("{\"app\":\"etabs-promax\"}"); break;
            case "GET /api/state": await call.JsonAsync(hub.State().ToJsonString()); break;
            case "POST /api/send":
                try { hub.Send(Body().TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""); await call.JsonAsync("{}"); }
                catch (InvalidOperationException ex) { await call.JsonAsync(new JsonObject { ["error"] = ex.Message }.ToJsonString(), 409); }
                break;
            case "POST /api/stop": hub.Stop(); await call.JsonAsync("{}"); break;
            case "POST /api/new": hub.NewChat(); await call.JsonAsync("{}"); break;
            case "POST /api/approve":
                hub.Answer(Body().GetProperty("id").GetString() ?? "", Body().GetProperty("allow").GetBoolean());
                await call.JsonAsync("{}");
                break;
            case "POST /api/settings":
                if (Body().TryGetProperty("askBeforeChanges", out var ask)) hub.AskBeforeChanges = ask.GetBoolean();
                await call.JsonAsync("{}");
                break;
            case "POST /api/claude/install": ClaudeCli.StartInstall(); await call.JsonAsync("{}"); break;
            case "POST /api/claude/signin": hub.CheckClaude(); if (hub.Install is { } install) ClaudeCli.StartSignIn(install); await call.JsonAsync("{}"); break;
            case "POST /api/claude/check": hub.CheckClaude(); await call.JsonAsync(hub.State().ToJsonString()); break;
            case "POST /api/quit": await call.JsonAsync("{}"); ended.TrySetResult(); break;
            case "GET /api/events":
                var (id, events) = hub.Listen();
                try
                {
                    await call.StartEventsAsync();
                    while (true)
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(call.Gone);
                        wait.CancelAfter(15000);
                        string? line = null;
                        try { line = await events.ReadAsync(wait.Token); }
                        catch (OperationCanceledException) when (!call.Gone.IsCancellationRequested) { }
                        await call.EventAsync(line);
                    }
                }
                finally { hub.StopListening(id); }
            case "POST /mcp":
                var answer = await endpoint.HandleAsync(Body());
                if (answer is null) await call.SendAsync(202); else await call.JsonAsync(answer.ToJsonString());
                break;
            case "GET /mcp" or "DELETE /mcp": await call.SendAsync(405); break;
            default: await call.SendAsync(404); break;
        }
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        await call.JsonAsync(new JsonObject { ["error"] = "The request could not be read: " + ex.Message }.ToJsonString(), 400);
    }
});

var port = Array.IndexOf(args, "--port") is var at and >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var asked) ? asked : 0;
server.Start(port);
address = server.Address;
var window = address + "/?k=" + token;
Console.WriteLine("ETABS ProMax is running. Its window: " + window);
_ = Task.Run(hub.CheckClaude);

if (!args.Contains("--no-window"))
{
    Running.Write(window);
    AppWindow.Open(window);
    // The program ends a little after its window is closed.
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        while (hub.Listeners > 0 || DateTime.UtcNow - hub.LastListener < TimeSpan.FromSeconds(20)) await Task.Delay(2000);
        ended.TrySetResult();
    });
}
Console.CancelKeyPress += (_, e) => { e.Cancel = true; ended.TrySetResult(); };
await ended.Task;
if (!args.Contains("--no-window")) Running.Clear();
hub.Dispose();
return 0;
