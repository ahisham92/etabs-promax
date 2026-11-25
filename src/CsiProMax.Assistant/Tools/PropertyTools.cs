using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's Define and Assign items for properties: materials of every type, frame sections of every shape,
/// slab and wall types, property modifiers, member end releases, local axes, insertion points, end offsets and
/// springs (point, line, area). Modeler only; written into the .e2k and .s2k.
/// </summary>
public static class PropertyTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Modeler's own Define or Assign item (no CSI program behind it). In a live program use its own tools or edit_table.");

    private static T Core<T>(Func<T> work)
    {
        try { return work(); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        catch (EtabsException ex) { throw new ToolInputException(ex.Message); }
    }

    private const string Numbers = """{"type":"object","additionalProperties":{"type":"number"}}""";

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("define_material_data",
            "Adds or changes a material of any type (Define > Material Properties): Concrete, Steel, Rebar, Tendon, Aluminum, ColdFormed, Masonry or Other, with its grade and values in the model's units: WEIGHTPERVOLUME, E, U (Poisson), A (thermal), FC for concrete, FY FU FYE FUE for steel, rebar, tendon, cold formed and aluminum. 'was': its name now; sections keep it under a new name.",
            """{"type":"object","required":["name","type","values"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"type":{"type":"string"},"grade":{"type":"string"},"values":""" + Numbers + "}}",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var mat = Core(() => link.Edit(m => PropertyDefs.SetMaterial(m, a.Need("name"), Was(a), a.Need("type"), a.Text("grade") is { Length: > 0 } g ? g : null, Values(a, "values", true))));
                return new JsonObject { ["material"] = mat.Name, ["type"] = mat.Type };
            }, a => $"Define the material {a.Text("name")}"),
        new("define_frame_shape",
            "Adds or changes a frame section of any shape (Define > Section Properties > Frame Sections): Concrete Rectangular, Circle, Tee, L; Steel I/Wide Flange, Channel, Tee, Angle, Double Angle, Double Channel, Tube, Pipe, Rod. dims are the shape's dimensions (D, B, TF, TW, ... as section_shapes lists them). modifiers (AMOD A2MOD A3MOD JMOD I2MOD I3MOD MMOD WMOD) are the section's property modifiers. 'was': its name now; members keep it under a new name.",
            """{"type":"object","required":["name","shape","material","dims"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"shape":{"type":"string"},"material":{"type":"string"},"dims":""" + Numbers + ""","modifiers":""" + Numbers + "}}",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var dims = Values(a, "dims", true); var mods = Values(a, "modifiers", true);
                Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.FrameModifiers, mods); return 0; });
                var s = Core(() => link.Edit(m =>
                {
                    var sec = PropertyDefs.SetShape(m, a.Need("name"), Was(a), a.Need("shape"), a.Need("material"), dims);
                    if (mods.Count > 0) PropertyDefs.SetModifiers(sec.Lines, PropertyDefs.FrameModifiers, mods);
                    return sec;
                }));
                return new JsonObject { ["section"] = s.Name, ["shape"] = s.Shape, ["area"] = s.Area };
            }, a => $"Define the frame section {a.Text("name")}"),
        new("define_shell_type",
            "Adds or changes a slab or wall section with its type (Define > Section Properties > Slab or Wall Sections): kind slab or wall; slab_type Slab, Drop, Stiff, Ribbed, Waffle, Mat, Footing; modeling ShellThin, ShellThick, Membrane, Layered; one_way load distribution; modifiers F11MOD F22MOD F12MOD M11MOD M22MOD M12MOD V13MOD V23MOD MMOD WMOD (e.g. cracked slabs M11MOD 0.25).",
            """{"type":"object","required":["name","kind","thickness","material"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"kind":{"type":"string","enum":["slab","wall"]},"slab_type":{"type":"string"},"modeling":{"type":"string"},"thickness":{"type":"number"},"material":{"type":"string"},"one_way":{"type":"boolean"},"modifiers":""" + Numbers + "}}",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var mods = Values(a, "modifiers", true);
                Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.ShellModifiers, mods); return 0; });
                var wall = a.Need("kind").Equals("wall", StringComparison.OrdinalIgnoreCase);
                var s = Core(() => link.Edit(m =>
                {
                    var sec = PropertyDefs.SetShellType(m, a.Need("name"), Was(a), wall, a.Text("slab_type", "Slab"), a.Text("modeling", wall ? "ShellThin" : "ShellThin"), a.Number("thickness"), a.Need("material"), a.Flag("one_way"));
                    if (mods.Count > 0) PropertyDefs.SetModifiers(sec.Lines, PropertyDefs.ShellModifiers, mods);
                    return sec;
                }));
                return new JsonObject { ["section"] = s.Name, ["wall"] = s.IsWall, ["thickness"] = s.Thickness };
            }, a => $"Define the section {a.Text("name")}"),
        new("define_spring",
            "Adds or changes a spring property (Define > Spring Properties): kind point (UX UY UZ RX RY RZ, force per length or moment per radian), line (U1 U2 U3 R1, per length of member) or area (U1 U2 U3, per area, e.g. a soil subgrade modulus). delete: take it away (refused while assigned).",
            """{"type":"object","required":["kind","name"],"properties":{"kind":{"type":"string","enum":["point","line","area"]},"name":{"type":"string"},"values":""" + Numbers + ""","delete":{"type":"boolean"}}}""",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var kind = a.Need("kind").ToLowerInvariant(); var name = a.Need("name");
                if (a.Flag("delete")) { Core(() => link.Edit(m => { PropertyDefs.DeleteSpring(m, kind, name); return 0; })); return new JsonObject { ["deleted"] = name }; }
                var values = Values(a, "values", false);
                Core(() => link.Edit(m => { PropertyDefs.SetSpring(m, kind, name, values); return 0; }));
                return new JsonObject { ["spring"] = name, ["kind"] = kind };
            }, a => a.Flag("delete") ? $"Delete the spring {a.Text("name")}" : $"Define the spring {a.Text("name")}"),
        new("assign_frame_data",
            "Assigns to frames (label@story) what Assign > Frame offers: release_i and release_j (any of P V2 V3 T M2 M3; [] takes them off), angle (local axis 2 rotation, degrees), cardinal (insertion point 1 to 11: 10 centroid, 8 top center), offset_i, offset_j and rigid_zone (end length offsets, rigid zone factor 0 to 1), modifiers (AMOD A2MOD A3MOD JMOD I2MOD I3MOD MMOD WMOD) and spring (a line spring property; \"\" takes it off). Only what is given changes.",
            """{"type":"object","required":["objects"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"release_i":{"type":"array","items":{"type":"string"}},"release_j":{"type":"array","items":{"type":"string"}},"angle":{"type":"number"},"cardinal":{"type":"integer"},"offset_i":{"type":"number"},"offset_j":{"type":"number"},"rigid_zone":{"type":"number"},"modifiers":""" + Numbers + ""","spring":{"type":"string"}}}""",
            Ask.Changes, AssignFrames, a => "Assign frame data"),
        new("assign_shell_data",
            "Assigns to slabs and walls (label@story) what Assign > Shell offers: modifiers (F11MOD F22MOD F12MOD M11MOD M22MOD M12MOD V13MOD V23MOD MMOD WMOD) and spring (an area spring property, e.g. soil under a mat; \"\" takes it off).",
            """{"type":"object","required":["objects"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"modifiers":""" + Numbers + ""","spring":{"type":"string"}}}""",
            Ask.Changes, AssignShells, a => "Assign shell data"),
        new("assign_joint_data",
            "Assigns to joints (point@story) what Assign > Joint offers: restraint (any of UX UY UZ RX RY RZ; [] frees the joint) and spring (a point spring property; \"\" takes it off).",
            """{"type":"object","required":["objects"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"restraint":{"type":"array","items":{"type":"string"}},"spring":{"type":"string"}}}""",
            Ask.Changes, AssignJoints, a => "Assign joint data"),
        new("property_definitions",
            "What the property forms show: material types and each material's values, every section shape with its dimensions, the frame and shell sections with their modifiers, slab and modeling types, the spring properties with where they are used; with objects (label@story), what is assigned to each.",
            """{"type":"object","properties":{"objects":{"type":"array","items":{"type":"string"}}}}""",
            Ask.Never, (a, e) => Definitions(Offline(e).Model, a.Texts("objects"))),
    ];

    private static string? Was(JsonElement a) => a.Text("was") is { Length: > 0 } w ? w : null;

    private static Dictionary<string, double> Values(JsonElement a, string name, bool upper)
    {
        var o = new Dictionary<string, double>(StringComparer.Ordinal);
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return o;
        foreach (var x in v.EnumerateObject())
        {
            var key = x.Name.Trim().ToUpperInvariant();
            if (x.Value.ValueKind == JsonValueKind.Number) o[key] = x.Value.GetDouble();
            else if (x.Value.ValueKind == JsonValueKind.String && double.TryParse(x.Value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) o[key] = d;
            else throw new ToolInputException($"'{x.Name}' in {name} needs a number.");
        }
        return o;
    }

    private static List<(string Label, string Story)> Objects(JsonElement a, Func<string, string, bool> exists, string what)
    {
        var list = new List<(string, string)>();
        foreach (var o in a.Texts("objects"))
        {
            var at = o.LastIndexOf('@');
            if (at <= 0) throw new ToolInputException($"'{o}' is not label@story.");
            var (label, story) = (o[..at], o[(at + 1)..]);
            if (!exists(label, story)) throw new ToolInputException($"There is no {what} {o}.");
            list.Add((label, story));
        }
        if (list.Count == 0) throw new ToolInputException("Name the objects, as label@story.");
        return list;
    }

    private static bool Has(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static string? Spring(JsonElement a, StructuralModel m, string kind)
    {
        if (!Has(a, "spring")) return null;
        var s = a.Text("spring");
        if (s.Length > 0 && !PropertyDefs.Springs(m).Exists(x => x.Kind == kind && x.Name == s)) throw new ToolInputException($"There is no {kind} spring '{s}'. Define it with define_spring first.");
        return s;
    }

    private static JsonNode AssignFrames(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var m0 = link.Model;
        var objects = Objects(a, (l, s) => m0.LineAssigns.Exists(x => x.Label == l && x.Story == s), "frame");
        List<string>? ri = Has(a, "release_i") ? a.Texts("release_i").Select(x => x.ToUpperInvariant()).ToList() : null;
        List<string>? rj = Has(a, "release_j") ? a.Texts("release_j").Select(x => x.ToUpperInvariant()).ToList() : null;
        double? angle = Has(a, "angle") ? a.Number("angle") : null;
        int? cardinal = Has(a, "cardinal") ? a.Whole("cardinal", 10) : null;
        if (cardinal is < 1 or > 11) throw new ToolInputException("cardinal is an insertion point from 1 to 11 (10 is the centroid).");
        double? oi = Has(a, "offset_i") ? a.Number("offset_i") : null, oj = Has(a, "offset_j") ? a.Number("offset_j") : null, rz = Has(a, "rigid_zone") ? a.Number("rigid_zone") : null;
        if (oi < 0 || oj < 0) throw new ToolInputException("End offsets are lengths, 0 or more.");
        if (rz is < 0 or > 1) throw new ToolInputException("The rigid zone factor is from 0 to 1.");
        var mods = Values(a, "modifiers", true);
        var spring = Spring(a, m0, "line");
        if (ri is null && rj is null && angle is null && cardinal is null && oi is null && oj is null && rz is null && mods.Count == 0 && spring is null)
            throw new ToolInputException("Give what to assign: release_i, release_j, angle, cardinal, offset_i, offset_j, rigid_zone, modifiers or spring.");
        Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.FrameModifiers, mods); return 0; });
        foreach (var (label, story) in objects)
        {
            var (i0, j0) = PropertyDefs.ReleasesOf(m0.LineAssigns.First(y => y.Label == label && y.Story == story));
            Core(() => { PropertyDefs.CheckReleases(ri ?? i0, rj ?? j0); return 0; });
            var line = m0.Line(label);
            if (line is null) continue;
            var len = Physical.Frames(m0).FirstOrDefault(f => f.Label == label && f.Story == story)?.Length ?? double.MaxValue;
            if ((oi ?? 0) + (oj ?? 0) >= len) throw new ToolInputException($"The end offsets of {label}@{story} are longer than the member ({len:0.###}).");
        }
        return Core(() => link.Edit(m =>
        {
            foreach (var (label, story) in objects)
            {
                var x = m.LineAssigns.First(y => y.Label == label && y.Story == story);
                if (ri is not null || rj is not null)
                {
                    var (i0, j0) = PropertyDefs.ReleasesOf(x);
                    PropertyDefs.SetReleases(x, ri ?? i0, rj ?? j0);
                }
                if (angle is { } ang) PropertyDefs.Put(x.Extra, "ANG", Math.Abs(ang % 360) < 1e-12 ? null : E2k.E2kText.Number(ang));
                if (cardinal is { } c) PropertyDefs.Put(x.Extra, "CARDINALPT", c.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (oi is { } a1) PropertyDefs.Put(x.Extra, "LENGTHOFFI", E2k.E2kText.Number(a1));
                if (oj is { } a2) PropertyDefs.Put(x.Extra, "LENGTHOFFJ", E2k.E2kText.Number(a2));
                if (rz is { } r) PropertyDefs.Put(x.Extra, "RIGIDZONE", E2k.E2kText.Number(r));
                if (mods.Count > 0) PropertyDefs.SetModifiers(x.Extra, PropertyDefs.FrameModifiers, mods);
                if (spring is not null) PropertyDefs.Put(x.Extra, "SPRINGPROP", spring.Length == 0 ? null : E2k.E2kText.Quote(spring));
            }
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        }));
    }

    private static JsonNode AssignShells(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var m0 = link.Model;
        var objects = Objects(a, (l, s) => m0.AreaAssigns.Exists(x => x.Label == l && x.Story == s), "slab or wall");
        var mods = Values(a, "modifiers", true);
        var spring = Spring(a, m0, "area");
        if (mods.Count == 0 && spring is null) throw new ToolInputException("Give what to assign: modifiers or spring.");
        Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.ShellModifiers, mods); return 0; });
        return Core(() => link.Edit(m =>
        {
            foreach (var (label, story) in objects)
            {
                var x = m.AreaAssigns.First(y => y.Label == label && y.Story == story);
                if (mods.Count > 0) PropertyDefs.SetModifiers(x.Extra, PropertyDefs.ShellModifiers, mods);
                if (spring is not null) PropertyDefs.Put(x.Extra, "SPRINGPROP", spring.Length == 0 ? null : E2k.E2kText.Quote(spring));
            }
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        }));
    }

    private static readonly string[] Dofs = ["UX", "UY", "UZ", "RX", "RY", "RZ"];

    private static JsonNode AssignJoints(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var m0 = link.Model;
        var objects = Objects(a, (p, s) => m0.Point(p) is not null && m0.StoryIndex(s) >= 0, "joint");
        List<string>? restraint = Has(a, "restraint") ? a.Texts("restraint").Select(x => x.ToUpperInvariant()).ToList() : null;
        if (restraint?.FirstOrDefault(r => !Dofs.Contains(r)) is { } bad) throw new ToolInputException($"'{bad}' is not one of {string.Join(", ", Dofs)}.");
        var spring = Spring(a, m0, "point");
        if (restraint is null && spring is null) throw new ToolInputException("Give what to assign: restraint or spring.");
        return Core(() => link.Edit(m =>
        {
            foreach (var (point, story) in objects)
            {
                var x = m.PointAssigns.Find(y => y.Point == point && y.Story == story);
                if (x is null) m.PointAssigns.Add(x = new PointAssign { Point = point, Story = story });
                if (restraint is not null) x.Restraint = restraint.Count == 0 ? null : string.Join(" ", Dofs.Where(restraint.Contains));
                if (spring is not null) PropertyDefs.Put(x.Extra, "SPRINGPROP", spring.Length == 0 ? null : E2k.E2kText.Quote(spring));
            }
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        }));
    }

    private static JsonObject Numbers_(Dictionary<string, double> d) => new(d.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    private static JsonNode Definitions(StructuralModel m, List<string> objects)
    {
        var springs = PropertyDefs.Springs(m);
        var o = new JsonObject
        {
            ["material_types"] = new JsonArray(PropertyDefs.MaterialTypes.Select(t => (JsonNode)t).ToArray()),
            ["material_values"] = new JsonArray(PropertyDefs.MaterialValues.Select(v => (JsonNode)new JsonObject { ["key"] = v.Key, ["label"] = v.Label }).ToArray()),
            ["materials"] = new JsonArray(m.Materials.Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.Name, ["type"] = x.Type, ["grade"] = x.Get("GRADE"),
                ["values"] = new JsonObject(PropertyDefs.MaterialValues.Where(v => x.Number(v.Key) is not null).Select(v => KeyValuePair.Create(v.Key, (JsonNode?)JsonValue.Create(x.Number(v.Key)!.Value)))),
            }).ToArray()),
            ["shapes"] = new JsonArray(PropertyDefs.Shapes.Select(s => (JsonNode)new JsonObject { ["shape"] = s.Name, ["dims"] = new JsonArray(s.Dims.Select(d => (JsonNode)d).ToArray()), ["concrete"] = s.Concrete, ["about"] = s.About }).ToArray()),
            ["frame_sections"] = new JsonArray(m.FrameSections.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["shape"] = s.Shape, ["material"] = s.Material,
                ["dims"] = new JsonObject((PropertyDefs.Shape(s.Shape)?.Dims ?? ["D", "B"]).Where(d => s.Number(d) is not null).Select(d => KeyValuePair.Create(d, (JsonNode?)JsonValue.Create(s.Number(d)!.Value)))),
                ["modifiers"] = Numbers_(PropertyDefs.Modifiers(s.Lines, PropertyDefs.FrameModifiers)),
            }).ToArray()),
            ["shell_sections"] = new JsonArray(m.ShellSections.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["wall"] = s.IsWall, ["material"] = s.Material, ["thickness"] = s.Thickness,
                ["slab_type"] = s.Get("SLABTYPE"), ["modeling"] = s.Get("MODELINGTYPE"), ["one_way"] = s.Get("ONEWAYLOADDIST") == "Yes",
                ["modifiers"] = Numbers_(PropertyDefs.Modifiers(s.Lines, PropertyDefs.ShellModifiers)),
            }).ToArray()),
            ["slab_types"] = new JsonArray(PropertyDefs.SlabTypes.Select(t => (JsonNode)t).ToArray()),
            ["modeling_types"] = new JsonArray(PropertyDefs.ModelingTypes.Select(t => (JsonNode)t).ToArray()),
            ["frame_modifiers"] = new JsonArray(PropertyDefs.FrameModifiers.Select(t => (JsonNode)t).ToArray()),
            ["shell_modifiers"] = new JsonArray(PropertyDefs.ShellModifiers.Select(t => (JsonNode)t).ToArray()),
            ["springs"] = new JsonArray(springs.Select(s => (JsonNode)new JsonObject
            {
                ["kind"] = s.Kind, ["name"] = s.Name, ["values"] = Numbers_(s.Values), ["used_by"] = PropertyDefs.SpringUsers(m, s.Kind, s.Name).Count(),
            }).ToArray()),
        };
        if (objects.Count > 0)
        {
            var list = new JsonArray();
            foreach (var x in objects)
            {
                var at = x.LastIndexOf('@'); if (at <= 0) continue;
                var (label, story) = (x[..at], x[(at + 1)..]);
                if (m.LineAssigns.Find(y => y.Label == label && y.Story == story) is { } l)
                {
                    var (i, j) = PropertyDefs.ReleasesOf(l);
                    list.Add(new JsonObject
                    {
                        ["object"] = x, ["kind"] = "frame", ["section"] = l.Section,
                        ["release_i"] = new JsonArray(i.Select(r => (JsonNode)r).ToArray()), ["release_j"] = new JsonArray(j.Select(r => (JsonNode)r).ToArray()),
                        ["angle"] = double.TryParse(PropertyDefs.Value(l.Extra, "ANG"), System.Globalization.CultureInfo.InvariantCulture, out var ang) ? ang : 0,
                        ["cardinal"] = int.TryParse(PropertyDefs.Value(l.Extra, "CARDINALPT"), out var c) ? c : 10,
                        ["offset_i"] = Num(PropertyDefs.Value(l.Extra, "LENGTHOFFI")), ["offset_j"] = Num(PropertyDefs.Value(l.Extra, "LENGTHOFFJ")), ["rigid_zone"] = Num(PropertyDefs.Value(l.Extra, "RIGIDZONE")),
                        ["modifiers"] = Numbers_(PropertyDefs.Modifiers(l.Extra, PropertyDefs.FrameModifiers)), ["spring"] = PropertyDefs.Value(l.Extra, "SPRINGPROP") ?? "",
                    });
                }
                else if (m.AreaAssigns.Find(y => y.Label == label && y.Story == story) is { } ar)
                    list.Add(new JsonObject { ["object"] = x, ["kind"] = "shell", ["section"] = ar.Section, ["modifiers"] = Numbers_(PropertyDefs.Modifiers(ar.Extra, PropertyDefs.ShellModifiers)), ["spring"] = PropertyDefs.Value(ar.Extra, "SPRINGPROP") ?? "" });
                else if (m.PointAssigns.Find(y => y.Point == label && y.Story == story) is { } p)
                    list.Add(new JsonObject { ["object"] = x, ["kind"] = "joint", ["restraint"] = p.Restraint ?? "", ["spring"] = PropertyDefs.Value(p.Extra, "SPRINGPROP") ?? "" });
                else if (m.Point(label) is not null)
                    list.Add(new JsonObject { ["object"] = x, ["kind"] = "joint", ["restraint"] = "", ["spring"] = "" });
            }
            o["objects"] = list;
        }
        return o;
    }

    private static double Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}
