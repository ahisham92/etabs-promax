using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's Define and Assign items that ETABS's .e2k has no place for: SAP2000's joint constraints and moving
/// loads (lanes, vehicles, vehicle classes, moving load cases) and SAFE's hand-drawn design strips. Kept in the .cpm,
/// written into the .s2k (save_s2k) and the .f2k (save_safe_file). Modeler only.
/// </summary>
public static class ExtraTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Modeler's own Define or Assign item (no CSI program behind it). In a live program, use its own Define menu, or call_api.");

    private const string Del = ""","delete":{"type":"boolean"}""";

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("define_constraint",
            "Adds, changes or deletes a joint constraint (SAP2000 Define > Joint Constraints): Body, Diaphragm, Plate, Rod, Beam, Equal, Local or Weld. axis (X, Y, Z or Auto) is for diaphragm, plate, rod and beam; dofs (UX..RZ, or U1..R3 for local; default all) for body, equal, local and weld; tolerance for weld. Goes into the .s2k.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"type":{"type":"string"},"axis":{"type":"string"},"dofs":{"type":"array","items":{"type":"string"}},"tolerance":{"type":"number"}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { ExtrasDefs.DeleteConstraint(m, name); return new JsonObject { ["deleted"] = name }; }
                var c = ExtrasDefs.SetConstraint(m, name, a.Text("type", "Body"), a.Text("axis"), a.Texts("dofs"), a.Number("tolerance", 0));
                return new JsonObject { ["constraint"] = c.Name, ["type"] = c.Type, ["joints"] = c.Joints.Count };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the constraint {a.Text("name")}"),
        new("assign_joint_constraint",
            "Puts joints (point@story) in a joint constraint (Assign > Joint > Constraints); \"\" takes them out of every constraint.",
            """{"type":"object","required":["objects","constraint"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"constraint":{"type":"string"}}}""",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var joints = a.Texts("objects");
                if (joints.Count == 0) throw new ToolInputException("Name the joints, as point@story.");
                return new JsonObject { ["assigned"] = ExtrasDefs.AssignConstraint(m, joints, a.Text("constraint")) };
            }), a => a.Text("constraint").Length > 0 ? $"Assign the constraint {a.Text("constraint")}" : "Take joints out of their constraints"),
        new("define_lane",
            "Adds, changes or deletes a lane for moving loads (SAP2000 Define > Moving Loads > Lanes): along frames (label@story, in order), at an offset from them, of a width.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"frames":{"type":"array","items":{"type":"string"}},"offset":{"type":"number"},"width":{"type":"number"}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { ExtrasDefs.DeleteMoving(m, "lane", name); return new JsonObject { ["deleted"] = name }; }
                var l = ExtrasDefs.SetLane(m, name, a.Texts("frames"), a.Number("offset", 0), a.Number("width", 0));
                return new JsonObject { ["lane"] = l.Name, ["frames"] = l.Frames.Count };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the lane {a.Text("name")}"),
        new("define_vehicle",
            "Adds, changes or deletes a vehicle (Define > Moving Loads > Vehicles): a standard one (standard: HL-93K, HL-93M, HL-93S, HS20-44, HS15-44, H20-44, H15-44, P5..P13) or a general one by its axles, front to back, each {load, spacing from the axle before}, and a uniform load along it. scale multiplies it.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"standard":{"type":"string"},"scale":{"type":"number"},"axles":{"type":"array","items":{"type":"object","properties":{"load":{"type":"number"},"spacing":{"type":"number"}}}},"uniform":{"type":"number"}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { ExtrasDefs.DeleteMoving(m, "vehicle", name); return new JsonObject { ["deleted"] = name }; }
                var axles = Has(a, "axles") ? a.Items("axles").Select(x => new Axle { Load = x.Number("load"), Spacing = x.Number("spacing", 0) }).ToList() : [];
                var v = ExtrasDefs.SetVehicle(m, name, a.Text("standard"), a.Number("scale", 1), axles, a.Number("uniform", 0));
                return new JsonObject { ["vehicle"] = v.Name, ["standard"] = v.Standard, ["axles"] = v.Axles.Count };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the vehicle {a.Text("name")}"),
        new("define_vehicle_class",
            "Adds, changes or deletes a vehicle class (Define > Moving Loads > Vehicle Classes): vehicles, each {vehicle, scale}.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"vehicles":{"type":"array","items":{"type":"object","properties":{"vehicle":{"type":"string"},"scale":{"type":"number"}}}}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { ExtrasDefs.DeleteMoving(m, "vehicle class", name); return new JsonObject { ["deleted"] = name }; }
                var list = Has(a, "vehicles") ? a.Items("vehicles").Select(x => new ClassVehicle { Vehicle = x.Text("vehicle"), Scale = x.Number("scale", 1) }).ToList() : [];
                var c = ExtrasDefs.SetVehicleClass(m, name, list);
                return new JsonObject { ["vehicle_class"] = c.Name, ["vehicles"] = c.Vehicles.Count };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the vehicle class {a.Text("name")}"),
        new("define_moving_case",
            "Adds, changes or deletes a moving load case (Define > Load Cases > Moving Load): assignments, each {class, lanes, scale, min_loaded, max_loaded (0: all its lanes)}.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"assignments":{"type":"array","items":{"type":"object","properties":{"class":{"type":"string"},"lanes":{"type":"array","items":{"type":"string"}},"scale":{"type":"number"},"min_loaded":{"type":"integer"},"max_loaded":{"type":"integer"}}}}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { ExtrasDefs.DeleteMoving(m, "moving load case", name); return new JsonObject { ["deleted"] = name }; }
                var list = Has(a, "assignments") ? a.Items("assignments").Select(x => new MovingAssign
                {
                    Class = x.Text("class"), Lanes = x.Texts("lanes"), Scale = x.Number("scale", 1), MinLoaded = x.Whole("min_loaded", 1), MaxLoaded = x.Whole("max_loaded", 0),
                }).ToList() : [];
                var c = ExtrasDefs.SetMovingCase(m, name, list);
                return new JsonObject { ["moving_case"] = c.Name, ["assignments"] = c.Assignments.Count };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the moving load case {a.Text("name")}"),
        new("define_strip",
            "Adds, changes or deletes a design strip for ETABS or SAFE slab design (Draw > Draw Design Strips, with Design > Concrete Slab Design > Strip Overwrites): on a story (default: the one with the most slab), layer A or B, through points [[x, y], ...] on the slab, with its width to the left and right; design_type Column, Middle or No Design; design (default true), ignore_pt, rebar_material and llrf (live load reduction factor, default 1). It goes into the .e2k as ETABS writes strips, and into the .f2k beside the grid strips (replacing a grid strip of the same name).", // ETABS itself
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"story":{"type":"string"},"layer":{"type":"string","enum":["A","B"]},"points":{"type":"array","items":{"type":"array","items":{"type":"number"}}},"width_left":{"type":"number"},"width_right":{"type":"number"},"design_type":{"type":"string","enum":["Column","Middle","No Design"]},"design":{"type":"boolean"},"ignore_pt":{"type":"boolean"},"rebar_material":{"type":"string"},"llrf":{"type":"number"}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { SlabDefs.DeleteStrip(m, name); return new JsonObject { ["deleted"] = name }; }
                var points = Has(a, "points") ? a.Items("points").Select(p => { var (x, y) = p.Point(); return new[] { x, y }; }).ToList() : [];
                var story = a.Text("story");
                if (story.Length == 0) story = SlabDefs.Strips(m).Find(x => x.Name == name && x.Story.Length > 0)?.Story ?? F2k.F2kWriter.BusiestStory(m);
                var s = SlabDefs.SetStrip(m, name, story, a.Text("layer", "A"), points, a.Number("width_left", 0), a.Number("width_right", 0),
                    a.Text("design_type", "Column"), a.Flag("design", true), a.Flag("ignore_pt"), a.Text("rebar_material"), a.Number("llrf", 1));
                return new JsonObject { ["strip"] = s.Name, ["story"] = s.Story, ["layer"] = s.Layer, ["points"] = s.Plan.Count, ["design_type"] = s.DesignType };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the strip {a.Text("name")}"),
        new("define_tendon_section",
            "Adds, changes or deletes a tendon section (Define > Section Properties > Tendon Sections): its Tendon material (define_material_data type Tendon first) and the area of one strand, in the model's units. ETABS, SAFE and SAP2000.", // ETABS itself
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"material":{"type":"string"},"strand_area":{"type":"number"}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { SlabDefs.DeleteTendonSection(m, name); return new JsonObject { ["deleted"] = name }; }
                var t = SlabDefs.SetTendonSection(m, name, a.Need("material"), a.Number("strand_area"));
                return new JsonObject { ["tendon_section"] = t.Name, ["material"] = t.Material, ["strand_area"] = t.StrandArea };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the tendon section {a.Text("name")}"),
        new("draw_tendon",
            "Adds, changes or deletes a post-tensioning tendon in a slab (Draw > Draw Tendons, Assign > Tendon Loads), for ETABS, SAFE and SAP2000. " + // ETABS itself
            "story: the slab's story (the tendon's datum is the slab top there). points: its plan [[x, y], ...]. spans: its vertical profile, span by span along it: " +
            "length (0 or left out: the rest of the tendon, shared out), depth_left, depth_low and depth_right (the tendon's centroid below the slab top at the span's left end, low point and right end), " +
            "and a, b, c (default 0.1, 0.1, 0.5: the inflection points from each end and the low point, as fractions of the span; a = b = 0 is a simple parabola). " +
            "section (a tendon section), strands (default 1), bonded (default false). jacking: {transfer_pattern, final_pattern (load patterns of type Prestress-Transfer and Prestress-Final), stress, end: I, J or Both}. " +
            "losses: {method: Fixed (stressing and long_term are stresses lost) or Percent (of the jacking stress), stressing, long_term}.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"story":{"type":"string"},"points":{"type":"array","items":{"type":"array","items":{"type":"number"}}},""" +
            """ "spans":{"type":"array","items":{"type":"object","properties":{"length":{"type":"number"},"depth_left":{"type":"number"},"depth_low":{"type":"number"},"depth_right":{"type":"number"},"a":{"type":"number"},"b":{"type":"number"},"c":{"type":"number"}}}},""" +
            """ "section":{"type":"string"},"strands":{"type":"integer"},"bonded":{"type":"boolean"},""" +
            """ "jacking":{"type":"object","properties":{"transfer_pattern":{"type":"string"},"final_pattern":{"type":"string"},"stress":{"type":"number"},"end":{"type":"string","enum":["I","J","Both"]}}},""" +
            """ "losses":{"type":"object","properties":{"method":{"type":"string","enum":["Fixed","Percent"]},"stressing":{"type":"number"},"long_term":{"type":"number"}}}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { SlabDefs.DeleteTendon(m, name, a.Text("story") is { Length: > 0 } st ? st : null); return new JsonObject { ["deleted"] = name }; }
                var points = Has(a, "points") ? a.Items("points").Select(p => { var (x, y) = p.Point(); return new[] { x, y }; }).ToList() : [];
                var spans = Has(a, "spans") ? a.Items("spans").Select(s => new SlabDefs.SpanInput(s.Number("length", 0), s.Number("depth_left"), s.Number("depth_low"), s.Number("depth_right"),
                    s.Number("a", 0.1), s.Number("b", 0.1), s.Number("c", 0.5))).ToList() : [];
                SlabDefs.TendonJack? jack = null;
                if (Has(a, "jacking"))
                {
                    var j = a.GetProperty("jacking");
                    jack = new SlabDefs.TendonJack(j.Need("transfer_pattern"), j.Need("final_pattern"), j.Number("stress"), j.Text("end", "Both"));
                }
                SlabDefs.TendonLoss? loss = null;
                if (Has(a, "losses"))
                {
                    var l = a.GetProperty("losses");
                    loss = new SlabDefs.TendonLoss(l.Text("method", "Fixed"), l.Number("stressing", 0), l.Number("long_term", 0));
                }
                var story = a.Text("story");
                if (story.Length == 0) story = SlabDefs.Tendons(m).Find(x => x.Name == name)?.Story ?? F2k.F2kWriter.BusiestStory(m);
                var t = SlabDefs.SetTendon(m, name, story, points, spans, a.Need("section"), a.Whole("strands", 1), a.Flag("bonded"), jack, loss);
                return new JsonObject
                {
                    ["tendon"] = t.Name, ["story"] = t.Story, ["length"] = Math.Round(t.Length, 6), ["spans"] = t.Spans.Count, ["strands"] = t.Strands,
                    ["jacking"] = t.Jack is null ? "none" : $"{t.Jack.Stress} from {t.Jack.End}", ["losses"] = t.Loss is null ? "none" : $"{t.Loss.Stressing} at stressing, {t.Loss.LongTerm} long term",
                };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Draw")} the tendon {a.Text("name")}"),
        new("define_hyperstatic_case",
            "Adds or changes a hyperstatic load case (Define > Load Cases > Hyperstatic): the secondary forces of post-tensioning, from a linear static case that runs a prestress load pattern. Slab and beam design need it for PT. ETABS and SAFE.", // ETABS itself
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"base_case":{"type":"string"},"scale":{"type":"number"}""" + Del + """}}""",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                if (a.Flag("delete"))
                {
                    var name = a.Need("name");
                    var hp = m.LoadCases.Find(c => c.Name == name && AnalysisDefs.CaseType(c) == "Hyperstatic") ?? throw new ArgumentException($"There is no hyperstatic case '{name}'.");
                    var combo = m.Combos.Find(c => c.Lines.Any(l => l.Count >= 2 && l[0] == "LOADCASE" && E2k.E2kText.Unquote(l[1]) == name));
                    if (combo is not null) throw new ArgumentException($"The case '{name}' is in the combination {combo.Name}; take it out first.");
                    m.LoadCases.Remove(hp);
                    return new JsonObject { ["deleted"] = name };
                }
                var c = SlabDefs.SetHyperstatic(m, a.Need("name"), a.Need("base_case"), a.Number("scale", 1));
                return new JsonObject { ["load_case"] = c.Name, ["type"] = "Hyperstatic", ["base_case"] = a.Need("base_case") };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the hyperstatic case {a.Text("name")}"),
        new("extra_definitions",
            "What the joint constraint, moving load, design strip and tendon forms show: the constraint types, axes and standard vehicles, and the model's constraints (with their joints), lanes, vehicles, vehicle classes, moving load cases, strips, tendon sections and tendons (with each tendon's 3D shape).",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => Definitions(Offline(e).Model)),
    ];

    private static bool Has(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static JsonNode Core(ICsiLink e, Func<StructuralModel, JsonNode> work)
    {
        var link = Offline(e);
        try { return link.Edit(work); }
        catch (EtabsException ex) { throw new ToolInputException(ex.Message); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
    }

    private static JsonArray Strings(IEnumerable<string> list) => new(list.Select(s => (JsonNode)s).ToArray());

    private static JsonNode Definitions(StructuralModel m)
    {
        var x = m.Extras ?? new ProgramExtras();
        return new JsonObject
        {
            ["constraint_types"] = Strings(ExtrasDefs.ConstraintTypes),
            ["axes"] = Strings(ExtrasDefs.Axes),
            ["global_dofs"] = Strings(ExtrasDefs.GlobalDofs),
            ["local_dofs"] = Strings(ExtrasDefs.LocalDofs),
            ["standard_vehicles"] = Strings(ExtrasDefs.StandardVehicles),
            ["constraints"] = new JsonArray(x.Constraints.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["type"] = c.Type, ["axis"] = c.Axis, ["dofs"] = Strings(c.Dofs), ["tolerance"] = c.Tolerance, ["joints"] = Strings(c.Joints),
            }).ToArray()),
            ["lanes"] = new JsonArray(x.Lanes.Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["frames"] = Strings(l.Frames), ["offset"] = l.Offset, ["width"] = l.Width }).ToArray()),
            ["vehicles"] = new JsonArray(x.Vehicles.Select(v => (JsonNode)new JsonObject
            {
                ["name"] = v.Name, ["standard"] = v.Standard, ["scale"] = v.Scale, ["uniform"] = v.Uniform,
                ["axles"] = new JsonArray(v.Axles.Select(a => (JsonNode)new JsonObject { ["load"] = a.Load, ["spacing"] = a.Spacing }).ToArray()),
            }).ToArray()),
            ["vehicle_classes"] = new JsonArray(x.VehicleClasses.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["vehicles"] = new JsonArray(c.Vehicles.Select(v => (JsonNode)new JsonObject { ["vehicle"] = v.Vehicle, ["scale"] = v.Scale }).ToArray()),
            }).ToArray()),
            ["moving_cases"] = new JsonArray(x.MovingCases.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["assignments"] = new JsonArray(c.Assignments.Select(a => (JsonNode)new JsonObject { ["class"] = a.Class, ["lanes"] = Strings(a.Lanes), ["scale"] = a.Scale, ["min_loaded"] = a.MinLoaded, ["max_loaded"] = a.MaxLoaded }).ToArray()),
            }).ToArray()),
            ["strips"] = new JsonArray(SlabDefs.Strips(m).Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["story"] = s.Story, ["layer"] = s.Layer, ["width_left"] = s.WidthLeft, ["width_right"] = s.WidthRight,
                ["design_type"] = s.DesignType, ["design"] = s.Design, ["ignore_pt"] = s.IgnorePT, ["rebar_material"] = s.RebarMaterial, ["llrf"] = s.Llrf,
                ["points"] = new JsonArray(s.Plan.Select(p => (JsonNode)new JsonArray(p[0], p[1])).ToArray()),
            }).ToArray()),
            ["stories"] = Strings(m.Stories.Skip(1).Select(st => st.Name).Reverse()),
            ["slab_story"] = m.Areas.Exists(x => x.Kind == AreaKind.Floor) ? F2k.F2kWriter.BusiestStory(m) : m.Stories.Count > 1 ? m.Stories[^1].Name : "",
            ["tendon_materials"] = Strings(m.Materials.Where(x => x.Type.Equals("Tendon", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name)),
            ["rebar_materials"] = Strings(m.Materials.Where(x => x.Type.Equals("Rebar", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name)),
            ["prestress_transfer_patterns"] = Strings(m.LoadPatterns.Where(x => x.Type.Equals("Prestress-Transfer", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name)),
            ["prestress_final_patterns"] = Strings(m.LoadPatterns.Where(x => x.Type.Equals("Prestress-Final", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name)),
            ["hyperstatic_cases"] = new JsonArray(m.LoadCases.Where(c => AnalysisDefs.CaseType(c) == "Hyperstatic").Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["base_case"] = c.Lines.Select(l => E2k.E2kText.Value(l, "HYPERSTATICCASE")).FirstOrDefault(v => v is not null) ?? "",
                ["scale"] = c.Lines.Select(l => E2k.E2kText.TryNumber(E2k.E2kText.Value(l, "HYPERSTATICSF"), out var f) ? f : (double?)null).FirstOrDefault(v => v is not null) ?? 1,
            }).ToArray()),
            ["prestress_cases"] = Strings(m.LoadCases.Where(c => AnalysisDefs.CaseType(c) == "Linear Static" && c.Lines.Any(l => l.Count >= 2 && l[0] == "LOADPAT" && m.LoadPatterns.Find(pp => pp.Name == E2k.E2kText.Unquote(l[1]))?.Type.StartsWith("Prestress", StringComparison.OrdinalIgnoreCase) == true)).Select(c => c.Name)),
            ["tendon_sections"] = new JsonArray(SlabDefs.TendonSectionList(m).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["material"] = t.Material, ["strand_area"] = t.StrandArea }).ToArray()),
            ["tendons"] = new JsonArray(SlabDefs.Tendons(m).Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["story"] = t.Story, ["section"] = t.Section, ["strands"] = t.Strands, ["bonded"] = t.Bonded, ["length"] = Math.Round(t.Length, 6),
                ["points"] = new JsonArray(t.Plan.Select(p => (JsonNode)new JsonArray(p[0], p[1])).ToArray()),
                ["spans"] = new JsonArray(t.Spans.Select(sp => (JsonNode)new JsonObject
                {
                    ["length"] = Math.Round(sp.Length, 6), ["a"] = sp.A, ["b"] = sp.B, ["c"] = sp.C,
                    ["depth_left"] = Math.Round(t.Datum - sp.ZL, 6), ["depth_low"] = Math.Round(t.Datum - sp.ZC, 6), ["depth_right"] = Math.Round(t.Datum - sp.ZR, 6),
                }).ToArray()),
                ["jacking"] = t.Jack is null ? null : new JsonObject { ["transfer_pattern"] = t.Jack.TransferPattern, ["final_pattern"] = t.Jack.FinalPattern, ["stress"] = t.Jack.Stress, ["end"] = t.Jack.End },
                ["losses"] = t.Loss is null ? null : new JsonObject { ["method"] = t.Loss.Method, ["stressing"] = t.Loss.Stressing, ["long_term"] = t.Loss.LongTerm },
                ["shape"] = new JsonArray(t.Shape().Select(p => (JsonNode)new JsonArray(Math.Round(p[0], 6), Math.Round(p[1], 6), Math.Round(p[2], 6))).ToArray()),
            }).ToArray()),
        };
    }
}
