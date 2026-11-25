using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Checks;
using CsiProMax.E2k;
using CsiProMax.Model;

// Small self-contained checks, as in Sketch ProMax: each is a name and a body that throws when it fails.
var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));
static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
static void Equal<T>(T expected, T actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
}
static void Near(double expected, double actual, string what)
{
    if (Math.Abs(expected - actual) > 1e-6) throw new Exception($"{what}: expected {expected}, got {actual}");
}
E2kWriter.SavedAt = new DateTime(2026, 11, 25, 0, 57, 6);
var etabs23 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "etabs23-default.e2k"));
var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "etabs-style.e2k"));

Test("tokenizer keeps quoted text whole", () =>
{
    var t = E2kText.Tokenize("  MATERIAL  \"f'c 4000 psi\"    TYPE \"Concrete\"  E 24855578.28");
    Equal(6, t.Count, "token count");
    Equal("\"f'c 4000 psi\"", t[1], "quoted token");
    Equal("f'c 4000 psi", E2kText.Unquote(t[1]), "unquoted");
});

Test("numbers are written the same in every Windows language", () =>
{
    var before = Thread.CurrentThread.CurrentCulture;
    try
    {
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        Equal("0.25", E2kText.Number(0.25), "decimal point");
        Equal("33000000", E2kText.Number(33000000), "no thousands separator");
        Equal("0", E2kText.Number(-0.0), "zero");
        Check(E2kText.TryNumber("1.5E-05", out var d) && Math.Abs(d - 1.5e-5) < 1e-12, "exponent");
    }
    finally { Thread.CurrentThread.CurrentCulture = before; }
});

Test("template frame: counts", () =>
{
    var m = Templates.ConcreteFrame("T", [6, 6], [5], 3, 3.5);
    Equal(4, m.Stories.Count, "levels");
    Equal("Dead,Live", string.Join(",", m.LoadPatterns.Select(p => p.Name)), "ETABS's own load patterns");
    Equal(6, m.Points.Count, "plan points");
    Equal(18, Physical.Frames(m).Count(x => x.Kind == LineKind.Column), "columns");
    Equal(21, Physical.Frames(m).Count(x => x.Kind == LineKind.Beam), "beams");
    Equal(6, Physical.Areas(m).Count(), "slabs");
    Equal(6, m.Lines.Count(l => l.Kind == LineKind.Column), "column objects are shared between stories");
    Near(10.5, m.Elevation(3), "top elevation");
});

Test("template frame: no findings", () =>
{
    var findings = ModelChecks.Run(Templates.ConcreteFrame("T", [6, 6], [5, 5], 2, 3));
    Check(findings.Count == 0, "unexpected: " + string.Join(" | ", findings.Select(f => f.Message)));
});

Test("written file has the lines ETABS expects", () =>
{
    var text = E2kWriter.Write(Templates.ConcreteFrame("My \"tower\"", [6], [5], 1, 3));
    foreach (var expected in new[]
             {
                 "$ File model.e2k saved 11/25/2026 12:57:06 AM", "$ PROGRAM INFORMATION", "  PROGRAM  \"ETABS\"  VERSION  \"23.2.0\"", "  UNITS  \"KN\"  \"M\"  \"C\"",
                 "  TITLE2  \"My 'tower'\"", "  PREFERENCE  MERGETOL 0.001", "  RLLF  METHOD \"ASCE7-10\"  USEDEFAULTMIN \"YES\"", "$ STORIES - IN SEQUENCE FROM TOP", "  STORY  \"Story1\"  HEIGHT  3", "  STORY  \"Base\"  ELEV  0",
                 "  POINT  \"1\"  0  0", "  LINE  \"C1\"  COLUMN  \"1\"  \"1\"  1", "  LINE  \"B1\"  BEAM  \"1\"  \"3\"  0",
                 "  AREA  \"F1\"  FLOOR  4  \"1\"  \"3\"  \"4\"  \"2\"  0  0  0  0",
                 "  POINTASSIGN  \"1\"  \"Base\"  RESTRAINT  \"UX UY UZ RX RY RZ\"",
                 "  LINEASSIGN  \"C1\"  \"Story1\"  SECTION  \"C500x500\"  MINNUMSTA  3  AUTOMESH  \"YES\"  MESHATINTERSECTIONS  \"YES\"",
                 "  CONCRETESECTION  \"B300x600\"  LONGBARMATERIAL  \"DefaultRebar_500\"  CONFINEBARMATERIAL  \"DefaultRebar_500\"  TYPE  \"Beam\"  COVERTOP  0.06  COVERBOTTOM  0.06  ATI  0  ABI  0  ATJ  0  ABJ  0",
                 "  FRAMESECTION  \"C500x500\"  MATERIAL  \"C30/37\"  SHAPE  \"Concrete Rectangular\"  D  0.5  B  0.5",
                 "  PROJECTINFO  MODELNAME  \"My 'tower'\"", "$ MASS SOURCE", "$ CONCRETE DESIGN PREFERENCES", "  END",
                 "  AREAASSIGN  \"F1\"  \"Story1\"  SECTION  \"S200\"  DIAPH  \"D1\"  OBJMESHTYPE  \"DEFAULT\"  ADDRESTRAINT  \"No\"  CARDINALPOINT  \"TOP\"  TRANSFORMSTIFFNESSFOROFFSETS  \"No\"",
                 "  LOADPATTERN  \"Dead\"  TYPE  \"Dead\"  SELFWEIGHT  1",
                 "  AREALOAD  \"F1\"  \"Story1\"  TYPE  \"UNIFF\"  DIR  \"GRAV\"  LC  \"Live\"  FVAL  3",
                 "  AREALOAD  \"F1\"  \"Story1\"  TYPE  \"UNIFF\"  DIR  \"GRAV\"  LC  \"Dead\"  FVAL  2", "$ END OF MODEL FILE",
             })
        Check(text.Contains(expected + "\r\n"), "missing line: " + expected);
    var stories = text.IndexOf("$ STORIES", StringComparison.Ordinal);
    Check(!text.Contains("TITLE2  \"2\"") && !text.Contains("SteelCol\"  \"Story"), "nothing of the seed's own building is left");
    Check(text.IndexOf("\"Story1\"", stories, StringComparison.Ordinal) < text.IndexOf("\"Base\"", stories, StringComparison.Ordinal), "stories are top first");
});

Test("write, read, write gives the same file", () =>
{
    var first = E2kWriter.Write(Templates.ConcreteFrame("T", [6, 7.5], [5, 4.2], 4, 3.2));
    var second = E2kWriter.Write(E2kReader.Read(first));
    Equal(first, second, "round trip");
});

Test("reads an ETABS-style file", () =>
{
    var m = E2kReader.Read(sample);
    Equal("Sample two-story frame", m.Title, "title");
    Equal("23.0.0", m.ProgramVersion, "version");
    Equal("Base,Story1,Story2", string.Join(",", m.Stories.Select(s => s.Name)), "stories bottom first");
    Near(7.2, m.Elevation(2), "top elevation");
    Equal(4, m.Grids.Count, "grids");
    Equal(2, m.Materials.Count, "materials");
    Check(m.Materials[0].IsConcrete, "concrete material");
    Near(0.25, m.FrameSections[0].Area ?? 0, "column area");
    Equal("C30/37", m.FrameSections[1].Material, "beam material");
    Near(0.25, m.ShellSections.Single(s => s.IsWall).Thickness ?? 0, "wall thickness");
    Equal(4, Physical.Frames(m).Count(x => x.Kind == LineKind.Column), "columns (C1 is on two stories)");
    var c1Top = Physical.Frames(m).Single(x => x.Label == "C1" && x.Story == "Story2");
    Near(4, c1Top.I.Z, "C1 Story2 foot"); Near(7.2, c1Top.J.Z, "C1 Story2 head");
    var wall = Physical.Areas(m).Single(a => a.Kind == AreaKind.Wall);
    Near(24, wall.Area, "wall area 6 m x 4 m");
    Equal("D1", m.AreaAssigns.Single(a => a.Label == "F1").Diaphragm, "slab diaphragm");
    Equal("Dead", m.LineLoads.Single().Pattern, "line load pattern");
    Equal(2, m.LoadCases.Count, "load cases"); Equal(3, m.Combos.Single().Lines.Count, "combo lines");
});

Test("a real ETABS 23.2 export reads and writes back whole", () =>
{
    var m = E2kReader.Read(etabs23);
    Equal("23.2.0", m.ProgramVersion, "version");
    Equal(64, Physical.Frames(m).Count(x => x.Kind == LineKind.Column), "columns");
    Equal(48, Physical.Frames(m).Count(x => x.Kind == LineKind.Beam), "beams");
    Equal(20, Physical.Areas(m).Count(), "slabs and drops");
    Equal(16, m.PointAssigns.Count(a => a.Restraint == "UX UY UZ"), "pinned supports");
    Equal("Modal,Dead,Live", string.Join(",", m.LoadCases.Select(c => c.Name)), "load cases");
    Check(ModelChecks.Run(m).TrueForAll(f => f.Severity == Severity.Warning), "no errors in ETABS's own model");
    var text = E2kWriter.Write(m);
    // Every word of every line of the original is in the output, in a section of the same name, in the same order of sections.
    static string Squash(string line) => string.Join(" ", E2kText.Tokenize(line));
    var written = text.Split('\n').Select(Squash).Where(l => l.Length > 0).ToList();
    var original = etabs23.Split('\n').Select(Squash).Where(l => l.Length > 0).ToList();
    Equal(string.Join("|", original.Where(l => l.StartsWith('$')).Skip(1)), string.Join("|", written.Where(l => l.StartsWith('$')).Skip(1)), "section headings and their order");
    var writtenWords = written.Where(l => !l.StartsWith('$')).SelectMany(E2kText.Tokenize).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
    var originalWords = original.Where(l => !l.StartsWith('$')).SelectMany(E2kText.Tokenize).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
    foreach (var (word, count) in originalWords)
        Equal(count, writtenWords.GetValueOrDefault(word), $"how many times '{word}' appears");
    Equal(originalWords.Count, writtenWords.Count, "no new words");
    Equal(original.Count, written.Count, "same number of lines");
    Equal(text, E2kWriter.Write(E2kReader.Read(text)), "second pass is stable");
});

Test("what ETABS 23.2 taught us on import", () =>
{
    var m = Templates.ConcreteFrame("T", [6], [5], 2, 3);
    m.AddWallSection("W250", ModelEdit.DefaultConcrete, 0.25);
    m.AddWall(0, 0, 6, 0, "Story1", "W250");
    m.AddLoadPattern("SDL", "super dead");
    m.AddCombo("Gravity", ("Dead", 1.2), ("SDL", 1.2), ("Live", 1.6));
    var text = E2kWriter.Write(m);
    int At(string what) => text.IndexOf(what, StringComparison.Ordinal);
    // ETABS stops reading at the log's END: combinations written after it were silently dropped.
    Check(At("$ LOAD CASES") < At("$ LOAD COMBINATIONS") && At("$ LOAD COMBINATIONS") < At("$ STEEL DESIGN PREFERENCES"), "combinations follow the load cases");
    Check(text.TrimEnd().EndsWith("  END\r\n\r\n$ END OF MODEL FILE"), "the log's END is the last line before the end of the file");
    Check(text.Contains("  LOADPATTERN  \"SDL\"  TYPE  \"Super Dead\"  SELFWEIGHT  0\r\n"), "the type as ETABS writes it");
    Check(text.Contains("SECTION  \"W250\"  OBJMESHTYPE  \"DEFAULT\"  ADDRESTRAINT  \"No\"  CARDINALPOINT  \"MIDDLE\"  TRANSFORMSTIFFNESSFOROFFSETS  \"No\""), "wall assignment as ETABS writes it");
    try { m.AddLoadPattern("X", "Superimposed Dead"); throw new Exception("accepted a type ETABS does not have"); }
    catch (ArgumentException ex) { Check(ex.Message.Contains("Super Dead"), ex.Message); }
    m.LoadPatterns.Add(new LoadPattern { Name = "Y", Type = "Superimposed Dead", Extra = ["X"] });
    Check(ModelChecks.Run(m).Exists(f => f.Message.Contains("imports as 'Other'")), "a type ETABS does not list is reported");
    Equal(text, E2kWriter.Write(ModelFile.FromJson(ModelFile.ToJson(E2kReader.Read(text)))), "stable through .e2k and .cpm");
});

Test("a model ETABS 23.2 saved after importing ours reads and writes back", () =>
{
    var saved = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "etabs23-after-import.e2k"));
    var m = E2kReader.Read(saved);
    Equal(3, Physical.Areas(m).Count(a => a.Kind == AreaKind.Wall), "walls");
    Equal("Super Dead", m.LoadPatterns.Single(p => p.Name == "SDLSD2").Type, "the type ETABS wrote");
    Check(ModelChecks.Run(m).TrueForAll(f => f.Severity == Severity.Warning), "no errors");
    var text = E2kWriter.Write(m);
    Equal(text, E2kWriter.Write(E2kReader.Read(text)), "second pass is stable");
});

Test("keeps what it doesn't understand", () =>
{
    var m = E2kReader.Read(sample);
    Equal("REBAR DEFINITIONS,CONCRETE SECTIONS,ANALYSIS OPTIONS,MASS SOURCE,CONCRETE DESIGN PREFERENCES",
        string.Join(",", m.Passthrough.Select(s => s.Heading)), "kept sections");
    var text = E2kWriter.Write(m);
    foreach (var expected in new[]
             {
                 "  REBARDEFINITION  \"#4\"  AREA 0.000129032  DIA 0.0127", "  PREFERENCE  MERGETOL 0.001",
                 "  SEISMIC \"EQX\"  \"ASCE 7-16\"  DIR \"X\"  SS 1.2  S1 0.5", "RELEASE  \"M3I M3J\"", "PIER  \"P1\"",
                 "SIMILARTO  \"Story1\"", "JMOD  0.01", "INSERTIONPT  10", "  CONCRETEPREFERENCE  DESIGNCODE \"ACI 318-19\"",
             })
        Check(text.Contains(expected), "lost: " + expected);
    // Kept sections go back where they were: rebar before frame sections, mass source before load cases.
    int At(string heading) => text.IndexOf("$ " + heading + "\r\n", StringComparison.Ordinal);
    Check(At("MATERIAL PROPERTIES") < At("REBAR DEFINITIONS") && At("REBAR DEFINITIONS") < At("FRAME SECTIONS"), "rebar definitions in place");
    Check(At("FRAME SECTIONS") < At("CONCRETE SECTIONS") && At("CONCRETE SECTIONS") < At("SLAB PROPERTIES"), "concrete sections in place");
    Check(At("SHELL OBJECT LOADS") < At("ANALYSIS OPTIONS") && At("MASS SOURCE") < At("LOAD CASES"), "analysis options in place");
    Check(At("LOAD COMBINATIONS") < At("CONCRETE DESIGN PREFERENCES"), "design preferences last");
    Equal(text, E2kWriter.Write(E2kReader.Read(text)), "second pass is stable");
});

Test("the ETABS-style sample passes the checks apart from its real gaps", () =>
{
    var findings = ModelChecks.Run(E2kReader.Read(sample));
    // B3 runs from point 4, where only the wall meets it: joined. Story2 has one column and nothing else: fine.
    Check(findings.TrueForAll(f => f.Severity == Severity.Warning), "errors: " + string.Join(" | ", findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
});

Test("checks find what is wrong", () =>
{
    var m = Templates.ConcreteFrame("T", [6], [5], 1, 3);
    m.AddBeam(6, 5, 9, 5, "Story1", "B300x600");                 // cantilever stub: free end
    m.LineAssigns[0].Section = "Missing";                        // undefined section
    m.PointAssigns.RemoveAll(a => a.Point == "1");               // column with no support
    m.AddStory("Roof", 3);                                       // empty story
    m.AreaLoads.Add(new AreaLoad { Object = "F9", Story = "Story1", Tokens = ["LC", "\"Wind\""] });
    var findings = ModelChecks.Run(m);
    Check(findings.Exists(f => f.Check == "connectivity" && f.Message.Contains("free end at (9, 5)")), "free end");
    Check(findings.Exists(f => f.Check == "sections" && f.Message.Contains("'Missing'")), "undefined section");
    Check(findings.Exists(f => f.Check == "supports" && f.Object == "C1"), "unsupported column");
    Check(findings.Exists(f => f.Check == "stories" && f.Story == "Roof"), "empty story");
    Check(findings.Exists(f => f.Message.Contains("area 'F9'")), "load on a missing area");
    Check(findings.Exists(f => f.Message.Contains("'Wind'")), "load with a missing pattern");
});

Test("quantities", () =>
{
    var rows = Quantities.Run(Templates.ConcreteFrame("T", [6, 6], [5], 2, 3));
    var columns = rows.Single(r => r.Kind == "Column");
    Equal(12, columns.Count, "columns"); Near(36, columns.Length, "column length"); Near(9, columns.Volume ?? 0, "column volume");
    var beams = rows.Single(r => r.Kind == "Beam");
    Equal(14, beams.Count, "beams"); Near(78, beams.Length, "beam length"); Near(78 * 0.18, beams.Volume ?? 0, "beam volume");
    var slabs = rows.Single(r => r.Kind == "Floor");
    Near(120, slabs.Area, "slab area"); Near(24, slabs.Volume ?? 0, "slab volume");
});

Test("model file (.cpm) round trip", () =>
{
    var m = E2kReader.Read(sample);
    var back = ModelFile.FromJson(ModelFile.ToJson(m));
    Equal(E2kWriter.Write(m), E2kWriter.Write(back), "same .e2k after saving and loading the .cpm");
});

Test("editing guards", () =>
{
    var m = ModelEdit.New("T");
    m.AddStory("L1", 3);
    string Message(Action a) { try { a(); return ""; } catch (ArgumentException ex) { return ex.Message; } }
    Check(Message(() => m.AddColumn(0, 0, "Base", "C")).Contains("base level"), "column on the base level");
    Check(Message(() => m.AddColumn(0, 0, "L9", "C")).Contains("no story named 'L9'"), "unknown story");
    Check(Message(() => m.AddStory("L1", 3)).Contains("already"), "duplicate story");
    Check(Message(() => m.AddBeam(1, 1, 1, 1, "L1", "B")).Contains("two different ends"), "zero-length beam");
    m.AddBeam(0, 0, 5, 0, "L1", "B"); m.AddBeam(5, 0, 0, 0, "L1", "B2");
    Equal(1, m.LineAssigns.Count, "the same beam drawn the other way replaces the first");
    Equal("B2", m.LineAssigns[0].Section, "its section is the later one");
});

Test("a file that is not an ETABS text file", () =>
{
    try { E2kReader.Read("hello\nworld"); throw new Exception("accepted a non-e2k file"); }
    catch (E2kFormatException ex) { Check(ex.Message.Contains("not an ETABS text file"), ex.Message); }
});

// The assistant, on a model kept in memory (no ETABS).
JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
// The tools as the panel runs them (ModelTool), with the asking the approvals do: always for deletes, saves and
// analysis runs, and for every change when the engineer set it so.
Harness Endpoint(ICsiLink e, bool allow = true, bool ask = false, List<string>? asked = null) => new(e, allow, ask, asked);
JsonNode Call(Harness mcp, string tool, string args)
{
    var (text, failed) = mcp.Call(tool, J(args));
    if (failed) throw new Exception(text);
    return JsonNode.Parse(text)!;
}
string Refused(Harness mcp, string tool, string args)
{
    var (text, failed) = mcp.Call(tool, J(args));
    Check(failed, $"{tool} should have been refused, but gave {text}");
    return text;
}

Test("assistant: a frame is built with the tools", () =>
{
    var e = new MemoryEtabs();
    var mcp = Endpoint(e);
    Equal(12, (int)Call(mcp, "add_columns", """{"points":[[0,0],[6,0],[6,5],[0,5]],"section":"c500x500"}""")["added"]!, "4 columns on 3 stories");
    Equal(4, (int)Call(mcp, "add_beams", """{"lines":[[0,0,6,0],[6,0,6,5],[6,5,0,5],[0,5,0,0]],"stories":["Story3"],"section":"B300x600"}""")["added"]!, "beams on one story");
    Equal(3, (int)Call(mcp, "add_slabs", """{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"section":"S200"}""")["added"]!, "a slab per story");
    Equal(3, (int)Call(mcp, "add_walls", """{"lines":[[0,0,0,5]],"section":"W250"}""")["added"]!, "a wall panel per story");
    var context = Call(mcp, "get_model_context", "{}");
    Equal(12, (int)context["counts"]!["columns"]!, "columns"); Equal(4, (int)context["counts"]!["beams"]!, "beams");
    Equal(3, (int)context["counts"]!["floors"]!, "floors"); Equal(3, (int)context["counts"]!["walls"]!, "walls");
    Equal("m", (string)context["units"]!["length"]!, "units");
    var column = e.Frames().First(f => f.Kind == "column" && f.Story == "Story2");
    Near(3.2, column.Z1, "a column starts at the story below"); Near(6.4, column.Z2, "and ends at its story");
    var beams = Call(mcp, "query_objects", """{"kind":"beam"}""");
    Equal(4, (int)beams["count"]!, "query by kind"); Equal(4, (int)beams["by_section"]!["B300x600"]!, "counts by section");
    Equal(4, (int)Call(mcp, "set_supports", """{"type":"fixed","at_base":true}""")["done"]!, "supports under the columns and the wall");
    Check(e.Restraint(e.Points().First(p => p.Z == 0).Name).All(r => r), "fixed means all six");
});

Test("assistant: wrong input is answered with what the model has", () =>
{
    var e = new MemoryEtabs();
    var mcp = Endpoint(e);
    Check(Refused(mcp, "add_columns", """{"points":[[0,0]],"section":"C900"}""").Contains("C500x500"), "unknown section lists the real ones");
    Check(Refused(mcp, "add_beams", """{"lines":[[0,0,1,0]],"stories":["Roof"],"section":"B300x600"}""").Contains("Story1, Story2, Story3"), "unknown story lists the real ones");
    Check(Refused(mcp, "add_load_pattern", """{"name":"SDL","type":"Superimposed Dead"}""").Contains("Super Dead"), "unknown pattern type");
    Check(Refused(mcp, "read_table", """{"table":"Drift"}""").Contains("Story Drifts"), "a close table name is offered");
    Check(Refused(mcp, "no_such_tool", "{}").Contains("no tool"), "unknown tool");
    Equal(0, e.Frames().Count, "nothing was added");
});

Test("assistant: loads, combinations and tables", () =>
{
    var e = new MemoryEtabs();
    var mcp = Endpoint(e);
    var beam = (string)Call(mcp, "add_beams", """{"lines":[[0,0,6,0]],"stories":["Story1"],"section":"B300x600"}""")["names"]![0]!;
    Call(mcp, "add_load_pattern", """{"name":"SDL","type":"super dead"}""");
    Call(mcp, "set_loads", $$"""{"frames":["{{beam}}"],"pattern":"sdl","value":8}""");
    Call(mcp, "set_loads", $$"""{"frames":["{{beam}}"],"pattern":"SDL","value":2,"add":true}""");
    Near(10, e.Loads[$"frame {beam} / SDL"], "the second load was added to the first");
    Equal(3, (int)Call(mcp, "add_combination", """{"name":"ULS","cases":[{"case":"Dead","factor":1.2},{"case":"SDL","factor":1.2},{"case":"Live","factor":1.6}]}""")["cases"]!, "combination");
    Check(Refused(mcp, "add_combination", """{"name":"X","cases":[{"case":"Wind","factor":1}]}""").Contains("Dead, Live, SDL"), "unknown case");
    var table = Call(mcp, "read_table", """{"table":"story definitions","where":{"Story":"Story2"},"fields":["Story","Height"]}""");
    Equal(1, (int)table["row_count"]!, "where keeps one row"); Equal("3.2", (string)table["rows"]![0]![1]!, "fields keeps two columns");
    Check(((string)Call(mcp, "read_table", """{"table":"Story Drifts"}""")["note"]!).Contains("finished analysis"), "an empty result table says why");
    Equal(2, (int)Call(mcp, "list_tables", "{}")["count"]!, "empty tables are left out");
    var selected = Call(mcp, "select_objects", $$"""{"frames":["{{beam}}","nope"]}""");
    Equal(1, (int)selected["selected"]!, "one selected"); Equal(1, selected["failed"]!.AsArray().Count, "one reported");
});

Test("results: drift, torsion, modal, soft story, mass and design ratios from ETABS's tables", () =>
{
    var e = new MemoryEtabs();
    var mcp = Endpoint(e);
    Equal("not available", (string)Call(mcp, "check_drift", "{}")["status"]!, "no analysis yet");
    e.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Case Type", "Direction", "Drift", "Label"],
    [
        ["Story3", "EQX", "LinStatic", "X", "0.0011", "5"], ["Story2", "EQX", "LinStatic", "X", "0.0024", "5"], ["Story1", "EQX", "LinStatic", "X", "0.0019", "7"],
        ["Story2", "EQY", "LinStatic", "Y", "0.0009", "5"], ["Story2", "WX", "LinStatic", "X", "-0.0004", "5"],
    ]);
    var drift = Call(mcp, "check_drift", """{"limit":0.002}""");
    Equal("fail", (string)drift["status"]!, "over the limit"); Equal("Story2", (string)drift["at_story"]!, "governing story"); Equal("EQX", (string)drift["case"]!, "governing case");
    Near(0.0024, (double)drift["max_drift"]!, "largest drift"); Equal(1, drift["stories_over_limit"]!.AsArray().Count, "one story over");
    Equal("Story3", (string)drift["by_story"]![0]!["story"]!, "top story first");
    Equal("reported", (string)Call(mcp, "check_drift", "{}")["status"]!, "no limit, no verdict");
    Equal("pass", (string)Call(mcp, "check_drift", """{"limit":0.0025}""")["status"]!, "within a looser limit");

    e.ResultTables["Story Max Over Avg Displacements"] = new(["Story", "Output Case", "Direction", "Maximum", "Average", "Ratio"],
        [["Story3", "EQX", "X", "0.012", "0.010", "1.2"], ["Story2", "EQX", "X", "0.009", "0.0068", "1.32"], ["Story1", "EQY", "Y", "0.004", "0.004", "1.0"]]);
    var torsion = Call(mcp, "check_torsion", "{}");
    Equal("torsional irregularity", (string)torsion["verdict"]!, "between 1.2 and 1.4"); Equal("Story2", (string)torsion["at_story"]!, "where");
    Equal("extreme torsional irregularity", (string)Call(mcp, "check_torsion", """{"extreme":1.3}""")["verdict"]!, "the engineer's own limit");

    e.ResultTables["Modal Participating Mass Ratios"] = new(["Case", "Mode", "Period", "UX", "UY", "RZ", "SumUX", "SumUY"],
        [["Modal", "1", "0.82", "0.71", "0.01", "0.02", "0.71", "0.01"], ["Modal", "2", "0.64", "0.02", "0.69", "0.05", "0.73", "0.70"], ["Modal", "3", "0.5", "0.01", "0.03", "0.7", "0.74", "0.73"]]);
    var modal = Call(mcp, "check_modal", "{}");
    Equal("fail", (string)modal["status"]!, "74% is below 90%"); Equal("X", (string)modal["modes"]![0]!["mainly"]!, "first mode"); Equal("torsion", (string)modal["modes"]![2]!["mainly"]!, "third mode");
    Equal("pass", (string)Call(mcp, "check_modal", """{"min_mass":0.7}""")["status"]!, "a lower target");

    e.ResultTables["Story Stiffness"] = new(["Story", "Output Case", "Stiff X", "Stiff Y"],
        [["Story3", "EQX", "100000", "0"], ["Story2", "EQX", "120000", "0"], ["Story1", "EQX", "60000", "0"]]);
    e.ResultTables["Mass Summary by Story"] = new(["Story", "UX", "UY"], [["Story3", "200", "200"], ["Story2", "700", "700"], ["Story1", "400", "400"], ["Base", "50", "50"]]);
    var irregular = Call(mcp, "check_story_irregularity", "{}");
    Equal("fail", (string)irregular["status"]!, "both fail");
    Equal("Story1", (string)irregular["soft_story"]!["soft_stories"]![0]!["story"]!, "Story1 has half the stiffness of Story2");
    Near(0.5, (double)irregular["soft_story"]!["soft_stories"]![0]!["ratio_to_story_above"]!, "ratio");
    Equal("Story2", (string)irregular["mass"]!["heavy_stories"]![0]!["story"]!, "Story2 is 1.75 times Story1");

    e.ResultTables["Concrete Column Summary - ACI 318-19"] = new(["Story", "Label", "UniqueName", "DesignSect", "PMM Combo", "PMM Ratio", "V Major Ratio"],
        [["Story1", "C1", "11", "C500x500", "ULS1", "0.82", "0.3"], ["Story1", "C2", "12", "C500x500", "ULS2", "1.15", "0.4"], ["Story2", "C1", "21", "C500x500", "ULS1", "0.4", "1.02"]]);
    var design = Call(mcp, "design_ratios", "{}");
    Equal("fail", (string)design["status"]!, "two members over 1"); Equal(2, (int)design["members_over_limit"]!, "count"); Near(1.15, (double)design["max_ratio"]!, "largest");
    var worst = design["tables"]![0]!["worst"]!;
    Equal("12", (string)worst["name"]!, "which member"); Equal("ULS2", (string)worst["combination"]!, "its combination"); Equal("PMM Ratio", (string)worst["governs"]!, "which ratio");
    Equal("pass", (string)Call(mcp, "design_ratios", """{"limit":1.2}""")["status"]!, "a looser limit");

    var report = Call(mcp, "check_report", """{"drift_limit":0.002}""");
    Equal(5, report["summary"]!.AsArray().Count, "five checks"); Equal("fail", (string)report["checks"]!["drift"]!["status"]!, "the drift limit is passed on");
    Check(((string)report["summary"]![4]!).StartsWith("design ratios: fail"), "summary lines");
    Check(Call(mcp, "results_overview", "{}")["design_tables_with_data"]!.AsArray().Count == 1, "the overview names the design table");
});

Test("results: a snapshot is kept and compared with the model later", () =>
{
    ResultTools.SnapshotRoot = Path.Combine(Path.GetTempPath(), "cpm-snap-" + Guid.NewGuid().ToString("N"));
    try
    {
        var e = new MemoryEtabs();
        var mcp = Endpoint(e);
        Call(mcp, "add_columns", """{"points":[[0,0],[6,0]],"stories":["Story1"],"section":"C500x500"}""");
        var beam = (string)Call(mcp, "add_beams", """{"lines":[[0,0,6,0]],"stories":["Story1"],"section":"B300x600"}""")["names"]![0]!;
        Equal(3, (int)Call(mcp, "model_snapshot", """{"name":"before change"}""")["frames"]!, "three frames kept");
        Equal(0, (int)Call(mcp, "compare_snapshots", """{"from":"before change"}""")["changes"]!, "nothing changed yet");
        e.FrameSectionNames.Add("B300x700");
        Call(mcp, "set_section", $$"""{"frames":["{{beam}}"],"section":"B300x700"}""");
        Call(mcp, "add_beams", """{"lines":[[0,5,6,5]],"stories":["Story1"],"section":"B300x600"}""");
        Call(mcp, "add_load_pattern", """{"name":"SDL","type":"Super Dead"}""");
        var diff = Call(mcp, "compare_snapshots", """{"from":"before-change"}""");
        Equal(1, (int)diff["frames"]!["added"]!, "one beam added"); Equal(1, (int)diff["frames"]!["section_changed"]!, "one section changed");
        Check(((string)diff["frames"]!["section_changes"]![0]!).EndsWith("B300x600 to B300x700"), "which section");
        Check(diff["definitions"]!["frame_sections"]!["added"]!.AsArray().Count == 1 && diff["definitions"]!["load_patterns"] is not null, "definitions that changed");
        Equal(1, (int)Call(mcp, "list_snapshots", "{}")["count"]!, "one snapshot");
        Check(Refused(mcp, "compare_snapshots", """{"from":"nope"}""").Contains("before-change"), "an unknown snapshot lists the real ones");
    }
    finally { if (Directory.Exists(ResultTools.SnapshotRoot)) Directory.Delete(ResultTools.SnapshotRoot, true); }
});

Test("pack: the model, ETABS's tables and the checks go into one file and the Viewer page", () =>
{
    var e = new MemoryEtabs();
    var mcp = Endpoint(e);
    Call(mcp, "add_columns", """{"points":[[0,0],[6,0],[6,5],[0,5]],"section":"C500x500"}""");
    Call(mcp, "add_beams", """{"lines":[[0,0,6,0],[6,0,6,5],[6,5,0,5],[0,5,0,0]],"section":"B300x600"}""");
    Call(mcp, "add_slabs", """{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"section":"S200"}""");
    Call(mcp, "add_walls", """{"lines":[[0,0,0,5]],"section":"W250"}""");
    var top = e.Points().Where(p => p.Z > 9).ToList();
    e.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Direction", "Drift"], [["Story3", "EQX", "X", "0.0011"], ["Story2", "EQX", "X", "0.0024"], ["Story1", "EQX", "X", "0.0019"]]);
    e.ResultTables["Joint Displacements"] = new(["Story", "UniqueName", "Output Case", "Ux", "Uy", "Uz"],
        e.Points().Select(p => new[] { "", p.Name, "EQX", (p.Z * 0.002).ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "0" }).ToList());
    e.ResultTables["Concrete Column Summary - ACI 318-19"] = new(["Story", "UniqueName", "PMM Combo", "PMM Ratio"],
        e.Frames().Where(f => f.Kind == "column").Select((f, i) => new[] { f.Story, f.Name, "ULS1", (0.45 + i * 0.06).ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList());
    var checks = ResultTools.Report(J("""{"drift_limit":0.002}"""), e).AsObject();
    var built = Pack.Build(e, ["Frame Assignments - Section Properties", "No Such Table"], [], [], checks, null);
    Equal(24, built.Frames, "frames"); Equal(6, built.Areas, "areas");
    Check(built.Tables.Contains("Story Drifts") && built.Tables.Contains("Concrete Column Summary - ACI 318-19") && built.Tables.Contains("Frame Assignments - Section Properties"), "usual, design and asked-for tables");
    Check(built.Notes.Any(n => n.Contains("No Such Table")), "a table that does not exist is reported");
    var back = Pack.Read(built.Zip);
    Equal("demo", (string)back["manifest"]!["model"]!, "model name"); Equal("m", (string)back["manifest"]!["units"]!["length"]!, "units");
    Equal(24, back["model"]!["frames"]!.AsArray().Count, "frames read back"); Equal(3, back["tables"]!["Story Drifts"]!["rows"]!.AsArray().Count, "table rows read back");
    Equal("fail", (string)back["checks"]!["checks"]!["drift"]!["status"]!, "checks read back");
    var small = Pack.Build(e, [], [], [], null, null, mostRows: 2);
    Equal(1, (int)small.Content["tables"]!["Story Drifts"]!["left_out"]!, "a long table is cut and says so");
    var html = Pack.ViewerHtml(built.Content);
    Check(html.Contains("\"format\":\"cpmx\"") && html.Contains("CSI ProMax Viewer") && !html.Contains("<script id=\"pack\" type=\"application/json\"></script>"), "the page carries the pack");
    Check(Pack.ViewerHtml().Contains("<script id=\"pack\" type=\"application/json\"></script>"), "the empty page has none");

    var folder = Path.Combine(Path.GetTempPath(), "cpm-pack-" + Guid.NewGuid().ToString("N"));
    try
    {
        var made = CsiProMax.Tools.ExportPackTool.Make(e, new CsiProMax.Tools.ToolArgs(new Dictionary<string, JsonElement> { ["drift_limit"] = J("0.002") }), folder);
        Check(!made.IsError, made.Text);
        var files = CsiProMax.Chat.ChatFiles.Find(made.Text);
        Equal(2, files.Count, "both files get a card"); Check(files[0].EndsWith("demo viewer.html") && files[1].EndsWith("demo.cpmx"), "the page first, then the pack");
        Check(File.Exists(files[0]) && File.Exists(files[1]), "written");
        var comments = Path.Combine(folder, "demo comments.json");
        File.WriteAllText(comments, """{"format":"cpmx-comments","model":"demo","comments":[{"id":"a1","by":"Sara","at":"2026-11-25T08:00:00Z","text":"Check this beam","target":{"kind":"frame","what":"beam","name":"B7","story":"Story2"}}]}""");
        var read = JsonNode.Parse(CsiProMax.Tools.ReadPackCommentsTool.Read(comments).Text)!;
        Equal(1, (int)read["count"]!, "one comment"); Equal("B7", (string)read["comments"]![0]!["target"]!["name"]!, "on which member");
        Equal(0, (int)JsonNode.Parse(CsiProMax.Tools.ReadPackCommentsTool.Read(files[1]).Text)!["count"]!, "a fresh pack has none");
        if (Environment.GetEnvironmentVariable("CPM_KEEP_PACK") is { Length: > 0 } keep) { File.Copy(files[0], Path.Combine(keep, "demo viewer.html"), true); File.Copy(files[1], Path.Combine(keep, "demo.cpmx"), true); File.WriteAllText(Path.Combine(keep, "viewer.html"), Pack.ViewerHtml()); }
    }
    finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
});

Test("modeler: a building is modelled with no ETABS and saved as an ETABS text file", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "cpm-modeler-" + Guid.NewGuid().ToString("N"));
    var before = OfflineEtabs.DefaultFolder;
    OfflineEtabs.DefaultFolder = folder;
    try
    {
        var o = new OfflineEtabs();
        var asked = new List<string>();
        var mcp = Endpoint(o, asked: asked);
        Equal(0, (int)Call(mcp, "new_model", """{"title":"Office block"}""")["stories"]!, "an empty model");
        Equal(1, asked.Count, "replacing the model is asked about");
        Call(mcp, "add_stories", """{"stories":[{"name":"L1","height":4},{"name":"L2","height":3.2}]}""");
        Equal(4, (int)Call(mcp, "add_grids", """{"grids":[{"label":"A","direction":"X","coordinate":0},{"label":"B","direction":"X","coordinate":6},{"label":"1","direction":"Y","coordinate":0},{"label":"2","direction":"Y","coordinate":5}]}""")["grids"]!, "grids");
        Check(Refused(mcp, "add_grids", """{"grids":[{"label":"A","direction":"X","coordinate":9}]}""").Contains("already"), "a grid label twice");
        Call(mcp, "add_section", """{"name":"C400","kind":"column","depth":0.4,"width":0.4}""");
        Call(mcp, "add_section", """{"name":"B250x500","kind":"beam","depth":0.5,"width":0.25}""");
        Call(mcp, "add_section", """{"name":"S180","kind":"slab","thickness":0.18}""");
        Call(mcp, "add_section", """{"name":"W200","kind":"wall","thickness":0.2}""");
        Check(Refused(mcp, "add_section", """{"name":"X","kind":"slab","thickness":0.2,"material":"Gold"}""").Contains("C30/37"), "an unknown material lists the real ones");
        Equal(8, (int)Call(mcp, "add_columns", """{"points":[[0,0],[6,0],[6,5],[0,5]],"section":"C400"}""")["added"]!, "columns on both stories");
        var beams = Call(mcp, "add_beams", """{"lines":[[0,0,6,0],[6,0,6,5],[6,5,0,5],[0,5,0,0]],"section":"B250x500"}""");
        Equal(8, (int)beams["added"]!, "beams"); Equal("B1@L1", (string)beams["names"]![0]!, "names carry the story");
        var slabs = Call(mcp, "add_slabs", """{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"section":"S180"}""");
        Equal(2, (int)Call(mcp, "add_walls", """{"lines":[[0,0,0,5]],"section":"W200"}""")["added"]!, "a wall panel per story");
        Equal(4, (int)Call(mcp, "set_supports", """{"type":"fixed","at_base":true}""")["done"]!, "supports at the base");
        Call(mcp, "add_load_pattern", """{"name":"SDL","type":"Super Dead"}""");
        Equal(2, (int)Call(mcp, "set_loads", $$"""{"pattern":"SDL","value":2.5,"areas":["{{slabs["names"]![0]}}","{{slabs["names"]![1]}}"]}""")["done"]!, "slab loads");
        Call(mcp, "add_combination", """{"name":"ULS","cases":[{"case":"Dead","factor":1.35},{"case":"SDL","factor":1.35},{"case":"Live","factor":1.5}]}""");
        var context = Call(mcp, "get_model_context", "{}");
        Equal(8, (int)context["counts"]!["columns"]!, "columns"); Equal(2, (int)context["counts"]!["walls"]!, "walls"); Equal("Modeler (no ETABS)", (string)context["etabs"]!, "says it is not ETABS");
        Near(7.2, (double)context["stories"]![2]!["elevation"]!, "story levels");
        Equal(0, (int)Call(mcp, "check_model", "{}")["errors"]!, "no errors");
        Check(Call(mcp, "model_quantities", "{}")["rows"]!.AsArray().Count >= 4, "quantities by section");
        Equal(1, (int)Call(mcp, "delete_objects", """{"frames":["B1@L2"]}""")["done"]!, "one beam on one story deleted");
        Equal(7, (int)Call(mcp, "query_objects", """{"kind":"beam","limit":0}""")["count"]!, "seven beams left");
        Check(Refused(mcp, "run_analysis", "{}").Contains("no ETABS"), "the Modeler never analyses");
        Equal("not available", (string)Call(mcp, "check_drift", "{}")["status"]!, "and has no results");
        Check(Refused(mcp, "add_columns", """{"points":[[1,1]],"stories":["L9"],"section":"C400"}""").Contains("L1, L2"), "unknown story");

        var revision = o.Revision;
        var saved = Call(mcp, "save_model_as", """{"name":"office"}""");
        var file = (string)saved["file"]!;
        Check(file.EndsWith("office.e2k") && File.Exists(file) && File.Exists(Path.ChangeExtension(file, ".cpm")), "both files written");
        Check(o.Revision > revision && !o.Unsaved, "saved");
        var back = E2kReader.ReadFile(file);
        Equal(15, back.LineAssigns.Count, "frames in the ETABS file"); Equal(4, back.AreaAssigns.Count, "areas in the ETABS file");
        Check(back.FrameSections.Exists(x => x.Name == "C400") && back.Combos.Exists(c => c.Name == "ULS") && back.AreaLoads.Count == 2, "sections, combination and loads are in it");
        Check(ModelChecks.Run(back).TrueForAll(f => f.Severity == Severity.Warning), "the file reads back with no errors");
        Equal(15, (int)Call(mcp, "open_model_file", $$"""{"path":{{JsonSerializer.Serialize(file)}}}""")["frames"]!, "the file opens again");
        Equal(4, (int)Call(mcp, "new_model", """{"title":"Frame","template":"concrete_frame","x_spans":[6,6],"y_spans":[5],"stories":4,"story_height":3.2}""")["stories"]!, "the template");

        var page = Pack.LiveViewerHtml(Pack.Build(o, [], [], [], null, null, withTables: false).Content, o.Revision);
        Check(page.Contains("fetch('model?rev='") && page.Contains("\"model\":\"Frame\""), "the live view page carries the model and asks for changes");
        var drawn = Pack.Build(o, [], [], [], null, null, withTables: false).Content["model"]!;
        Check(drawn["grids"] is JsonArray { Count: 5 } && drawn["frame_sizes"] is JsonObject { Count: > 0 } && drawn["supports"] is JsonArray { Count: 6 }, "the view gets grid lines, section sizes and supports");
        Check(page.Contains("Model Explorer") && page.Contains("window.cpmLive=true") && page.Contains("Elevation View"), "the view is the workbench: explorer, plan, elevation and 3-D");
        Equal("fixed", Drawing.Kind([true, true, true, true, true, true]), "a fixed support"); Equal("pinned", Drawing.Kind([true, true, true, false, false, false]), "a pinned support");
        Check(CsiProMax.Host.Shortcuts.Due(null, "C:/a") && CsiProMax.Host.Shortcuts.Due("C:/old", "C:/a") && !CsiProMax.Host.Shortcuts.Due("c:/A|2\n", "C:/a") && CsiProMax.Host.Shortcuts.Due("C:/a", "C:/a"), "shortcuts are made once, and again when the program's folder changes");
        Check(Refused(Endpoint(new MemoryEtabs()), "add_stories", """{"stories":[{"name":"L3","height":3}]}""").Contains("Modeler"), "Modeler tools say so when ETABS is behind");
        Check(CsiProMax.Tools.ToolRegistry.CreateDefault(true).IsBuiltIn("new_model") && !CsiProMax.Tools.ToolRegistry.CreateDefault().IsBuiltIn("new_model"), "Modeler tools are offered in the Modeler only");
    }
    finally { OfflineEtabs.DefaultFolder = before; if (Directory.Exists(folder)) Directory.Delete(folder, true); }
});

Test("assistant: the engineer is asked before deletes, saves and runs", () =>
{
    var e = new MemoryEtabs();
    var asked = new List<string>();
    var no = Endpoint(e, allow: false, asked: asked);
    var beam = (string)Call(no, "add_beams", """{"lines":[[0,0,6,0]],"stories":["Story1"],"section":"B300x600"}""")["names"]![0]!;
    Equal(0, asked.Count, "a normal change is not asked about by default");
    Check(Refused(no, "delete_objects", $$"""{"frames":["{{beam}}"]}""").Contains("declined"), "declined delete");
    Check(Refused(no, "save_model", "{}").Contains("declined"), "declined save");
    Check(Refused(no, "run_analysis", "{}").Contains("declined"), "declined run");
    Equal(1, e.Frames().Count, "the beam is still there"); Equal(0, e.Saves + e.Runs, "nothing saved or run");
    Equal("Delete 1 frame", asked[0], "the question says what will happen");
    var strict = Endpoint(e, allow: false, ask: true);
    Check(Refused(strict, "add_beams", """{"lines":[[0,1,6,1]],"section":"B300x600"}""").Contains("declined"), "with ask-before-changes every change is asked");
    Call(strict, "query_objects", "{}");
    var yes = Endpoint(e);
    Equal(1, (int)Call(yes, "delete_objects", $$"""{"frames":["{{beam}}"]}""")["done"]!, "allowed delete");
    Call(yes, "run_analysis", "{}");
    Check(Refused(yes, "add_load_pattern", """{"name":"W","type":"Wind"}""").Contains("locked"), "a locked model says so");
});

Test("assistant: every tool has a name of its own and a schema", () =>
{
    Equal(EtabsTools.All.Count, EtabsTools.All.Select(t => t.Name).Distinct().Count(), "tool names are unique");
    foreach (var tool in EtabsTools.All)
    {
        var wrapped = new CsiProMax.Tools.ModelTool(tool);
        Check(JsonDocument.Parse(wrapped.PropertiesJson).RootElement.ValueKind == JsonValueKind.Object, $"schema of {tool.Name}");
    }
});

Test("assistant: the chat is given every tool, the drawing reader too", () =>
{
    var registry = CsiProMax.Tools.ToolRegistry.CreateDefault();
    foreach (var name in new[] { "read_cad_drawing", "read_drawing", "make_document", "run_self_test", "memory_note", "ask_user" })
        Check(registry.Find(name) is not null && registry.IsBuiltIn(name), "the chat has no " + name);
    foreach (var tool in EtabsTools.All)
        Check(registry.IsBuiltIn(tool.Name), "the chat has no " + tool.Name);
});

Test("files: a drawing is written as DXF and DWG and read back", () =>
{
    var folder = Directory.CreateTempSubdirectory("cpm").FullName;
    var entities = J("""[{"type":"line","x1":0,"y1":0,"x2":6,"y2":0,"layer":"Grid A"},{"type":"polyline","points":[[0,0],[6,0],[6,5],[0,5]],"closed":true,"layer":"slab"},{"type":"circle","x":3,"y":2.5,"r":0.3,"layer":"columns"},{"type":"text","x":3,"y":3,"height":0.25,"text":"C1","layer":"columns"},{"type":"nope"}]""").EnumerateArray().ToList();
    var (drawn, skipped) = CsiProMax.Tools.CadOut.Write(Path.Combine(folder, "a.dxf"), entities);
    Equal(4, drawn, "four drawn"); Equal(1, skipped, "one skipped");
    var back = JsonNode.Parse(CsiProMax.Tools.ReadCadDrawingTool.Read(Path.Combine(folder, "a.dxf"), [], [], 0, 0).Text)!;
    Equal(4, (int)back["objects"]!, "the DXF reads back");
    Check(back["layers"]!.AsArray().Any(l => (string)l!["layer"]! == "GRID_A"), "layer names are made safe");
    var (dwgDrawn, _) = CsiProMax.Tools.CadOut.Write(Path.Combine(folder, "a.dwg"), entities);
    Equal(4, dwgDrawn, "four in the DWG");
    var overview = JsonNode.Parse(CsiProMax.Tools.ReadCadDrawingTool.Read(Path.Combine(folder, "a.dwg"), [], [], 0, 0).Text)!;
    Equal(4, (int)overview["objects"]!, "the DWG reads back"); Near(6, (double)overview["extents"]!["width"]!, "its extents");
    var columns = JsonNode.Parse(CsiProMax.Tools.ReadCadDrawingTool.Read(Path.Combine(folder, "a.dwg"), ["COLUMNS"], ["text"], 0, 0).Text)!;
    Equal("C1", (string)columns["objects"]![0]!["text"]!, "text on a layer");
    Directory.Delete(folder, true);
});

Test("files: the kinds of file the chat makes", () =>
{
    var folder = Directory.CreateTempSubdirectory("cpm").FullName;
    CsiProMax.Tools.ToolResult Make(string json) => CsiProMax.Tools.MakeDocumentTool.Make(new CsiProMax.Tools.ToolArgs(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!), folder);
    foreach (var (format, ext) in new[] { ("csv", ".csv"), ("text", ".txt"), ("markdown", ".md"), ("word", ".docx"), ("pdf", ".pdf") })
    {
        var made = Make($$"""{"format":"{{format}}","title":"Reactions","content":"# Reactions\n\nA,B\n1,2"}""");
        Check(!made.IsError, format + ": " + made.Text);
        Check(File.Exists(Path.Combine(folder, "Reactions" + ext)), format + " file is there");
    }
    var excel = Make("""{"format":"excel","title":"Sections","sheets":[{"name":"S","columns":["Section","Count"],"rows":[["C500x500",12]]}]}""");
    Check(!excel.IsError && CsiProMax.Chat.ChatFiles.Find(excel.Text).Count == 1, "the Excel file shows as a card: " + excel.Text);
    var dwg = Make("""{"format":"dwg","title":"Layout","entities":[{"type":"line","x1":0,"y1":0,"x2":1,"y2":1}]}""");
    Check(!dwg.IsError && CsiProMax.Chat.ChatFiles.Find(dwg.Text).Single().EndsWith(".dwg"), "a DWG shows as a card: " + dwg.Text);
    Directory.Delete(folder, true);
});

Test("licensing: CSI ProMax keys are its own", () =>
{
    Check(CsiProMax.Licensing.LicenseKey.Prefix.StartsWith("CPM"), "the key prefix");
});

Test("licensing: keys from Sketch ProMax's one License manager work here when they name CSI ProMax", () =>
{
    using var signer = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    string Signed(string prefix, string payload) => prefix + B64(System.Text.Encoding.UTF8.GetBytes(payload)) + "." +
        B64(signer.SignData(System.Text.Encoding.UTF8.GetBytes(payload), System.Security.Cryptography.HashAlgorithmName.SHA256));
    var machine = "11111111-2222-3333-4444-555555555555";
    var spm = CsiProMax.Licensing.LicenseKey.SketchPcCode(machine);
    Check(spm == "SPM-YEX9-X05P-6G8D", "the Sketch ProMax PC code is the same as Sketch ProMax's: " + spm);
    var today = new DateTime(2026, 11, 25);
    var all3 = Signed("SPM1.", $"2|ALL30001|{spm}|Test Friend|2026-11-25|-|sketch,csi,plaxis|LIBRARYKEY");
    var read = CsiProMax.Licensing.LicenseKey.Read(all3, signer);
    Check(read is { Shared: true, ForCsi: true }, "a key for all three apps reads as shared and for CSI ProMax");
    Check(CsiProMax.Licensing.LicenseKey.Verify(all3, spm, today, verifier: signer) == CsiProMax.Licensing.LicenseKey.Check.Valid, "it unlocks this PC");
    Check(CsiProMax.Licensing.LicenseKey.Verify(all3, CsiProMax.Licensing.LicenseKey.PcCode(machine), today, verifier: signer) == CsiProMax.Licensing.LicenseKey.Check.WrongPc,
        "it is bound to the Sketch ProMax PC code, not the CPM- one");
    var sketchOnly = Signed("SPM1.", $"1|SKETCH01|{spm}|Test Friend|2026-11-25|-|LIBRARYKEY");
    Check(CsiProMax.Licensing.LicenseKey.Verify(sketchOnly, spm, today, verifier: signer) == CsiProMax.Licensing.LicenseKey.Check.OtherProgram, "a Sketch-only key isn't for CSI ProMax");
    var plaxisOnly = Signed("SPM1.", $"2|PLAXIS01|{spm}|Test Friend|2026-11-25|-|plaxis");
    Check(CsiProMax.Licensing.LicenseKey.Verify(plaxisOnly, spm, today, verifier: signer) == CsiProMax.Licensing.LicenseKey.Check.OtherProgram, "a Plaxis-only key isn't for CSI ProMax");
    var own = Signed("CPM1.", $"1|CSIOWN01|{CsiProMax.Licensing.LicenseKey.PcCode(machine)}|Test Friend|2026-11-25|-");
    Check(CsiProMax.Licensing.LicenseKey.Verify(own, CsiProMax.Licensing.LicenseKey.PcCode(machine), today, verifier: signer) == CsiProMax.Licensing.LicenseKey.Check.Valid,
        "CSI ProMax's own keys still work");
    Check(CsiProMax.Licensing.LicenseState.Explain(CsiProMax.Licensing.LicenseKey.Check.OtherProgram).Contains("includes CSI ProMax"), "it says what to ask for");
});

Test("assistant: ETABS's API is called by name, with numbers for its enums", () =>
{
    dynamic api = new EtabsApi(new ETABSv1.FakeApp(), typeof(ETABSv1.cFakeApp));
    dynamic model = api.SapModel;
    int n = 0; string[] names = []; int unit = 0;
    Equal(0, (int)model.GetNames(ref n, ref names, ref unit), "the call's own answer");
    Equal(2, n, "a number comes back"); Equal("B", names[1], "a list comes back"); Equal(6, unit, "an enum comes back as its number");
    Equal(4, (int)model.Add("x", 4, 1), "a number goes in as the enum, a whole number as a decimal");
    Equal(7, (int)model.Add("x", 7), "a left-out value takes its default");
    try { model.Nope(); throw new Exception("accepted a method ETABS doesn't have"); }
    catch (MissingMethodException ex) { Check(ex.Message.Contains("cFakeModel has no Nope"), ex.Message); }
});

Test("plug-in: the assistant is started with the dotnet ETABS runs on", () =>
{
    var root = Directory.CreateTempSubdirectory("cpm").FullName;
    var runtime = Directory.CreateDirectory(Path.Combine(root, "dotnet", "shared", "Microsoft.NETCore.App", "8.0.29")).FullName;
    var folder = Directory.CreateDirectory(Path.Combine(root, "CSI ProMax")).FullName;
    string Message() { try { CsiProMaxPlugin.Launcher.StartInfo(folder, runtime, 42004, "ETABS", [Path.Combine(root, "dotnet")]); return ""; } catch (IOException ex) { return ex.Message; } }
    Check(Message().Contains("CsiProMax.dll is missing"), "a plug-in copied alone says what is missing");
    File.WriteAllText(Path.Combine(folder, "CsiProMax.dll"), "");
    Check(Message().Contains(".NET 8 Desktop Runtime"), "no dotnet: the message says what to install");
    var dotnet = Path.Combine(root, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    File.WriteAllText(dotnet, "");
    var start = CsiProMaxPlugin.Launcher.StartInfo(folder, runtime, 42004);
    Equal(dotnet, start.FileName, "dotnet from three folders above the runtime");
    Equal($"\"{Path.Combine(folder, "CsiProMax.dll")}\" --etabs-pid 42004 --program etabs", start.Arguments, "the path is quoted, and the assistant is told which window and program started it");
    var older = CsiProMaxPlugin.Launcher.StartInfo(folder, @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319", 7, "SAP2000", [Path.Combine(root, "nope"), Path.Combine(root, "dotnet")]);
    Equal(dotnet, older.FileName, "a program on .NET Framework: the PC's own .NET 8 is found");
    Check(older.Arguments.EndsWith("--etabs-pid 7 --program sap2000"), "the program is named");
    Check(start.CreateNoWindow && !start.UseShellExecute, "no console window");
    Directory.Delete(root, true);
});

foreach (var (name, body) in SafeTests.All()) Test(name, body);

var failed = 0;
// ---- SAP2000 ----

Test("table text: values quoted where needed, read back whole", () =>
{
    var text = CsiProMax.CsiTables.CsiTableText.Document("File x.s2k", [CsiProMax.CsiTables.CsiTableText.Table("Joint Coordinates",
        [[("Joint", "1"), ("Note", "two words"), ("Empty", ""), ("Z", CsiProMax.CsiTables.CsiTableText.Number(-0.0))]])]);
    Check(text.Contains("TABLE:  \"JOINT COORDINATES\""), "heading upper-cased");
    Check(text.Contains("Note=\"two words\"") && text.Contains("Empty=\"\"") && text.Contains("Z=0"), "quoting");
    Check(text.TrimEnd().EndsWith("END TABLE DATA"), "end line");
    var back = CsiProMax.CsiTables.CsiTableText.Parse(text + "TABLE:  \"X\"\r\n   A=1   B=\"long _\r\n value\"\r\n");
    Equal("two words", back["joint coordinates"][0]["Note"], "quoted value");
    Equal(1, back["JOINT COORDINATES"].Count, "rows");
});

Test("s2k: the template frame becomes joints, frames and areas in 3D", () =>
{
    CsiProMax.S2k.S2kWriter.SavedAt = new DateTime(2026, 11, 25, 6, 0, 0);
    var m = Templates.ConcreteFrame("T", [6, 6], [5], 3, 3.5);
    var r = CsiProMax.S2k.S2kWriter.Write(m, "t.s2k");
    Equal(24, r.Joints, "joints: 6 plan points on 4 levels");
    Equal(39, r.Frames, "frames: 18 columns and 21 beams");
    Equal(6, r.Areas, "areas");
    var t = CsiProMax.CsiTables.CsiTableText.Parse(r.Text);
    Check(r.Text.StartsWith("File t.s2k was saved on 11/25/26 at 6:00:00"), "header line");
    Equal("KN, m, C", t["PROGRAM CONTROL"][0]["CurrUnits"], "units");
    Equal(24, t["JOINT COORDINATES"].Count, "joint rows");
    Equal(39, t["CONNECTIVITY - FRAME"].Count, "frame rows");
    Equal(39, t["FRAME SECTION ASSIGNMENTS"].Count, "every frame has its section");
    Equal(6, t["JOINT RESTRAINT ASSIGNMENTS"].Count, "fixed supports at the base");
    Check(t["JOINT RESTRAINT ASSIGNMENTS"].All(x => x["U1"] == "Yes" && x["R3"] == "Yes"), "fixed");
    var top = t["JOINT COORDINATES"].Max(x => double.Parse(x["Z"], System.Globalization.CultureInfo.InvariantCulture));
    Near(10.5, top, "top level");
    var c1 = t["CONNECTIVITY - FRAME"].First(x => x["Frame"] == "C1_Story1");
    var zi = t["JOINT COORDINATES"].First(x => x["Joint"] == c1["JointI"])["Z"];
    var zj = t["JOINT COORDINATES"].First(x => x["Joint"] == c1["JointJ"])["Z"];
    Equal("0", zi, "column bottom at the base"); Equal("3.5", zj, "column top at story 1");
    Equal(4, t["GRID LINES"].Count(x => x["AxisDir"] == "Z"), "a Z grid per level");
    Equal(3, t["CONSTRAINT DEFINITIONS - DIAPHRAGM"].Count, "one diaphragm per story");
    Equal(12, t["AREA LOADS - UNIFORM"].Count, "SDL and live on 2 slabs on 3 stories");
    Check(t["AREA LOADS - UNIFORM"].All(x => x["Dir"] == "Gravity"), "gravity");
    Equal("Dead", t["LOAD PATTERN DEFINITIONS"].First(x => x["LoadPat"] == "Dead")["DesignType"], "pattern type");
    Check(t["LOAD CASE DEFINITIONS"].Any(x => x["Case"] == "Modal" && x["Type"] == "LinModal"), "modal case");
    Check(t["LOAD CASE DEFINITIONS"].Any(x => x["Case"] == "Dead" && x["Type"] == "LinStatic"), "static case");
    Equal(3, t["GROUPS 1 - DEFINITIONS"].Count + 0 - (t["GROUPS 1 - DEFINITIONS"].Any(x => x["GroupName"] == "Base") ? 1 : 0), "a group per story");
    Check(t["FRAME SECTION PROPERTIES 01 - GENERAL"].Any(x => x["SectionName"] == "C500x500" && x["Shape"] == "Rectangular" && x["t3"] == "0.5"), "rectangle");
    Check(t["MATERIAL PROPERTIES 03B - CONCRETE DATA"].Any(x => x["Material"] == "C30/37" && x["Fc"] == "30000"), "concrete strength");
    Check(t["AREA SECTION PROPERTIES"].Any(x => x["Section"] == "S200" && x["Thickness"] == "0.2"), "slab");
});

Test("s2k: combinations, point and line loads go across", () =>
{
    var m = Templates.ConcreteFrame("T", [6], [5], 1, 3);
    m.AddCombo("ULS", ("Dead", 1.35), ("Live", 1.5));
    m.AddLineLoad("B1", "Story1", "Dead", 4.5);
    m.PointLoads.Add(new PointLoad { Object = m.Points[0].Name, Story = "Story1", Tokens = ["TYPE", "\"FORCE\"", "LC", "\"Live\"", "FZ", "-10"] });
    var r = CsiProMax.S2k.S2kWriter.Write(m);
    var t = CsiProMax.CsiTables.CsiTableText.Parse(r.Text);
    var combo = t["COMBINATION DEFINITIONS"].Where(x => x["ComboName"] == "ULS").ToList();
    Equal(2, combo.Count, "two cases");
    Equal("Linear Add", combo[0]["ComboType"], "type");
    Equal("1.5", combo[1]["ScaleFactor"], "factor");
    var line = t["FRAME LOADS - DISTRIBUTED"].Single();
    Equal("B1_Story1", line["Frame"], "frame"); Equal("4.5", line["FOverLA"], "value"); Equal("6", line["AbsDistB"], "length");
    Equal("-10", t["JOINT LOADS - FORCE"].Single()["F3"], "point load");
});

Test("s2k: section properties", () =>
{
    var rect = CsiProMax.S2k.S2kWriter.Rectangle(0.6, 0.3);
    Near(0.18, rect.A, "area"); Near(0.3 * Math.Pow(0.6, 3) / 12, rect.I33, "I33"); Near(0.6 * Math.Pow(0.3, 3) / 12, rect.I22, "I22");
    Check(Math.Abs(rect.J - 0.0037) < 0.0002, $"torsion of a 2:1 rectangle about 0.229 b^3 d, got {rect.J}");
    var i = CsiProMax.S2k.S2kWriter.ISection(0.5, 0.2, 0.02, 0.01, 0.2, 0.02);
    Near(0.2 * 0.02 * 2 + 0.46 * 0.01, i.A, "I area");
    Near((0.2 * Math.Pow(0.5, 3) - 0.19 * Math.Pow(0.46, 3)) / 12, i.I33, "I33 of a doubly symmetric I");
});

Test("SAP2000: joints, frames and areas read one by one, no stories", () =>
{
    var sap = new SapStandIn();
    var link = new CsiProMax.Assistant.Sap2000.SapLink(sap);
    Equal("SAP2000", link.Program.Name, "program");
    Equal(0, link.Stories().Count, "no stories");
    Equal(4, link.Points().Count, "joints");
    var frames = link.Frames();
    Equal(2, frames.Count, "frames");
    Equal("column", frames.First(f => f.Name == "1").Kind, "vertical");
    Equal("brace", frames.First(f => f.Name == "2").Kind, "sloping");
    Equal("FSEC1", frames[0].Section, "section");
    Equal("", frames[0].Story, "no story");
    var area = link.Areas().Single();
    Equal("wall", area.Kind, "upright area");
    Equal(3, area.Corners.Count, "corners");
    Equal("floor", CsiProMax.Assistant.Sap2000.SapLink.AreaKind([(0, 0, 1), (1, 0, 1), (1, 1, 1)]), "level area");
    Equal("shell", CsiProMax.Assistant.Sap2000.SapLink.AreaKind([(0, 0, 0), (1, 0, 0), (1, 1, 1)]), "sloping area");
});

Test("SAP2000: messages name SAP2000", () =>
{
    var link = new CsiProMax.Assistant.Sap2000.SapLink(new SapStandIn { Fail = true });
    try { link.Info(); throw new Exception("no error"); }
    catch (EtabsException ex) { Check(ex.Message.Contains("SAP2000") && !ex.Message.Contains("ETABS"), ex.Message); }
});

Test("SAP2000: its tools replace the story tools and add its own", () =>
{
    var tools = ProgramTools.For(CsiProgram.Sap2000);
    foreach (var name in new[] { "add_joints", "add_frames", "add_areas", "set_restraints", "joint_displacements", "frame_forces", "base_reactions", "modal_results", "check_displacement", "run_design", "design_ratios", "check_report", "get_model_context" })
        Check(tools.Any(t => t.Name == name), name);
    var addColumns = tools.First(t => t.Name == "add_columns");
    try { addColumns.Run(JsonDocument.Parse("{}").RootElement, new MemoryEtabs()); throw new Exception("no error"); }
    catch (ToolInputException ex) { Check(ex.Message.Contains("add_frames"), ex.Message); }
    Equal(0, ProgramTools.For(CsiProgram.Etabs).Count, "ETABS has only the shared tools");
    var always = ProgramTools.Every.Where(t => t.Ask == Ask.Always).Select(t => t.Name).ToHashSet();
    Check(always.IsSupersetOf(["delete_joints", "run_design", "save_model_s2k"]), "always asked");
    Check(ProgramTools.Modeler.Any(t => t.Name == "save_model_s2k"), "the Modeler writes .s2k");
    Check(ProgramTools.PackTable(CsiProgram.Sap2000, "Element Forces - Frames") && !ProgramTools.PackTable(CsiProgram.Sap2000, "Story Drifts"), "pack tables");
});

Test("SAP2000: add_frames sends coordinates and joint pairs", () =>
{
    var sap = new SapStandIn();
    var link = new CsiProMax.Assistant.Sap2000.SapLink(sap);
    var tool = ProgramTools.For(CsiProgram.Sap2000).First(t => t.Name == "add_frames");
    var result = tool.Run(JsonDocument.Parse("""{"section":"W1","lines":[[0,0,0,0,0,3]],"joints":[["1","2"]]}""").RootElement, link);
    Equal(2, result["added"]!.GetValue<int>(), "added");
    Equal("coord W1;point 1 2 W1", string.Join(";", sap.Added), "calls");
});

Test("SAP2000: results through the analysis results", () =>
{
    var sap = new SapStandIn();
    var link = new CsiProMax.Assistant.Sap2000.SapLink(sap);
    var tools = ProgramTools.For(CsiProgram.Sap2000);
    var d = tools.First(t => t.Name == "joint_displacements").Run(JsonDocument.Parse("""{"cases":["DEAD"]}""").RootElement, link);
    Near(-0.012, d["largest"]!["U3"]!["value"]!.GetValue<double>(), "largest U3");
    Equal("2", d["largest"]!["U3"]!["joint"]!.GetValue<string>(), "at joint");
    Equal("DEAD", string.Join(",", sap.Output), "the case chosen for output");
    var check = tools.First(t => t.Name == "check_displacement").Run(JsonDocument.Parse("""{"lateral_limit":500}""").RootElement, link);
    Near(3.0 / 500, check["lateral"]!["allowed"]!.GetValue<double>(), "H/500");
    Check(check["lateral"]!["passes"]!.GetValue<bool>(), "0.004 < 0.006");
    var modal = tools.First(t => t.Name == "modal_results").Run(JsonDocument.Parse("{}").RootElement, link);
    Check(!modal["passes"]!.GetValue<bool>(), "85% in UY is under 90%");
});

Test("plug-in: SAP2000 starts the assistant for SAP2000", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "cpm-sap-" + Guid.NewGuid().ToString("N"));
    var folder = Path.Combine(root, "plugin");
    Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.Combine(root, "dotnet"));
    File.WriteAllText(Path.Combine(folder, "CsiProMax.dll"), "");
    File.WriteAllText(Path.Combine(root, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");
    try
    {
        var start = CsiProMaxPlugin.Launcher.StartInfo(folder, "/x", 9, "SAP2000", [Path.Combine(root, "dotnet")]);
        Check(start.Arguments.EndsWith("--etabs-pid 9 --program sap2000"), start.Arguments);
        Equal(CsiProgram.Sap2000, CsiProgram.Find("sap2000"), "program row");
        Check(CsiProgram.Sap2000.Ready, "SAP2000 is offered");
    }
    finally { Directory.Delete(root, true); }
});

foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"ok    {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}
Console.WriteLine($"\n{tests.Count - failed} of {tests.Count} passed.");
return failed == 0 ? 0 : 1;

namespace ETABSv1
{
    // A stand-in shaped like ETABS's API (interfaces in the ETABSv1 namespace, ref lists, enums), for the EtabsApi test.
    public enum eFakeLength { mm = 4, m = 6 }
    public interface cFakeModel
    {
        int GetNames(ref int count, ref string[] names, ref eFakeLength unit);
        int Add(string name, eFakeLength unit, double factor = 1);
    }
    public interface cFakeApp { cFakeModel SapModel { get; } }
    public sealed class FakeModel : cFakeModel
    {
        public int GetNames(ref int count, ref string[] names, ref eFakeLength unit) { count = 2; names = ["A", "B"]; unit = eFakeLength.m; return 0; }
        public int Add(string name, eFakeLength unit, double factor = 1) => (int)unit * (int)factor;
    }
    public sealed class FakeApp : cFakeApp { public cFakeModel SapModel { get; } = new FakeModel(); }
}


/// <summary>Runs a tool as the panel does, with the approvals' asking.</summary>
sealed class Harness(ICsiLink etabs, bool allow, bool ask, List<string>? asked)
{
    public (string Text, bool Failed) Call(string name, JsonElement args)
    {
        var found = EtabsTools.All.Concat(ModelerTools.All).FirstOrDefault(t => t.Name == name);
        if (found is null) return ($"There is no tool '{name}'.", true);
        var tool = new CsiProMax.Tools.ModelTool(found);
        var input = new CsiProMax.Tools.ToolArgs(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())!);
        if (tool.AlwaysAsks || tool.ModifiesModel && ask)
        {
            asked?.Add(tool.Summary(input) ?? name);
            if (!allow) return ("The engineer declined this. Nothing was changed.", true);
        }
        var result = tool.Execute(new CsiProMax.ETABS.EtabsApp(etabs), input);
        return (result.Text, result.IsError);
    }
}

/// <summary>A small SAP2000 model behind the calls SAP2000's API answers: four joints, a column, a brace, an area, and results.</summary>
public sealed class SapStandIn : ICsiLink
{
    public bool Fail { get; init; }
    public List<string> Added { get; } = [];
    public List<string> Output { get; } = [];
    private readonly Model _m;
    public SapStandIn() => _m = new Model(this);

    public ModelInfo Info() => Fail ? throw new EtabsException("ETABS is not open. Open ETABS and a model, then try again.") : new("C:/m.sdb", "26.0.0", "kN", "m", "C", true);
    public object? Raw(string what, Func<dynamic, object?> work) => work(_m);

    public sealed class Model(SapStandIn owner)
    {
        public PointApi PointObj { get; } = new();
        public FrameApi FrameObj { get; } = new(owner);
        public AreaApi AreaObj { get; } = new();
        public ResultsApi Results { get; } = new(owner);
    }

    public sealed class PointApi
    {
        private static readonly (string N, double X, double Y, double Z)[] All = [("1", 0, 0, 0), ("2", 0, 0, 3), ("3", 4, 0, 0), ("4", 4, 0, 3)];
        public int GetNameList(ref int n, ref string[] names) { n = All.Length; names = All.Select(p => p.N).ToArray(); return 0; }
        public int GetCoordCartesian(string name, ref double x, ref double y, ref double z, string csys)
        { var p = All.First(a => a.N == name); x = p.X; y = p.Y; z = p.Z; return 0; }
    }

    public sealed class FrameApi(SapStandIn owner)
    {
        public int GetNameList(ref int n, ref string[] names) { n = 2; names = ["1", "2"]; return 0; }
        public int GetPoints(string name, ref string i, ref string j) { (i, j) = name == "1" ? ("1", "2") : ("3", "2"); return 0; }
        public int GetSection(string name, ref string section, ref string auto) { section = "FSEC1"; return 0; }
        public int AddByPoint(string i, string j, ref string name, string section, string user) { owner.Added.Add($"point {i} {j} {section}"); name = "9"; return 0; }
    }

    public sealed class AreaApi
    {
        public int GetNameList(ref int n, ref string[] names) { n = 1; names = ["A1"]; return 0; }
        public int GetPoints(string name, ref int n, ref string[] corners) { n = 3; corners = ["1", "3", "4"]; return 0; }
        public int GetProperty(string name, ref string section) { section = "ASEC1"; return 0; }
    }

    public sealed class ResultsApi(SapStandIn owner)
    {
        public SetupApi Setup { get; } = new(owner);
        public int JointDispl(string name, int item, ref int n, ref string[] obj, ref string[] elm, ref string[] cases, ref string[] steps, ref double[] num,
            ref double[] u1, ref double[] u2, ref double[] u3, ref double[] r1, ref double[] r2, ref double[] r3)
        {
            n = 2; obj = ["2", "4"]; elm = obj; cases = ["DEAD", "DEAD"]; steps = ["", ""]; num = [0, 0];
            u1 = [0.004, 0.003]; u2 = [0, 0.001]; u3 = [-0.012, -0.002]; r1 = [0, 0]; r2 = [0, 0]; r3 = [0, 0];
            return 0;
        }
        public int ModalParticipatingMassRatios(ref int n, ref string[] cases, ref string[] steps, ref double[] num, ref double[] period,
            ref double[] ux, ref double[] uy, ref double[] uz, ref double[] sux, ref double[] suy, ref double[] suz,
            ref double[] rx, ref double[] ry, ref double[] rz, ref double[] srx, ref double[] sry, ref double[] srz)
        {
            n = 2; cases = ["MODAL", "MODAL"]; steps = ["Mode", "Mode"]; num = [1, 2]; period = [0.8, 0.5];
            ux = [0.92, 0]; uy = [0, 0.85]; uz = [0, 0]; sux = [0.92, 0.92]; suy = [0, 0.85]; suz = [0, 0];
            rx = [0, 0]; ry = [0, 0]; rz = [0, 0]; srx = [0, 0]; sry = [0, 0]; srz = [0, 0];
            return 0;
        }
    }

    public sealed class SetupApi(SapStandIn owner)
    {
        public int DeselectAllCasesAndCombosForOutput() { owner.Output.Clear(); return 0; }
        public int SetCaseSelectedForOutput(string name, bool selected) { owner.Output.Add(name); return 0; }
        public int SetComboSelectedForOutput(string name, bool selected) { owner.Output.Add(name); return 0; }
    }

    public IReadOnlyList<StoryRow> Stories() => [];
    public IReadOnlyList<FrameRow> Frames() => [];
    public IReadOnlyList<AreaRow> Areas() => [];
    public IReadOnlyList<PointRow> Points() => [];
    public bool[] Restraint(string point) => new bool[6];
    public IReadOnlyList<(string Kind, string Name)> Selected() => [];
    public void ClearSelection() { }
    public void Select(string kind, string name) { }
    public IReadOnlyList<string> FrameSections() => ["FSEC1"];
    public IReadOnlyList<string> AreaSections() => ["ASEC1"];
    public IReadOnlyList<string> Materials() => ["4000Psi"];
    public IReadOnlyList<LoadPatternRow> LoadPatterns() => [new("DEAD", "Dead", 1)];
    public IReadOnlyList<string> LoadCases() => ["DEAD", "MODAL"];
    public IReadOnlyList<string> Combos() => [];
    public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section) { Added.Add("coord " + section); return "8"; }
    public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => "A9";
    public void SetFrameSection(string name, string section) { }
    public void SetAreaSection(string name, string section) { }
    public void Delete(string kind, string name) { }
    public void SetRestraint(string point, bool[] restraint) { }
    public void AddLoadPattern(string name, string type, double selfWeight) { }
    public void SetFrameLoad(string frame, string pattern, double value, bool replace) { }
    public void SetAreaLoad(string area, string pattern, double value, bool replace) { }
    public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) { }
    public IReadOnlyList<TableName> Tables() => [];
    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => new([], []);
    public void RunAnalysis() { }
    public void Save() { }
    public void Refresh() { }
}
