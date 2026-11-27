using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// SAP2000's cables, tendons, solids and links (SapOtherObjects): read from a .s2k as SAP2000's own tables, listed,
/// assigned through the Assign > Cable, Tendon, Solid and Link/Support forms' tool in place, reversed and switched, and
/// written back with every other row and field as the file had it.
/// </summary>
static class SapOtherObjectsTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    /// <summary>The sample frame with a cable, a tendon, a solid and a link on its joints, and a few of their assignments.</summary>
    static string WithOthers()
    {
        var add = string.Join("\r\n", [
            "TABLE:  \"CONNECTIVITY - CABLE\"",
            "   Cable=C1   JointI=1   JointJ=2   Length=3.5   GUID=c-1",
            "   Cable=C2   JointI=3   JointJ=4   Length=3.5   GUID=c-2",
            "",
            "TABLE:  \"CONNECTIVITY - TENDON\"",
            "   Tendon=T1   JointI=1   JointJ=3   Length=5   GUID=t-1",
            "",
            "TABLE:  \"CONNECTIVITY - SOLID\"",
            "   Solid=S1   Joint1=1   Joint2=2   Joint3=3   Joint4=4   Joint5=5   Joint6=6   Joint7=7   Joint8=8   GUID=s-1",
            "",
            "TABLE:  \"CONNECTIVITY - LINK\"",
            "   Link=L1   JointI=2   JointJ=4   GUID=l-1",
            "",
            "TABLE:  \"CABLE SECTION ASSIGNMENTS\"",
            "   Cable=C1   CableSect=CAB1   MatProp=Default   GUID=cs-1",
            "   Cable=C2   CableSect=CAB1   MatProp=Default   GUID=cs-2",
            "",
            "TABLE:  \"SOLID SPRING ASSIGNMENTS\"",
            "   Solid=S1   Type=Simple   Stiffness=10   SimpleType=\"Tension And Compression\"   Face=1   Dir1Type=\"Normal To Face\"   NormalDir=Inward",
            "",
            ""]);
        var text = Sample("sap2000-test-frame.s2k").Replace("\r", "");
        var at = text.IndexOf("END TABLE DATA", StringComparison.Ordinal);
        return (text[..at] + add.Replace("\r", "") + text[at..]).Replace("\n", "\r\n");
    }

    static JsonNode Run(OfflineEtabs link, string tool, object args) =>
        PropertyTools.All.Single(x => x.Name == tool).Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static List<Dictionary<string, string>> Rows(string text, string table) =>
        CsiTableText.Parse(text).FirstOrDefault(t => t.Key.Equals(table, StringComparison.OrdinalIgnoreCase)).Value ?? [];

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("sap2000 Assign > Cable, Tendon, Solid, Link/Support: the file's objects are listed with their joints and rows, and an unchanged file writes back the same", () =>
        {
            var text = WithOthers();
            var m = S2kReader.Read(text, "Others").Model;
            var all = SapOtherObjects.List(m);
            Check(all.Count(o => o.Kind == "cable") == 2 && all.Exists(o => o.Kind == "tendon" && o.Name == "T1") && all.Exists(o => o.Kind == "link" && o.Name == "L1"), "the objects: " + string.Join(", ", all.Select(o => o.Kind + " " + o.Name)));
            var solid = all.Single(o => o.Kind == "solid");
            Check(solid.Joints.Count == 8 && solid.Points.Count == 8, "the solid has its 8 corners");
            Check(SapOtherObjects.RowsOf(m, "cable", "C1")["Cable Section Assignments"][0]["CableSect"] == "CAB1", "C1's section row");
            var back = S2kWriter.Write(m).Text;
            foreach (var t in new[] { "CONNECTIVITY - CABLE", "CONNECTIVITY - TENDON", "CONNECTIVITY - SOLID", "CONNECTIVITY - LINK", "CABLE SECTION ASSIGNMENTS", "SOLID SPRING ASSIGNMENTS" })
            {
                var a = Rows(text, t); var b = Rows(back, t);
                Check(a.Count == b.Count && a.Zip(b).All(p => p.First.Count == p.Second.Count && p.First.All(kv => p.Second.GetValueOrDefault(kv.Key) == kv.Value)), $"{t} came back different");
            }
            var link = new OfflineEtabs(m);
            var data = Run(link, "sap_object_data", new { kind = "cable", objects = new[] { "C1" } });
            Check(data["objects_by_kind"]!["solid"]!.AsArray().Count == 1 && data["objects"]![0]!["tables"]!["Cable Section Assignments"] is not null, "sap_object_data: " + data.ToJsonString()[..Math.Min(300, data.ToJsonString().Length)]);
        });

        yield return ("sap2000 Assign > Cable and Link/Support: rows set in place (order, GUID and other fields kept), new tables in SAP2000's frame shape, Reverse Connectivity swaps the ends", () =>
        {
            var m = S2kReader.Read(WithOthers(), "Others").Model;
            var link = new OfflineEtabs(m);
            Run(link, "assign_sap_object_data", new { kind = "cable", objects = new[] { "C1" }, tables = new Dictionary<string, object>
            {
                ["Cable Section Assignments"] = new[] { new { CableSect = "CAB2" } },
                ["Cable Property Modifiers"] = new[] { new { AMod = "0.5", MassMod = "1", WeightMod = "1" } },
            } });
            var text = S2kWriter.Write(link.Model).Text;
            var sect = Rows(text, "CABLE SECTION ASSIGNMENTS");
            Check(sect.Count == 2 && sect[0]["Cable"] == "C1" && sect[0]["CableSect"] == "CAB2" && sect[0].GetValueOrDefault("GUID") == "cs-1" && sect[1]["CableSect"] == "CAB1", "the section row changed in place: " + string.Join(" | ", sect.Select(r => string.Join(" ", r.Select(kv => kv.Key + "=" + kv.Value)))));
            var mods = Rows(text, "CABLE PROPERTY MODIFIERS");
            Check(mods.Count == 1 && mods[0]["Frame"] == "C1" && mods[0]["Cable"] == "Yes" && mods[0]["AMod"] == "0.5", "the modifiers in SAP2000's frame shape: " + string.Join(" ", mods.SelectMany(r => r.Select(kv => kv.Key + "=" + kv.Value))));
            var back = S2kReader.Read(text, "Back").Model;
            Check(SapOtherObjects.RowsOf(back, "cable", "C1")["Cable Property Modifiers"][0]["AMod"] == "0.5", "the modifiers came back on C1");
            Check(!SapOtherObjects.RowsOf(back, "cable", "C2").ContainsKey("Cable Property Modifiers"), "C2 has no modifiers");
            Run(link, "reverse_objects", new { kind = "cable", objects = new[] { "C1" } });
            Run(link, "reverse_objects", new { kind = "link", objects = new[] { "L1" } });
            text = S2kWriter.Write(link.Model).Text;
            var c = Rows(text, "CONNECTIVITY - CABLE");
            Check(c[0]["JointI"] == "2" && c[0]["JointJ"] == "1" && c[0]["GUID"] == "c-1" && c[1]["JointI"] == "3", "C1's ends swapped, C2's stayed");
            Check(Rows(text, "CONNECTIVITY - LINK")[0]["JointI"] == "4", "L1's ends swapped");
            // Taking a table off: [] removes the object's rows and only its.
            Run(link, "assign_sap_object_data", new { kind = "cable", objects = new[] { "C1" }, tables = new Dictionary<string, object> { ["Cable Property Modifiers"] = Array.Empty<object>() } });
            Check(Rows(S2kWriter.Write(link.Model).Text, "CABLE PROPERTY MODIFIERS").Count == 0, "the modifiers came off");
        });

        yield return ("sap2000 Assign > Solid: Switch Faces renumbers the corners so the two faces change places (twice gives them back) and the face springs follow; wrong tables and fields are refused", () =>
        {
            var m = S2kReader.Read(WithOthers(), "Others").Model;
            var link = new OfflineEtabs(m);
            var before = SapOtherObjects.JointsOf("solid", SapOtherObjects.Kept(m, "CONNECTIVITY - SOLID")[0]);
            Run(link, "switch_solid_faces", new { objects = new[] { "S1" }, face = 1, with = 3 });
            var row = SapOtherObjects.Kept(link.Model, "CONNECTIVITY - SOLID")[0];
            var after = SapOtherObjects.JointsOf("solid", row);
            Check(after.Count == 8 && after.Distinct().Count() == 8 && !after.SequenceEqual(before), "the corners were renumbered: " + string.Join(",", after));
            // Face 1's corners now hold the joints face 3 had.
            Check(SapOtherObjects.Faces[0].Select(c => after[c - 1]).OrderBy(x => x).SequenceEqual(SapOtherObjects.Faces[2].Select(c => before[c - 1]).OrderBy(x => x)), "face 1 now has face 3's joints");
            Check(SapOtherObjects.Kept(link.Model, "Solid Spring Assignments")[0]["Face"] == "3", "the spring moved with its face");
            Run(link, "switch_solid_faces", new { objects = new[] { "S1" }, face = 1, with = 3 });
            Check(SapOtherObjects.JointsOf("solid", SapOtherObjects.Kept(link.Model, "CONNECTIVITY - SOLID")[0]).SequenceEqual(before), "switching twice gives the corners back");
            Check(SapOtherObjects.Kept(link.Model, "Solid Spring Assignments")[0]["Face"] == "1", "and the spring back on face 1");
            foreach (var (args, says) in new (object, string)[]
            {
                (new { kind = "solid", objects = new[] { "S1" }, tables = new Dictionary<string, object> { ["Cable Section Assignments"] = new[] { new { CableSect = "X" } } } }, "not one of SAP2000's solid"),
                (new { kind = "solid", objects = new[] { "S1" }, tables = new Dictionary<string, object> { ["Solid Property Assignments"] = new[] { new { Nope = "X" } } } }, "no field"),
                (new { kind = "solid", objects = new[] { "S9" }, tables = new Dictionary<string, object> { ["Solid Property Assignments"] = new[] { new { SolidProp = "X" } } } }, "There is no solid"),
            })
            {
                try { Run(link, "assign_sap_object_data", args); throw new Exception("took " + JsonSerializer.Serialize(args)); }
                catch (Exception ex) when (ex is ToolInputException or ArgumentException or CsiProMax.Assistant.Etabs.EtabsException) { Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), $"'{says}' expected in: {ex.Message}"); }
            }
        });
    }
}
