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
static void Throws<T>(Action act, string what) where T : Exception { try { act(); } catch (T) { return; } throw new Exception(what + ": nothing was thrown"); }
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
    e.Locked = true;   // the analysis has run: only now may a check pass or fail
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
    e.Locked = true;   // analysed: the checks answer from the result tables below
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
    // The Viewer's own screen, in a real browser when this machine has one.
    if (CsiProMax.Assistant.ViewerCheck.FindBrowser() is not null)
    {
        var shots = Path.Combine(Path.GetTempPath(), "cpm-viewer-check");
        var seen = CsiProMax.Assistant.ViewerCheck.Run(built.Content, shots, "test");
        Check(seen.NotRun is null, "the browser ran: " + seen.NotRun);
        if (Environment.GetEnvironmentVariable("CPM_SHOW_VIEWER_CHECK") == "1")
            foreach (var i in seen.Items) Console.WriteLine($"   {(i.Skipped ? "skip" : i.Ok ? "ok  " : "FAIL")} {i.Name}: {i.Detail}");
        Check(seen.Items.Count > 10 && seen.Items.All(i => i.Ok || i.Skipped), "the Viewer's screen: " + string.Join("; ", seen.Items.Where(i => !i.Ok && !i.Skipped).Select(i => i.Name + ": " + i.Detail)));
        Check(seen.Pictures.Count == CsiProMax.Assistant.ViewerCheck.Shots.Length, $"a picture of each view: {seen.Pictures.Count}");
        // Seen on 2026-11-25 with 0.4.20: the story, grid line and animation buttons were "not tested" on every
        // model, because they only work in a plan, an elevation or on the deformed shape and were pressed in 3-D.
        var left = seen.Items.Where(i => i.Skipped && (i.Name.Contains("story", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("grid line", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("Animate", StringComparison.Ordinal))).ToList();
        Check(left.Count == 0, "buttons left untested on a model that has stories, grid lines and results: " + string.Join("; ", left.Select(i => i.Name + ": " + i.Detail)));
    }

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

Test("design: every kind of design table is sorted into its kind, and analysis tables are not design", () =>
{
    foreach (var (table, kind) in new[]
             {
                 ("Steel Frame Design Summary - AISC 360-16", "steel"), ("Concrete Beam Flexure Envelope - ACI 318-19", "concrete"), ("Concrete Column PMM Envelope - ACI 318-14", "concrete"),
                 ("Concrete Joint Envelope - ACI 318-19", "concrete"), ("Concrete Column Summary - ACI 318-19", "concrete"), ("Composite Beam Design Summary - AISC 360-16", "composite"),
                 ("Composite Column Design Summary - AISC 360-16", "compcol"), ("Steel Joist Design Summary", "joist"), ("Shear Wall Pier Design Summary - ACI 318-19", "pier"),
                 ("Shear Wall Spandrel Design Summary - ACI 318-19", "spandrel"), ("Pier Design Envelope - Eurocode 2-2004", "pier"), ("Concrete Slab Design Summary - Strip Based Design", "slab"),
                 ("Concrete Slab Design - Punching Shear Data", "punching"), ("Steel Design 1 - Summary Data - AISC 360-16", "steel"), ("Concrete Design 2 - Beam Summary Data - ACI 318-14", "concrete"),
                 ("Steel Frame Design Summary (from the API)", "steel"),
             })
        Equal(kind, DesignResults.FamilyOf(table)?.Key, table);
    foreach (var table in new[] { "Pier Forces", "Spandrel Forces", "Mass Summary by Story", "Modal Participating Mass Ratios", "Story Drifts", "Shear Wall Pier Design Preferences", "Steel Frame Design Overwrites - AISC 360-16", "Concrete Frame Design Preferences - ACI 318-19", "Area Assignments - Pier Labels", "Element Forces - Beams" })
        Check(DesignResults.FamilyOf(table) is null, "not a design result: " + table);
    Check(DesignResults.IsWallTable("Area Assignments - Pier Labels") && DesignResults.IsWallTable("Frame Assignments - Spandrel Labels") && DesignResults.IsWallTable("Pier Section Properties"), "the wall label tables");
});

Test("design: shear wall piers and spandrels, concrete beams and columns all go into the pack, with the walls' labels", () =>
{
    var e = new MemoryEtabs();
    var mcp = Endpoint(e);
    Call(mcp, "add_columns", """{"points":[[0,0],[6,0]],"section":"C500x500"}""");
    Call(mcp, "add_beams", """{"lines":[[0,0,6,0]],"section":"B300x600"}""");
    Call(mcp, "add_walls", """{"lines":[[10,0,13,0],[15,0,18,0]],"section":"W250"}""");
    Call(mcp, "add_walls", """{"lines":[[13,0,15,0]],"stories":["Story2","Story3"],"section":"W250"}""");
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var walls = e.Areas().Where(a => a.Kind == "wall").ToList();
    string StoryOf(AreaRow a) => e.Stories().First(s => Math.Abs(s.Elevation - a.Corners.Max(c => c.Z)) < 1e-6).Name;
    string PierOf(AreaRow a) => a.Corners.Max(c => c.X) <= 13.001 ? "P1" : a.Corners.Min(c => c.X) >= 14.999 ? "P2" : "";
    e.ResultTables["Area Assignments - Pier Labels"] = new(["Story", "Label", "UniqueName", "Pier Name"], walls.Where(a => PierOf(a) != "").Select(a => new[] { StoryOf(a), a.Name, a.Name, PierOf(a) }).ToList());
    e.ResultTables["Area Assignments - Spandrel Labels"] = new(["Story", "Label", "UniqueName", "Spandrel Name"], walls.Where(a => PierOf(a) == "").Select(a => new[] { StoryOf(a), a.Name, a.Name, "S1" }).ToList());
    e.ResultTables["Shear Wall Pier Design Summary - ACI 318-19"] = new(["Story", "Pier", "Centerline", "Station", "Required Reinf Ratio", "Current Reinf Ratio", "Flexural Combo", "D/C Ratio", "Shear Rebar"],
        e.Stories().Skip(1).SelectMany(s => new[] { "P1", "P2" }.SelectMany(p => new[] { "Top", "Bottom" }.Select(st => new[] { s.Name, p, "Leg 1", st, "0.0125", "0.0125", "ULS1", p == "P1" && s.Name == "Story1" && st == "Bottom" ? "1.12" : "0.64", "0.00075" }))).ToList());
    e.ResultTables["Shear Wall Spandrel Design Summary - ACI 318-19"] = new(["Story", "Spandrel", "Station", "As Top", "As Bottom", "Av Vert", "Shear Combo"],
        new[] { "Story2", "Story3" }.SelectMany(s => new[] { "Left", "Right" }.Select(st => new[] { s, "S1", st, "0.00062", "0.00048", "0.0009", "ULS2" })).ToList());
    var beam = e.Frames().First(f => f.Kind == "beam");
    e.ResultTables["Concrete Beam Flexure Envelope - ACI 318-19"] = new(["Story", "Label", "UniqueName", "Station", "As Top", "As Bottom", "As Top Combo"],
        new[] { "0.25", "3", "5.75" }.Select(st => new[] { beam.Story, beam.Name, beam.Name, st, st == "3" ? "0.0003" : "0.0011", "0.0006", "ULS1" }).ToList());
    e.ResultTables["Concrete Column PMM Envelope - ACI 318-19"] = new(["Story", "Label", "UniqueName", "Station", "PMM Combo", "PMM Ratio", "Rebar %"],
        e.Frames().Where(f => f.Kind == "column").Select(f => new[] { f.Story, f.Name, f.Name, "0", "ULS1", "0.81", "1.2" }).ToList());
    e.ResultTables["Shear Wall Pier Design Preferences"] = new(["Item", "Value"], [["Code", "ACI 318-19"]]);
    e.Locked = true;

    var built = Pack.Build(e, [], [], [], null, null);
    foreach (var t in new[] { "Shear Wall Pier Design Summary - ACI 318-19", "Shear Wall Spandrel Design Summary - ACI 318-19", "Concrete Beam Flexure Envelope - ACI 318-19", "Concrete Column PMM Envelope - ACI 318-19", "Area Assignments - Pier Labels", "Area Assignments - Spandrel Labels" })
        Check(built.Tables.Contains(t), "the pack takes " + t);
    Check(!built.Tables.Contains("Shear Wall Pier Design Preferences"), "preferences are not results");
    var design = built.Content["manifest"]!["design"]!.AsObject();
    foreach (var k in new[] { "pier", "spandrel", "concrete" }) Check(design.ContainsKey(k), "the manifest says where the " + k + " design came from");
    var labels = built.Content["model"]!["wall_labels"]!.AsArray();
    Equal(walls.Count, labels.Count, "every wall panel has its pier or spandrel");
    Check(labels.Any(l => (string)l![2]! == "P1") && labels.Any(l => (string)l![3]! == "S1"), "piers and spandrels");

    // The check reads the pier's D/C, not its reinforcing ratio.
    var ratios = ResultTools.DesignCheck(e.ResultTables["Shear Wall Pier Design Summary - ACI 318-19"], "pier", 1.0, 10)!;
    Equal(1, (int)ratios["over_limit"]!, "one pier station over 1.0"); Equal("D/C Ratio", (string)ratios["worst"]!["governs"]!, "the D/C governs, not the reinforcing ratio");
    Equal("P1", (string)ratios["worst"]!["label"]!, "the pier is named");
    if (Environment.GetEnvironmentVariable("CPM_KEEP_PACK") is { Length: > 0 } keep) File.WriteAllText(Path.Combine(keep, "design viewer.html"), Pack.ViewerHtml(built.Content));
});

Test("design: a kind with empty tables says what to run in the program, and run_design asks first and needs the analysis", () =>
{
    var e = new MemoryEtabs();
    e.ResultTables["Shear Wall Pier Design Summary - ACI 318-19"] = new(["Story", "Pier", "D/C Ratio"], []);
    var built = Pack.Build(e, [], [], [], null, null);
    Check(built.Notes.Any(n => n.Contains("Shear Wall Pier Design has no results") && n.Contains("Design > Shear Wall Design > Start Design/Check") && n.Contains("Excel")), "the note says what to run: " + string.Join(" | ", built.Notes));
    Check(built.Content["manifest"]!["design"]!["not_run"]!.AsArray().Any(n => (string)n! == "Shear Wall Pier Design"), "the Viewer is told too");
    Check(Refused(Endpoint(e), "run_design", "{}").Contains("has not run"), "no design before the analysis");
    var asked = new List<string>();
    e.Locked = true;
    Check(Refused(Endpoint(e, allow: false, asked: asked), "run_design", "{}").Contains("declined") && asked.Count == 1, "the engineer is asked first");
    var offline = new OfflineEtabs(E2kReader.Read(sample));
    Check(Refused(Endpoint(offline), "run_design", "{}").Contains("cannot analyse or design"), "the Modeler says no");
});

Test("design: exported Excel and text table files fill what the program's tables lack, and the .e2k's pier labels are read", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "cpm-design-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        // CSI's Excel layout: a sheet per table, "TABLE:  name", the fields, the units, the rows.
        var xlsx = Path.Combine(folder, "Tower tables.xlsx");
        using (var zip = System.IO.Compression.ZipFile.Open(xlsx, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Put(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            Put("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Shear Wall Pier Design Su" sheetId="1" r:id="rId1"/><sheet name="Story Drifts" sheetId="2" r:id="rId2"/></sheets></workbook>""");
            Put("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="worksheets/sheet2.xml"/></Relationships>""");
            Put("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>TABLE:  Shear Wall Pier Design Summary - ACI 318-19</t></si><si><t>Story</t></si><si><t>Pier</t></si><si><t>Station</t></si><si><t>D/C Ratio</t></si><si><t>Story1</t></si><si><t>P1</t></si><si><t>Top</t></si></sst>""");
            Put("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c></row><row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2" t="s"><v>2</v></c><c r="C2" t="s"><v>3</v></c><c r="D2" t="s"><v>4</v></c></row><row r="3"><c r="D3" t="inlineStr"><is><t>Unitless</t></is></c></row><row r="4"><c r="A4" t="s"><v>5</v></c><c r="B4" t="s"><v>6</v></c><c r="C4" t="s"><v>7</v></c><c r="D4"><v>0.912</v></c></row></sheetData></worksheet>""");
            Put("xl/worksheets/sheet2.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>TABLE:  Story Drifts</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>Story</t></is></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>Story1</t></is></c></row></sheetData></worksheet>""");
        }
        var read = DesignResults.ReadExport(xlsx);
        Equal(2, read.Count, "two tables"); Equal("Shear Wall Pier Design Summary - ACI 318-19", read[0].Name, "the name from the first row, not the cut sheet name");
        Equal("Story,Pier,Station,D/C Ratio", string.Join(",", read[0].Data.Fields), "fields"); Equal(1, read[0].Data.Rows.Count, "the units row is not data");
        Equal("0.912", read[0].Data.Rows[0][3], "value"); Equal(1, read[1].Data.Rows.Count, "a table with no units row keeps its first row");

        // CSI's text layout.
        var txt = Path.Combine(folder, "Tower.txt");
        File.WriteAllText(txt, "TABLE:  \"SHEAR WALL SPANDREL DESIGN SUMMARY - ACI 318-19\"\r\n   Story=Story2   Spandrel=S1   Station=Left   \"As Top\"=0.0006\r\n");
        var text = DesignResults.ReadExport(txt);
        Equal("spandrel", DesignResults.FamilyOf(text[0].Name)?.Key, "the text table's kind"); Equal("S1", text[0].Data.Rows[0][1], "the text table's rows");
        Check(DesignResults.ExportsBeside(Path.Combine(folder, "Tower.EDB")).Count() == 2, "both files beside the model, named after it, are found");
        Check(!DesignResults.ExportsBeside(Path.Combine(folder, "Other.EDB")).Any(), "files named after another model are not");
        try { DesignResults.ReadExport(Path.Combine(folder, "Tower.mdb")); Check(false, "an Access file should be refused"); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("Excel"), "an Access file says to use Excel"); }

        // The pack takes the file's pier design, as the program's tables have none.
        var e = new MemoryEtabs();
        var built = Pack.Build(e, [], [], [], null, null, designFiles: [xlsx]);
        Check(built.Tables.Contains("Shear Wall Pier Design Summary - ACI 318-19") && !built.Tables.Contains("Story Drifts"), "the design table is taken, the file's other tables are not");
        Equal("file: Tower tables.xlsx", (string)built.Content["tables"]!["Shear Wall Pier Design Summary - ACI 318-19"]!["from"]!, "the table says where it came from");
        Check(built.Notes.Any(n => n.Contains("Tower tables.xlsx") && n.Contains("check it is from the model")), "the engineer is told it came from a file");
    }
    finally { Directory.Delete(folder, true); }

    var offline = new OfflineEtabs(E2kReader.Read(sample));
    var labels = offline.WallLabels();
    Check(labels.Any(l => l.Type == "a" && l.Name == "W1@Story1" && l.Pier == "P1"), "the .e2k's PIER \"P1\" on W1 is read: " + string.Join("; ", labels));
    Check(Pack.Build(offline, [], [], [], null, null).Content["model"]!["wall_labels"]!.AsArray().Count == labels.Count, "the Modeler's pack carries the labels");
});

Test("pack: it is saved in the model's own folder, beside the model file", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "cpm-pack-" + Guid.NewGuid().ToString("N"));
    var (project, made, other) = (Path.Combine(root, "Tower", "run"), Path.Combine(root, "Made in chat"), Path.Combine(root, "elsewhere"));
    var before = OfflineEtabs.DefaultFolder;
    OfflineEtabs.DefaultFolder = project;
    try
    {
        var o = new OfflineEtabs();
        var mcp = Endpoint(o);
        Call(mcp, "new_model", """{"title":"Tower"}""");
        var none = new CsiProMax.Tools.ToolArgs(new Dictionary<string, JsonElement>());

        // Never saved: the model has no folder yet, so the files go to the usual one, and the answer says so.
        var unsaved = JsonNode.Parse(CsiProMax.Tools.ExportPackTool.Make(o, none, made).Text)!;
        Equal(made, (string)unsaved["folder"]!, "an unsaved model's pack goes to the usual folder");
        Check(!(bool)unsaved["beside_model"]! && ((string?)unsaved["not_beside_model"] ?? "").Contains("not been saved"), "and it says why: " + unsaved.ToJsonString());

        // Saved: both files land beside the model file, with nothing to say about it.
        var file = (string)Call(mcp, "save_model_as", """{"name":"tower"}""")["file"]!;
        Equal(project, Path.GetDirectoryName(file), "the model's folder");
        var beside = CsiProMax.Tools.ExportPackTool.Make(o, none, made);
        Check(!beside.IsError, beside.Text);
        var answer = JsonNode.Parse(beside.Text)!;
        Equal(project, (string)answer["folder"]!, "the pack's folder is the model's");
        Check((bool)answer["beside_model"]! && answer["not_beside_model"] is null, "nothing to explain: " + answer.ToJsonString());
        var files = CsiProMax.Chat.ChatFiles.Find(beside.Text);
        Equal(2, files.Count, "the page and the pack get a card each, the folder does not");
        Check(files.All(f => Path.GetDirectoryName(f) == project && File.Exists(f)), "both files are beside the model: " + string.Join(", ", files));
        Equal(0, Directory.GetFiles(project, ".csi-promax-*").Length, "the file that tried the folder is gone again");
        var again = CsiProMax.Chat.ChatFiles.Find(CsiProMax.Tools.ExportPackTool.Make(o, none, made).Text);
        Check(again.All(f => !files.Contains(f) && Path.GetDirectoryName(f) == project), "a second pack does not replace the first");

        // A folder that was asked for wins.
        var asked = JsonNode.Parse(CsiProMax.Tools.ExportPackTool.Make(o, new CsiProMax.Tools.ToolArgs(new Dictionary<string, JsonElement> { ["folder"] = J(JsonSerializer.Serialize(other)) }), made).Text)!;
        Equal(other, (string)asked["folder"]!, "the folder that was asked for");
        Check(!(bool)asked["beside_model"]! && asked["not_beside_model"] is null, "asked for, so nothing to explain");

        // The model's folder is gone (a drive that was unplugged): the usual folder again, with the reason.
        var (where, why) = CsiProMax.Tools.ExportPackTool.BesideModel(Path.Combine(root, "unplugged", "tower.EDB"), made);
        Equal(made, where, "no folder to write to"); Check(why!.Contains("is not there"), why);
        Equal(project, CsiProMax.Tools.ExportPackTool.BesideModel(Path.Combine(project, "tower.sdb"), made).Folder, "SAP2000 and SAFE models the same way");
        Equal(made, CsiProMax.Tools.ExportPackTool.BesideModel("(Untitled)", made).Folder, "a name with no folder is an unsaved model");
    }
    finally { OfflineEtabs.DefaultFolder = before; if (Directory.Exists(root)) Directory.Delete(root, true); }
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
        Equal(8, (int)context["counts"]!["columns"]!, "columns"); Equal(2, (int)context["counts"]!["walls"]!, "walls"); Equal("CSI ProMax Modeler", (string)context["program"]!, "says it is the Modeler"); Equal("Modeler (no ETABS)", (string)context["version"]!, "and that no ETABS is behind it");
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

Test("field test: one report with the PC's plug-in lists, every check, and ETABS's own table names", () =>
{
    var root = Directory.CreateTempSubdirectory("cpm-field").FullName;
    try
    {
        var settings = Path.Combine(root, "Computers and Structures");
        Directory.CreateDirectory(Path.Combine(settings, "ETABS 23"));
        Directory.CreateDirectory(Path.Combine(settings, "Other 1"));
        File.WriteAllText(Path.Combine(settings, "ETABS 23", "ETABS.ini"),
            "[Units]\r\n Force=kN\r\n[PlugIn]\r\n NumberPlugIns=1\r\n PlugInName=CsiProMaxPlugin\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath=C:\\X\\CsiProMaxPlugin.dll\r\n\r\n[Graphics]\r\n Colour=1\r\n");
        File.WriteAllText(Path.Combine(settings, "Other 1", "Other.ini"), "[PlugIn]\r\n NumberPlugIns=0\r\n");
        var lists = FieldTest.PlugInLists(settings);
        Equal(1, lists.Count, "only the CSI programs' settings are read");
        Equal(@"ETABS 23\ETABS.ini", lists[0].File, "named by folder and file");
        Check(lists[0].Section.StartsWith("[PlugIn]") && lists[0].Section.EndsWith("CsiProMaxPlugin.dll") && !lists[0].Section.Contains("Graphics"), "the plug-in list alone, as written");
        Check(FieldTest.PlugInSection("[A]\nx=1\n") is null, "a file with no plug-in list has none");
        Equal(0, FieldTest.PlugInLists(Path.Combine(root, "nowhere")).Count, "no CSI settings, no lists");

        // A model that has not been analysed: the reading checks run and the result checks say why they were left out.
        var e = new MemoryEtabs();
        e.AddFrame(0, 0, 0, 0, 0, 3, e.FrameSections()[0]);
        var at = new DateTime(2026, 11, 25, 14, 2, 0);
        var first = FieldTest.Run(e, false, "the Help menu", Path.Combine(root, "out"), at, settings);
        Equal(Path.Combine(root, "out", "Field test ETABS 2026-11-25 1402.md"), first.File, "the report's name says the program and when");
        Equal(first.Report, File.ReadAllText(first.File), "the report is saved as returned");
        Check(first.Steps.Any(s => s.Name == "ETABS results" && s.Detail.Contains("not been analysed")), "no analysis: the result checks are left out, and it says so");
        Check(first.Steps.Any(s => s.Name == "ETABS review pack" && s.Ok), "the review pack is built from the model");
        Check(first.Report.Contains("started from the Help menu") && first.Report.Contains("Changes to the model: none"), "how it was run");
        Check(first.Report.Contains("### ETABS 23\\ETABS.ini") && first.Report.Contains(" PlugInName=CsiProMaxPlugin"), "the plug-in list is in the report");
        Check(first.Report.Contains("analysed: no") && first.Report.Contains("model \"demo.EDB\"") && !first.Report.Contains("Models"), "the model's name without its folder, in every part of the report");
        Check(first.Report.Contains("opened on its own"), "not started by the plug-in");
        var again = FieldTest.Run(e, false, "the chat", Path.Combine(root, "out"), at, settings);
        Check(again.File != first.File && File.Exists(first.File), "a second report in the same minute does not replace the first");

        // Analysed, with ETABS's tables: each table the checks read is sampled with its fields, and the checks run.
        e.RunAnalysis();
        e.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Direction", "Drift", "Label"], [["Story1", "EQX", "X", "0.0019", "7"], ["Story1", "EQY", "Y", "0.0009", "5"], ["Story1", "WX", "X", "0.0004", "5"]]);
        e.ResultTables["Modal Participating Mass Ratios"] = new(["Case", "Mode", "Period", "UX", "UY", "RZ", "SumUX", "SumUY"], [["Modal", "1", "0.8", "0.7", "0", "0", "0.7", "0"], ["Modal", "2", "0.7", "0", "0.72", "0", "0.7", "0.72"]]);
        var run = FieldTest.Run(e, false, "the command line", Path.Combine(root, "out"), at.AddMinutes(5), settings);
        var drifts = run.Steps.Single(s => s.Name == "ETABS table Story Drifts");
        Check(drifts.Ok && (drifts.Full ?? drifts.Detail).Contains("\"fields\":[\"Story\",\"Output Case\",\"Direction\",\"Drift\",\"Label\"]") && (drifts.Full ?? drifts.Detail).Contains("\"rows\":3"), "the table's own fields and row count");
        Check(!(drifts.Full ?? drifts.Detail).Contains("WX"), "only the first rows are sampled");
        var missing = run.Steps.Single(s => s.Name == "ETABS table Story Stiffness");
        Check(!missing.Ok && missing.Detail.Contains("no table named 'Story Stiffness'") && missing.Detail.Contains("Story Drifts"), "a name ETABS does not have fails and lists the tables with a similar name");
        Check(run.Steps.Any(s => s.Name == "ETABS check_drift" && s.Ok) && run.Steps.Any(s => s.Name == "ETABS check_report" && s.Ok), "the checks themselves are run");
        Check(run.Failed > 0 && run.Report.Contains("## Not working") && run.Report.Contains("- ETABS table Story Stiffness:"), "what failed is listed first");
        Check(run.Report.Contains("### FAIL: ETABS table Story Stiffness") && run.Report.Contains("### ok: ETABS table Story Drifts"), "every check has its own part");
        Check(run.Summary().Contains("Not working:") && run.Summary().Contains(run.File), "the dialog says what failed and where the report is");
        var long_ = run.Steps.Single(s => s.Name == "ETABS check_report");
        Check(long_.Detail.Length <= 303 && (long_.Full is null || long_.Full.Length > long_.Detail.Length), "the dialog's text stays short; the report keeps the whole answer");

        // With changes, on a scratch model.
        var scratch = new MemoryEtabs();
        scratch.AddFrame(0, 0, 0, 0, 0, 3, scratch.FrameSections()[0]);
        var changed = FieldTest.Run(scratch, true, "the Help menu", Path.Combine(root, "out"), at.AddMinutes(9), settings);
        Check(changed.Steps.Any(s => s.Name == "AddFrame" && s.Ok) && changed.Report.Contains("the load pattern CPM-TEST"), "with changes it says what it added");
    }
    finally { Directory.Delete(root, true); }
});

Test("assistant: the chat is given every tool, the drawing reader too", () =>
{
    var registry = CsiProMax.Tools.ToolRegistry.CreateDefault();
    foreach (var name in new[] { "read_cad_drawing", "read_drawing", "make_document", "run_self_test", "run_field_test", "memory_note", "ask_user" })
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
    Directory.CreateDirectory(Path.Combine(root, "dotnet", "shared", "Microsoft.WindowsDesktop.App", "8.0.29"));
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
    // An older dotnet in Program Files and the Desktop Runtime 8 in the user's own folder (the one-line install).
    var old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
    var own = Path.Combine(root, "own");
    Directory.CreateDirectory(Path.Combine(own, "shared", "Microsoft.WindowsDesktop.App", "8.0.11"));
    File.WriteAllText(Path.Combine(old, Path.GetFileName(dotnet)), "");
    File.WriteAllText(Path.Combine(own, Path.GetFileName(dotnet)), "");
    var mixed = CsiProMaxPlugin.Launcher.StartInfo(folder, @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319", 7, "SAFE", [old, own]);
    Equal(Path.Combine(own, Path.GetFileName(dotnet)), mixed.FileName, "the dotnet that has the Desktop Runtime 8 is used, not the first one found");
    var only = CsiProMaxPlugin.Launcher.StartInfo(folder, @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319", 7, "SAFE", [old]);
    Equal(Path.Combine(old, Path.GetFileName(dotnet)), only.FileName, "with no Desktop Runtime 8 anywhere the dotnet that exists is started, and says itself what is missing");
    Check(!CsiProMaxPlugin.Launcher.HasDesktopRuntime8(old) && CsiProMaxPlugin.Launcher.HasDesktopRuntime8(own), "Desktop Runtime 8 is told by its folder");
    Directory.Delete(root, true);
});

foreach (var (name, body) in SafeTests.All()) Test(name, body);
foreach (var (name, body) in CsiToolsTests.All()) Test(name, body);
foreach (var (name, body) in CsiFieldStepsTests.All()) Test(name, body);
foreach (var (name, body) in TesterBugTests.All()) Test(name, body);
Test("set_section: a frame or area that is not in the model is reported as failed (ETABS 23 says done to it)", () =>
{
    var e = new MemoryEtabs();
    var mcp = Endpoint(e);
    Call(mcp, "add_columns", """{"points":[[0,0]],"stories":["Story1"],"section":"C500x500"}""");
    var made = e.Frames().Select(f => f.Name).ToList();
    var r = Call(mcp, "set_section", $$"""{"frames":["no such frame","{{made[0]}}"],"section":"C500x500"}""");
    Equal(1, (int)r["done"]!, "the frame that is there is set");
    Check(r["failed"]!.AsArray().Count == 1 && r["failed"]![0]!.GetValue<string>().Contains("no such frame"), "the missing frame is not reported: " + r.ToJsonString());
    var areas = Call(mcp, "set_section", """{"areas":["no such slab"],"section":"S200"}""");
    Check(areas["failed"]!.AsArray().Count == 1, "the missing area is not reported: " + areas.ToJsonString());
});
Test("add_load_pattern: a user coefficient the program's API cannot set keeps the Seismic pattern and says how to set it (ETABS 23)", () =>
{
    var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Tools", "EtabsTools.cs"));
    var at = source.IndexOf("private static JsonNode AddPattern(", StringComparison.Ordinal);
    var body = source.Substring(at, source.IndexOf("private static JsonNode SetLoads(", at, StringComparison.Ordinal) - at);
    Check(body.Contains("ex is MissingMethodException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException") && body.Contains("Modify Lateral Load"),
        "a program with no SetUserCoefficient (ETABS 23) still fails add_load_pattern instead of keeping the pattern and saying how to set it");
    var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Field", "EtabsFieldSteps.cs"));
    Check(steps.Contains("its automatic load cannot be set through this ETABS's API"), "the field test still fails the seismic step on an ETABS with no user coefficient");
});
Test("field test: the ETABS scratch model gets a lateral load its checks can read, also on ETABS 23", () =>
{
    var steps = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Field", "EtabsFieldSteps.cs"));
    var load = steps.IndexOf("joint forces in X in CPM-EQX", StringComparison.Ordinal);
    var analysis = steps.IndexOf("ctx.Tool(\"run_analysis\"", load > 0 ? load : 0, StringComparison.Ordinal);
    Check(load > 0 && analysis > load && steps.Contains("m.PointObj.SetLoadForce(p, \"CPM-EQX\""), "the seismic pattern has no load before the analysis when ETABS cannot take a user coefficient");
});
Test("add_load_pattern: ETABS refusing ASCE 7-16 keeps the Seismic pattern and says how to set it, not an error", () =>
{
    // 0.4.18 field test on ETABS 23: no user coefficient, and ASCE 7-16 refused; the pattern is kept with a note.
    var model = new CpmFakes.EtabsSeismic();
    model.AutoSeismic.Refuse = true;
    using var link = new ComEtabs(CsiProgram.Etabs, () => model);
    var a = EtabsTools.All.First(t => t.Name == "add_load_pattern").Run(JsonDocument.Parse("""{"name":"EQX","type":"Seismic","user_coefficient":{"c":0.05,"direction":"X"}}""").RootElement, link);
    Check(a["lateral"] is null && a["note"]!.GetValue<string>().Contains("Modify Lateral Load", StringComparison.Ordinal), a.ToJsonString());
    Check(model.LoadPatterns.Names.Contains("EQX"), "the Seismic pattern is kept");
});
Test("tables for editing: every load pattern is selected for display first (SAP2000 26 hides the others)", () =>
{
    var root = SourceText.Root();
    foreach (var file in new[] { "Etabs/EditableTables.cs", "Tools/CsiTools.cs", "Safe/SafeTools.cs" })
    {
        var text = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", file));
        var reads = System.Text.RegularExpressions.Regex.Matches(text, @"GetTableForEditingArray\(").Count;
        var shown = System.Text.RegularExpressions.Regex.Matches(text, @"ShowEveryPattern\(m\);").Count;
        Check(reads > 0 && shown >= reads, $"{file}: {reads} table reads for editing, {shown} with every load pattern selected for display first");
    }
    var helper = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Etabs", "EditableTables.cs"));
    Check(helper.Contains("m.DatabaseTables.SetLoadPatternsSelectedForDisplay(ref names)") && helper.Contains("m.LoadPatterns.GetNameList(ref n, ref names)"), "ShowEveryPattern does not select the model's own patterns");
});
Test("edit_table: an analysed model is unlocked first, and a refused edit is cancelled", () =>
{
    var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Tools", "CsiTools.cs"));
    var at = source.IndexOf("var log = (JsonObject)e.Raw(\"apply the table edit\"", StringComparison.Ordinal);
    Check(at > 0, "the table edit's apply step is gone");
    var body = source.Substring(at, source.IndexOf("e.Refresh();", at, StringComparison.Ordinal) - at);
    var unlock = body.IndexOf("m.SetModelIsLocked(false)", StringComparison.Ordinal);
    var set = body.IndexOf("m.DatabaseTables.SetTableForEditingArray(", StringComparison.Ordinal);
    Check(unlock > 0 && unlock < set, "an analysed (locked) model is not unlocked before the edit; SAP2000 26 refuses it then");
    Check(body.Contains("m.DatabaseTables.CancelTableEditing()"), "a refused edit is not cancelled; SAP2000 then refused the table and listed no patterns");
    Check(source.Contains("log[\"unlocked\"]"), "the answer does not say the results are gone");
});
foreach (var (name, body) in CdtSdkTests.All()) Test(name, body);
foreach (var (name, body) in HandModelTests.All()) Test(name, body);
foreach (var (name, body) in AiModeTests.All()) Test(name, body);
foreach (var (name, body) in ModelTablesTests.All()) Test(name, body);
foreach (var (name, body) in ModelerWindowTests.All()) Test(name, body);
foreach (var (name, body) in KeptPlugInsTests.All()) Test(name, body);
foreach (var (name, body) in FieldRun048Tests.All()) Test(name, body);
foreach (var (name, body) in FieldQuietTests.All()) Test(name, body);

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
    Check(ProgramTools.For(CsiProgram.Etabs).All(t => GeoProMaxTools.All.Contains(t)), "ETABS adds only the link with GeoProMax to the shared tools");
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

Test("link: SAP2000 that answers an empty list with no list at all, and units only by the older call", () =>
{
    // Ahmed's SAP2000 26 field test (0.4.8): a scratch model with no combinations gave a null list, so model_snapshot
    // failed with "Value cannot be null (source)"; GetPresentUnits_2 left the units empty.
    using var link = new ComEtabs(CsiProgram.Sap2000, () => new CpmFakes.SapLikeModel());
    Equal(0, link.Combos().Count, "no combinations");
    Equal(1, link.LoadCases().Count, "one load case");
    var info = link.Info();
    Equal("kN", info.Force, "force"); Equal("m", info.Length, "length"); Equal("C", info.Temperature, "temperature");
});

Test("link: a section given to a frame or area that is not there is reported, though the program says done", () =>
{
    // ETABS 23 field test (0.4.9): SetSection on a missing frame answered 0, so set_section said done 1, failed none.
    using var link = new ComEtabs(CsiProgram.Etabs, () => new CpmFakes.SectionModel());
    link.SetFrameSection("B1", "W12");
    link.SetAreaSection("F1", "S200");
    Throws<EtabsException>(() => link.SetFrameSection("no such frame", "W12"), "a missing frame");
    Throws<EtabsException>(() => link.SetAreaSection("no such area", "S200"), "a missing area");
});

Test("link: a redraw the program does not come back from never fails the tool, and is not asked for again", () =>
{
    // ETABS 23 field test (0.4.9): View.RefreshView after a selection never came back; the link waited 3 minutes,
    // then failed every later call. A redraw is waited for briefly, never throws, and after one hang is skipped.
    var model = new CpmFakes.HangingView();
    using var link = new ComEtabs(CsiProgram.Etabs, () => model) { RefreshLimit = TimeSpan.FromMilliseconds(300) };
    link.Refresh();
    Equal(1, model.Redraws, "the first redraw is asked for and comes back");
    model.Hang.Reset();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    link.Refresh();
    Check(clock.ElapsedMilliseconds < 2000, $"the tool waited {clock.ElapsedMilliseconds} ms for a redraw that hangs");
    clock.Restart();
    link.Refresh();
    Check(clock.ElapsedMilliseconds < 100, "after a hang, a redraw is not waited for again");
    model.Hang.Set();
    Equal(7, (int)link.Raw("read joint displacements", _ => 7)!, "once the program answers, the link works");
    Equal(2, model.Redraws, "no redraw was asked for after the hang");
});

Test("pack: SAP2000's steel design summary comes from its API, as ETABS's does", () =>
{
    // 0.4.9 field test: the SAP2000 pack had no design ratios, so the Viewer's "Show design ratios" had nothing.
    using var com = new ComEtabs(CsiProgram.Sap2000, () => new CpmFakes.SapDesigned());
    var made = DesignResults.FromApi(new CsiProMax.Assistant.Sap2000.SapLink(com), new HashSet<string>(), []);
    var steel = made.FirstOrDefault(m => m.Name.StartsWith("Steel", StringComparison.Ordinal));
    Check(steel.Data is not null, "no steel design table: " + string.Join(", ", made.Select(m => m.Name)));
    Equal("0.75", steel.Data!.Rows[0][2], "B1's ratio");
});

Test("link: a table the program lists reads as empty when the program refuses to read it", () =>
{
    // ETABS 23 field test (0.4.11): "refused to read the table 'Story Stiffness'" with no seismic case, and
    // 'Element Forces - Braces' with no braces.
    using var link = new ComEtabs(CsiProgram.Etabs, () => new CpmFakes.TablesModel());
    Equal(0, link.ReadTable("Story Stiffness", ["DEAD"], null).Rows.Count, "a listed empty table");
    Equal(0, link.ReadTable("Steel Column Envelope - AISC 360-16", null, null).Rows.Count, "a listed table marked with data that ETABS refuses");
    Equal(1, link.ReadTable("Story Definitions", null, null).Rows.Count, "a table with data");
    Throws<EtabsException>(() => link.ReadTable("No Such Table", null, null), "a table that is not there");
});

Test("tool: add_load_pattern refuses a user coefficient that is not for a seismic pattern, or not above 0", () =>
{
    var link = new MemoryEtabs();
    var tool = EtabsTools.All.First(t => t.Name == "add_load_pattern");
    Throws<ToolInputException>(() => tool.Run(JsonDocument.Parse("""{"name":"W","type":"Wind","user_coefficient":{"c":0.05}}""").RootElement, link), "wind");
    Throws<ToolInputException>(() => tool.Run(JsonDocument.Parse("""{"name":"E","type":"Seismic","user_coefficient":{"c":0}}""").RootElement, link), "zero");
    Throws<ToolInputException>(() => tool.Run(JsonDocument.Parse("""{"name":"E","type":"Seismic","user_coefficient":{"c":0.05,"direction":"Z"}}""").RootElement, link), "direction Z");
});

Test("tool: a seismic pattern by coefficient in ETABS, whose API has only ASCE 7-16, uses ASCE 7-16 and says so", () =>
{
    // 0.4.15 field test: ETABS 23's cAutoSeismic has no SetUserCoefficient, only SetASCE716 and SetIBC2006.
    var model = new CpmFakes.EtabsSeismic();
    using var link = new ComEtabs(CsiProgram.Etabs, () => model);
    var tool = EtabsTools.All.First(t => t.Name == "add_load_pattern");
    var a = tool.Run(JsonDocument.Parse("""{"name":"EQX","type":"Seismic","user_coefficient":{"c":0.05,"direction":"X"}}""").RootElement, link);
    Check(a["lateral"]!.GetValue<string>().StartsWith("ASCE 7-16", StringComparison.Ordinal), a.ToJsonString());
    Equal("EQX", model.AutoSeismic.Named, "the pattern given ASCE 7-16");
    Check(model.AutoSeismic.Flags is [true, false, false, false, false, false], "X only");
});

Test("link: units from the older one-value call, by name or number", () =>
{
    Equal(("kN", "m", "C"), ComEtabs.Units("kN_m_C"), "by name");
    Equal(("kN", "m", "C"), ComEtabs.Units(6), "by number");
    Equal(("tonf", "cm", "C"), ComEtabs.Units(16), "tonne");
    Equal(("kip", "ft", "F"), ComEtabs.Units("kip_ft_F"), "US");
    Equal(("", "", ""), ComEtabs.Units(99), "unknown");
});

Test("link: a call that fails comes out as an EtabsException, never wrapped", () =>
{
    using var link = new ComEtabs(CsiProgram.Sap2000, () => throw new EtabsException("The scratch SAP2000 window is no longer there."));
    try { link.Raw("close the scratch window", _ => null); throw new Exception("no error"); }
    catch (EtabsException ex) { Check(ex.Message.Contains("no longer there"), ex.Message); }
    using var other = new ComEtabs(CsiProgram.Sap2000, () => new object());
    try { other.Raw("read the joints", _ => throw new InvalidOperationException("boom")); throw new Exception("no error"); }
    catch (EtabsException ex) { Check(ex.Message.Contains("boom"), ex.Message); }
});

Test("link: a call made from inside another call runs at once instead of waiting for itself", () =>
{
    using var link = new ComEtabs(CsiProgram.Sap2000, () => new object()) { CallLimit = TimeSpan.FromSeconds(5) };
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var answer = (int)link.Raw("read joint displacements", _ => (int)link.Raw("list the load cases", _ => 3)! + 4)!;
    Equal(7, answer, "the inner call's answer");
    Check(clock.ElapsedMilliseconds < 2000, $"it took {clock.ElapsedMilliseconds} ms: the inner call waited behind the outer one");
});

Test("link: a call the program does not come back from is given up on, later calls say so at once, and it recovers", () =>
{
    using var link = new ComEtabs(CsiProgram.Sap2000, () => new object()) { CallLimit = TimeSpan.FromMilliseconds(300), AnalysisLimit = TimeSpan.FromMilliseconds(600) };
    Check(ComEtabs.IsLong("run the analysis") && ComEtabs.IsLong("start the steel frame design") && ComEtabs.IsLong("open the file") && ComEtabs.IsLong("save the model") && !ComEtabs.IsLong("read the joints"), "analysis, design, open and save get the long limit");
    Equal(TimeSpan.FromMinutes(3), new ComEtabs(CsiProgram.Sap2000, () => new object()).CallLimit, "an ordinary call's limit");
    using var release = new ManualResetEventSlim();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    try { link.Raw("run the analysis", _ => { release.Wait(); return null; }); throw new Exception("the stuck call returned"); }
    catch (EtabsException ex) { Check(ex.Message.Contains("Analysis Complete") && ex.Message.Contains("SAP2000"), ex.Message); }
    Check(clock.ElapsedMilliseconds < 5000, "it gave up within its limit");
    clock.Restart();
    try { link.Raw("read the joints", _ => 1); throw new Exception("a call behind the stuck one ran"); }
    catch (EtabsException ex) { Check(ex.Message.Contains("run the analysis"), "the next call names the stuck one: " + ex.Message); }
    Check(clock.ElapsedMilliseconds < 200, "the next call says so at once");
    release.Set();
    var answer = 0;
    for (var i = 0; i < 50 && answer == 0; i++)
    {
        Thread.Sleep(50);
        try { answer = (int)link.Raw("read the joints", _ => 7)!; } catch (EtabsException) { }
    }
    Equal(7, answer, "calls work again once the program answers");
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

Test("plug-in: every program from the oldest on can create it (a plain cPlugin, and from the API file only what the oldest one has)", () =>
{
    // The bug of 0.4.4: "class cPlugin : cPluginContract". Only the API files of the programs on .NET 8 have that
    // interface, so ETABS 18 to 21, SAFE 20 and 21 and SAP2000 20 to 25 could not create the plug-in: ETABS 20 said
    // "Cannot create plugin!" and listed it as "Not Found". What a plug-in may use from the API file is what the
    // oldest one has (read from the API file of ETABS 18): the model, the callback, and the callback's Finish.
    // Read from the files as built, by their metadata: this is what the program's .NET binds, whatever the source says.
    string[] oldest = ["cPluginCallback", "cPluginCallback::Finish", "cSapModel"];
    var root = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(root, "CsiProMax.sln"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new Exception("CsiProMax.sln was not found above " + AppContext.BaseDirectory);
    var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
    string Built(string project, string file) => Path.Combine(root, "src", project, "bin", configuration, "netstandard2.0", file + ".dll");
    static string Token(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.BlobHandle key)
    {
        var bytes = md.GetBlobBytes(key);
        // A whole public key is told by its token: the last eight bytes of its SHA-1, backwards.
        if (bytes.Length > 8) bytes = Enumerable.Reverse(System.Security.Cryptography.SHA1.HashData(bytes)[^8..]).ToArray();
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    foreach (var (project, plugin, stub, api) in new[]
             {
                 ("CsiProMax.Plugin", "CsiProMaxPlugin", "EtabsApiStub", "ETABSv1"),
                 ("CsiProMax.Plugin.Safe", "CsiProMaxSafePlugin", "SafeApiStub", "SAFEv1"),
                 ("CsiProMax.Plugin.Sap", "CsiProMaxSapPlugin", "SapApiStub", "SAP2000v1"),
             })
    {
        Check(File.Exists(Built(project, plugin)), $"{plugin}.dll is built ({Built(project, plugin)})");
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(Built(project, plugin)));
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        Equal(plugin, md.GetString(md.GetAssemblyDefinition().Name), "the file's name is the plug-in's name in the program's list");

        // It needs two files only: .NET Standard 2.0 (every .NET the programs run on has it) and the program's API file,
        // by the name, version and key the real file has in every version (the .NET Framework wants all three the same).
        var references = md.AssemblyReferences.Select(h => md.GetAssemblyReference(h)).ToList();
        Equal($"{api}, netstandard", string.Join(", ", references.Select(r => md.GetString(r.Name)).OrderBy(n => n, StringComparer.Ordinal)), $"what {plugin} needs");
        var apiReference = references.Single(r => md.GetString(r.Name) == api);
        Equal("1.0.0.0", apiReference.Version.ToString(), $"{api}'s version");
        Equal("453d728ef24c6f5e", Token(md, apiReference.PublicKeyOrToken), $"{api}'s key");

        // The class the program makes: <plug-in name>.cPlugin, public, with no interface, and its two methods.
        var types = md.TypeDefinitions.Select(h => md.GetTypeDefinition(h)).ToList();
        var made = types.Single(t => md.GetString(t.Name) == "cPlugin");
        Equal(plugin, md.GetString(made.Namespace), "cPlugin's namespace is the plug-in's name");
        Check((made.Attributes & System.Reflection.TypeAttributes.VisibilityMask) == System.Reflection.TypeAttributes.Public, $"{plugin}.cPlugin is public");
        Equal(0, made.GetInterfaceImplementations().Count, $"{plugin}.cPlugin names no interface (not cPluginContract: ETABS 18 to 21 have none)");
        var methods = made.GetMethods().Select(h => md.GetMethodDefinition(h))
            .Where(m => (m.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) == System.Reflection.MethodAttributes.Public)
            .Select(m => md.GetString(m.Name)).ToList();
        Check(methods.Contains("Main") && methods.Contains("Info") && methods.Contains(".ctor"), $"{plugin}.cPlugin has a public constructor, Main and Info ({string.Join(", ", methods)})");

        // Everything it uses from the API file is in the oldest API file.
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<System.Reflection.Metadata.TypeReferenceHandle, string>();
        foreach (var handle in md.TypeReferences)
        {
            var reference = md.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind != System.Reflection.Metadata.HandleKind.AssemblyReference) continue;
            if (md.GetString(md.GetAssemblyReference((System.Reflection.Metadata.AssemblyReferenceHandle)reference.ResolutionScope).Name) != api) continue;
            Equal(api, md.GetString(reference.Namespace), "the API's namespace");
            names[handle] = md.GetString(reference.Name);
            used.Add(names[handle]);
        }
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.Parent.Kind == System.Reflection.Metadata.HandleKind.TypeReference && names.TryGetValue((System.Reflection.Metadata.TypeReferenceHandle)member.Parent, out var owner))
                used.Add(owner + "::" + md.GetString(member.Name));
        }
        Equal("", string.Join(", ", used.Except(oldest)), $"{plugin} uses from {api} what the oldest program's API file does not have");
        Check(used.Contains("cSapModel") && used.Contains("cPluginCallback"), $"{plugin}'s Main takes the model and the callback");

        // The stand-in the plug-in is built against holds no more than that, so naming anything newer does not compile,
        // and it has the real file's name, version and key.
        using var standIn = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(Built(stub, api)));
        var sd = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(standIn);
        var definition = sd.GetAssemblyDefinition();
        Equal($"{api} 1.0.0.0 453d728ef24c6f5e", $"{sd.GetString(definition.Name)} {definition.Version} {Token(sd, definition.PublicKey)}", $"the stand-in for {api}");
        var offered = sd.TypeDefinitions.Select(h => sd.GetTypeDefinition(h)).Where(t => sd.GetString(t.Namespace) == api).Select(t => sd.GetString(t.Name)).OrderBy(n => n, StringComparer.Ordinal);
        Equal("cPluginCallback, cSapModel", string.Join(", ", offered), $"the stand-in for {api} offers only what the oldest program has");
    }
});

Test("chat: Claude Code is looked for where each way of installing puts it, the most likely first", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "cpm-find-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string Make(params string[] parts)
    {
        var file = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        return file;
    }
    try
    {
        var (home, local, roaming, programs, tools) = (Path.Combine(root, "home"), Path.Combine(root, "local"), Path.Combine(root, "roaming"), Path.Combine(root, "pf"), Path.Combine(root, "tools"));
        var places = new CsiProMax.Chat.ClaudeFinder.Places([home], local, roaming, [programs], [tools], User: "DAR\\tester");
        var asked = new List<string>();
        (int, string) Answer(string path, int ms) { asked.Add(path); return (0, "2.1.119 (Claude Code)\n"); }
        string? Found() => CsiProMax.Chat.ClaudeFinder.Search(places, Answer).Install?.Path;

        // A PC with no Claude Code: nothing is started, and the answer says where it looked and for whom.
        var none = CsiProMax.Chat.ClaudeFinder.Search(places, Answer);
        Check(none.Install is null && asked.Count == 0 && none.Tried.Count == 0, "nothing is there, so nothing is started");
        Equal(Path.Combine(home, ".local", "bin", "claude.exe"), none.Looked[0], "the first place is where Anthropic's installer puts it");
        Check(none.Looked.Count >= 15, $"only {none.Looked.Count} places were looked at");
        Check(none.Where.Contains("DAR\\tester") && none.Where.Contains(Path.Combine(home, ".local", "bin")), none.Where);
        Check(none.Details("CSI ProMax 0.4.2").Contains("no Claude Code that answers") && none.Details("x").Contains(Path.Combine(roaming, "npm", "claude.cmd")), "the details list every place");

        // Each kind of install is found; a likelier place wins over a wider guess.
        Make("roaming", "Claude", "claude-code", "2.1.90", "claude.exe");
        var desktop = Make("roaming", "Claude", "claude-code", "2.1.119", "claude.exe");
        Equal(desktop, Found(), "the copy inside the Claude desktop app, its newest version");
        var store = Make("local", "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude-code", "2.1.120", "claude.exe");
        Check(CsiProMax.Chat.ClaudeFinder.Candidates(places).Contains(store), "the desktop app from the Store keeps its copy under Packages");
        var backup = Make("home", ".local", "bin", "claude.exe.old.1764000000000");
        Make("home", ".local", "bin", "claude.exe.old.1763000000000");
        Equal(backup, Found(), "an update that was cut off leaves only the backup: the newest one is used");
        Make("home", ".local", "share", "claude", "versions", "2.1.97");
        var kept = Make("home", ".local", "share", "claude", "versions", "2.1.118");
        Equal(kept, Found(), "the installer's own store of versions, the newest");
        var nvm = Make("roaming", "nvm", "v22.11.0", "claude.cmd");
        Equal(nvm, Found(), "a Node version kept by nvm");
        var winget = Make("local", "Microsoft", "WinGet", "Packages", "Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe", "claude.exe");
        Equal(winget, Found(), "WinGet's package folder, when it made no link");
        var npm = Make("roaming", "npm", "claude.cmd");
        Equal(npm, Found(), "npm install -g");
        var onPath = Make("tools", "claude.cmd");
        Equal(onPath, Found(), "a folder on PATH");
        var native = Make("home", ".local", "bin", "claude.exe");
        Equal(native, Found(), "Anthropic's installer");
        var mine = Make("elsewhere", "claude.exe");
        places = places with { Chosen = "\"" + mine + "\"" };
        Equal(mine, Found(), "the file the user pointed at comes first");
        Equal("2.1.119", CsiProMax.Chat.ClaudeFinder.Search(places, Answer).Install?.Version, "its version");

        // A package manager's own folder (npm's prefix, pnpm, Volta, Bun, Scoop).
        var prefix = Make("d", "npm-global", "claude.cmd");
        Check(CsiProMax.Chat.ClaudeFinder.Candidates(places with { Prefixes = [Path.Combine(root, "d", "npm-global")] }).Contains(prefix), "npm's prefix");
        Equal(@"D:\npm-global", CsiProMax.Chat.ClaudeFinder.NpmPrefix("# mine\nregistry=https://registry.npmjs.org/\nprefix = \"D:\\npm-global\"\r\n"), "the prefix line of .npmrc");
        Equal("", CsiProMax.Chat.ClaudeFinder.NpmPrefix("registry=https://registry.npmjs.org/\n;prefix=C:\\old"), "no prefix line");
    }
    finally { Directory.Delete(root, true); }
});

Test("chat: a Claude Code that is there but does not answer is named with the reason, and the desktop app is never started", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "cpm-find-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string Make(params string[] parts)
    {
        var file = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        return file;
    }
    try
    {
        var (home, local, roaming) = (Path.Combine(root, "home"), Path.Combine(root, "local"), Path.Combine(root, "roaming"));
        var native = Make("home", ".local", "bin", "claude.exe");
        var blocked = Make("tools", "claude.exe");
        var slowToo = Make("tools", "claude.cmd");
        var npm = Make("roaming", "npm", "claude.cmd");
        var squirrel = Make("local", "AnthropicClaude", "claude.exe");
        var app = Make("apps", "Claude", "claude.exe");
        Make("apps", "Claude", "resources", "app.asar");
        var gone = Path.Combine(root, "was-here", "claude.exe");
        var places = new CsiProMax.Chat.ClaudeFinder.Places([home], local, roaming, [], [Path.Combine(root, "tools"), Path.Combine(local, "AnthropicClaude"), Path.Combine(root, "apps", "Claude")], Chosen: gone, User: "DAR\\tester");
        var asked = new List<(string Path, int Ms)>();
        (int, string) Answer(string path, int ms)
        {
            asked.Add((path, ms));
            if (path == native || path == slowToo) return (-1, "");                       // no answer in time
            if (path == blocked) return (-1, "Access is denied");                          // Windows did not start it
            return (1, "\n'node' is not recognized as an internal or external command,\noperable program or batch file.\n");
        }
        var result = CsiProMax.Chat.ClaudeFinder.Search(places, Answer);
        Check(result.Install is null, "none of them counts");
        string Why(string path) => result.Tried.FirstOrDefault(t => t.Path == path)?.Outcome ?? "(not listed)";
        Equal("the file chosen in CSI ProMax is no longer there", Why(gone), "a chosen file that is gone");
        Equal("did not answer within 60 seconds", Why(native), "a slow one gets a second, longer try");
        Equal("did not answer within 20 seconds", Why(slowToo), "the longer try is given once in a search");
        Equal("Windows did not start it: Access is denied", Why(blocked), "blocked");
        Equal("answered with exit code 1: 'node' is not recognized as an internal or external command,", Why(npm), "an npm install without Node");
        Check(Why(squirrel).Contains("Claude desktop app") && Why(app).Contains("Claude desktop app"), "the desktop app is named: " + Why(squirrel) + " / " + Why(app));
        Check(!asked.Any(a => a.Path == squirrel || a.Path == app), "the desktop app was started");
        Check(result.DesktopAppSeen && result.ChosenGone == gone, "the desktop app and the chosen file that is gone are told apart from a Claude Code that failed");
        Equal(4, result.Failed.Count, "files that are Claude Code but did not work");

        // Only the desktop app on the PC: that is "not installed", with the app named, never "couldn't use it".
        var appOnly = CsiProMax.Chat.ClaudeFinder.Search(new CsiProMax.Chat.ClaudeFinder.Places([Path.Combine(root, "nobody")], local, Path.Combine(root, "nothing"), [], []), Answer);
        Check(appOnly.Install is null && appOnly.Failed.Count == 0 && appOnly.DesktopAppSeen && appOnly.ChosenGone is null, "a PC with only the desktop app");
        Equal("20000,60000", string.Join(",", asked.Where(a => a.Path == native).Select(a => a.Ms)), "the waits for the slow one");
        Equal(1, asked.Count(a => a.Path == slowToo), "tries for the second slow one");
        var details = result.Details("CSI ProMax 0.4.2");
        Check(details.Contains("CSI ProMax 0.4.2") && details.Contains("DAR\\tester") && details.Contains(native + ": did not answer within 60 seconds") && details.Contains("Places looked at"), details);

        // The answer to --version: Claude Code's own line counts, whatever else was printed around it.
        var judge = CsiProMax.Chat.ClaudeFinder.Judge;
        Check(judge(0, "2.1.119 (Claude Code)\n\n", 20000) is (true, "2.1.119", ""), "the usual answer");
        Check(judge(1, "warning: could not check for updates\n2.0.14 (Claude Code)", 20000) is (true, "2.0.14", _), "a version with a warning and exit code 1");
        Check(judge(0, "1.0.3", 20000).Works, "an older one that prints only the number");
        Check(!judge(0, "", 20000).Works && !judge(1, "Error 1.2.3 happened", 20000).Works, "no version, or an error that happens to hold numbers");
        Check(CsiProMax.Chat.ClaudeFinder.IsDesktopApp(@"C:\Users\x\AppData\Local\Microsoft\WindowsApps\claude.exe"), "the desktop app's alias in WindowsApps");
    }
    finally { Directory.Delete(root, true); }
});

Test("field test: the Modeler part runs every Modeler tool, and every step holds", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "cpm-field-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var snapshots = ResultTools.SnapshotRoot;
    try
    {
        var ctx = new FieldContext(CsiProgram.Etabs, folder, new MemoryEtabs());
        ModelerFieldSteps.Run(ctx);
        // CPM_FIELD_DUMP=1 prints every step, to read what the field test did when working on it.
        if (Environment.GetEnvironmentVariable("CPM_FIELD_DUMP") == "1")
            foreach (var s in ctx.Steps) Console.WriteLine($"      {(s.Skipped ? "skip" : s.Ok ? "ok  " : "FAIL")} {s.Name}: {(s.Detail.Length > 140 ? s.Detail[..140] : s.Detail)}");
        var failed = ctx.Steps.Where(s => !s.Ok).Select(s => $"{s.Name}: {s.Full ?? s.Detail}").ToList();
        Check(failed.Count == 0, $"{failed.Count} of {ctx.Steps.Count} steps failed:\n      - " + string.Join("\n      - ", failed));
        Check(ctx.Steps.Count > 60, $"only {ctx.Steps.Count} steps");
        Check(ctx.Steps.All(s => s.Section == ModelerFieldSteps.Section), "every step is in the Modeler's part of the report");
        Check(ctx.Steps.Any(s => s.Name == "every Modeler tool was run" && s.Ok), "the closing check that every tool was run");
        foreach (var key in new[] { "etabs", "sap2000", "safe" })
            Check(ctx.Files.TryGetValue(key, out var file) && File.Exists(file) && new FileInfo(file).Length > 500, $"the file for {key} was not written");
        Equal(ResultTools.SnapshotRoot, snapshots, "the snapshot folder is put back");
    }
    finally { ResultTools.SnapshotRoot = snapshots; Directory.Delete(folder, true); }
});

Test("field test: the panel's own tools are each run or listed as not tested, and nothing of the test is left behind", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "cpm-field-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var e = new MemoryEtabs();
        e.AddFrame(0, 0, 0, 0, 0, 3, e.FrameSections()[0]);
        var ctx = new FieldContext(CsiProgram.Etabs, folder, e);
        PanelFieldSteps.Run(ctx, e, scratch: false, live: false);
        if (Environment.GetEnvironmentVariable("CPM_FIELD_DUMP") == "1")
            foreach (var s in ctx.Steps) Console.WriteLine($"      {(s.Skipped ? "skip" : s.Ok ? "ok  " : "FAIL")} {s.Name}: {(s.Detail.Length > 200 ? s.Detail[..200] : s.Detail)}");
        var failed = ctx.Steps.Where(s => !s.Ok).Select(s => $"{s.Name}: {s.Full ?? s.Detail}").ToList();
        Check(failed.Count == 0, $"{failed.Count} of {ctx.Steps.Count} steps failed:\n      - " + string.Join("\n      - ", failed));
        Check(ctx.Steps.Any(s => s.Name == "every tool of the panel was run, or is listed as not tested" && s.Ok), "the closing check");
        Check(ctx.Steps.Where(s => s.Skipped).All(s => s.Detail.Length > 20), "everything not tested says why");
        Check(!File.Exists(CsiProMax.Tools.Recipes.PathFor("cpm-field-test-recipe")) && !File.Exists(CsiProMax.Tools.LearnedTools.PathFor("cpm_field_test_tool")), "the test's recipe and learned tool are gone again");
        Check(CsiProMax.Bugs.BugLog.RootOverride is null, "the bug folder is put back");
    }
    finally { Directory.Delete(folder, true); }
});

Test("field test: the full test runs every part, closes its window, and leaves the open model as it was", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "cpm-full-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var snapshots = ResultTools.SnapshotRoot;
    try
    {
        var open = new MemoryEtabs();
        open.AddFrame(0, 0, 0, 0, 0, 3, open.FrameSections()[0]);
        var (frames, patterns) = (open.Frames().Count, open.LoadPatterns().Count);
        CsiProMax.Tests.StandInWindow? window = null;
        var at = new DateTime(2026, 11, 25, 15, 30, 0);
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        foreach (var plugIn in new[] { "CsiProMaxPlugin.dll", "CsiProMaxSafePlugin.dll", "CsiProMaxSapPlugin.dll" }) File.WriteAllText(Path.Combine(install, plugIn), "");
        var options = new FieldTest.Options
        {
            Full = true, StartedFrom = "the Help menu", Folder = Path.Combine(root, "out"), WorkFolder = Path.Combine(root, "work"), At = at,
            SettingsRoot = Path.Combine(root, "nowhere"), InstallFolder = install, Programs = [CsiProgram.Etabs], LiveScratch = false,
            // The stand-in for a second ETABS window: the Modeler's .e2k read into a model of its own.
            StartScratch = (program, ctx, file) =>
            {
                var path = Path.Combine(ctx.Folder, "cpm-field-test.edb");
                return window = new CsiProMax.Tests.StandInWindow(new OfflineEtabs(OfflineEtabs.Load(file), path), path);
            },
        };
        var resultsFile = Path.Combine(root, "last-results.json");
        options = options with { ResultsFile = resultsFile, Print = _ => "same" };
        var outcome = FieldTest.Run(open, options);
        // Only what changed or failed: with every part passed and unchanged, no scratch window is reopened again.
        {
            var keptParts = FieldRetest.Load(resultsFile);
            Check(keptParts.ContainsKey("etabs") && keptParts.ContainsKey(FieldRetest.Panel), "the full run did not keep its parts' results: " + string.Join(", ", keptParts.Keys));
            var reopened = 0;
            var again = options with { Retest = true, Folder = Path.Combine(root, "out2"), WorkFolder = Path.Combine(root, "work2"), At = at.AddMinutes(10),
                StartScratch = (program, ctx, file) => { reopened++; return options.StartScratch(program, ctx, file); } };
            var partsDue = keptParts["etabs"].Failed > 0 || keptParts[FieldRetest.Panel].Failed > 0;
            var second = FieldTest.Run(open, again);
            Equal(partsDue ? 1 : 0, reopened, "scratch windows reopened by the retest of an unchanged build");
            if (!partsDue)
            {
                Check(second.Steps.Any(s => s.Name == "ETABS on a scratch model: not run again") && second.Steps.Any(s => s.Name == "CSI ProMax's own features: not run again"), "the report does not name the parts left out");
                Check(second.Report.Contains("only what is new, changed or failed", StringComparison.Ordinal), "the report does not say what kind of run it was");
                Equal(keptParts["etabs"].NotTested.Count + keptParts[FieldRetest.Panel].NotTested.Count, second.Steps.Count(s => s.Skipped && s.Detail.Contains("as in the run of version", StringComparison.Ordinal)), "steps listed again as not tested");
            }
            // Changed code of ETABS's part: its window opens again; the panel's part stays left out when it passed.
            reopened = 0;
            var changed = FieldTest.Run(open, again with { Print = part => part == "etabs" ? "new" : "same", Folder = Path.Combine(root, "out3"), WorkFolder = Path.Combine(root, "work3"), At = at.AddMinutes(20) });
            Equal(1, reopened, "scratch windows reopened after ETABS's code changed");
            Check(!changed.Steps.Any(s => s.Name == "ETABS on a scratch model: not run again"), "ETABS's part was left out though its code changed");
        }
        if (Environment.GetEnvironmentVariable("CPM_FIELD_DUMP") == "1")
            foreach (var s in outcome.Steps) Console.WriteLine($"      {(s.Skipped ? "skip" : s.Ok ? "ok  " : "FAIL")} [{s.Section}] {s.Name}: {(s.Detail.Length > 160 ? s.Detail[..160] : s.Detail)}");
        var sections = outcome.Steps.Select(s => s.Section).Distinct().ToList();
        foreach (var part in new[] { PcFieldSteps.Section, FieldTest.OpenSection, ModelerFieldSteps.Section, FieldParts.ScratchSection(CsiProgram.Etabs), PanelFieldSteps.Section })
        {
            Check(sections.Contains(part), "no part " + part + " in " + string.Join(" | ", sections));
            Check(outcome.Report.Contains("\n## " + part), "the report has no heading for " + part);
        }
        Check(window is { Closed: true, Disposed: true }, "the scratch window is closed and let go of");
        Check(outcome.Steps.Any(s => s.Name == "close the scratch ETABS window without saving" && s.Ok), "closing it is a step of the report");
        Check(outcome.Steps.Any(s => s.Name == "ETABS read every story, frame and area the Modeler wrote" && s.Ok), "the scratch part reads the model the Modeler wrote");
        // The stand-in is the Modeler's model, which cannot analyse: that one step fails, and nothing else may.
        var failures = outcome.Steps.Where(s => !s.Ok).Select(s => $"[{s.Section}] {s.Name}: {s.Detail}").ToList();
        Check(failures.Count == 1 && failures[0].Contains("tool run_analysis"), $"{failures.Count} steps failed, and only the analysis should:\n      - " + string.Join("\n      - ", failures));
        Check(outcome.Steps.Any(s => s.Name == "the scratch ETABS window still has the scratch model open" && s.Ok), "the scratch model is checked again before CSI ProMax's own tools change it");
        Check(outcome.Steps.Any(s => s.Name == "tool export_pack with no folder saves beside the model" && s.Ok), "the pack goes beside the scratch model");
        // The Viewer's screen is part of every program's scratch part: checked where a browser is found, else not tested.
        var viewer = outcome.Steps.Where(s => s.Section == FieldParts.ScratchSection(CsiProgram.Etabs) && s.Name.StartsWith("Viewer, ", StringComparison.Ordinal)).ToList();
        var opened = outcome.Steps.Single(s => s.Name == "the Viewer opens on the scratch ETABS model's pack");
        if (ViewerCheck.FindBrowser() is null) Check(opened.Skipped && viewer.Count == 0, "no browser here: the Viewer is listed as not tested");
        else
        {
            Check(opened is { Ok: true, Skipped: false }, "the Viewer opened: " + opened.Detail);
            Check(viewer.Any(s => s.Name == "Viewer, the model is drawn" && s.Ok) && viewer.Count(s => s.Name.StartsWith("Viewer, button: ", StringComparison.Ordinal)) > 30, $"the Viewer's buttons are steps of the report: {viewer.Count}");
            Check(outcome.Steps.Any(s => s.Name.StartsWith("Viewer, the page shows the same numbers", StringComparison.Ordinal) && s.Ok), "the numbers of objects on the Viewer page are checked against the pack");
            Check(Directory.GetFiles(Path.Combine(root, "work"), "viewer ETABS *.png", SearchOption.AllDirectories).Length == ViewerCheck.Shots.Length, "a picture of each Viewer view is in the test's folder");
            using var sent = System.IO.Compression.ZipFile.OpenRead(outcome.Zip!);
            Check(sent.Entries.Count(x => x.Name.StartsWith("viewer ETABS ", StringComparison.Ordinal) && x.Name.EndsWith(".png", StringComparison.Ordinal)) == ViewerCheck.Shots.Length, "the pictures are in the zip to send");
        }
        Check(outcome.Steps.Count > 200, $"only {outcome.Steps.Count} steps");
        Check(outcome.Steps.Where(s => s.Skipped).All(s => s.Detail.Length > 20), "everything not tested says why");
        Check(outcome.Steps.Any(s => s.Section == PanelFieldSteps.Section && s.Name == "every tool of the panel was run, or is listed as not tested" && s.Ok), "the panel's part ran on the scratch window");
        Check(outcome.Steps.Any(s => s.Section == FieldTest.OpenSection && s.Name == "Info" && s.Ok), "the open model is read");
        Equal(frames, open.Frames().Count, "no frame was added to the open model");
        Equal(patterns, open.LoadPatterns().Count, "no load pattern was added to the open model");
        Check(outcome.Report.Contains("Kind: the full test") && outcome.Report.Contains("none to the open model"), "the report says what kind of test it was");
        Check(outcome.Report.Contains("Please send the zip beside this report"), "the report says to send the zip");
        Check(outcome.Report.Replace("\r\n", "\n").Contains("## Not working\n\n" + FieldParts.ScratchSection(CsiProgram.Etabs) + ":\n\n- tool run_analysis:"), "what failed is listed under the part it belongs to");
        Check(outcome.NotTested > 0 && outcome.Report.Contains("## Not tested") && outcome.Report.Contains("## Check by hand"), "what was not tested, and what to check by hand, come first");
        Check(outcome.Report.Contains("### not tested: "), "a step that was not tested says so in its own part");
        Equal(Path.Combine(root, "out", "Field test ETABS 2026-11-25 1530.zip"), outcome.Zip, "the zip is beside the report");
        Equal(outcome.Zip, outcome.Send, "the zip is what to send");
        Check(outcome.Summary().Contains(outcome.Zip!), "the dialog names the zip");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(outcome.Zip!))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Check(names.Contains("Field test ETABS 2026-11-25 1530.md"), "the report is in the zip");
            Check(names.Any(n => n.EndsWith(".e2k")) && names.Any(n => n.EndsWith(".s2k")) && names.Any(n => n.EndsWith(".f2k")), "the files the Modeler wrote are in the zip: " + string.Join(", ", names));
            Check(!names.Any(n => n.StartsWith("files/Bugs/")), "nothing of the made-up bug report");
        }
        Check(Directory.Exists(Path.Combine(root, "work")) && outcome.WorkFolder == Path.Combine(root, "work"), "the test's own folder");
        Check(!FieldTest.IsRunning, "it is not running any more");
        Check(CsiProMax.Bugs.BugLog.RootOverride is null, "the bug folder is put back");

        // Seen on a real SAP2000 on 2026-11-25: letting go of the scratch window threw (a faulted call, wrapped), the test
        // ended there and no report was written. Whatever closing throws, the report is written and says so.
        var thrown = FieldTest.Run(open, options with
        {
            At = at.AddMinutes(3), Viewer = false,
            StartScratch = (program, ctx, file) =>
            {
                var path = Path.Combine(ctx.Folder, "cpm-field-test.edb");
                return new CsiProMax.Tests.StandInWindow(new OfflineEtabs(OfflineEtabs.Load(file), path), path) { OnDispose = new AggregateException(new EtabsException("the scratch window is no longer there")) };
            },
        });
        Check(File.Exists(thrown.File) && thrown.Zip is not null, "the report and its zip are written when letting go of the window throws");
        Check(thrown.Steps.Any(s => !s.Ok && s.Name == "let go of the scratch ETABS window" && s.Detail.Contains("no longer there")), "the report says what letting go threw");
        Check(thrown.Steps.Any(s => s.Name == "every tool of the panel was run, or is listed as not tested"), "the parts after it still ran");

        // Seen on a real ETABS 23 on 2026-11-25: the scratch window stopped answering, and CSI ProMax's own features were
        // then run on that same window, so every one failed with the same message. They run on a link that answers.
        var dead = FieldTest.Run(open, options with
        {
            At = at.AddMinutes(4), Viewer = false,
            StartScratch = (program, ctx, file) => new CsiProMax.Tests.StandInWindow(System.Reflection.DispatchProxy.Create<ICsiLink, CsiProMax.Tests.DeadLink>(), Path.Combine(ctx.Folder, "cpm-field-test.edb")),
        });
        Check(dead.Steps.Any(s => s.Skipped && s.Name == "CSI ProMax's own features on the scratch ETABS window" && s.Detail.Contains("no longer answers")), "the window that stopped answering is left out of CSI ProMax's own features, and the report says so");
        var etabsPart = dead.Steps.Where(s => s.Section == FieldParts.ScratchSection(CsiProgram.Etabs)).ToList();
        if (Environment.GetEnvironmentVariable("CPM_FIELD_DUMP") == "1")
            foreach (var s in etabsPart) Console.WriteLine($"      {(s.Skipped ? "skip" : s.Ok ? "ok  " : "FAIL")} {s.Name}: {(s.Detail.Length > 140 ? s.Detail[..140] : s.Detail)}");
        Check(etabsPart.Count(s => !s.Ok) <= 4, $"one window that stopped answering is a few failures, not one per step: {etabsPart.Count(s => !s.Ok)} failed");
        Check(etabsPart.Any(s => s.Skipped && s.Detail.Contains("stopped answering at the step")), "the steps after it are listed as not tested, naming the step it stopped at");
        var own = dead.Steps.Where(s => s.Section == PanelFieldSteps.Section).ToList();
        Check(own.Count > 20 && own.All(s => !s.Detail.Contains("stopped answering")), "CSI ProMax's own features ran on a link that answers: " + string.Join("; ", own.Where(s => s.Detail.Contains("stopped answering")).Take(3).Select(s => s.Name)));

        // A program window that never comes back is given up on; the rest of the test still runs and the report is saved.
        using var hold = new ManualResetEventSlim(false);
        var stuck = FieldTest.Run(open, options with
        {
            At = at.AddMinutes(1), Programs = [CsiProgram.Sap2000], ScratchLimit = TimeSpan.FromSeconds(1),
            StartScratch = (_, _, _) => { hold.Wait(TimeSpan.FromSeconds(30)); return null; },
        });
        hold.Set();
        var given = stuck.Steps.SingleOrDefault(s => s.Name == "SAP2000 on a scratch model");
        Check(given is { Ok: false } && given.Detail.Contains("given up on"), "the part that did not come back is a failure that says so");
        Check(stuck.Steps.Any(s => s.Name == "every tool of the panel was run, or is listed as not tested"), "the panel's part still ran, on the open model");
        Check(File.Exists(stuck.File) && stuck.Zip is not null, "the report is still saved");
        Equal(frames, open.Frames().Count, "the open model is still as it was");
    }
    finally { ResultTools.SnapshotRoot = snapshots; Directory.Delete(root, true); }
});

foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"ok    {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}
Console.WriteLine($"\n{tests.Count - failed} of {tests.Count} passed.");
return failed == 0 ? 0 : 1;

namespace CsiProMax.Tests
{
    /// <summary>Stands in for the field test's own window of a CSI program.</summary>
    /// <summary>A link whose every call fails the way a program that stopped answering does.</summary>
    public class DeadLink : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method?.Name == "get_Program" ? CsiProgram.Etabs : throw new EtabsException("the program stopped answering (a call did not come back)");
    }

    sealed class StandInWindow(ICsiLink link, string path) : IScratchWindow
    {
        public bool Closed, Disposed;
        public ICsiLink Link => link;
        public string ModelPath => path;
        public string Close() { Closed = true; return "the scratch ETABS window was closed without saving"; }
        public Exception? OnDispose;
        public void Dispose() { Disposed = true; if (OnDispose is not null) throw OnDispose; }
    }
}

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
        public int BaseReact(ref int n, ref string[] cases, ref string[] steps, ref double[] num, ref double[] fx, ref double[] fy, ref double[] fz,
            ref double[] mx, ref double[] my, ref double[] mz, ref double gx, ref double gy, ref double gz)
        {
            n = 1; cases = ["DEAD"]; steps = [""]; num = [0]; fx = [0]; fy = [0]; fz = [120]; mx = [0]; my = [0]; mz = [0];
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


namespace CpmFakes
{
    // Shaped like SAP2000 26 on Ahmed's PC: lists with nothing in them come back as null, and the three-value units call gives nothing.
    public sealed class NameList(string[]? names)
    {
        public int GetNameList(ref int count, ref string[]? list) { count = names?.Length ?? 0; list = names; return 0; }
    }
    public sealed class SapLikeModel
    {
        public NameList RespCombo { get; } = new(null);
        public NameList LoadCases { get; } = new(["DEAD"]);
        public int GetVersion(ref string version, ref double number) { version = "26.3.0"; number = 26.3; return 0; }
        public int GetPresentUnits_2(ref int force, ref int length, ref int temperature) => 1;
        public int GetPresentUnits() => 6;
        public string GetModelFilename(bool path) => @"C:\x\cpm.sdb";
        public bool GetModelIsLocked() => false;
    }
}

namespace CpmFakes
{
    // Like ETABS 23: setting a section always answers 0, and only the objects that are there keep it.
    public sealed class SectionObjects(string have)
    {
        private readonly Dictionary<string, string> _now = new() { [have] = "" };
        public int SetSection(string name, string section, int item, double a, double b) { if (_now.ContainsKey(name)) _now[name] = section; return 0; }
        public int GetSection(string name, ref string section, ref string auto) { section = _now.GetValueOrDefault(name, ""); return 0; }
        public int SetProperty(string name, string section, int item) { if (_now.ContainsKey(name)) _now[name] = section; return 0; }
        public int GetProperty(string name, ref string section) { section = _now.GetValueOrDefault(name, ""); return 0; }
    }
    public sealed class SectionModel
    {
        public SectionObjects FrameObj { get; } = new("B1");
        public SectionObjects AreaObj { get; } = new("F1");
    }
}

namespace CpmFakes
{
    public sealed class HangingView
    {
        public readonly System.Threading.ManualResetEventSlim Hang = new(true);
        public int Redraws;
        public HangingView View => this;
        public int RefreshView(int window, bool zoom) { Redraws++; Hang.Wait(); return 0; }
    }
}

namespace CpmFakes
{
    public sealed class SteelDesign
    {
        public int GetSummaryResults(string name, ref int n, ref string[] frame, ref double[] ratio, ref int[] type, ref double[] loc, ref string[] combo, ref string[] err, ref string[] warn, int item)
        {
            if (name != "ALL" || item != 1) return 1;
            n = 1; frame = ["B1"]; ratio = [0.75]; type = [1]; loc = [0]; combo = ["COMB1"]; err = [""]; warn = [""]; return 0;
        }
    }
    public sealed class SapDesigned { public SteelDesign DesignSteel { get; } = new(); }
}

namespace CpmFakes
{
    public sealed class Tables
    {
        public int GetAllTables(ref int n, ref string[] keys, ref string[] names, ref int[] import, ref bool[] empty)
        { n = 3; keys = ["Story Definitions", "Story Stiffness", "Steel Column Envelope - AISC 360-16"]; names = keys; import = [0, 0, 0]; empty = [false, true, false]; return 0; }
        public int SetLoadCasesSelectedForDisplay(ref string[] list) => 0;
        public int SetLoadCombinationsSelectedForDisplay(ref string[] list) => 0;
        public int GetTableForDisplayArray(string key, ref string[] ask, string group, ref int version, ref string[]? fields, ref int records, ref string[]? data)
        {
            if (key != "Story Definitions") { fields = null; data = null; return 1; }
            fields = ["Story", "Height"]; records = 1; data = ["Story1", "3"]; return 0;
        }
    }
    public sealed class TablesModel { public Tables DatabaseTables { get; } = new(); }
}

namespace CpmFakes
{
    public sealed class Asce716
    {
        public string Named = ""; public bool[] Flags = []; public bool Refuse;
        // ETABS 23: 0-based, X, Y, X+e, Y+e, X-e, Y-e.
        public int SetASCE716_1(string name, ref bool[] dir, double ecc, int period, int ct, double userT, bool userZ, double top, double bottom,
            double r, double omega, double cd, double i, double ss, double s1, double tl, int site, double fa, double fv) { if (Refuse) return 1; Named = name; Flags = dir; return 0; }
        public int SetASCE716(string name, ref bool[] dir, double ecc, int period, int ct, double userT, bool userZ, double top, double bottom,
            double r, double omega, double cd, double i, double ss, double s1, double tl, int site, double fa, double fv) { if (Refuse) return 1; Named = name; Flags = dir; return 0; }
    }
    public sealed class SeismicPatterns
    {
        public readonly List<string> Names = [];
        public Asce716 AutoSeismic { get; } = new();
        public int Add(string name, int type, double self, bool addCase) { Names.Add(name); return 0; }
        public int GetNameList(ref int n, ref string[] names) { n = Names.Count; names = Names.ToArray(); return 0; }
        public int GetLoadType(string name, ref int type) { type = 5; return 0; }
        public int GetSelfWTMultiplier(string name, ref double self) { self = 0; return 0; }
        public int GetAutoSeismicCode(string name, ref string code) { code = AutoSeismic.Named == name ? "ASCE 7-16" : ""; return 0; }
    }
    public sealed class StoryFake
    {
        public int GetStories_2(ref double baseElevation, ref int n, ref string[] names, ref double[] elevations, ref double[] heights, ref bool[] master, ref string[] similar, ref bool[] splice, ref double[] spliceHeight, ref int[] color)
        {
            n = 2; names = ["Story1", "Story2"]; elevations = [3, 6]; heights = [3, 3]; master = [false, true]; similar = ["", ""]; splice = [false, false]; spliceHeight = [0, 0]; color = [0, 0]; return 0;
        }
    }
    public sealed class EtabsSeismic
    {
        public StoryFake Story { get; } = new();
        public SeismicPatterns LoadPatterns { get; } = new();
        public Asce716 AutoSeismic => LoadPatterns.AutoSeismic;
        public int GetNameList(ref int n, ref string[] names) { n = 0; names = []; return 0; }
    }
}
