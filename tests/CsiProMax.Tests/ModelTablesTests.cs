using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>
/// The model's tables in Excel (ModelTables, TableTools): exported and imported the way SAP2000 does it, one sheet
/// per table with the TABLE title, field names and units. Ahmed: "import and export excels the same exact way" as SAP.
/// </summary>
static class ModelTablesTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static StructuralModel Frame()
    {
        var m = Templates.ConcreteFrame("Tables", [6, 5], [4], 3, 3.2);
        m.AddCombo("1.4D", ("Dead", 1.4));
        m.AddCombo("ULS", ("Dead", 1.2), ("Live", 1.6));
        m.AddLineLoad(m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam).Label, "Story1", "Dead", 7.5);
        return m;
    }

    static string Temp(string name) => Path.Combine(Path.GetTempPath(), "cpm-tables-test-" + Guid.NewGuid().ToString("N")[..8] + "-" + name);

    static ModelTable Table(List<ModelTable> tables, string name) => tables.Single(t => t.Name == name);

    static JsonObject Do(ICsiLink link, string tool, object args) =>
        HandModel.Handle(link, JsonDocument.Parse(JsonSerializer.Serialize(new { tool, args })).RootElement);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("model tables: the workbook is laid out as SAP2000's, TABLE title, fields, units, data", () =>
        {
            var m = Frame();
            var path = Temp("layout.xlsx");
            try
            {
                TableTools.Write(path, ModelTables.Export(m));
                using var book = CsiProMax.Tools.XlsxBook.Read(path);
                Check(book.SheetNames.Count == ModelTables.Names.Length, $"one sheet per table ({book.SheetNames.Count})");
                var sheet = book.SheetNames.First(s => s.StartsWith("Story Definitions", StringComparison.Ordinal));
                Equal("TABLE:  Story Definitions", book.Text(sheet, 0, 1), "row 1 holds the table's title");
                Equal("Story", book.Text(sheet, 0, 2), "row 2 holds the field names"); Equal("Height", book.Text(sheet, 1, 2), "field 2");
                Equal("m", book.Text(sheet, 1, 3), "row 3 holds the units");
                Equal("Story3", book.Text(sheet, 0, 4), "the data starts on row 4, top story first, as the programs list them");
                Equal(3.2, book.Number(sheet, 1, 4), "numbers are numbers in Excel");
                var concrete = book.SheetNames.First(s => s.StartsWith("Material Properties", StringComparison.Ordinal));
                Equal("kN/m²", book.Text(concrete, 1, 3), "stresses carry their unit");
            }
            finally { File.Delete(path); }
        });

        yield return ("model tables: export then import into an empty model gives the same model back", () =>
        {
            var m = Frame();
            var path = Temp("round.xlsx");
            try
            {
                TableTools.Write(path, ModelTables.Export(m));
                var back = ModelEdit.New("Tables");
                var result = ModelTables.Import(back, TableTools.Read(path));
                Equal(ModelTables.Names.Length, result.Read.Count, "every table was read");
                E2kWriter.SavedAt = new DateTime(2026, 11, 25, 12, 0, 0);
                try
                {
                    var a = E2kWriter.Write(m).Split('\n');
                    var b = E2kWriter.Write(back).Split('\n');
                    var diff = a.Zip(b).Select((p, i) => (p, i)).FirstOrDefault(x => x.p.First != x.p.Second);
                    Check(a.Length == b.Length && diff.p.First is null, $"the .e2k is the same (line {diff.i + 1}: '{diff.p.First?.Trim()}' vs '{diff.p.Second?.Trim()}'; {a.Length} vs {b.Length} lines)");
                }
                finally { E2kWriter.SavedAt = null; }
            }
            finally { File.Delete(path); }
        });

        yield return ("model tables: edits made in Excel come in, in the file's units, and rows left out are removed", () =>
        {
            var m = Frame();
            var tables = ModelTables.Export(m);
            // Sections in mm.
            var sections = Table(tables, ModelTables.Rectangular);
            for (var i = 0; i < sections.Units.Count; i++) if (sections.Units[i] == "m") sections.Units[i] = "mm";
            foreach (var row in sections.Rows) { row[2] = (double.Parse(row[2]) * 1000).ToString(); row[3] = (double.Parse(row[3]) * 1000).ToString(); }
            sections.Rows.Single(r => r[0] == "C500x500")[2] = "600";
            // A beam deleted on Story1, a combination added, the live load in N/m².
            var assigns = Table(tables, ModelTables.FrameSections);
            var beam = assigns.Rows.First(r => r[0] == "Story1" && r[2] == "B300x600");
            assigns.Rows.Remove(beam);
            Table(tables, ModelTables.Combos).Rows.Add(["SLS", "Linear Add", "Dead", "1"]);
            Table(tables, ModelTables.Combos).Rows.Add(["", "", "Live", "1"]);
            var loads = Table(tables, ModelTables.AreaLoads);
            loads.Units[4] = "N/m²";
            foreach (var row in loads.Rows) row[4] = (double.Parse(row[4]) * 1000).ToString();
            var path = Temp("edit.xlsx");
            try
            {
                TableTools.Write(path, tables);
                var result = ModelTables.Import(m, TableTools.Read(path));
                Equal(0.6, m.FrameSections.Single(s => s.Name == "C500x500").Number("D"), "600 mm came in as 0.6 m");
                Equal(0.3, m.FrameSections.Single(s => s.Name == "B300x600").Number("B"), "300 mm came in as 0.3 m");
                Check(!m.LineAssigns.Exists(a => a.Label == beam[1] && a.Story == "Story1"), "the beam left out of the table is gone");
                Check(!m.LineLoads.Exists(l => l.Object == beam[1] && l.Story == "Story1"), "and its load with it");
                Check(result.Notes.Exists(n => n.Contains("not in Frame Assignments", StringComparison.Ordinal)), "the answer says a frame was removed: " + string.Join(" | ", result.Notes));
                var sls = m.Combos.Single(c => c.Name == "SLS");
                Equal(3, sls.Lines.Count, "SLS: its type and two cases");
                Equal(3.0, E2kText.TryNumber(E2kText.Value(m.AreaLoads.First(l => l.Pattern.Contains("Live")).Tokens, "FVAL"), out var v) ? v : 0, "3000 N/m² came in as 3 kN/m²");
            }
            finally { File.Delete(path); }
        });

        yield return ("model tables: a table that can't be read changes nothing and names the table, row and field", () =>
        {
            HandModel.Reset();
            var link = new OfflineEtabs(Frame());
            var before = ModelFile.ToJson(link.Model);
            var tables = ModelTables.Export(link.Model, [ModelTables.FrameSections, ModelTables.Rectangular]);
            Table(tables, ModelTables.Rectangular).Rows[0][2] = "0.9";
            Table(tables, ModelTables.FrameSections).Rows[3][0] = "Story9";
            var path = Temp("bad.xlsx");
            try
            {
                TableTools.Write(path, tables);
                var a = Do(link, "import_tables_excel", new { path });
                Check(!a["ok"]!.GetValue<bool>(), "the import was refused");
                var error = a["error"]!.GetValue<string>();
                Check(error.Contains("Frame Assignments - Section Properties, row 7", StringComparison.Ordinal) && error.Contains("Story9", StringComparison.Ordinal), "the error names the table and the row Excel shows (data starts on row 4): " + error);
                Equal(before, ModelFile.ToJson(link.Model), "the model is as it was, also the section changed before the bad row");
            }
            finally { File.Delete(path); }
        });

        yield return ("model tables: the window imports a workbook it was handed, and Undo takes it back", () =>
        {
            HandModel.Reset();
            var link = new OfflineEtabs(Frame());
            var before = ModelFile.ToJson(link.Model);
            var tables = ModelTables.Export(link.Model, [ModelTables.Patterns]);
            Table(tables, ModelTables.Patterns).Rows.Add(["Wind X", "Wind", "0"]);
            var path = Temp("window.xlsx");
            try
            {
                TableTools.Write(path, tables);
                var data = Convert.ToBase64String(File.ReadAllBytes(path));
                var a = Do(link, "import_tables_excel", new { data = "data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64," + data, file_name = "window.xlsx" });
                Check(a["ok"]!.GetValue<bool>(), "imported: " + a["error"]);
                Check(link.Model.LoadPatterns.Exists(p => p.Name == "Wind X" && p.Type == "Wind"), "the new pattern is in the model");
                Check(link.Model.LoadCases.Exists(c => c.Name == "Wind X"), "with its load case");
                var u = HandModel.Handle(link, JsonDocument.Parse("""{"undo":true}""").RootElement);
                Check(u["ok"]!.GetValue<bool>(), "undo: " + u["error"]);
                Equal(before, ModelFile.ToJson(link.Model), "Undo takes the whole import back");
            }
            finally { File.Delete(path); }
        });

        yield return ("model tables: a definition left out goes only when nothing uses it", () =>
        {
            var m = Frame();
            m.AddRectangularSection("Spare", ModelEdit.DefaultConcrete, 0.4, 0.4, beam: false);
            var tables = ModelTables.Export(m, [ModelTables.Rectangular]);
            Table(tables, ModelTables.Rectangular).Rows.RemoveAll(r => r[0] is "Spare" or "C500x500");
            var result = ModelTables.Import(m, tables);
            Check(!m.FrameSections.Exists(s => s.Name == "Spare"), "the unused section went");
            Check(m.FrameSections.Exists(s => s.Name == "C500x500"), "the used one stayed");
            Check(result.Notes.Exists(n => n.Contains("C500x500", StringComparison.Ordinal) && n.Contains("kept", StringComparison.Ordinal)), "and the answer says why");
            Check(!(m.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS")?.Lines ?? []).Exists(l => l.Contains("\"Spare\"", StringComparison.Ordinal)), "its reinforcement data went with it");
        });

        yield return ("model tables: SAP2000's own tables of the model go to Excel too", () =>
        {
            HandModel.Reset();
            var folder = Temp("sap");
            var link = new OfflineEtabs(Frame());
            var a = Do(link, "export_tables_excel", new { format = "sap2000", folder, name = "Frame" });
            Check(a["ok"]!.GetValue<bool>(), "exported: " + a["error"]);
            try
            {
                var tables = TableTools.Read(Path.Combine(folder, "Frame.xlsx"));
                var joints = tables.Single(t => t.Name == "Joint Coordinates");
                Equal("m", joints.Units[joints.Field("XorR")], "joint coordinates carry their unit");
                Check(joints.Rows.Count > 0 && tables.Exists(t => t.Name == "Connectivity - Frame") && tables.Exists(t => t.Name == "Load Combination Definitions" || t.Name == "Combination Definitions"), "frames and combinations are there");
            }
            finally { Directory.Delete(folder, true); }
        });

        yield return ("model tables: units the tables use convert both ways", () =>
        {
            Equal(1000.0, ModelTables.UnitFactor("kN"), "kN");
            Equal(1e6, Math.Round(ModelTables.UnitFactor("N/mm²")!.Value), "N/mm² is MPa");
            Equal(1e6, Math.Round(ModelTables.UnitFactor("N/mm2")!.Value), "N/mm2 too");
            Equal(1000.0, Math.Round(ModelTables.UnitFactor("kN-m")!.Value, 6), "kN-m");
            Check(Math.Abs(ModelTables.UnitFactor("kip/ft^3")!.Value - 157087.5) < 1, "kip/ft³");
            Check(ModelTables.UnitFactor("furlong") is null, "an unknown unit is refused");
        });
    }
}

/// <summary>
/// The Modeler's window in a real browser (ModelerCheck), when this machine has one: the live page behind the
/// Modeler's own server and edit route, every File, Assign, Define, Draw and Edit item run, with the AI chat on and off.
/// </summary>
static class ModelerWindowTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        foreach (var chat in new[] { true, false })
            yield return ($"modeler window: every menu item works in a browser, {(chat ? "with the AI chat" : "without the AI")}", () =>
            {
                if (CsiProMax.Assistant.ViewerCheck.FindBrowser() is null) return;
                var seen = CsiProMax.Assistant.ModelerCheck.Run(chat, Path.Combine(Path.GetTempPath(), "cpm-modeler-check"));
                Check(seen.NotRun is null, "the browser ran: " + seen.NotRun);
                if (Environment.GetEnvironmentVariable("CPM_SHOW_VIEWER_CHECK") == "1")
                    foreach (var i in seen.Items) Console.WriteLine($"   {(i.Skipped ? "skip" : i.Ok ? "ok  " : "FAIL")} {i.Name}: {i.Detail}");
                Check(seen.Items.Count(i => i.Name.StartsWith("menu: ", StringComparison.Ordinal) && i.Ok && !i.Skipped) > 30, "most menu items ran: " + seen.Items.Count(i => i.Name.StartsWith("menu: ", StringComparison.Ordinal) && !i.Skipped));
                Check(seen.Items.All(i => i.Ok || i.Skipped), "the Modeler's window: " + string.Join("; ", seen.Items.Where(i => !i.Ok).Select(i => i.Name + ": " + i.Detail)));
                // Nothing is left out (Ahmed 2026-11-26): File > Open, Import > Excel and Print run another way, and an item
                // greyed out where the check reaches it is a failure.
                var skipped = seen.Items.Where(i => i.Skipped).Select(i => i.Name).ToList();
                Check(skipped.Count == 0, "items left out: " + string.Join(", ", skipped));
                foreach (var must in new[] { "File > Open...", "Tables from Excel (.xlsx)...", "Print the active window..." })
                    Check(seen.Items.Any(i => i.Ok && !i.Skipped && i.Name.Contains(must, StringComparison.Ordinal)), must + " was not run");
            });
    }
}
