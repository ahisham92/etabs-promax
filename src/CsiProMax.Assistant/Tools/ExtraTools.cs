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
            "Adds, changes or deletes a design strip for SAFE (Draw > Draw Design Strips): layer A or B, through points [[x, y], ...] on the slab, with its width to the left and right. It goes into the .f2k (save_safe_file) beside the grid strips, and replaces a grid strip of the same name.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"layer":{"type":"string","enum":["A","B"]},"points":{"type":"array","items":{"type":"array","items":{"type":"number"}}},"width_left":{"type":"number"},"width_right":{"type":"number"}""" + Del + "}}",
            Ask.Changes, (a, e) => Core(e, m =>
            {
                var name = a.Need("name");
                if (a.Flag("delete")) { ExtrasDefs.DeleteStrip(m, name); return new JsonObject { ["deleted"] = name }; }
                var points = Has(a, "points") ? a.Items("points").Select(p => { var (x, y) = p.Point(); return new[] { x, y }; }).ToList() : [];
                var s = ExtrasDefs.SetStrip(m, name, a.Text("layer", "A"), points, a.Number("width_left", 0), a.Number("width_right", 0));
                return new JsonObject { ["strip"] = s.Name, ["layer"] = s.Layer, ["points"] = s.Points.Count };
            }), a => $"{(a.Flag("delete") ? "Delete" : "Define")} the strip {a.Text("name")}"),
        new("extra_definitions",
            "What the joint constraint, moving load and design strip forms show: the constraint types, axes and standard vehicles, and the model's constraints (with their joints), lanes, vehicles, vehicle classes, moving load cases and strips.",
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
            ["strips"] = new JsonArray(x.Strips.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["layer"] = s.Layer, ["width_left"] = s.WidthLeft, ["width_right"] = s.WidthRight,
                ["points"] = new JsonArray(s.Points.Select(p => (JsonNode)new JsonArray(p[0], p[1])).ToArray()),
            }).ToArray()),
        };
    }
}
