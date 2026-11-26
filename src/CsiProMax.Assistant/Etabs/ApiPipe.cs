using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CsiProMaxPlugin;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The window's end of the pipe to a CSI program that runs on the .NET Framework (ETABS 21, SAP2000 25, SAFE 21 and
/// older). Their API files reach the program through .NET Remoting, which .NET 8 does not have, so every call goes
/// through the plug-in's <see cref="ApiBridge"/> instead: in the program itself when the window was opened from its
/// Tools menu, or in Windows PowerShell for a window of the program's own. The program's API file is still loaded here,
/// for its types only, so <see cref="EtabsApi"/> and <see cref="Tools.ApiCall"/> work through it as they do for
/// ETABS 23; only the calls go over the pipe (<see cref="Remote"/>).
/// </summary>
internal sealed class ApiPipe : IDisposable
{
    private readonly Stream _pipe;
    private readonly Process? _host;
    private readonly StringBuilder _hostSaid = new();
    private readonly object _gate = new();
    private bool _gone;

    /// <summary>The program's API file, loaded for its types.</summary>
    public Assembly Api { get; }

    /// <summary>The API interface of the first object: cSapModel from the plug-in, cOAPI from PowerShell.</summary>
    public Type RootType { get; }

    /// <summary>Where the program's side runs: "the plug-in in SAFE 20" or "Windows PowerShell".</summary>
    public string Via { get; }

    private ApiPipe(Stream pipe, Process? host, string via, Func<string, Assembly>? load = null)
    {
        _pipe = pipe;
        _host = host;
        Via = via;
        if (host is not null)
        {
            host.OutputDataReceived += (_, e) => Said(e.Data);
            host.ErrorDataReceived += (_, e) => Said(e.Data);
            try { host.BeginOutputReadLine(); host.BeginErrorReadLine(); } catch (InvalidOperationException) { /* not redirected */ }
        }
        var answer = Ask(w => w.Write(ApiWire.Hello), TimeSpan.FromMinutes(5));
        var type = answer.ReadString();
        var file = answer.ReadString();
        Api = (load ?? Assembly.LoadFrom)(file);
        RootType = Api.GetType(type, false) ?? throw new EtabsException($"The program's API file ({file}) has no {type}.");
    }

    private void Said(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_hostSaid) if (_hostSaid.Length < 4000) _hostSaid.AppendLine(line.Trim());
    }

    /// <summary>The program's first object, called through its API interface as <see cref="EtabsApi"/> calls any other.</summary>
    public EtabsApi Root() => new(new Remote(this, 0), RootType);

    /// <summary>
    /// The pipe of the plug-in in the program with this process id, or null when there is none (the window was not
    /// opened from that program's Tools menu, or the plug-in there is older than 0.4.41).
    /// </summary>
    public static ApiPipe? ToPlugin(int pid, string programName, int waitMs = 2000)
    {
        var client = new NamedPipeClientStream(".", ApiWire.PipeName(pid), PipeDirection.InOut);
        try { client.Connect(waitMs); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { client.Dispose(); return null; }
        try { return new ApiPipe(client, null, $"the CSI ProMax plug-in in {programName}"); }
        catch { client.Dispose(); throw; }
    }

    /// <summary>
    /// Starts Windows PowerShell 5.1 with the plug-in file beside CsiProMax.dll, and the program through its own API
    /// file there: <paramref name="mode"/> "start" opens a new window of the program, "attach" finds the running one
    /// (<paramref name="pid"/>'s, when the API can tell windows apart).
    /// </summary>
    public static ApiPipe ToPowerShell(CsiProgram program, string apiFile, string mode, int? pid)
    {
        var plugin = PluginFile();
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new EtabsException($"{program.Name} runs on the .NET Framework, and Windows PowerShell, which CSI ProMax reaches it through, is not on this PC ({powershell}).");
        var pipeName = "CsiProMax.Api.host." + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(plugin)!,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", HostCommand(plugin, apiFile, program.ProgId, pipeName, mode, pid ?? 0) })
            start.ArgumentList.Add(a);
        var host = Process.Start(start) ?? throw new EtabsException("Windows PowerShell did not start.");
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        var until = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try { client.Connect(1000); break; }
            catch (TimeoutException) when (!host.HasExited && DateTime.UtcNow < until) { }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                var said = host.HasExited ? (host.StandardError.ReadToEnd() + " " + host.StandardOutput.ReadToEnd()).Trim() : "";
                client.Dispose();
                try { if (!host.HasExited) host.Kill(); } catch (InvalidOperationException) { }
                host.Dispose();
                throw new EtabsException($"{program.Name} runs on the .NET Framework, and Windows PowerShell, which CSI ProMax reaches it through, did not answer" +
                    (said.Length > 0 ? ": " + Cut(said) : ".") + " Start CSI ProMax from " + program.Name + "'s Tools menu instead.");
            }
        }
        try { return new ApiPipe(client, host, "Windows PowerShell"); }
        catch
        {
            client.Dispose();
            try { if (!host.HasExited) host.Kill(); } catch (InvalidOperationException) { }
            host.Dispose();
            throw;
        }
    }

    /// <summary>The PowerShell line that serves the pipe: the plug-in file read as bytes (no file lock, no download mark), then ApiBridge.Host.</summary>
    internal static string HostCommand(string plugin, string apiFile, string progId, string pipe, string mode, int pid)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return "$ErrorActionPreference='Stop'; $a=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes(" + Q(plugin) + ")); " +
               "$a.GetType('CsiProMaxPlugin.ApiBridge').GetMethod('Host').Invoke($null, [object[]]@(" + Q(apiFile) + ", " + Q(progId) + ", " + Q(pipe) + ", " + Q(mode) + ", [int]" +
               pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "))";
    }

    /// <summary>The ETABS plug-in file beside CsiProMax.dll: it holds the bridge every program's PowerShell host uses.</summary>
    internal static string PluginFile()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "CsiProMaxPlugin.dll");
        return File.Exists(file) ? file : throw new EtabsException($"CsiProMaxPlugin.dll is missing beside CsiProMax.dll ({AppContext.BaseDirectory}): run the install line again.");
    }

    private static string Cut(string s) => s.Length > 400 ? s[..400] + "..." : s;

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_gone)
            {
                try { Ask(w => w.Write(ApiWire.Bye), TimeSpan.FromSeconds(5)); } catch (Exception) { /* gone already */ }
                _gone = true;
            }
            _pipe.Dispose();
        }
        if (_host is not null)
        {
            try { if (!_host.WaitForExit(5000)) _host.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _host.Dispose();
        }
    }

    /// <summary>A property of an object on the program's side.</summary>
    internal object? Get(Remote target, PropertyInfo property)
    {
        var answer = Ask(w =>
        {
            w.Write(ApiWire.Get);
            w.Write(target.Id);
            w.Write(ApiWire.Name(property.DeclaringType!));
            w.Write(property.Name);
        }, null);
        return ApiWire.Read(answer, property.PropertyType, FromHandle);
    }

    /// <summary>
    /// A method of an object on the program's side, as <see cref="MethodInfo.Invoke(object, object[])"/> would call
    /// it: what the program sends back for a ref or out parameter is put in <paramref name="values"/>, and the
    /// program's own error comes out as a <see cref="TargetInvocationException"/>.
    /// </summary>
    internal object? Call(Remote target, MethodInfo method, object?[] values)
    {
        var parameters = method.GetParameters();
        var answer = Ask(w =>
        {
            w.Write(ApiWire.Call);
            w.Write(target.Id);
            w.Write(ApiWire.Name(method.DeclaringType!));
            w.Write(method.Name);
            w.Write(parameters.Length);
            foreach (var p in parameters) w.Write(ApiWire.Name(p.ParameterType));
            for (var i = 0; i < parameters.Length; i++)
            {
                var value = i < values.Length ? values[i] : Type.Missing;
                var missing = value == Type.Missing;
                w.Write(missing);
                if (!missing) ApiWire.Write(w, value, HandleOf);
            }
        }, null);
        var result = ApiWire.Read(answer, method.ReturnType, FromHandle);
        var count = answer.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var back = ApiWire.Read(answer, i < parameters.Length ? parameters[i].ParameterType : null, FromHandle);
            if (i < values.Length && i < parameters.Length && parameters[i].ParameterType.IsByRef) values[i] = back;
        }
        return result;
    }

    private int HandleOf(object value) => value is Remote r && ReferenceEquals(r.Pipe, this)
        ? r.Id
        : throw new ArgumentException($"A {value.GetType().Name} cannot be sent to the program: only numbers, text, yes/no, lists of these and the program's own objects.");

    private object? FromHandle(int id, Type? _) => new Remote(this, id);

    /// <summary>
    /// One request and its answer. A program that has gone (the pipe broke) comes out as a <see cref="COMException"/>,
    /// as a closed program does through COM, so the link attaches again on the next call.
    /// </summary>
    private BinaryReader Ask(Action<BinaryWriter> write, TimeSpan? most)
    {
        byte[]? reply;
        lock (_gate)
        {
            if (_gone) throw new COMException("The program's window is gone.");
            var request = new MemoryStream();
            using (var w = ApiWire.Writer(request)) write(w);
            try
            {
                ApiWire.Send(_pipe, request.ToArray());
                var read = Task.Run(() => ApiWire.Receive(_pipe));
                if (most is { } limit && !read.Wait(limit)) { _gone = true; throw new COMException($"The program did not answer within {limit.TotalMinutes:0} minutes."); }
                reply = read.GetAwaiter().GetResult();
            }
            catch (IOException ex) { _gone = true; throw new COMException("The program's window is gone (" + ex.Message + ")."); }
            catch (ObjectDisposedException) { _gone = true; throw new COMException("The program's window is gone."); }
            if (reply is null) { _gone = true; throw new COMException("The program's window is gone." + HostSaid()); }
        }
        var r = ApiWire.Reader(reply);
        if (r.ReadByte() == 0) return r;
        var kind = r.ReadString();
        var message = r.ReadString();
        throw new TargetInvocationException(new ApiPipeException(kind, message));
    }

    private string HostSaid()
    {
        lock (_hostSaid) return _hostSaid.Length == 0 ? "" : " Windows PowerShell said: " + Cut(_hostSaid.ToString().Trim());
    }

    /// <summary>An object on the program's side, by its handle there.</summary>
    internal sealed class Remote(ApiPipe pipe, int id)
    {
        public ApiPipe Pipe { get; } = pipe;
        public int Id { get; } = id;
        public override string ToString() => $"the program's object {Id} ({Pipe.Via})";
    }
}

/// <summary>An error the program's API gave on the other side of the pipe: its kind there, and its message.</summary>
public sealed class ApiPipeException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}
