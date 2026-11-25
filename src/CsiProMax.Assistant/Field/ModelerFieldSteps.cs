using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The Modeler's part of the field test. It needs no CSI program, so it runs on every PC: a building is modelled with
/// every Modeler tool on a model of the test's own (never the one open in a Modeler window), each change is read back,
/// and the model is written as the files the CSI programs open (.e2k, .s2k, .f2k). The scratch windows then open them.
/// </summary>
public static class ModelerFieldSteps
{
    public const string Section = "The Modeler (no CSI program needed)";

    /// <summary>The scratch model's name: its files, in every program, start with this.</summary>
    public const string ModelName = "cpm-field-test";

    public static void Run(FieldContext ctx)
    {
        ctx.Section(Section);
        var modeler = new OfflineEtabs();
        var open = ctx.Link;
        var program = ctx.Program;
        var snapshots = ResultTools.SnapshotRoot;
        ctx.Link = modeler;
        ctx.Program = CsiProgram.Etabs;
        ResultTools.SnapshotRoot = Path.Combine(ctx.Folder, "snapshots", "modeler");
        try { Steps(ctx, modeler); }
        finally
        {
            ResultTools.SnapshotRoot = snapshots;
            ctx.Link = open;
            ctx.Program = program;
        }
    }

    /// <summary>
    /// The checker on models broken on purpose, one fault each (CsiProMax.Checks.Stability.BrokenModels): each fault is
    /// found by the check that promises it, naming the object, with a fix; the sound model gives no error. The same
    /// broken models open in the Modeler from the checker's window.
    /// </summary>
    private static void Instabilities(FieldContext ctx)
    {
        foreach (var c in CsiProMax.Checks.Stability.BrokenModels.All)
            ctx.Step($"the instability checker on a broken model: {c.What}", () =>
            {
                var report = CsiProMax.Checks.Stability.StabilityCheck.Run(CsiProMax.Checks.Stability.FromStructural.Read(c.Build(), c.Program));
                var found = string.Join(" | ", report.Findings.Take(6).Select(f => $"{f.Level} {f.Check}: {f.Message}"));
                if (c.Check.Length == 0)
                {
                    FieldContext.Expect(report.Errors == 0, "the sound model has errors: " + found);
                    return "no errors, as it should";
                }
                var hit = report.Findings.Where(f => f.Check == c.Check).ToList();
                FieldContext.Expect(hit.Count > 0, $"no '{c.Check}' finding: " + found);
                FieldContext.Expect(c.Object.Length == 0 || hit.Any(f => f.Objects.Any(o => o.Name.Split('@')[0] == c.Object) || f.Message.Contains(c.Object)), $"'{c.Object}' is not named: " + found);
                FieldContext.Expect(hit.All(f => f.Fix.Length > 0), "a finding has no fix");
                return hit[0].Message;
            });
    }

    private static string Q(string text) => JsonSerializer.Serialize(text);
    private static string List(IEnumerable<string> names) => "[" + string.Join(",", names.Select(Q)) + "]";
    private static int Int(JsonNode? node) => node?.GetValue<int>() ?? -1;
    private static List<string> Names(JsonNode answer) => answer["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    private static void Steps(FieldContext ctx, OfflineEtabs modeler)
    {
        ctx.ClearTools();
        ctx.Offer(EtabsTools.All);
        ctx.Offer(ModelerTools.All);
        ctx.Offer(ProgramTools.Modeler);
        ctx.Offer(HandTools.All);
        ctx.Offer(TableTools.All);
        ctx.Offer(LateralTools.All);
        ctx.Offer(CaseTools.All);
        ctx.Offer(ModelTextTools.All);
        ctx.Offer(PropertyTools.All);
        ctx.Offer(StructureTools.All);
        var folder = Q(ctx.Folder);

        // A new model from the template: three stories, two bays each way.
        ctx.Tool("new_model", """{"title":"CPM field test","template":"concrete_frame","x_spans":[6,6],"y_spans":[5,5],"stories":3,"story_height":3.2,"sdl":2,"live":3}""", a =>
        {
            FieldContext.Expect(Int(a["stories"]) == 3, $"3 stories were asked for, the model has {a["stories"]}");
            FieldContext.Expect(Int(a["frames"]) == 63 && Int(a["areas"]) == 12, $"27 columns, 36 beams and 12 slabs were expected, the model has {a["frames"]} frames and {a["areas"]} areas");
        });
        var context = ctx.Tool("get_model_context", "{}", a =>
        {
            var c = a["counts"]!;
            FieldContext.Expect(Int(c["columns"]) == 27 && Int(c["beams"]) == 36 && Int(c["floors"]) == 12 && Int(c["walls"]) == 0 && Int(c["braces"]) == 0, "the counts are not 27 columns, 36 beams, 12 floors: " + c.ToJsonString());
            FieldContext.Expect(a["units"]!["force"]!.GetValue<string>() == "kN" && a["units"]!["length"]!.GetValue<string>() == "m", "the units are not kN, m: " + a["units"]!.ToJsonString());
            foreach (var key in new[] { "file", "program", "version", "locked", "stories", "frame_sections", "slab_and_wall_sections", "materials", "load_patterns", "load_cases", "combinations", "selected", "selected_count" })
                FieldContext.Expect(a[key] is not null, $"the answer has no '{key}'");
        });
        if (context is null) { ctx.Fail("the Modeler's model", "the model could not be started, so the Modeler's other tools were not run"); ctx.Coverage("every Modeler tool was run"); return; }
        var beamSection = modeler.Frames().First(f => f.Kind == "beam").Section;
        var cases = modeler.LoadCases();

        // What ETABS's dialogs define.
        ctx.Tool("add_concrete", """{"name":"CPM-C35","fc":35000,"elastic_modulus":32000000,"unit_weight":25}""", _ => FieldContext.Expect(modeler.Materials().Contains("CPM-C35"), "the material CPM-C35 is not in the model"));
        ctx.Refused("add_concrete", """{"name":"CPM-C35","fc":35000,"elastic_modulus":32000000}""", "already", "tool add_concrete says no to a name that is taken");
        foreach (var (name, json) in new[]
        {
            ("CPM-C400", """{"name":"CPM-C400","kind":"column","depth":0.4,"width":0.4,"material":"CPM-C35"}"""),
            ("CPM-B250x500", """{"name":"CPM-B250x500","kind":"beam","depth":0.5,"width":0.25,"material":"CPM-C35"}"""),
            ("CPM-S180", """{"name":"CPM-S180","kind":"slab","thickness":0.18,"material":"CPM-C35"}"""),
            ("CPM-W200", """{"name":"CPM-W200","kind":"wall","thickness":0.2,"material":"CPM-C35"}"""),
        })
            ctx.Tool("add_section", json, _ => FieldContext.Expect(modeler.FrameSections().Concat(modeler.AreaSections()).Contains(name), $"the section {name} is not in the model"), $"tool add_section {name}");
        ctx.Refused("add_section", """{"name":"CPM-X","kind":"column","depth":0.4,"width":0.4,"material":"no such material"}""", "no material", "tool add_section says no to a material that is not there");
        ctx.Tool("add_stories", """{"stories":[{"name":"CPM-Roof","height":3}]}""", _ =>
        {
            var top = modeler.Stories()[^1];
            FieldContext.Expect(top.Name == "CPM-Roof" && Math.Abs(top.Elevation - 12.6) < 1e-6, $"the top story should be CPM-Roof at 12.6, it is {top.Name} at {top.Elevation}");
        });
        ctx.Tool("add_grids", """{"grids":[{"label":"CPM-X","direction":"X","coordinate":18},{"label":"CPM-Y","direction":"Y","coordinate":15}]}""", _ =>
            FieldContext.Expect(modeler.Grids().Any(g => g.Label == "CPM-X" && Math.Abs(g.Coordinate - 18) < 1e-9) && modeler.Grids().Any(g => g.Label == "CPM-Y"), "the grid lines CPM-X and CPM-Y are not in the model"));

        // A small roof structure over one bay, and a wall: every kind of member, each read back.
        List<string> columns = [], beams = [], slabs = [], walls = [];
        ctx.Tool("add_columns", """{"points":[[0,0],[6,0],[6,5],[0,5]],"stories":["CPM-Roof"],"section":"CPM-C400"}""", a =>
        {
            columns = Names(a);
            var made = modeler.Frames().Where(f => columns.Contains(f.Name)).ToList();
            FieldContext.Expect(columns.Count == 4 && made.Count == 4 && made.All(f => f.Kind == "column" && f.Section == "CPM-C400" && f.Story == "CPM-Roof" && Math.Abs(f.Z1 - 9.6) < 1e-6 && Math.Abs(f.Z2 - 12.6) < 1e-6),
                "4 columns from 9.6 to 12.6 with section CPM-C400 were expected: " + string.Join("; ", made.Select(f => $"{f.Name} {f.Kind} {f.Section} {f.Z1}-{f.Z2}")));
        });
        ctx.Tool("add_beams", $$"""{"lines":[[0,0,6,0],[6,0,6,5],[6,5,0,5],[0,5,0,0]],"stories":["CPM-Roof"],"section":{{Q(beamSection)}}}""", a =>
        {
            beams = Names(a);
            var made = modeler.Frames().Where(f => beams.Contains(f.Name)).ToList();
            FieldContext.Expect(beams.Count == 4 && made.Count == 4 && made.All(f => f.Kind == "beam" && Math.Abs(f.Z1 - 12.6) < 1e-6), "4 beams at 12.6 were expected: " + string.Join("; ", made.Select(f => $"{f.Name} {f.Kind} at {f.Z1}")));
        });
        ctx.Tool("add_slabs", """{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"stories":["CPM-Roof"],"section":"CPM-S180"}""", a =>
        {
            slabs = Names(a);
            var made = modeler.Areas().Where(x => slabs.Contains(x.Name)).ToList();
            FieldContext.Expect(slabs.Count == 1 && made.Count == 1 && made[0].Kind == "floor" && made[0].Section == "CPM-S180" && made[0].Corners.Count == 4, "one floor with 4 corners and section CPM-S180 was expected");
        });
        ctx.Tool("add_walls", """{"lines":[[12,0,12,5]],"stories":["Story1"],"section":"CPM-W200"}""", a =>
        {
            walls = Names(a);
            var made = modeler.Areas().Where(x => walls.Contains(x.Name)).ToList();
            FieldContext.Expect(walls.Count == 1 && made.Count == 1 && made[0].Kind == "wall" && made[0].Section == "CPM-W200", "one wall with section CPM-W200 was expected");
        });
        ctx.Refused("add_columns", """{"points":[[0,0]],"stories":["No such story"],"section":"CPM-C400"}""", "No story named", "tool add_columns says no to a story that is not there");
        ctx.Refused("add_beams", """{"lines":[[0,0,6,0]],"section":"No such section"}""", "no frame section", "tool add_beams says no to a section that is not there");

        if (beams.Count > 0)
            ctx.Tool("set_section", $$"""{"frames":{{List(beams)}},"section":"CPM-B250x500"}""", a =>
            {
                FieldContext.Expect(Int(a["done"]) == beams.Count && a["failed"]!.AsArray().Count == 0, "not every beam was changed: " + a.ToJsonString());
                FieldContext.Expect(modeler.Frames().Where(f => beams.Contains(f.Name)).All(f => f.Section == "CPM-B250x500"), "the beams do not have the section CPM-B250x500 when read back");
            }, "tool set_section on frames");
        if (slabs.Count > 0)
        {
            var other = modeler.AreaSections().First(s => s != "CPM-S180" && s != "CPM-W200");
            ctx.Tool("set_section", $$"""{"areas":{{List(slabs)}},"section":{{Q(other)}}}""", _ => FieldContext.Expect(modeler.Areas().First(x => x.Name == slabs[0]).Section == other, $"the slab does not have the section {other} when read back"), "tool set_section on areas");
        }

        // Supports: at the base, then one point by name.
        var basePoints = modeler.Points().Where(p => Math.Abs(p.Z) < 1e-6).Select(p => p.Name).ToList();
        ctx.Tool("set_supports", """{"type":"pinned","at_base":true}""", a =>
        {
            FieldContext.Expect(Int(a["done"]) >= 9 && a["failed"]!.AsArray().Count == 0, "the 9 column bases were not all set: " + a.ToJsonString());
            FieldContext.Expect(basePoints.Count > 0 && modeler.Restraint(basePoints[0]).SequenceEqual([true, true, true, false, false, false]), "a base point is not pinned when read back");
        }, "tool set_supports pinned at the base");
        ctx.Tool("set_supports", """{"type":"fixed","at_base":true}""", _ => FieldContext.Expect(basePoints.All(p => modeler.Restraint(p).All(r => r)), "not every base point is fixed when read back"), "tool set_supports fixed at the base");
        var topPoint = modeler.Points().OrderByDescending(p => p.Z).First().Name;
        ctx.Tool("set_supports", $$"""{"type":"pinned","points":[{{Q(topPoint)}}]}""", _ => FieldContext.Expect(modeler.Restraint(topPoint).SequenceEqual([true, true, true, false, false, false]), "the point is not pinned when read back"), "tool set_supports on a named point");
        ctx.Tool("set_supports", $$"""{"type":"free","points":[{{Q(topPoint)}}]}""", _ => FieldContext.Expect(modeler.Restraint(topPoint).All(r => !r), "the point is not free when read back"), "tool set_supports free");

        // Loads and a combination.
        ctx.Tool("add_load_pattern", """{"name":"CPM-SDL","type":"Super Dead","self_weight":0}""", _ =>
        {
            FieldContext.Expect(modeler.LoadPatterns().Any(p => p.Name == "CPM-SDL" && p.Type == "Super Dead"), "the pattern CPM-SDL is not in the model");
            FieldContext.Expect(modeler.LoadCases().Contains("CPM-SDL"), "the pattern CPM-SDL has no load case of its name");
        });
        ctx.Refused("add_load_pattern", """{"name":"CPM-SDL","type":"Super Dead"}""", "already", "tool add_load_pattern says no to a name that is taken");
        if (beams.Count > 0 && slabs.Count > 0)
        {
            var (beam, slab) = (beams[0].Split('@')[0], slabs[0].Split('@')[0]);
            ctx.Tool("set_loads", $$"""{"frames":{{List(beams)}},"areas":{{List(slabs)}},"pattern":"CPM-SDL","value":4}""", a =>
            {
                FieldContext.Expect(Int(a["done"]) == beams.Count + slabs.Count, "not every object was loaded: " + a.ToJsonString());
                var onBeam = modeler.Model.LineLoads.Where(l => l.Object == beam && l.Story == "CPM-Roof" && l.Pattern == "CPM-SDL").Sum(Fval);
                var onSlab = modeler.Model.AreaLoads.Where(l => l.Object == slab && l.Story == "CPM-Roof" && l.Pattern == "CPM-SDL").Sum(Fval);
                FieldContext.Expect(Math.Abs(onBeam - 4) < 1e-9 && Math.Abs(onSlab - 4) < 1e-9, $"4 was put on the beam and the slab; the model has {onBeam} and {onSlab}");
            });
            ctx.Tool("set_loads", $$"""{"frames":[{{Q(beams[0])}}],"pattern":"CPM-SDL","value":1.5,"add":true}""", _ =>
            {
                var onBeam = modeler.Model.LineLoads.Where(l => l.Object == beam && l.Story == "CPM-Roof" && l.Pattern == "CPM-SDL").Sum(Fval);
                FieldContext.Expect(Math.Abs(onBeam - 5.5) < 1e-9, $"1.5 was added to 4; the model has {onBeam}");
            }, "tool set_loads adds to the load there");
        }
        ctx.Refused("set_loads", """{"frames":["no such frame"],"pattern":"No such pattern","value":1}""", "no load pattern", "tool set_loads says no to a pattern that is not there");
        var factors = string.Join(",", cases.Take(2).Append("CPM-SDL").Select(c => $$"""{"case":{{Q(c)}},"factor":1.4}"""));
        ctx.Tool("add_combination", $$"""{"name":"CPM-ULS","cases":[{{factors}}]}""", _ => FieldContext.Expect(modeler.Combos().Contains("CPM-ULS"), "the combination CPM-ULS is not in the model"));
        ctx.Refused("add_combination", """{"name":"CPM-BAD","cases":[{"case":"No such case","factor":1}]}""", "No load case named", "tool add_combination says no to a case that is not there");

        // Reading: every filter of query_objects, the selection, the tables.
        ctx.Tool("query_objects", """{"kind":"column","limit":0}""", a => FieldContext.Expect(Int(a["count"]) == 31 && a["objects"]!.AsArray().Count == 0, $"31 columns and no list were expected: {a["count"]}"), "tool query_objects by kind, counts only");
        ctx.Tool("query_objects", """{"kind":"beam","story":"CPM-Roof"}""", a => FieldContext.Expect(Int(a["count"]) == 4, $"4 beams on CPM-Roof were expected: {a["count"]}"), "tool query_objects by story");
        ctx.Tool("query_objects", """{"section":"CPM-C400"}""", a => FieldContext.Expect(Int(a["count"]) == 4, $"4 objects with CPM-C400 were expected: {a["count"]}"), "tool query_objects by section");
        ctx.Tool("query_objects", $$"""{"names":{{List(columns.Take(2).Concat(walls))}}}""", a =>
        {
            FieldContext.Expect(Int(a["count"]) == Math.Min(2, columns.Count) + walls.Count, $"the named objects were not all found: {a["count"]}");
            foreach (var o in a["objects"]!.AsArray())
                FieldContext.Expect(o!["name"] is not null && o["kind"] is not null && o["section"] is not null && (o["corners"] is not null || (o["from"] is not null && o["to"] is not null && o["length"] is not null && o["story"] is not null)), "an object is missing a field: " + o.ToJsonString());
        }, "tool query_objects by name, every field");
        ctx.Tool("select_objects", $$"""{"frames":{{List(columns.Take(2))}},"areas":{{List(slabs)}}}""", a =>
        {
            FieldContext.Expect(Int(a["selected"]) == Math.Min(2, columns.Count) + slabs.Count, "not everything was selected: " + a.ToJsonString());
            FieldContext.Expect(modeler.Selected().Count == Math.Min(2, columns.Count) + slabs.Count, $"the selection has {modeler.Selected().Count} objects when read back");
        });
        ctx.Tool("select_objects", $$"""{"frames":{{List(beams.Take(1))}},"keep_selection":true}""", _ => FieldContext.Expect(modeler.Selected().Count == Math.Min(2, columns.Count) + slabs.Count + Math.Min(1, beams.Count), $"the selection has {modeler.Selected().Count} objects after adding one"), "tool select_objects keeps the selection");
        ctx.Tool("query_objects", """{"selected_only":true}""", a => FieldContext.Expect(Int(a["count"]) == modeler.Selected().Count, $"{modeler.Selected().Count} objects are selected, the answer has {a["count"]}"), "tool query_objects, the selection only");
        ctx.Tool("select_objects", """{"frames":["no such frame"]}""", a => FieldContext.Expect(Int(a["selected"]) == 0 && a["failed"]!.AsArray().Count == 1 && modeler.Selected().Count == 0, "a name that is not there should be reported as failed, and the selection cleared: " + a.ToJsonString()), "tool select_objects reports a name that is not there");
        ctx.Tool("list_tables", "{}", a => FieldContext.Expect(Int(a["count"]) >= 5, $"the Modeler's own tables were expected: {a["count"]}"));
        ctx.Tool("list_tables", """{"contains":"story","include_empty":true}""", a => FieldContext.Expect(a["tables"]!.AsArray().Any(t => t!.GetValue<string>().Contains("Story Definitions", StringComparison.Ordinal)), "Story Definitions was not found by 'story'"), "tool list_tables with a search");
        foreach (var table in modeler.Tables())
            ctx.Tool("read_table", $$"""{"table":{{Q(table.Name)}},"limit":5}""", a => FieldContext.Expect(a["fields"]!.AsArray().Count > 0, "the table has no fields"), $"tool read_table {table.Name}");
        ctx.Tool("read_table", """{"table":"Frame Assignments - Section Properties","where":{"Story":"CPM-Roof"},"fields":["UniqueName","Section Property"],"offset":1,"limit":2}""", a =>
        {
            FieldContext.Expect(Int(a["row_count"]) == 8 && a["rows"]!.AsArray().Count == 2 && a["fields"]!.AsArray().Count == 2, "8 frames on CPM-Roof, 2 rows of 2 fields were expected: " + a.ToJsonString());
            FieldContext.Expect(a["note"] is not null, "no note says there are more rows");
        }, "tool read_table with where, fields, offset and limit");
        ctx.Refused("read_table", """{"table":"Story Drift"}""", "no table", "tool read_table says no to a table that is not there");

        Defining(ctx, modeler);
        Tables(ctx, modeler);
        ModelerCheck.FieldSteps(ctx);
        Lateral(ctx, modeler);
        Analysis(ctx, modeler);

        // The checks and the quantities.
        ctx.Tool("check_model", "{}", a => FieldContext.Expect(Int(a["errors"]) == 0, "the model the test built has errors: " + a["findings"]!.ToJsonString()));
        ctx.Tool(StabilityTools.Name, "{}", a => FieldContext.Expect(Int(a["errors"]) == 0 && a["checked"]?["stiffness_check"]?.GetValue<bool>() == true,
            "the model the test built should have no instability, and the stiffness check should run: " + a["findings"]!.ToJsonString() + " " + a["notes"]!.ToJsonString()));
        Instabilities(ctx);
        ctx.Tool("model_quantities", "{}", a =>
        {
            var rows = a["rows"]!.AsArray();
            FieldContext.Expect(rows.Count >= 4 && rows.Any(r => r!["section"]!.GetValue<string>() == "CPM-C400" && Int(r["count"]) == 4), "4 columns with CPM-C400 were expected in the quantities: " + rows.ToJsonString());
        });

        // No analysis here: the result tools answer plainly, and the analysis says where it is run.
        foreach (var name in new[] { "results_overview", "check_drift", "check_torsion", "check_story_irregularity", "check_modal", "design_ratios", "check_report" })
            ctx.Tool(name, "{}", a => FieldContext.Expect(a.ToJsonString().Contains("not available", StringComparison.Ordinal), "with no analysis the answer should say 'not available'"), $"tool {name} with no analysis says so");
        ctx.Refused("run_analysis", "{}", "cannot analyse", "tool run_analysis says no in the Modeler");
        ctx.Refused("run_design", "{}", "cannot analyse or design", "tool run_design says no in the Modeler");

        // The files the programs open.
        var saved = ctx.Tool("save_model_as", $$"""{"name":"{{ModelName}}","folder":{{folder}}}""", a =>
        {
            var file = a["file"]!.GetValue<string>();
            FieldContext.Expect(File.Exists(file) && new FileInfo(file).Length > 1000, $"the file {file} was not written");
            FieldContext.Expect(File.Exists(a["modeler_file"]!.GetValue<string>()), "the Modeler's own file was not written beside it");
            FieldContext.Expect(Int(a["errors"]) == 0, "the saved model has errors: " + a["findings"]!.ToJsonString());
        });
        if (saved?["file"]?.GetValue<string>() is { } e2k)
        {
            ctx.Files[CsiProgram.Etabs.Key] = e2k;
            ctx.Written = (modeler.Stories().Count - 1, modeler.Frames().Count, modeler.Areas().Count);
            ctx.Tool("save_model", "{}", a => FieldContext.Expect(string.Equals(a["saved"]!.GetValue<string>(), e2k, StringComparison.OrdinalIgnoreCase), $"it saved to {a["saved"]}, not to the model's own file"));
        }

        // The other programs' files, each written by that program's own part of the test.
        Safe.SafeFieldSteps.Modeler(ctx);
        Sap2000.SapFieldSteps.Modeler(ctx);

        // Snapshots, a delete between them, and the files read back.
        ctx.Tool("model_snapshot", """{"name":"cpm-before"}""", a => FieldContext.Expect(Int(a["frames"]) == modeler.Frames().Count && Int(a["areas"]) == modeler.Areas().Count, "the snapshot does not hold every object: " + a.ToJsonString()));
        if (columns.Count > 0 && walls.Count > 0)
            ctx.Tool("delete_objects", $$"""{"frames":[{{Q(columns[^1])}}],"areas":{{List(walls)}}}""", a =>
            {
                FieldContext.Expect(Int(a["done"]) == 2 && a["failed"]!.AsArray().Count == 0, "the column and the wall were not both deleted: " + a.ToJsonString());
                FieldContext.Expect(!modeler.Frames().Any(f => f.Name == columns[^1]) && !modeler.Areas().Any(x => x.Name == walls[0]), "a deleted object is still in the model");
            });
        else ctx.Refused("delete_objects", "{}", "Give 'frames' or 'areas'", "tool delete_objects says no with nothing to delete");
        ctx.Tool("list_snapshots", "{}", a => FieldContext.Expect(Int(a["count"]) >= 1, "the snapshot is not listed"));
        ctx.Tool("compare_snapshots", """{"from":"cpm-before"}""", a =>
        {
            FieldContext.Expect(Int(a["frames"]!["removed"]) == 1 && Int(a["areas"]!["removed"]) == 1, "one frame and one area removed were expected: " + a.ToJsonString());
        });
        if (ctx.Files.TryGetValue(CsiProgram.Etabs.Key, out var written))
        {
            ctx.Tool("open_model_file", $$"""{"path":{{Q(Path.ChangeExtension(written, ".cpm"))}}}""", a => Same(ctx, a, "the Modeler's own file"), "tool open_model_file reads the .cpm back");
            ctx.Tool("open_model_file", $$"""{"path":{{Q(written)}}}""", a =>
            {
                Same(ctx, a, "the .e2k");
                FieldContext.Expect(modeler.Materials().Contains("CPM-C35") && modeler.FrameSections().Contains("CPM-C400") && modeler.AreaSections().Contains("CPM-W200") && modeler.LoadPatterns().Any(p => p.Name == "CPM-SDL") && modeler.Combos().Contains("CPM-ULS"),
                    "the material, the sections, the load pattern or the combination did not come back from the .e2k");
            }, "tool open_model_file reads the .e2k back");
        }
        ctx.Refused("open_model_file", $$"""{"path":{{Q(Path.Combine(ctx.Folder, "no such file.e2k"))}}}""", "no file", "tool open_model_file says no to a file that is not there");

        ctx.Coverage("every Modeler tool was run");
    }

    /// <summary>
    /// The Define forms' tools (add, change, rename and delete, the way SAP2000's forms do) and the window's hand edits
    /// with undo. What is added here is deleted again, so the files the programs open are the ones the steps above built.
    /// </summary>
    private static void Defining(FieldContext ctx, OfflineEtabs modeler)
    {
        string Done(JsonNode a) => a["done"]?.GetValue<string>() ?? "";
        ctx.Tool("model_definitions", "{}", a =>
        {
            FieldContext.Expect(a["stories"]!.AsArray().Count == modeler.Stories().Count - 1, "the forms' story list does not match the model");
            FieldContext.Expect(a["combinations"]!.AsArray().Any(c => c!["name"]!.GetValue<string>() == "CPM-ULS" && c["cases"]!.AsArray().Count > 0), "CPM-ULS and its cases are not in the definitions: " + a["combinations"]!.ToJsonString());
            FieldContext.Expect(a["frame_sections"]!.AsArray().Any(x => x!["name"]!.GetValue<string>() == "CPM-C400" && x["used"]!.GetValue<int>() > 0), "CPM-C400 and its use are not in the definitions");
        });
        var stories = modeler.Model.Stories.Skip(1).Select(x => $$"""{"name":{{Q(x.Name)}},"height":{{CsiProMax.E2k.E2kText.Number(x.Height)}},"was":{{Q(x.Name)}}}""");
        var frames = modeler.Frames().Count;
        ctx.Tool("define_stories", $$"""{"stories":[{{string.Join(",", stories)}}]}""", a => FieldContext.Expect(modeler.Frames().Count == frames, $"the story data was sent back as it is, and {frames - modeler.Frames().Count} objects went missing"));
        var grids = modeler.Model.Grids.Select(g => $$"""{"label":{{Q(g.Label)}},"direction":{{Q(g.Direction)}},"coordinate":{{CsiProMax.E2k.E2kText.Number(g.Coordinate)}}}""").ToList();
        var gridCount = grids.Count;
        ctx.Tool("define_grids", $$"""{"grids":[{{string.Join(",", grids.Append("""{"label":"CPM-G","direction":"X","coordinate":-3}"""))}}]}""", a => FieldContext.Expect(modeler.Model.Grids.Count == gridCount + 1, "the new grid line is not in the model"), "tool define_grids adds a grid line");
        ctx.Tool("define_grids", $$"""{"grids":[{{string.Join(",", grids)}}]}""", a => FieldContext.Expect(modeler.Model.Grids.Count == gridCount && !modeler.Model.Grids.Exists(g => g.Label == "CPM-G"), "the grid line was not deleted again"), "tool define_grids deletes it again");
        ctx.Tool("define_material", """{"name":"CPM-C40","fc":40000,"elastic_modulus":35000000}""", a => FieldContext.Expect(modeler.Materials().Contains("CPM-C40"), "CPM-C40 was not added: " + Done(a)), "tool define_material adds a concrete");
        ctx.Tool("define_material", """{"name":"CPM-C45","was":"CPM-C40","fc":45000,"elastic_modulus":36000000}""", a => FieldContext.Expect(modeler.Model.Materials.Find(m => m.Name == "CPM-C45")?.Number("FC") == 45000, "CPM-C40 was not renamed and changed"), "tool define_material changes and renames it");
        ctx.Tool("define_frame_section", """{"name":"CPM-B300","kind":"beam","depth":0.6,"width":0.3,"material":"CPM-C45"}""", a => FieldContext.Expect(modeler.FrameSections().Contains("CPM-B300"), "CPM-B300 was not added"), "tool define_frame_section adds a beam section");
        ctx.Tool("define_frame_section", """{"name":"CPM-B350","was":"CPM-B300","kind":"beam","depth":0.65,"width":0.35,"material":"CPM-C45"}""", a => FieldContext.Expect(modeler.Model.FrameSections.Find(x => x.Name == "CPM-B350")?.Number("D") == 0.65, "CPM-B300 was not renamed and changed"), "tool define_frame_section changes and renames it");
        ctx.Tool("define_shell_section", """{"name":"CPM-S170","kind":"slab","thickness":0.18}""", a => FieldContext.Expect(modeler.AreaSections().Contains("CPM-S170"), "CPM-S170 was not added"), "tool define_shell_section adds a slab section");
        ctx.Tool("define_shell_section", """{"name":"CPM-S170","was":"CPM-S170","kind":"slab","thickness":0.19}""", a => FieldContext.Expect(modeler.Model.ShellSections.Find(x => x.Name == "CPM-S170")?.Thickness == 0.19, "CPM-S170's thickness did not change"), "tool define_shell_section changes it");
        ctx.Tool("define_load_pattern", """{"name":"CPM-WIND","type":"Wind","self_weight":0}""", a => FieldContext.Expect(modeler.LoadPatterns().Any(p => p.Name == "CPM-WIND"), "CPM-WIND was not added"), "tool define_load_pattern adds a pattern");
        ctx.Tool("define_load_pattern", """{"name":"CPM-W","was":"CPM-WIND","type":"Wind","self_weight":0}""", a => FieldContext.Expect(modeler.LoadPatterns().Any(p => p.Name == "CPM-W") && modeler.LoadCases().Contains("CPM-W"), "CPM-WIND and its case were not renamed"), "tool define_load_pattern renames it");
        ctx.Tool("define_combination", """{"name":"CPM-SLS","type":"Linear Add","cases":[{"case":"Dead","factor":1},{"case":"CPM-W","factor":0.6}]}""", a => FieldContext.Expect(modeler.Combos().Contains("CPM-SLS"), "CPM-SLS was not added"), "tool define_combination adds a combination");
        ctx.Tool("define_combination", """{"name":"CPM-ENV","type":"Envelope","cases":[{"case":"CPM-ULS","factor":1},{"case":"CPM-SLS","factor":1}]}""", a => FieldContext.Expect(modeler.Model.Combos.Find(c => c.Name == "CPM-ENV")?.Lines.Count(l => l[0] == "LOADCOMBO") == 2, "CPM-ENV does not hold the two combinations"), "tool define_combination makes an envelope of combinations");
        ctx.Refused("delete_definition", """{"kind":"frame_section","name":"CPM-C400"}""", "used by", "tool delete_definition keeps a section in use");
        foreach (var (kind, name) in new[] { ("combination", "CPM-ENV"), ("combination", "CPM-SLS"), ("load_pattern", "CPM-W"), ("frame_section", "CPM-B350"), ("shell_section", "CPM-S170"), ("material", "CPM-C45") })
            ctx.Tool("delete_definition", $$"""{"kind":"{{kind}}","name":"{{name}}"}""", a => FieldContext.Expect(Done(a).StartsWith("deleted", StringComparison.Ordinal), $"{name} was not deleted: " + a.ToJsonString()), $"tool delete_definition deletes {name}");

        // The window's own path: a hand edit and its undo, through HandModel as the window sends them.
        ctx.Step("a hand edit in the Modeler's window is undone and redone", () =>
        {
            HandModel.Reset();
            var before = modeler.LoadPatterns().Count;
            var add = HandModel.Handle(modeler, JsonDocument.Parse("""{"tool":"define_load_pattern","args":{"name":"CPM-HAND","type":"Other"}}""").RootElement);
            FieldContext.Expect(add["ok"]!.GetValue<bool>() && modeler.LoadPatterns().Count == before + 1, "the hand edit was not made: " + add.ToJsonString());
            var undo = HandModel.Handle(modeler, JsonDocument.Parse("""{"undo":true}""").RootElement);
            FieldContext.Expect(undo["ok"]!.GetValue<bool>() && modeler.LoadPatterns().Count == before, "undo did not take it back: " + undo.ToJsonString());
            var redo = HandModel.Handle(modeler, JsonDocument.Parse("""{"redo":true}""").RootElement);
            FieldContext.Expect(redo["ok"]!.GetValue<bool>() && modeler.LoadPatterns().Count == before + 1, "redo did not bring it back: " + redo.ToJsonString());
            HandModel.Handle(modeler, JsonDocument.Parse("""{"undo":true}""").RootElement);
            HandModel.Reset();
            FieldContext.Expect(modeler.LoadPatterns().Count == before, "the test's pattern was left in the model");
            return "made, undone, redone and undone again";
        });
    }

    /// <summary>The model's tables to Excel and back, the way SAP2000 does it: the workbook the Modeler writes reads back
    /// into the very same model, and SAP2000's own tables go to Excel too.</summary>
    /// <summary>Every seismic and wind code the Modeler lists, set on a pattern with its defaults: the forces must
    /// come out and add up. Ahmed: wind and seismic "should cover all options as csi programs gives them".</summary>
    private static void Lateral(FieldContext ctx, OfflineEtabs modeler)
    {
        ctx.Tool("lateral_codes", """{"kind":"seismic"}""", a => FieldContext.Expect(a["codes"]!.AsArray().Count >= 25, "the seismic codes are missing: " + a["codes"]!.AsArray().Count));
        ctx.Tool("lateral_codes", """{"kind":"wind","code":"ASCE 7-16"}""", a => FieldContext.Expect(a["inputs"]!.AsArray().Count > 5, "ASCE 7-16 wind has no inputs: " + a.ToJsonString()), "tool lateral_codes gives one code's inputs");
        foreach (var code in CsiProMax.Lateral.LateralCodes.All)
        {
            var name = code.Kind == CsiProMax.Lateral.LateralCodes.Wind ? "CPM-WIND" : "CPM-EQ";
            ctx.Tool("set_lateral_load", $$"""{"name":"{{name}}","kind":"{{code.Kind.ToLowerInvariant()}}","code":{{Q(code.Name)}}}""", a =>
            {
                FieldContext.Expect(a["error"] is null, $"{code.Name} could not be computed: {a["error"]}");
                var forces = a["story_forces"]!.AsArray();
                FieldContext.Expect(code.Family == "user-loads" || forces.Sum(f => Math.Abs(f!["fx"]!.GetValue<double>()) + Math.Abs(f["fy"]!.GetValue<double>())) > 0, $"{code.Name} gave no story forces: " + a.ToJsonString());
                var shear = modeler.Model.PointLoads.Where(l => l.Pattern == name).Sum(l => CsiProMax.E2k.E2kText.TryNumber(CsiProMax.E2k.E2kText.Value(l.Tokens, "FX"), out var v) ? v : 0);
                var expected = forces.Sum(f => f!["fx"]!.GetValue<double>());
                FieldContext.Expect(Math.Abs(shear - expected) <= 1e-3 * Math.Max(1, Math.Abs(expected)), $"the joint loads add up to {shear}, the story forces to {expected}");
            }, $"tool set_lateral_load, {code.Kind.ToLowerInvariant()} {code.Name}");
        }
        foreach (var name in new[] { "CPM-EQ", "CPM-WIND" })
        {
            ctx.Tool("set_lateral_load", $$"""{"name":"{{name}}","code":"None"}""", _ => FieldContext.Expect(!modeler.Model.PointLoads.Any(l => l.Pattern == name), $"{name}'s forces are still there"), $"tool set_lateral_load takes the code load off {name}");
            ctx.Tool("delete_definition", $$"""{"kind":"load_pattern","name":"{{name}}"}""", null, $"tool delete_definition takes {name} out again");
        }
        ctx.Refused("set_lateral_load", """{"name":"CPM-EQ","code":"ASCE 7-99"}""", "codes", "tool set_lateral_load says no to a code that is not there");
    }

    /// <summary>The load cases the scratch programs must find in the files the Modeler wrote (CsiFieldSteps checks them).</summary>
    public static readonly string[] Cases = ["CPM-RSX", "CPM-THX", "CPM-NLS", "CPM-BUCK"];

    /// <summary>Functions, a case of each kind, the mass source, P-Delta and temperature: they stay in the model the
    /// programs open, so their scratch parts show whether each program reads them.</summary>
    private static void Analysis(FieldContext ctx, OfflineEtabs modeler)
    {
        ctx.Tool("define_function", """{"name":"CPM-RS","kind":"spectrum","from":"code","code":"ASCE 7-16","values":{"Ss":"1.0","S1":"0.4","SiteClass":"D"}}""", a => FieldContext.Expect(Int(a["points"]) > 50, "the spectrum has few points: " + a.ToJsonString()), "tool define_function, the ASCE 7-16 spectrum");
        ctx.Tool("define_function", """{"name":"CPM-TH","kind":"history","from":"sine","period":0.5,"cycles":4,"amplitude":0.2}""", a => FieldContext.Expect(Int(a["points"]) == 65, "4 cycles of 16 steps: " + a.ToJsonString()), "tool define_function, a sine history");
        ctx.Refused("define_function", """{"name":"CPM-BAD","kind":"spectrum","points":[[1,1],[0.5,1]]}""", "must go up", "tool define_function says no to periods that go back");
        if (!modeler.Model.LoadCases.Exists(c => c.Name == "Modal"))
            ctx.Tool("define_load_case", """{"name":"Modal","type":"Modal - Eigen","max_modes":12}""", null, "tool define_load_case, the modal case");
        ctx.Tool("define_load_case", """{"name":"CPM-RSX","type":"Response Spectrum","modal_case":"Modal","modal_combo":"CQC","dir_combo":"SRSS","damping":0.05,"eccentricity":0.05,"loads":[{"accel":"U1","factor":9.80665,"function":"CPM-RS"}]}""", null, "tool define_load_case, a response spectrum case");
        ctx.Tool("define_load_case", """{"name":"CPM-THX","type":"Linear Modal History","modal_case":"Modal","steps":100,"step_size":0.02,"loads":[{"accel":"U1","factor":9.80665,"function":"CPM-TH"}]}""", null, "tool define_load_case, a linear modal history");
        ctx.Tool("define_load_case", """{"name":"CPM-NLS","type":"Nonlinear Static","geometric":"P-Delta","loads":[{"pattern":"Dead","factor":1.2},{"pattern":"Live","factor":0.5}]}""", null, "tool define_load_case, a nonlinear static case with P-Delta");
        ctx.Tool("define_load_case", """{"name":"CPM-BUCK","type":"Buckling","max_modes":3,"loads":[{"pattern":"Dead","factor":1}]}""", null, "tool define_load_case, a buckling case");
        ctx.Refused("define_load_case", """{"name":"CPM-BAD","type":"Response Spectrum","loads":[{"accel":"U1","factor":1,"function":"CPM-TH"}]}""", "spectrum function", "tool define_load_case says no to a history in a spectrum case");
        ctx.Tool("define_mass_source", """{"elements":true,"loads":[{"pattern":"Live","factor":0.25}]}""", _ => FieldContext.Expect(CsiProMax.Lateral.MassSource.Of(modeler.Model).Loads.Count == 1, "the mass source has no Live"), "tool define_mass_source, the elements and a quarter of the live load");
        ctx.Tool("define_pdelta", """{"method":"non-iterative"}""", _ => FieldContext.Expect(CsiProMax.Model.AnalysisDefs.PDelta(modeler.Model).Method == "non-iterative", "P-Delta was not set"), "tool define_pdelta, non-iterative from the mass");
        var beam = modeler.Model.LineAssigns.FirstOrDefault(x => modeler.Model.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam);
        if (beam is not null)
        {
            if (!modeler.Model.LoadPatterns.Exists(p => p.Name == "CPM-TEMP")) modeler.AddLoadPattern("CPM-TEMP", "Temperature", 0);
            var obj = Q(beam.Label + "@" + beam.Story);
            ctx.Tool("assign_object_loads", """{"kind":"frame_temperature","pattern":"CPM-TEMP","objects":[""" + obj + """],"values":{"temperature":20}}""", null, "tool assign_object_loads, a temperature on a beam");
            ctx.Tool("assign_object_loads", """{"kind":"frame_point","pattern":"Live","objects":[""" + obj + """],"values":{"value":10,"distance":0.5}}""", null, "tool assign_object_loads, a point load mid-span");
        }
        ctx.Tool("analysis_definitions", "{}", a =>
        {
            var cases = a["load_cases"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToHashSet();
            FieldContext.Expect(Cases.All(cases.Contains), "the cases are not all listed: " + string.Join(", ", cases));
            FieldContext.Expect(a["functions"]!.AsArray().Any(f => f!["name"]!.GetValue<string>() == "CPM-RS"), "CPM-RS is not listed");
        });
        ModelText(ctx, modeler);
        Properties(ctx, modeler);
        Structure(ctx, modeler);
    }

    /// <summary>Diaphragms, pier and spandrel labels, groups and a section cut, named CPM- for the program checks.</summary>
    private static void Structure(FieldContext ctx, OfflineEtabs modeler)
    {
        var m = modeler.Model;
        var floors = m.AreaAssigns.Where(x => m.Area(x.Label)?.Kind == CsiProMax.Model.AreaKind.Floor).Select(x => x.Label + "@" + x.Story).ToList();
        var beams = m.LineAssigns.Where(x => m.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam).Select(x => x.Label + "@" + x.Story).ToList();
        ctx.Tool("define_diaphragm", """{"name":"CPM-D2","semi_rigid":true}""", null, "tool define_diaphragm, a semi-rigid diaphragm");
        if (floors.Count > 0) ctx.Tool("assign_diaphragm_data", """{"objects":[""" + Q(floors[0]) + """],"diaphragm":"CPM-D2"}""", null, "tool assign_diaphragm_data, on a floor");
        ctx.Refused("define_diaphragm", """{"name":"CPM-D2","delete":true}""", floors.Count > 0 ? "take it off" : "", "tool define_diaphragm says no to deleting one in use");
        ctx.Tool("define_pier_spandrel", """{"kind":"pier","name":"CPM-P1"}""", null, "tool define_pier_spandrel, a pier label");
        if (beams.Count > 0) ctx.Tool("assign_pier_spandrel_data", """{"objects":[""" + Q(beams[0]) + """],"spandrel":"CPM-S1"}""", null, "tool assign_pier_spandrel_data, a spandrel on a beam");
        ctx.Tool("define_group", """{"name":"CPM-G","color":"Blue"}""", null, "tool define_group");
        ctx.Tool("assign_group_data", """{"group":"CPM-G","objects":""" + List(beams.Take(3)) + "}", a => FieldContext.Expect(Int(a["members"]) == Math.Min(3, beams.Count), "the group has " + a["members"]), "tool assign_group_data, three beams");
        ctx.Tool("define_section_cut", """{"name":"CPM-SC","group":"CPM-G"}""", null, "tool define_section_cut, from the group");
        ctx.Refused("define_group", """{"name":"CPM-G","delete":true}""", "CPM-SC", "tool define_group says no to deleting a group a section cut uses");
        ctx.Tool("structure_definitions", "{}", a => FieldContext.Expect(a["groups"]!.AsArray().Any(g => g!["name"]!.GetValue<string>() == "CPM-G"), "CPM-G is not listed"));
    }

    /// <summary>The property items: a steel, a steel I section with modifiers, a cracked drop panel, releases and more on a
    /// beam, and a spring under a base joint. Their names start with CPM- so the program checks can find them.</summary>
    public static readonly string[] Shapes = ["CPM-W", "CPM-PIPE", "CPM-ANGLE"];

    private static void Properties(FieldContext ctx, OfflineEtabs modeler)
    {
        ctx.Tool("define_material_data", """{"name":"CPM-S355","type":"Steel","grade":"S355","values":{"WEIGHTPERVOLUME":77,"E":210000000,"U":0.3,"FY":355000,"FU":510000}}""",
            _ => FieldContext.Expect(modeler.Model.Materials.Exists(m => m.Name == "CPM-S355" && m.Type == "Steel"), "CPM-S355 is not a steel in the model"));
        ctx.Refused("define_material_data", """{"name":"CPM-BAD","type":"Steel","values":{"E":200000000,"FY":355000,"FU":100}}""", "Fu at least Fy", "tool define_material_data says no to Fu below Fy");
        ctx.Tool("define_frame_shape", """{"name":"CPM-W","shape":"Steel I/Wide Flange","material":"CPM-S355","dims":{"D":0.5,"B":0.2,"TF":0.016,"TW":0.01},"modifiers":{"I3MOD":0.9}}""", null, "tool define_frame_shape, a steel I with a modifier");
        ctx.Tool("define_frame_shape", """{"name":"CPM-PIPE","shape":"Steel Pipe","material":"CPM-S355","dims":{"D":0.3,"TW":0.01}}""", null, "tool define_frame_shape, a pipe");
        ctx.Tool("define_frame_shape", """{"name":"CPM-ANGLE","shape":"Steel Angle","material":"CPM-S355","dims":{"D":0.1,"B":0.1,"TF":0.01,"TW":0.01}}""", null, "tool define_frame_shape, an angle");
        ctx.Refused("define_frame_shape", """{"name":"CPM-BAD","shape":"Steel Pipe","material":"CPM-S355","dims":{"D":0.3}}""", "needs TW", "tool define_frame_shape says no to a missing dimension");
        var conc = modeler.Model.Materials.FirstOrDefault(m => m.IsConcrete)?.Name ?? "";
        ctx.Tool("define_shell_type", """{"name":"CPM-DROP","kind":"slab","slab_type":"Drop","modeling":"ShellThick","thickness":0.3,"material":""" + Q(conc) + ""","modifiers":{"M11MOD":0.25,"M22MOD":0.25}}""", null, "tool define_shell_type, a cracked drop panel");
        ctx.Tool("define_spring", """{"kind":"point","name":"CPM-PS","values":{"UX":1000,"UY":1000,"UZ":50000}}""", null, "tool define_spring, a point spring");
        ctx.Tool("define_spring", """{"kind":"area","name":"CPM-AS","values":{"U3":20000}}""", null, "tool define_spring, an area spring");
        ctx.Refused("define_spring", """{"kind":"line","name":"CPM-BAD","values":{"UX":5}}""", "not 'UX'", "tool define_spring says no to a value the kind does not have");
        var beam = modeler.Model.LineAssigns.FirstOrDefault(x => modeler.Model.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam);
        if (beam is not null)
        {
            var obj = Q(beam.Label + "@" + beam.Story);
            ctx.Tool("assign_frame_data", """{"objects":[""" + obj + """],"release_i":["M3"],"release_j":["M3"],"angle":0,"cardinal":8,"modifiers":{"I3MOD":0.35,"JMOD":0.01}}""", null, "tool assign_frame_data, pinned ends, top center and cracked");
            ctx.Refused("assign_frame_data", """{"objects":[""" + obj + """],"release_i":["T"],"release_j":["T"]}""", "unstable", "tool assign_frame_data says no to torsion released at both ends");
        }
        var floor = modeler.Model.AreaAssigns.FirstOrDefault(x => modeler.Model.Area(x.Label)?.Kind == CsiProMax.Model.AreaKind.Floor);
        if (floor is not null) ctx.Tool("assign_shell_data", """{"objects":[""" + Q(floor.Label + "@" + floor.Story) + """],"modifiers":{"M11MOD":0.25,"M22MOD":0.25}}""", null, "tool assign_shell_data, cracked slab modifiers");
        var support = modeler.Model.PointAssigns.FirstOrDefault(p => p.Restraint is not null);
        if (support is not null) ctx.Tool("assign_joint_data", """{"objects":[""" + Q(support.Point + "@" + support.Story) + """],"spring":"CPM-PS"}""", null, "tool assign_joint_data, a spring under a support");
        ctx.Tool("define_link", """{"name":"CPM-LINK","type":"RUBBER ISOLATOR","dofs":{"U1":{"stiffness":500000},"U2":{"stiffness":1000,"damping":20},"U3":{"stiffness":1000,"damping":20}}}""", null, "tool define_link, a rubber isolator");
        ctx.Refused("define_link", """{"name":"CPM-BAD","type":"SPRINGY","dofs":{"U1":{"stiffness":1}}}""", "link type", "tool define_link says no to a type the programs do not have");
        ctx.Tool("property_definitions", "{}", a =>
        {
            FieldContext.Expect(a["frame_sections"]!.AsArray().Count(s => Shapes.Contains(s!["name"]!.GetValue<string>())) == Shapes.Length, "the new sections are not all listed");
            FieldContext.Expect(a["springs"]!.AsArray().Count >= 2, "the springs are not listed");
        });
    }

    /// <summary>Edit > Model Text: the safety net reads every section, takes a hand-written combination, and refuses text that breaks the model.</summary>
    private static void ModelText(FieldContext ctx, OfflineEtabs modeler)
    {
        ctx.Tool("model_text", "{}", a => FieldContext.Expect(a["sections"]!.AsArray().Any(s => s!["section"]!.GetValue<string>() == "LOAD CASES"), "LOAD CASES is not listed: " + a.ToJsonString()));
        var combos = "";
        ctx.Tool("model_text", """{"section":"LOAD COMBINATIONS"}""", a => combos = a["text"]?.GetValue<string>() ?? "", "tool model_text, one section");
        var text = (combos.Length > 0 ? combos + "\n" : "") + "  COMBO \"CPM-TXT\"  TYPE \"Linear Add\"\n  COMBO \"CPM-TXT\"  LOADCASE \"Dead\"  SF 1.4";
        ctx.Tool("edit_model_text", """{"section":"LOAD COMBINATIONS","text":""" + Q(text) + "}", _ =>
            FieldContext.Expect(modeler.Combos().Contains("CPM-TXT"), "the combination written as text is not in the model"), "tool edit_model_text, a combination written by hand");
        ctx.Refused("edit_model_text", """{"section":"LINE CONNECTIVITIES","text":"  LINE \"B1\" BEAM \"no such point\""}""", "Nothing was changed", "tool edit_model_text says no to text that breaks the model");
    }

    private static void Tables(FieldContext ctx, OfflineEtabs modeler)
    {
        ctx.Tool("model_tables", "{}", a => FieldContext.Expect(a["tables"]!.AsArray().Any(t => t!["name"]!.GetValue<string>() == "Frame Assignments - Section Properties" && t["rows"]!.GetValue<int>() == modeler.Model.LineAssigns.Count),
            "the table list does not count the model's frames: " + a.ToJsonString()));
        var before = CsiProMax.Model.ModelFile.ToJson(modeler.Model);
        string? saved = null;
        ctx.Tool("export_tables_excel", $$"""{"name":"CPM field test tables","folder":{{Q(ctx.Folder)}}}""", a =>
        {
            saved = a["saved"]?.GetValue<string>();
            FieldContext.Expect(saved is not null && File.Exists(saved), "the workbook was not written: " + a.ToJsonString());
            FieldContext.Expect(a["tables"]!.AsArray().Count == CsiProMax.CsiTables.ModelTables.Names.Length, "not every table went to the workbook");
        }, "tool export_tables_excel writes the model's tables");
        if (saved is not null)
            ctx.Tool("import_tables_excel", $$"""{"path":{{Q(saved)}}}""", a =>
            {
                FieldContext.Expect(a["read"]!.AsArray().Count == CsiProMax.CsiTables.ModelTables.Names.Length, "not every table was read back: " + a.ToJsonString());
                var after = CsiProMax.Model.ModelFile.ToJson(modeler.Model);
                var at = 0;
                while (at < Math.Min(before.Length, after.Length) && before[at] == after[at]) at++;
                string Around(string text) => text.Substring(Math.Max(0, at - 120), Math.Min(text.Length - Math.Max(0, at - 120), 200));
                FieldContext.Expect(after == before, $"the workbook read back did not give the same model: before ...{Around(before)}... after ...{Around(after)}...");
            }, "tool import_tables_excel reads the workbook back into the same model");
        ctx.Tool("export_tables_excel", $$"""{"name":"CPM field test SAP2000 tables","folder":{{Q(ctx.Folder)}},"format":"sap2000"}""", a =>
        {
            var path = a["saved"]?.GetValue<string>();
            FieldContext.Expect(path is not null && TableTools.Read(path).Exists(t => t.Name == "Joint Coordinates" && t.Rows.Count > 0), "SAP2000's joint table is not in the workbook");
        }, "tool export_tables_excel writes SAP2000's own tables");
        ctx.Refused("import_tables_excel", $$"""{"path":{{Q(Path.Combine(ctx.Folder, "no such workbook.xlsx"))}}}""", "There is no file", "tool import_tables_excel refuses a file that is not there");
    }

    private static void Same(FieldContext ctx, JsonNode answer, string what) =>
        FieldContext.Expect(Int(answer["stories"]) == ctx.Written.Stories && Int(answer["frames"]) == ctx.Written.Frames && Int(answer["areas"]) == ctx.Written.Areas,
            $"{what} was written with {ctx.Written.Stories} stories, {ctx.Written.Frames} frames and {ctx.Written.Areas} areas, and read back with {answer["stories"]}, {answer["frames"]} and {answer["areas"]}");

    /// <summary>The value of a load as the Modeler keeps it (the number after FVAL); 0 when it has none.</summary>
    private static double Fval(CsiProMax.Model.ObjectLoad load) =>
        CsiProMax.E2k.E2kText.TryNumber(CsiProMax.E2k.E2kText.Value(load.Tokens, "FVAL"), out var value) ? value : 0;
}
