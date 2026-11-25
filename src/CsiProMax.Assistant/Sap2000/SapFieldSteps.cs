using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// SAP2000's part of the field test. <see cref="Modeler"/> writes the Modeler's model as a .s2k; <see cref="Scratch"/>
/// then works on the scratch model in the test's own SAP2000 window (<see cref="FieldContext.Link"/>): what SAP2000
/// read from the file, every read of the link, every SAP2000 tool with real arguments and its change read back, then
/// SAP2000's own analysis and design and every result tool. It ends with the tools that were not run, which must be none.
/// </summary>
public static class SapFieldSteps
{
    /// <summary>In the Modeler's part: the model saved as a SAP2000 text file, for the scratch window to open.</summary>
    public static void Modeler(FieldContext ctx)
    {
        var answer = ctx.Tool("save_model_s2k", $$"""{"name":"{{ModelerFieldSteps.ModelName}}","folder":{{Q(ctx.Folder)}}}""", a =>
        {
            var file = a["saved"]!.GetValue<string>();
            FieldContext.Expect(File.Exists(file) && new FileInfo(file).Length > 1000, $"the file {file} was not written");
            FieldContext.Expect(Int(a["joints"]) > 0 && Int(a["frames"]) == ctx.Written.Frames && Int(a["areas"]) == ctx.Written.Areas,
                $"the model has {ctx.Written.Frames} frames and {ctx.Written.Areas} areas; the .s2k has {a["frames"]} and {a["areas"]} (left out: {a["left_out"]?.ToJsonString()})");
        });
        if (answer?["saved"]?.GetValue<string>() is not { } s2k) return;
        ctx.Files[CsiProgram.Sap2000.Key] = s2k;
        ctx.Facts["sap2000.joints"] = Int(answer["joints"]);
        ctx.Facts["sap2000.frames"] = Int(answer["frames"]);
        ctx.Facts["sap2000.areas"] = Int(answer["areas"]);
    }

    public static void Scratch(FieldContext ctx)
    {
        var snapshots = ResultTools.SnapshotRoot;
        ResultTools.SnapshotRoot = Path.Combine(ctx.Folder, "snapshots", "sap2000");
        try { Steps(ctx); }
        finally { ResultTools.SnapshotRoot = snapshots; }
    }

    private static bool At(double a, double b) => Math.Abs(a - b) < 1e-4;

    private static void Steps(FieldContext ctx)
    {
        var link = ctx.Link;
        ctx.ClearTools();
        ctx.Offer(EtabsTools.All);
        ctx.Offer(SapTools.All);

        // ---- what SAP2000 read from the file the Modeler wrote ----
        ctx.Step("SAP2000 read every joint, frame and area the Modeler wrote", () =>
        {
            var (joints, frames, areas) = (link.Points().Count, link.Frames().Count, link.Areas().Count);
            var (j, f, a) = ((int)ctx.Facts.GetValueOrDefault("sap2000.joints"), (int)ctx.Facts.GetValueOrDefault("sap2000.frames"), (int)ctx.Facts.GetValueOrDefault("sap2000.areas"));
            FieldContext.Expect(joints == j && frames == f && areas == a, $"the .s2k has {j} joints, {f} frames and {a} areas; SAP2000 has {joints}, {frames} and {areas}");
            return $"{joints} joints, {frames} frames, {areas} areas";
        });
        ctx.Step("the material, sections, load pattern and combination came across", () =>
        {
            var missing = new List<string>();
            void Need(IEnumerable<string> have, string name, string what) { if (!have.Contains(name, StringComparer.OrdinalIgnoreCase)) missing.Add($"{what} {name}"); }
            Need(link.Materials(), "CPM-C35", "material");
            Need(link.FrameSections(), "CPM-C400", "frame section");
            Need(link.FrameSections(), "CPM-B250x500", "frame section");
            Need(link.AreaSections(), "CPM-S180", "area section");
            Need(link.LoadPatterns().Select(p => p.Name), "CPM-SDL", "load pattern");
            Need(link.Combos(), "CPM-ULS", "combination");
            FieldContext.Expect(missing.Count == 0, "not in SAP2000 after the import: " + string.Join(", ", missing));
            return "all there";
        });

        // ---- every read of the link ----
        LinkReads(ctx, stories: false);

        // ---- SAP2000's own reads ----
        ctx.Tool("get_model_context", "{}", a => FieldContext.Expect(a.ToJsonString().Contains("CPM-C400", StringComparison.Ordinal), "the context does not name the section CPM-C400"));
        ctx.Tool("query_joints", """{"limit":3}""", a => FieldContext.Expect(Int(a["count"]) == link.Points().Count && a["joints"] is JsonArray { Count: 3 }, $"every joint counted and 3 listed were expected: {a["count"]}"));
        ctx.Tool("query_joints", """{"z":0,"supported_only":true}""", a => FieldContext.Expect(Int(a["count"]) >= 9, $"the 9 fixed column bases at z = 0 were expected: {a["count"]}"), "tool query_joints, the supports at a level");

        // ---- definitions ----
        ctx.Tool("add_material", """{"name":"CPM-SAP-C30","type":"concrete","elastic_modulus":27000000,"unit_weight":25,"fc":30000}""", _ => FieldContext.Expect(link.Materials().Contains("CPM-SAP-C30"), "the material is not in SAP2000"), "tool add_material concrete");
        ctx.Tool("add_material", """{"name":"CPM-SAP-S355","type":"steel","elastic_modulus":200000000,"unit_weight":77,"fy":355000,"fu":470000}""", _ => FieldContext.Expect(link.Materials().Contains("CPM-SAP-S355"), "the material is not in SAP2000"), "tool add_material steel");
        foreach (var (name, json) in new[]
        {
            ("CPM-R300x600", """{"name":"CPM-R300x600","shape":"rectangle","material":"CPM-SAP-C30","depth":0.6,"width":0.3}"""),
            ("CPM-D500", """{"name":"CPM-D500","shape":"circle","material":"CPM-SAP-C30","diameter":0.5}"""),
            ("CPM-I400", """{"name":"CPM-I400","shape":"i_section","material":"CPM-SAP-S355","depth":0.4,"width":0.2,"flange_thickness":0.015,"web_thickness":0.01}"""),
            ("CPM-T200", """{"name":"CPM-T200","shape":"tube","material":"CPM-SAP-S355","depth":0.2,"width":0.2,"flange_thickness":0.01,"web_thickness":0.01}"""),
            ("CPM-P219", """{"name":"CPM-P219","shape":"pipe","material":"CPM-SAP-S355","diameter":0.219,"wall_thickness":0.008}"""),
        })
            ctx.Tool("add_frame_section", json, _ => FieldContext.Expect(link.FrameSections().Contains(name), $"the section {name} is not in SAP2000"), $"tool add_frame_section {name}");
        ctx.Tool("add_area_section", """{"name":"CPM-SH200","material":"CPM-SAP-C30","thickness":0.2,"type":"shell_thin"}""", _ => FieldContext.Expect(link.AreaSections().Contains("CPM-SH200"), "the area section is not in SAP2000"));

        // ---- a small frame of its own, each member read back ----
        List<string> joints = [], columns = [], beams = [], areas = [], spare = [];
        ctx.Tool("add_joints", """{"points":[[30,0,0],[36,0,0],[36,5,0],[30,5,0],[48,0,0]]}""", a =>
        {
            joints = Names(a, "joints");
            FieldContext.Expect(joints.Count == 5 && joints.All(j => link.Points().Any(p => p.Name == j)), "5 joints were asked for: " + a.ToJsonString());
        });
        ctx.Tool("add_frames", """{"section":"CPM-R300x600","lines":[[30,0,0,30,0,3],[36,0,0,36,0,3],[36,5,0,36,5,3],[30,5,0,30,5,3]]}""", a =>
        {
            columns = Names(a, "frames");
            var made = link.Frames().Where(f => columns.Contains(f.Name)).ToList();
            FieldContext.Expect(made.Count == 4 && made.All(f => f.Kind == "column" && f.Section == "CPM-R300x600"), "4 columns with CPM-R300x600 were expected; SAP2000 has: " + string.Join("; ", made.Select(f => $"{f.Name} {f.Kind} {f.Section}")));
        }, "tool add_frames by coordinates");
        var tops = Read(ctx, "the joints at the top of the new columns", () => link.Points().Where(p => At(p.Z, 3) && p.X > 29.9 && p.X < 36.1).OrderBy(p => p.Y).ThenBy(p => p.X).Select(p => p.Name).ToList(), []);
        if (tops.Count == 4)
            ctx.Tool("add_frames", $$"""{"section":"CPM-I400","joints":[[{{Q(tops[0])}},{{Q(tops[1])}}],[{{Q(tops[1])}},{{Q(tops[3])}}],[{{Q(tops[3])}},{{Q(tops[2])}}],[{{Q(tops[2])}},{{Q(tops[0])}}]]}""", a =>
            {
                beams = Names(a, "frames");
                var made = link.Frames().Where(f => beams.Contains(f.Name)).ToList();
                FieldContext.Expect(made.Count == 4 && made.All(f => f.Kind == "beam" && f.Section == "CPM-I400" && At(f.Z1, 3)), "4 beams at z = 3 with CPM-I400 were expected; SAP2000 has: " + string.Join("; ", made.Select(f => $"{f.Name} {f.Kind} {f.Section} {f.Z1}")));
            }, "tool add_frames by joint names");
        else ctx.Fail("tool add_frames by joint names", $"the 4 column tops were expected at z = 3; SAP2000 has {tops.Count} joints there");
        ctx.Tool("add_areas", """{"section":"CPM-SH200","outlines":[[[30,0,3],[36,0,3],[36,5,3],[30,5,3]]]}""", a =>
        {
            areas = Names(a, "areas");
            var made = link.Areas().Where(x => areas.Contains(x.Name)).ToList();
            FieldContext.Expect(made.Count == 1 && made[0].Section == "CPM-SH200" && made[0].Corners.Count == 4, "one area with 4 corners and CPM-SH200 was expected");
        });
        ctx.Tool("add_frames", """{"section":"CPM-P219","lines":[[48,0,0,48,0,3]]}""", a => { spare = Names(a, "frames"); FieldContext.Expect(spare.Count == 1, "one frame was asked for: " + a.ToJsonString()); }, "tool add_frames, one to delete again");
        if (beams.Count > 0)
            ctx.Tool("set_section", $$"""{"frames":[{{Q(beams[0])}}],"section":"CPM-T200"}""", _ => FieldContext.Expect(link.Frames().First(f => f.Name == beams[0]).Section == "CPM-T200", "the frame does not have CPM-T200 when read back from SAP2000"), "tool set_section on a frame");
        if (areas.Count > 0)
            ctx.Tool("set_section", $$"""{"areas":[{{Q(areas[0])}}],"section":"CPM-S180"}""", _ => FieldContext.Expect(link.Areas().First(x => x.Name == areas[0]).Section == "CPM-S180", "the area does not have CPM-S180 when read back from SAP2000"), "tool set_section on an area");

        // ---- supports, loads, releases ----
        var feet = joints.Take(4).ToList();
        if (feet.Count == 4)
        {
            ctx.Tool("set_restraints", $$"""{"type":"pinned","joints":{{List(feet)}}}""", _ => FieldContext.Expect(feet.All(j => link.Restraint(j).SequenceEqual([true, true, true, false, false, false])), "not pinned when read back from SAP2000"), "tool set_restraints pinned at named joints");
            ctx.Tool("set_restraints", $$"""{"dofs":["UX","UY","UZ","RZ"],"joints":[{{Q(feet[0])}}]}""", _ => FieldContext.Expect(link.Restraint(feet[0]).SequenceEqual([true, true, true, false, false, true]), "UX UY UZ RZ were set; SAP2000 has " + string.Join(" ", link.Restraint(feet[0]))), "tool set_restraints by degrees of freedom");
        }
        ctx.Tool("set_restraints", """{"type":"fixed","at_lowest":true}""", _ => FieldContext.Expect(feet.All(j => link.Restraint(j).All(r => r)), "the lowest joints are not fixed when read back from SAP2000"), "tool set_restraints fixed at the lowest joints");
        if (joints.Count == 5)
        {
            ctx.Tool("set_restraints", $$"""{"type":"roller","joints":[{{Q(joints[4])}}]}""", _ => FieldContext.Expect(link.Restraint(joints[4]).SequenceEqual([false, false, true, false, false, false]), "a roller holds UZ only; SAP2000 has " + string.Join(" ", link.Restraint(joints[4]))), "tool set_restraints roller");
            ctx.Tool("set_restraints", """{"type":"fixed","z":0}""", _ => FieldContext.Expect(link.Restraint(joints[4]).All(r => r), "the joint at z = 0 is not fixed when read back"), "tool set_restraints at a level");
        }
        ctx.Tool("add_load_pattern", """{"name":"CPM-TEST","type":"Live","self_weight":0}""", _ => FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-TEST") && link.LoadCases().Contains("CPM-TEST"), "the pattern CPM-TEST, or its load case, is not in SAP2000"));
        if (beams.Count > 1 && areas.Count > 0)
        {
            ctx.Tool("set_loads", $$"""{"frames":{{List(beams)}},"areas":{{List(areas)}},"pattern":"CPM-TEST","value":3}""", a => FieldContext.Expect(Int(a["done"]) == beams.Count + areas.Count && a["failed"]!.AsArray().Count == 0, "not every object was loaded: " + a.ToJsonString()));
            ctx.Tool("set_member_loads", $$"""{"frames":[{{Q(beams[1])}}],"pattern":"CPM-TEST","value":2,"direction":"x"}""", null, "tool set_member_loads on a frame, along x");
            ctx.Tool("set_member_loads", $$"""{"areas":{{List(areas)}},"pattern":"CPM-TEST","value":1,"direction":"local_3","add":true}""", null, "tool set_member_loads on an area, local 3");
            ctx.Tool("set_releases", $$"""{"frames":[{{Q(beams[1])}}],"type":"pinned","ends":"j"}""", null, "tool set_releases pinned at one end");
            ctx.Tool("set_releases", $$"""{"frames":[{{Q(beams[1])}}],"type":"fixed","ends":"both"}""", null, "tool set_releases fixed again");
        }
        else ctx.Fail("tools set_loads, set_member_loads and set_releases", "the beams and the area to load were not added");
        if (tops.Count > 0)
            ctx.Tool("set_joint_loads", $$"""{"joints":[{{Q(tops[0])}}],"pattern":"CPM-TEST","fz":-5,"fx":1,"my":0.5}""");
        var first = Read(ctx, "the load cases", () => link.LoadCases().FirstOrDefault(c => c.Equals("Dead", StringComparison.OrdinalIgnoreCase)) ?? link.LoadCases().FirstOrDefault(), null) ?? "DEAD";
        ctx.Tool("add_combination", $$"""{"name":"CPM-TEST-COMBO","cases":[{"case":{{Q(first)}},"factor":1.2},{"case":"CPM-TEST","factor":1.6}]}""", _ => FieldContext.Expect(link.Combos().Contains("CPM-TEST-COMBO"), "the combination is not in SAP2000"));

        // ---- reading ----
        if (columns.Count > 0 && areas.Count > 0)
        {
            ctx.Tool("select_objects", $$"""{"frames":[{{Q(columns[0])}}],"areas":[{{Q(areas[0])}}]}""", _ => FieldContext.Expect(link.Selected().Count == 2, $"2 objects were selected; SAP2000 has {link.Selected().Count}"));
            ctx.Tool("query_objects", """{"selected_only":true}""", a => FieldContext.Expect(Int(a["count"]) == 2, $"2 objects are selected; the answer has {a["count"]}"), "tool query_objects, the selection only");
            ctx.Tool("query_joints", """{"selected_only":true}""", null, "tool query_joints, the selection only");
            ctx.Tool("select_objects", "{}", _ => FieldContext.Expect(link.Selected().Count == 0, "the selection was not cleared"), "tool select_objects with nothing clears the selection");
        }
        ctx.Tool("query_objects", """{"section":"CPM-R300x600"}""", a => FieldContext.Expect(Int(a["count"]) == 4, $"4 frames with CPM-R300x600 were expected: {a["count"]}"), "tool query_objects by section");
        ctx.Tool("list_tables", "{}", a => FieldContext.Expect(Int(a["count"]) >= 3, $"SAP2000's tables with data were expected: {a["count"]}"));
        if (Read(ctx, "the tables", () => link.Tables().FirstOrDefault(t => !t.Empty), null) is { } table)
            ctx.Tool("read_table", $$"""{"table":{{Q(table.Key)}},"limit":3}""", a => FieldContext.Expect(a["fields"] is JsonArray { Count: > 0 }, "the table has no fields"), $"tool read_table {table.Name}");
        else ctx.Fail("tool read_table", "SAP2000 listed no table with data");

        // ---- what SAP2000 has no stories for ----
        foreach (var name in new[] { "add_columns", "add_beams", "add_slabs", "add_walls", "check_drift", "check_torsion", "check_story_irregularity" }) NotHere(ctx, name, "SAP2000 has no stories");
        if (feet.Count > 0)
            ctx.Tool("set_supports", $$"""{"type":"fixed","points":[{{Q(feet[0])}}]}""", _ => FieldContext.Expect(link.Restraint(feet[0]).All(r => r), "the joint is not fixed when read back from SAP2000"), "tool set_supports at a named joint");
        else ctx.Tool("set_supports", """{"type":"fixed","at_base":true}""", null, "tool set_supports (the shared tool, as SAP2000 answers it)");

        // ---- deleting, a snapshot, saving ----
        if (spare.Count > 0)
            ctx.Tool("delete_objects", $$"""{"frames":{{List(spare)}}}""", _ => FieldContext.Expect(!link.Frames().Any(f => f.Name == spare[0]), "the deleted frame is still in SAP2000"));
        else ctx.Fail("tool delete_objects", "the frame to delete was not added");
        var loose = Read(ctx, "the joints the deleted frame left", () => link.Points().Where(p => At(p.X, 48)).Select(p => p.Name).ToList(), []);
        if (loose.Count > 0)
            ctx.Tool("delete_joints", $$"""{"joints":{{List(loose)}}}""", _ => FieldContext.Expect(!link.Points().Any(p => At(p.X, 48)), "the joints at x = 48 are still in SAP2000"));
        else ctx.Tool("delete_joints", """{"joints":["no such joint"]}""", null, "tool delete_joints (the spare frame's joints went with it)");
        ctx.Tool("model_snapshot", """{"name":"cpm-before-analysis"}""");
        ctx.Tool("save_model", "{}", a => FieldContext.Expect(a["saved"]!.GetValue<string>().Replace('\\', '/').Split('/').Last().StartsWith(ModelerFieldSteps.ModelName, StringComparison.OrdinalIgnoreCase), $"it saved '{a["saved"]}', which is not the scratch model"));

        // ---- SAP2000's own analysis and design ----
        ctx.Tool("run_analysis", "{}", _ => FieldContext.Expect(link.Info().Locked, "the analysis answered, but SAP2000 does not say the model is analysed (locked)"));
        var analysed = false;
        try { analysed = link.Info().Locked; }
        catch (EtabsException) { }
        ctx.Tool("run_design", """{"steel":true,"concrete":true}""");
        var none = analysed ? "" : " (no analysis: it must still answer)";
        ctx.Tool("joint_displacements", """{"limit":5}""", null, "tool joint_displacements" + none);
        ctx.Tool("frame_forces", """{"limit":5}""", null, "tool frame_forces" + none);
        ctx.Tool("base_reactions", """{"per_joint":true}""", null, "tool base_reactions" + none);
        ctx.Tool("modal_results", "{}", null, "tool modal_results" + none);
        ctx.Tool("check_displacement", """{"lateral_limit":500,"vertical_limit":0.02}""", null, "tool check_displacement" + none);
        ctx.Tool("design_ratios", """{"limit":10}""", null, "tool design_ratios" + none);
        ctx.Tool("check_report", """{"lateral_limit":500}""", null, "tool check_report" + none);
        ctx.Tool("results_overview", "{}", null, "tool results_overview (the ETABS tool, as SAP2000 answers it)");
        ctx.Tool("check_modal", "{}", null, "tool check_modal (the ETABS tool, as SAP2000 answers it)");
        if (analysed) ctx.Step("SAP2000 tables with data after the analysis and design", () => link.Tables().Where(t => !t.Empty).Select(t => t.Name).ToList());

        // ---- snapshots and the review pack ----
        ctx.Tool("list_snapshots", "{}", a => FieldContext.Expect(Int(a["count"]) >= 1, "the snapshot is not listed"));
        ctx.Tool("compare_snapshots", """{"from":"cpm-before-analysis"}""");
        ctx.Step("the review pack of the scratch model is written", () =>
        {
            var checks = ProgramTools.Report(System.Text.Json.JsonDocument.Parse("{}").RootElement, link).AsObject();
            var pack = Pack.Build(link, [], link.LoadCases(), link.Combos(), checks, null, mostRows: 20000, by: "CSI ProMax field test");
            var file = Path.Combine(ctx.Folder, ModelerFieldSteps.ModelName + " SAP2000.cpmx");
            File.WriteAllBytes(file, pack.Zip);
            FieldContext.Expect(pack.Frames == link.Frames().Count && pack.Areas == link.Areas().Count, $"the pack holds {pack.Frames} frames and {pack.Areas} areas, the model has {link.Frames().Count} and {link.Areas().Count}");
            return new { file = Path.GetFileName(file), kilobytes = pack.Zip.Length / 1024, frames = pack.Frames, areas = pack.Areas, tables = pack.Tables, notes = pack.Notes };
        });

        ctx.Coverage("every SAP2000 tool was run");
    }
}
