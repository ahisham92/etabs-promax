using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtabsProMax.Assistant.Mcp;

namespace EtabsProMax.Assistant.Chat;

/// <summary>
/// Finds Claude Code on this PC, checks the sign-in, opens the official installer and the sign-in in a console window
/// the user can see, and builds the command line of the chat's engine. As in Sketch ProMax: the chat runs on the
/// user's own Claude plan, and the Claude desktop app is not needed.
/// </summary>
public static partial class ClaudeCli
{
    /// <summary>The official native installer for Windows (code.claude.com/docs/en/setup).</summary>
    public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>The permission rule that keeps the chat from reading Claude Code's transcripts of earlier conversations.</summary>
    public const string TranscriptRule = "Read(~/.claude/projects/**/*.jsonl)";

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionText();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtabsProMax", "chat");

    /// <summary>The chat engine's working folder: per user, holds nothing but what Claude writes there.</summary>
    public static string WorkFolder => Path.Combine(Folder, "work");

    public sealed record Install(string Path, string Version);

    public enum SignIn { SignedIn, SignedOut, Unknown }

    /// <summary>Every place Claude Code is installed to on Windows, most likely first.</summary>
    public static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".local", "bin", "claude.exe");                // native installer
        yield return Path.Combine(local, "Programs", "claude", "claude.exe");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe"); // WinGet
        var paths = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = null;
            try { value = Environment.GetEnvironmentVariable("PATH", target); }
            catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException) { }
            if (value is not null) paths.AddRange(value.Split(Path.PathSeparator));
        }
        foreach (var dir in paths.Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(dir, "claude.exe");
            yield return Path.Combine(dir, "claude.cmd");
            if (!OperatingSystem.IsWindows()) yield return Path.Combine(dir, "claude");
        }
        yield return Path.Combine(roaming, "npm", "claude.cmd");                        // npm install -g
    }

    /// <summary>Finds a working Claude Code (it answers --version). Null when there is none.</summary>
    public static Install? Find()
    {
        foreach (var path in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The Claude desktop app is also "claude.exe"; never start it by mistake.
            if (path.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase) || path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(path)) continue;
            var (code, output) = Run(path, "--version", 20000);
            var match = VersionText().Match(output);
            if (code == 0 && match.Success) return new Install(path, match.Value);
        }
        return null;
    }

    /// <summary>Whether Claude Code is signed in: `claude auth status` exits 0 when signed in and 1 when not.</summary>
    public static SignIn CheckSignIn(Install install)
    {
        var (code, output) = Run(install.Path, "auth status", 20000);
        var json = output.IndexOf('{') is var at and >= 0 ? output[at..] : "";
        try
        {
            if (json.Length > 0 && JsonNode.Parse(json[..(json.LastIndexOf('}') + 1)]) is JsonObject o)
            {
                if (o["loggedIn"] is JsonValue v && v.TryGetValue<bool>(out var loggedIn)) return loggedIn ? SignIn.SignedIn : SignIn.SignedOut;
                if (code == 0) return SignIn.SignedIn;
                if (code == 1) return SignIn.SignedOut;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException) { }
        if (code == 1 && output.Contains("not logged in", StringComparison.OrdinalIgnoreCase)) return SignIn.SignedOut;
        return SignIn.Unknown;
    }

    /// <summary>Opens a PowerShell window running Anthropic's official installer. The user watches it run.</summary>
    public static void StartInstall()
    {
        var script = InstallCommand + "; Write-Host ''; Write-Host 'Finished. Close this window and go back to ETABS ProMax: it finds Claude Code by itself.'";
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NoExit -Command \"" + script + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>Opens a console window running `claude auth login`, which opens the browser to sign in.</summary>
    public static void StartSignIn(Install install)
    {
        var line = Quote(install.Path) + " auth login & echo. & echo When it says you are signed in, close this window and go back to ETABS ProMax.";
        Process.Start(new ProcessStartInfo("cmd.exe", "/d /s /k \"" + line + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>
    /// The engine's start info: Claude Code in print mode with stream-json in and out, our MCP server and its tools
    /// allowed without prompts, Read, Glob and Grep for files, and nothing else that could act on the PC. Anything that
    /// would need a permission prompt is denied (dontAsk), because nobody can answer a prompt in a hidden process.
    /// ETABS ProMax's own window asks before deletes, saves and analysis runs, and before other changes if set so.
    /// </summary>
    public static ProcessStartInfo EngineStart(Install install, string mcpUrl, string mcpToken, string? resumeSessionId, string? model = null)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpEndpoint.ServerName] = new JsonObject
                {
                    ["type"] = "http", ["url"] = mcpUrl,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + mcpToken },
                    // A call may wait on an approval in the window, or on a long analysis run.
                    ["timeout"] = 1_800_000,
                },
            },
        };
        var mcpFile = Path.Combine(engine, "mcp-" + pid + ".json");
        File.WriteAllText(mcpFile, mcp.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        var promptFile = Path.Combine(engine, "prompt-" + pid + ".md");
        File.WriteAllText(promptFile, SystemPrompt.Text, new UTF8Encoding(false));

        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--append-system-prompt-file", promptFile,
            "--tools", "Read,Glob,Grep",
            "--allowedTools", $"mcp__{McpEndpoint.ServerName},mcp__{McpEndpoint.ServerName}__*,Read,Glob,Grep",
            "--permission-mode", "dontAsk",
            "--disallowedTools", TranscriptRule,
        };
        if (!string.IsNullOrEmpty(resumeSessionId)) { args.Add("--resume"); args.Add(resumeSessionId); }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model); }

        var command = string.Join(" ", args.Select(Quote));
        var info = install.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(install.Path) + " " + command + "\"")
            : new ProcessStartInfo(install.Path, command);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = WorkFolder;
        // The chat runs on the user's Claude plan: an API key in the environment would bill the API instead.
        info.Environment.Remove("ANTHROPIC_API_KEY");
        // Its own session, always: nothing of a parent Claude Code session carries over.
        foreach (var name in info.Environment.Keys.Where(k => k == "CLAUDECODE" || k.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal) && k.Contains("SESSION")).ToList())
            info.Environment.Remove(name);
        return info;
    }

    /// <summary>Runs a short command hidden and returns its exit code and output (stdout then stderr); -1 on timeout or failure.</summary>
    public static (int Code, string Output) Run(string exe, string arguments, int timeoutMs)
    {
        try
        {
            var info = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(exe) + " " + arguments + "\"")
                : new ProcessStartInfo(exe, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.RedirectStandardInput = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.WorkingDirectory = Path.GetTempPath();
            info.Environment.Remove("ANTHROPIC_API_KEY");
            using var process = Process.Start(info)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                KillTree(process);
                return (-1, "");
            }
            Task.WaitAll([stdout, stderr], 2000);
            return (process.ExitCode, (stdout.IsCompleted ? stdout.Result : "") + "\n" + (stderr.IsCompleted ? stderr.Result : ""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Ends a process and everything it started.</summary>
    public static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '*']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}
