using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Safe;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.F2k;
using CsiProMax.Model;

/// <summary>The SAFE adapter's checks: the table text, the .f2k writer, SAFE's tools and checks, and the wiring.</summary>
static class SafeTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static JsonNode Run(string tool, string json, ICsiLink e) => SafeTools.All.First(t => t.Name == tool).Run(Args(json), e);

    /// <summary>The demo model, said to be SAFE, with SAFE's result tables.</summary>
    internal static SafeMemory Safe()
    {
        var m = new MemoryEtabs();
        m.AddArea([(0, 0, 0), (12, 0, 0), (12, 5, 0), (0, 5, 0)], "S200");
        m.Locked = true;
        m.ResultTables["Concrete Slab Design - Punching Shear Data"] = new(["Point", "GlobalX", "GlobalY", "Location", "Status", "Ratio", "Combo"],
        [
            ["11", "0", "0", "Corner", "OK", "0.62", "ULS1"],
            ["12", "6", "0", "Edge", "OK", "1.18", "ULS1"],
            ["13", "6", "5", "Interior", "OK", "0.94", "ULS2"],
            ["14", "12", "5", "Edge", "N/C", "", "ULS1"],
        ]);
        m.ResultTables["Soil Pressures"] = new(["Point", "GlobalX", "GlobalY", "OutputCase", "CaseType", "Pressure"],
        [
            ["1", "0", "0", "SLS", "Combination", "-120.5"], ["2", "6", "0", "SLS", "Combination", "-182.25"], ["3", "12", "0", "SLS", "Combination", "0.4"],
            ["1", "0", "0", "Dead", "LinStatic", "-80"],
        ]);
        m.ResultTables["Joint Displacements"] = new(["UniqueName", "OutputCase", "CaseType", "Ux", "Uy", "Uz"],
        [
            ["1", "SLS", "Combination", "0", "0", "-0.004"], ["2", "SLS", "Combination", "0", "0", "-0.031"], ["2", "Dead", "LinStatic", "0", "0", "-0.012"],
        ]);
        m.ResultTables["Strip Forces"] = new(["Strip", "Station", "OutputCase", "P", "V2", "T", "M2", "M3"],
        [
            ["CSA1", "0", "ULS1", "0", "45", "0", "-60.5", "0"], ["CSA1", "3", "ULS1", "0", "-5", "0", "38.2", "0"], ["CSA1", "6", "ULS1", "0", "-50", "0", "-72", "0"],
            ["CSB2", "0", "ULS1", "0", "30", "0", "-20", "0"],
        ]);
        m.ResultTables["Concrete Slab Design - Strip Based Design"] = new(["Strip", "SpanID", "Location", "FTopCombo", "AsTop", "AsBot", "Status"],
        [
            ["CSA1", "Span 1", "Start", "ULS1", "0.00085", "0.0004", "OK"], ["CSA1", "Span 1", "End", "ULS1", "0.0021", "0.0004", "Exceeds max steel"],
        ]);
        return new SafeMemory(m);
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("SAFE: table text writes and reads back, quotes and continuations", () =>
        {
            var text = CsiTableText.Document("File x", [CsiTableText.Table("Program Control", [[("ProgramName", "SAFE"), ("CurrUnits", "KN, m, C"), ("Empty", "")]])]);
            Check(text.Contains("TABLE:  \"PROGRAM CONTROL\"\r\n   ProgramName=SAFE   CurrUnits=\"KN, m, C\"   Empty=\"\""), text);
            Check(text.EndsWith("END TABLE DATA\r\n"), "ends the data");
            var back = CsiTableText.Parse(text + "TABLE: \"X\"\n   A=1   B=\"two words\" _\n   C=3\n");
            Equal("KN, m, C", back["program control"][0]["CurrUnits"], "quoted value comes back without quotes");
            Check(!back.ContainsKey("X"), "nothing after END TABLE DATA is read");
            var more = CsiTableText.Parse("TABLE: \"X\"\n   A=1   B=\"two words\" _\n   C=3\n");
            Equal("3", more["X"][0]["C"], "a line ending in _ carries on");
            Equal("two words", more["X"][0]["B"], "spaces inside quotes");
            Equal("1.5", CsiTableText.Number(1.5), "numbers"); Equal("0", CsiTableText.Number(-1e-15), "tiny is zero");
        });

        yield return ("SAFE: .f2k of a floor has its slabs, beams, columns as supports, strips, loads and combos", () =>
        {
            var m = Templates.ConcreteFrame("T", [6, 6], [5], 3, 3.5);
            m.AddCombo("1.2D+1.6L", ("Dead", 1.2), ("Live", 1.6));
            var r = F2kWriter.Write(m, new F2kOptions(Story: "Story2"));
            var t = CsiTableText.Parse(r.Text);
            Equal("SAFE", t["PROGRAM CONTROL"][0]["ProgramName"], "program");
            Equal(2, r.Slabs, "slabs on the story");
            Equal(7, r.Beams, "beams on the story");
            Equal(12, r.Columns, "columns below and above as supports");
            Equal(2, t["OBJECT GEOMETRY - AREAS 01 - GENERAL"].Count, "area rows");
            Check(t["OBJECT GEOMETRY - AREAS 01 - GENERAL"].All(a => a["NumPoints"] == "4" && a.ContainsKey("Point4")), "each area names its corners");
            var points = t["OBJECT GEOMETRY - POINT COORDINATES"].ToDictionary(p => p["Point"], p => double.Parse(p["GlobalZ"], System.Globalization.CultureInfo.InvariantCulture));
            Check(points.Values.Contains(-3.5) && points.Values.Contains(3.5), "columns reach a story down and up");
            var restrained = t["RESTRAINT ASSIGNMENTS"].Select(x => x["Point"]).ToHashSet();
            Check(restrained.All(p => Math.Abs(points[p]) > 1), "the columns' far ends hold the slab");
            Equal(12, restrained.Count, "every column end is restrained");
            Equal(r.Strips, t["OBJECT GEOMETRY - DESIGN STRIPS"].Select(x => x["Strip"]).Distinct().Count(), "strips");
            Equal(5, r.Strips, "a strip per grid line (3 along X, 2 along Y)");
            Check(t["OBJECT GEOMETRY - DESIGN STRIPS"].Any(x => x["Layer"] == "A") && t["OBJECT GEOMETRY - DESIGN STRIPS"].Any(x => x["Layer"] == "B"), "two layers");
            Equal(2, t["LOAD PATTERNS"].Count, "patterns");
            Equal(2, t["LOAD COMBINATIONS"].Count, "a combination row per case");
            Equal("Linear Add", t["LOAD COMBINATIONS"][0]["Type"], "combination type");
            Check(t.ContainsKey("SLAB PROPERTIES 01 - GENERAL") && t.ContainsKey("BEAM PROPERTIES 01 - GENERAL") && t.ContainsKey("COLUMN PROPERTIES 01 - GENERAL"), "properties");
            Check(t["MATERIAL PROPERTIES 03 - CONCRETE"].Count >= 1, "concrete");
            Check(!t.ContainsKey("SOIL PROPERTIES"), "no soil unless asked");
        });

        yield return ("SAFE: .f2k of a mat has soil under every slab and says what it left out", () =>
        {
            var m = Templates.ConcreteFrame("Mat", [6], [5], 1, 3);
            var r = F2kWriter.Write(m, new F2kOptions(Story: "Base", Subgrade: 20000));
            var t = CsiTableText.Parse(r.Text);
            Equal("Compression Only", t["SOIL PROPERTIES"][0]["NonlinOpt"], "soil pushes only");
            Equal("20000", t["SOIL PROPERTIES"][0]["Subgrade"], "subgrade");
            Check(r.Notes.Any(n => n.Contains("no slabs")), "the base has no slabs: said");
            var top = F2kWriter.Write(m, new F2kOptions(Subgrade: 20000, Strips: false));
            Equal("Story1", top.Story, "default: the story with most slab");
            Equal(0, top.Strips, "strips off");
            Check(CsiTableText.Parse(top.Text)["SOIL PROPERTY ASSIGNMENTS"].Count == top.Slabs, "every slab gets the soil");
            try { F2kWriter.Write(m, new F2kOptions(Story: "Nope")); throw new Exception("a missing story was accepted"); }
            catch (ArgumentException ex) { Check(ex.Message.Contains("Stories:"), ex.Message); }
        });

        yield return ("SAFE: the Modeler saves a floor as .f2k and never overwrites", () =>
        {
            var folder = Directory.CreateTempSubdirectory("cpm-safe").FullName;
            var o = new OfflineEtabs(Templates.ConcreteFrame("Job", [6], [5], 2, 3));
            var tool = SafeModelerTools.All.Single();
            var first = tool.Run(Args($$"""{"folder":{{JsonSerializer.Serialize(folder)}},"name":"floor"}"""), o);
            var second = tool.Run(Args($$"""{"folder":{{JsonSerializer.Serialize(folder)}},"name":"floor"}"""), o);
            Check(File.Exists((string)first["file"]!) && (string)first["file"]! != (string)second["file"]!, "a second save gets its own name");
            Check(File.ReadAllText((string)first["file"]!).Contains("PROGRAM CONTROL"), "SAFE table text");
            try { tool.Run(Args("{}"), new MemoryEtabs()); throw new Exception("ran outside the Modeler"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("Modeler"), ex.Message); }
            Directory.Delete(folder, true);
        });

        yield return ("SAFE: punching shear from SAFE's table, with the failing and unchecked points", () =>
        {
            var r = Run("check_punching", "{}", Safe());
            Equal("fail", (string)r["status"]!, "a ratio above 1.0 fails");
            Equal("12", (string)r["largest"]!["point"]!, "the governing point");
            Equal(1, (int)r["above_limit"]!, "one above");
            Equal(1, r["not_checked_or_warning"]!.AsArray().Count, "the point SAFE could not check is listed apart");
            Equal("pass", (string)Run("check_punching", """{"limit":1.2}""", Safe())["status"]!, "the engineer's limit");
            Equal("not available", (string)Run("check_punching", "{}", new SafeMemory(new MemoryEtabs()))["status"]!, "no table: not available");
        });

        yield return ("SAFE: soil pressure, deflection and strip forces", () =>
        {
            var e = Safe();
            var soil = Run("check_soil_pressure", """{"allowable":150}""", e);
            Equal("fail", (string)soil["status"]!, "182 over 150");
            var sls = soil["cases"]!.AsArray().First(c => (string)c!["case"]! == "SLS")!;
            Equal(182.25, (double)sls["largest_bearing"]!, "bearing is compression, positive");
            Equal(1, (int)sls["points_lifting_off"]!, "lift-off counted");
            Equal("reported", (string)Run("check_soil_pressure", "{}", e)["status"]!, "no allowable: no verdict");
            var d = Run("check_deflection", """{"span":6,"ratio":240}""", e);
            Equal("fail", (string)d["status"]!, "31 mm over 25 mm");
            Equal(0.031, (double)d["cases"]![0]!["largest_down"]!, "largest down first");
            var s = Run("strip_forces", "{}", e);
            var csa1 = s["strips"]!.AsArray().First(x => (string)x!["strip"]! == "CSA1")!;
            Equal(-72.0, (double)csa1["M2_min"]!["value"]!, "hogging moment");
            Equal(38.2, (double)csa1["M2_max"]!["value"]!, "sagging moment");
            Equal(-50.0, (double)csa1["V2_largest"]!["value"]!, "largest shear by size");
            Equal(1, Run("strip_forces", """{"strips":["CSB2"]}""", e)["strips"]!.AsArray().Count, "strip filter");
        });

        yield return ("SAFE: slab design and the check report", () =>
        {
            var e = Safe();
            var design = Run("slab_design", "{}", e);
            Equal("fail", (string)design["status"]!, "a row that exceeds max steel");
            Equal(1, (int)design["tables"]![0]!["not_ok"]!, "one row not OK");
            Equal(0.0021, (double)design["tables"]![0]!["largest_reinforcement"]!["AsTop"]!["value"]!, "largest top steel");
            var report = SafeTools.Report(Args("""{"allowable":200,"span":6,"ratio":240}"""), e);
            Equal(5, report["summary"]!.AsArray().Count, "five checks");
            Equal("pass", (string)report["checks"]!["soil_pressure"]!["status"]!, "the allowable is passed on");
            Equal("fail", (string)report["checks"]!["deflection"]!["status"]!, "span and ratio are passed on");
            Equal("SAFE", (string)report["program"]!, "says it is SAFE");
            var overview = Run("safe_results_overview", "{}", e);
            Equal(1.18, (double)overview["at_a_glance"]!["largest_punching_ratio"]!, "overview");
        });

        yield return ("SAFE: tools that call SAFE's API say so plainly without SAFE", () =>
        {
            var e = new SafeMemory(new MemoryEtabs());
            try { Run("add_slab_section", """{"name":"MAT1","type":"mat","material":"C30","thickness":0.8}""", e); throw new Exception("took an unknown material"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("no material"), ex.Message); }
            try { Run("add_soil_support", """{"subgrade":0}""", e); throw new Exception("took a zero modulus"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("more than 0"), ex.Message); }
            try { Run("edit_safe_table", """{"table":"Nope","action":"read"}""", e); throw new Exception("found a missing table"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("list_tables"), ex.Message); }
            try { Run("list_design_strips", "{}", e); throw new Exception("the demo has no SAFE"); }
            catch (EtabsException) { }
        });

        yield return ("SAFE: offered in SAFE only, with its own words and pack tables", () =>
        {
            Check(CsiProgram.Safe.Ready, "SAFE is switched on");
            Equal(CsiProgram.Safe, CsiProgram.Find("SAFE"), "found by its key");
            Check(ProgramTools.For(CsiProgram.Safe).Any(t => t.Name == "check_punching"), "SAFE's tools");
            Equal(0, ProgramTools.For(CsiProgram.Etabs).Count, "ETABS has only the shared ones");
            var names = SafeTools.All.Select(t => t.Name).Concat(SafeModelerTools.All.Select(t => t.Name)).ToList();
            Check(names.Distinct().Count() == names.Count, "no tool named twice");
            Check(!names.Intersect(EtabsTools.All.Select(t => t.Name)).Any() && !names.Intersect(ModelerTools.All.Select(t => t.Name)).Any(), "no clash with the shared tools");
            var registry = CsiProMax.Tools.ToolRegistry.CreateDefault(false, CsiProgram.Safe);
            Check(registry.IsBuiltIn("check_soil_pressure") && registry.IsBuiltIn("get_model_context"), "SAFE's and the shared tools in SAFE");
            Check(!CsiProMax.Tools.ToolRegistry.CreateDefault(false, CsiProgram.Etabs).IsBuiltIn("check_punching"), "not in ETABS");
            Check(CsiProMax.Tools.ToolRegistry.CreateDefault(true).IsBuiltIn("save_safe_file"), "the Modeler writes SAFE files");
            Check(SafePack.Takes("Concrete Slab Design - Punching Shear Data") && SafePack.Takes("Soil Pressures") && SafePack.Takes("Strip Forces") && !SafePack.Takes("Story Drifts"), "pack tables");
            Equal("SAFE", (string)ProgramTools.Report(Args("{}"), new SafeMemory(new MemoryEtabs()))["program"]!, "SAFE's report in the pack");
            Check(ProgramTools.Report(Args("{}"), new MemoryEtabs())["checks"]!["drift"] is not null, "ETABS's report in ETABS");
            Check(SafePrompt.Text.Contains("check_punching") && SafePrompt.Text.Contains("not ETABS"), "SAFE's prompt");
            foreach (var tool in SafeTools.All.Concat(SafeModelerTools.All)) JsonDocument.Parse(tool.Schema);
        });

        yield return ("SAFE: the review pack says SAFE and takes SAFE's tables", () =>
        {
            var e = Safe();
            var built = Pack.Build(e, [], [], [], SafeTools.Report(Args("{}"), e).AsObject(), null);
            Equal("SAFE", (string)built.Content["manifest"]!["program"]!, "program in the manifest");
            Check(built.Tables.Contains("Soil Pressures") && built.Tables.Contains("Strip Forces") && built.Tables.Contains("Concrete Slab Design - Punching Shear Data"), string.Join(", ", built.Tables));
            var html = Pack.ViewerHtml(built.Content);
            if (Environment.GetEnvironmentVariable("CPM_VIEWER_OUT") is { Length: > 0 } output) File.WriteAllText(output, html);
            Check(html.Contains("value=\"punching\"") && html.Contains("readMarks"), "the Viewer draws punching ratios and soil pressure");
        });

        yield return ("SAFE: the plug-in starts the assistant for SAFE", () =>
        {
            var root = Directory.CreateTempSubdirectory("cpm").FullName;
            var folder = Directory.CreateDirectory(Path.Combine(root, "CSI ProMax")).FullName;
            File.WriteAllText(Path.Combine(folder, "CsiProMax.dll"), "");
            var dotnet = Path.Combine(root, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            Directory.CreateDirectory(Path.GetDirectoryName(dotnet)!); File.WriteAllText(dotnet, "");
            var start = CsiProMaxPlugin.Launcher.StartInfo(folder, @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.29", 9, "SAFE", [Path.Combine(root, "dotnet")]);
            Check(start.Arguments.EndsWith("--etabs-pid 9 --program safe"), start.Arguments);
            Equal(CsiProgram.Safe, CsiProgram.Find("safe"), "the assistant knows the name it is given");
            Directory.Delete(root, true);
        });
    }
}

/// <summary>A link that is the demo model but says it is SAFE.</summary>
sealed class SafeMemory(MemoryEtabs inner) : ICsiLink
{
    public CsiProgram Program => CsiProgram.Safe;
    /// <summary>Given: the only tables this SAFE lists, and any other is refused as SAFE refuses a table it does not have.</summary>
    public IReadOnlyList<TableName>? Listed { get; init; }
    /// <summary>Given: what stands in for SAFE's own API in the calls made through Raw.</summary>
    public object? Api { get; init; }
    public ModelInfo Info() => inner.Info() with { File = @"C:\Models\mat.FDB", Version = "22.0.0" };
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
    public IReadOnlyList<TableName> Tables() => Listed ?? inner.Tables();
    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) =>
        Listed is null || Listed.Any(t => t.Key == key) ? inner.ReadTable(key, cases, combos)
            : throw new EtabsException($"SAFE refused to read the table '{key}' (check its name with the table list).");
    public object? Raw(string what, Func<dynamic, object?> work) => Api is null ? inner.Raw(what, work) : work(Api);
    public void RunAnalysis() => inner.RunAnalysis();
    public void Save() => inner.Save();
    public void Refresh() => inner.Refresh();
}
