using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

/// <summary>
/// The review pack in minutes, not 30-60 (Ahmed, 2026-11-27): while a pack is written the program lists its tables once
/// and gives each table once, and the pack says where its time went.
/// </summary>
static class PackSpeedTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    sealed class CopyWindow(ICsiLink link, string path) : IScratchWindow
    {
        public int Closed;
        public ICsiLink Link => link;
        public string ModelPath => path;
        public string Close() { Closed++; return "closed without saving"; }
        public void Dispose() { }
    }

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("pack speed: the large model of the field test is written for ETABS, SAP2000 and SAFE, with thousands of frames and slabs", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "cpm-large-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var program in CsiProgram.All)
                {
                    var model = PackSpeedField.LargeModel(program, Path.Combine(dir, program.Key));
                    Check(model.Frames().Count >= 1000 || program.Key == CsiProgram.Safe.Key && model.Frames().Count >= 400, $"{program.Name}: {model.Frames().Count} frames");
                    Check(model.Areas().Count >= 400, $"{program.Name}: {model.Areas().Count} slabs");
                    var file = model.WriteForRun(Path.Combine(dir, program.Key, "run", "cpm-large-pack.e2k"));
                    Check(File.Exists(file) && new FileInfo(file).Length > 10000, $"{program.Name}: the run file {file}");
                    var ext = program.Key == CsiProgram.Sap2000.Key ? ".s2k" : program.Key == CsiProgram.Safe.Key ? ".f2k" : ".e2k";
                    Equal(ext, Path.GetExtension(file), $"{program.Name}: the file the program opens");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        });
        yield return ("pack speed: a pack after a run is read from a copy of the model and its results in a hidden program, which is closed, and the copy removed", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "cpm-copy-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var oldRoot = CsiProMax.Assistant.Packs.PackCopy.Root;
            var oldStart = CsiProMax.Assistant.Packs.PackCopy.Start;
            try
            {
                var model = Path.Combine(dir, "Tower.EDB");
                foreach (var f in new[] { "Tower.EDB", "Tower.Y01", "Tower.LOG", "Tower.cpmx", "Tower viewer.html", "Other.EDB" }) File.WriteAllText(Path.Combine(dir, f), f);
                var names = CsiProMax.Assistant.Packs.PackCopy.FilesOf(model).Select(Path.GetFileName).OrderBy(n => n).ToList();
                Equal("Tower.EDB,Tower.LOG,Tower.Y01", string.Join(",", names), "the model's own files");
                CsiProMax.Assistant.Packs.PackCopy.Root = Path.Combine(dir, "copies");
                CopyWindow? window = null;
                string? opened = null;
                CsiProMax.Assistant.Packs.PackCopy.Start = (p, ctx, f) =>
                {
                    opened = f;
                    Check(File.Exists(Path.ChangeExtension(f, ".Y01")), "the results were copied beside the copy");
                    Check(!File.Exists(Path.ChangeExtension(f, ".cpmx")), "the pack is not copied");
                    var m = new MemoryEtabs { ModelFile = f };
                    m.AddFrame(0, 0, 0, 0, 0, 3, "C1");
                    m.Locked = true;
                    return window = new CopyWindow(m, f);
                };
                var got = CsiProMax.Assistant.Packs.PackCopy.Read(CsiProgram.Etabs, model, l => Pack.Build(l, [], [], [], null, null, fileAs: model));
                Check(got.Value is not null, "read: " + got.Said);
                Check(opened is not null && !opened.StartsWith(dir + Path.DirectorySeparatorChar + "Tower", StringComparison.Ordinal), "the copy, not the model, was opened");
                Equal(1, window!.Closed, "the hidden program is closed");
                Equal(model, got.Value!.Content["manifest"]!["file"]!.GetValue<string>(), "the pack names the engineer's model");
                Check(!Directory.EnumerateDirectories(Path.Combine(dir, "copies")).Any(), "the copy is removed");
                Equal("Tower.EDB", File.ReadAllText(model), "the model is untouched");

                CsiProMax.Assistant.Packs.PackCopy.Start = (p, ctx, f) => window = new CopyWindow(new MemoryEtabs { ModelFile = f }, f);
                var none = CsiProMax.Assistant.Packs.PackCopy.Read(CsiProgram.Etabs, model, l => Pack.Build(l, [], [], [], null, null));
                Check(none.Value is null && none.Said.Contains("without its analysis results"), "a copy with no results is said: " + none.Said);
                Equal(1, window!.Closed, "closed then too");
                CsiProMax.Assistant.Packs.PackCopy.Start = (p, ctx, f) => null;
                var refused = CsiProMax.Assistant.Packs.PackCopy.Read(CsiProgram.Sap2000, model, l => Pack.Build(l, [], [], [], null, null));
                Check(refused.Value is null && refused.Said.Contains("could not open the copy"), "no second program is said: " + refused.Said);
            }
            finally
            {
                CsiProMax.Assistant.Packs.PackCopy.Root = oldRoot;
                CsiProMax.Assistant.Packs.PackCopy.Start = oldStart;
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        });
        yield return ("pack speed: in a read session the table list is asked for once, though the program refuses tables it lists", () =>
        {
            var model = new CpmFakes.PackTablesModel();
            using var link = new ComEtabs(CsiProgram.Etabs, () => model);
            using (PackSession.Start(link))
            {
                link.Tables();
                for (var i = 0; i < 20; i++) Equal(0, link.ReadTable("Refused " + (i % 4), null, null).Rows.Count, "a listed table the program refuses is empty");
                Equal(1, model.Tables.Listed, "GetAllTables calls in the session");
            }
            link.ReadTable("Refused 0", null, null);
            Equal(2, model.Tables.Listed, "out of a session the list is asked for again");
        });
        yield return ("pack speed: in a read session the patterns and model file are asked for once, not before every table", () =>
        {
            var model = new CpmFakes.PackTablesModel();
            using var link = new ComEtabs(CsiProgram.Etabs, () => model);
            using (PackSession.Start(link))
                for (var i = 0; i < 10; i++) link.ReadTable("Story Drifts", ["DEAD", "C" + (i % 2 == 0 ? "" : "x")], []);
            Equal(2, model.FileAsked, "model file asked (once per cases asked for)");
            var before = model.FileAsked;
            for (var i = 0; i < 3; i++) link.ReadTable("Story Drifts", ["DEAD"], []);
            Equal(before + 3, model.FileAsked, "out of a session it is asked before every table");
        });
        yield return ("pack speed: a table is read once in a session; a change or a new output setting reads it again", () =>
        {
            var model = new CpmFakes.PackTablesModel();
            using var link = new ComEtabs(CsiProgram.Etabs, () => model);
            using (PackSession.Start(link))
            {
                using (PackSession.Start(link)) Equal(2, link.ReadTable("Story Drifts", ["DEAD"], []).Rows.Count, "rows");
                link.ReadTable("Story Drifts", ["DEAD"], []);
                Equal(1, model.Tables.Read, "reads of the same table in a session (a nested one shares it)");
                link.ReadTable("Story Drifts", ["LIVE"], []);
                Equal(2, model.Tables.Read, "other cases are another read");
                PackSession.Forget(link);
                link.ReadTable("Story Drifts", ["DEAD"], []);
                Equal(3, model.Tables.Read, "after the output options changed it is read again");
                link.AddLoadPattern("W", "Wind", 0);
                link.ReadTable("Story Drifts", ["DEAD"], []);
                Equal(4, model.Tables.Read, "after a change it is read again");
            }
            link.ReadTable("Story Drifts", ["DEAD"], []);
            Equal(5, model.Tables.Read, "out of a session every read goes to the program");
        });
        yield return ("pack speed: SAP2000's frame stresses are read whole; when they would be millions of rows, one load case or combination at a time", () =>
        {
            (System.Text.Json.Nodes.JsonObject Content, IReadOnlyList<string> Notes) Make(int forceRows)
            {
                var m = new MemoryEtabs { ModelFile = @"C:\Jobs\T\T.EDB" };
                m.AddFrame(0, 0, 0, 0, 0, 3, "C1");
                m.Locked = true;
                var forces = Enumerable.Range(0, forceRows).Select(i => new[] { "C1", (i * 0.1).ToString(), "Dead", "1" }).ToList();
                m.ResultTables[Pack.StressesFrom] = new(["Frame", "Station", "OutputCase", "P"], forces);
                m.ResultTables[Pack.Stresses] = new(["Frame", "Station", "OutputCase", "S11"], [["C1", "0", "Dead", "5"]]);
                var built = Pack.Build(m, [], [], [], null, null);
                return (built.Content, built.Notes);
            }
            var small = Make(20);
            Check(small.Content["tables"]![Pack.Stresses] is not null, "the stresses of a small model are in the pack");
            Check(!small.Notes.Any(n => n.Contains("at a time", StringComparison.Ordinal)), "a small table is read in one call");
            var big = Make(200_000);
            Check(big.Content["tables"]![Pack.Stresses] is not null, "the stresses of a large model are in the pack");
            Check(big.Notes.Any(n => n.Contains(Pack.Stresses, StringComparison.Ordinal) && n.Contains("one load case or combination at a time", StringComparison.Ordinal)), "read a case at a time: " + string.Join(" | ", big.Notes));
            Check(big.Content["manifest"]?["left_out_tables"]?.ToJsonString().Contains(Pack.Stresses) != true, "nothing is left out");
            Equal(6.0, Pack.LargeMostTime.TotalMinutes, "a large model's limit");
        });
        yield return ("pack speed: a pack read from a copy is zipped after the hidden program is closed, the same pack as one zipped at once", () =>
        {
            var m = new MemoryEtabs { ModelFile = @"C:\Jobs\T\T.EDB" };
            m.AddFrame(0, 0, 0, 0, 0, 3, "C1");
            m.Locked = true;
            m.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Drift"], [["Story1", "Dead", "0.001"]]);
            var later = Pack.Build(m, [], [], [], new System.Text.Json.Nodes.JsonObject { ["ok"] = true }, null, zip: false);
            Equal(0, later.Zip.Length, "nothing is zipped while the program is open");
            var zipped = Pack.Zipped(later);
            var back = Pack.Read(zipped.Zip);
            Check(back["tables"]!["Story Drifts"] is not null, "the tables are in the zip");
            Check(back["checks"]?["ok"]?.GetValue<bool>() == true, "the checks are in the zip");
            Check(back["manifest"]!["timing"] is not null && back["manifest"]!["notes"] is not null, "the manifest is in the zip");
            Check(ReferenceEquals(zipped, Pack.Zipped(zipped)), "a zipped pack is not zipped again");
        });
        yield return ("pack complete: every result and design table the program has is in the pack, every row and field; what is not is named", () =>
        {
            var m = new MemoryEtabs { ModelFile = @"C:\Jobs\T\T.EDB" };
            m.AddFrame(0, 0, 0, 0, 0, 3, "C1");
            m.Locked = true;
            m.ResultTables["Joint Displacements"] = new(["Joint", "OutputCase", "Ux"], [["1", "Dead", "0.1"], ["2", "Dead", "0.2"]]);
            m.ResultTables["Concrete Frame Design - Summary"] = new(["Story", "Label", "Status"], [["S1", "C1", "OK"]]);
            var built = Pack.Build(m, [], [], [], null, null);
            var none = Pack.Missing(m, built.Content, [], []);
            Check(none.Count == 0, "nothing lost: " + string.Join("; ", none));
            var tables = (System.Text.Json.Nodes.JsonObject)built.Content["tables"]!;
            tables.Remove("Concrete Frame Design - Summary");
            ((System.Text.Json.Nodes.JsonArray)tables["Joint Displacements"]!["rows"]!).RemoveAt(1);
            tables["Joint Displacements"]!["fields"] = new System.Text.Json.Nodes.JsonArray("Joint", "OutputCase");
            var lost = Pack.Missing(m, built.Content, [], []);
            Check(lost.Any(l => l.Contains("Concrete Frame Design - Summary") && l.Contains("not in the pack")), "a design table left out is named: " + string.Join("; ", lost));
            Check(lost.Any(l => l.Contains("1 of 2 rows")), "a row lost is named: " + string.Join("; ", lost));
            Check(lost.Any(l => l.Contains("fields Ux")), "a field lost is named: " + string.Join("; ", lost));
        });
        yield return ("pack memory: a large table goes into the pack and its Viewer page in parts, written as it goes, and reads back whole", () =>
        {
            var old = Pack.PartRows;
            var oldWindow = Pack.WindowMostRows;
            try
            {
                Pack.PartRows = 10;
                Pack.WindowMostRows = 20;
                var m = new MemoryEtabs { ModelFile = @"C:\Jobs\T\T.EDB" };
                m.AddFrame(0, 0, 0, 0, 0, 3, "C1");
                m.Locked = true;
                m.ResultTables["Joint Displacements"] = new(["Joint", "OutputCase", "Ux"], Enumerable.Range(0, 35).Select(i => new[] { i.ToString(), "</script><!--", "0." + i }).ToList());
                var built = Pack.Build(m, [], [], [], null, null);
                var back = Pack.Read(built.Zip);
                var rows = (System.Text.Json.Nodes.JsonArray)back["tables"]!["Joint Displacements"]!["rows"]!;
                Equal(35, rows.Count, "rows read back from the parts");
                Equal("34", rows[34]![0]!.GetValue<string>(), "the last row");
                var page = Pack.ViewerHtml(built.Content);
                Equal(3, System.Text.RegularExpressions.Regex.Matches(page, "class=\"pack-rows\"").Count, "the rows past the first part in their own script elements");
                var json = page[(page.IndexOf("<script id=\"pack\"", StringComparison.Ordinal) + 40)..];
                Check(!json[..json.IndexOf("</script>", StringComparison.Ordinal)].Contains("<!--"), "nothing in the pack's JSON can end its script element");
                Check(Pack.JsonSize(built.Content) > 1000, "the size is counted");
                var window = Pack.ForWindow(built.Content);
                Equal(10, ((System.Text.Json.Nodes.JsonArray)window["tables"]!["Joint Displacements"]!["rows"]!).Count, "the window is given the first part of a pack too large for it");
                Equal(35, window["tables"]!["Joint Displacements"]!["rows_total"]!.GetValue<int>(), "with the whole row count");
            }
            finally { Pack.PartRows = old; Pack.WindowMostRows = oldWindow; }
        });
        yield return ("pack speed: the manifest says how long the pack took and which tables were slowest, against 3 minutes", () =>
        {
            var m = new MemoryEtabs { ModelFile = @"C:\Jobs\T\T.EDB" };
            m.AddFrame(0, 0, 0, 0, 0, 3, "C1");
            m.Locked = true;
            m.ResultTables["Story Drifts"] = new(["Story", "Output Case", "Drift"], [["Story1", "Dead", "0.001"]]);
            var built = Pack.Build(m, [], [], [], null, null);
            var timing = built.Content["manifest"]!["timing"]!;
            Check(timing["seconds"]!.GetValue<double>() >= 0, "seconds");
            Equal(180.0, timing["most_seconds"]!.GetValue<double>(), "the limit");
            Check(timing["slowest"] is System.Text.Json.Nodes.JsonArray, "slowest tables");
            Check(timing["steps_ms"]!["table list"] is not null, "the steps");
        });
    }
}

namespace CpmFakes
{
    public sealed class PackCountingTables
    {
        public int Listed, Read;
        public int GetAllTables(ref int n, ref string[] keys, ref string[] names, ref int[] import, ref bool[] empty)
        {
            Listed++;
            keys = ["Story Drifts", "Refused 0", "Refused 1", "Refused 2", "Refused 3"];
            names = keys; n = keys.Length; import = [0, 0, 0, 0, 0]; empty = [false, false, false, false, false];
            return 0;
        }
        public int GetAvailableTables(ref int n, ref string[] keys, ref string[] names, ref int[] import) { n = 0; keys = []; names = []; import = []; return 0; }
        public int SetLoadCasesSelectedForDisplay(ref string[] list) => 0;
        public int SetLoadCombinationsSelectedForDisplay(ref string[] list) => 0;
        public int SetLoadPatternsSelectedForDisplay(ref string[] list) => 0;
        public int GetTableForDisplayArray(string key, ref string[] ask, string group, ref int version, ref string[] fields, ref int records, ref string[] data)
        {
            if (key.StartsWith("Refused", StringComparison.Ordinal)) { fields = ["A"]; records = 0; data = []; return 1; }
            Read++;
            fields = ["Story", "Drift"]; records = 2; data = ["S2", "0.002", "S1", "0.001"];
            return 0;
        }
    }
    public sealed class PackPatterns
    {
        public int Asked;
        public int GetNameList(ref int n, ref string[] names) { Asked++; n = 1; names = ["DEAD"]; return 0; }
        public int Add(string name, int type, double selfWeight, bool addCase) => 0;
    }
    public sealed class PackTablesModel
    {
        public PackCountingTables Tables { get; } = new();
        public PackCountingTables DatabaseTables => Tables;
        public PackPatterns LoadPatterns { get; } = new();
        public int FileAsked;
        public string GetModelFilename(bool path) { FileAsked++; return @"C:\x\cpm.EDB"; }
    }
}
