using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsiProMax.Settings;
using CsiProMax.Update;

namespace CsiProMax.Chat;

/// <summary>
/// Finds Claude Code on this PC, checks the sign-in, opens the official installer and the sign-in in a console window
/// the user can see, and builds the command line of the chat's engine. Everything here may block for a few seconds
/// (it starts processes): call it from a background thread, never on ETABS's UI thread.
/// </summary>
internal static class ClaudeCli
{
    /// <summary>The official native installer for Windows (code.claude.com/docs/en/setup).</summary>
    public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>The permission rule that keeps the chat from reading Claude Code's transcripts of earlier conversations.</summary>
    public const string TranscriptRule = "Read(~/.claude/projects/**/*.jsonl)";

    /// <summary>The name the chat registers our MCP server under: its tools are mcp__csi-promax__&lt;tool&gt;.</summary>
    public const string ServerName = "csi-promax";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "chat");

    /// <summary>The chat engine's working folder: per user, holds nothing but what Claude writes there.</summary>
    public static string WorkFolder => Path.Combine(Folder, "work");

    public static string AttachmentsFolder => Path.Combine(Folder, "attachments");

    /// <summary>Where Claude Code was found, and its version text.</summary>
    internal sealed record Install(string Path, string Version);

    internal enum SignIn { SignedIn, SignedOut, Unknown }

    /// <summary>Every place Claude Code is looked for on this PC, the most likely first (ClaudeFinder).</summary>
    public static IEnumerable<string> Candidates() => ClaudeFinder.Candidates(PlacesNow());

    /// <summary>Finds a working Claude Code (it answers --version). Null when there is none.</summary>
    public static Install? Find() => Search().Install;

    /// <summary>
    /// Looks for a working Claude Code and says what it saw on the way: the file that answered, or every file that was
    /// there and why it was not used, and each place looked at. The pane shows that when nothing was found.
    /// </summary>
    public static ClaudeFinder.Result Search()
    {
        var found = ClaudeFinder.Search(PlacesNow(), (path, ms) => Run(path, "--version", ms));
        LastSearch = found;
        return found;
    }

    /// <summary>What the last search on this PC saw (null before the first).</summary>
    internal static volatile ClaudeFinder.Result? LastSearch;

    /// <summary>This PC's folders, PATH and settings, as the search needs them.</summary>
    internal static ClaudeFinder.Places PlacesNow()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homes = Several(home, Variable("USERPROFILE").FirstOrDefault() ?? "");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programs = Several(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        });
        // PATH as the user has it now (ETABS's own copy is from when it started, before an install).
        var paths = Several(Variable("PATH").SelectMany(v => v.Split(';')).ToArray());

        // Folders a package manager was told to put programs in.
        var prefixes = new List<string>();
        foreach (var folder in homes)
            try
            {
                var npmrc = Path.Combine(folder, ".npmrc");
                if (File.Exists(npmrc)) prefixes.Add(ClaudeFinder.NpmPrefix(File.ReadAllText(npmrc)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        prefixes.AddRange(Variable("NPM_CONFIG_PREFIX"));
        prefixes.AddRange(Variable("NVM_SYMLINK"));
        prefixes.AddRange(Variable("PNPM_HOME"));
        prefixes.AddRange(Variable("VOLTA_HOME").Select(v => v.TrimEnd('\\', '/') + "\\bin"));
        prefixes.AddRange(Variable("BUN_INSTALL").Select(v => v.TrimEnd('\\', '/') + "\\bin"));
        prefixes.AddRange(Variable("SCOOP").Select(v => v.TrimEnd('\\', '/') + "\\shims"));

        var chosen = "";
        try { chosen = AgentSettings.Load().ClaudePath ?? ""; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException) { }
        var user = "";
        try { user = Environment.UserDomainName + "\\" + Environment.UserName; }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { user = Environment.UserName; }
        return new ClaudeFinder.Places(homes, local, roaming, programs, paths, chosen, Several(prefixes.ToArray()), user);
    }

    /// <summary>A variable as this program has it and as Windows keeps it for the user and for the PC (those two are current).</summary>
    private static IEnumerable<string> Variable(string name)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = null;
            try { value = Environment.GetEnvironmentVariable(name, target); }
            catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException) { }
            if (!string.IsNullOrWhiteSpace(value)) yield return value!;
        }
    }

    /// <summary>Folders as written in a variable: %NAMES% filled in, quotes and blanks off, each once.</summary>
    private static List<string> Several(params string[] folders) =>
        folders.Select(f => Environment.ExpandEnvironmentVariables((f ?? "").Trim().Trim('"'))).Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Whether Claude Code is signed in: `claude auth status` exits 0 when signed in and 1 when not. An older Claude Code
    /// without that command gives Unknown; the first message then says whether it works. Detail: the sign-in method.
    /// </summary>
    public static (SignIn State, string Method) CheckSignIn(Install install)
    {
        var (code, output) = Run(install.Path, "auth status", 20000);
        var method = "";
        var json = output.IndexOf('{') is var at and >= 0 ? output.Substring(at) : "";
        try
        {
            if (json.Length > 0 && JsonNode.Parse(json) is JsonObject o)
            {
                method = o["authMethod"]?.ToString() ?? "";
                if (o["loggedIn"] is JsonValue v && v.TryGetValue<bool>(out var loggedIn))
                    return (loggedIn ? SignIn.SignedIn : SignIn.SignedOut, method);
                if (code == 0) return (SignIn.SignedIn, method);
                if (code == 1) return (SignIn.SignedOut, method);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        if (code == 1 && output.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0) return (SignIn.SignedOut, "");
        return (SignIn.Unknown, "");
    }

    /// <summary>Opens a PowerShell window running Anthropic's official installer. The user watches it run.</summary>
    public static void StartInstall()
    {
        var script = InstallCommand + $"; Write-Host ''; Write-Host 'Finished. Close this window and go back to {Csi.Name}: the CSI ProMax chat finds it by itself.'";
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NoExit -Command \"" + script + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>Opens a console window running `claude auth login`, which opens the browser to sign in.</summary>
    public static void StartSignIn(Install install)
    {
        var line = Quote(install.Path) + $" auth login & echo. & echo When it says you are signed in, close this window and go back to {Csi.Name}.";
        Process.Start(new ProcessStartInfo("cmd.exe", "/d /s /k \"" + line + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>
    /// The engine's start info: Claude Code in print mode with stream-json in and out, our MCP server (the chat's own
    /// connection inside ETABS, or the connector the desktop app is given when that couldn't start) and its tools allowed
    /// without prompts, Read, Glob and Grep for attached files, and nothing else that could act on the PC. Anything that
    /// would need a permission prompt is denied (dontAsk), because nobody can answer a prompt in a hidden process. The MCP
    /// server knows it serves the chat (inside ETABS always; the connector by CSIPROMAX_CHAT), so ETABS's own dialog
    /// asks before model changes (per the settings) and always before code, recipes and files written to a folder Claude chose.
    /// </summary>
    /// <param name="coordinator">A chat With coordinator (ChatTypes): it may start jobs with chat_jobs and is told how.
    /// A Simple chat never gets chat_jobs or those instructions.</param>
    /// <param name="effort">Claude Code's --effort level (ChatAuto.Efforts); left out when empty or this Claude Code doesn't take it.</param>
    public static ProcessStartInfo EngineStart(Install install, string? resumeSessionId, string? model = null, bool coordinator = true, string? effort = null, string? chatKey = null)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The chat's own key (JobBoard.ChatKey) goes with its calls, so one left working in the background is told apart
        // from the chat shown in the pane (JobBoard.Caller).
        var mcpFile = string.IsNullOrEmpty(chatKey) ? WriteMcpConfig(engine, pid, claude: install.Path, model: model)
            : WriteMcpConfig(engine, pid, "mcp-" + chatKey + "-", install.Path, model, chatKey);

        var promptFile = Path.Combine(engine, "prompt-" + pid + ".md");
        File.WriteAllText(promptFile, Prompt(coordinator), new UTF8Encoding(false));

        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--append-system-prompt-file", promptFile,
            "--tools", BuiltInTools,
            "--allowedTools", $"mcp__{ServerName},mcp__{ServerName}__*,Read,Glob,Grep",
            "--permission-mode", "dontAsk",
        };
        // A connector older than this add-in (only used when the connection inside ETABS couldn't start) may not pass
        // CSIPROMAX_CHAT on, so ETABS would not know to ask: then the code and recipe tools are left out of the chat altogether.
        // Claude Code's own transcripts of earlier conversations (…\.claude\projects\…\<session>.jsonl): after a shrink
        // it is told it may read the full transcript, which would send the long conversation again and fill the chat with
        // file steps. The summary is what a shrink keeps; anything else is asked for or found with recall_chats.
        var denied = new List<string> { TranscriptRule };
        if (OlderConnectorInUse) denied.AddRange(CsiProMax.Tools.OutputPaths.CodeTools.Select(t => $"mcp__{ServerName}__{t}"));
        // Parallel jobs need the connection to pass the job on (so its changes take turns): the one inside ETABS always
        // does, the connector program when it is as new as this add-in. And a chat With coordinator: a Simple chat does
        // everything itself.
        if (!JobsAvailable || !coordinator) denied.Add($"mcp__{ServerName}__{CsiProMax.Tools.ChatJobsTool.ToolName}");
        args.Add("--disallowedTools");
        args.Add(string.Join(",", denied));
        if (!string.IsNullOrEmpty(resumeSessionId)) { args.Add("--resume"); args.Add(resumeSessionId!); }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        AddEffort(args, effort);

        var info = CliStart(install, args);
        info.EnvironmentVariables["CSIPROMAX_ETABS_PID"] = pid;
        return info;
    }

    /// <summary>
    /// Claude Code run with <paramref name="args"/> (through cmd.exe for an npm .cmd shim), its output read as UTF-8, in
    /// the chat's work folder, on the user's Claude plan.
    /// </summary>
    private static ProcessStartInfo CliStart(Install install, List<string> args)
    {
        var command = string.Join(" ", args.Select(Quote));
        var info = install.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(install.Path) + " " + command + "\"")
            : new ProcessStartInfo(install.Path, command);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = WorkFolder;
        // The chat runs on the user's Claude plan: an API key in the environment would bill the API instead.
        info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
        return info;
    }

    /// <summary>
    /// The built-in tools the chat and its jobs get: Read, Glob and Grep for files, and ToolSearch. ToolSearch matters for
    /// speed: with it Claude Code lists the 200+ ETABS tools by name and loads a tool's description only when it is used.
    /// Without it every message carried all of them (about 130k tokens before a word of the conversation), so each task
    /// went over the shrink size and the chat was shrunk after every answer, forgot what it had read, and read the model
    /// again on the next task. ToolSearch only reads tool descriptions; an older Claude Code without it ignores the name.
    /// </summary>
    internal const string BuiltInTools = "Read,Glob,Grep,ToolSearch";

    /// <summary>
    /// A parallel job's own Claude Code (Chat.JobBoard, ChatPane.Jobs): like the chat's engine, with the job's instructions,
    /// the connector told which job it serves (CSIPROMAX_JOB, so its changes take turns with the other jobs'), no
    /// chat_jobs (a job doesn't start jobs), whole messages rather than streamed words, and its own session: a new id
    /// (<paramref name="newSession"/>) or the one it resumes, never the main chat's.
    /// </summary>
    public static ProcessStartInfo JobStart(Install install, string jobKey, string? resume, string? newSession, string? model, string? effort = null)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mcpFile = WriteMcpConfig(engine, pid, "mcp-job-" + jobKey + "-", install.Path, model, jobKey);
        var promptFile = Path.Combine(engine, "job-" + jobKey + "-" + pid + ".md");
        File.WriteAllText(promptFile, JobPrompt(), new UTF8Encoding(false));
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--append-system-prompt-file", promptFile,
            // Its plan's steps too (JobPlan: TodoWrite, or TaskCreate/TaskUpdate where Claude Code has those instead),
            // so the crew strip shows how far along it is. Names Claude Code doesn't have are left out by it.
            "--tools", BuiltInTools + "," + JobPlan.Tools,
            "--allowedTools", $"mcp__{ServerName},mcp__{ServerName}__*,Read,Glob,Grep," + JobPlan.Tools,
            "--permission-mode", "dontAsk",
            "--disallowedTools", string.Join(",", TranscriptRule, $"mcp__{ServerName}__{CsiProMax.Tools.ChatJobsTool.ToolName}"),
        };
        if (!string.IsNullOrEmpty(resume)) { args.Add("--resume"); args.Add(resume!); }
        else { args.Add("--session-id"); args.Add(newSession ?? Guid.NewGuid().ToString()); }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        AddEffort(args, effort);
        var info = CliStart(install, args);
        // Its own session, always: nothing of a parent session carries over.
        foreach (var name in info.EnvironmentVariables.Keys.Cast<string>().Where(k => k == "CLAUDECODE" || k.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal) && k.Contains("SESSION")).ToList())
            info.EnvironmentVariables.Remove(name);
        info.EnvironmentVariables["CSIPROMAX_ETABS_PID"] = pid;
        // A change may wait its turn behind other jobs' changes (BridgeServer.TurnWait): the call must not time out first.
        info.EnvironmentVariables["MCP_TOOL_TIMEOUT"] = JobToolTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return info;
    }

    /// <summary>How long a job's tool call may take, its wait for its turn included (BridgeServer.TurnWait is 30 minutes).</summary>
    private const int JobToolTimeoutMs = 2_400_000;

    /// <summary>
    /// True when the chat may run parallel jobs: always with the connection inside ETABS; with the connector program
    /// only when it is as new as this add-in (an older one doesn't pass the job on, so the job's changes couldn't take turns).
    /// </summary>
    public static bool JobsAvailable => !OlderConnectorInUse;

    /// <summary>
    /// True when the chat falls back to the connector program (the connection inside ETABS couldn't start) and that is
    /// older than this add-in, so it may lack newer connector tools (read_pdf_part) and the job key.
    /// </summary>
    internal static bool OlderConnectorInUse => Bridge.ChatMcp.Ensure() is null && ConnectorIsOlder();

    /// <summary>
    /// Set once Claude Code refused --effort (a version older than the option, ChatAuto.IsEffortRefusal): from then on
    /// this ETABS session starts it without, at its own default effort.
    /// </summary>
    public static volatile bool EffortRefused;

    private static void AddEffort(List<string> args, string? effort)
    {
        if (EffortRefused || string.IsNullOrEmpty(effort) || !ChatAuto.Efforts.Contains(effort)) return;
        args.Add("--effort");
        args.Add(effort!);
    }

    /// <summary>
    /// Writes the MCP config. Normally the chat's own connection inside ETABS (Bridge.ChatMcp): its address, its token, and
    /// headers naming the parallel job and the model. When that couldn't start, the connector program the installer and
    /// ClaudeRegistration register for the desktop app, as before. Written at each start, so a new core's address is used.
    /// </summary>
    private static string WriteMcpConfig(string engine, string pid, string name = "mcp-", string? claude = null, string? model = null, string? job = null)
    {
        if (!string.IsNullOrEmpty(claude)) Bridge.ChatMcp.Claude = claude;
        var mcp = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = McpServer(pid, claude, model, job) } };
        var mcpFile = Path.Combine(engine, name + pid + ".json");
        File.WriteAllText(mcpFile, mcp.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return mcpFile;
    }

    /// <summary>The config's entry for our server: the connection inside ETABS, or the connector program.</summary>
    internal static JsonObject McpServer(string pid, string? claude, string? model, string? job)
    {
        if (Bridge.ChatMcp.Ensure() is { } http)
        {
            var headers = new JsonObject { ["Authorization"] = "Bearer " + http.Token };
            // The job this session is (its changes take turns with the other jobs'), and the model read_pdf_part reads with.
            if (!string.IsNullOrEmpty(job)) headers[Bridge.McpHttpServer.JobHeader] = job;
            if (!string.IsNullOrEmpty(model)) headers[Bridge.McpHttpServer.ModelHeader] = model;
            // Claude Code gives up on an HTTP server's call that is silent for 5 minutes (30 for a program it started):
            // a call may wait on an approval in ETABS, and a job's change on its turn (BridgeServer.TurnWait). This
            // server's own limit raises that wait, as long as a job's MCP_TOOL_TIMEOUT or the 30 minutes the connector had.
            return new JsonObject
            {
                ["type"] = "http", ["url"] = http.Url, ["headers"] = headers,
                ["timeout"] = string.IsNullOrEmpty(job) || JobBoard.IsChatKey(job) ? 1_800_000 : JobToolTimeoutMs,
            };
        }
        return new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = ClaudeRegistration.ConnectorPath,
            ["args"] = new JsonArray(),
            // CSIPROMAX_CHAT: the bridge then asks in ETABS before code, recipes and files written where Claude chose.
            // CSIPROMAX_CLAUDE and _MODEL: read_pdf_part reads a part of a large PDF with the same Claude Code and model.
            // CSIPROMAX_JOB: the parallel job this connector serves (its changes take turns with the other jobs').
            ["env"] = Env(pid, claude, model, job),
        };
    }

    /// <summary>The address the chat's Claude Code reaches the ETABS tools at now: the connection inside ETABS, or "" for the connector program.</summary>
    internal static string McpAddress => Bridge.ChatMcp.Current?.Url ?? "";

    /// <summary>
    /// A one-shot run for the prompt enhancer (PromptBriefs): print mode, the message read from stdin, one JSON result
    /// out. No built-in tools at all; with <paramref name="readTools"/> only the planner's read-only ETABS tools
    /// (PromptBriefs.PlannerTools) are allowed, and everything else is denied without asking (dontAsk), so it cannot
    /// change the model. Without them it gets no MCP server.
    /// </summary>
    public static ProcessStartInfo OneShotStart(Install install, string instructions, bool readTools, string? model)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var promptFile = Path.Combine(engine, (readTools ? "brief-" : "distil-") + pid + ".md");
        File.WriteAllText(promptFile, instructions + Notes(), new UTF8Encoding(false));
        // With the ETABS tools, ToolSearch too (see BuiltInTools): the planner loads only the few read tools it uses.
        var args = new List<string> { "-p", "--output-format", "json", "--append-system-prompt-file", promptFile, "--tools", readTools ? "ToolSearch" : "", "--permission-mode", "dontAsk" };
        if (readTools)
        {
            args.AddRange(new[] { "--mcp-config", WriteMcpConfig(engine, pid, "mcp-brief-"), "--strict-mcp-config", "--max-turns", "12" });
            args.Add("--allowedTools");
            args.Add(string.Join(",", PromptBriefs.PlannerTools.Select(t => $"mcp__{ServerName}__{t}")));
        }
        else
        {
            args.AddRange(new[] { "--strict-mcp-config", "--max-turns", "1" });
        }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        var info = CliStart(install, args);
        info.EnvironmentVariables["CSIPROMAX_ETABS_PID"] = pid;
        return info;
    }

    private static JsonObject Env(string pid, string? claude, string? model, string? job = null)
    {
        var env = new JsonObject { ["CSIPROMAX_ETABS_PID"] = pid, ["CSIPROMAX_CHAT"] = "1" };
        if (!string.IsNullOrEmpty(claude)) env["CSIPROMAX_CLAUDE"] = claude;
        if (!string.IsNullOrEmpty(model)) env["CSIPROMAX_MODEL"] = model;
        if (!string.IsNullOrEmpty(job)) env["CSIPROMAX_JOB"] = job;
        return env;
    }

    /// <summary>
    /// What the chat says about ETABS tools that didn't connect (<paramref name="status"/> from Claude Code), naming a PC
    /// that blocks them: Claude Code's own log of the connection says EPERM or access denied, or, for the connector
    /// program, ETABS itself isn't allowed to start it. Reads files and may start the connector: call off the UI thread.
    /// </summary>
    internal static string ToolsFailed(string status, string address, bool job = false)
    {
        var inside = address.Length > 0;
        var blocked = ToolsNote.IsBlocked(LastMcpError()) || !inside && ConnectorRefused();
        return ToolsNote.Failed(status, inside, inside ? address : ClaudeRegistration.ConnectorPath, blocked, job);
    }

    /// <summary>The end of Claude Code's newest log of connecting to our server (its cache folder, the last few minutes), or "".</summary>
    private static string LastMcpError()
    {
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-cli-nodejs", "Cache");
            if (!Directory.Exists(cache)) return "";
            var newest = Directory.GetDirectories(cache)
                .Select(d => Path.Combine(d, "mcp-logs-" + ServerName))
                .Where(Directory.Exists)
                .SelectMany(d => new DirectoryInfo(d).GetFiles())
                .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return "";
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 4000), SeekOrigin.Begin);
            return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return ""; }
    }

    /// <summary>True when Windows refuses to start the connector program for ETABS too (a policy such as AppLocker), not when it is missing.</summary>
    private static bool ConnectorRefused()
    {
        try
        {
            using var process = ChildProcess.Start(new ProcessStartInfo(ClaudeRegistration.ConnectorPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            // It started: with its input closed it ends by itself.
            process.StandardInput!.Close();
            if (!process.WaitForExit(3000)) KillTree(process.Process);
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 5 access denied, 1260 blocked by group policy, 4551 blocked by App Control; 2 (not found) is a broken install.
            return ex.NativeErrorCode is 5 or 1260 or 4551 || ToolsNote.IsBlocked(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return false; }
    }

    /// <summary>True when the installed connector is older than this add-in (so it may lack newer connector tools, such as read_pdf_part).</summary>
    internal static bool ConnectorIsOlder()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(ClaudeRegistration.ConnectorPath);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0) return false;
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) < Updater.Current;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return false; }
    }

    /// <summary>CSI ProMax's agent instructions (also sent by the MCP server) plus what is special about the pane.</summary>
    private static string Prompt(bool coordinator) => CsiProMax.Agent.SystemPrompt.Build(AgentSettings.Load().FirmStandards) + $"""


        You are chatting in the CSI ProMax window beside {Csi.Name}. The person is a structural engineer.
        - The {Csi.Name} tools come from the csi-promax MCP server and act on the {Csi.Name} this window was opened from. Use
          them for anything about the model. Besides them you have only Read, Glob and Grep (to look at files on this
          PC); there is no shell, file editing or web access here.
        - The {Csi.Name} tools are listed by name only, so every message stays small. Before using one, load it with ToolSearch,
          all the ones the task needs in one call (select:mcp__csi-promax__get_model_context,mcp__csi-promax__query_objects);
          once loaded they stay loaded in this conversation. Call them by their full names. If a call says "No such tool
          available", load it with ToolSearch (or, without ToolSearch, call it again once: the tools may still be connecting).
        - Follow-ups: most messages carry on with earlier work in this chat ("those too", "now the selected ones",
          "finish it"). Use what you already found in this conversation (names, sections, stories, the method or code
          that worked) instead of reading the model again. Each message ends with a model note from CSI ProMax: the
          model's file and what is selected in {Csi.Name} (with names). Read again only what you don't have. Don't mention
          the note to the user.
        - Code (run_etabs_query_code, run_etabs_code) is the last resort: prefer a dedicated tool over writing the same
          thing as code. When code fails, the result names the closest real API members or a hint: use them rather
          than guessing again.
        - CSI ProMax asks the user to approve some calls before they run: always run_etabs_code, new recipes and
          learned tools, deletes, saving the model, running the analysis, and writing over existing files outside
          CSI ProMax's own folders; other model changes only if the user set it to ask. Leave folder and file_name
          out unless the user asked for a location. If the user declines, ask what they want instead. Don't ask the
          user for permission in the chat before a normal change: CSI ProMax asks when needed.
        - Files the user attaches are named by full path. Pictures and PDFs that fit come with the message itself, and the
          text of Word, PowerPoint and Excel files is in the message (sheets as tables with row numbers and
          column letters): read them there. A PDF too large to send whole comes with a text file of its pages: find the
          pages in the text, and look at a page's drawings with read_drawing (file and page, then cell to zoom), which
          shows any PDF page or scan as a picture in seconds and zooms until small text can be read. DWG and DXF
          drawings: read_cad_drawing (the overview first, then the layers you need); don't try to Read them. Read
          other files with the Read tool.
        - Files: when the user wants something to keep or send (an Excel sheet, a Word report or calculation note, a
          PowerPoint presentation, a PDF, a CSV, a DWG or DXF layout; a schedule of sections, a load take-down, reactions
          for the foundation designer, a drift check, a summary of the model), make it with make_document. Get the
          facts first with the {Csi.Name} tools, then write the whole file in one call, with formulas for sums and checks in
          Excel. For a picture of the model, capture_view_image and give its path. The chat shows the file as a card
          with Open, Show in folder and Save a copy: say in a line or two what is in it, without the path.
        - "Like our past ones" / "in our usual format" (or the user names a folder or file of earlier reports, calculation
          notes or letters): read two or three of them first (read_document_text with the folder lists them, the newest
          first, and with a file gives its text; PDFs with Read), note their layout, headings, numbering, wording and
          tone, and write the new file the same way with make_document. Say in one line which files you followed. No
          examples given and none remembered in the notes: ask once for the folder, and save it with memory_note
          (scope=global) so you don't ask again.
        - The selection is the scope: with objects selected in {Csi.Name}, or "these", "the selected ones", "just these two",
          work on those objects only, however many like them the model has; a named story, grid line or group is the
          whole scope the same way. Found the same problem elsewhere: say so in one line with the count, and leave it.
        - Their words: use the names the user uses for things ("transfer beam", "core wall", "podium"), not the API's
          or your own. A word they asked the meaning of is never used again: save it with memory_note (scope=global) as
          "say X, not Y".
        - "Why ...?" is a question: give the reason in one or two lines, from the model, and change nothing. Offer the fix
          in one line; make it only when they say so.
        - Any question ("did you ...?", "is it ...?", "are all tasks done?") is answered by you, now, answer first (yes or
          no when it asks so), in one to three lines, also while jobs run. Never hand a question to a job or say a job will
          check: read the fact yourself with a quick read-only tool (get_activity_log, query_objects, chat_jobs action=ask).
        - What the user puts off ("leave the basement for now", "later") stays an open item. As soon as the work it
          waits on is done, say in one line what is next from those items and offer to start; never let one drop, and
          never report a part done while an item the user named for it is still open.
        - A method the user agreed or praised goes into every task for that kind of work as a must, word for word:
          never add "or another way where neater".
        - Never tell the user a part is done unless the job's check passed for it, and say "checked" only then. A job that
          stalled or was stopped hasn't done what it didn't read back: say plainly which parts are not done.
        - When the user corrects a note or a rule kept earlier, save the new one with memory_note replaces=<words of the old
          one>, so the two never stand side by side.
        - When the user says how a thing is always done (the practice above, the notes, a lesson), that settles it: tell the
          job the answer rather than passing its question on. Ask the user only what the practice and notes don't settle.
        - Sharing work between workers ("give the tasks to the other guys", "reassign to everyone") spreads the open work over
          all workers, the named one too. Stop a worker only when the user says stop.
        - Something the user asks not to see or not to happen again ("don't show this message") holds for the whole chat,
          not until their next message: fix its cause, or keep the setting off, and say in one line which.
        - Open items, comments or findings to follow up ("what is left", "all comments", "open items"): one list, one line
          each as "<where>: <problem> - <what to do>", the user's part first, nothing already done. As a file when they
          ask for one (make_document, Excel: where, problem, what to do, who), not a report with sections.
        - Do only what was asked. What the user said to keep stays: before deleting or replacing anything, check it
          against what they asked to keep in this chat. When a wider change (a section property edit that reaches
          every member with that section) would also change something they asked to keep or didn't mention, leave that
          part out, or ask in one line first.
        - When the user says a result is wrong ("no, that's wrong", "we are repeating the same mistakes"), stop. Say
          back in one sentence what they want, in their words, and wait for their yes before trying again, rather than
          trying another variation.
        - When the user says you did too much ("that's too much", "only the beams, not the columns"), say in one line
          which of your changes went beyond what they asked, put only those back as they were (delete what you added,
          set back what you changed, using the names and values from this chat: {Csi.Name} has no undo for them), keep the
          rest, then read back that what they wanted is still there and say what you put back.
        - To the point: one reply per request, the result first, three lines at most unless they ask for more. Say what
          is done (or the answer) and, only if there is one, the single thing they must decide or look at. Leave out how
          you did it, what you found on the way, values that didn't change, lists of things to check, and offers of more
          work. Nothing changed or it failed: say so in the first words, with the one next step. No tool names, JSON
          or API terms unless the user asks for them. Use the model's own units.
        - Small asks are done at once, by you: work on what is selected or on one member, story or load is not a job,
          needs no plan, and is not split. Several ways to do it: don't list them. Take the one that changes least, do
          it, and say in one line which you took. Ask first only when a wrong guess would change objects they didn't
          name, and then one short question: {Csi.Name} can't undo it.
        - "What is in this model?" and other overview questions: a few lines only, such as the model's name, the stories,
          the main objects with counts (columns, beams, walls, slabs), the load patterns and whether it has been
          analysed. Then offer more detail in one short line. Don't list everything.
        - When a call says {Csi.Name} didn't start it, the user already sees a card in the chat that says what to do. Say it in
          one short line (for example "{Csi.Name} has a dialog open: close it, then Continue"), without the technical reason.
        - Answer in plain sentences (Markdown). A short list only for three or more separate items; a table only when
          the user asks for one or the answer is a schedule of many rows, kept narrow. Put numbers with their units.
        - When the user says "remember that ..." (or "from now on ..."), save it with memory_note action=remember: scope=global
          for habits and standards that hold for every model, scope=model for facts about this model. When they say
          "forget ...", use action=forget. Confirm in one line. They can see and edit the notes in the Notes tab.
        - Don't make them say it twice: a rule given with "always", "never", "typically", "as always", "keep it as default",
          or a correction that will hold next time ("no, cracked stiffness for walls is 0.35", "always use the envelope
          combination for reactions") is saved the same way without being asked, in their words, and confirmed in a
          short line. Not one-off instructions for this task. Before a task like one done before, check the notes and
          recall_chats for how it was done and do it that way.
        - When the user says something is a bug ("that's a bug", "report this"), call report_bug with one plain line you
          write from the conversation: what they did, what happened, what they expected, and the tool involved. Then say
          the report is saved in Documents\CSI ProMax\Bugs\Reports.
        - Earlier chats: below is what recent chats about this model remembered. When the user refers to something done
          in another chat or project ("the same way as in the Tower project", "like last time"), call recall_chats with the words
          that name it, then action=open on the best match and follow its steps and settings here (checking this model
          first). Say in one line which chat you followed. If nothing matches, ask the user which project they mean.
        - When the chat was shrunk, the summary you have is what it keeps. Don't try to open the earlier conversation's
          file (the .jsonl transcript): it isn't allowed here, and it would cost far more than it saves. If a detail is
          missing, read it from the model or ask the user in one line.
        """ + (coordinator ? JobsPrompt() : SimplePrompt) + Notes() + Recall();

    /// <summary>A Simple chat (ChatTypes): it may have used jobs before it was switched, so it is told it has none now.</summary>
    private const string SimplePrompt = """

        - This is a simple chat: there are no jobs here. Do all the work in this chat yourself, step by step, even if
          earlier messages mention jobs.
        """;

    /// <summary>How the main chat coordinates parallel jobs (chat_jobs); nothing when jobs aren't available.</summary>
    private static string JobsPrompt() => !JobsAvailable ? "" : $"""

        - {CrewStrip.Names.Line()}
        - Jobs: you coordinate. When a request has separate parts that can go side by side ("add the beams on stories 1-5
          and check the drifts"), or is one long piece of model work, hand it to jobs with chat_jobs. Call action=list first;
          a follow-up that belongs to an existing job ("also do story 6" after a beams job) goes to that job with
          action=message, never to a new job. Otherwise action=start, once per part: a short title, and the whole task in
          full with every detail (the job doesn't see this chat), its done_when (one to three conditions the job can check
          in the model to know it is finished: "every Story 3 beam has section B300x600",
          "the combination ULS1 exists with Dead 1.2 and Live 1.6"), including the facts already found here that it needs
          (object names, sections, stories, the method that worked), so it doesn't read the model again, and its kind: simple (rename, list, select, assign one section or load,
          short summaries), everyday (normal modelling, loads, combinations, reading result tables, checks) or hard (a
          design basis or a drawing set against the model, tricky modelling logic, studying results, design decisions); the kind picks its model and effort. Up to {ChatJobs.Limit} jobs run at once; more wait to start.
          Reads run in parallel; changes to the model take turns, one at a time. Answer quick questions and do small
          asks yourself, without a job (see "Small asks" above): a job takes minutes to start, read and be checked.
        - You run light (a quick model, low effort, and the chat is shrunk often), so the long chat stays cheap: keep your
          own reading to what the parts share and your replies to a few lines. Anything that needs real thinking (a design
          basis or drawings against the model, studying results, tricky modelling logic, long documents) goes to a job of kind hard,
          with everything it needs in the task, rather than being worked through here.
        - Work as a team lead: you are the team's reader, and dividing the work is the default. Before handing out work,
          read the model once yourself for what the parts share (stories, grids, sections, names). Then divide the request
          into parts that don't overlap (by story, zone, kind of member, or reading vs. checking vs. changing), as
          many as there are free workers (action=list says how many), and start them in one call: action=start with parts
          and facts (what you read). Use one job only when the work truly can't be divided (one member, one story, one
          short answer), and then say why in single: a start of one whole job while two or more workers are free is sent
          back to be divided. Jobs also hear
          from CSI ProMax what the jobs before them found. When a job reports something another open job needs, pass
          it on with action=message.
        - Never hold known work back until a whole list is checked: when a job reports items that need doing (a review
          list read in parts, a triage), start jobs on those items at once, while the rest is still being read. "Check
          first, then fix" in a request means don't change an item before it is checked, not wait for every item. When
          workers are free while one job still holds a long list, stop it at its next report and split what is left across
          them; give no job more than about 8 items of a list (bug 131: the chat checked 58 review rows for 2 hours, one
          worker alone on the last 29, four idle, nothing fixed).
        - After starting or messaging jobs, say in one line which jobs have it and end your turn: don't wait for them and
          don't do their work. While jobs work, leave model changes to them ({Csi.Name} may say it is busy), and don't write
          about them: their cards show the progress. Each job reports back when it finishes, in a message marked as a job
          report: then tell the user the result in three lines at most (what is done, and what they must decide if
          anything), not the job's checks, its numbers or its list of things to look at. Stop a job with action=stop
          when the user asks.
        - What other jobs are doing: say it only when the user asks or it changes what they must do, and then only from
          a live read in this turn (chat_jobs action=list, or other_jobs in this turn's chat_jobs result), never from
          memory: a job you started earlier may have finished minutes ago. A job that finished and is being checked is
          not "still working on" its task.
        - Questions for the user go in the Questions tab, never in your answer's text: chat_questions action=ask with all
          of them in one call, each short and plain with two to four answers to tap and the one you suggest. A job's
          report that asks the user something: put its question in the tab (it shows as yours; name the job in it). Then
          say in one line "Q4 in Questions". The answers come back as one message from the tab: act on them, and pass a
          job's answer to that job. "I don't understand" comes back for a question: rewrite it with action=update.
        """;

    /// <summary>A parallel job's instructions: CSI ProMax's agent instructions plus what is special about a job.</summary>
    private static string JobPrompt() => CsiProMax.Agent.SystemPrompt.Build(AgentSettings.Load().FirmStandards) + $"""


        You are one job of the CSI ProMax chat beside {Csi.Name}: the main chat gave you one task, and you work on it
        in the background, perhaps alongside other jobs on the same model. The person is a structural engineer; they see
        your progress in your own view of the chat (they open your card) and can send you messages there.
        - First write your plan as a few steps (2 to 8), with TaskCreate for each step (or TodoWrite where you have that
          instead), and mark each step in progress and then completed as you go (TaskUpdate or TodoWrite): the person
          sees from it how far along you are (if they are listed by name only, load them with ToolSearch in the same
          call as the {Csi.Name} tools). Don't mention the plan in your answers.
        - Do your task completely with the {Csi.Name} tools from the csi-promax MCP server. Besides them and your plan you
          have only Read, Glob and Grep (to look at files on this PC). Don't start other work. The {Csi.Name} tools are listed by name only:
          load the ones you need with ToolSearch in one call (select:mcp__csi-promax__query_objects,...) before using
          them. If {Csi.AName} call says "No such tool available", load it with ToolSearch, or call it again once.
        - Your task carries the facts the main chat already found (names, sections, sizes): use them rather than reading the
          model again, and read only what you still need. A message may end with a model note from CSI ProMax (the
          model's file and what is selected, with names): use it, and don't mention it.
        - Other jobs may work at the same time. Reads run at once; changes to the model take turns, one at a time, so a
          change may wait a while before it runs: that is normal, don't send it again. Don't change objects
          outside your task: {Csi.Name} has no undo.
        - CSI ProMax asks the user to approve some calls; if they decline, leave that part and say so.
        - When the task asks for a file (Excel, Word, PowerPoint, PDF, CSV, DWG or DXF), make it with make_document after getting the
          facts, and name the file in your report. When the task points to earlier files of the same kind (a folder of
          past reports, letters or minutes), read two or three of them first (read_document_text) and follow their layout, headings, wording
          and tone.
        - You are part of a team. When you need something another job is working on or has found (stories, grids, names,
          a method that worked), ask it rather than reading the model again: chat_jobs action=list shows your teammates,
          action=ask with job and question gives what that job has found so far (forward=true passes the question on, and
          it messages you back), and action=message with job and text tells a teammate something its part needs. Keep it
          to what the work really needs.
        - Messages that reach you later say who sent them: "The user says: …" (the person, typing in your view), "The
          main chat says: …" (the coordinator) or "A teammate, job N …" (another job). All count; the person's word wins
          if they disagree; answer a teammate's question with chat_jobs action=message.
        - Don't save the model and don't run the analysis yourself unless your task says so: both hold {Csi.Name} and
          every other job waits behind them, and running the analysis locks the model for the jobs that change it.
        - If you need the person to decide something the model can't settle, put it in their Questions tab: chat_questions
          action=ask with the question, two to four answers to tap and the one you suggest. Carry on with the work that
          doesn't depend on it, and say in your report what waits on it; the answer reaches you as a message. The practice above and the memory notes are not questions: apply them
          and ask only what they don't settle.
        - Finish line: your task may end with "Done when": those are the checks that tell you the task is finished. Without
          one, write your own (one to three conditions you can check in the model) as the last step of your plan. Before you
          report, check each condition with the {Csi.Name} tools (read back, don't assume). One that fails: fix it and check
          again, at most twice; then report it as not done, with what is left.
        - Get it right the first time: before you report, check your own work exactly as the teammate will: every part
          of the task read back from the model (counts, sections, loads, supports). Fix what you find before you
          report. Never write done for a part you didn't change and read back: a report that says done when nothing
          changed costs the person a whole round.
        - "Exactly", "follow", "match", "flush" and "on" mean exact: within 1 mm (rounding), never a looser figure.
        - End every run with a short report for the main chat, in plain words: each part of your task as done or not
          done (with counts and names: "38 of 40 beams changed" is not done), each
          finish condition and whether it held, anything you couldn't do and why, and what the person should check. A part you did not read back in the model
          is not done. A teammate then checks every part in the model; what it finds not done comes back to you to
          finish. A few lines; no tool names or JSON.
        """ + (Autopilot.Unattended ? Autopilot.JobRules + Autopilot.JobWords() : "") + Notes();

    /// <summary>The memory notes for this chat (global and the open model's), as a block at the end of the prompt.</summary>
    private static string Notes()
    {
        var block = MemoryNotes.PromptFor(ChatPane.CurrentModelKey);
        return block.Length == 0 ? "" : "\n\n" + block;
    }

    /// <summary>What this model's other chats remembered (ChatRecall), as a block at the end of the prompt.</summary>
    private static string Recall()
    {
        try
        {
            var key = ChatPane.CurrentChatKey;
            var block = ChatRecall.PromptBlock(ChatStore.ForModel(key), key, ChatPane.CurrentChatId);
            return block.Length == 0 ? "" : "\n\n" + block;
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Runs a short command hidden and returns its exit code and output (stdout then stderr); -1 on timeout or failure.</summary>
    /// <summary>Short helper runs still going (claude --version, auth status): stopped when CSI ProMax closes, so none is left behind.</summary>
    private static readonly HashSet<Process> Helpers = new();

    static ClaudeCli() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        Process[] left;
        lock (Helpers) left = Helpers.ToArray();
        foreach (var p in left)
            try { KillTree(p); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    };

    public static (int Code, string Output) Run(string exe, string arguments, int timeoutMs)
    {
        try
        {
            var cmdShim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var info = cmdShim
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(exe) + " " + arguments + "\"")
                : new ProcessStartInfo(exe, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.RedirectStandardInput = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.WorkingDirectory = Directory.Exists(WorkFolder) ? WorkFolder : Path.GetTempPath();
            info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            using var process = StartHelper(info);
            process.StandardInput!.Close();
            var stdout = process.StandardOutput!.ReadToEndAsync();
            var stderr = process.StandardError!.ReadToEndAsync();
            bool exited;
            lock (Helpers) Helpers.Add(process.Process);
            try { exited = process.WaitForExit(timeoutMs); }
            finally { lock (Helpers) Helpers.Remove(process.Process); }
            if (!exited)
            {
                KillTree(process.Process);
                return (-1, "");
            }
            Task.WaitAll(new Task[] { stdout, stderr }, 2000);
            return (process.Process.ExitCode, (stdout.IsCompleted ? stdout.Result : "") + "\n" + (stderr.IsCompleted ? stderr.Result : ""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return (-1, ex.Message.Trim().Length > 0 ? ex.Message : ex.GetType().Name);
        }
    }

    /// <summary>
    /// Starts a short helper run so that it inherits none of ETABS's open files (bug 72). When Windows refuses that kind
    /// of start on this PC (not: the file is missing), it is started the plain way: the run lasts seconds, and a Claude
    /// Code that is there must not be reported as missing.
    /// </summary>
    private static ChildProcess StartHelper(ProcessStartInfo info)
    {
        try { return ChildProcess.Start(info); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is not (2 or 3))
        {
            try { return ChildProcess.StartPlain(info); }
            catch (Exception again) when (again is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                throw new System.ComponentModel.Win32Exception(ex.NativeErrorCode, ex.Message);
            }
        }
    }

    /// <summary>Ends a process and everything it started (Claude Code starts the MCP server).</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
#if NETFRAMEWORK
            using var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            kill?.WaitForExit(5000);
            if (!process.HasExited) process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' }) < 0) return arg;
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
