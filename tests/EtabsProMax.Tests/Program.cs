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

var failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"ok    {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}
Console.WriteLine($"\n{tests.Count - failed} of {tests.Count} passed.");
return failed == 0 ? 0 : 1;
