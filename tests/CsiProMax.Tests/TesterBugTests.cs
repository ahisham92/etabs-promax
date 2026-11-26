using CsiProMax.Update;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsiProMax;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

/// <summary>
/// One test for each bug a tester sent back, and one for the kind of mistake behind it, so that neither the bug nor
/// its kind comes back. The first report: SAFE 23 with CSI ProMax 0.4.1, a model that had never been analysed
/// (THT.FDB), on a PC that also had ETABS 22 and 23, SAFE 22 and SAP2000 26. The table names, section names and
/// plug-in lists below are the ones in that report.
/// </summary>
static class TesterBugTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }
    static readonly JsonElement None = JsonDocument.Parse("{}").RootElement;

    // The two tables of the tester's model that had rows although nothing had been analysed: what was entered.
    const string Overwrites = "Concrete Slab Design Overwrites - Punching Shear - General", Combinations = "Concrete Slab Design Load Combination Data";

    /// <summary>The status of each check in a report (not the statuses the program wrote in its own rows).</summary>
    static IEnumerable<string> Statuses(JsonNode? report) =>
        report?["checks"] is JsonObject checks ? checks.Select(c => c.Value?["status"]?.GetValue<string>() ?? "(none)") : ["(the report has no checks)"];

    static IEnumerable<string> Unused(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key == "status" && value is JsonValue v && v.TryGetValue<string>(out var text)) yield return text!;
                    else foreach (var inner in Statuses(value)) yield return inner;
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var inner in Statuses(item)) yield return inner;
                break;
        }
    }

    static List<string> Summary(JsonNode report) => report["summary"] is JsonArray list ? list.Select(n => n?.GetValue<string>() ?? "").ToList() : [];

    /// <summary>A model in memory that says it is this program, with the tester's two input tables filled.</summary>
    static MemoryEtabs Entered(CsiProgram program)
    {
        var m = new MemoryEtabs { Program = program };
        m.AddFrame(0, 0, 0, 0, 0, 3.2, "C500x500");
        m.AddArea([(0, 0, 3.2), (6, 0, 3.2), (6, 5, 3.2), (0, 5, 3.2)], "S200");
        m.ResultTables[Overwrites] = new(["Point", "Check", "Status", "Ratio"], [["11", "Program Determined", "OK", "0.4"]]);
        m.ResultTables[Combinations] = new(["Combo", "Type", "Status"], [["ULS1", "Strength", "OK"], ["SLS1", "Service", "OK"]]);
        return m;
    }

    /// <summary>The tester's SAFE: it lists its own tables (no 'Story Definitions'), and nothing has been analysed.</summary>
    static SafeMemory TestersSafe(out MemoryEtabs inner)
    {
        inner = Entered(CsiProgram.Safe);
        inner.ResultTables["Load Pattern Definitions"] = new(["Name", "Type", "SelfWtMult"], [["Dead", "Dead", "1"], ["Live", "Live", "0"]]);
        return new SafeMemory(inner)
        {
            Listed = [new("Load Pattern Definitions", "Load Pattern Definitions", false), new(Overwrites, Overwrites, false), new(Combinations, Combinations, false), new("Soil Pressures", "Soil Pressures", true)],
            Api = new SafeApi(),
        };
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        // ---- "slab design: pass" on a model that had never been analysed ----

        yield return ("first tester: a model that was never analysed gets no pass and no fail in any program, whatever its tables hold", () =>
        {
            foreach (var program in CsiProgram.All.Where(p => p.Ready))
            {
                var m = Entered(program);
                // Left-over result rows too (a table file read in, or a program that keeps old rows): still no verdict.
                m.ResultTables["Concrete Slab Design - Strip Based Design"] = new(["Strip", "SpanID", "Location", "AsTop", "AsBot", "Status"], [["CSA1", "Span 1", "Start", "0.00085", "0.0004", "OK"]]);
                m.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Direction", "Drift"], [["Story1", "EQX", "X", "0.001"]]);
                m.ResultTables["Modal Participating Mass Ratios"] = new(["Mode", "Period", "UX", "UY", "RZ", "SumUX", "SumUY"], [["1", "0.5", "0.95", "0", "0", "0.95", "0"], ["2", "0.4", "0", "0.95", "0", "0.95", "0.95"]]);
                Check(!m.Info().Locked, "the model is not analysed");
                var report = ProgramTools.Report(None, m);
                var lines = Summary(report);
                Check(lines.Count >= 4, $"{program.Name}'s report has its checks ({lines.Count})");
                var verdicts = lines.Where(l => !l.EndsWith(": " + Verdict.NotAvailable, StringComparison.Ordinal)).ToList();
                Equal("", string.Join("; ", verdicts), $"{program.Name}: checks that answered as if the model had been analysed");
                Equal("", string.Join(", ", Statuses(report).Where(s => s != Verdict.NotAvailable).Distinct()), $"{program.Name}: a status other than 'not available'");
                Check(Readable.Text(report).Contains(program.Name + "'s analysis has not run", StringComparison.Ordinal), $"{program.Name}'s report says why, in its own name");
            }
        });

        yield return ("first tester: every status a check gives is one of pass, fail, reported, not available, in every program", () =>
        {
            var etabs = Entered(CsiProgram.Etabs);
            etabs.Locked = true;
            etabs.ResultTables["Modal Participating Mass Ratios"] = new(["Mode", "Period", "UX", "UY", "RZ", "SumUX", "SumUY"], [["1", "0.5", "0.95", "0", "0", "0.95", "0"], ["2", "0.4", "0", "0.95", "0", "0.95", "0.95"]]);
            foreach (var (name, link) in new (string, ICsiLink)[] { ("ETABS", etabs), ("SAFE", SafeTests.Safe()), ("SAP2000", new CsiProMax.Assistant.Sap2000.SapLink(new SapStandIn())) })
            {
                var report = ProgramTools.Report(None, link);
                var statuses = Statuses(report).Distinct().ToList();
                Equal("", string.Join(", ", statuses.Where(s => !Verdict.Words.Contains(s))), $"{name}: a status that is not one of the four words");
                Check(statuses.Any(s => s is Verdict.Pass or Verdict.Fail), $"{name}'s analysed model gets a verdict somewhere ({string.Join(", ", statuses)})");
                foreach (var line in Summary(report))
                    Check(Verdict.Words.Any(w => line.EndsWith(": " + w, StringComparison.Ordinal)), $"{name}: the summary line '{line}' ends with one of the four words");
            }
            // SAP2000 said "passes" and "fails", and "passes" where there was no limit to hold the value against.
            Equal(Verdict.Reported, CsiProMax.Assistant.Sap2000.SapTools.DisplacementStatus(JsonNode.Parse("""{"lateral":{"largest":0.004}}""")!), "no limit: reported, not passed");
            Equal(Verdict.Pass, CsiProMax.Assistant.Sap2000.SapTools.DisplacementStatus(JsonNode.Parse("""{"lateral":{"allowed":0.006,"passes":true}}""")!), "within the limit");
            Equal(Verdict.Fail, CsiProMax.Assistant.Sap2000.SapTools.DisplacementStatus(JsonNode.Parse("""{"lateral":{"allowed":0.006,"passes":true},"vertical":{"allowed":0.01,"passes":false}}""")!), "one limit passed");
            Equal("pass, fail, reported, not available", string.Join(", ", Verdict.Words), "the four words");
            Equal(Verdict.NotAvailable, Verdict.From(0, 0), "nothing judged is not a pass");
        });

        // ---- tables of what was entered, listed and read as results ----

        yield return ("first tester: a table of what the engineer entered is never a result (overwrites, design load combinations)", () =>
        {
            foreach (var entered in new[] { Overwrites, Combinations, "Concrete Frame Design Preferences - ACI 318-19", "Slab Property Definitions", "Area Assignments - Summary", "Load Combination Definitions", "Area Connectivity", "Frame Section Property Definitions - Summary", "Analysis Options" })
            {
                Check(Verdict.IsInputTable(entered), $"'{entered}' is what was entered");
                Check(!DesignResults.IsDesignTable(entered), $"'{entered}' is not a design result");
                Check(!ResultTools.IsDesignSummary(entered), $"'{entered}' is not a design summary");
            }
            foreach (var result in new[] { "Story Drifts", "Joint Displacements", "Base Reactions", "Soil Pressures", "Strip Forces", "Concrete Slab Design - Punching Shear Data", "Concrete Slab Design - Strip Based Design", "Steel Frame Design Summary - AISC 360-16", "Shear Wall Pier Design Summary - ACI 318-19", "Modal Participating Mass Ratios" })
                Check(!Verdict.IsInputTable(result), $"'{result}' is a result");
            Check(DesignResults.IsDesignTable("Concrete Slab Design - Punching Shear Data") && DesignResults.IsDesignTable("Shear Wall Pier Design Summary - ACI 318-19"), "design results are still found");

            // An analysed SAFE model that also has them: the overview lists results only, and the slab design does not count them.
            var safe = SafeTests.Safe();
            var overview = CsiProMax.Assistant.Safe.SafeTools.All.First(t => t.Name == "safe_results_overview").Run(None, WithEntered(safe)).ToJsonString();
            Check(!overview.Contains("Overwrites", StringComparison.Ordinal) && !overview.Contains("Load Combination Data", StringComparison.Ordinal), "SAFE's overview lists no table of what was entered: " + overview);
            Check(overview.Contains("Punching Shear Data", StringComparison.Ordinal), "and still lists the results");
            // The same in ETABS's overview.
            var etabs = Entered(CsiProgram.Etabs);
            etabs.Locked = true;
            etabs.ResultTables["Base Reactions"] = new(["Output Case", "FX", "FY", "FZ"], [["Dead", "0", "0", "100"]]);
            var seen = ResultTools.All.First(t => t.Name == "results_overview").Run(None, etabs);
            var listed = seen["result_tables_with_data"]!.ToJsonString() + seen["design_tables_with_data"]!.ToJsonString();
            Check(!listed.Contains("Overwrites", StringComparison.Ordinal) && !listed.Contains("Load Combination Data", StringComparison.Ordinal), "ETABS's overview lists no table of what was entered: " + listed);
            Check(listed.Contains("Base Reactions", StringComparison.Ordinal), "and still lists the results");
        });

        // ---- the self test in SAFE: 'Story Definitions', ETABS's tools, the selection, the wall shown as a slab ----

        yield return ("first tester: the self test in SAFE reads a table SAFE lists, uses SAFE's tools, and leaves the selection as it was", () =>
        {
            var safe = TestersSafe(out _);
            var area = safe.Areas()[0].Name;
            safe.Select("area", area);
            var steps = SelfTest.Steps(safe, false);
            SelfTest.StepResult Named(string name) => steps.SingleOrDefault(s => s.Name == name) ?? throw new Exception($"there is no step '{name}': {string.Join(", ", steps.Select(s => s.Name))}");

            // "ETABS refused to read the table 'Story Definitions'": SAFE has no such table.
            var read = Named("ReadTable");
            Check(read.Ok, "ReadTable: " + read.Detail);
            Check(read.Detail.Contains("Load Pattern Definitions", StringComparison.Ordinal), "it read a table the program lists: " + read.Detail);
            Equal("", string.Join("; ", steps.Where(s => (s.Detail + s.Full).Contains("Story Definitions", StringComparison.Ordinal)).Select(s => s.Name)), "steps that asked SAFE for ETABS's table");
            Equal("", string.Join("; ", steps.Where(s => !s.Ok).Select(s => s.Name + ": " + s.Detail)), "steps that failed in SAFE");

            // A SAFE that lists no table with data: nothing to read, said so, and not a failure.
            var empty = new SafeMemory(new MemoryEtabs { Program = CsiProgram.Safe }) { Listed = [new("Soil Pressures", "Soil Pressures", true)], Api = new SafeApi() };
            var skipped = SelfTest.Steps(empty, false).Single(s => s.Name == "ReadTable");
            Check(skipped.Ok && skipped.Detail.StartsWith("skipped: SAFE lists no table with data", StringComparison.Ordinal), skipped.Detail);

            // The tools are SAFE's, and every text names SAFE.
            Check(Named("Tool get_model_context").Ok, Named("Tool get_model_context").Detail);
            Equal("", string.Join("; ", steps.Where(s => s.Name.StartsWith("ETABS ", StringComparison.Ordinal)).Select(s => s.Name)), "steps named after ETABS, in SAFE");
            Equal("", string.Join("; ", steps.Where(s => Regex.IsMatch(s.Detail + s.Full, @"\bETABS\b")).Select(s => s.Name + ": " + s.Detail)), "answers that name ETABS, in SAFE");
            Check(Named("SAFE checks give no verdict without an analysis").Ok, Named("SAFE checks give no verdict without an analysis").Detail);

            // The selection the engineer had is back.
            Check(Named("Select").Ok, Named("Select").Detail);
            Equal("area " + area, string.Join(", ", safe.Selected().Select(s => s.Kind + " " + s.Name)), "the selection after the test");

            // "WALL1: type 0, shell 0, , 0": a wall property is not a slab with no thickness.
            var slabs = Named("SAFE slab properties").Detail;
            Check(slabs.Contains("WALL1: not a slab property", StringComparison.Ordinal) && !slabs.Contains("WALL1: type", StringComparison.Ordinal), slabs);
            Check(slabs.Contains("F400: type 6, shell 2, F'c_40, 0.4", StringComparison.Ordinal), "the slab properties are still read, with their letters as they are: " + slabs);

            // Answers are readable: no JSON wrapped in JSON, no quote written as a code.
            Equal("", string.Join("; ", steps.Where(s => (s.Detail + s.Full).Contains("\\u00", StringComparison.Ordinal) || s.Detail.StartsWith("\"{", StringComparison.Ordinal) || s.Detail.StartsWith("\"[", StringComparison.Ordinal)).Select(s => s.Name + ": " + s.Detail)), "answers that are not readable");
        });

        yield return ("first tester: the self test asks each program for its own tools and tables, never ETABS's by name", () =>
        {
            // The kind of mistake: a table name written into shared code. A table is read by a key the program listed.
            var written = new List<string>();
            foreach (var (path, text) in SourceText.Files("src/CsiProMax.Assistant"))
            {
                if (path.EndsWith("/MemoryEtabs.cs", StringComparison.Ordinal) || path.EndsWith("/OfflineEtabs.cs", StringComparison.Ordinal)) continue;   // they are the tables
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    if (Regex.IsMatch(lines[i], @"\.ReadTable\(\s*\$?""") && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) written.Add($"{path}:{i + 1}: {lines[i].Trim()}");
            }
            Equal("", string.Join("\n", written), "a table read by a name written in the code (take it from the program's table list: ResultTools.Read, or Tables())");

            // What the chat is given in each program is what the self test and the field test run: one list, one tool per name.
            foreach (var program in CsiProgram.All.Where(p => p.Ready))
            {
                var tools = ProgramTools.Chat(program);
                Equal(tools.Count, tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count(), $"{program.Name}: each tool once");
                var context = tools.First(t => t.Name == "get_model_context");
                var answer = context.Run(None, new MemoryEtabs { Program = program });
                Equal(program.Name, (string?)answer["program"], $"{program.Name}'s model context names the program");
            }
            var sap = ProgramTools.Chat(CsiProgram.Sap2000).First(t => t.Name == "get_model_context");
            Check(ReferenceEquals(sap, CsiProMax.Assistant.Sap2000.SapTools.All.First(t => t.Name == "get_model_context")), "SAP2000's own get_model_context takes the place of the shared one");
        });

        // ---- the report that could not be read ----

        yield return ("first tester: a report shows answers as they are, not JSON wrapped in JSON", () =>
        {
            Equal("""{"file":"THT.FDB","locked":false}""", Readable.Text("""{"file":"THT.FDB","locked":false}"""), "a text that is JSON already is left as it is");
            Equal("""{"file":"(model folder)\\THT.FDB","material":"F'c_40","note":"say \"no\" <here> & there"}""",
                Readable.Text(new JsonObject { ["file"] = @"(model folder)\THT.FDB", ["material"] = "F'c_40", ["note"] = "say \"no\" <here> & there" }), "an answer keeps its quotes and letters");
            Equal("""["F400: type 6, shell 2, F'c_40, 0.4","WALL1: not a slab property (SAFE answered 1)"]""",
                Readable.Text(new List<string> { "F400: type 6, shell 2, F'c_40, 0.4", "WALL1: not a slab property (SAFE answered 1)" }), "a list of lines");
            Equal("null", Readable.Text(null), "nothing");
            Check(!Readable.Text(new { Name = "é ü ع", Corners = new[] { (1.5, 2.0, 0.0) } }).Contains("\\u", StringComparison.Ordinal), "letters of other alphabets are written as they are");
        });

        // ---- the wrong program named ----

        yield return ("first tester: no shared text says ETABS (the texts name the program the window was started from)", () =>
        {
            // Files that are about ETABS itself, or whose text is put into the program's words as a whole.
            var apart = new Dictionary<string, string>
            {
                ["src/CsiProMax.Assistant/Etabs/OfflineEtabs.cs"] = "the Modeler: it writes ETABS's files and says so",
                ["src/CsiProMax.Assistant/Tools/ModelerTools.cs"] = "the Modeler's tools",
                ["src/CsiProMax.Assistant/Field/EtabsFieldSteps.cs"] = "the field test's steps for ETABS",
                ["src/CsiProMax.Assistant/Panel/Agent/SystemPrompt.cs"] = "the shared prompt: ProgramPrompt.Reword puts it into the program's words (checked below)",
                ["src/CsiProMax.Assistant/Safe/SafePrompt.cs"] = "tells the chat it is SAFE, not ETABS",
                ["src/CsiProMax.Assistant/Sap2000/SapPrompt.cs"] = "tells the chat it is SAP2000, not ETABS",
            };
            var seen = new HashSet<string>();
            var wrong = new List<string>();
            foreach (var (path, text) in SourceText.Files("src/CsiProMax.Assistant"))
            {
                if (apart.ContainsKey(path)) { seen.Add(path); continue; }
                foreach (var literal in SourceText.Literals(path, text))
                {
                    if (!Regex.IsMatch(literal.Text, @"(?<![A-Za-z0-9_])ETABS(?![A-Za-z0-9_])")) continue;
                    if (literal.Opening.Contains("// ETABS itself", StringComparison.Ordinal) || literal.Opening.Contains("Csi.Words(", StringComparison.Ordinal)) continue;
                    var shown = Regex.Replace(literal.Text, @"\s+", " ").Trim();
                    wrong.Add($"{path}:{literal.Line}: \"{(shown.Length > 100 ? shown[..100] + "..." : shown)}\"");
                }
            }
            Equal("", string.Join("\n", wrong), "texts that say ETABS (say Csi.Name, or the link's Program.Name; a line that means ETABS itself ends with the comment // ETABS itself)");
            Equal("", string.Join(", ", apart.Keys.Where(k => !seen.Contains(k))), "files set apart that are no longer there");

            // The reader of the source itself: what it must and must not take for a text.
            var sample = SourceText.Literals("x.cs", "var a = \"one ETABS\"; // \"a comment\"\nvar b = $\"two {(c ? \"in a hole\" : d.ETABS)} \\nETABS\";\nvar e = @\"\"\"quoted\"\" ETABS\";\nvar f = \"\"\"\n  raw ETABS\n  \"\"\";\nvar g = '\"';\nvar h = \"after\";");
            Equal("one ETABS|in a hole|two    ETABS|\"quoted\" ETABS|\n  raw ETABS\n  |after", string.Join("|", sample.Select(l => l.Text)), "the texts in a piece of source");
            Equal("1,2,2,3,4,8", string.Join(",", sample.Select(l => l.Line)), "and their lines");

            // The rule itself, with the window started from SAFE.
            var before = Csi.Program;
            try
            {
                Csi.Program = CsiProgram.Safe;
                Equal("SAFE|a SAFE|A SAFE", $"{Csi.Name}|{Csi.AName}|{Csi.ANameCap}", "the names");
                Equal("Open SAFE and a model, then start CSI ProMax from SAFE's Tools menu.", Csi.Words("Open ETABS and a model, then start CSI ProMax from ETABS's Tools menu."), "a fixed text in SAFE's words");
                Csi.Program = CsiProgram.Etabs;
                Equal("ETABS|an ETABS|An ETABS", $"{Csi.Name}|{Csi.AName}|{Csi.ANameCap}", "the names in ETABS");
            }
            finally { Csi.Program = before; }

            // What the chat is told, in SAFE and in SAP2000: the shared prompt never leaves ETABS standing as the program.
            foreach (var program in new[] { CsiProgram.Safe, CsiProgram.Sap2000 })
            {
                var prompt = CsiProMax.Agent.ProgramPrompt.Reword("You are CSI ProMax, an AI assistant working beside CSI ETABS. Open ETABS first.", program);
                Equal($"You are CSI ProMax, an AI assistant working beside CSI {program.Name}. Open {program.Name} first.", prompt, "the prompt's words");
            }

            // A link's own messages name its program (the in-memory model answers as each of them).
            foreach (var program in CsiProgram.All.Where(p => p.Ready))
            {
                var m = new MemoryEtabs { Program = program, Locked = true };
                string said;
                try { m.AddFrame(0, 0, 0, 0, 0, 3, "C500x500"); said = "(nothing)"; }
                catch (EtabsException ex) { said = ex.Message; }
                Equal($"{program.Name} refused: the model is locked. Unlock it in {program.Name} first.", said, "a refusal");
            }
        });

        yield return ("first tester: the field test stops an answer that names another program", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-words-" + Guid.NewGuid().ToString("N")[..8]);
            var inSafe = new FieldContext(CsiProgram.Safe, folder, new MemoryEtabs { Program = CsiProgram.Safe });
            Equal("ETABS", inSafe.NamesAnotherProgram("ETABS refused to read the table 'Story Definitions' (check its name with the table list)."), "the tester's message, in SAFE");
            Equal(null, inSafe.NamesAnotherProgram("SAFE refused to read the table 'Story Definitions'."), "SAFE's own name");
            Equal(null, inSafe.NamesAnotherProgram("The file METABS_final.fdb was read."), "a name that only holds the letters");
            Equal(null, new FieldContext(CsiProgram.Etabs, folder, new MemoryEtabs()).NamesAnotherProgram("ETABS refused. In SAFE it is different."), "ETABS may name itself and the others");
            Equal(null, new FieldContext(CsiProgram.Safe, folder, new OfflineEtabs()).NamesAnotherProgram("The Modeler has no ETABS behind it."), "the Modeler writes ETABS's files and says so");

            // Our sentences in an answer are looked at; names from the model are not ours.
            var words = FieldContext.WordsOf(JsonNode.Parse("""{"file":"ETABS tower.fdb","note":"Run it in ETABS.","checks":[{"status":"fail","warning":"ETABS says no"},{"table":"ETABS Export"}],"next":"Open SAFE"}""")).ToList();
            Equal("Run it in ETABS.|ETABS says no|Open SAFE", string.Join("|", words), "the sentences of ours");

            // And it is used: a tool that answers in ETABS's words fails its step in SAFE.
            inSafe.Offer([new Tool("say_it", "Says it.", """{"type":"object"}""", Ask.Never, (_, _) => new JsonObject { ["note"] = "Unlock the model in ETABS first." })]);
            inSafe.Tool("say_it");
            var step = inSafe.Steps.Single();
            Check(!step.Ok && step.Detail.Contains("answered in ETABS's words, and this is SAFE", StringComparison.Ordinal), $"the step fails and says why: {step.Detail}");
        });

        yield return ("first tester: the field test holds 'no verdict before the analysis' on each program's own scratch model", () =>
        {
            foreach (var program in CsiProgram.All.Where(p => p.Ready))
            {
                var ctx = new FieldContext(program, Path.GetTempPath(), Entered(program));
                FieldParts.NoVerdictBeforeAnalysis(ctx);
                var step = ctx.Steps.Single();
                Check(step.Ok && !step.Skipped, $"{program.Name}: {step.Detail}");
                Check(step.Name.Contains(program.Name, StringComparison.Ordinal), step.Name);
                Check((step.Full ?? step.Detail).Contains(Combinations, StringComparison.Ordinal), "the report keeps which tables had rows before the analysis");
            }
            // A program that did answer as if analysed (what 0.4.1 did in SAFE) fails the step, with the check named.
            var wrong = new FieldContext(CsiProgram.Safe, Path.GetTempPath(), new Answering(Entered(CsiProgram.Safe)));
            FieldParts.NoVerdictBeforeAnalysis(wrong);
            // (The stand-in says it is analysed only to the tools, so the report is made as on an analysed model.)
            var failed = wrong.Steps.Single();
            Check(!failed.Ok || failed.Skipped, "a model the step cannot judge is not passed: " + failed.Detail);
        });

        // ---- ETABS 23 had no Tools > CSI ProMax ----

        yield return ("first tester: a program whose Tools menu has no CSI ProMax gets it at the next start (ETABS 23 on the tester's PC)", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cpm-menu-" + Guid.NewGuid().ToString("N")[..8]);
            var install = Path.Combine(root, "CSI ProMax");
            var settings = Path.Combine(root, "Computers and Structures");
            Directory.CreateDirectory(install);
            try
            {
                foreach (var plugIn in ToolsMenu.PlugIns) File.WriteAllText(Path.Combine(install, plugIn.Dll), "");
                string Listed(string name) => $"[Units]\r\n Force=kN\r\n[PlugIn]\r\n NumberPlugIns=1\r\n PlugInName={name}\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath={Path.Combine(install, name + ".dll")}\r\n\r\n[Graphics]\r\n Colour=1\r\n";
                const string none = "[Units]\r\n Force=kN\r\n[PlugIn]\r\n NumberPlugIns=0\r\n\r\n[Graphics]\r\n Colour=1\r\n";
                string Ini(string folder, string file, string text)
                {
                    Directory.CreateDirectory(Path.Combine(settings, folder));
                    var path = Path.Combine(settings, folder, file);
                    File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(text));
                    return path;
                }
                // The five lists of the tester's PC: all had CSI ProMax but ETABS 23.
                var etabs22 = Ini("ETABS 22", "ETABS.ini", Listed("CsiProMaxPlugin"));
                var etabs23 = Ini("ETABS 23", "ETABS.ini", none);
                Ini("SAFE 22", "SAFE.ini", Listed("CsiProMaxSafePlugin"));
                Ini("SAFE 23", "SAFE.ini", Listed("CsiProMaxSafePlugin"));
                Ini("SAP2000 26", "SAP2000v26.ini", Listed("CsiProMaxSapPlugin"));
                Ini("CSiBridge 26", "CSiBridge.ini", none);
                Ini("ETABS 17", "ETABS.ini", none);

                // While ETABS 23 is open its list is left alone (it rewrites the file when it closes), and it is said.
                var open = ToolsMenu.Repair(install, settings, (program, number) => program.Key == "etabs" && number == 23);
                Equal(ToolsMenu.State.Open, open.Single(e => e.Label == "ETABS 23").State, "ETABS 23, open");
                Equal(none, System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(etabs23)), "its file while it is open");

                var entries = ToolsMenu.Repair(install, settings, (_, _) => false);
                Equal("ETABS 22, ETABS 23, SAFE 22, SAFE 23, SAP2000 26", string.Join(", ", entries.Select(e => e.Label)), "the programs CSI ProMax works with");
                var added = entries.Single(e => e.Label == "ETABS 23");
                Equal(ToolsMenu.State.Added, added.State, "ETABS 23");
                Check(added.Listed && added.Says.Contains("is added now", StringComparison.Ordinal), added.Says);
                Equal(Listed("CsiProMaxPlugin"), System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(etabs23)), "ETABS 23's file, with the rest of it kept");
                Equal(none, System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(etabs23 + ".before-csi-promax")), "the file as it was is kept beside it");
                Equal("", string.Join(", ", entries.Where(e => e.Label != "ETABS 23" && e.State != ToolsMenu.State.Already).Select(e => $"{e.Label} {e.State}")), "the others were listed already");
                Equal(Listed("CsiProMaxPlugin"), System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(etabs22)), "and are left as they were");
                Check(!File.Exists(etabs22 + ".before-csi-promax"), "a file that is not changed gets no copy");

                // The next start finds nothing more to do.
                Equal("", string.Join(", ", ToolsMenu.Repair(install, settings, (_, _) => false).Where(e => e.State != ToolsMenu.State.Already).Select(e => $"{e.Label} {e.State}")), "the second look");

                // A list with someone else's plug-in is never changed: how several are written is not known.
                var other = "[PlugIn]\r\n NumberPlugIns=1\r\n PlugInName=Someone.Else\r\n PlugInMenuText=Else\r\n PlugInPath=C:\\Else\\Else.dll\r\n";
                var sap = Ini("SAP2000 25", "SAP2000v25.ini", other);
                var left = ToolsMenu.Repair(install, settings, (_, _) => false).Single(e => e.Label == "SAP2000 25");
                Equal(ToolsMenu.State.Others, left.State, "a list with another plug-in");
                Equal(other, System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(sap)), "is left as it is");
                Check(left.Says.Contains("Tools > Add/Show Plugins", StringComparison.Ordinal) && left.Says.Contains("CsiProMaxSapPlugin", StringComparison.Ordinal), "and the engineer is told how to add it: " + left.Says);
            }
            finally { Directory.Delete(root, true); }
        });

        // ---- the second report: ETABS 20 answered "Cannot create plugin!" (CSI ProMax 0.4.4) ----

        // ---- the rerun "on 0.4.5" that was made by 0.4.1: the window offered 0.4.5 and the old code still ran the test ----

        yield return ("second SAFE run: one rule says when the running version is not the newest, and how to get onto it", () =>
        {
            var old = new Version(0, 4, 1);
            Check(Updater.Behind(new Version(0, 4, 5), "0.4.5", "0.4.5", null) is null, "the newest version was called old");
            Check(Updater.Behind(old, null, null, null) is null, "with nothing known, nothing is said");
            var ready = Updater.Behind(old, "0.4.5", "0.4.5", "0.4.5") ?? throw new Exception("0.4.1 with 0.4.5 downloaded was not called old");
            Check(ready.Contains("0.4.1") && ready.Contains("0.4.5") && ready.Contains("is downloaded") && ready.Contains("switches to it by itself"), ready);
            // A download nobody recorded (the state file lost) still counts: the folder is there.
            Check(Updater.Behind(old, null, null, "0.4.5")?.Contains("is downloaded") == true, "a downloaded folder alone was not seen");
            var available = Updater.Behind(old, "0.4.5", null, null) ?? throw new Exception("0.4.1 with 0.4.5 out was not called old");
            Check(available.Contains("0.4.5 is out") && available.Contains("install line"), available);
            // A newer release than the one downloaded: the download is not the answer.
            Check(Updater.Behind(old, "0.4.6", "0.4.5", "0.4.5")?.Contains("0.4.6 is out") == true, "a release newer than the download was not named");
        });

        yield return ("second SAFE run: a test on an old version fails its first check, and the report says so at the top", () =>
        {
            try
            {
                Updater.BehindProbe = () => Updater.Behind(new Version(0, 4, 1), "0.4.5", null, null);
                var steps = SelfTest.Steps(new MemoryEtabs(), changes: false);
                Check(steps[0].Name == "CSI ProMax is the newest version" && !steps[0].Ok && steps[0].Detail.Contains("0.4.5"), "the first check did not fail on the old version: " + steps[0]);
                var folder = Path.Combine(Path.GetTempPath(), "cpm-stale-" + Guid.NewGuid().ToString("N"));
                var outcome = FieldTest.Run(new MemoryEtabs(), changes: false, "a test", folder);
                Check(outcome.Failed > 0, "the field test of an old version passed");
                Check(File.ReadAllText(outcome.File).Contains("- NOT THE NEWEST VERSION: This is CSI ProMax 0.4.1"), "the report does not say at the top that it is from an old version");
                Updater.BehindProbe = () => null;
                var fresh = SelfTest.Steps(new MemoryEtabs(), changes: false);
                Check(fresh[0].Ok, "the newest version failed the check: " + fresh[0]);
            }
            finally { Updater.BehindProbe = null; }
        });

        yield return ("second SAFE run: the chat's tests refuse to run on an old version when the new one is already on the PC", () =>
        {
            try
            {
                Updater.BehindProbe = () => Updater.Behind(new Version(0, 4, 1), "0.4.5", "0.4.5", "0.4.5");
                var app = new CsiProMax.ETABS.EtabsApp(new MemoryEtabs());
                var none = new CsiProMax.Tools.ToolArgs(new Dictionary<string, JsonElement>());
                var field = new CsiProMax.Tools.RunFieldTestTool().Execute(app, none);
                Check(field.IsError && field.Text.Contains("0.4.5 is downloaded"), "run_field_test ran on the old version: " + field.Text);
            }
            finally { Updater.BehindProbe = null; }
        });

        yield return ("update button: at the top only while a newer version is out or downloaded, and it names that version", () =>
        {
            var now = new Version(0, 4, 6);
            Equal<string?>(null, CsiProMax.UI.UpdateBadge.TopButton(now, null, null), "nothing known");
            Equal<string?>(null, CsiProMax.UI.UpdateBadge.TopButton(now, "0.4.6", "0.4.6"), "the newest is running");
            Equal<string?>(null, CsiProMax.UI.UpdateBadge.TopButton(now, "0.4.5", null), "an older release");
            Equal<string?>("Update to 0.4.7", CsiProMax.UI.UpdateBadge.TopButton(now, "0.4.7", null), "a newer release that is not downloaded");
            Equal<string?>("Switch to 0.4.7 now", CsiProMax.UI.UpdateBadge.TopButton(now, "0.4.7", "0.4.7"), "a newer release that is downloaded");
            Equal<string?>("Update to 0.4.8", CsiProMax.UI.UpdateBadge.TopButton(now, "0.4.8", "0.4.7"), "a release newer than the download");
            var window = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Host/MainWindow.cs"));
            Check(window.Contains("UpdateBadge.TopButton(") && window.Contains("CsiProMax.App.SwitchWhenIdle();"), "the window has no update button that installs and switches");
        });

        // ---- Ahmed: "no need to download everything let it be just the updated things only" ----

        yield return ("update: only the changed files are downloaded, the rest comes from this PC, and every file is checked", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cpm-delta-" + Guid.NewGuid().ToString("N"));
            string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
            string old = Dir("old"), next = Dir("new"), updates = Dir("updates"), release = Dir("release");
            try
            {
                var big = new byte[300_000];
                new Random(7).NextBytes(big);
                foreach (var d in new[] { old, next }) { File.WriteAllBytes(Path.Combine(d, "Library.dll"), big); File.WriteAllText(Path.Combine(d, "check.ps1"), "same"); }
                File.WriteAllText(Path.Combine(old, "CsiProMax.dll"), "version 1");
                File.WriteAllText(Path.Combine(next, "CsiProMax.dll"), "version 2");
                File.WriteAllText(Path.Combine(next, "New.dll"), "added in 2");
                // The release's own script writes the file list and the changed zip.
                var script = Path.Combine(SourceText.Root(), "scripts", "changed-files.py");
                string manifest = Path.Combine(release, "files.json"), changed = Path.Combine(release, DeltaUpdate.ChangedName("9.9.9"));
                using (var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3") { ArgumentList = { script, next, old, manifest, changed }, RedirectStandardOutput = true, RedirectStandardError = true })!)
                {
                    run.WaitForExit();
                    Check(run.ExitCode == 0, "scripts/changed-files.py failed: " + run.StandardError.ReadToEnd());
                }
                var files = DeltaUpdate.Read(File.ReadAllText(manifest)) ?? throw new Exception("the file list was not read");
                Equal("CsiProMax.dll, New.dll", string.Join(", ", files.Where(f => f.Changed).Select(f => f.Path).OrderBy(f => f, StringComparer.Ordinal)), "the changed files");
                Check(new FileInfo(changed).Length < 5000, $"the changed zip holds more than what changed ({new FileInfo(changed).Length} bytes)");

                var asked = new List<string>();
                using var http = new HttpClient(new Served(url =>
                {
                    asked.Add(url);
                    return url.EndsWith("/files.json", StringComparison.Ordinal) ? File.ReadAllBytes(manifest) : url.EndsWith("/" + DeltaUpdate.ChangedName("9.9.9"), StringComparison.Ordinal) ? File.ReadAllBytes(changed) : null;
                }));
                var target = Path.Combine(root, "versions", "9.9.9");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Check(DeltaUpdate.TryAsync(http, "o/r", "9.9.9", target, updates, [old], new UpdateProgress(), CancellationToken.None).GetAwaiter().GetResult(), "the update of changed files only did not work");
                Equal("version 2", File.ReadAllText(Path.Combine(target, "CsiProMax.dll")), "the changed file");
                Equal("added in 2", File.ReadAllText(Path.Combine(target, "New.dll")), "the new file");
                Check(File.ReadAllBytes(Path.Combine(target, "Library.dll")).AsSpan().SequenceEqual(big), "the unchanged file was not carried over whole");
                Check(UpdateResume.IsComplete(target), "the new version's folder is not marked complete");
                Equal(2, asked.Count, "downloads (the file list and the changed zip): " + string.Join(", ", asked));

                // A PC that lacks a file the changed zip does not hold: the whole package is taken instead, and nothing is left behind.
                var bare = Dir("bare");
                var second = Path.Combine(root, "versions", "9.9.8");
                Check(!DeltaUpdate.TryAsync(http, "o/r", "9.9.9", second, updates, [bare], new UpdateProgress(), CancellationToken.None).GetAwaiter().GetResult(), "an update was built without the files it needs");
                Check(!Directory.Exists(second) && !Directory.Exists(second + ".delta"), "a half-built version folder was left behind");

                // A file on this PC that is not what the release lists is never trusted: it is downloaded, or the update falls back.
                File.WriteAllText(Path.Combine(old, "Library.dll"), "damaged");
                Check(!DeltaUpdate.TryAsync(http, "o/r", "9.9.9", Path.Combine(root, "versions", "9.9.7"), updates, [old], new UpdateProgress(), CancellationToken.None).GetAwaiter().GetResult(), "a damaged local file was used");

                // A release without the list (0.4.7 and older): the whole package, as before.
                using var none = new HttpClient(new Served(_ => null));
                Check(!DeltaUpdate.TryAsync(none, "o/r", "9.9.9", Path.Combine(root, "versions", "9.9.6"), updates, [old], new UpdateProgress(), CancellationToken.None).GetAwaiter().GetResult(), "a release without a file list was not left to the whole package");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        });

        yield return ("update: no update text tells the user to close or restart the CSI program", () =>
        {
            // Ahmed: "I need all updates not to close the etabs or safe or sap it should be automatic while they're open".
            var files = SourceText.Files("src/CsiProMax.Assistant/Panel/Update").ToList();
            files.AddRange(SourceText.Files("src/CsiProMax.Assistant/Panel/UI").Where(f => Path.GetFileName(f.Path).StartsWith("Update", StringComparison.Ordinal) || f.Path.EndsWith("RestartNote.cs", StringComparison.Ordinal)));
            var bad = new List<string>();
            foreach (var (path, text) in files)
            {
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && Regex.IsMatch(lines[i], @"(?i)(restart|reopen|close and reopen|restart of|close) \{Csi\.Name\}")) bad.Add($"{path}:{i + 1}");
            }
            Check(files.Count > 4, "the update files were not found");
            Check(bad.Count == 0, "these update texts tell the user to restart or reopen the program: " + string.Join(", ", bad));
        });

        yield return ("update: looked for within seconds of opening the window, so close and open updates", () =>
        {
            Check(CsiProMax.App.UpdateDelay <= TimeSpan.FromSeconds(5), $"the first update check waits {CsiProMax.App.UpdateDelay.TotalSeconds:0} s after the window opens");
            var app = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Host/App.cs"));
            Check(app.IndexOf("Task.Delay(UpdateDelay, stop)", StringComparison.Ordinal) is var first and > 0 && first < app.IndexOf("Task.Delay(BackgroundDelay, stop)", StringComparison.Ordinal), "the update check waits for the other background work again");
        });

        yield return ("update: the file list refuses paths outside the package, and the changed zip can never be taken for the whole package", () =>
        {
            foreach (var bad in new[] { "../x.dll", "/x.dll", "a/../../x.dll", "C:/x.dll", "a\\b.dll", "" })
                Check(!DeltaUpdate.Safe(bad), $"'{bad}' was accepted as a path inside the package");
            Check(DeltaUpdate.Safe("CsiProMax.dll") && DeltaUpdate.Safe("runtimes/win-x64/native/a.dll"), "a normal path was refused");
            var sha = new string('a', 64);
            Check(DeltaUpdate.Read($$"""{"files":[{"path":"CsiProMax.dll","sha256":"{{sha}}","size":1,"changed":true},{"path":"../evil.dll","sha256":"{{sha}}","size":1,"changed":true}]}""") is null, "a list with a path outside the package was read");
            Check(DeltaUpdate.Read($$"""{"files":[{"path":"Other.dll","sha256":"{{sha}}","size":1,"changed":true}]}""") is null, "a list without CsiProMax.dll was read");
            Check(DeltaUpdate.Read("not json") is null, "text that is no list was read");
            // Versions up to 0.4.7 take the first release file named CsiProMax-*.zip as the whole package.
            Check(!DeltaUpdate.ChangedName("0.4.8").StartsWith("CsiProMax-", StringComparison.OrdinalIgnoreCase), "an older version would install the changed zip as if it were the whole package");
            var workflow = File.ReadAllText(Path.Combine(SourceText.Root(), ".github/workflows/release.yml"));
            Check(workflow.Contains("\"changed-$NAME.zip\" files.json") && !workflow.Contains("-changed.zip"), "the release does not publish the changed zip and the file list under the names the updater asks for");
        });

        yield return ("second SAFE run: a newer version is started with the same arguments, once, and told what to wait for", () =>
        {
            var info = CsiProMax.Assistant.Program.Start("dotnet", "/v/0.4.5", ["--etabs-pid", "7", "--program", "SAFE", "--this-version", "--installed-in", "/old", "--after-pid", "3", "--fieldtest"], 42);
            var args = info.ArgumentList.ToList();
            Equal(Path.Combine("/v/0.4.5", "CsiProMax.dll"), args[0], "what is started");
            Equal("--etabs-pid 7 --program SAFE --fieldtest --this-version --installed-in", string.Join(' ', args.Skip(1).Take(7)), "the arguments");
            Equal(1, args.Count(a => a == "--this-version"), "--this-version");
            Equal("42", args[^1], "the process to wait for");
            // The tests from the command line go to the newest version before anything else (they used to run the installed one).
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs"));
            Check(source.IndexOf("Newer(args) is { } newerTest", StringComparison.Ordinal) is var at and > 0 && at < source.IndexOf("FieldTest.Run(link", StringComparison.Ordinal), "--fieldtest runs before a newer version is looked for");
        });

        // ---- the first full field test on a real SAP2000 26: run_analysis never came back, "Analysis Complete" waited for OK ----

        yield return ("SAP2000 field test: only the 'analysis complete' notice of the scratch window is closed for it, never a licence or other message", () =>
        {
            Check(ScratchDialogs.MayClose("Analysis Complete - cpm-field-test"), "the notice that stopped the test is not closed");
            Check(ScratchDialogs.MayClose("analysis complete"), "the notice in other letters is not closed");
            foreach (var other in new[] { "Maintenance Expiring", "SAP2000 26.3.0 Ultimate - cpm-field-test", "License", "Save Model File As", "SAFE", "" })
                Check(!ScratchDialogs.MayClose(other), $"'{other}' would be closed by the test");
            // No process: nothing is looked for or closed.
            Equal(0, ScratchDialogs.CloseNotices(0).Count, "notices closed with no process");
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(source.Contains("ScratchDialogs.CloseNotices(ProcessId, _program)"), "the scratch window no longer watches for the notice");
        });

        yield return ("SAP2000 field test: a scratch process left running is named with its process id, and counts as a failure", () =>
        {
            var said = "the scratch SAP2000 window stopped answering, so it was left as it is; close it yourself without saving" + ScratchProgram.LeftRunning("SAP2000", "SAP2000", 47136, hasWindow: false);
            Check(said.Contains("process id 47136") && said.Contains("no window on the screen") && said.Contains("Task Manager") && said.Contains("licence"), said);
            Check(ScratchProgram.LeftRunning("SAFE", "SAFE", 7, hasWindow: true).Contains("close that window without saving, or end"), "a window that is there is not offered to be closed");
            // The close step fails on any answer that asks the engineer to do something.
            Check(said.Contains("yourself"), "a process left running would pass the close step");
        });

        yield return ("SAP2000 field test: the test ends only a process that can be no one's but its own scratch program", () =>
        {
            var asked = new DateTime(2026, 11, 25, 16, 48, 0);
            Check(ScratchProgram.MayEnd(asked.AddSeconds(5), asked, []), "its own process with no window left is not ended");
            Check(ScratchProgram.MayEnd(asked.AddSeconds(5), asked, ["SAP2000 26.3.0 Ultimate - cpm-field-test", "Analysis Complete - cpm-field-test"]), "its own process on the scratch model is not ended");
            Check(!ScratchProgram.MayEnd(asked.AddHours(-3), asked, []), "a process that was running before the test asked would be ended");
            Check(!ScratchProgram.MayEnd(asked.AddSeconds(5), asked, ["SAP2000 26.3.0 Ultimate - Tower B"]), "a window with another model would be ended");
            Check(!ScratchProgram.MayEnd(asked.AddSeconds(5), asked, ["SAP2000 26.3.0 Ultimate - cpm-field-test", "Maintenance Expiring"]), "a process with a message that is not the scratch model's would be ended");
            Check(!ScratchProgram.MayEnd(asked.AddSeconds(5), default, []), "a process would be ended although the test never asked for one");
            // Ending a process is written in one place only, behind that rule.
            var kills = SourceText.Files("src").Where(f => f.Text.Contains(".Kill(", StringComparison.Ordinal)).Select(f => f.Path).ToList();
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(source.Contains("MayEnd(process.StartTime, _startedAt, titles))\n            {\n                process.Kill();".Replace("\n", "\n")) || System.Text.RegularExpressions.Regex.IsMatch(source, @"MayEnd\(process\.StartTime, _startedAt, titles\)\)\s*\{\s*process\.Kill\(\);"), "the scratch process is ended without asking MayEnd first");
            Check(kills.Contains("src/CsiProMax.Assistant/Field/ScratchProgram.cs"), "the rule is not used");
        });

        yield return ("SAP2000 field test: a second full test on the same PC is refused, and each run's scratch model has its own name", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "cpm-lock-" + Guid.NewGuid().ToString("N"), "field-test.lock");
            using (FieldTest.OnlyOne(file))
            {
                try { FieldTest.OnlyOne(file).Dispose(); throw new Exception("a second full field test was let in while one is running"); }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains("already running on this PC"), ex.Message); }
            }
            // The first one ended: the next may start.
            FieldTest.OnlyOne(file).Dispose();
            string a = ScratchProgram.ScratchName(), b = ScratchProgram.ScratchName();
            Check(a != b && a.StartsWith("cpm-field-test-", StringComparison.Ordinal) && a.Length == "cpm-field-test-".Length + 6, $"scratch names: {a}, {b}");
            // The rules that look for the scratch model by name still know it.
            Check(ScratchProgram.MayEnd(DateTime.Now, DateTime.Now, [$"SAP2000 26.3.0 Ultimate - {a}"]) && ScratchDialogs.MayClose($"Analysis Complete - {a}"), "the scratch model's own name is not recognised");
        });

        yield return ("field test: each step's outcome is said as it happens and kept in the work folder, so a run can be followed live", () =>
        {
            // Ahmed: "can we work live whenever something is there in field test you can work on it here until the test finish".
            Equal("FAIL        [SAP2000] tool run_analysis: it never came back", FieldContext.LiveLine(new FieldStep("SAP2000", "tool run_analysis", false, "it never\ncame back")), "a failure's line");
            Equal("ok          [This PC] the install folder", FieldContext.LiveLine(new FieldStep("This PC", "the install folder", true, "C:/x")), "a passed step's line");
            Check(FieldContext.LiveLine(new FieldStep("ETABS", "open", true, "skipped: no licence", null, true)).StartsWith("not tested  [ETABS] open: skipped: no licence", StringComparison.Ordinal), "a step left out");
            var folder = Path.Combine(Path.GetTempPath(), "cpm-live-" + Guid.NewGuid().ToString("N"));
            var said = new List<string>();
            var outcome = FieldTest.Run(new MemoryEtabs(), new FieldTest.Options { StartedFrom = "a test", Folder = folder, WorkFolder = Path.Combine(folder, "work"), Outcome = said.Add });
            Check(said.Count > 10 && said.Count == outcome.Steps.Count, $"{said.Count} outcomes were said for {outcome.Steps.Count} steps");
            Equal(said.Count, File.ReadAllLines(Path.Combine(folder, "work", FieldContext.LiveFile)).Length, "lines in the work folder's live file");
        });

        yield return ("usage line: a limit that reset since the last answer is said in words, never a bare 'Week reset'", () =>
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1795620000);
            var past = now.AddHours(-3).ToUnixTimeSeconds();
            var old = CsiProMax.Chat.PlanUsage.ParseLine("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\",\"rateLimitType\":\"seven_day\",\"resetsAt\":" + past + "}}", now.AddDays(-1))!;
            Equal(CsiProMax.Chat.PlanUsage.AllReset, old.Meter(now, timeLeft: true, narrow: false), "all reset");
            Check(!old.Meter(now).Contains("Week reset"), "the line still reads 'Week reset'");
            var fresh = CsiProMax.Chat.PlanUsage.ParseLine("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\",\"rateLimitType\":\"five_hour\",\"resetsAt\":" + now.AddHours(2).ToUnixTimeSeconds() + ",\"unifiedWindows\":{\"five_hour\":{\"utilization\":0.43,\"resetsAt\":" + now.AddHours(2).ToUnixTimeSeconds() + "},\"seven_day\":{\"utilization\":0.05,\"resetsAt\":" + now.AddDays(3).ToUnixTimeSeconds() + "}}}}", now)!;
            Equal("5h 43%, 2h 0m left · Week 5%", CsiProMax.Chat.PlanUsage.Merge(old, fresh).Meter(now), "fresh figures");
            var bare = CsiProMax.Chat.PlanUsage.ParseLine("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\",\"rateLimitType\":\"seven_day\",\"resetsAt\":" + now.AddDays(3).ToUnixTimeSeconds() + "}}", now)!;
            Equal("Week no figure yet", bare.Meter(now), "no figure");
        });
        yield return ("ETABS 23 field test: the scratch model is known by its name when the program answers with its working file (.$et)", () =>
        {
            Check(ScratchProgram.SameName(@"C:\Users\x\AppData\Local\CsiProMax\Field test\ETABS 1\cpm-field-test-949e09.$et", @"C:/Users/x/AppData/Local/CsiProMax/Field test/ETABS 1/cpm-field-test-949e09.edb"), "the .$et beside the scratch .edb is the scratch model");
            Check(ScratchProgram.SameName("CPM-FIELD-TEST-949e09.EDB", "cpm-field-test-949e09.edb"), "the same name in other letters");
            Check(!ScratchProgram.SameName(@"C:\Models\Tower.edb", @"C:\t\cpm-field-test-949e09.edb"), "another model is not the scratch model");
            Check(!ScratchProgram.SameName(@"C:\t\cpm-field-test-111111.$et", @"C:\t\cpm-field-test-949e09.edb"), "another run's scratch model is not this one's");
            Check(!ScratchProgram.SameName("", @"C:\t\cpm-field-test-949e09.edb"), "no model open is not the scratch model");
        });
        // A tester's PC, 2026-11-26: CSI ProMax gone from the Tools menu of the CSI programs. A program started through
        // its API writes its settings back with an empty plug-in list, and CSI ProMax put itself back only once it was
        // opened, which with the menu entry gone it could no longer be. tests/keep-plugins.Tests.ps1 tests the script itself.
        yield return ("tester PC: CSI ProMax keeps itself in the Tools menus while it is closed, and is taken out on uninstall", () =>
        {
            var root = SourceText.Root();
            var script = File.ReadAllText(Path.Combine(root, "keep-plugins.ps1"));
            Check(MenuKeeper.ScriptText() is { } inside && inside.Replace("\r\n", "\n") == script.Replace("\r\n", "\n"), "the keeper inside CSI ProMax is not the repository's keep-plugins.ps1");
            Check(script.Contains("-AtLogOn") && script.Contains("MultipleInstances IgnoreNew") && script.Contains("GetFolderPath('Startup')"), "the keeper does not start at every sign-in (task, or Startup shortcut where tasks are not allowed)");
            Check(script.Contains("Local\\CsiProMax.KeepPlugins"), "two keepers could run at once");
            var app = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Panel", "Host", "App.cs"));
            Check(app.Contains("Task.Run(CsiProMax.Assistant.MenuKeeper.Ensure)"), "a PC updated by the updater only never gets the keeper");
            Check(File.ReadAllText(Path.Combine(root, "install.ps1")).Contains("-File $keeper -Register -Folder $Folder"), "the install line does not set the keeper up");
            Check(File.ReadAllText(Path.Combine(root, "uninstall.ps1")).Contains("Remove = $true"), "uninstall does not take CSI ProMax out of the Tools menus");
            // Set up once per installed folder; again when the folder changes; never when turned off by hand.
            Check(MenuKeeper.Due(null, @"C:\Users\m\Documents\CSI ProMax", false), "a PC with no keeper gets one");
            Check(!MenuKeeper.Due(@"task|C:\Users\m\Documents\CSI ProMax", @"c:\users\m\documents\csi promax\", false), "the keeper is set up again at every start");
            Check(MenuKeeper.Due(@"task|D:\Old\CSI ProMax", @"C:\Users\m\Documents\CSI ProMax", false), "a keeper for another folder is not moved");
            Check(!MenuKeeper.Due(null, @"C:\Users\m\Documents\CSI ProMax", true), "keep-plugins-off.txt is not respected");
            // Every place that starts a CSI program through its API keeps the plug-in lists as they were.
            var sap = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Sap2000", "SapScratchTest.cs"));
            Check(sap.Contains("KeptPlugIns.Take(program)") && sap.Contains("KeptPlugIns.PutBack(plugIns)"), "the SAP2000 PC check empties SAP2000's plug-in list");
            foreach (var file in SourceText.Files("src/CsiProMax.Assistant").Where(f => f.Text.Contains(".ApplicationStart()")))
                Check(file.Text.Contains("KeptPlugIns."), file.Path + " starts a CSI program through its API without keeping its plug-in lists");
        });
        yield return ("SAP2000 field test: a process left behind with no window does not keep CSI ProMax out of the Tools menu", () =>
        {
            var now = new DateTime(2026, 11, 25, 18, 40, 0);
            Check(ToolsMenu.LeftBehind(false, now.AddHours(-8), now), "a windowless process from the morning is not the program being open");
            Check(!ToolsMenu.LeftBehind(true, now.AddHours(-8), now), "a program with a window is open");
            Check(!ToolsMenu.LeftBehind(false, now.AddSeconds(-20), now), "a program that is starting (no window yet) is open");
            Check(!ToolsMenu.LeftBehind(null, now.AddHours(-8), now) && !ToolsMenu.LeftBehind(false, null, now), "a process that cannot be read counts as open");
            var install = File.ReadAllText(Path.Combine(SourceText.Root(), "install.ps1"));
            Check(install.Contains("($_.MainWindowHandle -ne 0 -and $_.MainWindowTitle)") && install.Contains("TotalMinutes -le 10"), "install.ps1 follows the same rule");
            // The three left on Ahmed's PC had a main window after all: SAP2000's hidden helper, with no title and no size.
            Check(!ToolsMenu.HasWindow(true, "") && !ToolsMenu.HasWindow(true, null) && !ToolsMenu.HasWindow(false, "SAP2000"), "a hidden helper window with no title is taken for the program's window");
            Check(ToolsMenu.HasWindow(true, "SAP2000 26.3.0 Ultimate - (Untitled)"), "a titled main window is the program being open");
        });
        yield return ("ETABS 23 field test: the test never makes ETABS ask to close, and answers that question itself", () =>
        {
            // Seen on Ahmed's PC on 2026-11-25: "Program is active. Do you really want to close the program?" (Yes / No)
            // in the scratch ETABS during its analysis, waiting for a person.
            // The notice is answered by its own OK button, never by closing the window: closing it is what made ETABS ask,
            // and what took SAP2000's analysis window away in the second full run of 0.4.9.
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            var notices = scratch[scratch.IndexOf("public static List<string> CloseNotices", StringComparison.Ordinal)..scratch.IndexOf("public static bool IsButton", StringComparison.Ordinal)];
            Check(!notices.Contains("0x0010", StringComparison.Ordinal) && notices.Contains("BM_CLICK", StringComparison.Ordinal) && notices.Contains("IsWindowEnabled(ok)", StringComparison.Ordinal), "the notice is answered by its enabled OK button and its window is never closed");
            Check(ScratchDialogs.IsButton("&OK", "OK") && ScratchDialogs.IsButton(" ok ", "OK") && !ScratchDialogs.IsButton("Cancel", "OK") && !ScratchDialogs.IsButton("OK to all", "OK"), "the OK button is known by its label");
            Check(ScratchDialogs.IsCloseQuestion(["&Yes", "&No", "Program is active. Do you really want to close the program?"]), "the close question is known by its words");
            foreach (var other in new[] { "License Message", "Your license will expire in 5 days.", "Do you want to save changes to the model?", "Analysis Complete", "" })
                Check(!ScratchDialogs.IsCloseQuestion(["OK", other]), $"'{other}' is taken for the close question");
            Equal(7, ScratchDialogs.CloseAnswer(closing: false), "while the test works in the window the answer is No");
            Equal(6, ScratchDialogs.CloseAnswer(closing: true), "while the test closes its own window the answer is Yes");
            Equal(0, ScratchDialogs.AnswerCloseQuestion(0, yes: false), "nothing is answered with no process");
        });
        yield return ("ETABS 23 field test: the story picker of the slab design is answered by the test (all stories, OK), nothing else is", () =>
        {
            // Seen on 2026-11-25 with 0.4.10: run_design waited ten minutes on "Select Stories for Slab Design".
            Check(ScratchDialogs.IsStoryPicker("Select Stories for Slab Design"), "the story picker is not known");
            foreach (var other in new[] { "Select Stories", "License", "Maintenance Expiring", "Save Model File As", "ETABS", "" })
                Check(!ScratchDialogs.IsStoryPicker(other) , $"'{other}' is taken for the story picker");
            Check(!ScratchDialogs.MayClose("Select Stories for Slab Design"), "the story picker is not an 'analysis complete' notice");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            var notices = scratch[scratch.IndexOf("public static List<string> CloseNotices", StringComparison.Ordinal)..scratch.IndexOf("public static bool IsStoryPicker", StringComparison.Ordinal)];
            Check(notices.Contains("IsStoryPicker(title)", StringComparison.Ordinal) && notices.IndexOf("\"Select All\"", StringComparison.Ordinal) is var all and > 0 && all < notices.LastIndexOf("PostMessageW(go", StringComparison.Ordinal) && !notices.Contains("0x0010", StringComparison.Ordinal), "all stories are picked before OK, and the window is never closed");
        });
        yield return ("SAP2000 26 field test: the import log of the scratch .s2k is answered by the test (its errors read, Done), nothing else is", () =>
        {
            // Seen on Ahmed's PC on 2026-11-26: the 0.4.36 run opened cpm-field-test.s2k, SAP2000 showed
            // "Access Database Import Log" (1 error, 13 warnings, Done) and the open waited all night for a person.
            Check(ScratchDialogs.IsImportLog("Access Database Import Log") && ScratchDialogs.IsImportLog(" Access Database Import Log "), "the import log is not known");
            foreach (var other in new[] { "License Message", "Licence Import Log", "Analysis Complete", "Select Stories for Slab Design", "Import", "SAP2000 26.3.0 Ultimate - (Untitled)", "" })
                Check(!ScratchDialogs.IsImportLog(other), $"'{other}' is taken for the import log");
            var errors = ScratchDialogs.ImportErrors(["Number of errors = 1\nNumber of warnings = 13", "Error\n1 errors\nImport Control for Errors", "ERROR: Frame section \"FSEC1\" not found, line 212\nWARNING: default material used"]);
            Equal(1, errors.Count, "error lines of the log: " + string.Join(" | ", errors));
            Check(errors[0].Contains("FSEC1", StringComparison.Ordinal), "the error line is not kept whole");
            Check(SandboxedLeftCopy(), "a left copy showing only its import log is not known as the test's own");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            var notices = scratch[scratch.IndexOf("public static List<string> CloseNotices", StringComparison.Ordinal)..scratch.IndexOf("public static bool IsStoryPicker", StringComparison.Ordinal)];
            var log = notices[notices.IndexOf("IsImportLog(title)", StringComparison.Ordinal)..];
            Check(log.Contains("Button(window, \"Done\")", StringComparison.Ordinal) && log.Contains("IsWindowEnabled(done)", StringComparison.Ordinal) && log.Contains("PostMessageW(done, 0x00F5 /* BM_CLICK */", StringComparison.Ordinal)
                && log.IndexOf("ImportErrors(", StringComparison.Ordinal) < log.IndexOf("PostMessageW(done", StringComparison.Ordinal) && !notices.Contains("0x0010", StringComparison.Ordinal), "the log is answered by its Done button after its errors are read, and never closed");
            static bool SandboxedLeftCopy() => ScratchProgram.IsLeftCopy(["SAP2000 - cpm-field-test-ab12cd", "Access Database Import Log"]);
        });
        yield return ("field test: a file error says where it came from, and the team library's file calls try again for a moment (0.4.40: 'Access to the path is denied.')", () =>
        {
            // Seen on Ahmed's PC on 2026-11-26: the team library's check failed with only "Access to the path is denied."
            string where = "";
            try { CsiProMax.Tools.TeamSync.Retry(() => throw new ArgumentException("not a file error")); }
            catch (ArgumentException) { where = "other errors are not tried again"; }
            Equal("other errors are not tried again", where, "an error that is not a file error");
            var tries = 0;
            CsiProMax.Tools.TeamSync.Retry(() => { if (++tries < 4) throw new UnauthorizedAccessException("Access to the path is denied."); });
            Equal(4, tries, "a denied file call is tried again until it works");
            try { DeniedHere(); }
            catch (UnauthorizedAccessException ex) { where = FieldContext.Where(ex); }
            Check(where.Contains("DeniedHere", StringComparison.Ordinal), "the step's detail names where the denial came from: " + where);
            Equal("", FieldContext.Where(new InvalidOperationException("x")), "other errors keep their message alone");
            static void DeniedHere() => throw new UnauthorizedAccessException("Access to the path is denied.");
        });
        yield return ("ETABS 23 field test: a check whose table has no rows for the scratch model is not tested; a table that cannot be read still fails", () =>
        {
            // Seen on 2026-11-25 with 0.4.11: "1 of 5 checks had nothing to read. check_story_irregularity: no reason given".
            JsonNode Answer(string json) => JsonNode.Parse(json)!;
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            FieldParts.Verdicts(ctx, "checks", [("check_drift", Answer("""{"status":"pass"}""")), ("check_story_irregularity", Answer("""{"status":"not available","no_data":true,"note":"ETABS's 'Story Stiffness' table has no data"}"""))]);
            var step = ctx.Steps.Last();
            Check(step.Skipped && step.Detail.Contains("Story Stiffness", StringComparison.Ordinal), $"no data is not 'not tested' with its reason: {step.Ok} {step.Skipped} {step.Detail}");
            ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            FieldParts.Verdicts(ctx, "checks", [("check_drift", Answer("""{"status":"not available","note":"The 'Story Drifts' table has no 'Drift' field in this version."}"""))]);
            step = ctx.Steps.Last();
            Check(!step.Ok && !step.Skipped, "a table that cannot be read no longer fails");
            // What run_design says of a design it could not start is the reason given for the empty pier tables.
            var designed = Answer("""{"ran":["concrete"],"not_designed":[],"run_in_program":[{"kind":"shear_wall","menu":"Design > Shear Wall Design","why":"ETABS's API has no call to start this design"}]}""");
            Equal("ETABS's API has no call to start this design", FieldParts.WhyNot(designed, "shear_wall"), "why the shear wall design did not run");
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/ResultTools.cs"));
            Check(source.Contains("result[\"note\"] = string.Join(\" \", parts.Select", StringComparison.Ordinal), "check_story_irregularity may say 'not available' with no reason");
        });
        yield return ("ETABS 23 field test: the Viewer is checked while the model still has its results, once", () =>
        {
            // Seen on 2026-11-25 with 0.4.11: every result button of the Viewer was greyed out for ETABS, because the
            // ETABS steps unlock the model at their end and the Viewer's check came after them.
            var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/EtabsFieldSteps.cs"));
            Check(steps.IndexOf("ctx.WithResults()", StringComparison.Ordinal) is var at and > 0 && at < steps.IndexOf("m.SetModelIsLocked(false)", StringComparison.Ordinal), "the Viewer's check does not come before the model is unlocked");
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            var runs = 0;
            ctx.WhileAnalysed = () => runs++;
            ctx.WithResults(); ctx.WithResults();
            Equal(1, runs, "times the Viewer's check ran");
        });
        yield return ("SAP2000 26 field test: the shared steps check the Viewer before they unlock the analysed model", () =>
        {
            // Seen on 2026-11-25 with 0.4.20: the shared steps now unlock the model at their start (a locked SAP2000
            // refused a pattern added by hand), and SAP2000's Viewer check came after them: 10 buttons greyed out and
            // "the pack has no result tables".
            var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/CsiFieldSteps.cs"));
            Check(steps.IndexOf("ctx.WithResults()", StringComparison.Ordinal) is var at and > 0 && at < steps.IndexOf("\"unlock_model\"", StringComparison.Ordinal), "the shared steps unlock the model before the Viewer is checked");
        });
        yield return ("field test: a program on .NET 8 has its plug-in created in the test itself when this PC has no PowerShell for it", () =>
        {
            // Seen on 2026-11-25, 0.4.21 on Ahmed's PC: 5 steps "can create the CSI ProMax plug-in" were not tested,
            // because Windows PowerShell runs on the .NET Framework and ETABS 23 and SAP2000 26 run on .NET 8.
            var root = SourceText.Root();
            var plugIn = Path.Combine(root, "src/CsiProMax.Plugin/bin/Release/netstandard2.0/CsiProMaxPlugin.dll");
            var api = Path.Combine(AppContext.BaseDirectory, "ETABSv1.dll");
            if (!File.Exists(plugIn)) plugIn = Path.Combine(root, "src/CsiProMax.Plugin/bin/Debug/netstandard2.0/CsiProMaxPlugin.dll");
            Check(File.Exists(plugIn) && File.Exists(api), $"the built plug-in and API file are there: {plugIn}, {api}");
            var (ok, detail) = PlugInCheck.TryHere(plugIn, api);
            Check(ok && detail.Contains("cSapModel", StringComparison.Ordinal), "the plug-in is created against the API file: " + detail);
            var (bad, why) = PlugInCheck.TryHere(api, api);
            Check(!bad && why.Contains("cPlugin", StringComparison.Ordinal), "a file with no plug-in class in it is refused with the reason: " + why);
            using (File.Open(plugIn, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // not kept open by the try

            var read = PlugInCheck.Read($$"""{"Text":"x","Programs":[{"Program":"ETABS 23","Listed":true,"Loads":null,"Runtime":".NET","Reasons":[],"Path":{{System.Text.Json.JsonSerializer.Serialize(plugIn)}},"ApiFile":{{System.Text.Json.JsonSerializer.Serialize(api)}}},{"Program":"ETABS 20","Listed":true,"Loads":null,"Runtime":".NET Framework","Reasons":[],"Path":"","ApiFile":""}]}""");
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            PcFieldSteps.PlugIn(ctx, read);
            var steps = ctx.Steps.Where(s => s.Name.Contains("can create", StringComparison.Ordinal)).ToList();
            Check(steps.Count == 2 && steps[0] is { Ok: true, Skipped: false } && steps[1].Skipped, "ETABS 23 is tried here and ETABS 20 stays not tested: " + string.Join("; ", steps.Select(s => $"{s.Name}: {s.Ok} {s.Skipped} {s.Detail}")));
            // Seen with 0.4.28: ETABS 20, ETABS 21 and SAP2000 24 said "could not be tried ... on .NET Framework" when the real reason was that their API file was not on the PC.
            Check(steps[1].Detail.Contains("API file was not found", StringComparison.Ordinal), "a program whose API file is missing does not say so: " + steps[1].Detail);
            Check(File.ReadAllText(Path.Combine(root, "check.ps1")).Contains("$finding.ApiFile = $candidate", StringComparison.Ordinal), "check.ps1 no longer says where the program's API file is");
        });
        yield return ("field test: the real window is opened with the AI chat and without it", () =>
        {
            // Asked on 2026-11-25: "are we testing for every single option we have I mean the modelling and viewing
            // without AI". The window itself was only opened on release builds, never by the test on a PC.
            Check(WindowFieldSteps.Arguments("x.dll", ai: false).Contains("--no-ai") && !WindowFieldSteps.Arguments("x.dll", ai: true).Contains("--no-ai"), "only the start without the chat passes --no-ai");
            Check(WindowFieldSteps.Arguments("x.dll", ai: true).Contains("--demo") && WindowFieldSteps.Arguments("x.dll", ai: true).Contains("--no-update"), "the second start is on the demo model and does not update itself away");
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            var asked = new List<bool>();
            WindowFieldSteps.Run(ctx, ai => { asked.Add(ai); return ai ? new WindowFieldSteps.Seen(true, "'CSI ProMax' opened") : new WindowFieldSteps.Seen(false, "it ended by itself with code 1"); });
            Equal("True, False", string.Join(", ", asked), "both ways are started");
            Check(ctx.Steps.Count == 2 && ctx.Steps[0].Ok && !ctx.Steps[1].Ok && ctx.Steps[1].Name.Contains("without the AI chat", StringComparison.Ordinal), "a window that does not open without the chat is a failure of its own");
            var busy = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            WindowFieldSteps.Run(busy, _ => new WindowFieldSteps.Seen(false, "a demo window is already open", NotTried: true));
            Check(busy.Steps.All(s => s.Skipped), "a start that could not be tried is not tested, not a failure");
            var notes = Path.Combine(Path.GetTempPath(), "cpm-notes-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            try
            {
                File.WriteAllText(notes, "old\n");
                var before = new FileInfo(notes).Length;
                Equal("", WindowFieldSteps.Wrote(notes, before), "nothing new in errors.txt");
                File.AppendAllText(notes, "[window] boom\n");
                Check(WindowFieldSteps.Wrote(notes, before).Contains("boom", StringComparison.Ordinal) && !WindowFieldSteps.Wrote(notes, before).Contains("old", StringComparison.Ordinal), "only what the new window wrote is reported");
            }
            finally { File.Delete(notes); }
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(source.Contains("WindowFieldSteps.Run(ctx, o.OpenWindow)", StringComparison.Ordinal), "the full test no longer opens the window");
        });
        yield return ("edit_table says that it saved the model before the edit", () =>
        {
            // 0.4.26 saves the model before every table edit (SAP2000 26 empties its load patterns on a refused edit).
            // A save nobody asked for must be said: in the tool's description and in every answer.
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/CsiTools.cs"));
            Check(source.Contains("log[\"saved_before_edit\"]", StringComparison.Ordinal), "the answer of an applied edit does not say the model was saved");
            var tool = ProgramTools.Chat(CsiProgram.Sap2000).First(x => x.Name == "edit_table");
            Check(tool.Description.Contains("first saves the model", StringComparison.Ordinal), "edit_table's description does not say that a change saves the model");
        });
        yield return ("edit_table asks before it saves the model, each time and at every approval level", () =>
        {
            // Ahmed, 2026-11-25, on whether a table edit may save his model first: "Ask me before each save".
            Dictionary<string, System.Text.Json.JsonElement> Args(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
            foreach (var action in new[] { "add", "update", "delete" })
                foreach (var level in Enum.GetValues<CsiProMax.Tools.ApprovalLevel>())
                {
                    var asked = CsiProMax.Tools.ApprovalPolicy.Decide("edit_table", true, false, Args($$"""{"table":"Load Pattern Definitions","action":"{{action}}"}"""), true, new CsiProMax.Tools.ApprovalFacts(),
                        level, new List<string> { "edit_table" }, true, _ => CsiProMax.Tools.PathState.Missing, []);
                    Check(asked.Ask && !asked.CanSilence, $"{action} at {level} is not asked, or can be silenced");
                    Check(asked.Reason.Contains("saves the model", StringComparison.Ordinal) && asked.Reason.Contains("Load Pattern Definitions", StringComparison.Ordinal) && asked.Reason.Contains("If you say no", StringComparison.Ordinal), "the question does not say what is saved and what no means: " + asked.Reason);
                }
            foreach (var action in new[] { "fields", "read" })
                Check(CsiProMax.Tools.OutputPaths.AlwaysAskReason("edit_table", Args($$"""{"table":"Load Pattern Definitions","action":"{{action}}"}""")) is null, $"reading a table ({action}) asks about a save it does not make");
            Check(ProgramTools.Chat(CsiProgram.Etabs).First(x => x.Name == "edit_table").Description.Contains("always asked before that save", StringComparison.Ordinal), "edit_table's description does not say the engineer is asked");
        });
        yield return ("set_lateral_load in SAP2000 asks before it saves the model, each time, for a code its API has no call for (PR 137 review)", () =>
        {
            Dictionary<string, System.Text.Json.JsonElement> Args(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
            var was = CsiProMax.Csi.Program;
            try
            {
                CsiProMax.Csi.Program = CsiProgram.Sap2000;
                foreach (var json in new[] { """{"name":"EQ","kind":"seismic","code":"ASCE 7-22"}""", """{"name":"EQ","code":"KDS 41 17 00-2019"}""", """{"name":"W","kind":"wind","code":"AASHTO 2020"}""" })
                    foreach (var level in Enum.GetValues<CsiProMax.Tools.ApprovalLevel>())
                    {
                        var asked = CsiProMax.Tools.ApprovalPolicy.Decide("set_lateral_load", true, false, Args(json), true, new CsiProMax.Tools.ApprovalFacts(),
                            level, new List<string> { "set_lateral_load" }, true, _ => CsiProMax.Tools.PathState.Missing, []);
                        Check(asked.Ask && !asked.CanSilence, $"{json} at {level} is not asked, or can be silenced");
                        Check(asked.Reason.Contains("saves the model", StringComparison.Ordinal) && asked.Reason.Contains("If you say no", StringComparison.Ordinal), "the question does not say what is saved: " + asked.Reason);
                    }
                // The direct Set calls and None save nothing: not asked about for a save.
                foreach (var json in new[] { """{"name":"EQ","code":"IBC2006"}""", """{"name":"EQ","code":"None"}""", """{"name":"W","kind":"wind","code":"ASCE 7-16"}""" })
                    Check(!CsiProMax.Tools.OutputPaths.SavesBeforeLateral("set_lateral_load", Args(json)), $"{json} is asked about a save it does not make");
                CsiProMax.Csi.Program = CsiProgram.Etabs;
                Check(!CsiProMax.Tools.OutputPaths.SavesBeforeLateral("set_lateral_load", Args("""{"name":"EQ","code":"ASCE 7-22"}""")), "ETABS is asked about SAP2000's save");
            }
            finally { CsiProMax.Csi.Program = was; }
        });
        yield return ("0.4.33 run: the pack takes SAP2000's own names for springs, hinges, functions, cases and section cuts (13 Viewer items were greyed out)", () =>
        {
            foreach (var table in new[] { "Joint Spring Assignments 1 - Uncoupled", "Frame Hinge Assigns 04 - Auto FEMA 356 - Concrete Beam", "Function - Response Spectrum - User", "Function - Time History - User",
                         "Case - Modal History 1 - General", "Case - Static 2 - Nonlinear Load Application", "Section Cuts 1 - General", "Section Cut Definitions", "Joint Assignments - Springs" })
                Check(CsiProMax.Assistant.Tools.PackTables.Kind(new CsiProMax.Assistant.Etabs.TableName(table, table, false, 1)) == "input", $"'{table}' is not taken by the pack as an input");
            foreach (var table in new[] { "Section Cut Forces - Analysis", "Element Forces - Links", "Element Deformations - Links" })
                Check(CsiProMax.Assistant.Tools.PackTables.Kind(new CsiProMax.Assistant.Etabs.TableName(table, table, false, 0)) == "result", $"'{table}' is not taken by the pack as a result");
            // The scratch model's extras are one honest line on a stand-in, and every name they use is fixed.
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchExtras.cs"));
            foreach (var call in new[] { "PointObj.SetSpring", "PropLink.SetLinear", "LinkObj.AddByPoint", "SectCut.SetByGroup", "LoadCases.StaticNonlinear.SetLoadApplication", "Func.FuncTH.SetUser", "LoadCases.ModHistLinear.SetTimeStep" })
                Check(source.Contains('"' + call + '"'), $"the scratch extras no longer call {call}");
            foreach (var steps in new[] { "Field/EtabsFieldSteps.cs", "Sap2000/SapFieldSteps.cs" })
            {
                var text = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant", steps));
                Check(text.IndexOf("ScratchExtras.Add(ctx)", StringComparison.Ordinal) is var add and >= 0 && text.IndexOf("\"run_analysis\"", StringComparison.Ordinal) > add, $"{steps} must add the extras before its first analysis");
                Check(text.Contains("ScratchExtras.Check(ctx)"), $"{steps} must check the extras' tables after the analysis");
            }
        });
        yield return ("ETABS 23 field test: a test copy an earlier run left open is closed by the next run, and nothing else is", () =>
        {
            // Seen on 2026-11-25: the 0.4.10 run gave up on ETABS with the story picker showing; the 0.4.14 run then failed
            // "Tools > CSI ProMax in ETABS 23: ... ETABS 23 is open" because of that leftover.
            Check(ScratchProgram.IsLeftCopy(["ETABS Plus v23.2.0 - cpm-field-test-400277", "Select Stories for Slab Design"]), "the leftover with the story picker is not known");
            Check(ScratchProgram.IsLeftCopy(["SAP2000 26.3.0 Ultimate - cpm-field-test-1ff9a4", "", "Analysis Complete - cpm-field-test-1ff9a4"]), "a leftover with the analysis notice is not known");
            Check(!ScratchProgram.IsLeftCopy(["ETABS Plus v23.2.0 - Tower A"]), "an engineer's own model would be closed");
            Check(!ScratchProgram.IsLeftCopy(["ETABS Plus v23.2.0 - cpm-field-test-400277", "License"]), "a copy showing a licence message would be closed");
            Check(!ScratchProgram.IsLeftCopy([]) && !ScratchProgram.IsLeftCopy([""]) && !ScratchProgram.IsLeftCopy(["Select Stories for Slab Design"]), "a process with no scratch model would be closed");
            Check(!ScratchProgram.IsLeftCopy(["ETABS Plus v23.2.0 - cpm-field-test"]), "the Modeler's own model name is taken for a scratch copy");
            var runner = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(runner.IndexOf("o.EndLeftCopies()", StringComparison.Ordinal) is var at and > 0 && at < runner.IndexOf("menus = ToolsMenu.Repair(", StringComparison.Ordinal), "leftovers are not closed before the Tools menu is looked at");
            Check(runner.Contains("Tools > CSI ProMax is still in each program after the test", StringComparison.Ordinal), "the Tools menu is not looked at again after the scratch windows closed");
        });
        yield return ("Tools menu: an entry that names a copy outside the installed folder is made to name the installed one", () =>
        {
            // Seen on 2026-11-25 after the 0.4.15 run: both settings files named ...\etabs-promax-test\v0415\app\dist, so
            // Tools > CSI ProMax would have started the test copy and not the one in Documents\CSI ProMax.
            const string test = @"C:\Users\a\etabs-promax-test\v0415\app\dist\CsiProMaxPlugin.dll", installed = @"C:\Users\a\Documents\CSI ProMax\CsiProMaxPlugin.dll";
            var ini = "[PlugIn]\r\n NumberPlugIns=1\r\n PlugInName=CsiProMaxPlugin\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath=" + test + "\r\n[Other]\r\n A=1\r\n";
            var (state, text) = ToolsMenu.Edit(ini, "CsiProMaxPlugin", installed, _ => true, prefer: true);
            Equal(ToolsMenu.State.Moved, state, "the test copy's entry, seen from the installed folder");
            Check(text!.Contains("PlugInPath=" + installed, StringComparison.Ordinal) && !text.Contains("etabs-promax-test", StringComparison.Ordinal) && text.Contains("NumberPlugIns=1", StringComparison.Ordinal) && text.Contains("[Other]\r\n A=1", StringComparison.Ordinal), text);
            // Without that preference (CSI ProMax runs from a folder of its own choosing) a copy that is there is left alone, as before.
            Equal(ToolsMenu.State.Already, ToolsMenu.Edit(ini, "CsiProMaxPlugin", installed, _ => true).State, "no preference: left alone");
            Equal(ToolsMenu.State.Already, ToolsMenu.Edit(ini.Replace(test, installed), "CsiProMaxPlugin", installed, _ => true, prefer: true).State, "already the installed one");
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/ToolsMenu.cs"));
            Check(source.Contains("new[] { DefaultInstall(), InstalledIn, AppContext.BaseDirectory }", StringComparison.Ordinal), "Documents\\CSI ProMax no longer comes before the folder CSI ProMax runs from");
        });
        yield return ("field test, only what changed or failed: a part that passed and is unchanged is not run again; a failed, new or changed one is", () =>
        {
            // Ahmed, 2026-11-25: "ignore for testing things that passed and just check the new things in addition to the things failed or not tested".
            var passed = new FieldRetest.Part("0.4.16", "aaaa", 140, 0, [("ETABS table Element Forces - Braces", "ETABS does not list this table")]);
            Check(!FieldRetest.Decide(passed, "aaaa").Run && FieldRetest.Decide(passed, "aaaa").Why.Contains("0.4.16", StringComparison.Ordinal), "a part that passed and is unchanged would run again");
            Check(FieldRetest.Decide(passed, "bbbb").Run, "a part whose code changed is left out");
            Check(FieldRetest.Decide(passed with { Failed = 1 }, "aaaa").Run, "a part with a failed step is left out");
            Check(FieldRetest.Decide(null, "aaaa").Run && FieldRetest.Decide(passed, "").Run && FieldRetest.Decide(passed with { Print = "" }, "aaaa").Run, "a part never run, or a build with no source hashes, is left out");
            // The code of a part: its own files and the shared tools, not another program's.
            var files = new Dictionary<string, string> { ["Etabs/ComEtabs.cs"] = "1", ["Tools/ResultTools.cs"] = "2", ["Sap2000/SapTools.cs"] = "3", ["Safe/SafeTools.cs"] = "4", ["Panel/Chat/ChatPane.cs"] = "5", ["FieldTest.cs"] = "6" };
            string Print(string part, string file, string hash) => FieldRetest.Print(part, new Dictionary<string, string>(files) { [file] = hash });
            Check(Print("sap2000", "Sap2000/SapTools.cs", "x") != FieldRetest.Print("sap2000", files), "a change in SAP2000's tools does not change SAP2000's part");
            Check(Print("etabs", "Sap2000/SapTools.cs", "x") == FieldRetest.Print("etabs", files) && Print(FieldRetest.Panel, "Sap2000/SapTools.cs", "x") == FieldRetest.Print(FieldRetest.Panel, files), "a change in SAP2000's tools changes another part");
            Check(new[] { "etabs", "sap2000", "safe" }.All(part => Print(part, "Tools/ResultTools.cs", "x") != FieldRetest.Print(part, files)), "a change in a shared tool does not change every program's part");
            Check(Print(FieldRetest.Panel, "Panel/Chat/ChatPane.cs", "x") != FieldRetest.Print(FieldRetest.Panel, files) && Print("etabs", "FieldTest.cs", "x") == FieldRetest.Print("etabs", files), "the panel's part, or the runner itself, is not told apart");
            Check(FieldRetest.Files().Count > 100 && FieldRetest.Print("etabs").Length == 16 && FieldRetest.Print(FieldRetest.Panel).Length == 16, $"this build carries no source hashes ({FieldRetest.Files().Count} files)");
            // Kept on the PC and read back; a part that did not run keeps what it had.
            var folder = Path.Combine(Path.GetTempPath(), "cpm-retest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var file = Path.Combine(folder, "last-results.json");
                FieldRetest.Save(new Dictionary<string, FieldRetest.Part> { ["etabs"] = passed, ["sap2000"] = passed with { Failed = 2 } }, file);
                FieldRetest.Save(new Dictionary<string, FieldRetest.Part> { ["sap2000"] = passed with { Version = "0.4.17" } }, file);
                var back = FieldRetest.Load(file);
                Equal("0.4.16|aaaa|140|0|1", $"{back["etabs"].Version}|{back["etabs"].Print}|{back["etabs"].Passed}|{back["etabs"].Failed}|{back["etabs"].NotTested.Count}", "ETABS's part read back");
                Equal("0.4.17|0", $"{back["sap2000"].Version}|{back["sap2000"].Failed}", "SAP2000's part after its next run");
                File.WriteAllText(file, "not json");
                Equal(0, FieldRetest.Load(file).Count, "parts read from a file that is not understood");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            // In the report: one line that says it was not run again, and what it could not test, with its reason.
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            FieldRetest.LeftOut(ctx, "ETABS on a scratch model", passed, "all 140 steps of it passed in version 0.4.16 and its code is unchanged since");
            var steps = ctx.Steps.ToList();
            Check(steps.Count == 2 && steps[0].Ok && !steps[0].Skipped && steps[0].Name.Contains("not run again", StringComparison.Ordinal) && steps[1].Skipped && steps[1].Detail.Contains("as in the run of version 0.4.16", StringComparison.Ordinal), string.Join(" | ", steps.Select(s => $"{s.Name}: {s.Detail}")));
        });
        yield return ("build check: the window that is checked does not update itself away", () =>
        {
            // Seen on 2026-11-25: a pull request's windows job failed with "it is still running twelve seconds later"
            // because a newer release existed and the checked window switched to it.
            var check = File.ReadAllText(Path.Combine(SourceText.Root(), "tests/window.Tests.ps1"));
            Check(check.Contains("'--no-update'", StringComparison.Ordinal), "the window check starts a window that may update itself");
            var app = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Host/App.cs"));
            Check(app.Contains("if (NoUpdate && !interactive) return;", StringComparison.Ordinal) && app.Contains("if (NoUpdate || SwitchTo is null", StringComparison.Ordinal), "--no-update does not stop the look for a version and the switch to it");
            Check(File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs")).Contains("args.Contains(\"--no-update\")", StringComparison.Ordinal), "--no-update is not read");
        });
        yield return ("ETABS 23 field test: delete_joints deletes a loose joint in ETABS and SAFE too", () =>
        {
            // Seen on 2026-11-25 with 0.4.17: "a test joint is still in the model". The ETABS link knew frames and areas only.
            var link = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/ComEtabs.cs"));
            Check(link.Contains("\"point\" => m.PointObj.DeleteSpecialPoint(name, 0)", StringComparison.Ordinal), "the ETABS link cannot delete a joint");
            // Same run: "should have the scratch model open again after the checkpoint; it has ...cpm-field-test-xxxxxx.$et".
            var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/CsiFieldSteps.cs"));
            Check(!steps.Contains("Path.GetFileName(link.Info().File), Path.GetFileName(scratchFile)", StringComparison.Ordinal) && steps.Contains("ScratchProgram.SameName(link.Info().File, scratchFile)", StringComparison.Ordinal), "the checkpoint steps compare the open model's name with its file type");
            Check(ScratchProgram.SameName(@"C:\t\cpm-field-test-1ff9a4.$et", @"C:\t\cpm-field-test-1ff9a4.edb"), "ETABS's working file is not taken for the scratch model");
        });
        yield return ("ETABS 20 tester: the field test tries what Tools > CSI ProMax does in each program, and fails where the plug-in cannot be created", () =>
        {
            // What the check answered on that PC (in its own words), and a PC where all is well.
            var broken = PlugInCheck.Read("""{"Text":"CSI ProMax check ...","Programs":{"Program":"ETABS 20","Listed":true,"Loads":false,"Runtime":".NET Framework","Reasons":"This CSI ProMax plug-in names cPluginContract, which the API file of ETABS 20 does not have."}}""");
            Equal("ETABS 20|True|False|1", $"{broken.Programs[0].Program}|{broken.Programs[0].Listed}|{broken.Programs[0].Loads}|{broken.Programs[0].Reasons.Count}", "a list of one, as PowerShell writes it");
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            PcFieldSteps.PlugIn(ctx, broken);
            var failed = ctx.Steps.First();
            Check(!failed.Ok && failed.Name.StartsWith("ETABS 20 can create", StringComparison.Ordinal) && failed.Detail.Contains("names cPluginContract", StringComparison.Ordinal), $"{failed.Name}: {failed.Detail}");

            var fine = PlugInCheck.Read("""{"Text":"ok","Programs":[{"Program":"ETABS 20","Listed":true,"Loads":true,"Runtime":".NET Framework","Reasons":[]},{"Program":"SAFE 21","Listed":false,"Loads":null,"Runtime":".NET Framework","Reasons":["CSI ProMax is not in this list."]},{"Program":"SAP2000 24","Listed":true,"Loads":null,"Runtime":".NET Framework","Reasons":[]}]}""");
            ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            PcFieldSteps.PlugIn(ctx, fine);
            Equal("ETABS 20 ok|SAP2000 24 skipped|report ok", string.Join("|", ctx.Steps.Select(s => (s.Name.StartsWith("the plug-in check", StringComparison.Ordinal) ? "report" : s.Name[..s.Name.IndexOf(" can", StringComparison.Ordinal)]) + (s.Skipped ? " skipped" : s.Ok ? " ok" : " FAILED"))), "one step for each program that lists it; one not in the list has its own step elsewhere");

            // His list held CSI ProMax twice (added by hand a second time, from another folder): it becomes one entry, here.
            var twice = "[PlugIn]\r\n NumberPlugIns=2\r\n PlugInName=CsiProMaxPlugin\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath=C:\\Users\\x\\OneDrive - Company\\Documents\\CSI ProMax\\CsiProMaxPlugin.dll\r\n PlugInName=CsiProMaxPlugin\r\n PlugInMenuText=myCsiProMaxPlugin\r\n PlugInPath=C:\\Users\\x\\Documents\\CSI ProMax\\CsiProMaxPlugin.dll\r\n\r\n[Graphics]\r\n";
            var (state, text) = ToolsMenu.Edit(twice, "CsiProMaxPlugin", @"D:\Here\CsiProMaxPlugin.dll", _ => true);
            Equal(ToolsMenu.State.Moved, state, "listed twice");
            Equal("[PlugIn]\r\n NumberPlugIns=1\r\n PlugInName=CsiProMaxPlugin\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath=D:\\Here\\CsiProMaxPlugin.dll\r\n\r\n[Graphics]\r\n", text, "one entry, for this install");
            var beside = "[PlugIn]\r\n NumberPlugIns=2\r\n PlugInName=Someone.Else\r\n PlugInMenuText=Else\r\n PlugInPath=C:\\Else\\Else.dll\r\n PlugInName=CsiProMaxPlugin\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath=C:\\Gone\\CsiProMaxPlugin.dll\r\n";
            Equal(ToolsMenu.State.Others, ToolsMenu.Edit(beside, "CsiProMaxPlugin", @"D:\Here\CsiProMaxPlugin.dll", _ => false).State, "beside another plug-in: left alone");

            // Without the check file, it is said, not passed.
            ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            PcFieldSteps.PlugIn(ctx, PlugInCheck.Run(Path.Combine(Path.GetTempPath(), "cpm-nothing-here")));
            Check(ctx.Steps.Single().Skipped, "no check file: skipped");

            // The real thing, where this machine has a PowerShell: check.ps1 beside CsiProMax.dll is run and read.
            var shell = new[] { "pwsh", "powershell" }.SelectMany(n => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"))
                .SelectMany(d => new[] { Path.Combine(d, n), Path.Combine(d, n + ".exe") })).FirstOrDefault(File.Exists);
            if (shell is null) return;
            var root = Path.Combine(Path.GetTempPath(), "cpm-plugin-check-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var install = Directory.CreateDirectory(Path.Combine(root, "it's here")).FullName;
                File.Copy(Path.Combine(SourceText.Root(), PlugInCheck.FileName), Path.Combine(install, PlugInCheck.FileName));
                File.WriteAllText(Path.Combine(install, "CsiProMax.dll"), "");
                File.WriteAllText(Path.Combine(install, "CsiProMaxPlugin.dll"), "");
                var settings = Directory.CreateDirectory(Path.Combine(root, "Computers and Structures", "ETABS 20")).Parent!.FullName;
                File.WriteAllText(Path.Combine(settings, "ETABS 20", "ETABS.ini"), $"[PlugIn]\r\n NumberPlugIns=1\r\n PlugInName=CsiProMaxPlugin\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath={Path.Combine(install, "CsiProMaxPlugin.dll")}\r\n");
                var real = PlugInCheck.Run(install, settings, shell) ?? throw new Exception("the check did not run");
                Check(real.Text.StartsWith("CSI ProMax check", StringComparison.Ordinal), "its report is read: " + real.Text);
                Equal("ETABS 20 True", string.Join(", ", real.Programs.Select(p => $"{p.Program} {p.Listed}")), "the program it found");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    /// <summary>The analysed SAFE stand-in with the tester's two input tables added.</summary>
    static ICsiLink WithEntered(SafeMemory safe)
    {
        var tables = safe.Tables().ToList();
        return new Extra(safe, [.. tables, new TableName(Overwrites, Overwrites, false), new TableName(Combinations, Combinations, false)]);
    }

    /// <summary>A link with more tables listed than its model has: the added ones read as one row of entered data.</summary>
    sealed class Extra(ICsiLink inner, IReadOnlyList<TableName> tables) : Forward(inner)
    {
        public override IReadOnlyList<TableName> Tables() => tables;
        public override TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) =>
            key is Overwrites or Combinations ? new(["Name", "Status"], [["ULS1", "OK"]]) : base.ReadTable(key, cases, combos);
    }

    /// <summary>A link that passes everything on, for a stand-in that changes one answer.</summary>
    class Forward(ICsiLink inner) : ICsiLink
    {
        public CsiProgram Program => inner.Program;
        public virtual ModelInfo Info() => inner.Info();
        public IReadOnlyList<StoryRow> Stories() => inner.Stories();
        public IReadOnlyList<FrameRow> Frames() => inner.Frames();
        public IReadOnlyList<AreaRow> Areas() => inner.Areas();
        public IReadOnlyList<PointRow> Points() => inner.Points();
        public bool[] Restraint(string point) => inner.Restraint(point);
        public IReadOnlyList<(string Kind, string Name)> Selected() => inner.Selected();
        public void ClearSelection() => inner.ClearSelection();
        public void Select(string kind, string name) => inner.Select(kind, name);
        public IReadOnlyList<string> FrameSections() => inner.FrameSections();
        public IReadOnlyList<string> AreaSections() => inner.AreaSections();
        public IReadOnlyList<string> Materials() => inner.Materials();
        public IReadOnlyList<LoadPatternRow> LoadPatterns() => inner.LoadPatterns();
        public IReadOnlyList<string> LoadCases() => inner.LoadCases();
        public IReadOnlyList<string> Combos() => inner.Combos();
        public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section) => inner.AddFrame(x1, y1, z1, x2, y2, z2, section);
        public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => inner.AddArea(corners, section);
        public void SetFrameSection(string name, string section) => inner.SetFrameSection(name, section);
        public void SetAreaSection(string name, string section) => inner.SetAreaSection(name, section);
        public void Delete(string kind, string name) => inner.Delete(kind, name);
        public void SetRestraint(string point, bool[] restraint) => inner.SetRestraint(point, restraint);
        public void AddLoadPattern(string name, string type, double selfWeight) => inner.AddLoadPattern(name, type, selfWeight);
        public void SetFrameLoad(string frame, string pattern, double value, bool replace) => inner.SetFrameLoad(frame, pattern, value, replace);
        public void SetAreaLoad(string area, string pattern, double value, bool replace) => inner.SetAreaLoad(area, pattern, value, replace);
        public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) => inner.AddCombo(name, cases);
        public virtual IReadOnlyList<TableName> Tables() => inner.Tables();
        public virtual TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => inner.ReadTable(key, cases, combos);
        public object? Raw(string what, Func<dynamic, object?> work) => inner.Raw(what, work);
        public void RunAnalysis() => inner.RunAnalysis();
        public void Save() => inner.Save();
        public void Refresh() => inner.Refresh();
    }

    /// <summary>A model that is analysed, for the step that wants one that is not: the step must say it cannot judge it.</summary>
    sealed class Answering(ICsiLink inner) : Forward(inner)
    {
        public override ModelInfo Info() => base.Info() with { Locked = true };
    }
}

/// <summary>SAFE's own API as far as the self test asks it: the tester's three area properties, of which WALL1 is no slab.</summary>
public sealed class SafeApi
{
    public Properties PropArea { get; } = new();
    public Springs PropAreaSpring { get; } = new();
    public SlabDesign DesignConcreteSlab { get; } = new();

    public sealed class SlabDesign { public Strips DesignStrip { get; } = new(); }
    public sealed class Strips { public int GetNameList(ref int n, ref string[] names) { names = []; n = 0; return 0; } }

    public sealed class Properties
    {
        public int GetNameList(ref int n, ref string[] names) { names = ["F400", "WALL1", "F500"]; n = 3; return 0; }
        public int GetSlab(string name, ref int type, ref int shell, ref string material, ref double thickness, ref int color, ref string notes, ref string guid)
        {
            if (name == "WALL1") return 1;
            type = 6; shell = 2; material = "F'c_40"; thickness = name == "F400" ? 0.4 : 0.5;
            return 0;
        }
    }

    public sealed class Springs
    {
        public int GetNameList(ref int n, ref string[] names) { names = []; n = 0; return 0; }
    }
}

/// <summary>Answers downloads from a function of the address: bytes, or null for "not found".</summary>
sealed class Served(Func<string, byte[]?> files) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = files(request.RequestUri!.ToString());
        return Task.FromResult(bytes is null ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
