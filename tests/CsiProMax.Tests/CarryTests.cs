using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

/// <summary>The carry step (Ahmed 2026-11-27): what a program's model holds that its text file does not carry is kept in
/// the .cpm as rows of the program's own tables, and goes back in through them after the text file on every run.</summary>
static class CarryTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static TableData T(string[] fields, params string[][] rows) => new(fields, rows.ToList());

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("carry: only what the text file lost is kept: a lost table whole, a lost row whole, a changed field with its row's key", () =>
        {
            var before = new Dictionary<string, TableData>
            {
                ["Material Properties - Damping Parameters"] = T(["Material", "ModalRatio", "GUID"], ["C30", "0.05", "a"], ["S355", "0.02", "b"]),
                ["Frame Assignments - Output Stations"] = T(["Story", "Label", "UniqueName", "StationType", "MinStations"], ["S1", "B1", "1", "Min Stations", "3"], ["S1", "B2", "2", "Min Stations", "5"], ["S2", "B1", "3", "Min Stations", "3"]),
                ["Load Pattern Definitions"] = T(["Name", "Type", "SelfWtMult"], ["Dead", "Dead", "1"]),
                ["Story Definitions"] = T(["Name", "Height"], ["S1", "3"]),
            };
            var after = new Dictionary<string, TableData>
            {
                ["Frame Assignments - Output Stations"] = T(["Story", "Label", "UniqueName", "StationType", "MinStations"], ["S1", "B1", "7", "Min Stations", "3"], ["S1", "B2", "8", "Min Stations", "3"]),
                ["Load Pattern Definitions"] = T(["Name", "Type", "SelfWtMult"], ["Dead", "Dead", "1.00000001"]),
                ["Story Definitions"] = T(["Name", "Height"], ["S1", "3"]),
            };
            var c = ProgramCarry.Measure(CsiProgram.Etabs, before, after)!;
            Check(c.Program == "etabs" && c.Rows.Count == 2, "two tables to carry: " + string.Join(", ", c.Rows.Keys));
            var damping = c.Rows["Material Properties - Damping Parameters"];
            Check(damping.Count == 2 && damping.All(r => !r.ContainsKey("GUID")) && damping[0]["ModalRatio"] == "0.05", "the lost table whole, without its GUIDs");
            Check(c.Keys["Material Properties - Damping Parameters"] == "Material", "keyed by material: " + c.Keys["Material Properties - Damping Parameters"]);
            var stations = c.Rows["Frame Assignments - Output Stations"];
            Check(c.Keys["Frame Assignments - Output Stations"] == "Story|Label", "an object's rows by story and label: " + c.Keys["Frame Assignments - Output Stations"]);
            Check(stations.Count == 2, "B2's changed stations and the lost S2 row: " + stations.Count);
            var b2 = stations.Single(r => r["Label"] == "B2");
            Check(b2.Count == 3 && b2["MinStations"] == "5" && !b2.ContainsKey("UniqueName") && !b2.ContainsKey("StationType"), "a changed row carries its key and the changed field only: " + string.Join(",", b2.Keys));
            Check(stations.Any(r => r["Story"] == "S2" && r["StationType"] == "Min Stations"), "a lost row is carried whole");
            Check(ProgramCarry.Measure(CsiProgram.Etabs, after, after) is null, "nothing lost, nothing carried");
        });

        yield return ("carry: a table's key tells apart rows of one thing (a link's directions); a table with no named key uses its text fields", () =>
        {
            var link = new CsiProMax.Model.TableCarry.Table(["Name", "DOF", "Fixed", "TransKE"], [["L1", "U1", "No", "100"], ["L1", "U2", "No", "50"]]);
            Check(string.Join("|", TableCarry.KeyFields(link)) == "Name|DOF", "Name|DOF: " + string.Join("|", TableCarry.KeyFields(link)));
            var plain = new CsiProMax.Model.TableCarry.Table(["Option", "Value", "Count"], [["A", "Yes", "2"]]);
            Check(string.Join("|", TableCarry.KeyFields(plain)) == "Option|Value", "text fields: " + string.Join("|", TableCarry.KeyFields(plain)));
            Check(TableCarry.Rank("Material Properties - General") < TableCarry.Rank("Frame Section Property Definitions - Summary")
                && TableCarry.Rank("Frame Section Property Definitions - Summary") < TableCarry.Rank("Frame Assignments - Section Properties"), "definitions before what uses them");
        });

        yield return ("carry: kept in the .cpm, converted with the units, and named for a run in its program only", () =>
        {
            var m = new StructuralModel { Units = new Units { Force = "kN", Length = "m", Temperature = "C" } };
            m.Carried = new CarriedTables { Program = "etabs", Keys = { ["Frame Assignments - Insertion Point"] = "Story|Label" },
                Rows = { ["Frame Assignments - Insertion Point"] = [new() { ["Story"] = "S1", ["Label"] = "B1", ["XI"] = "1" }] } };
            var back = ModelFile.FromJson(ModelFile.ToJson(m));
            Check(back.Carried is { Count: 1 } && back.Carried.Keys["Frame Assignments - Insertion Point"] == "Story|Label", "the .cpm keeps it");
            Check(ProgramCarry.Has(back, CsiProgram.Etabs) && !ProgramCarry.Has(back, CsiProgram.Sap2000), "for ETABS only");
            var dir = Path.Combine(Path.GetTempPath(), "cpm-carry-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                var run = new OfflineEtabs(back, "");
                var e2k = run.WriteForRun(Path.Combine(dir, "run.e2k"));
                var cpm = ModelerTables.ModelerFileBeside(e2k);
                Check(cpm is not null, "a run's file has the Modeler's file beside it");
                Check(ModelerTables.HasRows(cpm!, CsiProgram.Etabs), "with rows to put in after the file opens");
            }
            finally { Directory.Delete(dir, true); }
        });
    }
}
