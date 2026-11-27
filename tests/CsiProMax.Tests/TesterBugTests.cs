using CsiProMax.Update;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsiProMax;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;

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
                // 0.4.64 SAP2000 rerun: the engineer's ETABS 23 was open with CSI ProMax's plug-in in use and its file listing
                // none: its Tools menu has it, so it is not a failure, and the file is still left alone.
                var inUse = ToolsMenu.Repair(install, settings, (program, number) => program.Key == "etabs" && number == 23, hasPlugIn: (program, number) => program.Key == "etabs" && number == 23);
                Equal(ToolsMenu.State.Running, inUse.Single(e => e.Label == "ETABS 23").State, "ETABS 23, open with the plug-in loaded");
                Check(inUse.Single(e => e.Label == "ETABS 23").Listed, "it counts as in the Tools menu");
                Equal(none, System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(etabs23)), "its file is still left alone");

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

        // ---- Ahmed 2026-11-27: a test build had to be pasted in by hand; the field test PC now takes them by itself ----

        yield return ("test builds: v0.4.67-testN is newer than the build before it and older than the next release, and only a PC that asks takes it", () =>
        {
            Check(Updater.TagVersion("v0.4.66") == "0.4.66", "a release tag");
            Check(Updater.TagVersion("v0.4.67-test5") == "0.4.67.5", "a test build tag");
            Check(Updater.TagVersion("v0.4.67-rc1") is null && Updater.TagVersion("nightly") is null, "an unknown tag was taken");
            var order = new[] { "0.4.66", "0.4.67", "0.4.67.4", "0.4.67.5", "0.4.68" }.Select(Updater.Parse).ToArray();
            for (var i = 1; i < order.Length; i++) Check(order[i] > order[i - 1], $"{order[i]} is not newer than {order[i - 1]}");
            Check(Updater.Parse("0.4.66.0") == Updater.Parse("0.4.66"), "a plain assembly version 0.4.66.0 is 0.4.66");
            static string Rel(string tag, bool draft = false, bool zip = true) =>
                $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"body":"notes","assets":[{"name":"{{(zip ? $"CsiProMax-{tag.TrimStart('v')}.zip" : "other.zip")}}","browser_download_url":"https://example.invalid/{{tag}}.zip","digest":"sha256:abc"}]}""";
            using (var doc = System.Text.Json.JsonDocument.Parse("[" + string.Join(",", Rel("v0.4.67-test4"), Rel("v0.4.67-test6", draft: true), Rel("v0.4.67-test5"), Rel("v0.4.66"), Rel("v0.4.67-test7", zip: false)) + "]"))
            {
                var pick = Updater.PickNewest(doc.RootElement) ?? throw new Exception("nothing picked");
                Check(pick.Version == "0.4.67.5" && pick.ZipUrl.EndsWith("v0.4.67-test5.zip", StringComparison.Ordinal) && pick.Sha256 == "abc", "picked " + pick);
            }
            using (var doc = System.Text.Json.JsonDocument.Parse("[" + string.Join(",", Rel("v0.4.67-test5"), Rel("v0.4.68")) + "]"))
                Check(Updater.PickNewest(doc.RootElement)?.Version == "0.4.68", "the public release after the test builds was not taken");
            Check(Updater.Behind(Updater.Parse("0.4.67.4"), "0.4.67.5", null, null)?.Contains("0.4.67.5") == true, "test4 was not told test5 is out");
            // Off by default: every other PC keeps taking only the public releases.
            Check(!new CsiProMax.Settings.AgentSettings().TestBuilds, "test builds are on by default");
            var settings = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/UI/SettingsWindow.cs"));
            Check(settings.Contains("Get test builds", StringComparison.Ordinal), "the switch is not in Settings > Updates");
            // The first test6 build stamped -p:AssemblyVersion for the whole build, so the ETABS/SAFE/SAP2000 API stand-ins
            // became 0.4.67.6 too and a .NET Framework program could not have created the plug-in. Only our files carry it.
            var release = File.ReadAllText(Path.Combine(SourceText.Root(), ".github/workflows/release.yml"));
            Check(!release.Contains("-p:AssemblyVersion", StringComparison.Ordinal) && release.Contains("-p:CsiTestBuild=", StringComparison.Ordinal), "a test build stamps every project, the API stand-ins too");
            foreach (var project in new[] { "CsiProMax.Assistant/CsiProMax.Assistant", "CsiProMax.Plugin/CsiProMax.Plugin", "CsiProMax.Plugin.Safe/CsiProMax.Plugin.Safe", "CsiProMax.Plugin.Sap/CsiProMax.Plugin.Sap" })
                Check(File.ReadAllText(Path.Combine(SourceText.Root(), "src", project + ".csproj")).Contains("$(Version).$(CsiTestBuild)", StringComparison.Ordinal), project + " does not carry the test build number");
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
            Check(window.Contains("UpdateBar.Build(") && window.Contains("CsiProMax.App.SwitchWhenIdle();"), "the window has no update button that installs and switches");
            // Ahmed, 2026-11-26: "I need the button of update in the chat at the top very clear so that it is a one click".
            Equal<string?>(null, CsiProMax.UI.UpdateBar.Text(now, "0.4.6", "0.4.6"), "no bar on the newest version");
            Equal<string?>("Update to 0.4.7 now  (one click, nothing is closed)", CsiProMax.UI.UpdateBar.Text(now, "0.4.7", null), "the bar for a release");
            Equal<string?>("Switch to 0.4.7 now  (one click, nothing is closed)", CsiProMax.UI.UpdateBar.Text(now, "0.4.7", "0.4.7"), "the bar for a download");
            Check(window.Contains("DockPanel.SetDock(_updateBar, Dock.Top);"), "the Update bar is not across the top of the window");
            var bar = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/UI/UpdateBar.cs"));
            Check(bar.Contains("HorizontalAlignment = HorizontalAlignment.Stretch") && bar.Contains("Background = Accent"), "the Update bar is not a wide bar in the accent colour");
            var pages = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Host/WebWindow.cs"));
            var program = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs"));
            Check(pages.Contains("UpdateBar.Build(click)") && program.Split("WebWindow.UpdateClick =").Length == 3, "the Viewer's and the 3D view's windows have no Update bar (in a CSI program's window process and in the Viewer on its own)");
            Check(File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/PcFieldSteps.cs")).Contains("the Update bar at the top of the window"), "the field test does not check the Update bar");
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
            // 0.4.65 SAP2000 run: only "*** Error Message n ***" was kept. The message under each header comes with it.
            var headed = ScratchDialogs.ImportErrors(["*** Error Message 1 ***\nTable: FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY\nField M3I is not valid\n*** Error Message 2 ***\nTable: FRAME INSERTION POINT ASSIGNMENTS\nBad value\n*** Warning Message 1 ***\nsome warning"]);
            Equal(2, headed.Count, "headed errors: " + string.Join(" | ", headed));
            Check(headed[0].Contains("PARTIAL FIXITY", StringComparison.Ordinal) && headed[0].Contains("M3I", StringComparison.Ordinal) && headed[1].Contains("INSERTION", StringComparison.Ordinal) && !headed[1].Contains("warning", StringComparison.Ordinal), "an error's message is not kept with its header: " + string.Join(" | ", headed));
            Check(SandboxedLeftCopy(), "a left copy showing only its import log is not known as the test's own");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            var notices = scratch[scratch.IndexOf("public static List<string> CloseNotices", StringComparison.Ordinal)..scratch.IndexOf("public static bool IsStoryPicker", StringComparison.Ordinal)];
            var log = notices[notices.IndexOf("IsImportLog(title)", StringComparison.Ordinal)..];
            Check(log.Contains("Button(window, \"Done\")", StringComparison.Ordinal) && log.Contains("IsWindowEnabled(done)", StringComparison.Ordinal) && log.Contains("PostMessageW(done, 0x00F5 /* BM_CLICK */", StringComparison.Ordinal)
                && log.IndexOf("ImportErrors(", StringComparison.Ordinal) < log.IndexOf("PostMessageW(done", StringComparison.Ordinal) && !notices.Contains("0x0010", StringComparison.Ordinal), "the log is answered by its Done button after its errors are read, and never closed");
            static bool SandboxedLeftCopy() => ScratchProgram.IsLeftCopy(["SAP2000 - cpm-field-test-ab12cd", "Access Database Import Log"]);
        });
        yield return ("SAP2000 field test: when its next process cannot open the saved .sdb, each new item of the .s2k is tried alone (0.4.65 run)", () =>
        {
            var s2k = "File x.s2k\r\n\r\nTABLE:  \"FRAME RELEASE ASSIGNMENTS 1 - GENERAL\"\r\n   Frame=B1   M3I=Yes   M3J=Yes   PartialFix=Yes\r\n\r\nTABLE:  \"FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY\"\r\n   Frame=B1   M3I=5000   M3J=5000\r\n\r\n"
                + "TABLE:  \"FRAME INSERTION POINT ASSIGNMENTS\"\r\n   Frame=B1   CardinalPt=\"8 (Top center)\"   CoordSys=GLOBAL   JtOffsetZI=0.05   JtOffsetZJ=0.05   Mirror2=Yes   Mirror3=No\r\n\r\nTABLE:  \"JOINT COORDINATES\"\r\n   Joint=1   CoordSys=GLOBAL\r\n\r\nEND TABLE DATA\r\n";
            var items = ScratchProgram.ReopenSuspects(s2k);
            Equal(3, items.Count, "suspects: " + string.Join(", ", items.Select(i => i.Key)));
            var bare = items.Aggregate(s2k, (x, i) => i.Strip(x));
            Check(!bare.Contains("PARTIAL FIXITY", StringComparison.Ordinal) && bare.Contains("PartialFix=No", StringComparison.Ordinal) && bare.Contains("Mirror2=No", StringComparison.Ordinal)
                && bare.Contains("JtOffsetZI=0 ", StringComparison.Ordinal) && bare.Contains("CoordSys=Local", StringComparison.Ordinal), "the items are not all taken out: " + bare);
            Check(bare.Contains("Joint=1   CoordSys=GLOBAL", StringComparison.Ordinal) && bare.Contains("JOINT COORDINATES", StringComparison.Ordinal) && bare.Contains("END TABLE DATA", StringComparison.Ordinal), "another table was changed: " + bare);
            Equal(0, ScratchProgram.ReopenSuspects(bare).Count, "a stripped text still has suspects");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.Contains("if (text is not null && !_pinpointing) Pinpoint(ctx, program, text);", StringComparison.Ordinal), "a failed reopen of a converted .s2k is not pinpointed");
        });
        yield return ("install line: downloaded copies older than the installed version are retired, so an open program's Tools menu opens the installed one (2026-11-27, v0.4.67-test1)", () =>
        {
            var install = File.ReadAllText(Path.Combine(SourceText.Root(), "install.ps1"));
            var at = install.IndexOf("# 2a. Downloaded copies older than what was just installed are retired", StringComparison.Ordinal);
            Check(at > 0 && at < install.IndexOf("# 2b. With the AI chat", StringComparison.Ordinal) && at > install.IndexOf("files added or replaced", StringComparison.Ordinal), "the retire step is not after the copy into the folder");
            var step = install[at..install.IndexOf("# 2b. With the AI chat", StringComparison.Ordinal)];
            Check(step.Contains("-ge $installedVersion) { continue }", StringComparison.Ordinal) && step.Contains("Rename-Item", StringComparison.Ordinal) && step.Contains("'.del'", StringComparison.Ordinal) && !step.Contains("-Force -Recurse -LiteralPath $old", StringComparison.Ordinal),
                "only older copies are retired, by renaming (a copy in use stays)");
        });
        yield return ("field test: the Viewer's and the watcher's packs are read from a copy in a fresh hidden program, never in the long scratch session (SAP2000 heap crash, 0.4.67-test1)", () =>
        {
            var ft = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            var at = ft.IndexOf("ctx.WhileAnalysed = () =>", StringComparison.Ordinal);
            var body = ft[at..ft.IndexOf("try { ScratchSteps(program)(ctx); }", at, StringComparison.Ordinal)];
            Check(body.Contains("Packs.PackCopy.Read(program, saved,", StringComparison.Ordinal) && body.Contains("if (!IsLive(window.Link)) { Both(window.Link); return; }", StringComparison.Ordinal)
                && body.Contains("RunDesign(from,", StringComparison.Ordinal) && !body.Contains("ViewerCheck.FieldSteps(ctx, window.Link", StringComparison.Ordinal), "a live program's Viewer and watcher packs are not read from a copy");
        });
        yield return ("pack: the step by step tables are read from the program again, not from the shared reads of the same model (0.4.67-test4: no pushover curve)", () =>
        {
            var src = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/PackTables.cs"));
            var steps = src[src.IndexOf("public static JsonObject? AddSteps", StringComparison.Ordinal)..src.IndexOf("private static int Col(", StringComparison.Ordinal)];
            Check(steps.Contains("data = e.ReadTable(t.Key, cases, combos);", StringComparison.Ordinal) && !steps.Contains("Pack.ReadTable(", StringComparison.Ordinal), "the step tables come from the shared reads, which hold the envelopes");
        });
        yield return ("updates: the Tools menu keeper checks for a new version hourly with no window open (2026-11-27: 0.4.68 never reached a PC with none open)", () =>
        {
            var keeper = File.ReadAllText(Path.Combine(SourceText.Root(), "keep-plugins.ps1"));
            Check(keeper.Contains("try { Invoke-Updates } catch { }", StringComparison.Ordinal) && keeper.Contains("--check-update", StringComparison.Ordinal) && keeper.Contains("AddHours(1)", StringComparison.Ordinal) && keeper.Contains("updates-off.txt", StringComparison.Ordinal),
                "the keeper does not run the hourly update check");
            var program = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs"));
            Check(program.Contains("args.Contains(\"--check-update\")", StringComparison.Ordinal) && program.IndexOf("--check-update", StringComparison.Ordinal) < program.IndexOf("if (args.Contains(\"--fieldtest\"))", StringComparison.Ordinal), "the program has no window-less update check");
        });
        yield return ("field test: no step makes a program export to Access (0.4.68: Microsoft Access opened on Ahmed's PC), and a command-line run shows its progress window", () =>
        {
            foreach (var f in Directory.GetFiles(Path.Combine(SourceText.Root(), "src"), "*.cs", SearchOption.AllDirectories))
                Check(!File.ReadAllText(f).Contains("(\"DBTablesAccess\"", StringComparison.Ordinal), Path.GetFileName(f) + " exports the tables to Access");
            var program = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs"));
            Check(program.Contains("FieldProgressWindow.ShowAlone(run);", StringComparison.Ordinal) && program.Contains("--no-window", StringComparison.Ordinal), "the command-line full test shows no progress window");
            var window = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/UI/FieldProgressWindow.cs"));
            Check(window.Contains("ShowActivated = false", StringComparison.Ordinal), "the progress window takes the focus");
        });
        yield return ("e2k: the grid system is written with no grid lines, as ETABS 23.2 needs (Ahmed's real model: \"May not be a valid ETABS 23.2.0 text file\")", () =>
        {
            var e2k = "$ PROGRAM INFORMATION\r\n  PROGRAM  \"ETABS\"  VERSION \"23.2.0\"\r\n$ STORIES - IN SEQUENCE FROM TOP\r\n  STORY \"S1\"  HEIGHT 3\r\n  STORY \"Base\"  ELEV 0\r\n"
                + "$ GRIDS\r\n  GRIDSYSTEM \"G7\"  TYPE \"CARTESIAN\"  BUBBLESIZE 1.5\r\n$ DIAPHRAGM NAMES\r\n  DIAPHRAGM \"D1\"  TYPE RIGID\r\n$ END OF MODEL FILE\r\n";
            var back = CsiProMax.E2k.E2kWriter.Write(CsiProMax.E2k.E2kReader.Read(e2k));
            Check(back.Contains("$ GRIDS\r\n  GRIDSYSTEM  \"G7\"  TYPE  \"CARTESIAN\"  BUBBLESIZE  1.5", StringComparison.Ordinal) || back.Contains("GRIDSYSTEM") && back.Contains("\"G7\"") && back.Contains("BUBBLESIZE"), "the grid system with no grid lines is dropped:\n" + back);
            Check(back.IndexOf("$ GRIDS", StringComparison.Ordinal) is var g and > 0 && g < back.IndexOf("$ DIAPHRAGM NAMES", StringComparison.Ordinal), "$ GRIDS is not between the stories and the diaphragms");
            var blank = CsiProMax.E2k.E2kWriter.Write(new CsiProMax.Model.StructuralModel { GridSystemExtra = [] });
            Check(blank.Contains("GRIDSYSTEM") && blank.Contains("CARTESIAN"), "a model with no grid system details writes no grid system");
        });
        yield return ("engine: a model file the program rejects stops the run with the program's own words, never runs its blank default model (Ahmed's real model, 2026-11-27)", () =>
        {
            Check(ScratchDialogs.IsFileRefused("Error importing from file.  May not be a valid ETABS 23.2.0 text file"), "ETABS's import error is not taken as a refused file");
            var words = ScratchProgram.ImportRejected("ETABS", "MOD Template.e2k", 2401, ["Error importing from file. May not be a valid ETABS 23.2.0 text file"]);
            Check(words.StartsWith("ETABS rejected MOD Template.e2k: Error importing from file", StringComparison.Ordinal) && words.Contains("2401"), words);
            var dir = Path.Combine(Path.GetTempPath(), "cpm-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "m.e2k");
                File.WriteAllText(file, "$ POINT COORDINATES\r\n  POINT \"1\"  0 0\r\n  POINT \"2\"  5 0\r\n$ LINE CONNECTIVITIES\r\n  LINE  \"B1\"  BEAM  \"1\"  \"2\"  0\r\n$ END OF MODEL FILE\r\n");
                Check(ScratchProgram.TextObjectCount(CsiProMax.Assistant.Etabs.CsiProgram.Etabs, file) == 1, "the .e2k's objects are not counted");
                var s2k = Path.Combine(dir, "m.s2k");
                File.WriteAllText(s2k, "TABLE:  \"CONNECTIVITY - FRAME\"\r\n   Frame=1   JointI=1   JointJ=2\r\n   Frame=2   JointI=2   JointJ=3\r\n\r\nEND TABLE DATA\r\n");
                Check(ScratchProgram.TextObjectCount(CsiProMax.Assistant.Etabs.CsiProgram.Sap2000, s2k) == 2, "the .s2k's objects are not counted");
            }
            finally { Directory.Delete(dir, true); }
            var src = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/EngineRun.cs"));
            Check(src.Contains("could not open the model: {open.Detail}", StringComparison.Ordinal), "the Run Report does not lead with the rejected file");
        });
        yield return ("real model: shell uniform load sets are the slabs' loads everywhere, and an opening is a hole (Ahmed's real model, 2026-11-27: slabs with no load, \"no mass\")", () =>
        {
            var e2k = string.Join("\r\n", [
                "$ STORIES - IN SEQUENCE FROM TOP", "  STORY \"S1\"  HEIGHT 3", "  STORY \"Base\"  ELEV 0",
                "$ POINT COORDINATES", "  POINT \"1\"  0 0", "  POINT \"2\"  6 0", "  POINT \"3\"  6 6", "  POINT \"4\"  0 6", "  POINT \"5\"  2 2", "  POINT \"6\"  3 2", "  POINT \"7\"  3 3", "  POINT \"8\"  2 3",
                "$ AREA CONNECTIVITIES", "  AREA \"F1\"  FLOOR  4  \"1\"  \"2\"  \"3\"  \"4\"  0  0  0  0", "  AREA \"A1\"  FLOOR  4  \"5\"  \"6\"  \"7\"  \"8\"  0  0  0  0",
                "$ AREA ASSIGNS", "  AREAASSIGN  \"F1\"  \"S1\"  SECTION \"S200\"", "  AREAASSIGN  \"A1\"  \"S1\"  OPENING \"Yes\"",
                "$ LOAD PATTERNS", "  LOADPATTERN \"Live\"  TYPE  \"Live\"  SELFWEIGHT  0", "  LOADPATTERN \"Flooring\"  TYPE  \"Super Dead\"  SELFWEIGHT  0",
                "$ SHELL UNIFORM LOAD SETS", "  SHELLUNIFORMLOADSET \"Offices\"  LOADPAT \"Flooring\"  VALUE 1", "  SHELLUNIFORMLOADSET \"Offices\"  LOADPAT \"Live\"  VALUE 3.5",
                "$ SHELL OBJECT LOADS", "  AREALOAD  \"F1\"  \"S1\"  TYPE \"UNIFLOADSET\"  \"Offices\"",
                "$ END OF MODEL FILE", ""]);
            var m = CsiProMax.E2k.E2kReader.Read(e2k);
            var loads = CsiProMax.Model.LoadSets.Expanded(m);
            Check(loads.Count == 2 && loads.Any(l => l.Pattern == "Live" && l.Tokens.Contains("3.5")) && loads.Any(l => l.Pattern == "Flooring"), "the set is not the slab's loads: " + string.Join(" | ", loads.Select(l => string.Join(" ", l.Tokens))));
            var a = CsiProMax.Checks.Stability.FromStructural.Read(m, "etabs");
            Check(a.Patterns.All(p => p.HasLoads), "a pattern a load set carries counts as having no load");
            Check(a.Shells.Count == 1 && a.Shells[0].Label == "F1", "the opening is analysed as a shell: " + string.Join(", ", a.Shells.Select(x => x.Label)));
            var s2k = CsiProMax.S2k.S2kWriter.Write(m).Text;
            Check(s2k.Contains("UnifLoad=3.5", StringComparison.Ordinal) || s2k.Contains("UnifLoad=3.5 ", StringComparison.Ordinal), "SAP2000's file has no load from the set");
            var off = new CsiProMax.Assistant.Etabs.OfflineEtabs(m, "");
            Check(off.Areas().Count == 1, "the opening is drawn as a floor");
            Check(CsiProMax.Model.SlabDefs.Openings(m).Count == 1, "the opening is not drawn as an opening");
            Check(off.ReadTable("Shell Loads - Uniform", null, null).Rows.Count == 2, "the slab shows no load");
            Check(CsiProMax.E2k.E2kWriter.Write(m).Contains("TYPE \"UNIFLOADSET\"  \"Offices\"", StringComparison.Ordinal) || CsiProMax.E2k.E2kWriter.Write(m).Contains("UNIFLOADSET", StringComparison.Ordinal), "the set is not written back as a set");
        });
        yield return ("real models: the round trip finds the PC's models, and names every table, row and field that differs (Ahmed 2026-11-27: \"it should be exactly the same\")", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "real-models-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(dir, "Projects", "A"));
            try
            {
                File.WriteAllText(Path.Combine(dir, "Projects", "A", "tower.e2k"), "$ STORIES\r\n");
                File.WriteAllText(Path.Combine(dir, "Projects", "A", "cpm-field-test-1.e2k"), "$ STORIES\r\n");
                File.WriteAllText(Path.Combine(dir, "Projects", "A", "frame.EDB"), "x");
                File.WriteAllText(Path.Combine(dir, "Projects", "A", "frame.e2k"), "$ STORIES\r\n");
                File.WriteAllText(Path.Combine(dir, "Projects", "A", "solo.sdb"), "x");
                var etabs = RealModelField.Find(CsiProMax.Assistant.Etabs.CsiProgram.Etabs, [dir], Path.Combine(dir, "none.txt")).Select(Path.GetFileName).ToList();
                Check(etabs.Contains("tower.e2k") && etabs.Contains("frame.e2k") && !etabs.Contains("frame.EDB") && !etabs.Contains("cpm-field-test-1.e2k"), "ETABS models: " + string.Join(", ", etabs));
                // A SAP2000 model with no text beside it is taken too: its tables are read as its .s2k (SapTablesText).
                Check(RealModelField.Find(CsiProMax.Assistant.Etabs.CsiProgram.Sap2000, [dir], Path.Combine(dir, "none.txt")).Select(Path.GetFileName).SequenceEqual(["solo.sdb"]), "a SAP2000 .sdb with no text beside it is taken");
            }
            finally { Directory.Delete(dir, true); }
            var a = new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Shell Uniform Load Sets"] = new(["Name", "LoadPattern", "Value"], [["Offices", "Live", "3.5"], ["Offices", "Flooring", "1"]]),
                ["Area Assignments - Openings"] = new(["Story", "Label", "Opening"], [["L5", "A1", "Yes"]]),
                ["Joint Coordinates"] = new(["Label", "X", "GUID"], [["1", "5.6506705", "a"]]),
            };
            var b = new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Shell Uniform Load Sets"] = new(["Name", "LoadPattern", "Value"], [["Offices", "Live", "3.4"], ["Offices", "Flooring", "1"]]),
                ["Joint Coordinates"] = new(["Label", "X", "GUID"], [["1", "5.65067050001", "b"]]),
            };
            var d = RealModelField.Compare(a, b, model: true);
            Check(d.Count == 2 && d.Any(x => x.StartsWith("Shell Uniform Load Sets: Offices|Live Value is '3.4', not '3.5'", StringComparison.Ordinal)) && d.Any(x => x.StartsWith("Area Assignments - Openings: the table is gone", StringComparison.Ordinal)),
                "differences: " + string.Join(" | ", d));
            var src = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(src.Contains("RealModelField.Run(ctx, program);", StringComparison.Ordinal), "the field test does not run the real-model round trip");
        });
        yield return ("--version prints the running version and opens no window", () =>
        {
            var text = CsiProMax.Assistant.Program.VersionText();
            Check(text.StartsWith(CsiProMax.Update.Updater.Current.ToString(3), StringComparison.Ordinal) || text.Contains(CsiProMax.Update.Updater.Current.ToString(), StringComparison.Ordinal), "the version printed: " + text);
            var main = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Program.cs"));
            Check(main.IndexOf("args.Contains(\"--version\")", StringComparison.Ordinal) is var at and > 0 && at < main.IndexOf("Newer(args) is { } newerTest", StringComparison.Ordinal), "--version is answered before any window or hand-over");
        });
        yield return ("field test: an imported .e2k that ETABS keeps as (Untitled) counts as opened when its objects came in", () =>
        {
            Check(CsiProMax.Assistant.ScratchProgram.HoldsText(@"C:\x\(Untitled)", @"C:\t\cpm-field-test.e2k", 120, 120), "an imported .e2k with its objects held is opened");
            Check(!CsiProMax.Assistant.ScratchProgram.HoldsText(@"C:\x\(Untitled)", @"C:\t\cpm-field-test.e2k", 120, 0), "a blank (Untitled) model is not the file");
            Check(!CsiProMax.Assistant.ScratchProgram.HoldsText(@"C:\x\(Untitled)", @"C:\t\model.edb", 0, 50), "an .EDB still has to be held by its name");
            Check(CsiProMax.Assistant.ScratchProgram.HoldsText(@"C:\t\model.EDB", @"C:\t\model.edb", 0, 0), "the same name is held");
        });
        yield return ("field test: the real-model run notes each part and table read as it starts, so a stall names its call", () =>
        {
            var src = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Field", "RealModelField.cs"));
            Check(src.Contains("\"progress.txt\"", StringComparison.Ordinal), "no progress file");
            foreach (var part in new[] { "read every model table", "run the analysis", "run the design", "read every result and design table", "  model table ", "  result table " })
                Check(src.Contains("Mark(\"" + part, StringComparison.Ordinal), "not noted: " + part);
        });
        yield return ("field test: steps a workaround can test are tested, not left out (Ahmed, 2026-11-26: only a missing program or licence)", () =>
        {
            string Src(string f) => File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant", f));
            var extras = Src("Field/ScratchExtras.cs");
            // ETABS 23's history function and case through its own tables (keys from its Table and Field Keys.xml).
            Equal("Functions - Time History - User Defined", CsiProMax.Assistant.ScratchExtras.HistoryFunctionTable, "ETABS's history function table");
            Equal("Load Case Definitions - Time History - Linear Modal", CsiProMax.Assistant.ScratchExtras.HistoryCaseTable, "ETABS's linear modal history case table");
            Check(extras.Contains("EditableTables.Put(link, HistoryFunctionTable", StringComparison.Ordinal) && !extras.Contains("API has no call to define a time history function", StringComparison.Ordinal), "ETABS's history case is still left out");
            Check(extras.Contains("LoadCases.ModalEigen.SetCase\", (\"Name\", ModalForHistory)", StringComparison.Ordinal) && !extras.Contains("has no modal case for", StringComparison.Ordinal), "a model with no modal case still leaves the history case out");
            Check(extras.Contains("\"FrameObj.AddByPoint\"", StringComparison.Ordinal) && extras.Contains("UserName\", Brace", StringComparison.Ordinal), "ETABS's scratch model has no brace for the brace forces table");
            // A change that cannot be read back fails, by name.
            Check(!Src("Sap2000/SapFieldSteps.cs").Contains("throw new FieldSkipException($\"the change could not be read back", StringComparison.Ordinal)
                && !Src("Field/EtabsFieldSteps.cs").Contains("throw new FieldSkipException(\"the load could not be read back", StringComparison.Ordinal), "a read-back that cannot be made is still not tested");
            Check(!Src("Field/EtabsFieldSteps.cs").Contains("its automatic load cannot be set through this ETABS's API", StringComparison.Ordinal), "ETABS's seismic coefficient is still left out (its table takes it)");
        });
        yield return ("field test: SAP2000's 'Maintenance Expiring' reminder is answered by the test itself, no other licence message is (Ahmed, 2026-11-26)", () =>
        {
            var reminder = new[] { "License Available. Session Created. Maintenance expiring on 5-Jul-2019.", "Show this message at startup", "Do not show this message until next expiration period", "Never show this message again", "Contact CSI", "OK" };
            Check(CsiProMax.Assistant.ScratchDialogs.IsMaintenanceReminder("Maintenance Expiring", reminder), "the reminder seen on Ahmed's PC is answered");
            Check(CsiProMax.Assistant.ScratchDialogs.IsNotUntilNextPeriod("Do not show this message until next &expiration period") && !CsiProMax.Assistant.ScratchDialogs.IsNotUntilNextPeriod("Never show this message again"), "the choice made: not until the next expiration period");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsMaintenanceReminder(" License Message", ["Status 485. All 40 licenses in your license group(s) are in use.", "OK"]), "a licence that is not free is left to the engineer");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsMaintenanceReminder("Maintenance Expiring", ["License not available. Maintenance expiring on 5-Jul-2019.", "OK"]), "no licence given: left to the engineer");
            Check(!CsiProMax.Assistant.ScratchDialogs.IsMaintenanceReminder("Maintenance Expiring", ["License error -15. Maintenance expiring on 5-Jul-2019.", "OK"]), "a licence error is left to the engineer");
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(source.Contains("ScratchDialogs.AnswerMaintenanceReminder(fresh)", StringComparison.Ordinal), "the reminder is not answered while the test's own window starts (it waited for Ahmed at every start)");
        });
        yield return ("field test: it shows its progress live, with step X of about N, passed / failed / not tested and the time (Ahmed, 2026-11-26)", () =>
        {
            // The counts come from the outcome lines as they are recorded.
            var run = new CsiProMax.Assistant.FieldTest.Running(true, "a test");
            run.Counted("ok          [SAP2000 on a scratch model] tool run_analysis");
            run.Counted("FAIL        [SAP2000 on a scratch model] tool set_lateral_load, seismic KDS: why");
            run.Counted("not tested  [CSI ProMax's own features] export_pack: why");
            Check(run.Ok == 1 && run.Failed == 1 && run.NotTested == 1 && run.Done == 3 && run.Part == "CSI ProMax's own features", $"counted {run.Ok}/{run.Failed}/{run.NotTested}, part '{run.Part}'");
            // How many steps to expect: the last run of the same kind on this PC.
            var file = Path.Combine(Path.GetTempPath(), "cpm-steps-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                Check(CsiProMax.Assistant.FieldTest.ExpectedSteps(true, false, file) == 0, "a first run has no estimate");
                CsiProMax.Assistant.FieldTest.SaveExpected(true, false, 915, file);
                CsiProMax.Assistant.FieldTest.SaveExpected(true, true, 400, file);
                CsiProMax.Assistant.FieldTest.SaveExpected(true, false, 920, file);
                Check(CsiProMax.Assistant.FieldTest.ExpectedSteps(true, false, file) == 920 && CsiProMax.Assistant.FieldTest.ExpectedSteps(true, true, file) == 400, "the last count of each kind is kept");
                // One program alone keeps its own count.
                CsiProMax.Assistant.FieldTest.SaveExpected(true, false, 310, file, CsiProMax.Assistant.Etabs.CsiProgram.Sap2000);
                Check(CsiProMax.Assistant.FieldTest.ExpectedSteps(true, false, file, CsiProMax.Assistant.Etabs.CsiProgram.Sap2000) == 310 && CsiProMax.Assistant.FieldTest.ExpectedSteps(true, false, file) == 920, "one program alone has its own estimate");
            }
            finally { try { File.Delete(file); } catch (IOException) { } }
            // The window's lines.
            var p = CsiProMax.Assistant.FieldTest.ProgressLines.Of("SAP2000 on a scratch model", "tool run_analysis", 460, 920, 450, 6, 4, TimeSpan.FromSeconds(754), false);
            Check(p.Count == "Step 460 of about 920" && p.Percent == 50 && p.Tally == "Passed 450   Failed 6   Not tested 4" && p.Time == "Running for 12:34" && p.Step == "Now: tool run_analysis", "the lines: " + p);
            Check(CsiProMax.Assistant.FieldTest.ProgressLines.Of("x", "y", 2000, 920, 0, 0, 0, TimeSpan.Zero, false).Percent == 99, "never 100% before it is done");
            Check(CsiProMax.Assistant.FieldTest.ProgressLines.Of("", "starting", 3, 0, 3, 0, 0, TimeSpan.Zero, false) is { Percent: null, Part: "Starting" } first && first.Count.Contains("first run", StringComparison.Ordinal), "a first run shows a moving bar");
            // Started from the Help menu or the chat, it opens.
            Check(File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Tools/SelfTestTools.cs")).Contains("FieldProgressWindow.Show(run)", StringComparison.Ordinal), "the full field test does not show its progress window");
            // One program at a time, from the Help menu and the chat; the chat's status answers from the live counts.
            var tools = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Tools/SelfTestTools.cs"));
            var menu = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Host/MainWindow.cs"));
            Check(tools.Contains("OnlyProgram = only", StringComparison.Ordinal) && tools.Contains("\"program\"", StringComparison.Ordinal), "the chat cannot test one program alone");
            Check(menu.Contains("Field test: {program.Name} only", StringComparison.Ordinal), "the Help menu has no one-program field test");
            Check(tools.Contains("live_file = run.LiveFile", StringComparison.Ordinal) && tools.Contains("passed = run.Ok", StringComparison.Ordinal), "the chat's status does not give the live counts");
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
        yield return ("ETABS 23 field test: a check whose table has no rows for the scratch model fails with the table's name; a table that cannot be read still fails", () =>
        {
            // Seen on 2026-11-25 with 0.4.11: "1 of 5 checks had nothing to read. check_story_irregularity: no reason given".
            JsonNode Answer(string json) => JsonNode.Parse(json)!;
            var ctx = new FieldContext(CsiProgram.Etabs, Path.GetTempPath(), new MemoryEtabs());
            FieldParts.Verdicts(ctx, "checks", [("check_drift", Answer("""{"status":"pass"}""")), ("check_story_irregularity", Answer("""{"status":"not available","no_data":true,"note":"ETABS's 'Story Stiffness' table has no data"}"""))]);
            var step = ctx.Steps.Last();
            // Since 2026-11-26 (Ahmed: skip only for a missing program or licence) a check with no data on a program that is there is a failure that names the table, so the scratch model is made to have it.
            Check(!step.Ok && !step.Skipped && step.Detail.Contains("Story Stiffness", StringComparison.Ordinal), $"no data is not a failure with its reason: {step.Ok} {step.Skipped} {step.Detail}");
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
            WindowFieldSteps.Run(busy, _ => new WindowFieldSteps.Seen(false, "a CSI ProMax window on the demo model is already open on this PC, so a second one could not be started. Close it and run the test again.", NotTried: true));
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
            // 2026-11-26 (Ahmed: "exactly the same way"): the analysis mesh, for shell contours on the program's elements,
            // whatever the program says about importing it.
            foreach (var table in new[] { "Objects and Elements - Joints", "Objects and Elements - Shells", "Objects And Elements - Joints", "Objects And Elements - Areas" })
                foreach (var import in new[] { -1, 0, 1 })
                    Check(CsiProMax.Assistant.Tools.PackTables.Kind(new CsiProMax.Assistant.Etabs.TableName(table, table, false, import)) == "result", $"'{table}' (import {import}) is not taken by the pack for the mesh");
            // 0.4.49 SAP2000 run: Load Assigns greyed out. Every program's load tables are inputs the pack takes, with a
            // total of their own so that the results cannot crowd them out.
            foreach (var table in new[] { "Frame Loads - Distributed", "Frame Loads - Point", "Frame Loads - Temperature", "Area Loads - Uniform", "Area Loads - Temperature", "Joint Loads - Force", "Shell Loads - Uniform", "Shell Loads - Temperature",
                         // ETABS 23's names (0.4.55 ETABS run: "the pack has no Frame Loads Assignments - Distributed (taken as nothing)").
                         "Frame Loads Assignments - Distributed", "Frame Loads Assignments - Point", "Frame Loads Assignments - Open Structure Wind Parameters", "Joint Loads Assignments - Force", "Shell Loads Assignments - Uniform" })
            {
                Check(CsiProMax.Assistant.Tools.PackTables.IsLoadTable(table), $"'{table}' is not seen as a load table");
                Check(CsiProMax.Assistant.Tools.PackTables.Kind(new CsiProMax.Assistant.Etabs.TableName(table, table, false, 1)) == "input", $"'{table}' is not taken by the pack as an input");
            }
            Check(!CsiProMax.Assistant.Tools.PackTables.IsLoadTable("Load Pattern Definitions") && !CsiProMax.Assistant.Tools.PackTables.IsLoadTable("Joint Displacements"), "a table that is not a load assignment is seen as one");
            Check(CsiProMax.Assistant.Tools.PackTables.InputMostRows > 0, "the inputs have no total of their own");
            Check(CsiProMax.Assistant.Tools.PackTables.Kind(new CsiProMax.Assistant.Etabs.TableName("Objects and Elements - Frames", "Objects and Elements - Frames", false, 1)) is null, "the frame mesh is taken, which the Viewer does not use");
            foreach (var table in new[] { "Section Cut Forces - Analysis", "Element Forces - Links", "Element Deformations - Links" })
                Check(CsiProMax.Assistant.Tools.PackTables.Kind(new CsiProMax.Assistant.Etabs.TableName(table, table, false, 0)) == "result", $"'{table}' is not taken by the pack as a result");
            // The scratch model's extras are one honest line on a stand-in, and every name they use is fixed.
            // The section cut goes through ApiWorkarounds (ETABS 20 has no SectCut: its table takes it, 0.4.53).
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchExtras.cs")) + File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/ApiWorkarounds.cs"));
            foreach (var call in new[] { "PointObj.SetSpring", "PropLink.SetLinear", "LinkObj.AddByPoint", "SectCut.SetByGroup", "LoadCases.StaticNonlinear.SetLoadApplication", "Func.FuncTH.SetUser", "LoadCases.ModHistLinear.SetTimeStep" })
                Check(source.Contains('"' + call + '"'), $"the scratch extras no longer call {call}");
            foreach (var steps in new[] { "Field/EtabsFieldSteps.cs", "Sap2000/SapFieldSteps.cs" })
            {
                var text = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant", steps));
                Check(text.IndexOf("ScratchExtras.Add(ctx)", StringComparison.Ordinal) is var add and >= 0 && text.IndexOf("\"run_analysis\"", StringComparison.Ordinal) > add, $"{steps} must add the extras before its first analysis");
                Check(text.Contains("ScratchExtras.Check(ctx)"), $"{steps} must check the extras' tables after the analysis");
            }
        });
        yield return ("0.4.45 Modeler open for an hour with no update check written: a check that fails or cannot run always leaves a line to read", () =>
        {
            var updater = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Update/Updater.cs"));
            var check = updater[updater.IndexOf("public static async Task<string?> CheckAsync", StringComparison.Ordinal)..updater.IndexOf("private static void Apply(", StringComparison.Ordinal)];
            Check(check.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", StringComparison.Ordinal), "CheckAsync keeps whatever stops a check, not only the failures it expects");
            Check(check.Split("Said?.Invoke(").Length >= 3, "a held update.lock and an unexpected failure are both said");
            var app = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Panel/Host/App.cs"));
            var loop = app[app.IndexOf("internal static async Task UpdateLoopAsync", StringComparison.Ordinal)..app.IndexOf("internal static void CheckForUpdatesInteractive", StringComparison.Ordinal)];
            Check(loop.Contains("Updater.Said ??=", StringComparison.Ordinal) && loop.Split("ActivityLog.Note(\"update_check\"").Length >= 4, "the loop says in the activity log that it started, and what failed");
            Check(!loop.Contains("catch (Exception) { first = false;", StringComparison.Ordinal), "a failed round is not swallowed without a line");
        });

        yield return ("0.4.46 retest: steps left out because the program's window went away are tried again, not carried over as not tested", () =>
        {
            var cut = new FieldRetest.Part("0.4.46", "abc", 40, 0, [("set_lateral_load in SAP2000", "the program's window stopped answering at the step 'run_analysis' (The RPC server is unavailable), so this step was not run"), ("SAFE on this PC", "SAFE was not found on this PC")]);
            var (run, why) = FieldRetest.Decide(cut, "abc");
            Check(run && why.Contains("1 step of it was not run in version 0.4.46 because the program's window stopped answering", StringComparison.Ordinal), "the part is run again: " + why);
            var own = new FieldRetest.Part("0.4.46", "abc", 40, 0, [("CSI ProMax's own features on the scratch SAP2000 window", "that window no longer answers (gone), so they were not run")]);
            Check(FieldRetest.Decide(own, "abc").Run, "also when the window was gone before the part began");
            var settled = new FieldRetest.Part("0.4.46", "abc", 40, 0, [("SAFE on this PC", "SAFE was not found on this PC")]);
            Check(!FieldRetest.Decide(settled, "abc").Run, "a step that cannot be tested on this PC does not make the part run again");
            var context = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/FieldContext.cs"));
            var test = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(context.Contains("window stopped answering at the step", StringComparison.Ordinal) && test.Contains("that window no longer answers", StringComparison.Ordinal), "the words the rule looks for are the ones the test writes");
        });

        // ---- Ahmed, 2026-11-26: "you can skip as long as no license and if the program is not installed but as long as it is there do detailed testing for everything" ----
        yield return ("field test: a step is left out only for a missing program or licence; any other 'not tested' is a failure", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "cpm-skip-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            try
            {
                var ctx = new FieldContext(CsiProgram.Sap2000, folder, new MemoryEtabs());
                ctx.Section("skips");
                ctx.Skip("SAFE on this PC", "SAFE was not found on this PC (looked for SAFEv1.dll), so nothing of it is tested here.");
                ctx.Skip("ETABS starts", "ETABS has no licence on this PC");
                ctx.Skip("menu: Static Pushover Curve...", "greyed out, this pack has nothing for it");
                ctx.Skip("Modeler: File > Open", "it asks the PC for a file or a printer, which a browser without a window can't show");
                ctx.Step<object?>("history case", () => throw new FieldSkipException("ETABS 23's API has no call to define a time history function"));
                ctx.Step<object?>("slab design", () => throw new FieldSkipException("the analysis did not run"));
                var left = ctx.Steps.Where(x => x.Skipped).Select(x => x.Name).ToList();
                var failed = ctx.Steps.Where(x => !x.Ok).ToList();
                Check(left.SequenceEqual(["SAFE on this PC", "ETABS starts"]), "left out: " + string.Join(", ", left));
                Check(failed.Count == 4 && failed.All(x => x.Detail.Contains("counts as failed")), "failed: " + string.Join(" | ", failed.Select(x => x.Name + ": " + x.Detail)));
            }
            finally { Directory.Delete(folder, true); }
            // The retest tries a part again when an older run left a step of it out for a reason that no longer counts.
            var old = new FieldRetest.Part("0.4.52", "p", 10, 0, [("menu: Frame...", "greyed out, this pack has nothing for it")]);
            Check(FieldRetest.Decide(old, "p").Run, "a part with a step left out for no allowed reason was not run again");
            var fine = new FieldRetest.Part("0.4.52", "p", 10, 0, [("SAFE on this PC", "SAFE was not found on this PC")]);
            Check(!FieldRetest.Decide(fine, "p").Run, "a part left out only for a missing program was run again");
            // Every reason kind has its words, and the two Ahmed named are allowed.
            Check(SkipAudit.Allowed(SkipAudit.Why.ProgramNotHere) && SkipAudit.Allowed(SkipAudit.Why.Licence) && !SkipAudit.Allowed(SkipAudit.Why.NeedsAPerson) && !SkipAudit.Allowed(SkipAudit.Why.ProgramCannot) && !SkipAudit.Allowed(SkipAudit.Why.Unjustified), "the allowed reasons are not Ahmed's");
        });

        // Ahmed, 2026-11-26: "whatever I opened the CSI Promax where it is floating either opened in safe and then I went
        // to etabs or whatever to be smooth not depending from where it started".
        yield return ("the window follows the CSI program in use, whichever program it was opened from", () =>
        {
            var (safe, etabs, sap) = (CsiProgram.Safe, CsiProgram.Etabs, CsiProgram.Sap2000);
            var t0 = new DateTime(2026, 11, 26, 14, 0, 0, DateTimeKind.Utc);
            CsiProMax.Host.ProgramFollow.Front F(int pid, string name, string title = "model") => new(pid, name, title);
            var settle = CsiProMax.Host.ProgramFollow.Settle;

            // Opened from SAFE (process 10); he goes to ETABS (process 20) and stays there.
            var follow = new CsiProMax.Host.ProgramFollow.Tracker();
            Check(follow.Tick(F(10, "SAFE"), 10, safe, false, t0) is null, "its own program in front: nothing to follow");
            Check(follow.Tick(F(20, "ETABS"), 10, safe, false, t0) is null, "followed at the first glance");
            var to = follow.Tick(F(20, "ETABS"), 10, safe, false, t0 + settle);
            Check(to is { Pid: 20 } && to.Value.Program == etabs, "ETABS stayed in front and was not followed");

            // Work running here keeps the window where it is, and it follows when the work ends.
            follow = new CsiProMax.Host.ProgramFollow.Tracker();
            follow.Tick(F(20, "ETABS"), 10, safe, true, t0);
            Check(follow.Tick(F(20, "ETABS"), 10, safe, true, t0 + settle + settle) is null, "followed while a chat or a test was working");
            Check(follow.Tick(F(20, "ETABS"), 10, safe, false, t0 + settle + settle) is null, "followed in the moment the work ended");
            Check(follow.Tick(F(20, "ETABS"), 10, safe, false, t0 + settle + settle + settle) is { Pid: 20 }, "not followed after the work ended");

            // He comes to the CSI ProMax window itself (or to Excel) after ETABS: ETABS is still the program in use.
            follow = new CsiProMax.Host.ProgramFollow.Tracker();
            follow.Tick(F(20, "ETABS"), 10, safe, false, t0);
            Check(follow.Tick(F(99, "dotnet", "CSI ProMax 0.4.56 with SAFE"), 10, safe, false, t0 + settle) is { Pid: 20 }, "clicking into CSI ProMax forgot the program he came from");
            follow = new CsiProMax.Host.ProgramFollow.Tracker();
            follow.Tick(F(20, "ETABS"), 10, safe, false, t0);
            Check(follow.Tick(F(77, "EXCEL"), 10, safe, false, t0 + settle) is { Pid: 20 }, "another application in front forgot the program he came from");
            // Back in SAFE before it followed: it stays with SAFE.
            follow = new CsiProMax.Host.ProgramFollow.Tracker();
            follow.Tick(F(20, "ETABS"), 10, safe, false, t0);
            follow.Tick(F(10, "SAFE"), 10, safe, false, t0 + TimeSpan.FromSeconds(1));
            Check(follow.Tick(F(99, "dotnet"), 10, safe, false, t0 + settle + settle) is null, "he went back to SAFE and the window still left it");

            // A second window of the same program is followed too; other applications and a test's scratch copy never are.
            Check(CsiProMax.Host.ProgramFollow.Followable(F(11, "SAFE"), 10, safe) == safe, "another SAFE window is not followed");
            Check(CsiProMax.Host.ProgramFollow.Followable(F(30, "SAP2000"), 10, safe) == sap, "SAP2000 is not followed");
            Check(CsiProMax.Host.ProgramFollow.Followable(F(77, "EXCEL"), 10, safe) is null, "Excel is followed");
            Check(CsiProMax.Host.ProgramFollow.Followable(F(31, "SAP2000", "SAP2000 v26 - cpm-field-test-3e8eba"), 10, safe) is null, "a field test's scratch copy is followed");
            // Opened beside no window in particular (a shortcut): the same program is not a reason to start again.
            Check(CsiProMax.Host.ProgramFollow.Followable(F(20, "ETABS"), null, etabs) is null && CsiProMax.Host.ProgramFollow.Followable(F(10, "SAFE"), null, etabs) == safe, "a window beside no program window in particular");

            // It starts again with the same arguments, beside the other program window.
            var args = CsiProMax.Host.ProgramFollow.Args(["CsiProMax.dll", "--etabs-pid", "10", "--program", "safe", "--no-ai"], 20, etabs);
            Equal("CsiProMax.dll --no-ai --etabs-pid 20 --program etabs", string.Join(" ", args), "the arguments it starts again with");
            var program = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Program.cs"));
            Check(program.Contains("ProgramFollow.Tracker()") && program.Contains("ProgramFollow.Another(watched)") && program.Contains("FieldTest.RunningOnThisPc()") && program.Contains("ProgramFollow.HasWindow(to.Pid)"),
                "the window does not follow the program in use, or follows during a test, or into a program that has its own window");
            Check(program.Contains("\"--no-focus\"") && program.Contains("window.ShowActivated = false"), "following takes the keyboard away from the program he is working in");

            var folder = Path.Combine(Path.GetTempPath(), "cpm-follow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var lockFile = Path.Combine(folder, "field-test.lock");
                Check(!FieldTest.RunningOnThisPc(lockFile), "no lock file, yet a test is said to run");
                using (FieldTest.OnlyOne(lockFile)) Check(FieldTest.RunningOnThisPc(lockFile), "a full test holds the lock and is not seen");
                Check(!FieldTest.RunningOnThisPc(lockFile), "the test ended and is still seen as running");
            }
            finally { Directory.Delete(folder, true); }
        });

        // Ahmed, 2026-11-26: "everything you need actually from the any program you can include it as part of the bugs ...
        // but don't report it as bugs report it as needed things", and "Make sure that the bugs zip includes whatever you want".
        yield return ("field test: the needed things are gathered from each program into the zip, and are never failures", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cpm-reference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var items = ReferenceData.Items.ToList();
            try
            {
                // The program's examples folder stands in: a PT text model, a plain one, a binary model and one too big.
                var examples = Path.Combine(root, "install", "Verification");
                Directory.CreateDirectory(Path.Combine(examples, "Design"));
                File.WriteAllText(Path.Combine(examples, "Design", "ACI 318-19 PT-SL Ex001.e2k"), "$ PT example");
                File.WriteAllText(Path.Combine(examples, "Frame.e2k"), "$ frame example");
                File.WriteAllBytes(Path.Combine(examples, "Frame.EDB"), new byte[10]);
                File.WriteAllBytes(Path.Combine(examples, "Huge.s2k"), new byte[ReferenceData.MostOne + 1]);
                var work = Path.Combine(root, "work");
                var link = new MemoryEtabs();
                ReferenceData.Items.Add(new("an item that breaks", null, _ => throw new InvalidOperationException("no such thing here")));
                ReferenceData.Items.Add(new("a SAFE-only item", p => p.Key == "safe", g => g.Write("safe only.txt", "x")));
                ReferenceData.Items.Add(new("the examples of the stand-in folder", null, g => ReferenceData.CopyExamples(g, examples)));
                var lines = ReferenceData.Run(work, CsiProgram.Etabs, link);
                var folder = Path.Combine(work, ReferenceData.FolderName, "ETABS"); // ETABS itself
                Check(File.Exists(Path.Combine(folder, "program.txt")) && File.ReadAllText(Path.Combine(folder, "program.txt")).Contains("version: "), "the program and its version are not written");
                Check(File.Exists(Path.Combine(folder, "tables.csv")) && File.ReadAllLines(Path.Combine(folder, "tables.csv")).Length > 1, "the table list is not written");
                Check(File.Exists(Path.Combine(folder, "table fields.csv")), "the fields of the tables are not written");
                Check(File.Exists(Path.Combine(folder, "Verification", "Design", "ACI 318-19 PT-SL Ex001.e2k")) && File.Exists(Path.Combine(folder, "Verification", "Frame.e2k")), "the examples' text models are not copied");
                Check(!File.Exists(Path.Combine(folder, "Verification", "Frame.EDB")) && !File.Exists(Path.Combine(folder, "Verification", "Huge.s2k")), "a binary or oversized example was copied");
                Check(File.ReadAllText(Path.Combine(folder, "verification files.csv")).Contains("Frame.EDB"), "the examples are not all listed");
                Check(File.Exists(Path.Combine(examples, "Design", "ACI 318-19 PT-SL Ex001.e2k")) && File.ReadAllText(Path.Combine(examples, "Frame.e2k")) == "$ frame example", "an original example was changed");
                Check(lines.Any(l => l.Contains("an item that breaks: not gathered (InvalidOperationException: no such thing here)")), "an item that breaks is not noted: " + string.Join(" | ", lines));
                Check(!File.Exists(Path.Combine(folder, "safe only.txt")), "an item for SAFE ran for another program");
                Check(File.Exists(Path.Combine(folder, "gathered.md")), "what was gathered is not written down");
                Check(Directory.GetFiles(Path.Combine(folder, "tables"), "*.csv").Length > 0, "the scratch model's tables with data are not written");
                Check(File.Exists(Path.Combine(folder, "scratch folder files.csv")) is false && lines.Any(l => l.Contains("the scratch model's own file is not known, and no other file is ever saved")), "a model that is not the test's own was saved, or its folder listed: " + string.Join(" | ", lines));

                // The report names them as needed things, and the zip takes them, the scratch model and the failure pictures.
                var section = ReferenceData.Section(work);
                Check(section.Contains("## Reference data (needed things, not bugs)") && section.Contains("- the program, its version and its files: program.txt"), "the report does not list the needed things: " + section);
                Equal("", ReferenceData.Section(Path.Combine(root, "nothing")), "a run that gathered nothing");
                File.WriteAllBytes(Path.Combine(work, "cpm-field-test-abc123.sdb"), new byte[100]);
                Directory.CreateDirectory(Path.Combine(work, FailurePictures.FolderName));
                File.WriteAllBytes(Path.Combine(work, FailurePictures.FolderName, FailurePictures.NameFor(3, CsiProgram.Sap2000, "tool set_lateral_load: KDS/2019?", 0)), new byte[50]);
                var report = Path.Combine(root, "Field test ETABS 2026-11-26 1900.md"); // ETABS itself
                var zip = FieldTest.Pack(report, "# report", work);
                Check(zip is not null, "no zip was written");
                using (var sent = System.IO.Compression.ZipFile.OpenRead(zip!))
                {
                    var names = sent.Entries.Select(x => x.FullName).ToList();
                    Check(names.Contains("files/Reference data/ETABS/tables.csv") && names.Contains("files/Reference data/ETABS/Verification/Design/ACI 318-19 PT-SL Ex001.e2k"), "the needed things are not in the zip: " + string.Join(", ", names)); // ETABS itself
                    Check(names.Contains("files/cpm-field-test-abc123.sdb"), "the scratch model is not in the zip");
                    Check(names.Contains("files/Failure pictures/03 SAP2000 tool set_lateral_load_ KDS_2019_.jpg"), "the failure picture is not in the zip: " + string.Join(", ", names));
                }
                Equal<int>(0, FailurePictures.Take(work, CsiProgram.Etabs, 0, "x").Count, "a picture with no scratch process");

                // Not a step: gathering adds nothing to the test's results, and the full test does it for each program.
                var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ReferenceData.cs"));
                Check(!source.Contains("ctx.Step(") && !source.Contains("ctx.Fail("), "gathering reference data is recorded as a test step");
                var test = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
                Check(test.Contains("if (finished && o.ReferenceData) GatherReference(ctx, o, program, open);") && test.Contains("ReferenceData.Run(ctx.Folder, program, open.Link, open.ModelPath, (open as ScratchProgram)?.ProcessId ?? 0)") && test.Contains("sb.Append(ReferenceData.Section(work));") && test.Contains("FailurePictures.Take(ctx.Folder, program, scratch.ProcessId, step.Name)"),
                    "the full test does not gather the needed things, list them, or picture a failure");
            }
            finally
            {
                ReferenceData.Items.Clear(); ReferenceData.Items.AddRange(items);
                Directory.Delete(root, true);
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
            // 0.4.58/0.4.59: copies left on ETABS's "Log", its file picker or its reiterate question are ended by the next run;
            // one opening the Modeler's own file is ended only when the test itself started that process.
            Check(ScratchProgram.IsLeftCopy(["ETABS Plus v23.2.0 - cpm-field-test-256eaf", "Log"]) && ScratchProgram.IsLeftCopy(["SAP2000 26.3.0 Ultimate - cpm-field-test-3795ee", "Open Model File"]), "a leftover on the Log window or the file picker is not known");
            Check(ScratchProgram.IsOwnLeftCopy(["ETABS Plus v23.2.0 - cpm-field-test", "Log"]) && !ScratchProgram.IsOwnLeftCopy(["ETABS Plus v23.2.0 - Tower A"]) && !ScratchProgram.IsOwnLeftCopy(["ETABS Plus v23.2.0 - cpm-field-test", "License Error"]), "the test's own started copy is told apart, never an engineer's model or a licence message");
            var t0 = new DateTime(2026, 11, 27, 1, 16, 0, DateTimeKind.Utc);
            Check(ScratchProgram.Started(51208, t0, [$"51208|{t0.Ticks}"]) && !ScratchProgram.Started(51208, t0.AddMinutes(5), [$"51208|{t0.Ticks}"]) && !ScratchProgram.Started(50884, t0, [$"51208|{t0.Ticks}"]), "a process the test started is known by its id and start time, not by its id alone");
            const string Api = "56164|44064|\"C:\\Program Files\\Computers and Structures\\ETABS 23\\ETABS.exe\" \"{8f0c}\" \"{9a1b}\" \"ETABSv1\" /A";
            Check(ScratchProgram.OrphanLine(Api, _ => false) == 56164 && ScratchProgram.OrphanLine(Api, _ => true) is null
                && ScratchProgram.OrphanLine("50884|7012|\"C:\\Program Files\\Computers and Structures\\ETABS 23\\ETABS.exe\"", _ => false) is null,
                "only a program started through the API whose starter is gone is an orphan, never one started from Explorer");
            Check(ScratchProgram.IsOrphanedCopy(["ETABS Plus v23.2.0 - (Untitled)  ", "Log"]) && ScratchProgram.IsOrphanedCopy(["SAP2000 v26.3.0 Plus 64-bit - (Untitled)  ", "Open Model File"])
                && !ScratchProgram.IsOrphanedCopy(["ETABS Plus v23.2.0 - Tower A"]) && !ScratchProgram.IsOrphanedCopy(["ETABS Plus v23.2.0 - (Untitled)", "License Manager"]),
                "an orphaned copy on an empty model and the test's own boxes may end; a named model or a licence message never");
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
            var passed = new FieldRetest.Part("0.4.16", "aaaa", 140, 0, [("SAFE on this PC", "SAFE was not found on this PC")]);
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

        yield return ("SAFE 22.7 tester (0.4.66): the blank model's story is at the .f2k's level, so SAFE takes its floors; a refused floor says why", () =>
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "safe22-tester-cpm-field-test.f2k"));
            Equal(3.2, CsiProMax.Assistant.Safe.F2kRebuild.StoryHeightOf(text), "the story height the blank model takes (the file's Story Height Below)");
            Equal(3.2, CsiProMax.Assistant.Safe.F2kRebuild.FloorLevelOf(text), "the floors' level");
            Equal(3.0, CsiProMax.Assistant.Safe.F2kRebuild.StoryHeightOf("TABLE:  \"PROGRAM CONTROL\"\r\n   ProgramName=SAFE\r\n"), "3 when the file names none");
            Equal(4.5, CsiProMax.Assistant.Safe.F2kRebuild.StoryHeightOf("TABLE:  \"GRID DEFINITIONS - GENERAL\"\r\n   Name=G1   \"Model Datum\"=4.5\r\n"), "the datum when no height below is given");
            var off = CsiProMax.Assistant.Safe.F2kRebuild.LevelProblem([new StoryRow("Base", 0, 0), new StoryRow("Story1", 3, 3)], 3.2);
            Check(off.Contains("Z = 3.2", StringComparison.Ordinal) && off.Contains("Story1 3", StringComparison.Ordinal), "a story at 3 m with floors at 3.2 m is named: " + off);
            Equal("", CsiProMax.Assistant.Safe.F2kRebuild.LevelProblem([new StoryRow("Base", 0, 0), new StoryRow("Story1", 3.2, 3.2)], 3.2), "a story at the floors' level is no problem");

            // SAFE refuses a floor where no story is (as the tester's SAFE 22.7 did): the build says so, with the levels.
            var memory = new MemoryEtabs { Program = CsiProgram.Safe };
            var refusing = new StoryBound(new SafeMemory(memory, program: CsiProgram.Safe) { Api = new OldProgramTests.ApiRecorder() }, [new StoryRow("Base", 0, 0), new StoryRow("Story1", 3, 3)]);
            try { CsiProMax.Assistant.Safe.F2kRebuild.Build(refusing, text); throw new Exception("the floors at 3.2 m were taken at a story at 3 m"); }
            catch (EtabsException ex) { Check(ex.Message.Contains("no story is at the floors' level", StringComparison.Ordinal) && ex.Message.Contains("S200", StringComparison.Ordinal), ex.Message); }
            var level = new StoryBound(new SafeMemory(new MemoryEtabs { Program = CsiProgram.Safe }, program: CsiProgram.Safe) { Api = new OldProgramTests.ApiRecorder() }, [new StoryRow("Base", 0, 0), new StoryRow("Story1", 3.2, 3.2)]);
            var built = CsiProMax.Assistant.Safe.F2kRebuild.Build(level, text);
            Equal(4, built["slabs"]!.GetValue<int>(), "the file's four floors at a story at their level");

            // The story moves to the file's level through the API when NewBlank put it elsewhere.
            var api = new StoryApi(3);
            var said = CsiProMax.Assistant.Safe.SafeStory.AtLevel(api, 3.2);
            Check(Math.Abs(api.Story.Elevation - 3.2) < 1e-9 && said.Contains("moved", StringComparison.Ordinal), "the story is moved to 3.2: " + said);
            Check(CsiProMax.Assistant.Safe.SafeStory.AtLevel(api, 3.2).Contains("is at the file's level", StringComparison.Ordinal), "and then left as it is");

            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.Contains("(\"TypicalStoryHeight\", height), (\"BottomStoryHeight\", height)", StringComparison.Ordinal) && scratch.Contains("F2kRebuild.StoryHeightOf(text)", StringComparison.Ordinal),
                "the blank model takes the file's story height, not 3");
            Check(scratch.Contains("SafeStory.AtLevel(m, level.Value)", StringComparison.Ordinal), "and its story is put at the file's level");
        });

        yield return ("SAFE 22.7 tester (0.4.66): when SAFE opens the Modeler's .f2k empty, the test opens other forms of it and SAFE's own export, to say why", () =>
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "safe22-tester-cpm-field-test.f2k"));
            var forms = CsiProMax.Assistant.Safe.F2kProbe.Forms(text, "22.7.0");
            Equal(4, forms.Count, "four forms of the Modeler's file");
            foreach (var (name, body) in forms)
            {
                var t = CsiTableText.Parse(body);
                Equal("Standard", t["PROGRAM CONTROL"].Single()["ProgLevel"], $"{name}: SAFE Standard, as SAFE 23.1's own PT export says");
                Equal(4, t["FLOOR OBJECT CONNECTIVITY"].Count, $"{name}: the four floors are still in it");
            }
            Equal("20.1.0", CsiTableText.Parse(forms[0].Text)["PROGRAM CONTROL"].Single()["Version"], "the first keeps the file's version");
            Equal("22.7.0", CsiTableText.Parse(forms[1].Text)["PROGRAM CONTROL"].Single()["Version"], "the second is at the running SAFE's");
            Check(forms[2].Text.StartsWith("File ", StringComparison.Ordinal) && forms[2].Text.Split("\r\n")[0].Contains(" saved ", StringComparison.Ordinal), "the third starts as SAFE's own file does");
            Check(!CsiTableText.Parse(forms[3].Text).ContainsKey("TENDON OBJECT CONNECTIVITY") && forms[3].Text.TrimEnd().EndsWith("END TABLE DATA", StringComparison.Ordinal), "the fourth has only what the slabs need");
            var own = CsiProMax.Assistant.Safe.F2kProbe.Control() ?? throw new Exception("SAFE's own export is not in CsiProMax.dll");
            Check(CsiTableText.Parse(own)["FLOOR OBJECT CONNECTIVITY"].Count > 0, "SAFE's own export, the control, has floors");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.IndexOf("Probe(ctx, name, text)", StringComparison.Ordinal) is var probe and > 0 && probe > scratch.IndexOf("if (openedEmpty)", StringComparison.Ordinal), "the probe runs after an empty open");
        });

        yield return ("SAFE 23.1 tester (0.4.68): SAFE's File.OpenFile takes no .f2k, its own export neither, so the Modeler's file goes in through SAFE's tables as File > Import does", () =>
        {
            const string seen = "SAFE 23.1.0: 1. SAFE's own export of the RC-PN example (the control): answered 0, holds 0 objects (probe-1.f2k); 2. ProgLevel Standard: answered 0, holds 0 objects (probe-2.f2k)";
            Check(CsiProMax.Assistant.ScratchProgram.ProbeControlEmpty(seen), "the tester's probe: SAFE's own export came in empty");
            Check(!CsiProMax.Assistant.ScratchProgram.ProbeControlEmpty(seen.Replace("(the control): answered 0, holds 0", "(the control): answered 0, holds 212", StringComparison.Ordinal)), "a SAFE that opens its own export is not that case");

            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "safe22-tester-cpm-field-test.f2k"));
            var api = new TablesApi(["Point Object Connectivity", "Floor Object Connectivity", "Slab Property Definitions", "Material Properties - General", "Analysis Messages"]);
            var link = new SafeMemory(new MemoryEtabs { Program = CsiProgram.Safe }, program: CsiProgram.Safe) { Api = api };
            var answer = CsiProMax.Assistant.Safe.F2kRebuild.ImportTables(link, text);
            Equal(0, answer["applied"]!.GetValue<int>(), "one apply of every staged table");
            Check(api.Staged.ContainsKey("Floor Object Connectivity") && api.Staged["Floor Object Connectivity"].Rows == 4, "the four floors go in by SAFE's own table key: " + string.Join(", ", api.Staged.Keys));
            Check(api.Staged["Floor Object Connectivity"].Fields.Contains("Unique Name") && api.Staged["Point Object Connectivity"].Fields.Contains("UniqueName"), "by the fields SAFE names");
            Check(!api.Staged.ContainsKey("Analysis Messages"), "a table SAFE does not import is not sent");
            Check(answer["not_in_program"]!.AsArray().Any(n => n!.GetValue<string>() == "TENDON OBJECT CONNECTIVITY"), "a table this SAFE does not list is named");
            Check(answer["not_importable"]!.AsArray().Count == 0 || !answer["not_importable"]!.AsArray().Any(n => n!.GetValue<string>().StartsWith("Floor", StringComparison.Ordinal)), "the floors are importable");
            Equal(1, api.Applies, "applied once");

            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.IndexOf("F2kRebuild.ImportTables(Link, text)", StringComparison.Ordinal) is var tables and > 0 && tables < scratch.IndexOf("F2kRebuild.Build(Link, text)", StringComparison.Ordinal),
                "the tables first, the object calls only when they brought nothing in");
            Check(scratch.Contains("if (probe is not null && ProbeControlEmpty(probe))", StringComparison.Ordinal), "an empty open of the Modeler's file fails only when SAFE opens its own export");
        });

        yield return ("SAFE tester (0.4.68): a field test that runs out of memory still writes its report; a table too large for one array is read a load case at a time", () =>
        {
            // A step that runs out of memory is one failure, and the steps after it still run.
            var ctx = new FieldContext(CsiProgram.Safe, Path.GetTempPath(), new MemoryEtabs { Program = CsiProgram.Safe });
            ctx.Section("memory");
            ctx.Step<string>("a step that runs out of memory", () => throw new OutOfMemoryException());
            ctx.Step("the next step", () => "ran");
            var oom = ctx.Steps.Single(s => s.Name == "a step that runs out of memory");
            Check(!oom.Ok && oom.Detail.StartsWith("OutOfMemoryException", StringComparison.Ordinal) && oom.Detail.Contains("last table read", StringComparison.Ordinal), oom.Detail);
            Check(ctx.Steps.Single(s => s.Name == "the next step").Ok, "the run went on");

            var field = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(field.Contains("catch (OutOfMemoryException)", StringComparison.Ordinal) && field.IndexOf("catch (OutOfMemoryException)", StringComparison.Ordinal) < field.IndexOf("var report = Render(", StringComparison.Ordinal),
                "out of memory outside a step still reaches the report");
            var com = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/ComEtabs.cs"));
            Check(com.Contains("catch (OutOfMemoryException) { return ReadInPages(key, cases, combos); }", StringComparison.Ordinal), "a table read that runs out of memory is read in pages");
            Check(com.Contains("catch (OutOfMemoryException ex) { done.SetException(ex); }", StringComparison.Ordinal), "and out of memory is not wrapped on the program's thread, so the pages are tried");
            Check(com.Contains("ReadTableNow(key, isCase ? [one] : [], isCase ? [] : [one])", StringComparison.Ordinal), "one load case or combination a page");
        });

        yield return ("SAFE reference data (for 0.4.71): the testers' zips bring SAFE's own .f2k round trip with its differences, even when the scratch window stopped answering", () =>
        {
            var a = File.ReadAllText(Path.Combine(SourceText.Root(), "tests/CsiProMax.Tests/samples/safe-RC-PN-v23.f2k"));
            Check(SafeRoundTrip.Count(a, a) == "the same in every table, row and field", "a file against itself");
            var edited = SafeRoundTrip.Edited(a);
            Check(edited is not null && edited.Contains("\"Self Weight Multiplier\"=0.5", StringComparison.Ordinal), "the first pattern's self weight is set to 0.5");
            var diff = SafeRoundTrip.Diff(a, edited!, "A", "D");
            Check(diff.Contains("<!-- 1 tables, 1 rows, 1 fields differ -->", StringComparison.Ordinal) && diff.Contains("## LOAD PATTERN DEFINITIONS", StringComparison.Ordinal), diff[..Math.Min(400, diff.Length)]);
            var newGuids = System.Text.RegularExpressions.Regex.Replace(a, "GUID=[0-9a-f-]+", "GUID=x");
            Check(SafeRoundTrip.Count(a, newGuids) == "the same in every table, row and field", "a new GUID on export is no difference");
            Check(SafeRoundTrip.Texty("TextFile") && SafeRoundTrip.Texty("SAFEF2k") && !SafeRoundTrip.Texty("DBTablesText") && !SafeRoundTrip.Texty("DBTablesExcel"), "the text file types are tried, not the table exports");
            Check(ReferenceData.Items.Any(i => i.Run == SafeRoundTrip.Gather && i.For!(CsiProgram.Safe) && !i.For(CsiProgram.Etabs)), "the round trip is gathered from SAFE");
            Check(ReferenceData.Items.Last().Name.StartsWith("the program's defaults", StringComparison.Ordinal), "and the blank defaults stay last");
            var field = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(field.Contains("if (finished && o.ReferenceData) GatherReference(ctx, o, program, open);", StringComparison.Ordinal)
                && field.Contains("fresh = o.StartScratch(program, quiet, copy);", StringComparison.Ordinal), "a scratch window that stopped answering gives way to a fresh one for the reference data");
        });

        yield return ("SAFE tester (0.4.69): the .f2k's fields go in by the names SAFE shows, strips by SAFE 23.1's point table, a part import starts again, one API file loads once", () =>
        {
            // SAFE gives field keys and the names it shows; the .f2k uses the shown names.
            string[] keys = ["Name", "Thickness", "SlabType"], shown = ["Name", "Slab Thickness", "Property Type"];
            Check(CsiProMax.Assistant.Safe.F2kRebuild.FieldKeyOf(keys, shown, "Slab Thickness") == "Thickness", "a shown name finds its key");
            Check(CsiProMax.Assistant.Safe.F2kRebuild.FieldKeyOf(keys, shown, "Property Type") == "SlabType", "another shown name finds its key");
            Check(CsiProMax.Assistant.Safe.F2kRebuild.FieldKeyOf(keys, shown, "name") == "Name", "a key itself still matches");
            Check(CsiProMax.Assistant.Safe.F2kRebuild.FieldKeyOf(["UniqueName"], ["Unique Name"], "UniqueName") == "UniqueName", "and without spaces");
            Check(CsiProMax.Assistant.Safe.F2kRebuild.FieldKeyOf(keys, shown, "Colour") is null, "a field SAFE does not have is left out");

            // SAFE 23.1's strip table names point objects: one row per segment, points placed by the program.
            var text = File.ReadAllText(Path.Combine(SourceText.Root(), "tests/CsiProMax.Tests/samples/safe22-tester-cpm-field-test.f2k"));
            string[] safe23 = ["Name", "NumSegs", "StartPoint", "EndPoint", "WStartLeft", "WStartRight", "WEndLeft", "WEndRight", "AutoWiden", "Layer", "GUID"];
            var asked = new List<string>();
            var rows = CsiProMax.Assistant.Safe.F2kRebuild.StripRows(text, safe23, (x, y, z) => { asked.Add($"{x},{y},{z}"); return "P" + asked.Count; });
            Check(rows.Count is > 0 && rows.All(r => r.ContainsKey("StartPoint") && r.ContainsKey("EndPoint") && r["StartPoint"].StartsWith('P')), string.Join(" | ", rows.Select(r => string.Join(",", r))));
            Check(rows[0]["Name"] == "CSA1" && rows[0]["WStartLeft"] == "2.5" && rows[0]["NumSegs"] == "1" && rows[0]["Layer"] == "A", string.Join(",", rows[0]));
            Check(asked[0] == "0,0,3.2", "the point is asked for at the file's point's place: " + asked[0]);
            Check(asked.Count == asked.Distinct().Count(), "a point two strips share is asked for once");

            Check(!ScratchProgram.TablesBroughtAll(18, CsiProMax.Assistant.Safe.F2kRebuild.ObjectCount(text) + 1) && ScratchProgram.TablesBroughtAll(5, 5) && !ScratchProgram.TablesBroughtAll(0, 0), "a part import is built again");
            var scratch = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Field/ScratchProgram.cs"));
            Check(scratch.Contains("if (held > 0) again = StartBlank();", StringComparison.Ordinal), "on a blank model, so nothing is there twice");

            // The same API file from two folders loads once ("Assembly with same name is already loaded").
            var from = Path.Combine(AppContext.BaseDirectory, "XamlMath.Shared.dll");
            var a = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cpm-api-a-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            var b = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cpm-api-b-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            File.Copy(from, Path.Combine(a, "XamlMath.Shared.dll")); File.Copy(from, Path.Combine(b, "XamlMath.Shared.dll"));
            var first = CsiProMax.Assistant.Etabs.EtabsApi.Load(Path.Combine(a, "XamlMath.Shared.dll"));
            var second = CsiProMax.Assistant.Etabs.EtabsApi.Load(Path.Combine(b, "XamlMath.Shared.dll"));
            Check(first.GetName().Name == second.GetName().Name, "the second folder's copy gives the loaded one or its own, never an error");
            foreach (var file in new[] { "Etabs/EtabsApi.cs", "Field/ScratchProgram.cs", "Tools/SapFreshProcess.cs", "Field/ReferenceData.cs" })
                Check(!File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant", file)).Replace("try { return Assembly.LoadFrom(apiFile); }", "").Contains("LoadFrom(api", StringComparison.Ordinal), file + " loads the API through EtabsApi.Load");
        });

        yield return ("Out of memory box (0.4.68): a large model's results table is read one load case or combination at a time from the start, with repeated values kept once", () =>
        {
            Check(ComEtabs.IsCaseTable(["Story", "Joint", "OutputCase", "CaseType", "UX"]), "a table with OutputCase is read by case");
            Check(ComEtabs.IsCaseTable(["Frame", "Station", "Combo", "P"]), "a table with Combo is read by case");
            Check(!ComEtabs.IsCaseTable(["Story", "Height", "Elevation"]), "a table without cases is read whole");
            Check(!ComEtabs.PagesNeeded(1_000, 10, 20), "a small table stays one read");
            Check(ComEtabs.PagesNeeded(50_000, 20, 10), "one case of 50,000 rows x 20 fields over 10 cases is read in pages");
            var repeats = new Repeats(2);
            var a = repeats.Kept(0, new string("DEAD".ToCharArray()));
            var b = repeats.Kept(0, new string("DEAD".ToCharArray()));
            Check(ReferenceEquals(a, b), "a value repeated down a column is kept once");
            for (var i = 0; i < 5000; i++) repeats.Kept(1, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var c = repeats.Kept(1, new string("x1".ToCharArray()));
            var d = repeats.Kept(1, new string("x1".ToCharArray()));
            Check(!ReferenceEquals(c, d), "a column of mostly distinct numbers stops being tracked");
            var com = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/ComEtabs.cs"));
            Check(com.IndexOf("if (Paged(key, cases, combos) is { } paged) return paged;", StringComparison.Ordinal) is var paged and > 0
                && paged < com.IndexOf("return ReadTableKept(key, cases, combos);", StringComparison.Ordinal), "the size is checked before any whole-table read");
        });

        yield return ("SAFE tester (0.4.68): a field test started from a Modeler window with no program expects the Modeler to name itself in get_model_context", () =>
        {
            var self = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/SelfTest.cs"));
            Check(self.Contains("var expected = etabs is Etabs.OfflineEtabs ? \"CSI ProMax Modeler\" : program;", StringComparison.Ordinal), "the Modeler's own name is expected there");
            var tools = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Tools/EtabsTools.cs"));
            Check(tools.Contains("[\"program\"] = e is OfflineEtabs ? \"CSI ProMax Modeler\" : e.Program.Name", StringComparison.Ordinal), "which is what get_model_context says for it");
        });

        yield return ("SAFE 22.7 tester (0.4.66): one call the engineer's program never came back from does not fail every later tool; the report gives a failure no skip reason", () =>
        {
            var com = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/Etabs/ComEtabs.cs"));
            Check(com.Contains("if (_stuck is { Length: > 0 } stuck && !Renew())", StringComparison.Ordinal), "a stuck link tries a fresh thread and attach before it says no");
            Check(com.Contains("if (_renewed || _start is not null || _work.IsAddingCompleted) return false;", StringComparison.Ordinal), "once per stuck call, never for a scratch window the test started");
            Check(com.Contains("Packs.PackWatcher.Responding(pid)", StringComparison.Ordinal), "only while the program's window still answers Windows");
            Check(com.Contains("if (generation == Volatile.Read(ref _generation)) _stuck = \"\";", StringComparison.Ordinal), "a late answer on the old thread does not clear a new stuck call");
            Check(com.Contains("+ _stuckSeen", StringComparison.Ordinal), "the message names the program's windows then");
            Check(!com.Contains("lock (_work)", StringComparison.Ordinal), "the read session's lock is not the queue that is replaced");
            var field = File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Assistant/FieldTest.cs"));
            Check(field.Contains("Listed(\"Not working\", bad, false);", StringComparison.Ordinal), "a failure is listed without a skip reason ('[the program's licence]' for a failure that mentions a licence notice)");
            Check(field.Contains("the open {link.Program.Name} window answers before CSI ProMax's own features", StringComparison.Ordinal), "the open program not answering is said once, before the steps that would each fail on it");
            Equal("SAFE has still not come back", CsiProMax.Assistant.FieldTest.Unanswered(new Silent(new MemoryEtabs { Program = CsiProgram.Safe })), "the reason is the program's own words");
            Equal<string?>(null, CsiProMax.Assistant.FieldTest.Unanswered(new MemoryEtabs { Program = CsiProgram.Safe }), "an answering program has none");
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

    /// <summary>SAFE as the tester's SAFE 22.7 answered: a floor is refused where no story is.</summary>
    sealed class StoryBound(ICsiLink inner, IReadOnlyList<StoryRow> stories) : Forward(inner)
    {
        public override IReadOnlyList<StoryRow> Stories() => stories;
        public override string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) =>
            corners.All(c => Math.Abs(c.Z - corners[0].Z) < 1e-9) && !stories.Any(s => Math.Abs(s.Elevation - corners[0].Z) < 1e-4)
                ? throw new EtabsException($"SAFE refused to add an area with section {section}.") : base.AddArea(corners, section);
    }

    /// <summary>A program that never came back from a call.</summary>
    sealed class Silent(ICsiLink inner) : Forward(inner)
    {
        public override ModelInfo Info() => throw new EtabsException("SAFE has still not come back");
    }

    /// <summary>SAFE's cStory as far as SafeStory asks it: one story, moved by SetElevation.</summary>
    public sealed class StoryApi(double elevation)
    {
        public Stories Story { get; } = new() { Elevation = elevation };
        public sealed class Stories
        {
            public double Elevation;
            public int GetStories_2(ref double BaseElevation, ref int NumberStories, ref string[] StoryNames, ref double[] StoryElevations, ref double[] StoryHeights,
                ref bool[] IsMasterStory, ref string[] SimilarToStory, ref bool[] SpliceAbove, ref double[] SpliceHeight, ref int[] color)
            {
                BaseElevation = 0; NumberStories = 1; StoryNames = ["Story1"]; StoryElevations = [Elevation]; StoryHeights = [Elevation];
                IsMasterStory = [true]; SimilarToStory = ["None"]; SpliceAbove = [false]; SpliceHeight = [0]; color = [0];
                return 0;
            }
            public int SetElevation(string Name, double Elevation) { this.Elevation = Elevation; return 0; }
        }
    }

    /// <summary>A link that passes everything on, for a stand-in that changes one answer.</summary>
    class Forward(ICsiLink inner) : ICsiLink
    {
        public CsiProgram Program => inner.Program;
        public virtual ModelInfo Info() => inner.Info();
        public virtual IReadOnlyList<StoryRow> Stories() => inner.Stories();
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
        public virtual string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => inner.AddArea(corners, section);
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

/// <summary>SAFE's interactive database as far as ImportTables asks it: the tables it lists, each importable but Analysis Messages.</summary>
public sealed class TablesApi(string[] tables)
{
    public Dictionary<string, (string[] Fields, int Rows)> Staged { get; } = [];
    public int Applies;
    public TablesApi DatabaseTables => this;
    public int GetPresentUnits() => 6;
    public int GetAllTables(ref int NumberTables, ref string[] TableKey, ref string[] TableName, ref int[] ImportType, ref bool[] IsEmpty)
    {
        TableKey = tables; TableName = tables; NumberTables = tables.Length;
        ImportType = tables.Select(t => t == "Analysis Messages" ? 0 : 2).ToArray(); IsEmpty = tables.Select(_ => true).ToArray();
        return 0;
    }
    public int GetAllFieldsInTable(string TableKey, ref int TableVersion, ref int NumberFields, ref string[] FieldKey, ref string[] FieldName, ref string[] Description, ref string[] UnitsString, ref bool[] IsImportable)
    {
        FieldKey = TableKey switch
        {
            "Point Object Connectivity" => ["UniqueName", "Is Auto Point", "IsSpecial", "X", "Y", "Z", "GUID"],
            "Floor Object Connectivity" => ["Unique Name", "UniquePt1", "UniquePt2", "UniquePt3", "UniquePt4", "GUID"],
            _ => ["Name", "Material", "Type", "Modeling Type", "Property Type", "Slab Thickness", "SymType"],
        };
        FieldName = FieldKey; NumberFields = FieldKey.Length; IsImportable = FieldKey.Select(_ => true).ToArray();
        return 0;
    }
    public int SetTableForEditingArray(string TableKey, ref int TableVersion, ref string[] FieldsKeysIncluded, int NumberRecords, ref string[] TableData)
    {
        if (TableData.Length != FieldsKeysIncluded.Length * NumberRecords) return 1;
        Staged[TableKey] = (FieldsKeysIncluded, NumberRecords);
        return 0;
    }
    public int ApplyEditedTables(bool FillImportLog, ref int NumFatalErrors, ref int NumErrorMsgs, ref int NumWarnMsgs, ref int NumInfoMsgs, ref string ImportLog) { Applies++; ImportLog = "ok"; return 0; }
    public int CancelTableEditing() => 0;
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
