using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Bugs;
using CsiProMax.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test's last part: CSI ProMax's own tools, the ones that are not about the model (files in and out, the
/// review pack, notes, recipes, learned tools, code, the bug report, the export). Each is run as the chat runs it, on
/// the scratch model when there is one, and what it leaves behind is taken away again. It ends with the same closing
/// check as the programs' parts: every tool of the panel was run, or is listed as not tested with the reason.
/// </summary>
public static class PanelFieldSteps
{
    public const string Section = "CSI ProMax's own features";

    /// <summary>The words every file and note of the test carries, so it is never taken for real work.</summary>
    public const string Marker = "CPM field test";

    private const string RecipeName = "cpm-field-test-recipe", LearnedName = "cpm_field_test_tool";

    /// <param name="link">The model the tools read: the scratch model, or the open model when there is no scratch window.</param>
    /// <param name="scratch">True when <paramref name="link"/> is the test's own scratch model, so it may be changed.</param>
    /// <param name="live">True when a CSI program is behind the link (code runs against its API).</param>
    public static void Run(FieldContext ctx, ICsiLink link, bool scratch, bool live)
    {
        ctx.Section(Section);
        var registry = ToolRegistry.CreateDefault(link is OfflineEtabs, link.Program);
        var app = new EtabsApp(link);
        var own = registry.All.Where(t => t is not ModelTool).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var folder = Path.Combine(ctx.Folder, "Panel");
        Directory.CreateDirectory(folder);
        var windows = OperatingSystem.IsWindows();

        // One tool, run as the chat runs it: its answer as JSON (or as text), or the step fails with what it said.
        JsonNode? Tool(string name, string json, Action<JsonNode, ToolResult>? check = null, string? step = null) =>
            ctx.Step(step ?? "tool " + name, () =>
            {
                handled.Add(name);
                var result = Execute(registry, app, name, json);
                FieldContext.Expect(!result.IsError, "it answered with an error: " + result.Text);
                var answer = Node(result.Text);
                check?.Invoke(answer, result);
                return answer;
            });
        void NotTested(string name, string why, string? step = null)
        {
            handled.Add(name);
            ctx.Skip(step ?? "tool " + name, why);
        }

        // The panel's look: the CDT SDK theme, as in Sketch ProMax. Without it every window falls back to CSI ProMax's own older styles.
        if (!windows) ctx.Skip("the panel's CDT SDK theme loaded", "the panel's windows are drawn on Windows only");
        else if (!UI.CdtTheme.Tried && !HasWindow()) ctx.Skip("the panel's CDT SDK theme loaded", "this run has no CSI ProMax window, and the theme belongs to a window's thread (Help > Field test in the window checks it)");
        else ctx.Step("the panel's CDT SDK theme loaded", () =>
        {
            // No window asked for the theme yet: the test asks for it itself, on the windows' own thread.
            if (!UI.CdtTheme.Tried) LoadThemeOnTheWindowsThread();
            FieldContext.Expect(UI.CdtTheme.Error is null && UI.CdtTheme.Styles is not null,
                "the CDT SDK theme didn't load, so windows show CSI ProMax's own older look: " + (UI.CdtTheme.Error ?? "not loaded") + ". Run the install line again (CDT.Wpf.dll and CDT.Core.dll belong beside CsiProMax.dll).");
            FieldContext.Expect(Chat.ChatUi.StylesError is null, "the chat's styles didn't load as designed, so it shows a plainer look: " + Chat.ChatUi.StylesError);
            return "the CDT SDK theme and the chat's styles are in use";
        });

        // ---- Files out and in: every kind make_document writes, each read back with the tool that reads it. ----
        var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = FieldParts.Q(folder);
        void Make(string format, string rest)
        {
            var answer = Tool("make_document", $$"""{"format":"{{format}}","title":"{{Marker}} {{format}}","folder":{{q}},{{rest}}}""", (a, _) =>
            {
                var file = FileIn(a);
                FieldContext.Expect(file.Length > 0 && File.Exists(file), "no file was written: " + a.ToJsonString());
                FieldContext.Expect(new FileInfo(file).Length > 0, $"{file} is empty");
                FieldContext.Expect(file.StartsWith(folder, StringComparison.OrdinalIgnoreCase), $"it was written outside the test's folder: {file}");
            }, $"tool make_document writes {format}");
            if (answer is not null) made[format] = FileIn(answer);
        }
        const string words = "# CPM field test\\n\\nA made-up note written by the field test. Total load **123.5 kN**.\\n\\n| Member | Section |\\n|---|---|\\n| C1 | CPM-C400 |\\n| B1 | CPM-B250x500 |\\n";
        Make("word", $"\"subtitle\":\"made by the field test\",\"content\":\"{words}\"");
        Make("pdf", $"\"content\":\"{words}\",\"landscape\":true");
        Make("excel", """
            "sheets":[{"name":"Members","title":"CPM field test","columns":["Member","Section","Length"],"rows":[["C1","CPM-C400",3.2],["B1","CPM-B250x500",6],{"cells":["Total","","=SUM(C4:C5)"],"bold":true}],"formats":["text","text","decimal"],"widths":[12,18,0]}]
            """);
        Make("powerpoint", """
            "slides":[{"title":"CPM field test slide","bullets":["A made-up point","  a sub-point with **bold**"]},{"title":"A table","table":{"columns":["Member","Section"],"rows":[["C1","CPM-C400"]]}}]
            """);
        Make("csv", $"\"content\":\"{words}\"");
        Make("text", $"\"content\":\"{words}\"");
        Make("markdown", $"\"content\":\"{words}\"");
        Make("json", $"\"content\":\"{words}\"");
        const string drawing = """
            "entities":[{"type":"line","x1":0,"y1":0,"x2":6,"y2":0,"layer":"CPM-TEST"},{"type":"polyline","points":[[0,0],[6,0],[6,5],[0,5]],"closed":true,"layer":"CPM-TEST"},{"type":"circle","x":3,"y":2.5,"r":0.3,"layer":"CPM-COLUMNS"},{"type":"text","x":0,"y":5.5,"height":0.25,"text":"CPM field test","layer":"CPM-TEXT"}]
            """;
        Make("dxf", drawing);
        Make("dwg", drawing);

        foreach (var format in new[] { "word", "excel", "powerpoint" })
            if (made.TryGetValue(format, out var file))
                Tool("read_document_text", $$"""{"path":{{FieldParts.Q(file)}}}""", (a, r) =>
                    FieldContext.Expect(r.Text.Contains("CPM field test", StringComparison.OrdinalIgnoreCase) || r.Text.Contains("CPM-C400", StringComparison.Ordinal),
                        "the text that was written is not in what was read back: " + Short(r.Text)), $"tool read_document_text reads the {format} file back");
        // The plain kinds are read by the chat's own Read, not by a tool: the file itself is looked at.
        foreach (var format in new[] { "csv", "text", "markdown", "json" })
            if (made.TryGetValue(format, out var file) && file.Length > 0)
                ctx.Step($"the {format} file holds what was written", () =>
                {
                    var text = File.ReadAllText(file);
                    FieldContext.Expect(text.Contains("CPM-C400", StringComparison.Ordinal) || text.Contains("CPM field test", StringComparison.OrdinalIgnoreCase), "the text that was written is not in the file: " + Short(text));
                    return $"{Path.GetFileName(file)}, {text.Length} characters";
                });
        if (made.TryGetValue("text", out var plain) && plain.Length > 0)
            ctx.Step("tool read_document_text says what reads a plain text file", () =>
            {
                var result = Execute(registry, app, "read_document_text", $$"""{"path":{{FieldParts.Q(plain)}}}""");
                FieldContext.Expect(result.IsError && result.Text.Contains("Read", StringComparison.Ordinal), "it should have pointed to Read: " + Short(result.Text));
                return "refused as it should: " + result.Text;
            });
        Tool("read_document_text", $$"""{"path":{{q}}}""", (_, r) =>
            FieldContext.Expect(made.Values.Where(f => f.Length > 0).Take(3).All(f => r.Text.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)),
                "the folder's files are not all listed: " + Short(r.Text)), "tool read_document_text lists a folder");
        ctx.Step("tool read_document_text says no to a file that is not there", () =>
        {
            var result = Execute(registry, app, "read_document_text", $$"""{"path":{{FieldParts.Q(Path.Combine(folder, "no such file.docx"))}}}""");
            FieldContext.Expect(result.IsError, "it should have said the file is not there: " + Short(result.Text));
            return "refused as it should: " + result.Text;
        });
        foreach (var format in new[] { "dxf", "dwg" })
            if (made.TryGetValue(format, out var file))
            {
                Tool("read_cad_drawing", $$"""{"path":{{FieldParts.Q(file)}}}""", (_, r) =>
                    FieldContext.Expect(r.Text.Contains("CPM-TEST", StringComparison.Ordinal) && r.Text.Contains("CPM-COLUMNS", StringComparison.Ordinal),
                        "the layers that were drawn are not in the overview: " + Short(r.Text)), $"tool read_cad_drawing gives the overview of the {format} file");
                Tool("read_cad_drawing", $$"""{"path":{{FieldParts.Q(file)}},"layers":["CPM-TEST","CPM-TEXT"],"kinds":["line","polyline","text"],"limit":10}""", (_, r) =>
                    FieldContext.Expect(r.Text.Contains("CPM field test", StringComparison.Ordinal), "the text that was drawn was not read back: " + Short(r.Text)),
                    $"tool read_cad_drawing lists the objects of the {format} file");
            }
        if (!made.TryGetValue("pdf", out var pdf) || pdf.Length == 0) NotTested("read_drawing", "the PDF it would read was not written (see make_document above)");
        else if (!windows) NotTested("read_drawing", "it draws PDF pages with a Windows library, and this is not Windows");
        else
        {
            Tool("read_drawing", $$"""{"file":{{FieldParts.Q(pdf)}},"page":1,"pixel_width":1200}""", (_, r) =>
                FieldContext.Expect(r.PngImage is { Length: > 1000 }, "no picture of the page came back: " + Short(r.Text)), "tool read_drawing shows a PDF page");
            Tool("read_drawing", $$"""{"file":{{FieldParts.Q(pdf)}},"page":1,"cell":"B2","pixel_width":800}""", (_, r) =>
                FieldContext.Expect(r.PngImage is { Length: > 500 }, "no picture of the cell came back: " + Short(r.Text)), "tool read_drawing zooms into a cell");
        }

        // ---- The review pack, from the model the test works on. ----
        var pack = "";
        Tool("export_pack", $$"""{"name":"cpm-field-test panel","folder":{{q}},"drift_limit":0.002}""", (a, _) =>
        {
            pack = FileIn(a);
            FieldContext.Expect(pack.Length > 0 && File.Exists(pack) && new FileInfo(pack).Length > 200, "no pack was written: " + a.ToJsonString());
        });
        // With no folder named, the pack goes beside the model. Tried on the scratch model only, whose folder is the test's own.
        const string besideStep = "tool export_pack with no folder saves beside the model";
        if (!scratch) ctx.Skip(besideStep, "that writes into the model's own folder, so it is only tried on a scratch model, never on your open model");
        else
            Tool("export_pack", """{"name":"cpm-field-test beside"}""", (a, _) =>
            {
                var file = FileIn(a);
                var model = Path.GetDirectoryName(link.Info().File) ?? "";
                FieldContext.Expect(file.Length > 0 && File.Exists(file), "no pack was written: " + a.ToJsonString());
                FieldContext.Expect(string.Equals(Path.GetFullPath(Path.GetDirectoryName(file) ?? ""), Path.GetFullPath(model), StringComparison.OrdinalIgnoreCase),
                    $"the model is in {model}; the pack went to {Path.GetDirectoryName(file)} ({a["not_beside_model"]?.ToJsonString() ?? "no reason given"})");
            }, besideStep);
        if (pack.Length == 0) NotTested("read_pack_comments", "the pack it would read was not written (see export_pack above)");
        else
            ctx.Step("tool read_pack_comments on a pack nobody has commented on", () =>
            {
                handled.Add("read_pack_comments");
                var result = Execute(registry, app, "read_pack_comments", $$"""{"path":{{FieldParts.Q(pack)}}}""");
                // No comments yet is said either as an answer or as "there are none": both are right, a crash is not.
                return (result.IsError ? "it says there are none: " : "") + result.Text;
            });

        // ---- Notes, earlier chats, the activity log, code that worked before. ----
        var note = $"{Marker} note {DateTime.Now:yyyyMMdd-HHmmss}: safe to delete";
        if (!windows)
        {
            NotTested("memory_note", "the notes belong to the chat pane, which needs Windows");
            NotTested("recall_chats", "the earlier chats belong to the chat pane, which needs Windows");
        }
        else
        {
        try
        {
            Tool("memory_note", """{"action":"list"}""", step: "tool memory_note lists the notes");
            Tool("memory_note", $$"""{"action":"remember","scope":"global","note":{{FieldParts.Q(note)}}}""", step: "tool memory_note remembers a test note");
            Tool("memory_note", """{"action":"list"}""", (_, r) => FieldContext.Expect(r.Text.Contains(note, StringComparison.Ordinal), "the note that was just kept is not in the list"),
                "tool memory_note has the test note in its list");
        }
        finally
        {
            Tool("memory_note", $$"""{"action":"forget","scope":"global","note":{{FieldParts.Q(note)}}}""", step: "tool memory_note forgets the test note");
            ctx.Step("the test note is gone again", () =>
            {
                var left = Execute(registry, app, "memory_note", """{"action":"list"}""").Text.Contains(note, StringComparison.Ordinal);
                FieldContext.Expect(!left, $"the note \"{note}\" is still kept: delete it in the Notes tab");
                return "no note of the field test is kept";
            });
        }
        Tool("recall_chats", """{"action":"list"}""", step: "tool recall_chats lists earlier chats");
        Tool("recall_chats", """{"action":"search","query":"cpm field test scratch"}""", step: "tool recall_chats searches");
        }
        Tool("get_activity_log", """{"limit":5}""");
        Tool("get_activity_log", """{"limit":5,"changes_only":true}""", step: "tool get_activity_log, changes only");
        Tool("worked_code", """{"action":"list","max":3}""", step: "tool worked_code lists");
        Tool("worked_code", """{"action":"find","query":"count the frames","max":2}""", step: "tool worked_code finds");

        // ---- Recipes: saved, listed, run, and the file taken away again. ----
        try
        {
            Tool("save_recipe", $$$"""{"name":"{{{RecipeName}}}","description":"{{{Marker}}}: reads the model twice. Safe to delete.","steps":[{"tool":"get_model_context"},{"tool":"query_objects","arguments":{"kind":"frame","limit":1}}]}""");
            Tool("list_recipes", "{}", (_, r) => FieldContext.Expect(r.Text.Contains(RecipeName, StringComparison.Ordinal), "the recipe that was just saved is not listed: " + Short(r.Text)));
            Tool("run_recipe", $$"""{"name":"{{RecipeName}}"}""", (_, r) =>
                FieldContext.Expect(!r.Text.Contains("\"error\":true", StringComparison.OrdinalIgnoreCase) && !r.Text.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase), "a step of the recipe failed: " + Short(r.Text)));
            ctx.Step("tool save_recipe says no to a tool that is not there", () =>
            {
                var result = Execute(registry, app, "save_recipe", """{"name":"cpm-field-test-bad","steps":[{"tool":"no_such_tool"}]}""");
                FieldContext.Expect(result.IsError && result.Text.Contains("no_such_tool", StringComparison.Ordinal), "it should have named the unknown tool: " + Short(result.Text));
                return "refused as it should: " + result.Text;
            });
        }
        finally
        {
            ctx.Step("the test recipe is taken away again", () => Remove(Recipes.PathFor(RecipeName), Recipes.PathFor("cpm-field-test-bad")));
        }

        // ---- A learned tool: saved, listed, run through the program's API, deleted. ----
        try
        {
            Tool("save_as_tool", $$"""{"name":"{{LearnedName}}","description":"{{Marker}}: gives 42. Safe to delete.","code":"return 40 + 2;","changes_model":false}""");
            Tool("list_learned_tools", $$"""{"name":"{{LearnedName}}","include_code":true}""", (a, r) =>
            {
                var mine = (a["tools"] as JsonArray)?.FirstOrDefault(t => t?["name"]?.GetValue<string>() == LearnedName);
                FieldContext.Expect(mine is not null, "the tool that was just saved is not listed: " + Short(r.Text));
                FieldContext.Expect((mine!["code"]?.GetValue<string>() ?? "").Contains("40 + 2", StringComparison.Ordinal), "its code is not in the list: " + mine.ToJsonString());
            });
            if (live)
            {
                Tool("run_learned_tool", $$$"""{"name":"{{{LearnedName}}}","arguments":{}}""", (_, r) => FieldContext.Expect(r.Text.Contains("42", StringComparison.Ordinal), "it should have given 42: " + Short(r.Text)));
                ctx.Step("a learned tool is found by its own name, as the chat calls it", () =>
                {
                    var tool = registry.Find(LearnedName);
                    FieldContext.Expect(tool is not null, "the registry does not find the learned tool by name");
                    var result = tool!.Execute(app, Args("{}"));
                    FieldContext.Expect(!result.IsError && result.Text.Contains("42", StringComparison.Ordinal), "it should have given 42: " + Short(result.Text));
                    return result.Text;
                });
            }
            else NotTested("run_learned_tool", "code runs against a CSI program's API, and no program is behind this window");
            Tool("delete_learned_tool", $$"""{"name":"{{LearnedName}}"}""");
            ctx.Step("tool delete_learned_tool says no to a tool that is not there", () =>
            {
                var result = Execute(registry, app, "delete_learned_tool", $$"""{"name":"{{LearnedName}}"}""");
                FieldContext.Expect(result.IsError, "it was deleted a moment ago, so this should have been refused: " + Short(result.Text));
                return "refused as it should: " + result.Text;
            });
        }
        finally
        {
            ctx.Step("the test's learned tool is taken away again", () => Remove(LearnedTools.PathFor(LearnedName)));
        }

        // ---- The team library: the sync's rules on made-up PCs, how it stands, then the real library on GitHub. ----
        var builtIn = registry.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        ctx.Step("the team library's sync rules hold on two made-up PCs and a library in memory", () =>
            TeamSyncCheck.Run(Path.Combine(folder, "Team library"), t => TeamLibrary.Check(t, builtIn)));
        Tool("team_library", """{"action":"status"}""", (a, _) =>
            FieldContext.Expect(a["line"]?.GetValue<string>() is { Length: > 0 }, "it does not say how the team library stands: " + a.ToJsonString()));
        if (!live)
            ctx.Skip("the team library on GitHub: sign in, write, read back and remove a test file",
                "this run has no CSI program behind it (the tests' stand-in), and such a run never writes to the team's real library");
        else
            ctx.Step("the team library on GitHub: sign in, write, read back and remove a test file", () =>
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                return TeamLibrary.RoundTripAsync(limit.Token).GetAwaiter().GetResult();
            });

        // ---- Code: read through the API, a change on the scratch model, and a mistake that must come back as one. ----
        if (!live)
        {
            NotTested("run_etabs_query_code", "code runs against a CSI program's API, and no program is behind this window");
            NotTested("run_etabs_code", "code runs against a CSI program's API, and no program is behind this window");
        }
        else
        {
            Tool("run_etabs_query_code", """{"description":"count the frames","code":"int n = 0; string[] names = null; Ok(sap.FrameObj.GetNameList(ref n, ref names), \"list the frames\"); return n;"}""", (_, r) =>
            {
                var frames = link.Frames().Count;
                FieldContext.Expect(r.Text.Contains(frames.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), $"the link reads {frames} frames; the code gave: {Short(r.Text)}");
            });
            ctx.Step("tool run_etabs_query_code gives a mistake in the code back as one", () =>
            {
                var result = Execute(registry, app, "run_etabs_query_code", """{"description":"a mistake on purpose","code":"return thisNameIsNotDefined;"}""");
                FieldContext.Expect(result.IsError && result.Text.Contains("thisNameIsNotDefined", StringComparison.Ordinal), "the compiler's message should name what is wrong: " + Short(result.Text));
                return "refused as it should: " + Short(result.Text);
            });
            if (!scratch) NotTested("run_etabs_code", "it changes the model, and there is no scratch model to change (the open model is never changed)");
            else
                Tool("run_etabs_code", """{"description":"add the load pattern CPM-CODE to the scratch model","code":"Ok(sap.SetModelIsLocked(false), \"unlock the scratch model\"); Ok(sap.LoadPatterns.Add(\"CPM-CODE\", 8, 0, true), \"add a load pattern\"); return \"added\";"}""", (_, r) =>
                {
                    var patterns = link.LoadPatterns().Select(p => p.Name).ToList();
                    FieldContext.Expect(patterns.Contains("CPM-CODE", StringComparer.OrdinalIgnoreCase), $"the code said {Short(r.Text)}, but the model's load patterns are: {string.Join(", ", patterns)}");
                });
        }

        // ---- The self test, as the chat runs it. ----
        Tool("run_self_test", "{}", (a, _) => FieldContext.Expect(FieldParts.Int(a["passed"]) > 0, "no check passed: " + a.ToJsonString()));
        Tool("self_test_status", "{}", (a, _) => FieldContext.Expect(a["status"]?.GetValue<string>() is "all passed" or "some failed", "it should know the run a moment ago: " + a.ToJsonString()));
        handled.Add("run_field_test"); // this is it

        // ---- The bug report and the export, written into the test's own folder, never the real bug folder. ----
        var bugs = BugLog.RootOverride;
        try
        {
            BugLog.RootOverride = Path.Combine(ctx.Folder, "Bugs");
            ctx.Step("tool report_bug writes a report (a made-up bug, no picture of the screen)", () =>
            {
                handled.Add("report_bug");
                var file = BugReport.Create(app, $"{Marker}: a made-up report to check that reports are written. Not a real bug.", (null, "left out by the field test"), "the field test");
                FieldContext.Expect(File.Exists(file) && new FileInfo(file).Length > 100, "no report file was written: " + file);
                return $"{Path.GetFileName(file)}, {new FileInfo(file).Length} bytes";
            });
            Tool("export_chats_and_bugs", """{"period":"day"}""", (a, r) =>
            {
                var file = FileIn(a);
                FieldContext.Expect(file.Length == 0 || File.Exists(file), "the export names a file that is not there: " + Short(r.Text));
            });
        }
        finally { BugLog.RootOverride = bugs; }

        // ---- What needs the program's window, or a person. ----
        if (!windows || CsiProMax.App.MainWindow == IntPtr.Zero)
            NotTested("capture_view_image", "it takes a picture of the program's window, and this window was not opened from a CSI program");
        else
            Tool("capture_view_image", """{"pixel_width":800}""", (_, r) => FieldContext.Expect(r.PngImage is { Length: > 1000 }, "no picture came back: " + Short(r.Text)),
                "tool capture_view_image takes a picture of the program's window (not kept)");
        foreach (var (name, json) in new[] { ("chat_jobs", """{"action":"list"}"""), ("chat_questions", """{"action":"list"}""") })
            ctx.Step($"tool {name} lists", () =>
            {
                handled.Add(name);
                ToolResult result;
                try { result = Execute(registry, app, name, json); }
                catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
                {
                    // With a window open the chat is there, so this is a failure; a run without a window has no chat to list from.
                    if (windows && HasWindow()) throw;
                    throw new FieldSkipException($"this run has no CSI ProMax window, so there is no chat to list from ({ex.Message}); Help > Field test in the window checks it");
                }
                if (result.IsError && (!windows || !HasWindow())) throw new FieldSkipException($"this run has no CSI ProMax window, so there is no chat to list from ({Short(result.Text)}); Help > Field test in the window checks it");
                FieldContext.Expect(!result.IsError, "it answered with an error: " + result.Text);
                return result.Text;
            });
        // ask_user waits for a person on a phone, which a test has not: what is tested is that outside an autopilot it
        // refuses at once with a plain reason and never waits.
        ctx.Step("tool ask_user refuses at once outside an autopilot, with a plain reason", () =>
        {
            handled.Add("ask_user");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = Execute(registry, app, "ask_user", """{"action":"ask","question":"field test"}""");
            FieldContext.Expect(result.IsError && result.Text.Contains("autopilot", StringComparison.OrdinalIgnoreCase) && clock.Elapsed.TotalSeconds < 5,
                $"it did not refuse at once with its reason (after {clock.Elapsed.TotalSeconds:0.0} s): {Short(result.Text)}");
            return result.Text;
        });

        ctx.Step("every tool of the panel was run, or is listed as not tested", () =>
        {
            var missing = own.Where(n => !handled.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            FieldContext.Expect(missing.Count == 0, $"{missing.Count} of {own.Count} tools of the panel were not run by the field test: {string.Join(", ", missing)}");
            return $"all {own.Count} tools of the panel were run or listed";
        });
    }

    /// <summary>
    /// In each program's scratch window: a learned tool made in another CSI program runs here through this program's
    /// API, and then says it has run here too (what the team sees in the library). Learned tools are shared by ETABS,
    /// SAP2000 and SAFE, so each of the three is checked, not only the first.
    /// </summary>
    public static void InProgram(FieldContext ctx, ICsiLink link, CsiProgram program, bool live)
    {
        var step = $"a learned tool made in another program runs in {program.Name}";
        if (!live) { ctx.Skip(step, "code runs against a CSI program's API, and no program is behind this window"); return; }
        var name = "cpm_field_test_" + program.Key;
        var other = CsiProgram.All.First(p => p.Key != program.Key).Key;
        const string code = "int n = 0; string[] names = null; Ok(sap.FrameObj.GetNameList(ref n, ref names), \"list the frames\"); return n;";
        try
        {
            ctx.Step(step, () =>
            {
                var made = LearnedTool.Create(name, Marker + ": counts the frames. Safe to delete.", code, "{}", [], false, 0, null, "", [other]);
                Directory.CreateDirectory(LearnedTools.Folder);
                File.WriteAllText(LearnedTools.PathFor(name), made.ToJson());
                // The test's own code: allowed here so the run does not stop to ask.
                LearnedTools.MarkApproved(code);
                var registry = ToolRegistry.CreateDefault(link is OfflineEtabs, program);
                var tool = registry.Find(name) ?? throw new FieldCheckException("the registry does not find the learned tool by its name");
                var result = tool.Execute(new EtabsApp(link), new ToolArgs(new Dictionary<string, JsonElement>()));
                FieldContext.Expect(!result.IsError, "it failed in " + program.Name + ": " + Short(result.Text));
                var frames = link.Frames().Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                FieldContext.Expect(result.Text.Contains(frames, StringComparison.Ordinal), $"the model has {frames} frames; the tool gave: {Short(result.Text)}");
                var after = LearnedTools.Load(name) ?? throw new FieldCheckException("the tool's file is gone after it ran");
                FieldContext.Expect(after.Programs.Contains(program.Key), $"after it worked here it still says only {string.Join(", ", after.Programs)}, so the team would not see that it works in {program.Name}");
                return $"it counted {frames} frames through {program.Name}'s API and now says it runs in {string.Join(", ", after.Programs)}";
            });
        }
        finally
        {
            ctx.Step($"the learned tool of {program.Name}'s check is taken away again", () => Remove(LearnedTools.PathFor(name)));
        }
    }

    private static ToolResult Execute(ToolRegistry registry, EtabsApp app, string name, string json)
    {
        var tool = registry.Find(name) ?? throw new FieldCheckException($"There is no tool named {name} in the panel.");
        return tool.Execute(app, Args(json));
    }

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }

    /// <summary>A tool's answer as JSON; plain text when it is not JSON.</summary>
    private static JsonNode Node(string text)
    {
        try { return JsonNode.Parse(text) ?? JsonValue.Create(text)!; }
        catch (JsonException) { return JsonValue.Create(text)!; }
    }

    /// <summary>The file an answer names: the first of its usual names that holds a path with an extension.</summary>
    private static string FileIn(JsonNode answer)
    {
        if (answer is not JsonObject o) return "";
        foreach (var key in new[] { "file", "path", "saved", "pack", "zip", "output" })
            if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && Path.GetExtension(s).Length > 1) return s;
        foreach (var (_, value) in o)
            if (value is JsonValue v && v.TryGetValue<string>(out var s) && Path.IsPathRooted(s) && Path.GetExtension(s).Length > 1) return s;
        return "";
    }

    /// <summary>Deletes the test's own files, one by one, and says what is left.</summary>
    private static string Remove(params string[] files)
    {
        foreach (var file in files)
            if (File.Exists(file)) File.Delete(file);
        var left = files.Where(File.Exists).ToList();
        FieldContext.Expect(left.Count == 0, "still there, delete it by hand: " + string.Join(", ", left));
        return "nothing of the test is left";
    }

    private static string Short(string text) => text.Length > 300 ? text[..300] + "..." : text;

    // In methods of their own: off Windows (the tests) the steps above must not need the window types to load.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool HasWindow() => OperatingSystem.IsWindows() && HasUi();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool HasUi() => CsiProMax.App.Ui is not null;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void LoadThemeOnTheWindowsThread() => CsiProMax.App.Ui?.Invoke(() => { _ = UI.CdtTheme.Styles; });
}
