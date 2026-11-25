using System.Text.RegularExpressions;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test's first part: what this PC and this install are like, each as a step that passes, fails or is listed
/// as not tested. Nothing here needs a model: it is what must be right before any tool can work (the files of the
/// install, the plug-in lists the programs keep, the folders CSI ProMax writes to, the licence, Claude Code).
/// </summary>
public static class PcFieldSteps
{
    public const string Section = "This PC and the install";

    /// <summary>The plug-in file each program loads, by the program's key.</summary>
    private static readonly Dictionary<string, string> PlugIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["etabs"] = "CsiProMaxPlugin.dll", ["safe"] = "CsiProMaxSafePlugin.dll", ["sap2000"] = "CsiProMaxSapPlugin.dll",
    };

    /// <param name="install">The folder CsiProMax.dll is in.</param>
    /// <param name="plugInLists">The [PlugIn] part of each CSI program's settings file (FieldTest.PlugInLists).</param>
    /// <param name="writes">Folders the test itself writes to (the report's, the work folder), checked with the rest.</param>
    /// <param name="menus">What ToolsMenu.Repair found in each program's plug-in list, after putting right what it may.</param>
    /// <summary>One step for each program that lists CSI ProMax: its plug-in can be created, or why not.</summary>
    internal static void PlugIn(FieldContext ctx, PlugInCheck.Result? check)
    {
        if (check is null) { ctx.Skip("each program can create its CSI ProMax plug-in", $"{PlugInCheck.FileName} is not beside CsiProMax.dll, or this PC has no Windows PowerShell, so it was not tried. Run the install line again to get it."); return; }
        if (check.Programs.Count == 0) { ctx.Note("each program can create its CSI ProMax plug-in", check.Text.Length > 0 ? check.Text : "no program was found to try"); return; }
        foreach (var found in check.Programs.Where(p => p.Listed))
            ctx.Step($"{found.Program} can create the CSI ProMax plug-in (what Tools > CSI ProMax does)", () =>
            {
                FieldContext.Expect(found.Reasons.Count == 0 && found.Loads != false, $"{found.Program} cannot open CSI ProMax: {string.Join(" ", found.Reasons)}");
                if (found.Loads is null)
                {
                    // No PowerShell on this program's .NET here. A program on .NET 8 is tried in this process, which runs on it too.
                    if (found.Runtime != ".NET" || found.Path.Length == 0 || !File.Exists(found.Path))
                        throw new FieldSkipException($"it could not be tried on this PC for a program on {found.Runtime}");
                    if (found.ApiFile.Length == 0 || !File.Exists(found.ApiFile))
                        throw new FieldSkipException($"the program's own API file was not found on this PC, so creating the plug-in against it could not be tried");
                    var (ok, detail) = PlugInCheck.TryHere(found.Path, found.ApiFile);
                    FieldContext.Expect(ok, $"{found.Program} cannot create the CSI ProMax plug-in: {detail}");
                    return detail;
                }
                return $"created, on {found.Runtime}";
            });
        ctx.Note("the plug-in check's own report", check.Text);
    }

    public static void Run(FieldContext ctx, string install, IReadOnlyList<(string File, string Section)> plugInLists, IReadOnlyList<string> writes, IReadOnlyList<ToolsMenu.Entry>? menus = null)
    {
        ctx.Section(Section);
        var windows = OperatingSystem.IsWindows();

        ctx.Step("CSI ProMax's version, and what started it", () => new
        {
            version = Update.Updater.CurrentText,
            folder = install,
            windows = Environment.OSVersion.ToString(),
            dotnet = Environment.Version.ToString(),
            started_with = Environment.ProcessPath,
            // Only the plug-in passes --etabs-pid, so this says whether the Tools menu entry works on this PC.
            opened_from = Environment.GetCommandLineArgs().Contains("--etabs-pid") ? "the program's Tools menu (the plug-in started it)" : "on its own, not from a program's Tools menu",
        });

        ctx.Step("the plug-in files are beside CsiProMax.dll", () =>
        {
            var missing = PlugIns.Values.Where(f => !File.Exists(Path.Combine(install, f))).ToList();
            FieldContext.Expect(missing.Count == 0, $"missing from {install}: {string.Join(", ", missing)}. Run the install line again.");
            return string.Join(", ", PlugIns.Values) + " are there";
        });

        // Each program: installed or not. A program that is not there is not a failure: its part is listed as not tested.
        foreach (var program in CsiProgram.All)
        {
            var api = "";
            try { api = EtabsApi.FindApiFile(program); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { api = ex.Message; }
            if (!File.Exists(api)) ctx.Skip($"{program.Name} on this PC", $"{program.Name} was not found on this PC (looked for {program.ApiFile} under Program Files\\Computers and Structures), so nothing of it is tested here.");
            else
                ctx.Step($"{program.Name} on this PC", () =>
                {
                    var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(api);
                    return new { api_file = api, file_version = version.FileVersion, product = version.ProductName };
                });
        }

        // Tools > CSI ProMax in every program of this user, each version by itself: one that is missing is a failure
        // that says why it could not be added (the program is open, or it has other plug-ins) and what to do.
        foreach (var entry in menus ?? [])
            ctx.Step($"Tools > CSI ProMax in {entry.Label}", () =>
            {
                FieldContext.Expect(entry.Listed, entry.Says);
                return entry.Says;
            });

        // The entry being there is not enough: the program must be able to create the plug-in when it is picked
        // (0.4.4 was listed in ETABS 20 and could not be created). Tried here as the program does it.
        PlugIn(ctx, PlugInCheck.Run(install));

        // The plug-in lists: what makes Tools > CSI ProMax appear in each program.
        var ours = plugInLists.Where(l => Regex.IsMatch(l.File, @"^(ETABS|SAFE|SAP2000) ", RegexOptions.IgnoreCase)).ToList(); // ETABS itself
        if (ours.Count == 0)
            ctx.Skip("the programs' plug-in lists", "No ETABS, SAFE or SAP2000 has kept a plug-in list for this Windows user yet. Open the program once, close it, and run the install line again; or add the plug-in in the program under Tools > Add/Show Plugins."); // ETABS itself
        foreach (var (file, section) in ours)
            ctx.Step($"plug-in list {file}", () =>
            {
                var program = CsiProgram.All.First(p => file.StartsWith(p.Name + " ", StringComparison.OrdinalIgnoreCase));
                var wanted = PlugIns[program.Key];
                var paths = Regex.Matches(section, @"(?im)^\s*PlugInPath\w*\s*=\s*(.+?)\s*$").Select(m => m.Groups[1].Value.Trim().Trim('"')).ToList();
                var mine = paths.Where(p => p.Contains("CsiProMax", StringComparison.OrdinalIgnoreCase)).ToList();
                if (mine.Count == 0 && menus is { Count: > 0 }) throw new FieldSkipException($"CSI ProMax is not in this list: the step 'Tools > CSI ProMax in {file.Split('\\')[0]}' says why and what to do");
                FieldContext.Expect(mine.Count > 0, $"CSI ProMax is not in {program.Name}'s plug-in list ({paths.Count} plug-ins are). With {program.Name} closed, open CSI ProMax again (it adds itself); or add it in {program.Name}: Tools > Add/Show Plugins, file {wanted}.");
                var wrong = mine.Where(p => !Path.GetFileName(p.Replace('\\', '/')).Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                FieldContext.Expect(wrong.Count == 0, $"{program.Name} needs {wanted}, and its list names {string.Join(", ", wrong)}");
                var gone = mine.Where(p => !File.Exists(p)).ToList();
                FieldContext.Expect(gone.Count == 0, $"the list names a file that is not there: {string.Join(", ", gone)}. Run the install line again.");
                return $"{program.Name} lists CSI ProMax: {string.Join(", ", mine)}";
            });

        ctx.Step("the folders CSI ProMax writes to take files", () =>
        {
            var folders = writes.Concat(
            [
                Path.GetDirectoryName(Settings.AgentSettings.FilePath) ?? "",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax"),
            ]).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var refused = new List<string>();
            foreach (var folder in folders)
                try
                {
                    Directory.CreateDirectory(folder);
                    var probe = Path.Combine(folder, ".csi-promax-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "field test");
                    File.Delete(probe);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { refused.Add($"{folder} ({ex.Message.TrimEnd('.')})"); }
            FieldContext.Expect(refused.Count == 0, "these folders did not take a file: " + string.Join("; ", refused));
            return folders;
        });

        if (!windows)
        {
            ctx.Skip("the licence on this PC", "the licence is read from Windows, and this is not Windows");
            ctx.Skip("Claude Code on this PC", "the chat's Claude Code is looked for where Windows installs it, and this is not Windows");
            return;
        }

        ctx.Step("the licence on this PC", () =>
        {
            var status = Licensing.LicenseState.Current;
            FieldContext.Expect(status.Allowed, $"CSI ProMax is locked on this PC ({Licensing.LicenseState.Explain(status.Check, status.Ends)}). Send this PC's code to whoever gave you CSI ProMax and enter the key in the Licence window: {status.PcCode}");
            return new { kind = status.Kind.ToString(), pc_code = status.PcCode, ends = status.Ends?.ToString("yyyy-MM-dd") ?? "no end date" };
        });

        // Installed without the AI chat (install-no-ai.ps1, or turned off in the window): no Claude Code is needed.
        if (!CsiProMax.AiMode.On)
        {
            const string off = "the AI chat is off on this PC (installed without AI, or turned off in the CSI ProMax menu), so no Claude Code is needed";
            ctx.Skip("Claude Code on this PC", off);
            ctx.Skip("Claude Code is signed in", off);
            return;
        }
        Chat.ClaudeCli.Install? claude = null;
        ctx.Step("Claude Code on this PC", () =>
        {
            var search = Chat.ClaudeCli.Search();
            claude = search.Install;
            var tried = string.Join("; ", search.Failed.Select(t => $"{t.Path}: {t.Outcome}"));
            FieldContext.Expect(claude is not null, (search.Failed.Count > 0 ? "Claude Code is here but could not be used. " + tried + ". " : "No Claude Code was found. ") +
                search.Where + (search.DesktopAppSeen ? " The Claude desktop app is here, but it is a different program." : "") + " Open Chat: its card installs it, or lets you point at claude.exe.");
            return new { path = claude!.Path, version = claude.Version, also_tried = tried };
        });
        if (claude is null) ctx.Skip("Claude Code is signed in", "no Claude Code was found (see above)");
        else
            ctx.Step("Claude Code is signed in", () =>
            {
                var (state, method) = Chat.ClaudeCli.CheckSignIn(claude);
                if (state == Chat.ClaudeCli.SignIn.Unknown) throw new FieldSkipException("this Claude Code does not say whether it is signed in; the first chat message shows it");
                FieldContext.Expect(state == Chat.ClaudeCli.SignIn.SignedIn, "Claude Code is signed out. Open Chat and press Sign in.");
                return "signed in" + (method.Length > 0 ? " (" + method + ")" : "");
            });
    }
}
