using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Assistant.Tools;

namespace EtabsProMax.Assistant.Chat;

/// <summary>
/// The chat between the window and Claude Code: starts the engine, sends the engineer's messages, turns Claude Code's
/// stream into what the window shows, and holds the questions the window asks before a change ("Allow" or "Don't").
/// </summary>
public sealed class ChatHub(IEtabsLink etabs, Func<(string Url, string Token)> mcp) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<JsonObject> _history = [];
    private readonly ConcurrentDictionary<Guid, Channel<string>> _listeners = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _approvals = new();
    private Process? _engine;
    private string? _session;
    private bool _busy;

    public bool AskBeforeChanges { get; set; }
    public ClaudeCli.Install? Install { get; private set; }
    public ClaudeCli.SignIn SignIn { get; private set; } = ClaudeCli.SignIn.Unknown;
    public int Listeners => _listeners.Count;
    public DateTime LastListener { get; private set; } = DateTime.UtcNow;

    /// <summary>Looks for Claude Code again (after an install or a sign-in).</summary>
    public void CheckClaude()
    {
        Install = ClaudeCli.Find();
        SignIn = Install is null ? ClaudeCli.SignIn.Unknown : ClaudeCli.CheckSignIn(Install);
    }

    public JsonObject State()
    {
        JsonNode? model;
        try
        {
            var info = etabs.Info();
            model = new JsonObject { ["file"] = info.File.Length > 0 ? Path.GetFileName(info.File) : "(not saved yet)", ["etabs"] = info.Version, ["units"] = $"{info.Force}, {info.Length}", ["locked"] = info.Locked };
        }
        catch (EtabsException ex) { model = new JsonObject { ["error"] = ex.Message }; }
        lock (_gate)
            return new JsonObject
            {
                ["claude"] = Install is null ? "missing" : SignIn == ClaudeCli.SignIn.SignedOut ? "signed-out" : "ready",
                ["claudeVersion"] = Install?.Version,
                ["model"] = model,
                ["busy"] = _busy,
                ["askBeforeChanges"] = AskBeforeChanges,
                ["history"] = new JsonArray(_history.Select(h => h.DeepClone()).ToArray()),
                ["version"] = typeof(ChatHub).Assembly.GetName().Version?.ToString(3),
            };
    }

    /// <summary>The window's event stream: every event from now on, as JSON lines.</summary>
    public (Guid Id, ChannelReader<string> Events) Listen()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<string>();
        _listeners[id] = channel;
        return (id, channel.Reader);
    }

    public void StopListening(Guid id)
    {
        if (_listeners.TryRemove(id, out var channel)) channel.Writer.TryComplete();
        LastListener = DateTime.UtcNow;
    }

    private void Emit(JsonObject ev)
    {
        var line = ev.ToJsonString();
        foreach (var listener in _listeners.Values) listener.Writer.TryWrite(line);
    }

    private void Remember(string role, string text)
    {
        lock (_gate) _history.Add(new JsonObject { ["role"] = role, ["text"] = text });
    }

    /// <summary>Asks the engineer in the window before a tool runs. False when they decline, or nobody answers in 30 minutes.</summary>
    public async Task<bool> ApproveAsync(Tool tool, string summary)
    {
        var id = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[id] = answer;
        Emit(new JsonObject { ["type"] = "approval", ["id"] = id, ["summary"] = summary, ["always"] = tool.Ask == Ask.Always });
        try { return await answer.Task.WaitAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        finally
        {
            _approvals.TryRemove(id, out _);
            Emit(new JsonObject { ["type"] = "approval-closed", ["id"] = id });
        }
    }

    public void Answer(string id, bool allow)
    {
        if (_approvals.TryGetValue(id, out var answer)) answer.TrySetResult(allow);
    }

    public void ToolRan(string name, bool ok) => Emit(new JsonObject { ["type"] = "tool", ["name"] = name, ["ok"] = ok });

    /// <summary>Sends the engineer's message to Claude Code, starting it when needed. The answer arrives as events.</summary>
    public void Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        lock (_gate)
        {
            if (_busy) throw new InvalidOperationException("Claude is still answering. Wait, or press Stop.");
            _busy = true;
        }
        Remember("user", text);
        Emit(new JsonObject { ["type"] = "user", ["text"] = text });
        _ = Task.Run(() =>
        {
            try
            {
                if (Install is null || SignIn == ClaudeCli.SignIn.SignedOut) CheckClaude();
                if (Install is null) throw new InvalidOperationException("Claude Code is not installed on this PC. Press Install Claude Code.");
                var engine = Engine(Install);
                var message = new JsonObject
                {
                    ["type"] = "user",
                    ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text + ModelNote() }) },
                };
                engine.StandardInput.WriteLine(message.ToJsonString());
                engine.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Finish(ex.Message, failed: true);
            }
        });
    }

    /// <summary>What is open and selected now, added to each message so Claude need not read it again.</summary>
    private string ModelNote()
    {
        try
        {
            var info = etabs.Info();
            var selected = etabs.Selected();
            var names = selected.Count == 0 ? "nothing"
                : string.Join(", ", selected.Take(60).Select(s => $"{s.Kind} {s.Name}")) + (selected.Count > 60 ? $" and {selected.Count - 60} more" : "");
            return $"\n\n[Model note from ETABS ProMax: file {(info.File.Length > 0 ? info.File : "not saved yet")}; units {info.Force}, {info.Length}; {(info.Locked ? "model is locked; " : "")}selected: {names}.]";
        }
        catch (EtabsException ex) { return $"\n\n[Model note from ETABS ProMax: {ex.Message}]"; }
    }

    private Process Engine(ClaudeCli.Install install)
    {
        lock (_gate)
        {
            if (_engine is { HasExited: false }) return _engine;
            var (url, token) = mcp();
            var process = Process.Start(ClaudeCli.EngineStart(install, url, token, _session))
                ?? throw new InvalidOperationException("Claude Code could not be started.");
            _engine = process;
            var errors = new System.Text.StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null && errors.Length < 4000) errors.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            _ = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) Read(line);
                // It ended: mid-answer means it failed (not signed in, or it crashed).
                bool wasBusy;
                lock (_gate) wasBusy = _busy && ReferenceEquals(_engine, process);
                if (wasBusy)
                {
                    var why = errors.ToString().Trim();
                    Finish(why.Length > 0 ? why : "Claude Code stopped before it answered. Check that it is signed in.", failed: true);
                }
            });
            return process;
        }
    }

    /// <summary>One line of Claude Code's stream-json output.</summary>
    internal void Read(string line)
    {
        if (line.Length == 0 || line[0] != '{') return;
        JsonElement root;
        try { root = JsonDocument.Parse(line).RootElement; }
        catch (JsonException) { return; }
        switch (Str(root, "type"))
        {
            case "system" when Str(root, "subtype") == "init":
                _session = Str(root, "session_id");
                if (root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                    foreach (var server in servers.EnumerateArray())
                        if (Str(server, "status") is var status and not ("connected" or "" or "pending"))
                            Emit(new JsonObject { ["type"] = "notice", ["text"] = $"The ETABS tools did not connect ({status}). Close this window and open ETABS ProMax again." });
                break;
            case "stream_event":
                if (root.TryGetProperty("event", out var ev) && Str(ev, "type") == "content_block_delta" &&
                    ev.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta")
                    Emit(new JsonObject { ["type"] = "text", ["text"] = Str(delta, "text") });
                else if (root.TryGetProperty("event", out ev) && Str(ev, "type") == "message_start")
                    Emit(new JsonObject { ["type"] = "break" });
                break;
            case "result":
                var failed = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                var text = Str(root, "result");
                if (failed && text.Contains("login", StringComparison.OrdinalIgnoreCase)) { SignIn = ClaudeCli.SignIn.SignedOut; text = "Claude Code is not signed in. Press Sign in."; }
                Finish(text, failed);
                break;
        }
    }

    private void Finish(string text, bool failed)
    {
        lock (_gate) _busy = false;
        Remember(failed ? "error" : "assistant", text);
        Emit(new JsonObject { ["type"] = "done", ["text"] = text, ["failed"] = failed });
    }

    /// <summary>Asks Claude Code to stop the current turn; ends the process if it doesn't within a few seconds.</summary>
    public void Stop()
    {
        Process? engine;
        lock (_gate) engine = _engine;
        if (engine is null || engine.HasExited) { lock (_gate) if (_busy) { } else return; Finish("Stopped.", failed: false); return; }
        foreach (var approval in _approvals.Values) approval.TrySetResult(false);
        try
        {
            engine.StandardInput.WriteLine("{\"type\":\"control_request\",\"request_id\":\"stop-" + Guid.NewGuid().ToString("N")[..8] + "\",\"request\":{\"subtype\":\"interrupt\"}}");
            engine.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000).ConfigureAwait(false);
            bool still;
            lock (_gate) still = _busy && ReferenceEquals(_engine, engine);
            if (!still) return;
            ClaudeCli.KillTree(engine);
        });
    }

    /// <summary>A new chat: the engine and its memory of this conversation are dropped.</summary>
    public void NewChat()
    {
        Process? engine;
        lock (_gate) { engine = _engine; _engine = null; _session = null; _history.Clear(); _busy = false; }
        if (engine is not null) ClaudeCli.KillTree(engine);
        Emit(new JsonObject { ["type"] = "reset" });
    }

    public void Dispose()
    {
        Process? engine;
        lock (_gate) { engine = _engine; _engine = null; }
        if (engine is not null) ClaudeCli.KillTree(engine);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
