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
            Check(source.Contains("ScratchDialogs.CloseNotices(ProcessId)"), "the scratch window no longer watches for the notice");
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
