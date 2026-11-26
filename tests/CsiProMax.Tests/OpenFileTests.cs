using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.F2k;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// The Modeler opens every program's text file, both ways and with no limit (Ahmed, 2026-11-26: "whenever I import
/// from s2k or e2k or f2k to and from the csi promax ... the user does not feel anything"): a file CSI ProMax wrote
/// comes back exactly; one SAP2000 or SAFE wrote is read from its tables; the review pack and the Excel tables hold
/// every row however long.
/// </summary>
static class OpenFileTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    static int Terms(StructuralModel m) => m.Combos.Sum(c => c.Lines.Count(l => E2kText.Value(l, "LOADCASE") is not null || E2kText.Value(l, "LOADCOMBO") is not null));

    static string Temp(string ext) => Path.Combine(Path.GetTempPath(), "cpm-open-" + Guid.NewGuid().ToString("N")[..8] + ext);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("open: a .s2k the Modeler wrote opens exactly as it was, and an edited one is read from its tables", () =>
        {
            var m = Templates.ConcreteFrame("Two bays", [6, 6], [5], 3, 3.2);
            m.AddLoadPattern("SDL", "Super Dead");
            m.AddCombo("1.2D+1.6L", ("Dead", 1.2), ("Live", 1.6));
            m.Target = "sap2000";
            E2kWriter.SavedAt = new DateTime(2026, 11, 26, 12, 0, 0);
            var path = Temp(".s2k");
            try
            {
                S2kWriter.WriteFile(m, path);
                var text = File.ReadAllText(path);
                Check(CsiTableText.Parse(text).ContainsKey("JOINT COORDINATES"), "the tables SAP2000 reads are all there, ahead of END TABLE DATA");
                Check(text.IndexOf("END TABLE DATA", StringComparison.Ordinal) < text.IndexOf("$ CSI ProMax model", StringComparison.Ordinal), "the model goes after END TABLE DATA, where SAP2000 stops reading");
                var opened = OfflineEtabs.Open(path);
                Check(opened.Exact && opened.Notes.Count == 0, "the Modeler's own file opens exactly, with nothing to say");
                Equal(E2kWriter.Write(m), E2kWriter.Write(opened.Model), "the same model, to the last line");
                Equal("sap2000", opened.Model.Target, "still made for SAP2000");
                // Changed by hand after it was written: the tables are what the file says now.
                File.WriteAllText(path, text.Replace("LoadPat=SDL   DesignType=\"Super Dead\"", "LoadPat=SDL2   DesignType=\"Super Dead\"", StringComparison.Ordinal));
                var edited = OfflineEtabs.Open(path);
                Check(!edited.Exact && edited.Model.LoadPatterns.Exists(p => p.Name == "SDL2"), "an edited file is read from its tables");
                Equal("", OfflineEtabs.SavesTo(path, edited), "a file read from its tables opens as a copy: saving never writes over it");
            }
            finally { File.Delete(path); E2kWriter.SavedAt = null; }
        });

        yield return ("open: the large model's .s2k read from its tables gives every joint, member, support, load, case and combination back", () =>
        {
            var m = LargeModelTests.Large();
            var text = S2kWriter.Write(m).Text;
            var r = S2kReader.Read(text, "Large");
            Check(!r.Exact, "read from the tables (no model carried in the text)");
            var back = r.Model;
            Equal(m.Stories.Count, back.Stories.Count, "levels");
            Equal(string.Join(",", m.Stories.Select(s => s.Name)), string.Join(",", back.Stories.Select(s => s.Name)), "level names (the Z grid lines)");
            Equal(m.Points.Count, back.Points.Count, "plan points");
            Equal(Physical.Frames(m).Count(), Physical.Frames(back).Count(), "frames");
            foreach (var kind in Enum.GetValues<LineKind>())
                Equal(Physical.Frames(m).Count(f => f.Kind == kind), Physical.Frames(back).Count(f => f.Kind == kind), kind + "s");
            Equal(m.Lines.Count, back.Lines.Count, "line objects (one per plan line, shared by the stories)");
            Equal(Physical.Areas(m).Count(), Physical.Areas(back).Count(), "areas");
            Equal(m.Areas.Count, back.Areas.Count, "area objects");
            Equal(m.PointAssigns.Count(p => p.Restraint is not null), back.PointAssigns.Count(p => p.Restraint is not null), "supports");
            Equal(m.AreaAssigns.Count(a => a.Diaphragm is not null), back.AreaAssigns.Count(a => a.Diaphragm is not null), "slabs on a diaphragm");
            Equal(m.LoadPatterns.Count, back.LoadPatterns.Count, "load patterns");
            Equal(string.Join(",", m.LoadPatterns.Select(p => p.Type)), string.Join(",", back.LoadPatterns.Select(p => p.Type)), "pattern types");
            Equal(m.LoadCases.Count, back.LoadCases.Count, "load cases");
            Equal(m.Combos.Count, back.Combos.Count, "combinations");
            Equal(Terms(m), Terms(back), "combination terms");
            Equal(m.Combos.Count(c => c.Lines.Any(l => l[0] == "LOADCOMBO")), back.Combos.Count(c => c.Lines.Any(l => l[0] == "LOADCOMBO")), "envelopes of combinations");
            Equal(m.AreaLoads.Count, back.AreaLoads.Count, "area loads");
            Equal(m.LineLoads.Count, back.LineLoads.Count, "frame loads");
            Equal(m.AreaLoads.Sum(l => E2kText.TryNumber(E2kText.Value(l.Tokens, "FVAL"), out var v) ? v : 0), back.AreaLoads.Sum(l => E2kText.TryNumber(E2kText.Value(l.Tokens, "FVAL"), out var v) ? v : 0), "the area loads' values");
            // Each member in the same place with the same section.
            var where = Physical.Frames(m).ToDictionary(f => (f.Label, f.Story));
            foreach (var f in Physical.Frames(back))
            {
                Check(where.TryGetValue((f.Label, f.Story), out var o), $"frame {f.Label} on {f.Story} keeps its label and story");
                Check(o!.Section == f.Section && Math.Abs(o.I.Z - f.I.Z) < 1e-6 && Math.Abs(o.J.X - f.J.X) < 1e-6, $"frame {f.Label} on {f.Story} is where it was, with its section");
            }
            // And it writes the same .s2k again.
            Equal(CsiTableText.Parse(text)["CONNECTIVITY - FRAME"].Count, CsiTableText.Parse(S2kWriter.Write(back).Text)["CONNECTIVITY - FRAME"].Count, "the .s2k written again has every frame");
        });

        yield return ("open: the large model's .f2k opens again, every floor's file with the whole model, and a floor alone read from its tables", () =>
        {
            var m = LargeModelTests.Large();
            m.Target = "safe";
            E2kWriter.SavedAt = new DateTime(2026, 11, 26, 12, 0, 0);
            try
            {
            var floors = F2kWriter.WriteEveryFloor(m);
            Equal(20, floors.Count, "one file for each floor");
            var carried = F2kReader.Read(EmbeddedModel.Attach(floors[5].Text, m));
            Check(carried.Exact, "a floor's file the Modeler wrote opens as the whole model");
            Equal(E2kWriter.Write(m), E2kWriter.Write(carried.Model), "the whole model, to the last line");
            var alone = F2kReader.Read(floors[0].Text, "Floor");
            var back = alone.Model;
            Equal(15 * 15, Physical.Areas(back).Count(a => a.Kind == AreaKind.Floor), "the floor's slabs");
            Equal(m.LoadPatterns.Count, back.LoadPatterns.Count, "load patterns");
            var written = CsiTableText.Parse(floors[0].Text);
            Equal(written["LOAD COMBINATION DEFINITIONS"].Select(r => r["Name"]).Distinct().Count(), back.Combos.Count, "every combination of the file");
            Equal(written["AREA LOAD ASSIGNMENTS - UNIFORM"].Count, back.AreaLoads.Count, "the floor's slab loads");
            Equal(written["COLUMN OBJECT CONNECTIVITY"].Count, Physical.Frames(back).Count(f => f.Kind == LineKind.Column), "the columns under and over the floor");
            Equal("safe", back.Target, "made for SAFE");
            }
            finally { E2kWriter.SavedAt = null; }
        });

        yield return ("open: SAFE 20 and 23's own .f2k files open in the Modeler with their slabs, columns, supports, loads, cases, combinations, tendons and strips", () =>
        {
            foreach (var file in new[] { "safe-PT-SL-v20.f2k", "safe-RC-PN-v20.f2k", "safe-PT-SL-v23.f2k", "safe-RC-PN-v23.f2k" })
            {
                var text = Sample(file);
                var t = CsiTableText.Parse(text);
                int Rows(string table) => t.TryGetValue(table, out var r) ? r.Count : 0;
                var r = F2kReader.Read(text, file);
                var m = r.Model;
                Equal(Rows("FLOOR OBJECT CONNECTIVITY") + Rows("WALL OBJECT CONNECTIVITY"), Physical.Areas(m).Count(), file + ": slabs and walls");
                Equal(Rows("COLUMN OBJECT CONNECTIVITY") + Rows("BEAM OBJECT CONNECTIVITY"), Physical.Frames(m).Count(), file + ": columns and beams");
                Equal(Rows("JOINT ASSIGNMENTS - RESTRAINTS"), m.PointAssigns.Count(p => p.Restraint is not null), file + ": supports");
                Equal(Rows("LOAD PATTERN DEFINITIONS"), m.LoadPatterns.Count, file + ": load patterns");
                Equal(Rows("LOAD CASE DEFINITIONS - SUMMARY"), m.LoadCases.Count, file + ": load cases (static, modal, hyperstatic): " + string.Join(" | ", r.Notes.Where(n => n.StartsWith("Load case") || n.StartsWith("Hyper"))));
                Equal(t["LOAD COMBINATION DEFINITIONS"].Select(x => x["Name"]).Distinct().Count(), m.Combos.Count, file + ": combinations");
                Equal(Rows("AREA LOAD ASSIGNMENTS - UNIFORM"), m.AreaLoads.Count, file + ": slab loads");
                var tendons = t.TryGetValue("TENDON OBJECT CONNECTIVITY", out var tr) ? tr.Select(x => x["Name"]).Distinct().Count() : 0;
                Equal(tendons, SlabDefs.Tendons(m).Count, file + ": tendons: " + string.Join(" | ", r.Notes.Where(n => n.StartsWith("Tendon")).Take(3)));
                var strips = t.TryGetValue("STRIP OBJECT CONNECTIVITY", out var sr) ? sr.Select(x => x["Name"]).Distinct().Count() : 0;
                Equal(strips, SlabDefs.Strips(m).Count, file + ": design strips: " + string.Join(" | ", r.Notes.Where(n => n.StartsWith("Design strip")).Take(3)));
                Equal("safe", m.Target, file + ": made for SAFE");
                // The model the Modeler made of it is whole: it writes an .e2k that reads back, and SAFE's file again.
                E2kReader.Read(E2kWriter.Write(m));
                var again = F2kWriter.Write(m);
                Equal(Physical.Areas(m).Count(a => a.Kind == AreaKind.Floor), again.Slabs, file + ": the .f2k written again has every slab");
                Console.WriteLine($"      {file}: {Physical.Areas(m).Count()} areas, {Physical.Frames(m).Count()} frames, {m.LoadCases.Count} cases, {m.Combos.Count} combinations, {tendons} tendons, {strips} strips; not read: {r.Notes.Count}");
            }
        });

        yield return ("open: a long table goes into the pack and its Viewer page in parts and comes back whole", () =>
        {
            var before = Pack.PartRows;
            Pack.PartRows = 7;
            try
            {
                var link = new MemoryEtabs { ModelFile = @"C:\Jobs\Long\Long.EDB" };
                link.AddFrame(0, 0, 0, 0, 0, 3, "C500x500");
                link.Locked = true;
                var rows = Enumerable.Range(1, 30).Select(i => (IReadOnlyList<string>)new[] { "Story1", "Dead", i.ToString() }).ToList();
                link.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Drift"], rows.Select(r => r.ToArray()).ToList());
                var built = Pack.Build(link, [], [], ["Story Drifts"], null, null);
                using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(built.Zip)))
                    Check(zip.Entries.Count(e => e.FullName.StartsWith("tables/", StringComparison.Ordinal)) >= 5, "the table is stored in parts");
                var back = Pack.Read(built.Zip);
                var table = back["tables"]!["Story Drifts"]!;
                Equal(30, table["rows"]!.AsArray().Count, "every row comes back");
                Equal("30", table["rows"]![29]![2]!.GetValue<string>(), "in their order");
                Check(table["parts"] is null, "joined, with no trace of the parts");
                var page = Pack.ViewerHtml(back);
                Check(page.Split("class=\"pack-rows\"").Length - 1 == 4, "the Viewer page carries the rest of the rows in script elements of their own");
                Check(File.ReadAllText(Path.Combine(SourceText.Root(), "src/CsiProMax.Viewer/viewer.html")).Contains("function withRows("), "the Viewer joins them back");
            }
            finally { Pack.PartRows = before; }
        });

        yield return ("open: a table too long for one Excel sheet goes on more sheets and reads back as one", () =>
        {
            var before = TableTools.SheetRows;
            TableTools.SheetRows = 10;
            var path = Temp(".xlsx");
            try
            {
                var m = LargeModelTests.Large();
                var tables = ModelTables.Export(m);
                TableTools.Write(path, tables);
                var read = TableTools.Read(path);
                Equal(tables.Count, read.Count, "the same tables");
                foreach (var t in tables)
                    Equal(t.Rows.Count, read.First(x => x.Name == t.Name).Rows.Count, t.Name + " rows");
                var fromExcel = ModelEdit.New("Large");
                ModelTables.Import(fromExcel, read);
                Equal(E2kWriter.Write(m), E2kWriter.Write(fromExcel), "the model from the split workbook is the same model");
            }
            finally { TableTools.SheetRows = before; File.Delete(path); }
        });
    }
}
