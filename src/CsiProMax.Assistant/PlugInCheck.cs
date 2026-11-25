using System.Diagnostics;
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
    public sealed record Finding(string Program, bool Listed, bool? Loads, string Runtime, IReadOnlyList<string> Reasons);

    public sealed record Result(string Text, IReadOnlyList<Finding> Programs);

    public const string FileName = "check.ps1";

    /// <summary>Runs the check. Null when it cannot run here (no check.ps1 beside CsiProMax.dll, or no PowerShell).</summary>
    /// <param name="shell">For tests: the PowerShell to use. Left out: Windows PowerShell, which every Windows has.</param>
    public static Result? Run(string install, string? settingsRoot = null, string? shell = null, int seconds = 180)
    {
        var script = Path.Combine(install, FileName);
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
                    p.TryGetProperty("Runtime", out var rt) ? rt.GetString() ?? "" : "", reasons));
            }
        return new Result(root.TryGetProperty("Text", out var text) ? text.GetString() ?? "" : "", programs);
    }
}
