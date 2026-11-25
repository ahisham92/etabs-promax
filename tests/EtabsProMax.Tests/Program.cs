using System.Text.Json;
using System.Text.Json.Nodes;
using EtabsProMax.Assistant;
using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Assistant.Tools;
using EtabsProMax.Checks;
using EtabsProMax.E2k;
using EtabsProMax.Model;

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
    Equal(text, E2kWriter.Write(ModelFile.FromJson(ModelFile.ToJson(E2kReader.Read(text)))), "stable through .e2k and .epm");
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

Test("model file (.epm) round trip", () =>
{
    var m = E2kReader.Read(sample);
    var back = ModelFile.FromJson(ModelFile.ToJson(m));
    Equal(E2kWriter.Write(m), E2kWriter.Write(back), "same .e2k after saving and loading the .epm");
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
Harness Endpoint(MemoryEtabs e, bool allow = true, bool ask = false, List<string>? asked = null) => new(e, allow, ask, asked);
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
        var wrapped = new EtabsProMax.Tools.ModelTool(tool);
        Check(JsonDocument.Parse(wrapped.PropertiesJson).RootElement.ValueKind == JsonValueKind.Object, $"schema of {tool.Name}");
    }
});

Test("files: a drawing is written as DXF and DWG and read back", () =>
{
    var folder = Directory.CreateTempSubdirectory("epm").FullName;
    var entities = J("""[{"type":"line","x1":0,"y1":0,"x2":6,"y2":0,"layer":"Grid A"},{"type":"polyline","points":[[0,0],[6,0],[6,5],[0,5]],"closed":true,"layer":"slab"},{"type":"circle","x":3,"y":2.5,"r":0.3,"layer":"columns"},{"type":"text","x":3,"y":3,"height":0.25,"text":"C1","layer":"columns"},{"type":"nope"}]""").EnumerateArray().ToList();
    var (drawn, skipped) = EtabsProMax.Tools.CadOut.Write(Path.Combine(folder, "a.dxf"), entities);
    Equal(4, drawn, "four drawn"); Equal(1, skipped, "one skipped");
    var back = JsonNode.Parse(EtabsProMax.Tools.ReadCadDrawingTool.Read(Path.Combine(folder, "a.dxf"), [], [], 0, 0).Text)!;
    Equal(4, (int)back["objects"]!, "the DXF reads back");
    Check(back["layers"]!.AsArray().Any(l => (string)l!["layer"]! == "GRID_A"), "layer names are made safe");
    var (dwgDrawn, _) = EtabsProMax.Tools.CadOut.Write(Path.Combine(folder, "a.dwg"), entities);
    Equal(4, dwgDrawn, "four in the DWG");
    var overview = JsonNode.Parse(EtabsProMax.Tools.ReadCadDrawingTool.Read(Path.Combine(folder, "a.dwg"), [], [], 0, 0).Text)!;
    Equal(4, (int)overview["objects"]!, "the DWG reads back"); Near(6, (double)overview["extents"]!["width"]!, "its extents");
    var columns = JsonNode.Parse(EtabsProMax.Tools.ReadCadDrawingTool.Read(Path.Combine(folder, "a.dwg"), ["COLUMNS"], ["text"], 0, 0).Text)!;
    Equal("C1", (string)columns["objects"]![0]!["text"]!, "text on a layer");
    Directory.Delete(folder, true);
});

Test("files: the kinds of file the chat makes", () =>
{
    var folder = Directory.CreateTempSubdirectory("epm").FullName;
    EtabsProMax.Tools.ToolResult Make(string json) => EtabsProMax.Tools.MakeDocumentTool.Make(new EtabsProMax.Tools.ToolArgs(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!), folder);
    foreach (var (format, ext) in new[] { ("csv", ".csv"), ("text", ".txt"), ("markdown", ".md"), ("word", ".docx"), ("pdf", ".pdf") })
    {
        var made = Make($$"""{"format":"{{format}}","title":"Reactions","content":"# Reactions\n\nA,B\n1,2"}""");
        Check(!made.IsError, format + ": " + made.Text);
        Check(File.Exists(Path.Combine(folder, "Reactions" + ext)), format + " file is there");
    }
    var excel = Make("""{"format":"excel","title":"Sections","sheets":[{"name":"S","columns":["Section","Count"],"rows":[["C500x500",12]]}]}""");
    Check(!excel.IsError && EtabsProMax.Chat.ChatFiles.Find(excel.Text).Count == 1, "the Excel file shows as a card: " + excel.Text);
    var dwg = Make("""{"format":"dwg","title":"Layout","entities":[{"type":"line","x1":0,"y1":0,"x2":1,"y2":1}]}""");
    Check(!dwg.IsError && EtabsProMax.Chat.ChatFiles.Find(dwg.Text).Single().EndsWith(".dwg"), "a DWG shows as a card: " + dwg.Text);
    Directory.Delete(folder, true);
});

Test("licensing: ETABS ProMax keys are its own", () =>
{
    Check(EtabsProMax.Licensing.LicenseKey.Prefix.StartsWith("EPM"), "the key prefix");
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
    var root = Directory.CreateTempSubdirectory("epm").FullName;
    var runtime = Directory.CreateDirectory(Path.Combine(root, "dotnet", "shared", "Microsoft.NETCore.App", "8.0.29")).FullName;
    var folder = Directory.CreateDirectory(Path.Combine(root, "ETABS ProMax")).FullName;
    string Message() { try { EtabsProMaxPlugin.Launcher.StartInfo(folder, runtime, 42004); return ""; } catch (IOException ex) { return ex.Message; } }
    Check(Message().Contains("EtabsProMax.dll is missing"), "a plug-in copied alone says what is missing");
    File.WriteAllText(Path.Combine(folder, "EtabsProMax.dll"), "");
    Check(Message().Contains("was not found"), "no dotnet");
    var dotnet = Path.Combine(root, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    File.WriteAllText(dotnet, "");
    var start = EtabsProMaxPlugin.Launcher.StartInfo(folder, runtime, 42004);
    Equal(dotnet, start.FileName, "dotnet from three folders above the runtime");
    Equal(Path.Combine(folder, "EtabsProMax.dll"), start.ArgumentList[0], "a path with a space is one argument");
    Equal("--etabs-pid 42004", start.ArgumentList[1] + " " + start.ArgumentList[2], "the assistant is told which ETABS window started it");
    Check(start.CreateNoWindow && !start.UseShellExecute, "no console window");
    Directory.Delete(root, true);
});

var failed = 0;
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
sealed class Harness(MemoryEtabs etabs, bool allow, bool ask, List<string>? asked)
{
    public (string Text, bool Failed) Call(string name, JsonElement args)
    {
        var found = EtabsTools.All.FirstOrDefault(t => t.Name == name);
        if (found is null) return ($"There is no tool '{name}'.", true);
        var tool = new EtabsProMax.Tools.ModelTool(found);
        var input = new EtabsProMax.Tools.ToolArgs(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())!);
        if (tool.AlwaysAsks || tool.ModifiesModel && ask)
        {
            asked?.Add(tool.Summary(input) ?? name);
            if (!allow) return ("The engineer declined this. Nothing was changed.", true);
        }
        var result = tool.Execute(new EtabsProMax.ETABS.EtabsApp(etabs), input);
        return (result.Text, result.IsError);
    }
}
