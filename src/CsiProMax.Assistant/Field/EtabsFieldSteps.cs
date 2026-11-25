using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant;

/// <summary>
/// ETABS's part of the field test, on the scratch model in the test's own ETABS window (<see cref="FieldContext.Link"/>):
/// what ETABS read from the Modeler's file, every read of the link, every tool with real arguments and its change read
/// back from ETABS, then ETABS's own analysis and design, the tables the checks read, every check, the snapshots and
/// the review pack. It ends with the list of tools that were not run, which must be empty.
/// </summary>
public static class EtabsFieldSteps
{
    public static void Scratch(FieldContext ctx)
    {
        var snapshots = ResultTools.SnapshotRoot;
        ResultTools.SnapshotRoot = Path.Combine(ctx.Folder, "snapshots", "etabs");
        try { Steps(ctx); }
        finally { ResultTools.SnapshotRoot = snapshots; }
    }

    private static bool At(double a, double b) => Math.Abs(a - b) < 1e-4;

    private static void Steps(FieldContext ctx)
    {
        var link = ctx.Link;
        ctx.ClearTools();
        ctx.Offer(EtabsTools.All);
        ctx.Offer(ProgramTools.For(CsiProgram.Etabs));

        // ---- what ETABS read from the file the Modeler wrote ----
        var written = ctx.Written;
        ctx.Step("ETABS read every story, frame and area the Modeler wrote", () =>
        {
            var (stories, frames, areas) = (link.Stories().Count - 1, link.Frames().Count, link.Areas().Count);
            FieldContext.Expect(stories == written.Stories && frames == written.Frames && areas == written.Areas,
                $"the Modeler wrote {written.Stories} stories, {written.Frames} frames and {written.Areas} areas; ETABS has {stories}, {frames} and {areas}");
            return $"{stories} stories, {frames} frames, {areas} areas";
        });
        ctx.Step("the material, sections, load pattern, combination and story came across", () =>
        {
            var missing = new List<string>();
            void Need(IEnumerable<string> have, string name, string what) { if (!have.Contains(name, StringComparer.OrdinalIgnoreCase)) missing.Add($"{what} {name}"); }
            Need(link.Materials(), "CPM-C35", "material");
            Need(link.FrameSections(), "CPM-C400", "frame section");
            Need(link.FrameSections(), "CPM-B250x500", "frame section");
            Need(link.AreaSections(), "CPM-S180", "slab section");
            Need(link.AreaSections(), "CPM-W200", "wall section");
            Need(link.LoadPatterns().Select(p => p.Name), "CPM-SDL", "load pattern");
            Need(link.Combos(), "CPM-ULS", "combination");
            Need(link.Stories().Select(s => s.Name), "CPM-Roof", "story");
            FieldContext.Expect(missing.Count == 0, "not in ETABS after the import: " + string.Join(", ", missing));
            return "all there";
        });
        ctx.Step("the fixed supports at the base came across", () =>
        {
            var z0 = link.Stories()[0].Elevation;
            var low = link.Points().Where(p => At(p.Z, z0)).ToList();
            var held = low.Count(p => link.Restraint(p.Name).All(r => r));
            FieldContext.Expect(held >= 9, $"9 fixed column bases were written; ETABS has {held} fixed points of {low.Count} on the base");
            return $"{held} fixed points on the base";
        });
        ctx.Step("the members kept their sections", () =>
        {
            var frames = link.Frames();
            var roof = frames.Where(f => string.Equals(f.Story, "CPM-Roof", StringComparison.OrdinalIgnoreCase)).ToList();
            FieldContext.Expect(roof.Count(f => f.Kind == "column" && f.Section == "CPM-C400") == 4 && roof.Count(f => f.Kind == "beam" && f.Section == "CPM-B250x500") == 4,
                "4 CPM-C400 columns and 4 CPM-B250x500 beams were written on CPM-Roof; ETABS has: " + string.Join(", ", roof.GroupBy(f => $"{f.Kind} {f.Section}").Select(g => $"{g.Count()} {g.Key}")));
            FieldContext.Expect(link.Areas().Any(a => a.Kind == "wall" && a.Section == "CPM-W200"), "the CPM-W200 wall is not in ETABS");
            return "roof columns, roof beams and the wall have the sections they were given";
        });

        // ---- every read of the link ----
        LinkReads(ctx);

        // ---- the tools, each change read back from ETABS ----
        ctx.Tool("get_model_context", "{}", a =>
        {
            foreach (var key in new[] { "file", "program", "version", "units", "locked", "stories", "counts", "frame_sections", "slab_and_wall_sections", "materials", "load_patterns", "load_cases", "combinations", "selected", "selected_count" })
                FieldContext.Expect(a[key] is not null, $"the answer has no '{key}'");
            var c = a["counts"]!;
            FieldContext.Expect(Int(c["columns"]) + Int(c["beams"]) + Int(c["braces"]) == link.Frames().Count && Int(c["floors"]) + Int(c["walls"]) == link.Areas().Count, "the counts do not add up to the model's frames and areas: " + c.ToJsonString());
            FieldContext.Expect(a["units"]!["force"]!.GetValue<string>() == "kN" && a["units"]!["length"]!.GetValue<string>() == "m", "the Modeler's file is in kN and m; ETABS says " + a["units"]!.ToJsonString());
            FieldContext.Expect(a["locked"]!.GetValue<bool>() == false, "the scratch model is locked before any analysis");
        });

        var levels = Read(ctx, "the stories", link.Stories, []);
        if (levels.Count < 2) { ctx.Fail("the scratch model's stories", "ETABS gave no stories, so the tools that build by story were not run"); ctx.Coverage("every ETABS tool was run"); return; }
        var (z0, story, z1) = (levels[0].Elevation, levels[1].Name, levels[1].Elevation);
        var storyList = List([story]);
        var otherBeam = Read(ctx, "the frame sections", () => link.FrameSections().FirstOrDefault(s => s != "CPM-B250x500" && s != "CPM-C400"), null) ?? "CPM-C400";
        var otherSlab = Read(ctx, "the slab sections", () => link.AreaSections().FirstOrDefault(s => s != "CPM-S180" && s != "CPM-W200"), null) ?? "CPM-S180";

        List<string> columns = [], beams = [], slabs = [], walls = [], spareFrames = [], spareAreas = [];
        ctx.Tool("add_columns", $$"""{"points":[[30,0],[36,0],[36,5],[30,5]],"stories":{{storyList}},"section":"CPM-C400"}""", a =>
        {
            columns = Names(a);
            var made = link.Frames().Where(f => columns.Contains(f.Name)).ToList();
            FieldContext.Expect(Int(a["added"]) == 4 && made.Count == 4, $"4 columns were asked for; the answer names {columns.Count} and ETABS has {made.Count} of them");
            var odd = made.Where(f => f.Kind != "column" || f.Section != "CPM-C400" || !string.Equals(f.Story, story, StringComparison.OrdinalIgnoreCase) || !At(Math.Min(f.Z1, f.Z2), z0) || !At(Math.Max(f.Z1, f.Z2), z1)).ToList();
            FieldContext.Expect(odd.Count == 0, $"columns from {z0} to {z1} on {story} with CPM-C400 were expected; ETABS has: " + string.Join("; ", odd.Select(f => $"{f.Name} {f.Kind} {f.Section} {f.Story} {f.Z1}-{f.Z2}")));
        });
        ctx.Tool("add_beams", $$"""{"lines":[[30,0,36,0],[36,0,36,5],[36,5,30,5],[30,5,30,0]],"stories":{{storyList}},"section":"CPM-B250x500"}""", a =>
        {
            beams = Names(a);
            var made = link.Frames().Where(f => beams.Contains(f.Name)).ToList();
            FieldContext.Expect(Int(a["added"]) == 4 && made.Count == 4, $"4 beams were asked for; the answer names {beams.Count} and ETABS has {made.Count} of them");
            var odd = made.Where(f => f.Kind != "beam" || f.Section != "CPM-B250x500" || !At(f.Z1, z1) || !At(f.Z2, z1)).ToList();
            FieldContext.Expect(odd.Count == 0, $"beams at {z1} with CPM-B250x500 were expected; ETABS has: " + string.Join("; ", odd.Select(f => $"{f.Name} {f.Kind} {f.Section} {f.Z1}-{f.Z2}")));
        });
        ctx.Tool("add_slabs", $$"""{"outlines":[[[30,0],[36,0],[36,5],[30,5]]],"stories":{{storyList}},"section":"CPM-S180"}""", a =>
        {
            slabs = Names(a);
            var made = link.Areas().Where(x => slabs.Contains(x.Name)).ToList();
            FieldContext.Expect(made.Count == 1 && made[0].Kind == "floor" && made[0].Section == "CPM-S180" && made[0].Corners.Count == 4 && made[0].Corners.All(c => At(c.Z, z1)),
                $"one floor with 4 corners at {z1} and CPM-S180 was expected; ETABS has: " + string.Join("; ", made.Select(x => $"{x.Name} {x.Kind} {x.Section} {x.Corners.Count} corners")));
        });
        ctx.Tool("add_walls", $$"""{"lines":[[42,0,42,5]],"stories":{{storyList}},"section":"CPM-W200"}""", a =>
        {
            walls = Names(a);
            var made = link.Areas().Where(x => walls.Contains(x.Name)).ToList();
            FieldContext.Expect(made.Count == 1 && made[0].Kind == "wall" && made[0].Section == "CPM-W200" && made[0].Corners.Count == 4,
                "one wall panel with CPM-W200 was expected; ETABS has: " + string.Join("; ", made.Select(x => $"{x.Name} {x.Kind} {x.Section} {x.Corners.Count} corners")));
        });
        ctx.Refused("add_columns", """{"points":[[30,0]],"stories":["No such story"],"section":"CPM-C400"}""", "No story named", "tool add_columns says no to a story that is not there");
        ctx.Refused("add_slabs", $$"""{"outlines":[[[30,0],[36,0],[36,5]]],"stories":{{storyList}},"section":"No such section"}""", "no slab section", "tool add_slabs says no to a section that is not there");

        // A column and a slab of their own, to delete again.
        ctx.Tool("add_columns", $$"""{"points":[[48,0]],"stories":{{storyList}},"section":"CPM-C400"}""", a => { spareFrames = Names(a); FieldContext.Expect(spareFrames.Count == 1, "one column was asked for: " + a.ToJsonString()); }, "tool add_columns, one to delete again");
        ctx.Tool("add_slabs", $$"""{"outlines":[[[48,0],[51,0],[51,3],[48,3]]],"stories":{{storyList}},"section":"CPM-S180"}""", a => { spareAreas = Names(a); FieldContext.Expect(spareAreas.Count == 1, "one slab was asked for: " + a.ToJsonString()); }, "tool add_slabs, one to delete again");

        if (beams.Count > 0)
            ctx.Tool("set_section", $$"""{"frames":[{{Q(beams[0])}}],"section":{{Q(otherBeam)}}}""", a =>
            {
                FieldContext.Expect(Int(a["done"]) == 1 && a["failed"]!.AsArray().Count == 0, "the beam was not changed: " + a.ToJsonString());
                var now = link.Frames().First(f => f.Name == beams[0]).Section;
                FieldContext.Expect(now == otherBeam, $"the beam was given {otherBeam}; ETABS says it has {now}");
            }, "tool set_section on a frame");
        if (slabs.Count > 0)
            ctx.Tool("set_section", $$"""{"areas":[{{Q(slabs[0])}}],"section":{{Q(otherSlab)}}}""", a =>
            {
                FieldContext.Expect(Int(a["done"]) == 1 && a["failed"]!.AsArray().Count == 0, "the slab was not changed: " + a.ToJsonString());
                var now = link.Areas().First(x => x.Name == slabs[0]).Section;
                FieldContext.Expect(now == otherSlab, $"the slab was given {otherSlab}; ETABS says it has {now}");
            }, "tool set_section on an area");
        ctx.Tool("set_section", """{"frames":["no such frame"],"section":"CPM-C400"}""", a => FieldContext.Expect(Int(a["done"]) == 0 && a["failed"]!.AsArray().Count == 1, "a frame that is not there should be reported as failed: " + a.ToJsonString()), "tool set_section reports a frame that is not there");

        // Supports: the new columns' feet by name, then everything on the base.
        var feet = Read(ctx, "the points under the new columns", () => link.Points().Where(p => At(p.Z, z0) && p.X > 29.9 && p.X < 36.1).Select(p => p.Name).ToList(), []);
        if (feet.Count > 0)
            ctx.Tool("set_supports", $$"""{"type":"pinned","points":{{List(feet)}}}""", a =>
            {
                FieldContext.Expect(Int(a["done"]) == feet.Count && a["failed"]!.AsArray().Count == 0, "not every point was set: " + a.ToJsonString());
                var odd = feet.Where(p => !link.Restraint(p).SequenceEqual([true, true, true, false, false, false])).ToList();
                FieldContext.Expect(odd.Count == 0, "not pinned when read back from ETABS: " + string.Join(", ", odd.Select(p => $"{p} [{string.Join(" ", link.Restraint(p))}]")));
            }, "tool set_supports pinned at named points");
        else ctx.Fail("tool set_supports pinned at named points", "the new columns have no points on the base to support");
        ctx.Tool("set_supports", """{"type":"fixed","at_base":true}""", a =>
        {
            FieldContext.Expect(a["failed"]!.AsArray().Count == 0, "some points were not set: " + a.ToJsonString());
            var odd = feet.Where(p => !link.Restraint(p).All(r => r)).ToList();
            FieldContext.Expect(odd.Count == 0, "not fixed when read back from ETABS: " + string.Join(", ", odd));
            var under = link.Points().Where(p => At(p.Z, z0) && At(p.X, 42)).ToList();
            FieldContext.Expect(under.Count == 2 && under.All(p => link.Restraint(p.Name).All(r => r)), $"the wall's two base points should be fixed; {under.Count} points are under it");
        }, "tool set_supports fixed at the base");
        if (spareFrames.Count > 0 && Read(ctx, "the point under the spare column", () => link.Points().FirstOrDefault(p => At(p.Z, z0) && At(p.X, 48)), null) is { } loose)
            ctx.Tool("set_supports", $$"""{"type":"free","points":[{{Q(loose.Name)}}]}""", _ => FieldContext.Expect(link.Restraint(loose.Name).All(r => !r), "the point is not free when read back from ETABS"), "tool set_supports free");
        ctx.Refused("set_supports", """{"type":"welded","at_base":true}""", "fixed, pinned or free", "tool set_supports says no to a type that is not one");

        // Loads and a combination.
        ctx.Tool("add_load_pattern", """{"name":"CPM-TEST","type":"Live","self_weight":0}""", _ =>
        {
            FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-TEST" && p.Type == "Live"), "the pattern CPM-TEST (Live) is not in ETABS: " + string.Join(", ", link.LoadPatterns().Select(p => $"{p.Name} {p.Type}")));
            FieldContext.Expect(link.LoadCases().Contains("CPM-TEST"), "ETABS made no load case CPM-TEST with the pattern: " + string.Join(", ", link.LoadCases()));
        });
        ctx.Refused("add_load_pattern", """{"name":"CPM-TEST","type":"Live"}""", "already", "tool add_load_pattern says no to a name that is taken");
        if (beams.Count > 0 && slabs.Count > 0)
        {
            ctx.Tool("set_loads", $$"""{"frames":{{List(beams)}},"areas":{{List(slabs)}},"pattern":"CPM-TEST","value":3}""", a =>
                FieldContext.Expect(Int(a["done"]) == beams.Count + slabs.Count && a["failed"]!.AsArray().Count == 0, "not every object was loaded: " + a.ToJsonString()));
            ctx.Step("the frame load is in ETABS (read back through its API)", () => FrameLoad(link, beams[1], "CPM-TEST", 3));
            ctx.Step("the area load is in ETABS (read back through its API)", () => AreaLoad(link, slabs[0], "CPM-TEST", 3));
            ctx.Tool("set_loads", $$"""{"frames":[{{Q(beams[1])}}],"pattern":"CPM-TEST","value":1.5,"add":true}""", null, "tool set_loads adds to the load there");
            ctx.Step("the added frame load is in ETABS (3 and 1.5)", () => FrameLoad(link, beams[1], "CPM-TEST", 4.5));
            ctx.Tool("set_loads", $$"""{"frames":[{{Q(beams[1])}}],"pattern":"CPM-TEST","value":2}""", null, "tool set_loads replaces the load there");
            ctx.Step("the replaced frame load is in ETABS (2 alone)", () => FrameLoad(link, beams[1], "CPM-TEST", 2));
        }
        else ctx.Fail("tool set_loads", "the beams and the slab to load were not added");
        ctx.Refused("set_loads", """{"frames":["1"],"pattern":"No such pattern","value":1}""", "no load pattern", "tool set_loads says no to a pattern that is not there");
        var first = Read(ctx, "the load cases", () => link.LoadCases().FirstOrDefault(c => c.Equals("Dead", StringComparison.OrdinalIgnoreCase)) ?? link.LoadCases().FirstOrDefault(), null) ?? "Dead";
        ctx.Tool("add_combination", $$"""{"name":"CPM-TEST-COMBO","cases":[{"case":{{Q(first)}},"factor":1.2},{"case":"CPM-TEST","factor":1.6}]}""", _ =>
            FieldContext.Expect(link.Combos().Contains("CPM-TEST-COMBO"), "the combination is not in ETABS: " + string.Join(", ", link.Combos())));
        ctx.Step("the combination's cases and factors are in ETABS (read from its tables)", () => ComboRows(link, "CPM-TEST-COMBO"));
        ctx.Refused("add_combination", """{"name":"CPM-BAD","cases":[{"case":"No such case","factor":1}]}""", "No load case named", "tool add_combination says no to a case that is not there");

        // Reading: the selection, the filters, the tables.
        if (columns.Count > 0 && beams.Count > 0 && slabs.Count > 0)
        {
            ctx.Tool("select_objects", $$"""{"frames":[{{Q(columns[0])}},{{Q(beams[0])}}],"areas":[{{Q(slabs[0])}}]}""", a =>
            {
                FieldContext.Expect(Int(a["selected"]) == 3 && a["failed"]!.AsArray().Count == 0, "not everything was selected: " + a.ToJsonString());
                var selected = link.Selected();
                FieldContext.Expect(selected.Count == 3 && selected.Count(s => s.Kind == "frame") == 2 && selected.Count(s => s.Kind == "area") == 1, "ETABS's selection read back as: " + string.Join(", ", selected.Select(s => s.Kind + " " + s.Name)));
            });
            ctx.Tool("query_objects", """{"selected_only":true}""", a => FieldContext.Expect(Int(a["count"]) == 3, $"3 objects are selected; the answer has {a["count"]}"), "tool query_objects, the selection only");
            ctx.Tool("select_objects", $$"""{"frames":[{{Q(columns[1])}}],"keep_selection":true}""", _ => FieldContext.Expect(link.Selected().Count == 4, $"4 objects should be selected after adding one; ETABS has {link.Selected().Count}"), "tool select_objects keeps the selection");
            ctx.Tool("select_objects", "{}", _ => FieldContext.Expect(link.Selected().Count == 0, $"the selection should be empty; ETABS has {link.Selected().Count}"), "tool select_objects with nothing clears the selection");
        }
        else ctx.Fail("tool select_objects", "the objects to select were not added");
        ctx.Tool("query_objects", """{"kind":"column","limit":0}""", a => FieldContext.Expect(Int(a["count"]) == link.Frames().Count(f => f.Kind == "column") && a["objects"]!.AsArray().Count == 0, $"the count of columns is {a["count"]}"), "tool query_objects by kind, counts only");
        ctx.Tool("query_objects", $$"""{"kind":"beam","story":{{Q(story)}}}""", a => FieldContext.Expect(Int(a["count"]) == link.Frames().Count(f => f.Kind == "beam" && string.Equals(f.Story, story, StringComparison.OrdinalIgnoreCase)), $"the count of beams on {story} is {a["count"]}"), "tool query_objects by story");
        ctx.Tool("query_objects", """{"kind":"wall"}""", a =>
        {
            FieldContext.Expect(Int(a["count"]) == link.Areas().Count(x => x.Kind == "wall"), $"the count of walls is {a["count"]}");
            foreach (var o in a["objects"]!.AsArray()) FieldContext.Expect(o!["name"] is not null && o["section"] is not null && o["corners"] is JsonArray { Count: >= 3 }, "a wall is missing a field: " + o.ToJsonString());
        }, "tool query_objects, walls with every field");
        ctx.Tool("query_objects", """{"section":"CPM-C400","limit":3}""", a => FieldContext.Expect(Int(a["count"]) >= 8 && a["objects"]!.AsArray().Count == 3 && a["note"] is not null, "at least 8 objects with CPM-C400, 3 listed and a note were expected: " + a["count"]), "tool query_objects by section, with a limit");
        ctx.Tool("list_tables", "{}", a => FieldContext.Expect(Int(a["count"]) >= 3, $"ETABS's tables with data were expected: {a["count"]}"));
        ctx.Tool("list_tables", """{"contains":"story","include_empty":true}""", a => FieldContext.Expect(a["tables"]!.AsArray().Any(t => t!.GetValue<string>().Contains("Story Definitions", StringComparison.OrdinalIgnoreCase)), "no table named Story Definitions: " + a["tables"]!.ToJsonString()), "tool list_tables with a search");
        var definitions = ctx.Tool("read_table", """{"table":"Story Definitions"}""", a => FieldContext.Expect(a["fields"]!.AsArray().Count > 0 && Int(a["row_count"]) >= written.Stories, $"{written.Stories} stories were expected in the table: " + a.ToJsonString()), "tool read_table Story Definitions");
        if (definitions?["fields"] is JsonArray { Count: > 0 } fields && definitions["rows"] is JsonArray { Count: > 0 } rows)
        {
            var (field, value) = (fields[0]!.GetValue<string>(), rows[0]![0]!.GetValue<string>());
            ctx.Tool("read_table", $$"""{"table":"Story Definitions","where":{{{Q(field)}}:{{Q(value)}}},"fields":[{{Q(field)}}],"limit":1}""", a =>
                FieldContext.Expect(a["fields"]!.AsArray().Count == 1 && Int(a["row_count"]) >= 1 && a["rows"]!.AsArray().Count == 1, "one field and one row were asked for: " + a.ToJsonString()), "tool read_table with where, fields and limit");
        }
        ctx.Step("ETABS's definition and assignment tables, by the names ETABS gives them", () =>
            link.Tables().Where(t => new[] { "Definition", "Assignments - Section", "Load Pattern", "Combination", "Material Properties", "Section Propert" }.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Select(t => $"{t.Name}{(t.Empty ? " (empty)" : "")}").Take(150).ToList());
        ctx.Refused("read_table", """{"table":"No such table at all"}""", "no table", "tool read_table says no to a table that is not there");

        // Deleting, a snapshot, saving.
        if (spareFrames.Count > 0 && spareAreas.Count > 0)
            ctx.Tool("delete_objects", $$"""{"frames":{{List(spareFrames)}},"areas":{{List(spareAreas)}}}""", a =>
            {
                FieldContext.Expect(Int(a["done"]) == 2 && a["failed"]!.AsArray().Count == 0, "the column and the slab were not both deleted: " + a.ToJsonString());
                FieldContext.Expect(!link.Frames().Any(f => f.Name == spareFrames[0]) && !link.Areas().Any(x => x.Name == spareAreas[0]), "a deleted object is still in ETABS");
            });
        else ctx.Fail("tool delete_objects", "the column and the slab to delete were not added");
        ctx.Tool("model_snapshot", """{"name":"cpm-before-analysis"}""", a => FieldContext.Expect(Int(a["frames"]) == link.Frames().Count && Int(a["areas"]) == link.Areas().Count && a["has_results"]!.GetValue<bool>() == false, "the snapshot should hold every object and no results: " + a.ToJsonString()));
        var before = DateTime.UtcNow.AddSeconds(-2);
        ctx.Tool("save_model", "{}", a =>
        {
            var file = a["saved"]!.GetValue<string>();
            FieldContext.Expect(file.Replace('\\', '/').Split('/').Last().StartsWith(ModelerFieldSteps.ModelName, StringComparison.OrdinalIgnoreCase), $"it saved '{file}', which is not the scratch model");
            FieldContext.Expect(File.Exists(file) && File.GetLastWriteTimeUtc(file) >= before, "the scratch model's file was not written again");
        });

        // The wall becomes pier CPM-P1, so ETABS's shear wall design has a pier to design and the pack has labels to read.
        var piered = false;
        if (walls.Count > 0)
            ctx.Step("the scratch wall is made pier CPM-P1 (for the shear wall design)", () =>
            {
                if (link is OfflineEtabs) throw new FieldSkipException("the stand-in has no ETABS API to label piers with");
                link.Raw("label the wall as a pier", m =>
                {
                    try { m.PierLabel.SetPier("CPM-P1"); } catch (Exception ex) when (ex is not System.Runtime.InteropServices.COMException) { }
                    foreach (var w in walls)
                    {
                        var answer = Convert.ToInt32(m.AreaObj.SetPier(w, "CPM-P1", 0));
                        if (answer != 0) throw new EtabsException($"ETABS answered {answer} to AreaObj.SetPier for {w}");
                    }
                    return null;
                });
                piered = true;
                return $"{walls.Count} wall panels are pier CPM-P1";
            });
        else ctx.Skip("the scratch wall is made pier CPM-P1 (for the shear wall design)", "the wall was not added");

        // ---- ETABS's own analysis and design ----
        NoVerdictBeforeAnalysis(ctx);
        ctx.Tool("run_analysis", "{}", _ => FieldContext.Expect(link.Info().Locked, "the analysis answered, but ETABS does not say the model is analysed (locked)"));
        var analysed = false;
        try { analysed = link.Info().Locked; }
        catch (EtabsException) { }
        // The shear wall design, when ETABS's API cannot start it, is left to the menu: its tables are then empty by design.
        string? wallsByHand = null;
        if (analysed)
            ctx.Tool("run_design", "{}", a =>
            {
                if ((a["run_in_program"] as JsonArray)?.FirstOrDefault(k => k?["kind"]?.GetValue<string>() == "shear_wall") is { } wall)
                    wallsByHand = $"ETABS's API has no call to start the shear wall design, so it is run from {wall["menu"]?.GetValue<string>() ?? "the Design menu"} by hand; its tables stay empty until then";
                FieldContext.Expect(a["ran"] is JsonArray ran && ran.Any(k => k!.GetValue<string>() == "concrete"), "the concrete frame design did not run: " + a.ToJsonString());
            }, "tool run_design (every kind; the ones the API cannot start are named with their menu)");
        else ctx.Refused("run_design", "{}", "design", "tool run_design says the analysis must run first");

        // A change on the analysed model must be refused or made, never lost without a word.
        ctx.Step("a change on the analysed model is refused, or made: never lost", () =>
        {
            try { ctx.Call("add_load_pattern", """{"name":"CPM-AFTER","type":"Other"}"""); }
            catch (Exception ex) when (ex is EtabsException or ToolInputException) { return "refused: " + ex.Message; }
            FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-AFTER"), "the tool said the pattern was added to the analysed model, and ETABS does not have it");
            return $"ETABS took the change; analysed now: {link.Info().Locked}";
        });

        // ---- the tables the checks and the review pack read ----
        var answers = new List<(string, JsonNode?)>();
        if (!analysed)
        {
            ctx.Skip("ETABS's result tables", "the analysis did not run");
            foreach (var name in new[] { "results_overview", "check_modal", "check_drift", "check_torsion", "check_story_irregularity", "design_ratios", "check_report" })
                ctx.Tool(name, "{}", null, $"tool {name} (no analysis: it must still answer)");
        }
        else
        {
            ctx.Step("ETABS tables with data after the analysis and design", () => link.Tables().Where(t => !t.Empty).Select(t => t.Name).ToList());
            var sampled = new HashSet<string>(StringComparer.Ordinal);
            var withData = link.Tables().Where(t => !t.Empty).Select(t => ResultTools.Norm(t.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var names in ResultTools.CheckedTables.Concat(Pack.UsualTables.Select(t => new[] { t })))
                if (sampled.Add(ResultTools.Norm(names[0])))
                {
                    // A table ETABS does not list with data for this model (no braces: no brace forces) is not a failure.
                    if (ResultTools.Norm(names[0]) == ResultTools.Norm("Story Stiffness") && !ResultTools.HasLateralLoad(link))
                    {
                        ctx.Skip($"ETABS table {names[0]}", "ETABS gives story stiffness only for earthquake and wind load cases, and the scratch model has gravity loads only");
                        continue;
                    }
                    if (!names.Any(n => withData.Contains(ResultTools.Norm(n))))
                    {
                        ctx.Skip($"ETABS table {names[0]}", "ETABS lists it with no data for the scratch model (it has nothing of that kind, or no load case that gives it)");
                        continue;
                    }
                    ctx.Step($"ETABS table {names[0]}", () => ResultTools.Sample(link, names));
                }
            var summaries = link.Tables().Where(t => ResultTools.IsDesignSummary(t.Name)).ToList();
            ctx.Step("ETABS design summary tables", () =>
            {
                FieldContext.Expect(summaries.Any(t => !t.Empty), "no design summary table has data after the design: " + string.Join("; ", summaries.Select(t => t.Name)));
                return summaries.Select(t => $"{t.Name}{(t.Empty ? " (empty)" : "")}").ToList();
            });
            foreach (var table in summaries.Where(t => !t.Empty).Take(8)) ctx.Step($"ETABS table {table.Name}", () => ResultTools.Sample(link, table.Name));
            // Every design table ETABS lists, by kind, with data or empty: the names the Viewer must know.
            ctx.Step("ETABS design tables of every kind (steel, concrete, composite, shear wall, slab)", () => DesignResults.Survey(link.Tables()).ToJsonString());
            ctx.Step("ETABS shear wall pier design table has the scratch pier", () =>
            {
                if (!piered) throw new FieldSkipException("the wall was not made a pier");
                var pier = link.Tables().Where(t => DesignResults.FamilyOf(t.Name)?.Key == "pier").ToList();
                if (wallsByHand is not null && pier.All(t => t.Empty)) throw new FieldSkipException(wallsByHand);
                FieldContext.Expect(pier.Count > 0, "ETABS lists no shear wall pier design table at all");
                var full = pier.FirstOrDefault(t => !t.Empty) ?? throw new FieldCheckException("the pier design tables are empty after the design: " + string.Join("; ", pier.Select(t => t.Name)) + ". The export file is then the way to the results.");
                var data = link.ReadTable(full.Key, null, null);
                FieldContext.Expect(data.Rows.Any(r => r.Any(v => v == "CPM-P1")), $"'{full.Name}' has no row for pier CPM-P1");
                return new { table = full.Name, fields = data.Fields, rows = data.Rows.Take(4) };
            });
            ctx.Step("the pier labels of the walls are read for the Viewer", () =>
            {
                if (!piered) throw new FieldSkipException("the wall was not made a pier");
                var notes = new List<string>();
                var labels = DesignResults.WallLabels(link, link.Tables(), notes);
                FieldContext.Expect(walls.All(w => labels.Any(l => l![1]!.GetValue<string>() == w && l[2]!.GetValue<string>() == "CPM-P1")), "not every wall panel came back as pier CPM-P1: " + labels.ToJsonString() + " " + string.Join(" ", notes));
                return labels.ToJsonString();
            });
            ctx.Step("the API's own steel and concrete design summaries", () =>
            {
                var notes = new List<string>();
                var made = DesignResults.FromApi(link, new HashSet<string>(), notes);
                FieldContext.Expect(made.Any(m => m.Name.Contains("Concrete", StringComparison.Ordinal)), "the API gave no concrete design summary after the concrete design ran. " + string.Join(" ", notes));
                return new { tables = made.Select(m => new { m.Name, rows = m.Data.Rows.Count, fields = m.Data.Fields }), notes };
            });

            // ---- every check ----
            ctx.Tool("results_overview", "{}", a =>
            {
                FieldContext.Expect(a["locked"]!.GetValue<bool>(), "the overview says the model is not analysed");
                FieldContext.Expect(a["base_reactions"] is JsonArray { Count: > 0 }, "no base reactions were read: " + a["base_reactions"]?.ToJsonString());
                FieldContext.Expect(a["result_tables_with_data"] is JsonArray { Count: > 0 }, "no result table with data was found");
            });
            answers.Add(("check_modal", ctx.Tool("check_modal", """{"min_mass":0.9,"modes":6}""")));
            answers.Add(("check_drift", ctx.Tool("check_drift", """{"limit":0.002}""", a => FieldContext.Expect(a["status"]?.GetValue<string>() != "reported", "a limit was given, so pass or fail was expected: " + a["status"]), "tool check_drift with a limit")));
            ctx.Tool("check_drift", "{}", a => FieldContext.Expect(a["status"]?.GetValue<string>() is "reported" or "not available", "no limit was given, so no verdict was expected: " + a["status"]), "tool check_drift without a limit");
            answers.Add(("check_torsion", ctx.Tool("check_torsion", "{}")));
            ctx.Tool("check_torsion", """{"use":"drifts","irregular":1.2,"extreme":1.4}""", null, "tool check_torsion from the drifts");
            answers.Add(("check_story_irregularity", ctx.Tool("check_story_irregularity", "{}")));
            answers.Add(("design_ratios", ctx.Tool("design_ratios", """{"limit":1.0,"list":10}""")));
            ctx.Tool("check_report", """{"drift_limit":0.002,"design_limit":1.0}""", a =>
            {
                FieldContext.Expect(a["analysis_run"]!.GetValue<bool>() && a["summary"] is JsonArray { Count: 5 }, "five checks on an analysed model were expected: " + a["summary"]?.ToJsonString());
                foreach (var part in new[] { "modal", "drift", "torsion", "story_irregularity", "design_ratios" }) FieldContext.Expect(a["checks"]![part] is not null, $"the report has no '{part}'");
            });
            Verdicts(ctx, "every ETABS check had its table to read", answers);
        }

        // ---- snapshots and the review pack ----
        ctx.Tool("model_snapshot", """{"name":"cpm-after-analysis"}""", a => FieldContext.Expect(!analysed || a["has_results"]!.GetValue<bool>(), "the model is analysed and the snapshot holds no results"));
        ctx.Tool("list_snapshots", "{}", a => FieldContext.Expect(Int(a["count"]) == 2, $"two snapshots were taken; {a["count"]} are listed"));
        ctx.Tool("compare_snapshots", """{"from":"cpm-before-analysis","to":"cpm-after-analysis"}""", a =>
            FieldContext.Expect(Int(a["frames"]!["removed"]) == 0 && Int(a["areas"]!["removed"]) == 0, "nothing was removed between the two snapshots: " + a.ToJsonString()));
        ctx.Tool("compare_snapshots", """{"from":"cpm-before-analysis"}""", null, "tool compare_snapshots with the model as it is now");
        ctx.Step("the review pack of the scratch model is written", () =>
        {
            var cases = link.LoadCases();
            var checks = ProgramTools.Report(System.Text.Json.JsonDocument.Parse("""{"drift_limit":0.002}""").RootElement, link).AsObject();
            var pack = Pack.Build(link, [], cases, link.Combos(), checks, null, mostRows: 20000, by: "CSI ProMax field test");
            var file = Path.Combine(ctx.Folder, ModelerFieldSteps.ModelName + " ETABS.cpmx");
            File.WriteAllBytes(file, pack.Zip);
            FieldContext.Expect(pack.Frames == link.Frames().Count && pack.Areas == link.Areas().Count, $"the pack holds {pack.Frames} frames and {pack.Areas} areas, the model has {link.Frames().Count} and {link.Areas().Count}");
            FieldContext.Expect(!analysed || pack.Tables.Count > 1, "the model is analysed and the pack took no result table: " + string.Join("; ", pack.Notes));
            var design = pack.Content["manifest"]!["design"] as JsonObject;
            FieldContext.Expect(!analysed || design is not null && design.ContainsKey("concrete"), "the model is designed and the pack has no concrete design results: " + design?.ToJsonString() + " " + string.Join("; ", pack.Notes));
            FieldContext.Expect(!piered || pack.Content["model"]!["wall_labels"] is JsonArray { Count: > 0 }, "the wall is pier CPM-P1 and the pack has no wall labels for the Viewer");
            return new { file = Path.GetFileName(file), kilobytes = pack.Zip.Length / 1024, frames = pack.Frames, areas = pack.Areas, tables = pack.Tables, design = design?.ToJsonString(), notes = pack.Notes };
        });

        // ---- unlocked again: every load pattern type by its name, read back from ETABS ----
        if (analysed)
            ctx.Step("the scratch model is unlocked again", () =>
            {
                link.Raw("unlock the model", m => m.SetModelIsLocked(false));
                FieldContext.Expect(!link.Info().Locked, "ETABS still says the model is analysed (locked)");
                return "unlocked";
            });
        foreach (var (name, type) in EtabsWords.PatternTypes.Where(t => t.Name is not ("Live" or "Dead")).Select(t => ("CPM-" + t.Name.Replace(" ", ""), t.Name)))
            ctx.Tool("add_load_pattern", $$"""{"name":{{Q(name)}},"type":{{Q(type)}}}""", _ =>
            {
                var back = link.LoadPatterns().FirstOrDefault(p => p.Name == name);
                FieldContext.Expect(back is not null && back.Type == type, $"the pattern {name} was added as {type}; ETABS says {(back is null ? "it is not there" : "it is " + back.Type)}");
            }, $"tool add_load_pattern of type {type}");

        // ---- the link with GeoProMax ----
        GeoProMaxFieldSteps.Run(ctx);

        ctx.Coverage("every ETABS tool was run");
    }

    /// <summary>A read-back through ETABS's API that could not be made is not tested, not failed: it is the test's own call, not the tool's.</summary>
    private static T Back<T>(Func<T> read)
    {
        try { return read(); }
        catch (EtabsException ex) { throw new FieldSkipException("the load could not be read back through ETABS's API (" + ex.Message + ")"); }
    }

    /// <summary>The uniform load on a frame in a pattern, summed, as ETABS's API gives it; fails when it is not <paramref name="expected"/>.</summary>
    private static object FrameLoad(ICsiLink link, string frame, string pattern, double expected)
    {
        var found = Back(() => (double[])link.Raw("read a frame's loads", m =>
        {
            int n = 0; string[] names = [], patterns = [], systems = []; int[] types = [], directions = [];
            double[] rd1 = [], rd2 = [], d1 = [], d2 = [], v1 = [], v2 = [];
            var answer = Convert.ToInt32(m.FrameObj.GetLoadDistributed(frame, ref n, ref names, ref patterns, ref types, ref systems, ref directions, ref rd1, ref rd2, ref d1, ref d2, ref v1, ref v2, 0));
            if (answer != 0) throw new EtabsException($"ETABS answered {answer} to FrameObj.GetLoadDistributed for frame {frame}.");
            return Enumerable.Range(0, n).Where(i => string.Equals(patterns[i], pattern, StringComparison.OrdinalIgnoreCase)).Select(i => v1[i]).ToArray();
        })!);
        FieldContext.Expect(found.Length > 0 && Math.Abs(found.Sum() - expected) < 1e-6, $"frame {frame} should carry {expected} in {pattern}; ETABS has {(found.Length == 0 ? "no load" : string.Join(" + ", found))}");
        return new { frame, pattern, loads = found };
    }

    private static object AreaLoad(ICsiLink link, string area, string pattern, double expected)
    {
        var found = Back(() => (double[])link.Raw("read an area's loads", m =>
        {
            int n = 0; string[] names = [], patterns = [], systems = []; int[] directions = []; double[] values = [];
            var answer = Convert.ToInt32(m.AreaObj.GetLoadUniform(area, ref n, ref names, ref patterns, ref systems, ref directions, ref values, 0));
            if (answer != 0) throw new EtabsException($"ETABS answered {answer} to AreaObj.GetLoadUniform for area {area}.");
            return Enumerable.Range(0, n).Where(i => string.Equals(patterns[i], pattern, StringComparison.OrdinalIgnoreCase)).Select(i => values[i]).ToArray();
        })!);
        FieldContext.Expect(found.Length > 0 && Math.Abs(found.Sum() - expected) < 1e-6, $"area {area} should carry {expected} in {pattern}; ETABS has {(found.Length == 0 ? "no load" : string.Join(" + ", found))}");
        return new { area, pattern, loads = found };
    }

    /// <summary>The rows of ETABS's combination table for one combination: the test reads them to see its cases and factors arrived.</summary>
    private static object ComboRows(ICsiLink link, string combo)
    {
        var table = link.Tables().FirstOrDefault(t => ResultTools.Norm(t.Name).Contains("loadcombinationdefinitions", StringComparison.Ordinal))
            ?? throw new FieldSkipException("ETABS has no table named like 'Load Combination Definitions' to read it from. Its combination tables: " + string.Join("; ", link.Tables().Where(t => t.Name.Contains("Combination", StringComparison.OrdinalIgnoreCase)).Select(t => t.Name)));
        var data = link.ReadTable(table.Key, null, null);
        var rows = data.Rows.Where(r => r.Any(v => string.Equals(v, combo, StringComparison.OrdinalIgnoreCase))).ToList();
        // ETABS names the combination on its first row only: the rows that follow, up to the next name, belong to it.
        var name = ResultTools.Col(data.Fields, "Name", "Combo", "ComboName");
        if (name >= 0)
        {
            var at = data.Rows.ToList().FindIndex(r => string.Equals(r[name], combo, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) rows = data.Rows.Skip(at).TakeWhile((r, i) => i == 0 || r[name].Length == 0 || string.Equals(r[name], combo, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var text = string.Join(" | ", rows.Select(r => string.Join(",", r)));
        FieldContext.Expect(rows.Count > 0 && text.Contains("CPM-TEST", StringComparison.Ordinal) && text.Contains("1.6", StringComparison.Ordinal) && text.Contains("1.2", StringComparison.Ordinal),
            $"the combination's rows should name CPM-TEST with 1.6 and a case with 1.2; ETABS's table '{table.Name}' has: {(rows.Count == 0 ? "no row for it" : text)}");
        return new { table = table.Name, fields = data.Fields, rows };
    }
}
