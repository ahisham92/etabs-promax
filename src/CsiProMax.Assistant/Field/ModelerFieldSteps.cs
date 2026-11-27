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
        ctx.Offer(ExtraTools.All);
        ctx.Offer(EditTools.All);
        ctx.Offer(DefineTools.All);
        ctx.Offer(EtabsDefineTools.All);
        ctx.Offer(EtabsDesignTools.All);
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
        Editing(ctx, modeler);
        DefineItems(ctx, modeler);
        Tables(ctx, modeler);
        FileExports(ctx, modeler);
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

        // ETABS's Frame/Wall Nonlinear Hinges forms, read back in the scratch ETABS (EtabsHingeRoundTrip.Check).
        EtabsHingeRoundTrip.Define(ctx);
        // ETABS's Link Property Data forms (every type), kept as ETABS's own tables and read back field by field (EtabsLinkRoundTrip.Check).
        EtabsLinkRoundTrip.Define(ctx);

        // The files the programs open.
        var saved = ctx.Tool("save_model_as", $$"""{"name":"{{ModelName}}","folder":{{folder}}}""", a =>
        {
            var file = a["file"]!.GetValue<string>();
            FieldContext.Expect(File.Exists(file) && new FileInfo(file).Length > 1000, $"the file {file} was not written");
            FieldContext.Expect(File.Exists(a["modeler_file"]!.GetValue<string>()), "the Modeler's own file was not written beside it");
            FieldContext.Expect(a["modeler_file"]!.GetValue<string>().EndsWith(CsiProMax.Model.ModelFile.Etabs, StringComparison.OrdinalIgnoreCase), $"an ETABS model's own file is {a["modeler_file"]}, not a .cpme"); // ETABS itself
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
            ctx.Tool("open_model_file", $$"""{"path":{{Q(CsiProMax.Model.ModelFile.Beside(written) ?? CsiProMax.Model.ModelFile.PathFor(written, "etabs"))}}}""", a => Same(ctx, a, "the Modeler's own file"), "tool open_model_file reads the .cpme back");
            ctx.Tool("open_model_file", $$"""{"path":{{Q(written)}}}""", a =>
            {
                Same(ctx, a, "the .e2k");
                FieldContext.Expect(modeler.Materials().Contains("CPM-C35") && modeler.FrameSections().Contains("CPM-C400") && modeler.AreaSections().Contains("CPM-W200") && modeler.LoadPatterns().Any(p => p.Name == "CPM-SDL") && modeler.Combos().Contains("CPM-ULS"),
                    "the material, the sections, the load pattern or the combination did not come back from the .e2k");
            }, "tool open_model_file reads the .e2k back");
        }
        // The SAP2000 and SAFE files the Modeler wrote open again just as they were (Ahmed 2026-11-26: "to and from the
        // csi promax ... the user does not feel anything"): the whole model, nothing to say about them.
        foreach (var (key, ext) in new[] { (CsiProgram.Sap2000.Key, ".s2k"), (CsiProgram.Safe.Key, ".f2k") })
            if (ctx.Files.TryGetValue(key, out var file) && File.Exists(file))
                ctx.Tool("open_model_file", $$"""{"path":{{Q(file)}}}""", a =>
                {
                    Same(ctx, a, "the " + ext);
                    FieldContext.Expect(a["not_read"] is null, $"the {ext} the Modeler wrote should open with nothing left out: " + a["not_read"]?.ToJsonString());
                }, $"tool open_model_file reads the {ext} back, the whole model");
        FileTypes(ctx, modeler);
        if (ctx.Files.TryGetValue(CsiProgram.Etabs.Key, out var e2kAgain))
            ctx.Tool("open_model_file", $$"""{"path":{{Q(e2kAgain)}}}""", a => Same(ctx, a, "the .e2k"), "tool open_model_file opens the .e2k again for the steps after");
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
        // The program the model is for (Ahmed 2026-11-26): changed and changed back, the model otherwise as it was.
        var target = modeler.Model.Target;
        ctx.Tool("set_model_program", """{"program":"sap2000"}""", a => FieldContext.Expect(modeler.Model.Target == "sap2000" && a["was"]?.GetValue<string>() == CsiProMax.Model.StructuralModel.TargetName(target), "the model is not made for SAP2000 now: " + a.ToJsonString()), "tool set_model_program, to SAP2000");
        ctx.Tool("set_model_program", $$"""{"program":{{Q(target)}}}""", a => FieldContext.Expect(modeler.Model.Target == target, "the model's program did not come back: " + a.ToJsonString()), "tool set_model_program, back");
        // A units change (Ahmed 2026-11-27): the model shown in N, mm, saved in its own units, and back exactly.
        var ownText = CsiProMax.E2k.E2kWriter.Write(modeler.Model);
        var ownLength = modeler.Model.Units.Length;
        var height = modeler.Model.Stories.Count > 1 ? modeler.Model.Stories[1].Height : 0;
        ctx.Tool("set_display_units", """{"force":"N","length":"mm","temperature":"C"}""", a => FieldContext.Expect(modeler.Model.Units.Length == "mm" && modeler.SavedUnits is not null && (height == 0 || Math.Abs(modeler.Model.Stories[1].Height - height * CsiProMax.Model.ModelUnits.Factor(CsiProMax.Model.ModelUnits.Dim.Len, new CsiProMax.Model.Units { Force = "N", Length = ownLength }, new CsiProMax.Model.Units { Force = "N", Length = "mm" })) < 1e-6 * Math.Max(1, Math.Abs(modeler.Model.Stories[1].Height))), "the model is not shown in N, mm: " + a.ToJsonString()), "tool set_display_units, N mm");
        ctx.Tool("set_display_units", """{"own":true}""", a => FieldContext.Expect(modeler.SavedUnits is null && Same(CsiProMax.E2k.E2kWriter.Write(modeler.Model), ownText), "back in its own units the model is not as it was: " + a.ToJsonString()), "tool set_display_units, back");
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

    /// <summary>
    /// Draw Braces, Draw Joints and Edit > Replicate, Move and Divide Frames (Ahmed 2026-11-26: every button). Each is
    /// checked on the model, and the model is put back as it was afterwards, so the later counts still hold.
    /// </summary>
    /// <summary>Define > Reinforcing Bar Sizes and Modify/Show Rebar stay in the model (the .e2k has keywords for them, and
    /// the program opens them); the definitions kept as table rows are tried and taken back.</summary>
    private static void DefineItems(FieldContext ctx, OfflineEtabs modeler)
    {
        var m = modeler.Model;
        ctx.Tool("define_rebar_size", """{"name":"CPM16","area":0.000201,"diameter":0.016}""",
            a => FieldContext.Expect(CsiProMax.Model.DefineTables.RebarSizes(m).Exists(x => x.Name == "CPM16"), "no bar CPM16: " + a.ToJsonString()), "tool define_rebar_size, Define > Reinforcing Bar Sizes");
        ctx.Refused("define_rebar_size", """{"name":"CPM20","area":0.0003}""", "diameter", "tool define_rebar_size says no to a bar with no diameter");
        var concrete = m.FrameSections.FirstOrDefault(x => CsiProMax.Model.DefineTables.Rebar(m, x.Name) is { } r && r.GetValueOrDefault("TYPE") == "Column");
        if (concrete is not null)
        {
            ctx.Tool("define_frame_rebar", $$$"""{"section":{{{Q(concrete.Name)}}},"values":{"COVER":0.045}}""",
                a => FieldContext.Expect(CsiProMax.Model.DefineTables.Rebar(m, concrete.Name)!["COVER"] == "0.045", "the cover did not change: " + a.ToJsonString()), "tool define_frame_rebar, Frame Section > Modify/Show Rebar");
            ctx.Refused("define_frame_rebar", $$$"""{"section":{{{Q(concrete.Name)}}},"values":{"PATTERN":"many"}}""", "PATTERN", "tool define_frame_rebar says no to a wrong bar pattern");
        }
        var before = CsiProMax.Model.ModelFile.ToJson(m);
        var unsaved = modeler.Unsaved;
        try
        {
            ctx.Tool("define_table_rows", """{"table":"Hinge Definitions - Moment M3","name":"CPM-H1","rows":[{"IsSymmetric":true}]}""",
                a => FieldContext.Expect(m.DefinitionTables?.GetValueOrDefault("Hinge Definitions - Moment M3")?.Exists(r => r["Name"] == "CPM-H1") == true, "no hinge row: " + a.ToJsonString()), "tool define_table_rows, a hinge definition kept as the program's table rows");
            ctx.Refused("define_table_rows", """{"table":"Hinge Definitions - Moment M3","name":"CPM-H2","rows":[{"Nope":1}]}""", "no field", "tool define_table_rows says no to a field the table does not have");
            ctx.Tool("definition_data", "{}", a => FieldContext.Expect(a["rebar_sizes"]!.AsArray().Count > 0 && a["tables"]!.AsObject().Count > 0, "definition_data is empty: " + a.ToJsonString()), "tool definition_data reads the Define items");
        }
        finally { modeler.Rollback(CsiProMax.Model.ModelFile.FromJson(before), unsaved); }
    }

    private static void Editing(FieldContext ctx, OfflineEtabs modeler)
    {
        var before = CsiProMax.Model.ModelFile.ToJson(modeler.Model);
        var unsaved = modeler.Unsaved;
        try
        {
            var m = modeler.Model;
            var stories = m.Stories.Select(s => s.Name).ToList();
            var top = stories[^1];
            var beam = m.LineAssigns.FirstOrDefault(x => x.Story == top && m.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam);
            var column = m.LineAssigns.FirstOrDefault(x => m.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Column);
            var section = column?.Section ?? m.FrameSections[0].Name;
            var (minX, maxX) = (m.Points.Min(p => p.X), m.Points.Max(p => p.X));
            var (minY, maxY) = (m.Points.Min(p => p.Y), m.Points.Max(p => p.Y));
            var far = maxY - minY + 50;
            ctx.Tool("add_braces", $$"""{"braces":[[{{minX}},{{minY}},{{maxX}},{{minY}}]],"stories":[{{Q(top)}}],"section":{{Q(section)}}}""",
                a => FieldContext.Expect(a["braces"]!.AsArray().Count == 1 && m.Line(a["braces"]![0]!.GetValue<string>().Split('@')[0])?.Kind == CsiProMax.Model.LineKind.Brace, "no brace: " + a.ToJsonString()),
                "tool add_braces, Draw > Draw Braces");
            ctx.Refused("add_braces", $$"""{"braces":[[1,1,1,1]],"stories":[{{Q(top)}}],"section":{{Q(section)}}}""", "apart in plan", "tool add_braces says no to a brace with both ends at one place");
            ctx.Tool("add_joints", $$"""{"points":[[{{minX - 5}},{{minY - 5}}]],"stories":[{{Q(top)}}]}""",
                a => FieldContext.Expect(m.PointAssigns.Exists(p => p.Story == top && p.Point == a["joints"]![0]!.GetValue<string>().Split('@')[0]), "no joint: " + a.ToJsonString()),
                "tool add_joints, Draw > Draw Joints");
            if (beam is null) { ctx.Step("Edit > Replicate, Move and Divide Frames on a beam", () => throw new Exception("the Modeler's model has no beam on " + top)); return; }
            var name = Q(beam.Label + "@" + top);
            var loads = m.LineLoads.Count(l => l.Object == beam.Label && l.Story == top);
            ctx.Tool("replicate_objects", $$"""{"objects":[{{name}}],"mode":"linear","dy":{{far}},"number":2}""", a =>
            {
                var made = a["made"]!.AsArray().Select(x => x!.GetValue<string>().Split('@')[0]).ToList();
                FieldContext.Expect(made.Count == 2, "two copies were expected: " + a.ToJsonString());
                foreach (var c in made)
                {
                    FieldContext.Expect(m.LineAssigns.Single(x => x.Label == c && x.Story == top).Section == beam.Section, $"{c} lost the section");
                    FieldContext.Expect(m.LineLoads.Count(l => l.Object == c && l.Story == top) == loads, $"{c} lost its loads");
                }
            }, "tool replicate_objects, Edit > Replicate linear, with the section and loads");
            ctx.Refused("replicate_objects", $$"""{"objects":[{{name}}],"mode":"story","stories":[{{Q(top)}}]}""", "already on", "tool replicate_objects says no to a copy onto the object itself");
            ctx.Tool("move_objects", $$"""{"objects":[{{name}}],"dx":0,"dy":{{-far}}}""", a =>
            {
                var moved = a["moved"]![0]!.GetValue<string>().Split('@')[0];
                FieldContext.Expect(!m.LineAssigns.Exists(x => x.Label == beam.Label && x.Story == top), "the beam is still where it was");
                FieldContext.Expect(m.LineLoads.Count(l => l.Object == moved && l.Story == top) == loads, "its loads did not go with it");
                name = Q(moved + "@" + top);
            }, "tool move_objects, Edit > Move, with the section and loads");
            var lineLoads = m.LineLoads.Where(l => l.Story == top && Q(l.Object + "@" + top) == name).ToList();
            m.LineLoads.RemoveAll(l => lineLoads.Contains(l) && !string.Equals(CsiProMax.E2k.E2kText.Unquote(CsiProMax.E2k.E2kText.Value(l.Tokens, "TYPE") ?? ""), "UNIFF", StringComparison.OrdinalIgnoreCase));
            ctx.Tool("divide_frames", $$"""{"objects":[{{name}}],"parts":3}""",
                a => FieldContext.Expect(a["parts"]!.AsArray().Count == 3 && a["parts"]!.AsArray().All(p => m.LineAssigns.Exists(x => x.Label + "@" + x.Story == p!.GetValue<string>() && x.Section == beam.Section)), "three beams with the section were expected: " + a.ToJsonString()),
                "tool divide_frames, Edit > Divide Frames into 3");
            {
                var first = modeler.Model.LineAssigns.Where(x => x.Story == top && modeler.Model.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam).Select(x => (x, l: modeler.Model.Line(x.Label)!)).FirstOrDefault();
                if (first.l is not null)
                {
                    var pi = modeler.Model.Point(first.l.PointI)!;
                    ctx.Tool("reshape_object", $$"""{"object":{{Q(first.x.Label + "@" + top)}},"from":[{{pi.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}},{{pi.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}],"to":[{{(pi.X + 0.25).ToString(System.Globalization.CultureInfo.InvariantCulture)}},{{pi.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}]}""",
                        a => FieldContext.Expect(m.LineAssigns.Exists(x => x.Label + "@" + x.Story == a["reshaped"]!.GetValue<string>() && x.Section == first.x.Section), "the reshaped beam lost its section: " + a.ToJsonString()),
                        "tool reshape_object, Draw > Reshape Object moves a beam's end");
                }
            }
            if (column is not null) ctx.Refused("divide_frames", $$"""{"objects":[{{Q(column.Label + "@" + column.Story)}}],"parts":2}""", "divides beams", "tool divide_frames says no to a column");
        }
        finally
        {
            modeler.Rollback(CsiProMax.Model.ModelFile.FromJson(before), unsaved);
            ctx.Step("the Modeler's model is as it was after the Draw and Edit checks", () => FieldContext.Expect(CsiProMax.Model.ModelFile.ToJson(modeler.Model) == before, "the model was not put back"));
        }
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
            // ASCE 7-22 has no Fa and Fv tables: its SDS and SD1 are given (the USGS geodatabase's in a real model).
            var values = code.Name == "ASCE 7-22" && code.Kind == CsiProMax.Lateral.LateralCodes.Seismic ? ""","values":{"SDS":"1.0","SD1":"0.6"}""" : "";
            ctx.Tool("set_lateral_load", $$"""{"name":"{{name}}","kind":"{{code.Kind.ToLowerInvariant()}}","code":{{Q(code.Name)}}{{values}}}""", a =>
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
        // ETABS's own function forms' tables, read back by ETABS's scratch part (EtabsFunctionRoundTrip.Check).
        EtabsFunctionRoundTrip.Define(ctx);
        if (!modeler.Model.LoadCases.Exists(c => c.Name == "Modal"))
            ctx.Tool("define_load_case", """{"name":"Modal","type":"Modal - Eigen","max_modes":12}""", null, "tool define_load_case, the modal case");
        ctx.Tool("define_load_case", """{"name":"CPM-RSX","type":"Response Spectrum","modal_case":"Modal","modal_combo":"CQC","dir_combo":"SRSS","damping":0.05,"eccentricity":0.05,"loads":[{"accel":"U1","factor":9.80665,"function":"CPM-RS"}]}""", null, "tool define_load_case, a response spectrum case");
        ctx.Tool("define_load_case", """{"name":"CPM-THX","type":"Linear Modal History","modal_case":"Modal","steps":100,"step_size":0.02,"loads":[{"accel":"U1","factor":9.80665,"function":"CPM-TH"}]}""", null, "tool define_load_case, a linear modal history");
        ctx.Tool("define_load_case", """{"name":"CPM-NLS","type":"Nonlinear Static","geometric":"P-Delta","loads":[{"pattern":"Dead","factor":1.2},{"pattern":"Live","factor":0.5}]}""", null, "tool define_load_case, a nonlinear static case with P-Delta");
        ctx.Tool("define_load_case", """{"name":"CPM-BUCK","type":"Buckling","max_modes":3,"loads":[{"pattern":"Dead","factor":1}]}""", null, "tool define_load_case, a buckling case");
        ctx.Refused("define_load_case", """{"name":"CPM-BAD","type":"Response Spectrum","loads":[{"accel":"U1","factor":1,"function":"CPM-TH"}]}""", "spectrum function", "tool define_load_case says no to a history in a spectrum case");
        ctx.Tool("define_mass_source", """{"elements":true,"loads":[{"pattern":"Live","factor":0.25}]}""", _ => FieldContext.Expect(CsiProMax.Lateral.MassSource.Of(modeler.Model).Loads.Count == 1, "the mass source has no Live"), "tool define_mass_source, the elements and a quarter of the live load");
        ctx.Tool("define_pdelta", """{"method":"non-iterative"}""", _ => FieldContext.Expect(CsiProMax.Model.AnalysisDefs.PDelta(modeler.Model).Method == "non-iterative", "P-Delta was not set"), "tool define_pdelta, non-iterative from the mass");
        // Analyze > Set Load Cases to Run: the buckling case left out, then every case runs again.
        ctx.Tool("set_load_cases_to_run", """{"not_run":["CPM-BUCK"]}""", _ => FieldContext.Expect(!CsiProMax.Model.RunCases.Runs(modeler.Model, "CPM-BUCK") && CsiProMax.Model.RunCases.NotRun(modeler.Model).Count == 1, "CPM-BUCK is still set to run"), "tool set_load_cases_to_run leaves the buckling case out");
        ctx.Tool("set_load_cases_to_run", """{"all":true}""", _ => FieldContext.Expect(CsiProMax.Model.RunCases.NotRun(modeler.Model).Count == 0, "a case is still set not to run"), "tool set_load_cases_to_run runs every case again");
        var beam = modeler.Model.LineAssigns.FirstOrDefault(x => modeler.Model.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam);
        if (beam is not null)
        {
            if (!modeler.Model.LoadPatterns.Exists(p => p.Name == "CPM-TEMP")) modeler.AddLoadPattern("CPM-TEMP", "Temperature", 0);
            var obj = Q(beam.Label + "@" + beam.Story);
            ctx.Tool("assign_object_loads", """{"kind":"frame_temperature","pattern":"CPM-TEMP","objects":[""" + obj + """],"values":{"temperature":20}}""", null, "tool assign_object_loads, a temperature on a beam");
            ctx.Tool("assign_object_loads", """{"kind":"frame_point","pattern":"Live","objects":[""" + obj + """],"values":{"value":10,"distance":0.5}}""", null, "tool assign_object_loads, a point load mid-span");
            // Assign > Paste Assigns > Frame: the beam's point loads go on another beam, word for word.
            var other = modeler.Model.LineAssigns.FirstOrDefault(x => x != beam && modeler.Model.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam);
            if (other is not null)
                ctx.Tool("paste_object_loads", """{"from":""" + obj + ""","objects":[""" + Q(other.Label + "@" + other.Story) + """],"types":["POINTF","POINTM"]}""",
                    _ => FieldContext.Expect(modeler.Model.LineLoads.Exists(x => x.Object == other.Label && x.Story == other.Story && (CsiProMax.E2k.E2kText.Value(x.Tokens, "TYPE") ?? "").Contains("POINTF")), "the point load was not pasted"),
                    "tool paste_object_loads, a beam's point load pasted on another beam");
        }
        // Assign > Shell Loads > Uniform Load Sets: None takes a slab's load set off.
        var slab = modeler.Model.AreaAssigns.FirstOrDefault();
        if (slab is not null)
        {
            ctx.Tool("assign_shell_load_set", """{"set":"","objects":[""" + Q(slab.Label + "@" + slab.Story) + """]}""", null, "tool assign_shell_load_set, None on a slab");
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
        Extras(ctx, modeler);
        Robustness(ctx);
    }

    /// <summary>The standalone never crashes or corrupts the model (Ahmed 2026-11-25): every Modeler tool with bent input,
    /// on a model of its own (the test's model is left alone), then tools at random one after the other.</summary>
    private static void Robustness(FieldContext ctx)
    {
        static OfflineEtabs Fresh() => new(CsiProMax.Model.Templates.ConcreteFrame("Robustness", [6, 5], [4], 2, 3.2));
        static string Say(List<ModelFuzz.Finding> f) => $"{f.Count} finding(s): " + string.Join(" | ", f.Take(5).Select(x => $"{x.Tool} {(x.Args.Length > 160 ? x.Args[..160] + "..." : x.Args)} -> {x.What}"));
        var seed = Environment.TickCount;
        ctx.Step("the Modeler takes bent input to every tool (20 calls each) without crashing or a half-done edit", () =>
        {
            var found = ModelFuzz.Run(Fresh, HandModel.Tools.Values, 20, seed, TimeSpan.FromMinutes(3));
            FieldContext.Expect(found.Count == 0, $"seed {seed}: " + Say(found));
            return $"{HandModel.Tools.Values.Count(ModelFuzz.Fuzzed)} tools, seed {seed}";
        });
        ctx.Step("the Modeler takes 600 tool calls at random in a row and its model still saves and writes", () =>
        {
            var found = ModelFuzz.RunMixed(Fresh, HandModel.Tools.Values, 600, seed, TimeSpan.FromMinutes(3));
            FieldContext.Expect(found.Count == 0, $"seed {seed}: " + Say(found));
            return $"seed {seed}";
        });
    }

    /// <summary>SAP2000's joint constraints and moving loads and SAFE's own strips, named CPM- for the program checks:
    /// they stay in the model, so SAP2000's scratch part sees whether it knows their tables and fields.</summary>
    private static void Extras(FieldContext ctx, OfflineEtabs modeler)
    {
        var m = modeler.Model;
        var beams = m.LineAssigns.Where(x => m.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam).ToList();
        var top = beams.Where(b => b.Story == beams[^1].Story).Select(b => b.Label + "@" + b.Story).Take(2).ToList();
        var joints = beams.Count == 0 ? [] : new[] { m.Line(beams[0].Label)!.PointI, m.Line(beams[0].Label)!.PointJ }.Select(p => p + "@" + beams[0].Story).ToList();
        ctx.Tool("define_constraint", """{"name":"CPM-BODY","type":"Body","dofs":["UX","UY","RZ"]}""", null, "tool define_constraint, a body constraint");
        ctx.Tool("define_constraint", """{"name":"CPM-WELD","type":"Weld","tolerance":0.01}""", null, "tool define_constraint, a weld");
        ctx.Refused("define_constraint", """{"name":"CPM-BAD","type":"Weld"}""", "tolerance", "tool define_constraint says no to a weld with no tolerance");
        ctx.Refused("define_constraint", """{"name":"CPM-BAD","type":"Glue"}""", "constraint type", "tool define_constraint says no to a type SAP2000 does not have");
        if (joints.Count == 2) ctx.Tool("assign_joint_constraint", """{"objects":""" + List(joints) + ""","constraint":"CPM-BODY"}""", a => FieldContext.Expect(Int(a["assigned"]) == 2, "2 joints: " + a.ToJsonString()), "tool assign_joint_constraint, a beam's two ends");
        ctx.Refused("assign_joint_constraint", """{"objects":["NOPE@NOPE"],"constraint":"CPM-BODY"}""", "no point", "tool assign_joint_constraint says no to a joint that is not there");
        if (top.Count > 0) ctx.Tool("define_lane", """{"name":"CPM-LANE","frames":""" + List(top) + ""","offset":0,"width":3}""", null, "tool define_lane, along beams");
        ctx.Tool("define_vehicle", """{"name":"CPM-HL93","standard":"HL-93K"}""", null, "tool define_vehicle, a standard truck");
        ctx.Tool("define_vehicle", """{"name":"CPM-TRUCK","axles":[{"load":35},{"load":145,"spacing":4.3},{"load":145,"spacing":4.3}]}""", null, "tool define_vehicle, a general truck by its axles");
        ctx.Refused("define_vehicle", """{"name":"CPM-BAD"}""", "axles", "tool define_vehicle says no to a general vehicle with no load");
        ctx.Tool("define_vehicle_class", """{"name":"CPM-CLASS","vehicles":[{"vehicle":"CPM-HL93"},{"vehicle":"CPM-TRUCK","scale":1.25}]}""", null, "tool define_vehicle_class");
        if (top.Count > 0)
        {
            ctx.Tool("define_moving_case", """{"name":"CPM-MOVE","assignments":[{"class":"CPM-CLASS","lanes":["CPM-LANE"]}]}""", null, "tool define_moving_case");
            ctx.Refused("define_vehicle_class", """{"name":"CPM-CLASS","delete":true}""", "CPM-MOVE", "tool define_vehicle_class says no to deleting a class a case uses");
        }
        ctx.Tool("define_strip", """{"name":"CPM-STRIP","layer":"A","points":[[0,2.5],[12,2.5]],"width_left":1,"width_right":1}""", null, "tool define_strip, a hand-drawn strip for SAFE");
        ctx.Refused("define_strip", """{"name":"CPM-BAD","points":[[0,0]],"width_left":1}""", "two points", "tool define_strip says no to a strip of one point");
        // Post-tensioning (Ahmed 2026-11-26): a tendon section of a tendon material, the two prestress patterns, a draped
        // tendon jacked from both ends with fixed losses, and the hyperstatic case, written as ETABS 20 writes them.
        ctx.Tool("define_material_data", """{"name":"CPM-A416","type":"Tendon","values":{"E":196500000,"FY":1675000,"FU":1862000}}""", null, "tool define_material_data, a tendon material");
        ctx.Tool("define_tendon_section", """{"name":"CPM-T15","material":"CPM-A416","strand_area":0.00014}""", null, "tool define_tendon_section");
        ctx.Refused("define_tendon_section", """{"name":"CPM-BADT","material":"CPM-S355","strand_area":0.00014}""", "Tendon material", "tool define_tendon_section says no to a steel material");
        ctx.Tool("add_load_pattern", """{"name":"CPM-PTT","type":"Prestress-Transfer","self_weight":0}""", null, "tool add_load_pattern, the transfer prestress pattern");
        ctx.Tool("add_load_pattern", """{"name":"CPM-PTF","type":"Prestress-Final","self_weight":0}""", null, "tool add_load_pattern, the final prestress pattern");
        ctx.Tool("draw_tendon", """{"name":"CPM-TEN","points":[[0,2.5],[12,2.5]],"spans":[{"depth_left":0.05,"depth_low":0.1,"depth_right":0.05}],"section":"CPM-T15","strands":4,""" +
            """ "jacking":{"transfer_pattern":"CPM-PTT","final_pattern":"CPM-PTF","stress":1400000,"end":"Both"},"losses":{"method":"Fixed","stressing":100000,"long_term":150000}}""",
            a => FieldContext.Expect((int?)a["spans"] == 1 && (int?)a["strands"] == 4, "not the tendon asked for: " + a.ToJsonString()), "tool draw_tendon, a draped tendon jacked from both ends");
        ctx.Refused("draw_tendon", """{"name":"CPM-BADT","points":[[0,2.5],[12,2.5]],"spans":[{"depth_left":0.05,"depth_low":5,"depth_right":0.05}],"section":"CPM-T15"}""", "below the slab", "tool draw_tendon says no to a tendon below the slab");
        ctx.Tool("define_hyperstatic_case", """{"name":"CPM-PTF-HP","base_case":"CPM-PTF"}""", null, "tool define_hyperstatic_case, of the final prestress case");
        // ETABS 23.2's own export (2026-11-26): percentage and detailed losses, a punching overwrite with a user perimeter and
        // stud rails, an opening and a ribbed slab, as ETABS writes them.
        ctx.Tool("draw_tendon", """{"name":"CPM-TEN2","points":[[0,3.5],[12,3.5]],"spans":[{"depth_left":0.05,"depth_low":0.1,"depth_right":0.05,"a":0,"b":0}],"section":"CPM-T15","strands":2,""" +
            """ "jacking":{"transfer_pattern":"CPM-PTT","final_pattern":"CPM-PTF","stress":1400000,"end":"I"},"losses":{"method":"Detailed","curvature":0.07,"wobble":0.0003,"anchor_slip":0.006,"elastic_shortening":7000,"creep":48000,"shrinkage":41000,"relaxation":21000}}""",
            a => FieldContext.Expect(((string?)a["losses"])?.StartsWith("Detailed", StringComparison.Ordinal) == true, "not detailed losses: " + a.ToJsonString()), "tool draw_tendon, a parabola with detailed losses");
        var slabStory = CsiProMax.Model.SlabDefs.Tendons(modeler.Model).Find(t => t.Name == "CPM-TEN")?.Story ?? "";
        var at = modeler.Model.Points.Find(p => p.Below == 0 && Math.Abs(p.X) < 1e-6 && Math.Abs(p.Y - 2.5) < 1e-6)?.Name ?? "";
        ctx.Tool("set_punching_overwrite", """{"point":""" + Q(at) + ""","story":""" + Q(slabStory) + ""","location":"Edge","perimeter":[[-0.3,-0.3],[0.3,-0.3],[0.3,0.3],[-0.3,0.3]],""" +
            """ "reinforcement":{"type":"Stud Rails","pattern":"Radial","fy":420000,"diameter":0.01,"spacing":0.08}}""",
            a => FieldContext.Expect((string?)a["perimeter"] == "4 corners", "not the user perimeter: " + a.ToJsonString()), "tool set_punching_overwrite, a user perimeter with stud rails");
        ctx.Refused("set_punching_overwrite", """{"point":""" + Q(at) + ""","story":""" + Q(slabStory) + ""","reinforcement":{"type":"Stud Rails"}}""", "yield stress", "tool set_punching_overwrite says no to studs with no size");
        ctx.Tool("draw_opening", """{"name":"CPM-OPEN","story":""" + Q(slabStory) + ""","points":[[4,1],[5,1],[5,2],[4,2]]}""", null, "tool draw_opening");
        ctx.Refused("draw_opening", """{"name":"CPM-BADO","story":""" + Q(slabStory) + ""","points":[[4,1],[5,1]]}""", "3 corners", "tool draw_opening says no to two corners");
        // ETABS 23.2 (Ahmed's PC, the J export): a slab line release has no .e2k keyword, only its table, and ETABS Plus
        // refuses slab rebar; both are kept in the Modeler's file and written into the .f2k.
        ctx.Tool("set_slab_line_release", """{"name":"CPM-LR","story":""" + Q(slabStory) + ""","points":[[3,0],[3,5]],"shear_left":true,"moment_left":true}""",
            a => FieldContext.Expect((string?)a["released"] == "shear left, moment left", "not the release asked for: " + a.ToJsonString()), "tool set_slab_line_release, on a null line it draws");
        ctx.Refused("set_slab_line_release", """{"name":"CPM-LR"}""", "at least one", "tool set_slab_line_release says no to a release of nothing");
        // 0.4.58 full field test: a null line drawn on the beam at x = 6 made ETABS stop opening the .e2k on its "Log".
        ctx.Refused("set_slab_line_release", """{"name":"CPM-LR3","story":""" + Q(slabStory) + ""","points":[[6,0],[6,5]],"shear_left":true}""", "on top of another", "tool set_slab_line_release says no to a null line on top of a beam");
        ctx.Tool("define_material_data", """{"name":"CPM-REBAR","type":"Rebar","values":{"E":200000000,"FY":420000,"FU":620000}}""", null, "tool define_material_data, a rebar material");
        var bar = CsiProMax.Model.DefineTables.RebarSizes(modeler.Model).Select(x => x.Name).FirstOrDefault() ?? "16";
        ctx.Tool("draw_slab_rebar", """{"name":"CPM-SR","story":""" + Q(slabStory) + ""","from":[1,1.5],"to":[11,1.5],"width_left":0.5,"width_right":0.5,"vertical_offset":0.03,"layer":"A","bar_size":""" + Q(bar) + ""","material":"CPM-REBAR","total_bars":6}""",
            a => FieldContext.Expect(((string?)a["bars"])?.StartsWith("6 x", StringComparison.Ordinal) == true, "not 6 bars: " + a.ToJsonString()), "tool draw_slab_rebar, six bars in a band");
        ctx.Refused("draw_slab_rebar", """{"name":"CPM-SR2","story":""" + Q(slabStory) + ""","from":[1,1.5],"to":[11,1.5],"width_left":0.5,"bar_size":""" + Q(bar) + ""","material":"CPM-REBAR","total_bars":6,"max_spacing":0.2}""", "not both", "tool draw_slab_rebar says no to both a number of bars and a spacing");
        ctx.Tool("extra_definitions", "{}", a =>
        {
            FieldContext.Expect(a["constraints"]!.AsArray().Count == 2 && a["vehicles"]!.AsArray().Count == 2 && a["strips"]!.AsArray().Count == 1 && a["tendons"]!.AsArray().Count == 2 && a["tendons"]![0]!["shape"]!.AsArray().Count > 2
                && a["punching_overwrites"]!.AsArray().Count == 1 && a["openings"]!.AsArray().Count == 1 && a["slab_line_releases"]!.AsArray().Count == 1 && a["slab_rebar"]!.AsArray().Count == 1, "not all listed: " + a.ToJsonString());
            if (joints.Count == 2) FieldContext.Expect(a["constraints"]![0]!["joints"]!.AsArray().Count == 2, "CPM-BODY's joints are not listed");
        });
    }

    /// <summary>A pier label and a story where a wall carries it: the only rows ETABS's pier overwrite tables have.</summary>
    internal static (string Name, string Story) PierOnWall(CsiProMax.Model.StructuralModel m) =>
        m.AreaAssigns.Where(x => m.Area(x.Label)?.Kind == CsiProMax.Model.AreaKind.Wall)
            .Select(x => (Name: CsiProMax.Model.PropertyDefs.Value(x.Extra, "PIER") ?? "", x.Story))
            .FirstOrDefault(x => x.Name.Length > 0 && Tools.StructureTools.Labels(m).Contains(("pier", x.Name)));

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
        // The pier on a wall: ETABS lists a pier's design overwrite rows only for the stories where walls carry it
        // (0.4.68 full test: "Shear Wall Pier Design Overwrites - ACI 318-19 ... the table is as it was (0 rows)").
        var walls = m.AreaAssigns.Where(x => m.Area(x.Label)?.Kind == CsiProMax.Model.AreaKind.Wall).Select(x => x.Label + "@" + x.Story).ToList();
        if (walls.Count > 0) ctx.Tool("assign_pier_spandrel_data", """{"objects":[""" + Q(walls[0]) + """],"pier":"CPM-P1"}""", null, "tool assign_pier_spandrel_data, a pier on a wall");
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
        // The whole Material Property Data form (Ahmed 2026-11-26): nonlinear data by the .e2k's keywords, and damping and
        // notes as rows of the program's own tables, which each program must show after opening the Modeler's file.
        ctx.Tool("define_material_data", """{"name":"CPM-S355","type":"Steel","grade":"S355","values":{"WEIGHTPERVOLUME":77,"E":210000000,"U":0.3,"FY":355000,"FU":510000,"STRAINATHARDENING":0.02,"CPTENSION":0.06},"texts":{"HYSTYPE":"Kinematic","SSTYPE":"Simple"},"tables":{"Damping Parameters":[{"ModalRatio":"0.02"}],"General":[{"Notes":"CPM field test steel"}]}}""",
            _ => FieldContext.Expect(modeler.Model.Materials.Exists(m => m.Name == "CPM-S355" && m.Type == "Steel" && m.Tables is not null), "CPM-S355 is not a steel with its table rows in the model"));
        ctx.Refused("define_material_data", """{"name":"CPM-BAD","type":"Steel","values":{"E":200000000,"FY":355000,"FU":100}}""", "Fu at least Fy", "tool define_material_data says no to Fu below Fy");
        ctx.Tool("define_frame_shape", """{"name":"CPM-W","shape":"Steel I/Wide Flange","material":"CPM-S355","dims":{"D":0.5,"B":0.2,"TF":0.016,"TW":0.01},"modifiers":{"I3MOD":0.9},"extras":{"Color":"Cyan","Notes":"field check","FilletRad":"0.012"}}""", null, "tool define_frame_shape, a steel I with a modifier, colour, notes and fillet radius");
        ctx.Tool("section_properties", """{"shape":"Steel I/Wide Flange","dims":{"D":0.5,"B":0.2,"TF":0.016,"TW":0.01}}""", a =>
            FieldContext.Expect(Math.Abs(a["A"]!.GetValue<double>() - (2 * 0.2 * 0.016 + 0.468 * 0.01)) < 1e-9, "the area of a 500x200 I is not 2*200*16 + 468*10 mm2: " + a.ToJsonString()), "tool section_properties, a steel I's area");
        ctx.Tool("define_frame_shape", """{"name":"CPM-PIPE","shape":"Steel Pipe","material":"CPM-S355","dims":{"D":0.3,"TW":0.01}}""", null, "tool define_frame_shape, a pipe");
        ctx.Tool("define_frame_shape", """{"name":"CPM-ANGLE","shape":"Steel Angle","material":"CPM-S355","dims":{"D":0.1,"B":0.1,"TF":0.01,"TW":0.01}}""", null, "tool define_frame_shape, an angle");
        ctx.Refused("define_frame_shape", """{"name":"CPM-BAD","shape":"Steel Pipe","material":"CPM-S355","dims":{"D":0.3}}""", "needs TW", "tool define_frame_shape says no to a missing dimension");
        // The shapes the .e2k has no line for, kept by their ETABS table fields (FrameTableShapes); the ETABS part reads them back.
        var concTrap = modeler.Model.Materials.FirstOrDefault(m => m.IsConcrete)?.Name ?? "";
        ctx.Tool("define_frame_shape", """{"name":"CPM-TRAP","shape":"Concrete Trapezoid","material":""" + Q(concTrap) + ""","extras":{"t3":"0.6","t2":"0.4","t2b":"0.3","Color":"Red"}}""",
            _ => FieldContext.Expect(modeler.Model.FrameSections.Find(s => s.Name == "CPM-TRAP")?.Shape == "Concrete Trapezoid", "CPM-TRAP is not a trapezoid in the model"), "tool define_frame_shape, a concrete trapezoid by its table fields");
        ctx.Tool("define_frame_shape", """{"name":"CPM-NP","shape":"Nonprismatic","rows":[{"StartSect":"CPM-W","EndSect":"CPM-W","LengthType":"Proportional","Length":"1","EI33Var":"Parabolic","EI22Var":"Linear"},{"StartSect":"CPM-W","EndSect":"CPM-PIPE","LengthType":"Proportional","Length":"1","EI33Var":"Linear","EI22Var":"Linear"}]}""",
            _ => FieldContext.Expect(CsiProMax.Model.FrameTableShapes.Rows(modeler.Model.FrameSections.Find(s => s.Name == "CPM-NP")!).Count == 2, "CPM-NP does not have its 2 segments"), "tool define_frame_shape, a nonprismatic section of 2 segments");
        ctx.Tool("frame_section_to_sd", """{"name":"CPM-ANGLE","convert":false}""",
            _ => FieldContext.Expect(modeler.Model.FrameSections.Find(s => s.Name == "SDCPM-ANGLE")?.Shape == "SD Section", "Copy to SD Section made no SDCPM-ANGLE"), "tool frame_section_to_sd, Copy to SD Section");
        // ETABS's Define > Load Patterns (every auto seismic and wind code) and Define > Load Cases forms, as the forms set them;
        // the ETABS part reads every value back (EtabsFormRoundTrip.Check).
        EtabsFormRoundTrip.Define(ctx, modeler);
        // ETABS's Define > Load Combinations, Auto Construction Sequence Case, Walking Vibrations, Floor Vibrations, Table Named
        // Sets and Named Assignment Sets, as the forms set them; the ETABS part reads each back (EtabsDefineRoundTrip.Check).
        EtabsDefineRoundTrip.Define(ctx, modeler);
        // ETABS's Design > Live Load Reduction Factors, Steel Connection Design preferences and the design targets, as the
        // forms set them; the ETABS part reads the targets back (EtabsDesignRoundTrip.Check).
        EtabsDesignRoundTrip.Define(ctx, modeler);
        // ETABS's Detailing > Drawing Management > Drawing Format Properties and Drawing Sheet Setup, as the forms keep them
        // (the Engine's own tables: ETABS's .e2k and tables do not carry them); never put into the program's tables.
        if (modeler.Model.Target == CsiProMax.Model.ProgramOptions.Etabs)
        {
            ctx.Tool("define_table_rows", """{"table":"Engine - Detailing Drawing Format Properties","name":"Drawing Format Properties","rows":[{"Group":"General","Tab":"Line","Item":"Grid Lines","Color":"#ff0000","Thickness":"2.0-Double"},{"Group":"Beams","Tab":"Text","Item":"Beam Number","Size":"12","Bold":"No"}]}""",
                _ => FieldContext.Expect(modeler.Model.DefinitionTables?.GetValueOrDefault(CsiProMax.Model.EtabsDetailingTables.DrawingFormat)?.Count == 2, "the Drawing Format Properties form's 2 changed items are not in the model"),
                "Detailing > Drawing Format Properties, two items changed");
            ctx.Tool("define_table_rows", """{"table":"Engine - Detailing Drawing Sheet Setup","name":"Drawing Sheet Setup","rows":[{"SheetType":"ANSI Engineering","SheetSize":"E","SheetWidth":"40","SheetHeight":"30","MarginLeft":"1.5","Border":"Predefined","Position":"Right","TitleBlockSize":"4","SheetNoPrefix":"CPM-"}]}""",
                _ => FieldContext.Expect(modeler.Model.DefinitionTables?.GetValueOrDefault(CsiProMax.Model.EtabsDetailingTables.SheetSetup)?.FirstOrDefault()?.GetValueOrDefault("SheetNoPrefix") == "CPM-", "the Drawing Sheet Setup form's row is not in the model"),
                "Detailing > Drawing Sheet Setup");
        }
        var conc = modeler.Model.Materials.FirstOrDefault(m => m.IsConcrete)?.Name ?? "";
        ctx.Tool("define_shell_type", """{"name":"CPM-DROP","kind":"slab","slab_type":"Drop","modeling":"ShellThick","thickness":0.3,"material":""" + Q(conc) + ""","modifiers":{"M11MOD":0.25,"M22MOD":0.25}}""", null, "tool define_shell_type, a cracked drop panel");
        ctx.Tool("define_shell_type", """{"name":"CPM-RIB","kind":"slab","slab_type":"Ribbed","modeling":"ShellThin","thickness":0.1,"material":""" + Q(conc) + ""","overall_depth":0.4,"rib_width_top":0.15,"rib_width_bottom":0.125,"rib_spacing":0.9,"rib_direction":1}""", null, "tool define_shell_type, a ribbed slab");
        ctx.Refused("define_shell_type", """{"name":"CPM-RIB2","kind":"slab","slab_type":"Ribbed","thickness":0.1,"material":""" + Q(conc) + """}""", "ribs", "tool define_shell_type says no to a ribbed slab with no ribs");
        ctx.Tool("define_deck", """{"name":"CPM-DECK","deck_type":"Filled","slab_material":""" + Q(conc) + ""","deck_material":""" + Q(conc) + ""","slab_depth":0.0875,"rib_depth":0.075,"rib_width_top":0.175,"rib_width_bottom":0.125,"rib_spacing":0.3,"shear_thickness":0.001,"unit_weight":0.11,"stud_diameter":0.019,"stud_height":0.15,"stud_fu":400000,"extras":{"Notes":"field test"}}""", null, "tool define_deck, a filled deck");
        ctx.Refused("define_deck", """{"name":"CPM-DECK2","deck_type":"Corrugated"}""", "deck type", "tool define_deck says no to a deck type the programs do not have");
        // Analyze and Design forms with the program's own items (Ahmed 2026-11-27): a design code's preferences and
        // Set Active Degrees of Freedom, as rows of the program's tables, put into the program with the model.
        ctx.Tool("program_forms", "{}", null, "tool program_forms, the Analyze and Design menu forms");
        var (fDesign, fCode, fTable) = modeler.Model.Target switch
        {
            "sap2000" => ("steel", "AISC 360-16", "Preferences - Steel Design - AISC 360-16"),
            "safe" => ("slab", "ACI 318-19", "Concrete Slab Design Preferences - ACI 318-19"),
            _ => ("steel", "AISC 360-16", "Steel Frame Design Preferences - AISC 360-16"),
        };
        ctx.Tool("set_program_form", $$$"""{"design":{{{Q(fDesign)}}},"code":{{{Q(fCode)}}},"table":{{{Q(fTable)}}},"values":{"PatLLF":"0.75"}}""", null, "tool set_program_form, a design code and one of its preferences");
        var dofTable = CsiProMax.Model.ProgramForms.AnalysisForms(modeler.Model.Target).Select(f => f.Table).FirstOrDefault(t => t.Contains("Degrees of Freedom", StringComparison.Ordinal));
        if (dofTable is not null)
            ctx.Tool("set_program_form", $$$"""{"table":{{{Q(dofTable)}}},"values":{"UX":"Yes","UY":"Yes","UZ":"Yes","RX":"Yes","RY":"Yes","RZ":"Yes"}}""", null, "tool set_program_form, Set Active Degrees of Freedom");
        ctx.Refused("set_program_form", $$$"""{"table":{{{Q(fTable)}}},"values":{"No Such Item":"1"}}""", "no item", "tool set_program_form says no to an item the form does not have");
        // The Options menu's forms (Preferences, Colors) are forms of the same kind; a blank value goes back to the default.
        var tolTable = modeler.Model.Target == CsiProMax.Model.ProgramOptions.Sap2000 ? "Preferences - Dimensional" : "Preferences - Tolerance";
        ctx.Tool("set_program_form", $$$"""{"table":{{{Q(tolTable)}}},"values":{"Auto Merge Tolerance":"0.001"}}""", null, "tool set_program_form, Options > Preferences > Dimensions/Tolerances");
        ctx.Tool("set_program_form", $$$"""{"table":"Options - Colors - Display","values":{"Darkness":"0.5"}}""", null, "tool set_program_form, Options > Colors > Display");
        ctx.Tool("set_program_form", $$$"""{"table":"Options - Colors - Display","values":{"Darkness":""}}""", null, "tool set_program_form, a colour form back to the program's default");
        ctx.Tool("define_spring", """{"kind":"point","name":"CPM-PS","values":{"UX":1000,"UY":1000,"UZ":50000}}""", null, "tool define_spring, a point spring");
        ctx.Tool("define_spring", """{"kind":"area","name":"CPM-AS","values":{"U3":20000}}""", null, "tool define_spring, an area spring");
        ctx.Refused("define_spring", """{"kind":"line","name":"CPM-BAD","values":{"UX":5}}""", "not 'UX'", "tool define_spring says no to a value the kind does not have");
        var beam = modeler.Model.LineAssigns.FirstOrDefault(x => modeler.Model.Line(x.Label)?.Kind == CsiProMax.Model.LineKind.Beam);
        if (beam is not null)
        {
            var obj = Q(beam.Label + "@" + beam.Story);
            ctx.Tool("assign_frame_data", """{"objects":[""" + obj + """],"release_i":["M3"],"release_j":["M3"],"angle":0,"cardinal":8,"modifiers":{"I3MOD":0.35,"JMOD":0.01}}""", null, "tool assign_frame_data, pinned ends, top center and cracked");
            ctx.Refused("assign_frame_data", """{"objects":[""" + obj + """],"release_i":["T"],"release_j":["T"]}""", "unstable", "tool assign_frame_data says no to torsion released at both ends");
            // Releases/Partial Fixity springs on the released ends, and Insertion Point's mirror and joint offsets (Ahmed 2026-11-27).
            ctx.Tool("assign_frame_data", """{"objects":[""" + obj + """],"partial_fixity":{"M3I":5000,"M3J":5000}}""", null, "tool assign_frame_data, partial fixity springs on the released ends");
            ctx.Refused("assign_frame_data", """{"objects":[""" + obj + """],"partial_fixity":{"M2I":100}}""", "needs its release", "tool assign_frame_data says no to a partial fixity spring on an end not released");
            ctx.Tool("assign_frame_data", """{"objects":[""" + obj + """],"tables":{"Frame Assignments - Insertion Point":[{"Mirror2":"Yes","OffsetCSys":"Global","ZI":"0.05","ZJ":"0.05"}]}}""", null, "tool assign_frame_data, insertion point mirror and joint offsets");
            // Design > Concrete Frame Design > View/Revise Overwrites: the beam's row of the code's overwrite table (Ahmed 2026-11-27).
            var sapX = modeler.Model.Target == CsiProMax.Model.ProgramOptions.Sap2000;
            var cCode = CsiProMax.Model.ProgramForms.Code(modeler.Model, "concrete") is { Length: > 0 } cc ? cc : CsiProMax.Model.ProgramForms.Codes(modeler.Model.Target, "concrete").FirstOrDefault() ?? "";
            if (modeler.Model.Target != CsiProMax.Model.ProgramOptions.Safe && CsiProMax.Model.ProgramForms.OverwriteTables(modeler.Model.Target, "concrete", cCode).FirstOrDefault(t => !t.Contains("Column", StringComparison.Ordinal)) is { } owTable)
                ctx.Tool("assign_frame_data", """{"objects":[""" + obj + """],"tables":{""" + Q(owTable) + """:[{""" + Q(sapX ? "RLLF" : "LLRF") + """:"0.8"}]}}""", null, "tool assign_frame_data, a concrete beam's design overwrite (View/Revise Overwrites)");
            // Design > Shear Wall Design > View/Revise Pier Overwrites: a pier label's row for a story (ETABS only).
            var wCode = CsiProMax.Model.ProgramForms.Code(modeler.Model, "shear_wall") is { Length: > 0 } wc ? wc : CsiProMax.Model.ProgramForms.Codes(modeler.Model.Target, "shear_wall").FirstOrDefault() ?? "";
            // Only on a pier label the model has: ETABS takes an overwrite for its own piers only.
            if (modeler.Model.Target == CsiProMax.Model.ProgramOptions.Etabs && CsiProMax.Model.ProgramForms.OverwriteTables("etabs", "shear_wall", wCode).FirstOrDefault() is { } pierTable
                && PierOnWall(modeler.Model) is var (pierName, pierStory) && !string.IsNullOrEmpty(pierName))
                ctx.Tool("define_table_rows", """{"table":""" + Q(pierTable) + ""","name":""" + Q(pierName) + ""","rows":[{"Story":""" + Q(pierStory) + ""","LLRF":"0.9"}]}""", null, "tool define_table_rows, a pier's shear wall design overwrite (View/Revise Pier Overwrites)");
            ctx.Tool("assign_frame_data", """{"objects":[""" + obj + """],"hinges":[{"at":0.05,"dof":"M3"},{"at":0.95,"dof":"M3"},{"at":0.5,"dof":"V2"}]}""", null, "tool assign_frame_data, auto hinges at both ends and a shear hinge");
            ctx.Refused("assign_frame_data", """{"objects":[""" + obj + """],"hinges":[{"at":1.5,"dof":"M3"}]}""", "0 to 1", "tool assign_frame_data says no to a hinge beyond the member");
            ctx.Tool("property_definitions", """{"objects":[""" + obj + """]}""", a =>
                FieldContext.Expect(a["objects"]![0]!["hinges"]!.AsArray().Count == 3, "the beam does not show its 3 hinges: " + a["objects"]!.ToJsonString()), "tool property_definitions, the beam's hinges");
            // Assign > Frame items the .e2k has no keyword for, as rows of ETABS's own tables (Ahmed 2026-11-26: every button).
            ctx.Tool("assign_frame_data", """{"objects":[""" + obj + """],"tables":{"Frame Assignments - Additional Mass":[{"AddedMass":"0.05"}],"Frame Assignments - Output Stations":[{"StationOpt":"Min Stations","MinStations":"7"}]}}""",
                _ => FieldContext.Expect(CsiProMax.Model.AssignTables.Row(beam, "Frame Assignments - Additional Mass").GetValueOrDefault("AddedMass") == "0.05", "the beam's added mass is not kept"),
                "tool assign_frame_data, added mass and output stations by the program's assignment tables");
            ctx.Refused("assign_frame_data", """{"objects":[""" + obj + """],"tables":{"Frame Assignments - Additional Mass":[{"Mass":"1"}]}}""", "no field", "tool assign_frame_data says no to a field the table does not have");
        }
        var floor = modeler.Model.AreaAssigns.FirstOrDefault(x => modeler.Model.Area(x.Label)?.Kind == CsiProMax.Model.AreaKind.Floor);
        if (floor is not null)
        {
            ctx.Tool("assign_shell_data", """{"objects":[""" + Q(floor.Label + "@" + floor.Story) + """],"modifiers":{"M11MOD":0.25,"M22MOD":0.25}}""", null, "tool assign_shell_data, cracked slab modifiers");
            ctx.Tool("assign_shell_data", """{"objects":[""" + Q(floor.Label + "@" + floor.Story) + """],"tables":{"Area Assignments - Additional Mass":[{"AddedMass":"0.02"}]}}""", null, "tool assign_shell_data, added mass by the program's assignment table");
            // SAP2000's shell edge release (its own table, PR 207) and an auto edge constraint: SAP2000 resets an area's edge
            // constraints on every edge-release import, so the release goes in by the .s2k and the constraint after it (Ahmed
            // 2026-11-26: are they tested?). The SAP2000 part reads both back.
            ctx.Tool("assign_shell_data", """{"objects":[""" + Q(floor.Label + "@" + floor.Story) + """],"tables":{"Area Edge Release Assignments":[{"Edge":"1","ReleasedDOF":"Moment","StiffRot":"0"}],"Area Assignments - Auto Edge Constraints":[{"Constraint":"Yes"}]}}""",
                null, "tool assign_shell_data, a shell edge release and an auto edge constraint");
        }
        var support = modeler.Model.PointAssigns.FirstOrDefault(p => p.Restraint is not null);
        if (support is not null)
        {
            ctx.Tool("assign_joint_data", """{"objects":[""" + Q(support.Point + "@" + support.Story) + """],"spring":"CPM-PS"}""", null, "tool assign_joint_data, a spring under a support");
            ctx.Tool("assign_joint_data", """{"objects":[""" + Q(support.Point + "@" + support.Story) + """],"tables":{"Joint Assignments - Additional Mass":[{"MassXY":"1.5","MassZ":"0"}]}}""", null, "tool assign_joint_data, added mass by the program's assignment table");
            // SAP2000's joint panel zone (its own table, PR 207), at the top of the support's column.
            var above = modeler.Stories().Count > 1 ? modeler.Stories()[1].Name : support.Story;
            ctx.Tool("assign_joint_data", """{"objects":[""" + Q(support.Point + "@" + above) + """],"tables":{"Joint Panel Zone Assignments":[{"PZFrom":"Elastic","DoublerPl":"0","PZConnect":"Beams to Other Objects","AxesFrom":"Column"}]}}""",
                null, "tool assign_joint_data, a joint panel zone");
        }
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

    /// <summary>The program's File > Export items: the .e2k in the Consistent Units form's units, the database tables as
    /// text and XML, and a geometry file the page made; each a copy, the open model's file and units untouched.</summary>
    private static void FileExports(FieldContext ctx, OfflineEtabs modeler)
    {
        var path = modeler.Path; var units = modeler.Model.Units.Length;
        var e2k = Path.Combine(ctx.Folder, "CPM export check.e2k");
        ctx.Tool("export_e2k", $$"""{"path":{{Q(e2k)}},"length":"mm","force":"N","temperature":"C"}""", a =>
        {
            FieldContext.Expect(File.Exists(e2k) && File.ReadLines(e2k).Any(l => l.Contains("\"mm\"", StringComparison.OrdinalIgnoreCase) && l.TrimStart().StartsWith("UNITS", StringComparison.OrdinalIgnoreCase)), "the .e2k was not written in N, mm: " + a.ToJsonString());
            FieldContext.Expect(modeler.Path == path && modeler.Model.Units.Length == units, "the export changed the open model's file or units");
        }, "tool export_e2k writes the .e2k in the units asked, the model unchanged");
        var txt = Path.Combine(ctx.Folder, "CPM export check tables.txt");
        ctx.Tool("export_tables_file", $$"""{"path":{{Q(txt)}},"format":"text"}""", a =>
            FieldContext.Expect(File.Exists(txt) && CsiProMax.CsiTables.CsiTableText.Parse(File.ReadAllText(txt)).Count == a["tables"]!.AsArray().Count, "the text file does not hold every table: " + a.ToJsonString()), "tool export_tables_file writes the tables as text");
        var xml = Path.Combine(ctx.Folder, "CPM export check tables.xml");
        ctx.Tool("export_tables_file", $$"""{"path":{{Q(xml)}},"format":"xml"}""", a =>
            FieldContext.Expect(File.Exists(xml) && System.Xml.Linq.XDocument.Load(xml).Root!.Elements("Table").Count() == a["tables"]!.AsArray().Count, "the XML file does not hold every table: " + a.ToJsonString()), "tool export_tables_file writes the tables as XML");
        var obj = Path.Combine(ctx.Folder, "CPM export check.obj");
        ctx.Tool("write_export_file", $$"""{"path":{{Q(obj)}},"text":"v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"}""", a =>
            FieldContext.Expect(File.Exists(obj) && File.ReadAllText(obj).Contains("f 1 2 3", StringComparison.Ordinal), "the .obj was not written: " + a.ToJsonString()), "tool write_export_file writes the page's geometry file");
        ctx.Refused("write_export_file", $$"""{"path":{{Q(Path.Combine(ctx.Folder, "CPM export check.exe"))}},"text":"x"}""", "writes only", "tool write_export_file says no to a file type it does not write");
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

    /// <summary>
    /// The Modeler's own file has one extension per program (Ahmed 2026-11-27): each program's model saves its own
    /// (.cpme, .cpms, .cpmf), a file saved before that (.cpm) still opens with its program and the next save writes the
    /// new extension beside it without removing the old file, and a file of another program than the window's is
    /// recognised before anything is replaced. Each save is under a name of its own, so the scratch model's files stay.
    /// </summary>
    private static void FileTypes(FieldContext ctx, OfflineEtabs modeler)
    {
        var folder = Q(ctx.Folder);
        var was = modeler.Model.Target;
        var own = new Dictionary<string, string>();
        foreach (var key in CsiProMax.Model.StructuralModel.Targets)
        {
            var ext = CsiProMax.Model.ModelFile.ExtensionFor(key);
            ctx.Tool("set_model_program", $$"""{"program":{{Q(key)}}}""", step: $"tool set_model_program makes the model for {CsiProMax.Model.StructuralModel.TargetName(key)}");
            ctx.Tool("save_model_as", $$"""{"name":{{Q(ModelName + " " + key)}},"folder":{{folder}}}""", a =>
            {
                var file = a["modeler_file"]!.GetValue<string>();
                FieldContext.Expect(file.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && File.Exists(file), $"the {CsiProMax.Model.StructuralModel.TargetName(key)} model's own file is {file}, not a {ext} that was written");
                foreach (var other in CsiProMax.Model.ModelFile.Extensions.Where(x => x != ext))
                    FieldContext.Expect(!File.Exists(Path.ChangeExtension(file, other)), $"a {other} was written beside the {ext} too");
                own[key] = file;
            }, $"tool save_model_as writes a {CsiProMax.Model.StructuralModel.TargetName(key)} model as {ext}");
            if (own.TryGetValue(key, out var saved))
                ctx.Tool("open_model_file", $$"""{"path":{{Q(saved)}}}""", a =>
                {
                    Same(ctx, a, "the " + ext);
                    FieldContext.Expect(modeler.Model.Target == key, $"the {ext} opened as a model for {modeler.Model.Target}");
                }, $"tool open_model_file opens the {ext} for {CsiProMax.Model.StructuralModel.TargetName(key)}");
        }

        // An old .cpm, of SAP2000, written as the versions before wrote it.
        var old = Path.Combine(ctx.Folder, ModelName + " old" + CsiProMax.Model.ModelFile.Legacy);
        ctx.Step("write a Modeler file of the old kind (.cpm) for SAP2000", () =>
        {
            var m = CsiProMax.Model.ModelFile.FromJson(CsiProMax.Model.ModelFile.ToJson(modeler.Model));
            m.Target = "sap2000";
            CsiProMax.Model.ModelFile.Save(m, old);
        });
        if (File.Exists(old))
        {
            ctx.Tool("open_model_file", $$"""{"path":{{Q(old)}}}""", a =>
            {
                Same(ctx, a, "the old .cpm");
                FieldContext.Expect(modeler.Model.Target == "sap2000", $"the old .cpm of a SAP2000 model opened for {modeler.Model.Target}");
            }, "tool open_model_file opens an old .cpm, for the program it holds");
            ctx.Tool("save_model", "{}", _ =>
            {
                var now = Path.ChangeExtension(old, CsiProMax.Model.ModelFile.Sap2000);
                FieldContext.Expect(File.Exists(now), "saving the model opened from the old .cpm did not write a .cpms beside it");
                FieldContext.Expect(File.Exists(old), "the old .cpm was removed by the save");
            }, "tool save_model writes the new .cpms beside the old .cpm, and keeps the old file");
        }

        // A SAP2000 file in a window that works in ETABS: recognised, nothing replaced until the person chooses.
        if (own.TryGetValue("sap2000", out var sap))
        {
            ctx.Tool("open_model_file", $$"""{"path":{{Q(own["etabs"])}}}""", step: "tool open_model_file opens the ETABS model again"); // ETABS itself
            var revision = modeler.Revision;
            ctx.Tool("open_model_file", $$"""{"path":{{Q(sap)}},"for_program":"etabs"}""", a =>
            {
                FieldContext.Expect(a["needs_choice"]?.GetValue<bool>() == true && a["file_program"]?.GetValue<string>() == "SAP2000", "a SAP2000 file was not recognised in an ETABS window: " + a.ToJsonString()); // ETABS itself
                FieldContext.Expect(modeler.Revision == revision && modeler.Model.Target == "etabs", "the model was replaced before the person chose");
            }, "tool open_model_file recognises a SAP2000 file in an ETABS window and asks first"); // ETABS itself
            ctx.Tool("open_model_file", $$"""{"path":{{Q(sap)}},"for_program":"etabs","convert":true}""", a =>
            {
                Same(ctx, a, "the .cpms");
                FieldContext.Expect(modeler.Model.Target == "etabs" && a["converted_from"]?.GetValue<string>() == "SAP2000", "the SAP2000 file was not converted to an ETABS model: " + a.ToJsonString()); // ETABS itself
            }, "tool open_model_file converts it to ETABS when the person says so"); // ETABS itself
            ctx.Tool("open_model_file", $$"""{"path":{{Q(sap)}},"for_program":"etabs","convert":false}""", a =>
                FieldContext.Expect(modeler.Model.Target == "sap2000", "the SAP2000 file did not keep its program"), "tool open_model_file keeps the file's own program when the person says so");
        }
        ctx.Tool("set_model_program", $$"""{"program":{{Q(was.Length > 0 ? was : "etabs")}}}""", step: "tool set_model_program puts the model's program back");
    }

    /// <summary>The value of a load as the Modeler keeps it (the number after FVAL); 0 when it has none.</summary>
    private static double Fval(CsiProMax.Model.ObjectLoad load) =>
        CsiProMax.E2k.E2kText.TryNumber(CsiProMax.E2k.E2kText.Value(load.Tokens, "FVAL"), out var value) ? value : 0;

    // Two .e2k texts the same but for the line with the time they were written.
    private static bool Same(string a, string b)
    {
        static string Body(string t) => string.Join("\n", t.Split('\n').Where(l => !l.StartsWith("$ File", StringComparison.Ordinal)));
        return Body(a) == Body(b);
    }
}
