using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test of the tools every program has through the same API (<see cref="CsiTools"/>), on the scratch model
/// of whichever program the part is testing: the whole API, units, properties, load definitions, table editing,
/// groups (and ETABS's piers), the SAP2000 tools the other programs share, the lateral loads with a second analysis,
/// the results through the API, the response spectrum's scaling, unlocking, checkpoints, stories and a building made
/// in one call. Each change is read back from the program. Run last in each program's part, before its coverage
/// check; the building it makes at the end replaces the scratch model, which is closed without saving.
/// </summary>
public static class CsiFieldSteps
{
    private const string NoApi = "the stand-in has no program API behind it (a live program is needed)";

    /// <summary>edit_table's arguments: rows whose field equals a value, and the field to set on them.</summary>
    private static string Edit(string table, string action, string matchField, string matchValue, string? setField, string? setValue)
    {
        var a = new JsonObject { ["table"] = table, ["action"] = action, ["match"] = new JsonObject { [matchField] = matchValue } };
        if (setField is not null) a["set"] = new JsonObject { [setField] = setValue };
        return a.ToJsonString();
    }

    private static string Cut(string text) => text.Length > 300 ? text[..300] + "..." : text;

    public static void Run(FieldContext ctx)
    {
        var link = ctx.Link;
        var program = link.Program;
        var name = program.Name;
        var isSafe = program.Key == CsiProgram.Safe.Key;
        var tools = CsiTools.For(program);
        ctx.Offer(tools);
        var offered = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var live = link is not OfflineEtabs and not MemoryEtabs;

        JsonNode? T(string tool, string json = "{}", Action<JsonNode>? check = null, string? step = null)
        {
            if (!live) { ctx.Covered(tool); ctx.Skip(step ?? "tool " + tool, NoApi); return null; }
            return ctx.Tool(tool, json, check, step);
        }
        void No(string tool, string json, string saying, string step)
        {
            if (!live) { ctx.Covered(tool); ctx.Skip(step, NoApi); return; }
            ctx.Refused(tool, json, saying, step);
        }

        // ---- what needs no API: this PC, the redraw, what changed ----
        ctx.Tool("list_programs", "{}", a =>
        {
            FieldContext.Expect(a["programs"] is JsonArray { Count: 3 } list && list.Count(p => p!["this_window"]!.GetValue<bool>()) == 1, "the three programs, one of them this window's, were expected: " + a.ToJsonString());
            FieldContext.Expect(a["this_window_works_in"]!.GetValue<string>() == name, $"this part tests {name}; the tool says {a["this_window_works_in"]}");
        });
        ctx.Tool("refresh_view", "{}", a => FieldContext.Expect(a["refreshed"]?.GetValue<bool>() == true, "no redraw: " + a.ToJsonString()));
        ctx.Tool("what_changed", """{"reset":true}""", a => FieldContext.Expect(a["baseline"]?.GetValue<string>() == "set", "the starting point was not set: " + a.ToJsonString()), "tool what_changed sets its starting point");
        var marker = Read(ctx, "a load pattern added by hand for what_changed", () => { link.AddLoadPattern("CPM-BYHAND", "Other", 0); return true; }, false);
        ctx.Tool("what_changed", "{}", a =>
        {
            FieldContext.Expect(!marker || a["definitions"]?["load_patterns"]?["added"]?.ToJsonString().Contains("CPM-BYHAND") == true, "the pattern added by hand is not reported: " + a.ToJsonString());
        }, "tool what_changed reports a change made outside the tools");
        ctx.Tool("list_checkpoints", "{}", a => FieldContext.Expect(Int(a["count"]) >= 0, "no count: " + a.ToJsonString()), "tool list_checkpoints before any");

        // ---- the whole API ----
        T("search_api", """{"query":"PointObj SetRestraint"}""", a =>
        {
            FieldContext.Expect(Int(a["methods_in_api"]) > 300, $"only {a["methods_in_api"]} methods were found in {name}'s API");
            FieldContext.Expect(a["methods"] is JsonArray { Count: > 0 } m && m.Any(x => x!["path"]!.GetValue<string>() == "PointObj.SetRestraint"), "PointObj.SetRestraint was not found: " + a.ToJsonString());
        });
        T("describe_api", """{"path":"PointObj.SetRestraint"}""", a =>
            FieldContext.Expect(a["overloads"]?[0]?["parameters"] is JsonArray { Count: >= 2 } p && p.Any(x => x!["direction"]!.GetValue<string>() == "ref"), "the parameters, with the restraint given by ref, were expected: " + a.ToJsonString()));
        No("describe_api", """{"path":"PointObj.NoSuchMethod"}""", "no", "tool describe_api says no to a method that is not there");
        var points = Read(ctx, "the points", () => link.Points().Count, 0);
        T("call_api", """{"path":"PointObj.GetNameList"}""", a =>
        {
            FieldContext.Expect(a["return"]?.GetValue<int>() == 0, $"{name} answered {a["return"]}");
            var names = a["values"]?.AsObject().Select(v => v.Value).OfType<JsonArray>().FirstOrDefault();
            FieldContext.Expect(names is not null && names.Count == points, $"the link reads {points} points; the API call gave {names?.Count}");
        }, "tool call_api reads the point names");
        T("call_api", """{"calls":[{"path":"GetModelIsLocked"},{"path":"FrameObj.Count"}]}""", a =>
            FieldContext.Expect(Int(a["calls"]) == 2 && a["results"] is JsonArray { Count: 2 } r && r.All(x => x?["return"] is not null || x?["error"] is not null), "two answers were expected: " + a.ToJsonString()), "tool call_api, two calls in one go");
        No("call_api", """{"path":"FrameObj.Delete","args":{"Name":"1"}}""", "only reads", "tool call_api says no to a call that changes the model");

        // ---- the model must be changeable for the rest ----
        var locked = Read(ctx, "whether the model is locked", () => link.Info().Locked, false);
        if (locked) T("unlock_model", "{}", a => FieldContext.Expect(a["locked"]?.GetValue<bool>() == false, "still locked: " + a.ToJsonString()), "tool unlock_model on the analysed scratch model");

        // ---- units, properties, load definitions, design codes ----
        var info = Read(ctx, "the units", link.Info, new ModelInfo("", "", "kN", "m", "C", false));
        var units = Read(ctx, "the units' name", () => ComEtabs.UnitNames[Units.Code(info.Force, info.Length) - 1], "kN_m_C");
        T("set_units", """{"force":"kN","length":"mm"}""", a => FieldContext.Expect(link.Info().Length == "mm", $"the units were set to kN, mm; {name} says {link.Info().Force}, {link.Info().Length}"), "tool set_units to kN, mm");
        T("set_units", $$"""{"units":{{Q(units)}}}""", a => FieldContext.Expect(link.Info().Length == info.Length, $"the units were set back to {units}; {name} says {link.Info().Force}, {link.Info().Length}"), "tool set_units back to the model's own");
        No("set_units", """{"units":"furlong_C"}""", "not one of", "tool set_units says no to units that are not a set");
        T("list_properties", """{"kind":"all"}""", a =>
        {
            var mats = a["materials"] as JsonArray;
            FieldContext.Expect(mats is { Count: > 0 } && mats.Any(m => m!["name"]!.GetValue<string>() == "CPM-C35" && m["e"]?.GetValue<double>() > 0), "CPM-C35 with its modulus was expected: " + mats?.ToJsonString());
            FieldContext.Expect(a["frame_sections"] is JsonArray f && (isSafe || f.Any(s => s!["name"]!.GetValue<string>() == "CPM-C400" && s["shape"]!.GetValue<string>().Length > 0)), "CPM-C400 with its shape was expected: " + a["frame_sections"]?.ToJsonString());
            FieldContext.Expect(a["area_sections"] is JsonArray s2 && s2.Any(s => s!["kind"] is not null), "an area section with its kind was expected: " + a["area_sections"]?.ToJsonString());
        });
        T("list_load_definitions", "{}", a =>
        {
            FieldContext.Expect(a["load_patterns"] is JsonArray p && p.Any(x => x!["name"]!.GetValue<string>() == "CPM-SDL"), "CPM-SDL was expected: " + a["load_patterns"]?.ToJsonString());
            FieldContext.Expect(a["combinations"] is JsonArray c && c.Any(x => x!["name"]!.GetValue<string>() == "CPM-ULS" && x["cases"] is JsonArray { Count: > 0 }), "CPM-ULS with its cases was expected: " + a["combinations"]?.ToJsonString());
            FieldContext.Expect(a["load_cases"] is JsonArray l && l.All(x => x!["type"] is not null), "every load case with its type was expected: " + a["load_cases"]?.ToJsonString());
        });
        T("design_codes", "{}", a => FieldContext.Expect(a["codes"] is JsonObject c && c.Any(kv => kv.Value?.GetValue<string>() is { Length: > 0 } v && v != "not in this program"), $"no design code was read from {name}: " + a.ToJsonString()));

        // ---- groups, and ETABS's piers ----
        var frames = Read(ctx, "the frames", () => link.Frames().Select(f => f.Name).Take(2).ToList(), []);
        var areas = Read(ctx, "the areas", () => link.Areas().Select(x => x.Name).Take(2).ToList(), []);
        T("assign_group", $$"""{"group":"CPM-G1","frames":{{List(frames)}},"areas":{{List(areas)}}}""", a =>
        {
            FieldContext.Expect(Int(a["done"]) == frames.Count + areas.Count && a["failed"]!.AsArray().Count == 0, "not every object went in the group: " + a.ToJsonString());
            FieldContext.Expect(Int(a["in_group_now"]) >= frames.Count + areas.Count, "the group read back holds fewer: " + a.ToJsonString());
        });
        T("assign_group", $$"""{"group":"CPM-G1","areas":{{List(areas.Take(1))}},"remove":true}""", a => FieldContext.Expect(Int(a["done"]) == Math.Min(1, areas.Count), "not taken out: " + a.ToJsonString()), "tool assign_group takes an object out");
        if (offered.Contains("assign_pier_spandrel"))
        {
            var wall = Read(ctx, "a wall", () => link.Areas().FirstOrDefault(x => x.Kind == "wall")?.Name, null);
            if (wall is null) { ctx.Covered("assign_pier_spandrel"); ctx.Fail("tool assign_pier_spandrel", "the scratch model has no wall to label"); }
            else
            {
                T("assign_pier_spandrel", $$"""{"kind":"pier","label":"CPM-P2","areas":[{{Q(wall)}}]}""", a => FieldContext.Expect(Int(a["done"]) == 1, "the wall is not pier CPM-P2 when read back: " + a.ToJsonString()), "tool assign_pier_spandrel, a pier");
                T("assign_pier_spandrel", $$"""{"kind":"spandrel","label":"CPM-SP1","areas":[{{Q(wall)}}]}""", a => FieldContext.Expect(Int(a["done"]) == 1, "the wall is not spandrel CPM-SP1 when read back: " + a.ToJsonString()), "tool assign_pier_spandrel, a spandrel");
            }
        }
        if (offered.Contains("add_area_section"))
        {
            T("add_area_section", """{"name":"CPM-S150","kind":"slab","material":"CPM-C35","thickness":0.15}""", _ => FieldContext.Expect(link.AreaSections().Contains("CPM-S150"), "CPM-S150 is not in the model"), "tool add_area_section, a slab");
            T("add_area_section", """{"name":"CPM-W250","kind":"wall","material":"CPM-C35","thickness":0.25}""", _ => FieldContext.Expect(link.AreaSections().Contains("CPM-W250"), "CPM-W250 is not in the model"), "tool add_area_section, a wall");
        }

        // ---- table editing: the load patterns' table ----
        var table = Read(ctx, "the load pattern table's name", () =>
            (link.Tables().FirstOrDefault(t => t.Name.Equals("Load Pattern Definitions", StringComparison.OrdinalIgnoreCase))
             ?? link.Tables().FirstOrDefault(t => t.Name.StartsWith("Load Pattern Definitions", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Auto", StringComparison.OrdinalIgnoreCase)))?.Name, null);
        if (table is null) { ctx.Covered("edit_table"); if (live) ctx.Fail("tool edit_table", $"{name} lists no 'Load Pattern Definitions' table to edit"); else ctx.Skip("tool edit_table", NoApi); }
        else
        {
            string? nameField = null, weightField = null;
            T("edit_table", $$"""{"table":{{Q(table)}},"action":"fields"}""", a =>
            {
                var fields = a["fields"]!.AsArray().Select(f => f!["key"]!.GetValue<string>()).ToList();
                nameField = fields.FirstOrDefault(f => f.Equals("Name", StringComparison.OrdinalIgnoreCase) || f.Equals("LoadPat", StringComparison.OrdinalIgnoreCase) || f.Contains("Pattern", StringComparison.OrdinalIgnoreCase));
                weightField = fields.FirstOrDefault(f => f.Contains("Self", StringComparison.OrdinalIgnoreCase));
                FieldContext.Expect(nameField is not null && weightField is not null, "a name and a self weight field were expected: " + string.Join(", ", fields));
            }, "tool edit_table gives a table's fields");
            T("edit_table", $$"""{"table":{{Q(table)}},"action":"read","limit":50}""", a => FieldContext.Expect(Int(a["rows_total"]) >= 1, "no rows: " + a.ToJsonString()), "tool edit_table reads a table's rows");
            if (nameField is not null && weightField is not null)
            {
                T("edit_table", Edit(table, "update", nameField, "CPM-SDL", weightField, "0.5"), a =>
                {
                    FieldContext.Expect(a["applied"]?.GetValue<bool>() == true, "the edit was not applied: " + a.ToJsonString());
                    var back = link.LoadPatterns().First(p => p.Name == "CPM-SDL").SelfWeight;
                    FieldContext.Expect(Math.Abs(back - 0.5) < 1e-6, $"CPM-SDL's self weight was set to 0.5 through the table; {name} says {back}");
                }, "tool edit_table updates a row, read back");
                T("edit_table", Edit(table, "update", nameField, "CPM-SDL", weightField, "0"), null, "tool edit_table sets the row back");
                No("edit_table", Edit(table, "delete", nameField, "No such pattern", null, null), "No row", "tool edit_table says no to deleting rows that are not there");
            }
        }

        // ---- the SAP2000 tools the other programs share ----
        if (offered.Contains("add_frames"))
        {
            T("add_material", """{"name":"CPM-STEEL","type":"steel","elastic_modulus":200000000,"fy":345000,"unit_weight":77}""", _ => FieldContext.Expect(link.Materials().Contains("CPM-STEEL"), "CPM-STEEL is not in the model"));
            T("add_frame_section", """{"name":"CPM-I300","shape":"i_section","material":"CPM-STEEL","depth":0.3,"width":0.15,"flange_thickness":0.01,"web_thickness":0.007}""", _ => FieldContext.Expect(link.FrameSections().Contains("CPM-I300"), "CPM-I300 is not in the model"));
            var joints = Names(T("add_joints", """{"points":[[60,0,0],[60,0,3]]}""", a => FieldContext.Expect(Int(a["added"]) == 2, "two joints were asked for: " + a.ToJsonString())), "joints");
            var made = Names(T("add_frames", """{"lines":[[60,0,0,60,0,3],[60,0,0,63,0,3]],"section":"CPM-I300"}""", a =>
            {
                var names = Names(a, "frames");
                var kinds = link.Frames().Where(f => names.Contains(f.Name)).Select(f => f.Kind).OrderBy(k => k).ToList();
                FieldContext.Expect(names.Count == 2 && string.Join(",", kinds) == "brace,column", "a column and a brace were asked for; the model has: " + string.Join(", ", kinds));
            }, "tool add_frames, a column and a brace by coordinates"), "frames");
            if (made.Count == 2)
            {
                T("set_releases", $$"""{"frames":[{{Q(made[1])}}],"type":"pinned"}""", null, "tool set_releases pins the brace");
                T("set_member_loads", $$"""{"frames":[{{Q(made[1])}}],"pattern":"CPM-SDL","value":1,"direction":"gravity"}""", null, "tool set_member_loads on the brace");
            }
            else foreach (var t in new[] { "set_releases", "set_member_loads" }) { ctx.Covered(t); if (live) ctx.Fail("tool " + t, "the frames to try it on were not added"); else ctx.Skip("tool " + t, NoApi); }
            if (joints.Count == 2) T("set_joint_loads", $$"""{"joints":[{{Q(joints[1])}}],"pattern":"CPM-SDL","fz":-10}""", null, "tool set_joint_loads on the column's top");
            else { ctx.Covered("set_joint_loads"); if (live) ctx.Fail("tool set_joint_loads", "the joints were not added"); else ctx.Skip("tool set_joint_loads", NoApi); }
            // They stand on nothing: out again before the analysis, the frames, then their joints.
            if (made.Count > 0) T("delete_objects", $$"""{"frames":{{List(made)}}}""", null, "tool delete_objects takes the test frames out again");
            var loose = Read(ctx, "the test joints", () => link.Points().Where(p => Math.Abs(p.X - 60) < 1e-4 || Math.Abs(p.X - 63) < 1e-4).Select(p => p.Name).ToList(), []);
            T("delete_joints", $$"""{"joints":{{List(loose.Count > 0 ? loose : ["no such joint"])}}}""", _ => FieldContext.Expect(!link.Points().Any(p => Math.Abs(p.X - 60) < 1e-4 || Math.Abs(p.X - 63) < 1e-4), "a test joint is still in the model"), "tool delete_joints takes the test joints out");
        }

        // ---- lateral loads, a second analysis, results through the API, the spectrum's scaling ----
        var analysed = false;
        if (offered.Contains("add_seismic_load"))
        {
            T("set_mass_source", """{"name":"CPM-MS","loads":[{"pattern":"CPM-SDL","factor":1}]}""", a => FieldContext.Expect(a["from_loads"] is not JsonArray l || l.Any(x => x!.GetValue<string>() == "CPM-SDL"), "CPM-SDL is not in the mass source when read back: " + a.ToJsonString()));
            T("set_modal_case", """{"name":"CPM-MODAL","max_modes":6}""", a => FieldContext.Expect(Int(a["max_modes"]) == 6, "6 modes were asked for: " + a.ToJsonString()));
            T("assign_diaphragm", """{"name":"CPM-D","all_floors":true}""", a => FieldContext.Expect(Int(a["done"]) > 0 || a["diaphragms"] is JsonArray { Count: > 0 }, "no floor took the diaphragm: " + a.ToJsonString()));
            T("add_seismic_load", """{"name":"CPM-ELFX","direction":"x","method":"user","coefficient":0.1,"k":1}""", a =>
                FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-ELFX" && p.Type == "Seismic") && a["read_back"] is not null, "CPM-ELFX should be a seismic pattern with its coefficient read back: " + a.ToJsonString()), "tool add_seismic_load, a user coefficient");
            T("add_seismic_load", """{"name":"CPM-ELFY","direction":"y","r":8,"omega":3,"cd":5.5,"ss":1.0,"s1":0.4,"site_class":"D"}""", a =>
                FieldContext.Expect(a["read_back"] is JsonObject r && r.ToJsonString().Contains('8'), "ASCE 7-16 with R 8 should read back: " + a.ToJsonString()), "tool add_seismic_load, ASCE 7-16");
            No("add_seismic_load", """{"name":"CPM-SDL","direction":"x","method":"user","coefficient":0.1}""", "is there as", "tool add_seismic_load says no to a pattern of another type");
            T("add_response_spectrum", """{"case":"CPM-RS","function":"CPM-ASCE716","direction":"x","ss":1.0,"s1":0.4,"site_class":"D","r":8,"modal_case":"CPM-MODAL"}""", a =>
                FieldContext.Expect(a["read_back"]?["loads"] is JsonArray l && l.Any(x => x!.GetValue<string>() == "U1"), "the case should read back a U1 load: " + a.ToJsonString()));
            if (live)
            {
                ctx.Step($"{name} analyses the model with the seismic loads, the spectrum and the mass source", () =>
                {
                    ctx.Call("run_analysis", "{}");
                    FieldContext.Expect(link.Info().Locked, $"the analysis answered, and {name} does not say the model is analysed");
                    return "analysed";
                });
                analysed = Read(ctx, "whether the model is analysed", () => link.Info().Locked, false);
            }
        }
        else if (live && !Read(ctx, "whether the model is analysed", () => link.Info().Locked, false))
        {
            ctx.Step($"{name} analyses the scratch model again", () => { ctx.Call("run_analysis", "{}"); return link.Info().Locked ? "analysed" : "not analysed"; });
            analysed = Read(ctx, "whether the model is analysed", () => link.Info().Locked, false);
        }
        var none = analysed ? "" : " (no analysis: it must still answer)";
        T("analysis_status", "{}", a => FieldContext.Expect(!analysed || Int(a["finished"]) > 0, "the model is analysed and no case is finished: " + a.ToJsonString()));
        if (offered.Contains("joint_displacements"))
        {
            var results = analysed ? (Action<JsonNode, string>)((a, key) => FieldContext.Expect(Int(a["rows"]) > 0 || a[key] is JsonArray { Count: > 0 }, "no results after the analysis: " + Cut(a.ToJsonString()))) : null;
            T("joint_displacements", """{"limit":5}""", results is null ? null : a => results(a, "list"), "tool joint_displacements" + none);
            T("frame_forces", """{"limit":5}""", results is null ? null : a => results(a, "list"), "tool frame_forces" + none);
            T("base_reactions", "{}", results is null ? null : a => results(a, "base_reactions"), "tool base_reactions" + none);
            if (offered.Contains("modal_results")) T("modal_results", "{}", analysed ? a => FieldContext.Expect(a["modes"] is JsonArray { Count: > 0 }, "no modes: " + a.ToJsonString()) : null, "tool modal_results" + none);
        }
        if (offered.Contains("scale_response_spectrum"))
        {
            if (analysed)
            {
                T("scale_response_spectrum", """{"rs_case":"CPM-RS","elf_case":"CPM-ELFX","direction":"x"}""", a =>
                    FieldContext.Expect(a["rs_base_shear"]?.GetValue<double>() > 0 && a["elf_base_shear"]?.GetValue<double>() > 0, "both base shears were expected: " + a.ToJsonString()), "tool scale_response_spectrum compares");
                T("scale_response_spectrum", """{"rs_case":"CPM-RS","elf_case":"CPM-ELFX","direction":"x","apply":true}""", a =>
                    FieldContext.Expect(a["applied"]?.GetValue<bool>() == true || a["passes"]?.GetValue<bool>() == true, "it should scale, or say there is nothing to scale: " + a.ToJsonString()), "tool scale_response_spectrum scales");
            }
            else No("scale_response_spectrum", """{"rs_case":"CPM-RS","elf_case":"CPM-ELFX","direction":"x"}""", "analysis", "tool scale_response_spectrum says the analysis must run first");
        }
        if (Read(ctx, "whether the model is locked", () => link.Info().Locked, false) || !locked)
            T("unlock_model", "{}", a => FieldContext.Expect(a["locked"]?.GetValue<bool>() == false, "still locked: " + a.ToJsonString()));
        if (offered.Contains("add_wind_load"))
            T("add_wind_load", """{"name":"CPM-WX","speed":110,"angle":0,"exposure":"C"}""", a =>
                FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-WX" && p.Type == "Wind"), "CPM-WX should be a wind pattern: " + a.ToJsonString()));

        // ---- checkpoints of the scratch model's file ----
        // The open model is told by its name without the file type: ETABS 23 answers its working file (name.$et) for a
        // model saved as name.edb, which failed both comparisons below in the 0.4.17 run.
        var scratchFile = Read(ctx, "the scratch model's file", () => link.Info().File, "");
        string? checkpoint = null;
        T("save_checkpoint", """{"name":"cpm-cp1"}""", a =>
        {
            checkpoint = a["file"]!.GetValue<string>();
            FieldContext.Expect(File.Exists(checkpoint), "the checkpoint's file is not there: " + a.ToJsonString());
            FieldContext.Expect(ScratchProgram.SameName(link.Info().File, scratchFile), $"{name} should have the scratch model open again after the checkpoint; it has {link.Info().File}");
        });
        ctx.Tool("list_checkpoints", "{}", a => FieldContext.Expect(!live || Int(a["count"]) >= 1, "the checkpoint is not listed: " + a.ToJsonString()));
        var after = live && Read(ctx, "a pattern added after the checkpoint", () => { link.AddLoadPattern("CPM-AFTER-CP", "Other", 0); return true; }, false);
        T("restore_checkpoint", """{"name":"cpm-cp1"}""", _ =>
            FieldContext.Expect(!after || !link.LoadPatterns().Any(p => p.Name == "CPM-AFTER-CP"), "the pattern added after the checkpoint is still there"));
        No("restore_checkpoint", """{"name":"no-such-checkpoint"}""", "no checkpoint", "tool restore_checkpoint says no to a checkpoint that is not there");

        // ---- a model of its own: stories (ETABS), then a building in one call ----
        if (offered.Contains("set_stories"))
        {
            if (live)
                ctx.Step($"a blank {name} model for the stories", () =>
                {
                    link.Raw("start a blank model", m => { object model = m; ApiCall.Must(model, "InitializeNewModel", CsiTools.D(("Units", 6)), "start a new model"); return ApiCall.Must(model, "File.NewBlank", null, "start a blank model"); });
                    return "blank";
                });
            T("set_stories", """{"stories":[{"name":"CPM-L1","height":3.5},{"name":"CPM-L2","height":3}]}""", a =>
                FieldContext.Expect(link.Stories().Any(s => s.Name == "CPM-L2" && Math.Abs(s.Elevation - 6.5) < 1e-3), "CPM-L2 at 6.5 was expected: " + a.ToJsonString()));
        }
        T("generate_building", """{"spans_x":[6,6],"spans_y":[5],"stories":2,"story_height":3.2}""", a =>
        {
            if (isSafe)
                FieldContext.Expect(Int(a["slabs"]) == 2 && Int(a["supports"]) == 6 && link.Areas().Count == 2, "SAFE: 2 slabs on 6 supports were expected: " + a.ToJsonString());
            else
            {
                FieldContext.Expect(Int(a["columns"]) == 12 && Int(a["beams"]) == 14 && Int(a["slabs"]) == 4 && Int(a["supports"]) == 6, "12 columns, 14 beams, 4 slabs and 6 supports were expected: " + a.ToJsonString());
                FieldContext.Expect(link.Frames().Count == 26 && link.Areas().Count == 4, $"the new model should hold 26 frames and 4 areas; {name} has {link.Frames().Count} and {link.Areas().Count}");
            }
        });

        // The scratch model back in the window, under its own name, for the parts that follow (CSI ProMax's own tools).
        if (live && checkpoint is not null && scratchFile.Length > 0)
            ctx.Step($"the scratch model is open again in the scratch {name} window", () =>
            {
                link.Raw("open the scratch model again", m =>
                {
                    object model = m;
                    ApiCall.Must(model, "File.OpenFile", CsiTools.D(("FileName", checkpoint)), "open the checkpoint of the scratch model");
                    return ApiCall.Must(model, "File.Save", CsiTools.D(("FileName", scratchFile)), "save it as the scratch model");
                });
                FieldContext.Expect(ScratchProgram.SameName(link.Info().File, scratchFile), $"{name} has {link.Info().File} open, not the scratch model");
                return Path.GetFileName(scratchFile);
            });
    }
}
