using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtabsProMax.Assistant;
using EtabsProMax.Assistant.Chat;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Assistant.Mcp;
using EtabsProMax.Assistant.Tools;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// ETABS ProMax, the assistant: a small window beside ETABS. This program serves that window and the ETABS tools on
// this PC only (127.0.0.1, a free port, a secret made at each start) and runs the chat on the Claude Code installed here.

// --demo: a small model in memory instead of ETABS, to see the window on a PC without ETABS.
using var com = new ComEtabs();
IEtabsLink etabs = args.Contains("--demo") ? new MemoryEtabs() : com;
if (args.Contains("--selftest")) return SelfTest.Run(etabs, args.Contains("--changes"));

var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var address = "";
ChatHub? hub = null;
hub = new ChatHub(etabs, () => (address + "/mcp", token));
var endpoint = new McpEndpoint(etabs, EtabsTools.All, (tool, summary) => hub.ApproveAsync(tool, summary), () => hub.AskBeforeChanges);
endpoint.ToolRan += hub.ToolRan;

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
var port = Array.IndexOf(args, "--port") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : "0";
builder.WebHost.UseUrls("http://127.0.0.1:" + port);
var app = builder.Build();

// Every request carries the secret: the window in its address or a header, Claude Code as a bearer token.
bool Allowed(HttpContext c) =>
    c.Request.Query["k"] == token || c.Request.Headers["X-EtabsProMax"] == token || c.Request.Headers.Authorization == "Bearer " + token;

app.Use(async (context, next) =>
{
    if (!Allowed(context)) { context.Response.StatusCode = 401; return; }
    await next();
});

app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store";
    await using var page = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
    await page.CopyToAsync(context.Response.Body);
});

app.MapGet("/api/state", () => Json(hub.State()));
app.MapPost("/api/send", async (HttpContext c) =>
{
    var body = await Body(c);
    try { hub.Send(body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""); return Results.Ok(); }
    catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
});
app.MapPost("/api/stop", () => { hub.Stop(); return Results.Ok(); });
app.MapPost("/api/new", () => { hub.NewChat(); return Results.Ok(); });
app.MapPost("/api/approve", async (HttpContext c) =>
{
    var body = await Body(c);
    hub.Answer(body.GetProperty("id").GetString() ?? "", body.GetProperty("allow").GetBoolean());
    return Results.Ok();
});
app.MapPost("/api/settings", async (HttpContext c) =>
{
    var body = await Body(c);
    if (body.TryGetProperty("askBeforeChanges", out var ask)) hub.AskBeforeChanges = ask.GetBoolean();
    return Results.Ok();
});
app.MapPost("/api/claude/install", () => { ClaudeCli.StartInstall(); return Results.Ok(); });
app.MapPost("/api/claude/signin", () => { hub.CheckClaude(); if (hub.Install is { } install) ClaudeCli.StartSignIn(install); return Results.Ok(); });
app.MapPost("/api/claude/check", () => { hub.CheckClaude(); return Json(hub.State()); });

app.MapGet("/api/events", async context =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-store";
    var (id, events) = hub.Listen();
    try
    {
        await context.Response.WriteAsync(": connected\n\n");
        await context.Response.Body.FlushAsync();
        while (!context.RequestAborted.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            wait.CancelAfter(15000);
            try
            {
                var line = await events.ReadAsync(wait.Token);
                await context.Response.WriteAsync("data: " + line + "\n\n");
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": keep-alive\n\n");
            }
            await context.Response.Body.FlushAsync();
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    finally { hub.StopListening(id); }
});

app.MapPost("/mcp", async context =>
{
    using var document = await JsonDocument.ParseAsync(context.Request.Body);
    var answer = await endpoint.HandleAsync(document.RootElement);
    if (answer is null) { context.Response.StatusCode = 202; return; }
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(answer.ToJsonString());
});
app.MapMethods("/mcp", ["GET", "DELETE"], context => { context.Response.StatusCode = 405; return Task.CompletedTask; });

await app.StartAsync();
address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
var window = address + "/?k=" + token;
Console.WriteLine("ETABS ProMax is running. Its window: " + window);
_ = Task.Run(hub.CheckClaude);

if (!args.Contains("--no-window"))
{
    AppWindow.Open(window);
    // The program ends a little after its window is closed.
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        while (hub.Listeners > 0 || DateTime.UtcNow - hub.LastListener < TimeSpan.FromSeconds(20)) await Task.Delay(2000);
        await app.StopAsync();
    });
}
await app.WaitForShutdownAsync();
hub.Dispose();
return 0;

static IResult Json(JsonObject o) => Results.Content(o.ToJsonString(), "application/json");
static async Task<JsonElement> Body(HttpContext c)
{
    using var document = await JsonDocument.ParseAsync(c.Request.Body);
    return document.RootElement.Clone();
}
