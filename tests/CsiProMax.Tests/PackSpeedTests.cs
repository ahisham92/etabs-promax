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

    public static IEnumerable<(string, Action)> All()
    {
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
        public int GetNameList(ref int n, ref string[] names) { n = 1; names = ["DEAD"]; return 0; }
        public int Add(string name, int type, double selfWeight, bool addCase) => 0;
    }
    public sealed class PackTablesModel
    {
        public PackCountingTables Tables { get; } = new();
        public PackCountingTables DatabaseTables => Tables;
        public PackPatterns LoadPatterns { get; } = new();
        public string GetModelFilename(bool path) => @"C:\x\cpm.EDB";
    }
}
