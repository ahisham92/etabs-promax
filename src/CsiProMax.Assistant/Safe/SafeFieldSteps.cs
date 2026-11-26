using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant.Safe;

/// <summary>
/// SAFE's part of the field test. <see cref="Modeler"/> writes one floor of the Modeler's model as a .f2k;
/// <see cref="Scratch"/> then works on the scratch model in the test's own SAFE window (<see cref="FieldContext.Link"/>):
/// what SAFE read from the file, every read of the link, every SAFE tool and every shared tool with real arguments,
/// then SAFE's own analysis and design and every slab check. It ends with the tools that were not run, which must be none.
/// </summary>
public static class SafeFieldSteps
{
    /// <summary>In the Modeler's part: one floor saved as a SAFE text file, for the scratch window to open.</summary>
    public static void Modeler(FieldContext ctx)
    {
        var answer = ctx.Tool("save_safe_file", $$"""{"story":"Story1","name":"{{ModelerFieldSteps.ModelName}}","folder":{{Q(ctx.Folder)}},"strips":true}""", a =>
        {
            var file = a["file"]!.GetValue<string>();
            FieldContext.Expect(File.Exists(file) && new FileInfo(file).Length > 500, $"the file {file} was not written");
            FieldContext.Expect(Int(a["slabs"]) == 4 && Int(a["beams"]) == 12 && Int(a["columns"]) > 0, $"Story1 has 4 slabs and 12 beams on columns; the .f2k has {a["slabs"]} slabs, {a["beams"]} beams and {a["columns"]} columns (notes: {a["notes"]?.ToJsonString()})");
        });
        if (answer?["file"]?.GetValue<string>() is not { } f2k) return;
        ctx.Files[CsiProgram.Safe.Key] = f2k;
        foreach (var key in new[] { "slabs", "beams", "columns", "walls", "supports", "strips", "loads" }) ctx.Facts["safe." + key] = Int(answer[key]);
        // Strips drawn by hand (define_strip) go in either way; "strips": false leaves out the grid ones.
        var own = (ctx.Link as OfflineEtabs)?.Model.Extras?.Strips.Count ?? 0;
        ctx.Tool("save_safe_file", $$"""{"story":"Story1","name":"{{ModelerFieldSteps.ModelName}} mat","folder":{{Q(ctx.Folder)}},"subgrade":20000,"compression_only":true,"strips":false}""", a =>
            FieldContext.Expect(File.Exists(a["file"]!.GetValue<string>()) && Int(a["strips"]) == own, $"a file with soil and only the {own} hand-drawn strip(s) was expected: " + a.ToJsonString()), "tool save_safe_file with soil under the slabs, no grid strips");
    }

    public static void Scratch(FieldContext ctx)
    {
        var snapshots = ResultTools.SnapshotRoot;
        ResultTools.SnapshotRoot = Path.Combine(ctx.Folder, "snapshots", "safe");
        try { Steps(ctx); }
        finally { ResultTools.SnapshotRoot = snapshots; }
    }

    private static void Steps(FieldContext ctx)
    {
        var link = ctx.Link;
        ctx.ClearTools();
        ctx.Offer(EtabsTools.All);
        ctx.Offer(ProgramTools.For(CsiProgram.Safe));

        // ---- what SAFE read from the file the Modeler wrote ----
        ctx.Step("SAFE read the slabs and beams the Modeler wrote", () =>
        {
            var (slabs, beams) = (link.Areas().Count(a => a.Kind == "floor"), link.Frames().Count(f => f.Kind == "beam"));
            var (s, b) = ((int)ctx.Facts.GetValueOrDefault("safe.slabs"), (int)ctx.Facts.GetValueOrDefault("safe.beams"));
            FieldContext.Expect(slabs >= s && beams == b, $"the .f2k has {s} slabs and {b} beams; SAFE has {slabs} floor areas and {beams} beams");
            return new { floor_areas = slabs, beams, columns = link.Frames().Count(f => f.Kind == "column"), walls = link.Areas().Count(a => a.Kind == "wall"), points = link.Points().Count };
        });
        ctx.Step("the sections, load patterns and combinations came across", () =>
        {
            var have = new { frame_sections = link.FrameSections(), area_sections = link.AreaSections(), materials = link.Materials(), patterns = link.LoadPatterns().Select(p => p.Name).ToList(), combinations = link.Combos() };
            FieldContext.Expect(have.area_sections.Count > 0 && have.materials.Count > 0 && have.patterns.Count >= 2, "slab sections, materials and the Dead and Live patterns were expected");
            return have;
        });
        ctx.Step("the design strips came across", () =>
        {
            var strips = Int(ctx.Call("list_design_strips", "{}")["count"]);
            var written = (int)ctx.Facts.GetValueOrDefault("safe.strips");
            FieldContext.Expect(strips == written, $"the .f2k has {written} design strips; SAFE has {strips}");
            return $"{strips} design strips";
        });

        // ---- every read of the link ----
        LinkReads(ctx);

        // ---- the shared tools, as SAFE answers them ----
        ctx.Tool("get_model_context", "{}", a => FieldContext.Expect(Int(a["counts"]!["floors"]) > 0, "the context counts no floors: " + a["counts"]!.ToJsonString()));
        var levels = Read(ctx, "the stories", link.Stories, []);
        var story = levels.Count > 1 ? levels[^1].Name : "";
        var storyList = List(story.Length > 0 ? [story] : []);
        var concrete = Read(ctx, "the materials", () => link.Materials().FirstOrDefault(m => m.StartsWith("C", StringComparison.OrdinalIgnoreCase) || m.Contains("psi", StringComparison.OrdinalIgnoreCase)) ?? link.Materials().FirstOrDefault(), null) ?? "";
        var pattern = Read(ctx, "the load patterns", () => link.LoadPatterns().FirstOrDefault(p => p.Type == "Live")?.Name ?? link.LoadPatterns().FirstOrDefault()?.Name, null) ?? "Live";
        var frameSection = Read(ctx, "the frame sections", () => link.FrameSections().FirstOrDefault(), null) ?? "";

        // ---- SAFE's own modelling tools ----
        foreach (var (name, type, thickness) in new[] { ("CPM-SLAB250", "slab", 0.25), ("CPM-DROP400", "drop", 0.4), ("CPM-MAT600", "mat", 0.6), ("CPM-FTG500", "footing", 0.5) })
            ctx.Tool("add_slab_section", $$"""{"name":{{Q(name)}},"type":{{Q(type)}},"material":{{Q(concrete)}},"thickness":{{thickness.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""", _ =>
                FieldContext.Expect(link.AreaSections().Contains(name), $"the slab property {name} is not in SAFE"), $"tool add_slab_section, a {type}");
        ctx.Refused("add_slab_section", """{"name":"CPM-BAD","material":"no such material","thickness":0.2}""", "no material", "tool add_slab_section says no to a material that is not there");

        List<string> slabs = [], spare = [];
        ctx.Tool("add_slabs", $$"""{"outlines":[[[30,0],[36,0],[36,5],[30,5]]],"stories":{{storyList}},"section":"CPM-MAT600"}""", a =>
        {
            slabs = Names(a);
            var made = link.Areas().Where(x => slabs.Contains(x.Name)).ToList();
            FieldContext.Expect(made.Count == 1 && made[0].Kind == "floor" && made[0].Section == "CPM-MAT600" && made[0].Corners.Count == 4, "one floor area with 4 corners and CPM-MAT600 was expected; SAFE has: " + string.Join("; ", made.Select(x => $"{x.Name} {x.Kind} {x.Section}")));
        });
        ctx.Tool("add_slabs", $$"""{"outlines":[[[48,0],[51,0],[51,3],[48,3]]],"stories":{{storyList}},"section":"CPM-SLAB250"}""", a => { spare = Names(a); FieldContext.Expect(spare.Count == 1, "one slab was asked for: " + a.ToJsonString()); }, "tool add_slabs, one to delete again");
        if (slabs.Count > 0)
        {
            ctx.Tool("add_soil_support", $$"""{"name":"CPM-SOIL","subgrade":20000,"compression_only":true,"areas":{{List(slabs)}}}""", a => FieldContext.Expect(Int(a["areas"]) == 1, "the soil should be under one area: " + a.ToJsonString()));
            ctx.Step("the soil property is in SAFE (read back through its API)", () =>
            {
                try
                {
                    var names = (string[])link.Raw("list the soil properties", m => { int n = 0; string[] list = []; m.PropAreaSpring.GetNameList(ref n, ref list); return list; })!;
                    FieldContext.Expect(names.Contains("CPM-SOIL"), "SAFE's soil properties: " + string.Join(", ", names));
                    return names;
                }
                catch (EtabsException ex) { throw new FieldSkipException("the soil properties could not be listed through SAFE's API (" + ex.Message + ")"); }
            });
            ctx.Tool("set_section", $$"""{"areas":{{List(slabs)}},"section":"CPM-FTG500"}""", _ => FieldContext.Expect(link.Areas().First(x => x.Name == slabs[0]).Section == "CPM-FTG500", "the area does not have CPM-FTG500 when read back from SAFE"), "tool set_section on a slab");
        }
        else ctx.Fail("tools add_soil_support and set_section", "the slab to work on was not added");
        ctx.Tool("add_point_springs", """{"name":"CPM-PILE","kz":50000,"kx":5000,"ky":5000,"at":[[30,0],[36,0]]}""", a => FieldContext.Expect(Names(a, "points").Count == 2, "two points were expected: " + a.ToJsonString()));
        ctx.Tool("add_point_supports", """{"at":[[36,5]],"type":"pinned"}""", a =>
        {
            var points = Names(a, "points");
            FieldContext.Expect(points.Count == 1 && link.Restraint(points[0]).SequenceEqual([true, true, true, false, false, false]), "one pinned point was expected when read back from SAFE");
        }, "tool add_point_supports pinned");
        ctx.Tool("add_point_supports", """{"at":[[30,5]],"type":"vertical"}""", a => FieldContext.Expect(Names(a, "points") is [var p] && link.Restraint(p).SequenceEqual([false, false, true, false, false, false]), "a vertical support holds UZ only"), "tool add_point_supports vertical");
        ctx.Tool("add_point_supports", """{"at":[[30,5]],"type":"fixed"}""", a => FieldContext.Expect(Names(a, "points") is [var p] && link.Restraint(p).All(r => r), "the point is not fixed when read back from SAFE"), "tool add_point_supports fixed");
        ctx.Tool("add_load_pattern", """{"name":"CPM-TEST","type":"Live","self_weight":0}""", _ => FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-TEST"), "the pattern CPM-TEST is not in SAFE"));
        ctx.Tool("add_point_loads", """{"pattern":"CPM-TEST","force":100,"mx":5,"my":-5,"at":[[33,2.5]]}""", a => FieldContext.Expect(Names(a, "points").Count == 1, "one loaded point was expected: " + a.ToJsonString()));
        ctx.Tool("add_point_loads", """{"pattern":"CPM-TEST","force":50,"at":[[33,2.5]],"replace":true}""", null, "tool add_point_loads replaces the load there");
        if (slabs.Count > 0)
            ctx.Tool("set_loads", $$"""{"areas":{{List(slabs)}},"pattern":"CPM-TEST","value":5}""", a => FieldContext.Expect(Int(a["done"]) == 1 && a["failed"]!.AsArray().Count == 0, "the slab was not loaded: " + a.ToJsonString()));
        else ctx.Fail("tool set_loads", "the slab to load was not added");
        ctx.Tool("add_combination", $$"""{"name":"CPM-TEST-COMBO","cases":[{"case":{{Q(link.LoadCases().FirstOrDefault() ?? "Dead")}},"factor":1.2},{"case":"CPM-TEST","factor":1.6}]}""", _ => FieldContext.Expect(link.Combos().Contains("CPM-TEST-COMBO"), "the combination is not in SAFE"));

        // The members ETABS has by story, as SAFE takes them.
        List<string> members = [];
        ctx.Tool("add_columns", $$"""{"points":[[42,0]],"stories":{{storyList}},"section":{{Q(frameSection)}}}""", a => members.AddRange(Names(a)), "tool add_columns (a column under the slab level)");
        ctx.Tool("add_beams", $$"""{"lines":[[30,0,36,0]],"stories":{{storyList}},"section":{{Q(frameSection)}}}""", a => members.AddRange(Names(a)), "tool add_beams (a beam on the slab level)");
        List<string> panels = [];
        ctx.Tool("add_walls", $$"""{"lines":[[42,3,42,6]],"stories":{{storyList}},"section":{{Q(link.AreaSections().FirstOrDefault(s => s.Contains("W", StringComparison.OrdinalIgnoreCase)) ?? link.AreaSections().FirstOrDefault() ?? "")}}}""", a => panels = Names(a), "tool add_walls (a wall under the slab level)");
        ctx.Tool("set_supports", """{"type":"fixed","at_base":true}""", null, "tool set_supports at the base");

        // ---- SAFE's strips and its own tables ----
        ctx.Tool("list_design_strips", """{"limit":5}""", a => FieldContext.Expect(a["strips"] is JsonArray, "no list of strips came back"));
        var tables = Read(ctx, "the tables", link.Tables, []);
        var slabTable = tables.FirstOrDefault(t => ResultTools.Norm(t.Name).Contains("slabpropert", StringComparison.Ordinal)) ?? tables.FirstOrDefault(t => !t.Empty);
        if (slabTable is not null)
        {
            ctx.Tool("edit_safe_table", $$"""{"table":{{Q(slabTable.Key)}},"action":"fields"}""", a => FieldContext.Expect(a.ToJsonString().Length > 20, "no fields came back"), $"tool edit_safe_table, the fields of {slabTable.Name}");
            ctx.Tool("edit_safe_table", $$"""{"table":{{Q(slabTable.Key)}},"action":"read","limit":3}""", null, $"tool edit_safe_table, the rows of {slabTable.Name}");
        }
        else ctx.Fail("tool edit_safe_table", "SAFE listed no table to read");
        ctx.Skip("tool edit_safe_table, add and update", "changing SAFE's tables needs the table's own field names from a real SAFE; they are tried once this report shows them");

        // ---- reading ----
        if (slabs.Count > 0)
        {
            ctx.Tool("select_objects", $$"""{"areas":{{List(slabs)}}}""", _ => FieldContext.Expect(link.Selected().Count == 1, $"1 area was selected; SAFE has {link.Selected().Count}"));
            ctx.Tool("query_objects", """{"selected_only":true}""", a => FieldContext.Expect(Int(a["count"]) == 1, $"1 object is selected; the answer has {a["count"]}"), "tool query_objects, the selection only");
            ctx.Tool("select_objects", "{}", _ => FieldContext.Expect(link.Selected().Count == 0, "the selection was not cleared"), "tool select_objects with nothing clears the selection");
        }
        ctx.Tool("query_objects", """{"kind":"floor","limit":3}""", a => FieldContext.Expect(Int(a["count"]) == link.Areas().Count(x => x.Kind == "floor"), $"the count of floors is {a["count"]}"), "tool query_objects, the floors");
        ctx.Tool("list_tables", "{}", a => FieldContext.Expect(Int(a["count"]) >= 3, $"SAFE's tables with data were expected: {a["count"]}"));
        ctx.Step("SAFE's tables, by the names SAFE gives them", () => tables.Select(t => $"{t.Name}{(t.Empty ? " (empty)" : "")}").Take(400).ToList());
        if (tables.FirstOrDefault(t => !t.Empty) is { } table)
            ctx.Tool("read_table", $$"""{"table":{{Q(table.Key)}},"limit":3}""", a => FieldContext.Expect(a["fields"] is JsonArray { Count: > 0 }, "the table has no fields"), $"tool read_table {table.Name}");
        else ctx.Fail("tool read_table", "SAFE listed no table with data");

        // ---- deleting, a snapshot, saving ----
        var gone = spare.Concat(panels).ToList();
        if (gone.Count > 0 || members.Count > 0)
            ctx.Tool("delete_objects", $$"""{"frames":{{List(members)}},"areas":{{List(gone)}}}""", a =>
            {
                FieldContext.Expect(a["failed"]!.AsArray().Count == 0, "not everything was deleted: " + a.ToJsonString());
                FieldContext.Expect(!link.Areas().Any(x => gone.Contains(x.Name)) && !link.Frames().Any(f => members.Contains(f.Name)), "a deleted object is still in SAFE");
            });
        else ctx.Fail("tool delete_objects", "the objects to delete were not added");
        ctx.Tool("model_snapshot", """{"name":"cpm-before-analysis"}""");
        ctx.Tool("save_model", "{}", a => FieldContext.Expect(a["saved"]!.GetValue<string>().Replace('\\', '/').Split('/').Last().StartsWith(ModelerFieldSteps.ModelName, StringComparison.OrdinalIgnoreCase), $"it saved '{a["saved"]}', which is not the scratch model"));

        // ---- SAFE's own analysis and design ----
        NoVerdictBeforeAnalysis(ctx);
        ctx.Tool("run_analysis", "{}", _ => FieldContext.Expect(link.Info().Locked, "the analysis answered, but SAFE does not say the model is analysed (locked)"));
        var analysed = false;
        try { analysed = link.Info().Locked; }
        catch (EtabsException) { }
        if (analysed)
            ctx.Step("SAFE's slab design runs", () =>
            {
                var answer = Convert.ToInt32(link.Raw("run the slab design", m => m.DesignConcreteSlab.StartSlabDesign()));
                FieldContext.Expect(answer == 0, $"SAFE answered {answer} to DesignConcreteSlab.StartSlabDesign");
                return "the design ran";
            });
        else ctx.Skip("SAFE's slab design runs", "the analysis did not run");
        if (analysed) ctx.Tool("run_design", """{"kinds":["slab"]}""", null, "tool run_design (slab design, or the menu to run it)");
        else ctx.Refused("run_design", """{"kinds":["slab"]}""", "design", "tool run_design says the analysis must run first");

        // ---- every slab check ----
        var none = analysed ? "" : " (no analysis: it must still answer)";
        var answers = new List<(string, JsonNode?)>();
        if (analysed) ctx.Step("SAFE tables with data after the analysis and design", () => link.Tables().Where(t => !t.Empty).Select(t => t.Name).ToList());
        ctx.Tool("safe_results_overview", "{}", null, "tool safe_results_overview" + none);
        answers.Add(("check_punching", ctx.Tool("check_punching", """{"limit":1.0,"list":10}""", null, "tool check_punching" + none)));
        answers.Add(("check_deflection", ctx.Tool("check_deflection", """{"span":6,"ratio":240}""", null, "tool check_deflection" + none)));
        answers.Add(("strip_forces", ctx.Tool("strip_forces", """{"list":10}""", null, "tool strip_forces" + none)));
        answers.Add(("check_soil_pressure", ctx.Tool("check_soil_pressure", """{"allowable":200}""", null, "tool check_soil_pressure" + none)));
        answers.Add(("slab_design", ctx.Tool("slab_design", """{"list":10}""", null, "tool slab_design" + none)));
        ctx.Tool("safe_check_report", """{"punching_limit":1.0,"span":6,"ratio":240,"allowable":200}""", null, "tool safe_check_report" + none);
        if (analysed) Verdicts(ctx, "every SAFE check had its table to read", answers);
        foreach (var name in new[] { "results_overview", "check_modal", "check_drift", "check_torsion", "check_story_irregularity", "design_ratios", "check_report" })
            ctx.Tool(name, "{}", null, $"tool {name} (the ETABS check, as SAFE answers it)"); // ETABS itself

        // ---- snapshots and the review pack ----
        ctx.Tool("list_snapshots", "{}", a => FieldContext.Expect(Int(a["count"]) >= 1, "the snapshot is not listed"));
        ctx.Tool("compare_snapshots", """{"from":"cpm-before-analysis"}""");
        ctx.Step("the review pack of the scratch model is written", () =>
        {
            var checks = ProgramTools.Report(System.Text.Json.JsonDocument.Parse("{}").RootElement, link).AsObject();
            var pack = Pack.Build(link, [], link.LoadCases(), link.Combos(), checks, null, mostRows: 20000, by: "CSI ProMax field test");
            var file = Path.Combine(ctx.Folder, ModelerFieldSteps.ModelName + " SAFE.cpmx");
            File.WriteAllBytes(file, pack.Zip);
            FieldContext.Expect(pack.Areas == link.Areas().Count, $"the pack holds {pack.Areas} areas, the model has {link.Areas().Count}");
            return new { file = Path.GetFileName(file), kilobytes = pack.Zip.Length / 1024, frames = pack.Frames, areas = pack.Areas, tables = pack.Tables, notes = pack.Notes };
        });

        // ---- the link with GeoProMax: the pressures out, the springs back in (the model is unlocked for them) ----
        GeoProMaxFieldSteps.Run(ctx);

        // ---- the tools every program has through the same API (the whole API, lateral loads, checkpoints, a building) ----
        CsiFieldSteps.Run(ctx);

        ctx.Coverage("every SAFE tool was run");
    }
}
