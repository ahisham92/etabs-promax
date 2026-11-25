using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EtabsProMax.Assistant;

/// <summary>One request to the small server: what was asked, and the ways to answer.</summary>
public sealed class HttpCall(string method, string path, Dictionary<string, string> query, Dictionary<string, string> headers, byte[] body, NetworkStream stream, CancellationToken gone)
{
    public string Method => method;
    public string Path => path;
    public byte[] Body => body;
    /// <summary>Cancelled when the server stops.</summary>
    public CancellationToken Gone => gone;
    public string Query(string name) => query.GetValueOrDefault(name, "");
    public string Header(string name) => headers.GetValueOrDefault(name, "");

    public Task SendAsync(int status, string contentType = "text/plain", string text = "") => SendAsync(status, contentType, Encoding.UTF8.GetBytes(text));

    public async Task SendAsync(int status, string contentType, byte[] content)
    {
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), gone).ConfigureAwait(false);
        await stream.WriteAsync(content, gone).ConfigureAwait(false);
    }

    public Task JsonAsync(string json, int status = 200) => SendAsync(status, "application/json", json);

    /// <summary>Starts an event stream (the window's live updates); lines follow with <see cref="EventAsync"/>.</summary>
    public Task StartEventsAsync() =>
        stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n: connected\n\n"), gone).AsTask();

    /// <summary>Sends one event, or a keep-alive line when <paramref name="data"/> is null. Throws IOException once the window is gone.</summary>
    public async Task EventAsync(string? data)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(data is null ? ": keep-alive\n\n" : "data: " + data + "\n\n"), gone).ConfigureAwait(false);
        await stream.FlushAsync(gone).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 413 => "Payload Too Large", _ => "Error",
    };
}

/// <summary>
/// A small HTTP server for this PC only, as in Sketch ProMax: 127.0.0.1, a free port, one request per connection. It
/// needs nothing beyond the .NET that ETABS 23 itself runs on, so there is no extra runtime to install.
/// </summary>
public sealed class MiniHttp(Func<HttpCall, Task> handle) : IDisposable
{
    private const int MaxHead = 64 * 1024, MaxBody = 32 * 1024 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    public int Port { get; private set; }
    public string Address => $"http://127.0.0.1:{Port}";

    /// <summary>Listens on 127.0.0.1 (a free port when 0). Throws SocketException when it can't.</summary>
    public void Start(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
                catch (SocketException) { await Task.Delay(300).ConfigureAwait(false); continue; }
                _ = Task.Run(() => ServeAsync(client));
            }
        });
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (AggregateException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                // Only this PC: the listener is on 127.0.0.1, and anything else is turned away here as well.
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address)) return;
                var stream = client.GetStream();
                var call = await ReadAsync(stream).ConfigureAwait(false);
                if (call is not null) await handle(call).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task<HttpCall?> ReadAsync(NetworkStream stream)
    {
        // The head: everything up to the empty line. A connection that doesn't send it in 30 seconds is closed.
        using var slow = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        slow.CancelAfter(30000);
        var buffer = new byte[8192];
        using var head = new MemoryStream();
        var end = -1;
        while (end < 0)
        {
            var read = await stream.ReadAsync(buffer, slow.Token).ConfigureAwait(false);
            if (read == 0) return null;
            head.Write(buffer, 0, read);
            end = IndexOfHeadEnd(head.GetBuffer(), (int)head.Length);
            if (end < 0 && head.Length > MaxHead) return null;
        }
        var bytes = head.ToArray();
        var lines = Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var target = first[1];
        var mark = target.IndexOf('?');
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mark >= 0)
            foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                query[Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        var call = (byte[] body) => new HttpCall(first[0].ToUpperInvariant(), mark >= 0 ? target[..mark] : target, query, headers, body, stream, _stop.Token);
        if (!int.TryParse(headers.GetValueOrDefault("Content-Length", "0"), out var length) || length < 0 || headers.ContainsKey("Transfer-Encoding"))
        {
            await call([]).SendAsync(400, text: "Send the body with Content-Length.").ConfigureAwait(false);
            return null;
        }
        if (length > MaxBody) { await call([]).SendAsync(413).ConfigureAwait(false); return null; }
        var body = new byte[length];
        var have = Math.Min(length, bytes.Length - (end + 4));
        Array.Copy(bytes, end + 4, body, 0, have);
        while (have < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(have), slow.Token).ConfigureAwait(false);
            if (read == 0) return null;
            have += read;
        }
        return call(body);
    }

    private static int IndexOfHeadEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) return i;
        return -1;
    }
}
