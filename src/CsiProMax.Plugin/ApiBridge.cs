using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;

namespace CsiProMaxPlugin;

/// <summary>
/// Lets CSI ProMax's window (.NET 8) call the API of a CSI program that runs on the .NET Framework (ETABS 21 and
/// older, SAP2000 25 and older, SAFE 21 and older). Those programs' API files reach the program through .NET
/// Remoting, which .NET 8 does not have, so the window could never read a model there: SAFE 20's field test said
/// "The SAFE window this was started from is closed" for every read, and "gave no application object" for a second
/// window of SAFE 20, ETABS 20 and SAP2000 24 (2026-11-26).
/// Two hosts serve the same pipe:
/// - the plug-in, inside the program: Tools > CSI ProMax hands it the program's own model (<see cref="Serve"/>);
/// - Windows PowerShell 5.1 (the .NET Framework, on every Windows PC), for a window the field test starts itself or a
///   window not opened from the Tools menu (<see cref="Host"/>). No .exe of ours.
/// Calls name the API interface, the member and its parameter types, so the program's own API file decides what runs.
/// Everything here is by reflection: nothing of the program's API is needed to build or to load this file.
/// </summary>
public static class ApiBridge
{
    /// <summary>The pipe the plug-in serves in the program with this process id.</summary>
    public static string PipeName(int pid) => ApiWire.PipeName(pid);

    private static readonly object Gate = new();
    private static object? _model;
    private static bool _listening;

    /// <summary>
    /// Called by the plug-in each time Tools > CSI ProMax is picked: from then on the window reaches this program's model
    /// through the pipe. It never throws into the program.
    /// </summary>
    public static void Serve(object? model)
    {
        if (model is null) return;
        try
        {
            lock (Gate)
            {
                _model = model;
                if (_listening) return;
                _listening = true;
            }
            int pid;
            using (var me = Process.GetCurrentProcess()) pid = me.Id;
            var name = PipeName(pid);
            new Thread(() => Listen(name, () => _model, once: false)) { IsBackground = true, Name = "CSI ProMax API pipe" }.Start();
        }
        catch (Exception) { lock (Gate) _listening = false; }
    }

    /// <summary>
    /// Serves one window from Windows PowerShell: loads the program's API file, starts the program
    /// (<paramref name="mode"/> "start") or finds the running one ("attach": the one with <paramref name="pid"/> when the
    /// API can tell windows apart, else the program's active one), and answers that window's calls until it goes.
    /// What went wrong is answered to the window's first call, so it can say so.
    /// </summary>
    public static void Host(string apiFile, string progId, string pipe, string mode, int pid)
    {
        object? root = null;
        string? failed = null;
        var made = false;
        Listen(pipe, () =>
        {
            if (made) return root;
            made = true;
            try { root = Program(apiFile, progId, mode, pid); }
            catch (TargetInvocationException ex) { failed = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            catch (Exception ex) { failed = ex.GetType().Name + ": " + ex.Message; }
            if (root is null && failed is null)
                failed = mode == "start"
                    ? "The program's API gave no application object: is the program registered for the API on this PC? (Run its Register program once as administrator.)"
                    : "The program's API found no running window of it" + (pid > 0 ? " with process " + pid : "") + ".";
            return root;
        }, once: true, why: () => failed,
            // The program's object here may be a Remoting proxy that names no interface: it is the API file's cOAPI.
            describe: () => (Path.GetFileNameWithoutExtension(apiFile) + ".cOAPI", apiFile));
    }

    /// <summary>The program's application object (cOAPI), through its own API file's Helper.</summary>
    private static object? Program(string apiFile, string progId, string mode, int pid)
    {
        var api = Assembly.LoadFrom(apiFile);
        var ns = api.GetName().Name;
        var helper = Activator.CreateInstance(api.GetType(ns + ".Helper", true)!);
        var helperType = api.GetType(ns + ".cHelper", true)!;
        if (mode == "start") return helperType.GetMethod("CreateObjectProgID")!.Invoke(helper, new object[] { progId });
        var byProcess = pid > 0 ? helperType.GetMethod("GetObjectProcess") : null;
        var found = byProcess?.Invoke(helper, new object[] { progId, pid });
        // Older APIs have no GetObjectProcess, and some answer nothing for a window that was not started through the
        // API: then the program's active window, which is the one when only one is open.
        return found ?? helperType.GetMethod("GetObject")!.Invoke(helper, new object[] { progId });
    }

    private static void Listen(string name, Func<object?> root, bool once, Func<string?>? why = null, Func<(string, string)>? describe = null)
    {
        while (true)
        {
            NamedPipeServerStream server;
            try { server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None); }
            catch (Exception) { if (once) return; Thread.Sleep(2000); continue; }
            try { server.WaitForConnection(); }
            catch (Exception) { server.Dispose(); if (once) return; continue; }
            if (once) { Session(server, root, why, describe); return; }
            new Thread(() => Session(server, root, why, describe)) { IsBackground = true, Name = "CSI ProMax API call" }.Start();
        }
    }

    /// <summary>One window's calls, one after another, until it says bye or goes.</summary>
    private static void Session(NamedPipeServerStream pipe, Func<object?> root, Func<string?>? why, Func<(string, string)>? describe)
    {
        var handles = new List<object?> { null };
        var ids = new Dictionary<object, int>(ReferenceComparer.Instance);
        int HandleOf(object value)
        {
            if (ids.TryGetValue(value, out var id)) return id;
            handles.Add(value);
            return ids[value] = handles.Count - 1;
        }
        object? FromHandle(int id, Type? _) => id >= 0 && id < handles.Count ? handles[id] : null;
        using (pipe)
        {
            try
            {
                while (true)
                {
                    var request = ApiWire.Receive(pipe);
                    if (request is null) return;
                    var answer = new MemoryStream();
                    using (var w = ApiWire.Writer(answer))
                    {
                        var bye = false;
                        try
                        {
                            using var r = ApiWire.Reader(request);
                            var kind = r.ReadByte();
                            switch (kind)
                            {
                                case ApiWire.Hello:
                                {
                                    var model = root();
                                    handles[0] = model;
                                    if (model is null) throw new InvalidOperationException(why?.Invoke() ?? "The program's model is not there.");
                                    var (type, file) = describe?.Invoke() ?? Describe(model);
                                    w.Write((byte)0);
                                    w.Write(type);
                                    w.Write(file);
                                    break;
                                }
                                case ApiWire.Get:
                                {
                                    var target = Target(handles, r.ReadInt32());
                                    var property = Property(target, r.ReadString(), r.ReadString());
                                    var value = property.GetValue(target, null);
                                    w.Write((byte)0);
                                    ApiWire.Write(w, value, HandleOf);
                                    break;
                                }
                                case ApiWire.Call:
                                {
                                    var target = Target(handles, r.ReadInt32());
                                    var declaring = r.ReadString();
                                    var methodName = r.ReadString();
                                    var count = r.ReadInt32();
                                    var shape = new string[count];
                                    for (var i = 0; i < count; i++) shape[i] = r.ReadString();
                                    var method = Method(target, declaring, methodName, shape);
                                    var parameters = method.GetParameters();
                                    var values = new object?[parameters.Length];
                                    for (var i = 0; i < parameters.Length; i++)
                                    {
                                        // Left out by the window (an optional parameter with no default): the API's own default.
                                        values[i] = r.ReadBoolean() ? Type.Missing : ApiWire.Read(r, parameters[i].ParameterType, FromHandle);
                                    }
                                    var result = method.Invoke(target, values);
                                    w.Write((byte)0);
                                    ApiWire.Write(w, result, HandleOf);
                                    w.Write(values.Length);
                                    foreach (var value in values) ApiWire.Write(w, value == Type.Missing ? null : value, HandleOf);
                                    break;
                                }
                                case ApiWire.Bye:
                                    w.Write((byte)0);
                                    bye = true;
                                    break;
                                default: throw new InvalidOperationException("Unknown request " + kind + ".");
                            }
                        }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                            answer.SetLength(0);
                            w.Seek(0, SeekOrigin.Begin);
                            w.Write((byte)1);
                            w.Write(inner.GetType().Name);
                            w.Write(inner.Message);
                        }
                        w.Flush();
                        ApiWire.Send(pipe, answer.ToArray());
                        if (bye) return;
                    }
                }
            }
            catch (Exception) { /* the window went: its handles go with it */ }
        }
    }

    private static object Target(List<object?> handles, int id) =>
        id >= 0 && id < handles.Count && handles[id] is { } target ? target : throw new InvalidOperationException("The program's object " + id + " is gone.");

    /// <summary>The API interface the window calls the root object through, and the API file that defines it.</summary>
    private static (string Type, string File) Describe(object model)
    {
        var face = Interfaces(model).FirstOrDefault(i => i.Name == "cSapModel") ?? Interfaces(model).FirstOrDefault(i => i.Name == "cOAPI")
            ?? throw new InvalidOperationException("The program gave an object that is not its API's model (" + model.GetType().FullName + ").");
        return (ApiWire.Name(face), face.Assembly.Location);
    }

    private static IEnumerable<Type> Interfaces(object target)
    {
        var type = target.GetType();
        return type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : type.GetInterfaces();
    }

    /// <summary>The API interface by its full name: one the object has, else the one in the API file (a Remoting proxy names no interfaces of its own).</summary>
    private static Type Interface(object target, string fullName) =>
        Interfaces(target).FirstOrDefault(i => ApiWire.Name(i) == fullName)
        ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => SafeType(a, fullName)).FirstOrDefault(t => t is not null && t.IsInstanceOfType(target))
        ?? throw new MissingMemberException("The program's object has no " + fullName + ".");

    private static Type? SafeType(Assembly a, string name)
    {
        try { return a.GetType(name, false); }
        catch (Exception) { return null; }
    }

    private static PropertyInfo Property(object target, string declaring, string name)
    {
        var face = Interface(target, declaring);
        return new[] { face }.Concat(face.GetInterfaces()).Select(t => t.GetProperty(name)).FirstOrDefault(p => p is not null)
            ?? throw new MissingMemberException("The program's " + face.Name + " has no " + name + ".");
    }

    private static MethodInfo Method(object target, string declaring, string name, string[] shape)
    {
        var face = Interface(target, declaring);
        foreach (var method in new[] { face }.Concat(face.GetInterfaces()).SelectMany(t => t.GetMethods()))
        {
            if (method.Name != name) continue;
            var parameters = method.GetParameters();
            if (parameters.Length != shape.Length) continue;
            var same = true;
            for (var i = 0; i < parameters.Length && same; i++) same = ApiWire.Name(parameters[i].ParameterType) == shape[i];
            if (same) return method;
        }
        throw new MissingMethodException("The program's " + face.Name + " has no " + name + " with these parameters (" + string.Join(", ", shape) + ").");
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
