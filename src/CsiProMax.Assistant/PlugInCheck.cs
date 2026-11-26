using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace CsiProMax.Assistant;

/// <summary>
/// Whether each CSI program on this PC can create its CSI ProMax plug-in: what the program does when Tools > CSI ProMax
/// is picked, tried outside the program. It runs check.ps1 (the one line a tester can run alone, installed beside
/// CsiProMax.dll), so the field test and the tester's own check say the same thing.
///
/// It exists because CSI ProMax 0.4.4 could not be created by ETABS 20 ("Cannot create plugin!", listed as "Not Found")
/// while everything else on that PC looked right: the file was there and the list named it. Only trying it tells.
/// </summary>
public static class PlugInCheck
{
    /// <param name="Loads">True: created. False: cannot be. Null: could not be tried on this PC.</param>
    /// <param name="Path">The plug-in file the program's list names.</param>
    /// <param name="ApiFile">The program's own API file (ETABSv1.dll and the like), when its folder was found.</param>
    public sealed record Finding(string Program, bool Listed, bool? Loads, string Runtime, IReadOnlyList<string> Reasons, string Path = "", string ApiFile = "");

    public sealed record Result(string Text, IReadOnlyList<Finding> Programs);

    public const string FileName = "check.ps1";

    /// <summary>Runs the check. Null when it cannot run here (no check.ps1 beside CsiProMax.dll, or no PowerShell).</summary>
    /// <param name="shell">For tests: the PowerShell to use. Left out: Windows PowerShell, which every Windows has.</param>
    public static Result? Run(string install, string? settingsRoot = null, string? shell = null, int seconds = 180)
    {
        var script = System.IO.Path.Combine(install, FileName);
        if (!File.Exists(script)) return null;
        if (shell is null && !OperatingSystem.IsWindows()) return null;
        string Quoted(string s) => "'" + s.Replace("'", "''") + "'";
        // The file is read and run as text, as the one line does, so a PC that does not allow script files still runs it.
        var command = $"$r = & ([scriptblock]::Create((Get-Content -Raw -LiteralPath {Quoted(script)}))) -PassThru -NoClipboard"
                      + (settingsRoot is null ? "" : $" -SettingsRoot {Quoted(settingsRoot)}")
                      + "; [Console]::Out.WriteLine('CPMJSON' + (ConvertTo-Json -InputObject $r -Depth 6 -Compress))";
        var info = new ProcessStartInfo(shell ?? "powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }) info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(seconds * 1000))
            {
                try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return new Result($"The plug-in check did not finish in {seconds} seconds.", []);
            }
            var line = output.Result.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("CPMJSON", StringComparison.Ordinal));
            return line is null ? new Result("The plug-in check gave no answer: " + output.Result.Trim(), []) : Read(line["CPMJSON".Length..]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    /// <summary>The check's answer (what -PassThru gives, as JSON).</summary>
    internal static Result Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var programs = new List<Finding>();
        // PowerShell writes a list of one as the one thing itself.
        IEnumerable<JsonElement> Many(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToArray() : e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? Array.Empty<JsonElement>() : new[] { e };
        if (root.TryGetProperty("Programs", out var list))
            foreach (var p in Many(list))
            {
                bool? loads = p.TryGetProperty("Loads", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False ? l.GetBoolean() : null;
                var reasons = p.TryGetProperty("Reasons", out var r) ? Many(r).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
                programs.Add(new Finding(p.GetProperty("Program").GetString() ?? "", p.TryGetProperty("Listed", out var li) && li.ValueKind == JsonValueKind.True, loads,
                    p.TryGetProperty("Runtime", out var rt) ? rt.GetString() ?? "" : "", reasons,
                    p.TryGetProperty("Path", out var path) ? path.GetString() ?? "" : "", p.TryGetProperty("ApiFile", out var api) ? api.GetString() ?? "" : ""));
            }
        return new Result(root.TryGetProperty("Text", out var text) ? text.GetString() ?? "" : "", programs);
    }

    /// <summary>
    /// What a CSI program on .NET 8 does when Tools > CSI ProMax is picked, tried in this process (which runs on .NET 8
    /// too): the program's own API file and the plug-in are loaded, the plug-in's class is made, and its Main must take
    /// the program's model and callback. For the PCs where check.ps1 cannot try it because they have no PowerShell on
    /// .NET (Windows PowerShell runs on the .NET Framework). Both files are read into memory and unloaded again, so
    /// neither is kept open and an update can still replace them.
    /// </summary>
    public static (bool Ok, string Detail) TryHere(string plugIn, string apiFile)
    {
        var context = new AssemblyLoadContext("cpm-plugin-check", isCollectible: true);
        try
        {
            Assembly Load(string file) { using var bytes = new MemoryStream(File.ReadAllBytes(file)); return context.LoadFromStream(bytes); }
            // The program's API file is read from memory where it can be; one that only loads from its place is loaded there.
            Assembly api;
            try { api = Load(apiFile); }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or NotSupportedException) { api = context.LoadFromAssemblyPath(System.IO.Path.GetFullPath(apiFile)); }
            // The program has its API file loaded already; the newer .NET goes by the name alone.
            context.Resolving += (_, asked) => string.Equals(asked.Name, api.GetName().Name, StringComparison.OrdinalIgnoreCase) ? api : null;
            var assembly = Load(plugIn);
            var type = assembly.GetType(System.IO.Path.GetFileNameWithoutExtension(plugIn) + ".cPlugin", throwOnError: true)!;
            var made = Activator.CreateInstance(type)!;
            var main = type.GetMethod("Main");
            if (main is null) return (false, "the class has no public method named Main");
            var info = type.GetMethod("Info");
            if (info is null) return (false, "the class has no public method named Info");
            var given = main.GetParameters();
            if (given.Length != 2) return (false, $"Main takes {given.Length} things; the program gives it two, its model and its callback");
            var takes = new List<string>();
            foreach (var parameter in given)
            {
                var kind = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                takes.Add(kind.FullName ?? kind.Name);
                if (kind.Assembly != api) return (false, $"Main takes {kind.FullName} from another file than the program's API file: {kind.Assembly.FullName}");
            }
            var words = new object?[] { "" };
            info.Invoke(made, words);
            return (true, $"created on .NET {Environment.Version}, against {api.GetName().Name} {api.GetName().Version}; its Main takes {string.Join(" and ", takes)}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            var more = inner is ReflectionTypeLoadException load ? " " + string.Join(" ", load.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct().Take(3)) : "";
            return (false, $"{inner.GetType().Name}: {inner.Message}{more}");
        }
        finally { context.Unload(); }
    }
}
