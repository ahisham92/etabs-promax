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

        yield return ("carry (0.4.73 saved-4): ETABS's own diaphragm centre points are never carried; points are keyed by story and point label", () =>
        {
            string[] pf = ["UniqueName", "IsAuto", "Story", "PointBay", "IsSpecial", "X", "Y", "Z", "GUID"];
            string[] df = ["Story", "Label", "UniqueName", "Diaphragm"];
            var before = new Dictionary<string, TableData>
            {
                ["Point Object Connectivity"] = T(pf, ["1", "No", "Story1", "1", "No", "0", "0", "3", "g1"], ["2", "No", "Story1", "2", "No", "6", "0", "3", "g2"], ["37", "Yes", "Story1", "10", "Yes", "5.6992", "4.4987", "3", "g3"]),
                ["Joint Assignments - Diaphragms"] = T(df, ["Story1", "1", "1", "D1"], ["Story1", "10", "37", "D1"]),
            };
            var after = new Dictionary<string, TableData>
            {
                ["Point Object Connectivity"] = T(pf, ["1", "No", "Story1", "1", "No", "0", "0", "3", "h1"], ["2", "No", "Story1", "2", "No", "6.5", "0", "3", "h2"], ["40", "Yes", "Story1", "13", "Yes", "5.6923", "4.4872", "3", "h3"]),
                ["Joint Assignments - Diaphragms"] = T(df, ["Story1", "1", "1", "D1"], ["Story1", "13", "40", "D1"]),
            };
            var c = ProgramCarry.Measure(CsiProgram.Etabs, before, after)!;
            Check(c.Keys["Point Object Connectivity"] == "Story|PointBay", "points by story and label: " + c.Keys["Point Object Connectivity"]);
            var points = c.Rows["Point Object Connectivity"];
            Check(points.Count == 1 && points[0]["PointBay"] == "2" && points[0]["X"] == "6", "only the real point that moved: " + string.Join(" / ", points.Select(r => string.Join(",", r))));
            Check(!c.Rows.ContainsKey("Joint Assignments - Diaphragms"), "nothing of the centre point's diaphragm row");
        });

        yield return ("carry (0.4.73 Ex002): a one-row options table merges into ETABS's row; ~ items and a table set's repacked lists are not carried", () =>
        {
            string[] mo = ["StartPage", "CursorValue", "FrameInfo", "BoundPlane", "ArchDim"];
            string[] ts = ["Name", "TableKey", "LoadPattern", "LoadCase", "Combo", "GUID"];
            var before = new Dictionary<string, TableData>
            {
                ["Miscellaneous Options"] = T(mo, ["Yes", "Yes", "Yes", "Yes", "No"]),
                ["Database Table Named Set Definitions"] = T(ts, ["Test", "Program Control", "Dead", "~LLRF", "Comb1", "g"], ["Test", "", "Live", "Ultimate", "", ""], ["Test", "", "~LLRF", "", "", ""]),
                ["Load Pattern Definitions"] = T(["Name", "Type", "SelfWtMult"], ["Dead", "Dead", "1"], ["~LLRF", "Live", "0"]),
            };
            var after = new Dictionary<string, TableData>
            {
                ["Miscellaneous Options"] = T(mo, ["Yes", "Yes", "Yes", "No", "No"]),
                ["Database Table Named Set Definitions"] = T(ts, ["Test", "Program Control", "Dead", "Ultimate", "Comb1", "h"], ["Test", "", "Live", "", "", ""]),
                ["Load Pattern Definitions"] = T(["Name", "Type", "SelfWtMult"], ["Dead", "Dead", "1"]),
            };
            var c = ProgramCarry.Measure(CsiProgram.Etabs, before, after)!;
            Check(c.Keys["Miscellaneous Options"] == "", "a one-row table has no key: " + c.Keys["Miscellaneous Options"]);
            var row = c.Rows["Miscellaneous Options"].Single();
            Check(row.Count == 1 && row["BoundPlane"] == "Yes", "only the changed option: " + string.Join(",", row));
            Check(!c.Rows.ContainsKey("Database Table Named Set Definitions"), "the same table set, packed otherwise, is not carried");
            Check(!c.Rows.ContainsKey("Load Pattern Definitions"), "ETABS's own ~LLRF pattern is not carried");
        });

        yield return ("ETABS table put (0.4.73 saved-4, Ex002): a table ETABS edits by unique name takes a row given by story and label once, under the object's unique name", () =>
        {
            var (link, api, _) = CsiToolsTests.Model(CsiProgram.Etabs);
            const string key = "Concrete Slab Design Overwrites - Finite Element Based";
            api.Tables[key] = (["UniqueName", "RebarMat", "LLRF", "DoDesign"], [["9", "DefaultRebar_500", "1", "Yes"]]);
            EditableTables.Put(link, key, "Story|Label", [new Dictionary<string, string> { ["Story"] = "Story3", ["Label"] = "F1", ["RebarMat"] = "" }], "put back what the text file does not carry");
            var (fields, rows) = api.Tables[key];
            Check(rows.Count == 1, "one row, not the program's and a second with no unique name: " + string.Join(" / ", rows.Select(r => string.Join(",", r))));
            Check(!fields.Contains("Story") && !fields.Contains("Label"), "no Story or Label columns sent: " + string.Join(",", fields));
            Check(rows[0][Array.IndexOf(fields, "UniqueName")] == "9" && rows[0][Array.IndexOf(fields, "RebarMat")] == "" && rows[0][Array.IndexOf(fields, "DoDesign")] == "Yes", "the slab's own row, its material blank: " + string.Join(",", rows[0]));
        });

        yield return ("ETABS table put (0.4.73 saved-4): a field the given rows make blank goes in blank, not left out for ETABS's default (slab overwrite rebar material)", () =>
        {
            var (link, api, _) = CsiToolsTests.Model(CsiProgram.Etabs);
            const string key = "Concrete Slab Design Overwrites - Finite Element Based";
            api.Tables[key] = (["Story", "Label", "RebarMat", "Design"], [["Story1", "F1", "DefaultRebar_500", "Yes"]]);
            EditableTables.Put(link, key, "Story|Label", [new Dictionary<string, string> { ["Story"] = "Story1", ["Label"] = "F1", ["RebarMat"] = "" }], "put back what the text file does not carry");
            var (fields, rows) = api.Tables[key];
            Check(fields.Contains("RebarMat") && rows.Single()[Array.IndexOf(fields, "RebarMat")] == "", "the rebar material is blank again: " + string.Join(",", fields) + " / " + string.Join(",", rows.Single()));
            Check(rows.Single()[Array.IndexOf(fields, "Design")] == "Yes", "the other fields kept");
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
