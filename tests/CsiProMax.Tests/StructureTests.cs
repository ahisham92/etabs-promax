using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>The Modeler's diaphragms, pier and spandrel labels, groups and section cuts (StructureTools).</summary>
static class StructureTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Structure", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args) =>
        (JsonObject)StructureTools.All.Single(x => x.Name == tool).Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static void Refused(OfflineEtabs link, string tool, object args, string says)
    {
        var before = ModelFile.ToJson(link.Model);
        try { Run(link, tool, args); }
        catch (ToolInputException ex)
        {
            Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), $"{tool}: '{says}' expected in: {ex.Message}");
            Check(ModelFile.ToJson(link.Model) == before, $"{tool}: a refusal changed the model");
            return;
        }
        throw new Exception($"{tool} took {JsonSerializer.Serialize(args)}");
    }

    static StructuralModel ViaE2k(StructuralModel m) => E2kReader.Read(E2kWriter.Write(m));
    static List<string> Floors(StructuralModel m) => m.AreaAssigns.Where(x => m.Area(x.Label)!.Kind == AreaKind.Floor).Select(x => x.Label + "@" + x.Story).ToList();
    static List<string> Beams(StructuralModel m) => m.LineAssigns.Where(x => m.Line(x.Label)!.Kind == LineKind.Beam).Select(x => x.Label + "@" + x.Story).ToList();

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("structure: a semi-rigid diaphragm goes on floors and joints, comes back from the .e2k, and cannot be deleted while used", () =>
        {
            var link = Frame();
            Run(link, "define_diaphragm", new { name = "D2", semi_rigid = true });
            var floor = Floors(link.Model)[0];
            Run(link, "assign_diaphragm_data", new { objects = new[] { floor }, diaphragm = "D2" });
            var col = link.Model.LineAssigns.First(x => link.Model.Line(x.Label)!.Kind == LineKind.Column);
            var joint = link.Model.Line(col.Label)!.PointJ + "@" + col.Story;
            Run(link, "assign_diaphragm_data", new { objects = new[] { joint }, joints = true, diaphragm = "D2" });
            var back = ViaE2k(link.Model);
            Check(back.Diaphragms.Single(d => d.Name == "D2").Type == "SEMIRIGID", "D2 is semi-rigid in the .e2k");
            Check(back.AreaAssigns.Count(x => x.Diaphragm == "D2") == 1 && back.PointAssigns.Count(x => x.Diaphragm == "D2") == 1, "the floor and the joint have D2");
            Refused(link, "define_diaphragm", new { name = "D2", delete = true }, "take it off");
            Refused(link, "assign_diaphragm_data", new { objects = new[] { floor }, diaphragm = "D9" }, "no diaphragm");
            Refused(link, "assign_diaphragm_data", new { objects = new[] { Beams(link.Model)[0] }, diaphragm = "D2" }, "no floor");
            Run(link, "assign_diaphragm_data", new { objects = new[] { floor }, diaphragm = "" });
            Run(link, "assign_diaphragm_data", new { objects = new[] { joint }, joints = true, diaphragm = "" });
            Run(link, "define_diaphragm", new { name = "D2", delete = true });
            Check(!link.Model.Diaphragms.Exists(d => d.Name == "D2"), "deleted once unused");
        });

        yield return ("structure: pier and spandrel labels are written as ETABS writes them and read by the Modeler's wall labels", () =>
        {
            var link = Frame();
            var beam = Beams(link.Model)[0];
            Run(link, "assign_pier_spandrel_data", new { objects = new[] { beam }, spandrel = "S1" });
            Run(link, "define_pier_spandrel", new { kind = "pier", name = "P1" });
            var text = E2kWriter.Write(link.Model);
            Check(text.Contains("$ PIER/SPANDREL NAMES") && text.Contains("PIERNAME  \"P1\"") && text.Contains("SPANDRELNAME  \"S1\"") && text.Contains("SPANDREL  \"S1\""), "the labels in the .e2k");
            Check(link.WallLabels().Any(w => w.Spandrel == "S1"), "the Modeler's own wall labels see it");
            Refused(link, "define_pier_spandrel", new { kind = "spandrel", name = "S1", delete = true }, "take it off");
            Refused(link, "assign_pier_spandrel_data", new { objects = new[] { beam } }, "Give pier");
            Run(link, "define_pier_spandrel", new { kind = "pier", name = "P1", delete = true });
            var defs = Run(link, "structure_definitions", new { });
            Check(defs["piers"]!.AsArray().Count == 0 && defs["spandrels"]![0]!["used_by"]!.GetValue<int>() == 1, "structure_definitions: " + defs.ToJsonString());
        });

        yield return ("structure: groups and a section cut go into the .e2k, the group into the .s2k, and a used group cannot be deleted", () =>
        {
            var link = Frame();
            Run(link, "define_group", new { name = "G-CORE", color = "Blue" });
            var members = Floors(link.Model).Take(2).Concat(Beams(link.Model).Take(3)).ToArray();
            var j = Run(link, "assign_group_data", new { group = "G-CORE", objects = members });
            Check(j["members"]!.GetValue<int>() == 5, "5 members: " + j.ToJsonString());
            Run(link, "assign_group_data", new { group = "G-CORE", objects = members.Take(1).ToArray() });
            Check(GroupDefs.Groups(link.Model).Single().Members.Count == 5, "adding again does not double");
            Run(link, "define_section_cut", new { name = "SC1", group = "G-CORE" });
            var back = ViaE2k(link.Model);
            var g = GroupDefs.Groups(back).Single(x => x.Name == "G-CORE");
            Check(g.Members.Count == 5, "the group back from the .e2k");
            Check(!E2kWriter.Write(back).Contains("COLOR \"Blue\""), "no group COLOR in the .e2k (ETABS 23 says Line Ignored)");
            Check(E2kWriter.Write(back).Contains("SECTIONCUT  \"SC1\""), "the section cut back");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("GroupName=G-CORE"), "the .s2k has the group");
            Refused(link, "define_group", new { name = "G-CORE", delete = true }, "SC1");
            Refused(link, "define_section_cut", new { name = "SC2", group = "nope" }, "no group");
            Run(link, "define_group", new { name = "G-EMPTY" });
            Refused(link, "define_section_cut", new { name = "SC2", group = "G-EMPTY" }, "empty");
            Refused(link, "define_group", new { name = "G-X", color = "Puce" }, "color");
            Run(link, "assign_group_data", new { group = "G-CORE", objects = members.Take(2).ToArray(), remove = true });
            Check(GroupDefs.Groups(link.Model).Single(x => x.Name == "G-CORE").Members.Count == 3, "two taken out");
            Run(link, "define_section_cut", new { name = "SC1", delete = true });
            Run(link, "define_group", new { name = "G-CORE", delete = true });
            Check(!GroupDefs.Groups(link.Model).Exists(x => x.Name == "G-CORE"), "deleted with its members");
        });
    }
}
