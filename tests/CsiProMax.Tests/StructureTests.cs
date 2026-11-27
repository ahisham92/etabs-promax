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

        yield return ("structure: Group Definition's uses, Section Cut Data's result type, location and axes, a multi-story spandrel and Change Name are kept and written (Ahmed 2026-11-27, every field)", () =>
        {
            var link = Frame();
            Run(link, "define_group", new { name = "G-CORE", color = "Blue", uses = new { Steel = false, Concrete = true, Composite = false, AutoSeismic = true } });
            Run(link, "assign_group_data", new { group = "G-CORE", objects = Floors(link.Model).Take(2).ToArray() });
            Run(link, "define_section_cut", new { name = "SC1", group = "G-CORE", result_type = "Design", user_location = true, x = 1.5, y = 2, z = 3.2, rot_z = 90 });
            var row = StructureExtras.Get(link.Model, StructureExtras.GroupTable, "G-CORE");
            Check(row["Steel"] == "No" && row["Concrete"] == "Yes" && row["AutoSeismic"] == "Yes", "the group's uses: " + string.Join(",", row));
            Check(!StructureExtras.ForEtabs(StructureExtras.GroupTable, row).ContainsKey("AutoSeismic"), "a use only SAP2000 has stays out of the program's row");
            var cut = StructureExtras.Get(link.Model, StructureExtras.CutTable, "SC1");
            Check(cut["ResultType"] == "Design" && cut["ResultLoc"] == "User" && cut["LocX"] == "1.5" && cut["RotAboutZ"] == "90", "the cut's options: " + string.Join(",", cut));
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("GroupName=G-CORE") && s2k.Contains("Steel=No") && s2k.Contains("AutoSeismic=Yes"), "the .s2k group has its uses");
            Check(s2k.Contains("CutName=SC1") && s2k.Contains("ResultType=Design") && s2k.Contains("DefaultLoc=No") && s2k.Contains("AngleA=90"), "the .s2k has the section cut");
            var defs = Run(link, "structure_definitions", new { });
            Check(defs["groups"]![0]!["uses"]!["Concrete"]!.GetValue<bool>() && defs["section_cuts"]![0]!["options"]!["ResultType"]!.GetValue<string>() == "Design", "structure_definitions: " + defs.ToJsonString());
            Run(link, "define_pier_spandrel", new { kind = "spandrel", name = "S1", multistory = true });
            Run(link, "assign_pier_spandrel_data", new { objects = Beams(link.Model).Take(1).ToArray(), spandrel = "S1" });
            Run(link, "define_pier_spandrel", new { kind = "spandrel", name = "S2", was = "S1" });
            Check(StructureExtras.Yes(StructureExtras.Get(link.Model, StructureExtras.SpandrelTable, "S2"), "MultiStory", false), "the renamed spandrel stays multi-story");
            Check(E2kWriter.Write(link.Model).Contains("SPANDREL  \"S2\"") && !E2kWriter.Write(link.Model).Contains("\"S1\""), "Change Name renames the label and what it labels");
            Run(link, "define_section_cut", new { name = "SC1", delete = true });
            Check(StructureExtras.Get(link.Model, StructureExtras.CutTable, "SC1").Count == 0, "a deleted cut leaves no row");
        });

        yield return ("structure: Story Data's master, similar, splice and colour and Grid System Data's visibility, bubble and system read back (Ahmed 2026-11-27, every field)", () =>
        {
            var link = Frame();
            var st = link.Model.Stories.Skip(1).Select(x => x.Name).ToList();
            Run(link, "define_stories", new { stories = new object[] {
                new { name = st[0], height = 3.2, was = st[0], master = false, similar_to = st[2], splice = true, splice_height = 1.1, color = "#ff8000" },
                new { name = st[1], height = 3.2, was = st[1], master = false, similar_to = st[2] },
                new { name = st[2], height = 3.2, was = st[2], master = true } } });
            var back = E2kReader.Read(E2kWriter.Write(link.Model));
            Check(StoryGridOptions.IsMaster(back.Stories[3]) && StoryGridOptions.SimilarTo(back.Stories[1]) == st[2], "master and similar read back from the .e2k");
            var sp = StoryGridOptions.Splice(link.Model, st[0]);
            Check(sp.Splice && sp.Height == 1.1 && sp.Color == "#ff8000", "splice and colour kept as the program's table row");
            var grids = link.Model.Grids.Select(g => new { label = g.Label, direction = g.Direction, coordinate = g.Coordinate, visible = g.Label != link.Model.Grids[0].Label, bubble = "Start" }).ToArray();
            Run(link, "define_grids", new { grids, system_name = "G2", bubble_size = 1.5 });
            var g2 = E2kReader.Read(E2kWriter.Write(link.Model));
            Check(g2.GridSystem == "G2" && !StoryGridOptions.Visible(g2.Grids[0]) && StoryGridOptions.Bubble(g2.Grids[1]) == "Start" && StoryGridOptions.BubbleSize(g2) == 1.5, "grid options read back");
            var defs = Run(link, "model_definitions", new { });
            Check(defs["stories"]![2]!["master"]!.GetValue<bool>() && defs["grids"]![0]!["visible"]!.GetValue<bool>() == false && defs["grid_system"]!["name"]!.GetValue<string>() == "G2", "the forms see them: " + defs["grid_system"]!.ToJsonString());
        });
    }
}
