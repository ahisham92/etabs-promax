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

    /// <summary>A raft 12 m by 6 m in 3 m cells as GeoProMax writes it: soft at the ends (1000 kN/m3), stiff inside (2000).</summary>
    const string SpringsFile = """
        {"format":"geopromax-springs","version":1,"project":"Raft","round":1,"converged":false,"time_years":50,"founding_depth":1,
         "units":{"length":"m","force":"kN","pressure":"kN/m2","modulus":"kN/m3"},"cell_size":3,
         "zones":[{"name":"GPM-K1","modulus":1000,"from":900,"to":1100,"cells":4},{"name":"GPM-K2","modulus":2000,"from":1900,"to":2100,"cells":4}],
         "cells":[{"x":1.5,"y":1.5,"pressure":100,"settlement":0.1,"modulus":1000,"zone":"GPM-K1"},{"x":4.5,"y":1.5,"pressure":100,"settlement":0.05,"modulus":2000,"zone":"GPM-K2"},
                  {"x":7.5,"y":1.5,"pressure":100,"settlement":0.05,"modulus":2000,"zone":"GPM-K2"},{"x":10.5,"y":1.5,"pressure":100,"settlement":0.1,"modulus":1000,"zone":"GPM-K1"},
                  {"x":1.5,"y":4.5,"pressure":100,"settlement":0.1,"modulus":1000,"zone":"GPM-K1"},{"x":4.5,"y":4.5,"pressure":100,"settlement":0.05,"modulus":2000,"zone":"GPM-K2"},
                  {"x":7.5,"y":4.5,"pressure":100,"settlement":0.05,"modulus":2000,"zone":"GPM-K2"},{"x":10.5,"y":4.5,"pressure":100,"settlement":0.1,"modulus":1000,"zone":"GPM-K1"}]}
        """;

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
            Check(ProgramTools.For(CsiProgram.Etabs).SequenceEqual(GeoProMaxTools.All) && GeoProMaxTools.All.All(t => ProgramTools.For(CsiProgram.Safe).Contains(t)), "ETABS adds only the link with GeoProMax, and SAFE has it too");
            Check(!ProgramTools.For(CsiProgram.Sap2000).Any(t => t.Name == "apply_geopromax_springs"), "not in SAP2000");
            var names = ProgramTools.For(CsiProgram.Safe).Select(t => t.Name).Concat(SafeModelerTools.All.Select(t => t.Name)).ToList();
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
            foreach (var tool in ProgramTools.For(CsiProgram.Safe).Concat(SafeModelerTools.All)) JsonDocument.Parse(tool.Schema);
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

        yield return ("GeoProMax: the springs file becomes soil zones under the raft's panels, in the model's units", () =>
        {
            var folder = Directory.CreateTempSubdirectory("gpm").FullName;
            var file = Path.Combine(folder, "raft.gpmsprings.json");
            File.WriteAllText(file, SpringsFile);
            var args = JsonSerializer.Serialize(new { file });
            string With(string more) => args[..^1] + "," + more + "}";
            JsonNode Go(string json, ICsiLink e) => GeoProMaxTools.All.First(t => t.Name == "apply_geopromax_springs").Run(Args(json), e);

            // SAFE in kN and m, the raft drawn as eight 3 m panels, and a slab that is not on the ground's cells.
            var m = new MemoryEtabs();
            var names = new List<string>();
            foreach (var y in new[] { 0.0, 3 }) foreach (var x in new[] { 0.0, 3, 6, 9 }) names.Add(m.AddArea([(x, y, 0), (x + 3, y, 0), (x + 3, y + 3, 0), (x, y + 3, 0)], "S200"));
            var far = m.AddArea([(40, 0, 0), (43, 0, 0), (43, 3, 0), (40, 3, 0)], "S200");
            var model = new SpringModel(m);
            var a = Go(args, new SafeMemory(m, model));
            Equal(8, (int)a["areas"]!, "eight panels"); Equal(0, (int)a["mixed"]!["count"]!, "one zone each"); Equal(1, (int)a["outside"]!["count"]!, "the far slab is left");
            Equal("GPM-K1", model.AreaSoil[names[0]], "corner panel"); Equal("GPM-K2", model.AreaSoil[names[1]], "inner panel"); Equal("GPM-K1", model.AreaSoil[names[7]], "far corner");
            Check(!model.AreaSoil.ContainsKey(far), "nothing under the far slab");
            Equal((1000.0, 1), model.Soils["GPM-K1"], "kN/m3, compression only"); Equal(2000.0, model.Soils["GPM-K2"].K, "inner zone");
            Check(((string)a["note"]!).Contains("export_soil_pressures"), "what to do next");

            // The same raft in N and mm: 1000 kN/m3 is 0.001 N/mm3; and in kip and ft.
            var mm = new MemoryEtabs();
            foreach (var x in new[] { 0.0, 3000, 6000, 9000 }) mm.AddArea([(x, 0, 0), (x + 3000, 0, 0), (x + 3000, 6000, 0), (x, 6000, 0)], "S200");
            var inMm = new SpringModel(mm);
            Go(args, new SafeMemory(mm, inMm, null, "mm", "N"));
            Check(Math.Abs(inMm.Soils["GPM-K1"].K - 1e-3) < 1e-9 && inMm.AreaSoil.Count == 4, "N/mm3: " + inMm.Soils["GPM-K1"].K);
            var (kip, ft) = GeoProMaxTools.Units(new ModelInfo("", "", "kip", "ft", "F", false));
            Check(Math.Abs(1000 * kip / (ft * ft * ft) - 6.3659) < 1e-3, "1000 kN/m3 is 6.366 kip/ft3");

            // ETABS: the raft is the lowest slab, the floors above are left; a locked model says what to do.
            var et = new MemoryEtabs();
            var raft = et.AddArea([(0, 0, 0), (12, 0, 0), (12, 6, 0), (0, 6, 0)], "S200");
            var floor = et.AddArea([(0, 0, 3.2), (12, 0, 3.2), (12, 6, 3.2), (0, 6, 3.2)], "S200");
            var inEt = new SpringModel(et);
            var etabs = new SafeMemory(et, inEt, CsiProgram.Etabs);
            et.Locked = true;
            try { Go(args, etabs); throw new Exception("no error"); }
            catch (EtabsException ex) { Check(ex.Message.Contains("unlock") && ex.Message.Contains("ETABS") && inEt.Soils.Count == 0, ex.Message); }
            var one = Go(With("\"unlock\":true,\"compression_only\":false"), etabs);
            Check(!et.Locked && (bool)one["unlocked"]!, "unlocked when told to");
            Equal(1, (int)one["areas"]!, "the raft only"); Check(!inEt.AreaSoil.ContainsKey(floor), "the floor above has no soil");
            Equal(1, (int)one["mixed"]!["count"]!, "one area over both zones is reported");
            Equal("GPM-K2", inEt.AreaSoil[raft], "the zone nearest the average (1500)"); Equal(0, inEt.Soils["GPM-K2"].Nonlinear, "linear when asked");
            try { Go(With("\"as\":\"points\""), etabs); throw new Exception("no error"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("SAFE"), ex.Message); }

            // SAFE, one area for the whole raft: point springs at the cell centres, the area's own soil taken off.
            var sm = new MemoryEtabs();
            var mat = sm.AddArea([(0, 0, 0), (12, 0, 0), (12, 6, 0), (0, 6, 0)], "S200");
            var inSafe = new SpringModel(sm);
            inSafe.Soils["SOIL1"] = (20000, 1); inSafe.AreaSoil[mat] = "SOIL1";
            var pts = Go(With("\"as\":\"points\""), new SafeMemory(sm, inSafe));
            Equal(8, (int)pts["points"]!, "a point per cell"); Equal(8, inSafe.Added.Count, "added on the slab");
            Equal(9000.0, inSafe.PointSprings["GPM-K1-PT"], "1000 kN/m3 on a 3 m cell"); Equal(18000.0, inSafe.PointSprings["GPM-K2-PT"], "inner cells");
            Equal(4, inSafe.PointSpring.Values.Count(v => v == "GPM-K1-PT"), "four edge cells");
            Check(!inSafe.AreaSoil.ContainsKey(mat) && (bool)pts["area_soil_removed"]!, "the area's soil is taken off");

            // Files that are not GeoProMax's, and a raft somewhere else.
            File.WriteAllText(Path.Combine(folder, "other.json"), "{\"format\":\"something\"}");
            try { Go(JsonSerializer.Serialize(new { file = Path.Combine(folder, "other.json") }), new SafeMemory(sm, inSafe)); throw new Exception("no error"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("not a GeoProMax springs file"), ex.Message); }
            try { Go(With("\"offset\":[500,0]"), new SafeMemory(sm, inSafe)); throw new Exception("no error"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("offset"), ex.Message); }
            try { Go(args, new MemoryEtabs()); throw new Exception("no error"); }
            catch (Exception ex) when (ex is ToolInputException or EtabsException) { }
            Directory.Delete(folder, true);
        });

        yield return ("GeoProMax: the soil pressures are written in kN and m, with the coordinates", () =>
        {
            var folder = Directory.CreateTempSubdirectory("gpm").FullName;
            JsonNode Go(string json, ICsiLink e) => GeoProMaxTools.All.First(t => t.Name == "export_soil_pressures").Run(Args(json), e);
            var a = Go(JsonSerializer.Serialize(new { folder }), Safe());
            Equal("written", (string)a["status"]!, a.ToJsonString()); Equal(4, (int)a["rows"]!, "every row");
            var lines = File.ReadAllLines((string)a["file"]!);
            Equal("Point,GlobalX,GlobalY,OutputCase,StepType,Pressure", lines[0], "headings"); Equal(",m,m,,,kN/m2", lines[1], "units");
            Equal("2,6,0,SLS,,-182.25", lines[3], "a row");
            Check(Path.GetFileName((string)a["file"]!) == "mat soil pressures.csv" && ((JsonArray)a["cases"]!).Count == 2, a.ToJsonString());
            var again = Go(JsonSerializer.Serialize(new { folder }), Safe());
            Check(((string)again["file"]!).EndsWith("mat soil pressures (2).csv"), "never overwritten");

            // A table without coordinates takes them from the points, and N and mm become kN and m.
            var m = new MemoryEtabs();
            m.AddArea([(0, 0, 0), (6000, 0, 0), (6000, 5000, 0), (0, 5000, 0)], "S200");
            var third = m.Points().First(p => p.X == 6000 && p.Y == 5000).Name;
            m.ResultTables["Soil Pressures"] = new(["UniqueName", "OutputCase", "StepType", "SoilPressure"], [[third, "SLS", "Max", "-0.15"], ["nowhere", "SLS", "Max", "-0.1"]]);
            var b = Go(JsonSerializer.Serialize(new { folder, name = "r2" }), new SafeMemory(m, null, null, "mm", "N"));
            Equal(1, (int)b["rows"]!, "one placed"); Equal(1, (int)b["skipped"]!, "one without a place");
            Equal($"{third},6,5,SLS,Max,-150", File.ReadAllLines((string)b["file"]!)[2], "kN/m2 at metres");
            Equal("not available", (string)Go(JsonSerializer.Serialize(new { folder }), new SafeMemory(new MemoryEtabs()))["status"]!, "no table yet");
            Directory.Delete(folder, true);
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
sealed class SafeMemory(MemoryEtabs inner, object? model = null, CsiProgram? program = null, string length = "m", string force = "kN") : ICsiLink
{
    public CsiProgram Program => program ?? CsiProgram.Safe;
    /// <summary>Given: the only tables this SAFE lists, and any other is refused as SAFE refuses a table it does not have.</summary>
    public IReadOnlyList<TableName>? Listed { get; init; }
    /// <summary>Given: what stands in for SAFE's own API in the calls made through Raw.</summary>
    public object? Api { get; init; }
    public ModelInfo Info() => inner.Info() with { File = @"C:\Models\mat.FDB", Version = "22.0.0", Length = length, Force = force };
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
    public object? Raw(string what, Func<dynamic, object?> work) => (Api ?? model) is { } api ? work(api) : inner.Raw(what, work);
    public void RunAnalysis() => inner.RunAnalysis();
    public void Save() => inner.Save();
    public void Refresh() => inner.Refresh();
}

/// <summary>The few calls of SAFE's and ETABS's API the springs tools make, kept in lists the tests read.</summary>
public sealed class SpringModel(MemoryEtabs inner)
{
    public Dictionary<string, (double K, int Nonlinear)> Soils { get; } = [];
    public Dictionary<string, string> AreaSoil { get; } = [];
    public Dictionary<string, double> PointSprings { get; } = [];
    public Dictionary<string, string> PointSpring { get; } = [];
    public List<(double X, double Y, double Z)> Added { get; } = [];
    public PropAreaSpringApi PropAreaSpring => new(this);
    public AreaApi AreaObj => new(this);
    public PropPointSpringApi PropPointSpring => new(this);
    public PointApi PointObj => new(this);
    public int SetModelIsLocked(bool locked) { inner.Locked = locked; return 0; }

    public sealed class PropAreaSpringApi(SpringModel o)
    {
        public int SetAreaSpringProp(string name, double u1, double u2, double u3, int nonlinear, int option, string profile, double endRatio, double period, int color, string notes, string guid)
        { o.Soils[name] = (u3, nonlinear); return 0; }
    }

    public sealed class AreaApi(SpringModel o)
    {
        public int SetSpringAssignment(string name, string prop, int item) { if (!o.Soils.ContainsKey(prop)) return 1; o.AreaSoil[name] = prop; return 0; }
        public int DeleteSpring(string name, int item) { o.AreaSoil.Remove(name); return 0; }
    }

    public sealed class PropPointSpringApi(SpringModel o)
    {
        public int SetPointSpringProp(string name, int option, ref double[] k, string csys, string profile, string footing, double period, int color, string notes, string guid)
        { o.PointSprings[name] = k[2]; return 0; }
    }

    public sealed class PointApi(SpringModel o)
    {
        public int AddCartesian(double x, double y, double z, ref string name, string user, string csys, bool merge, int number)
        { o.Added.Add((x, y, z)); name = "P" + o.Added.Count; return 0; }
        public int SetSpringAssignment(string name, string prop, int item) { if (!o.PointSprings.ContainsKey(prop)) return 1; o.PointSpring[name] = prop; return 0; }
    }
}
