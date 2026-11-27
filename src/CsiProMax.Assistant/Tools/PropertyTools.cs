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
    /// <summary>Puts a form's other fields (extras) in the program's table row of the definition; blank takes a field off.</summary>
    private static void SetRowExtras(StructuralModel m, JsonElement a, string table, string name)
    {
        if (!a.TryGetProperty("extras", out var x) || x.ValueKind != JsonValueKind.Object) return;
        var now = new Dictionary<string, string>(StructureExtras.Get(m, table, name));
        foreach (var p in x.EnumerateObject())
        {
            var v = (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString()).Trim();
            var key = StructureExtras.Fields[table].First(f => f.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (v.Length == 0) now.Remove(key); else now[key] = v;
        }
        StructureExtras.Remove(m, table, name);
        if (now.Count > 0) StructureExtras.Set(m, table, name, now);
    }

    /// <summary>Refuses extras the form has no field for before anything changes (a refusal leaves the model as it was).</summary>
    private static void CheckExtras(JsonElement a, string[] fields, string what)
    {
        if (!a.TryGetProperty("extras", out var x) || x.ValueKind != JsonValueKind.Object) return;
        foreach (var p in x.EnumerateObject())
            if (!fields.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                throw new ToolInputException($"{what} has no field '{p.Name}'. It has: {string.Join(", ", fields)}.");
    }

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
            "Adds or changes a material of any type (Define > Material Properties, with its Design, Nonlinear, Damping and Time Dependent forms): Concrete, Steel, Rebar, Tendon, Aluminum, ColdFormed, Masonry or Other, with its grade and values in the model's units: WEIGHTPERVOLUME, E, U (Poisson), A (thermal), FC for concrete, FY FU FYE FUE for steel, rebar, tendon, cold formed and aluminum; the nonlinear strains and acceptance criteria STRAINATFC STRAINATULTIMATE STRAINATHARDENING STRAINATMAXSTRESS STRAINATRUPTURE FINALSLOPE ENERGYDEGFACT IOTENSION LSTENSION CPTENSION IOCOMPRESSION LSCOMPRESSION CPCOMPRESSION. texts: HYSTYPE (hysteresis), SSTYPE (stress-strain curve), TIMEDEPCONCCODE. tables: everything else the forms hold, as rows of the program's own material tables by name (property_definitions lists them with their fields), e.g. {\"Damping Parameters\":[{\"ModalRatio\":\"0.05\"}], \"General\":[{\"Notes\":\"...\",\"Color\":\"Yellow\"}], \"Concrete Data\":[{\"LtWtConc\":\"Yes\",\"LtWtFact\":\"0.85\"}], \"Time Dependence - CEB-FIP 90\":[{...}]}; [] takes a table off. 'was': its name now; sections keep it under a new name.",
            """{"type":"object","required":["name","type","values"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"type":{"type":"string"},"grade":{"type":"string"},"values":""" + Numbers + ""","texts":{"type":"object","additionalProperties":{"type":"string"}},"tables":{"type":"object","additionalProperties":{"type":"array","items":{"type":"object","additionalProperties":{"type":["string","number","boolean"]}}}}}}""",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var mat = Core(() => link.Edit(m => PropertyDefs.SetMaterial(m, a.Need("name"), Was(a), a.Need("type"), a.Text("grade") is { Length: > 0 } g ? g : null, Values(a, "values", true), Texts(a), RowsOf(a))));
                return new JsonObject { ["material"] = mat.Name, ["type"] = mat.Type };
            }, a => $"Define the material {a.Text("name")}"),
        new("define_frame_shape",
            "Adds or changes a frame section of any shape (Define > Section Properties > Frame Sections): Concrete Rectangular, Circle, Tee, L; Steel I/Wide Flange, Channel, Tee, Angle, Double Angle, Double Channel, Tube, Pipe, Rod. dims are the shape's dimensions (D, B, TF, TW, ... as section_shapes lists them). modifiers (AMOD A2MOD A3MOD JMOD I2MOD I3MOD MMOD WMOD) are the section's property modifiers. extras: the form's other fields, by the program's table field names (Color, Notes; concrete NotSizeType NotAutoFact NotUserSize RigidZone; steel FilletRad, tube CornerRad; MirrorAbt2 MirrorAbt3); given, they replace the ones it has. 'was': its name now; members keep it under a new name.",
            """{"type":"object","required":["name","shape","material","dims"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"shape":{"type":"string"},"material":{"type":"string"},"dims":""" + Numbers + ""","modifiers":""" + Numbers + ""","extras":{"type":"object","additionalProperties":{"type":"string"}}}}""",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var dims = Values(a, "dims", true); var mods = Values(a, "modifiers", true);
                Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.FrameModifiers, mods); return 0; });
                CheckExtras(a, PropertyDefs.FrameExtraFields(a.Need("shape")), "A " + a.Need("shape") + " section");
                var s = Core(() => link.Edit(m =>
                {
                    var sec = PropertyDefs.SetShape(m, a.Need("name"), Was(a), a.Need("shape"), a.Need("material"), dims);
                    if (mods.Count > 0) PropertyDefs.SetModifiers(sec.Lines, PropertyDefs.FrameModifiers, mods);
                    if (a.TryGetProperty("extras", out var x) && x.ValueKind == JsonValueKind.Object)
                        PropertyDefs.SetFrameExtras(sec, x.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString()));
                    return sec;
                }));
                return new JsonObject { ["section"] = s.Name, ["shape"] = s.Shape, ["area"] = s.Area };
            }, a => $"Define the frame section {a.Text("name")}"),
        new("define_shell_type",
            "Adds or changes a slab or wall section with its type (Define > Section Properties > Slab or Wall Sections): kind slab or wall; slab_type Slab, Drop, Stiff, Ribbed, Waffle, Mat, Footing; modeling ShellThin, ShellThick, Membrane, Layered; one_way load distribution; a Ribbed slab also takes overall_depth, rib_width_top, rib_width_bottom, rib_spacing and rib_direction (local axis 1 or 2 the ribs run along); modifiers F11MOD F22MOD F12MOD M11MOD M22MOD M12MOD V13MOD V23MOD MMOD WMOD (e.g. cracked slabs M11MOD 0.25). extras: the form's other fields (NotSizeType NotAutoFact NotUserSize Color Notes; a wall's RigidZone; MatAngle and BendThick for SAP2000); given, they replace the ones it has.",
            """{"type":"object","required":["name","kind","thickness","material"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"kind":{"type":"string","enum":["slab","wall"]},"slab_type":{"type":"string"},"modeling":{"type":"string"},"thickness":{"type":"number"},"material":{"type":"string"},"one_way":{"type":"boolean"},"overall_depth":{"type":"number"},"rib_width_top":{"type":"number"},"rib_width_bottom":{"type":"number"},"rib_spacing":{"type":"number"},"rib_direction":{"type":"integer"},"modifiers":""" + Numbers + ""","extras":{"type":"object","additionalProperties":{"type":"string"}}}}""",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var mods = Values(a, "modifiers", true);
                Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.ShellModifiers, mods); return 0; });
                var wall = a.Need("kind").Equals("wall", StringComparison.OrdinalIgnoreCase);
                CheckExtras(a, PropertyDefs.ShellExtraFields(wall), wall ? "A wall section" : "A slab section");
                var s = Core(() => link.Edit(m =>
                {
                    var sec = PropertyDefs.SetShellType(m, a.Need("name"), Was(a), wall, a.Text("slab_type", "Slab"), a.Text("modeling", wall ? "ShellThin" : "ShellThin"), a.Number("thickness"), a.Need("material"), a.Flag("one_way"),
                        a.TryGetProperty("overall_depth", out _) ? new PropertyDefs.Ribs(a.Number("overall_depth"), a.Number("rib_width_top"), a.Number("rib_width_bottom", a.Number("rib_width_top")), a.Number("rib_spacing"), a.Whole("rib_direction", 1)) : null);
                    if (mods.Count > 0) PropertyDefs.SetModifiers(sec.Lines, PropertyDefs.ShellModifiers, mods);
                    if (a.TryGetProperty("extras", out var x) && x.ValueKind == JsonValueKind.Object)
                        PropertyDefs.SetShellExtras(sec, x.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString()));
                    return sec;
                }));
                return new JsonObject { ["section"] = s.Name, ["wall"] = s.IsWall, ["thickness"] = s.Thickness };
            }, a => $"Define the section {a.Text("name")}"),
        new("define_spring",
            "Adds or changes a spring property (Define > Spring Properties): kind point (UX UY UZ RX RY RZ, force per length or moment per radian), line (U1 U2 U3 R1, per length of member) or area (U1 U2 U3, per area, e.g. a soil subgrade modulus). delete: take it away (refused while assigned).",
            """{"type":"object","required":["kind","name"],"properties":{"kind":{"type":"string","enum":["point","line","area"]},"name":{"type":"string"},"values":""" + Numbers + ""","delete":{"type":"boolean"},"extras":{"type":"object","additionalProperties":{"type":"string"},"description":"The form's other fields: point NonlinOpt (None, Compression Only, Tension Only), line NonlinOpt2 and NonlinOpt3, area NonlinOpt3; Color, Notes."}}}""",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var kind = a.Need("kind").ToLowerInvariant(); var name = a.Need("name");
                var table = StructureExtras.SpringTable(kind);
                if (a.Flag("delete")) { Core(() => link.Edit(m => { PropertyDefs.DeleteSpring(m, kind, name); StructureExtras.Remove(m, table, name); return 0; })); return new JsonObject { ["deleted"] = name }; }
                var values = Values(a, "values", false);
                CheckExtras(a, StructureExtras.Fields[table].ToArray(), "A " + kind + " spring");
                Core(() => link.Edit(m => { PropertyDefs.SetSpring(m, kind, name, values); SetRowExtras(m, a, table, name); return 0; }));
                return new JsonObject { ["spring"] = name, ["kind"] = kind };
            }, a => a.Flag("delete") ? $"Delete the spring {a.Text("name")}" : $"Define the spring {a.Text("name")}"),
        new("define_link",
            "Adds, changes or deletes a link property (Define > Section Properties > Link/Support Properties): type LINEAR, DAMPER, GAP, HOOK, PLASTIC (WEN), RUBBER ISOLATOR, FRICTION ISOLATOR, MULTILINEAR ELASTIC or MULTILINEAR PLASTIC, and per direction (U1 U2 U3 R1 R2 R3) its effective stiffness and damping, which the linear analysis uses. A nonlinear type's own curve goes in by Edit > Model Text.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"type":{"type":"string"},"dofs":{"type":"object","additionalProperties":{"type":"object","properties":{"stiffness":{"type":"number"},"damping":{"type":"number"}}}},"delete":{"type":"boolean"},"extras":{"type":"object","additionalProperties":{"type":"string"},"description":"Mass, Weight, RotInert1, RotInert2, RotInert3 (Total Mass and Weight), DefLength, DefArea (Factors For Line and Area Springs), Notes."}}}""",
            Ask.Changes, (a, e) =>
            {
                var link = Offline(e);
                var name = a.Need("name");
                if (a.Flag("delete")) { Core(() => link.Edit(m => { PropertyDefs.DeleteLink(m, name); StructureExtras.RemoveLink(m, name); return 0; })); return new JsonObject { ["deleted"] = name }; }
                CheckExtras(a, StructureExtras.LinkFields, "A link property");
                var dofs = new Dictionary<string, (double, double)>();
                if (a.TryGetProperty("dofs", out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var x in d.EnumerateObject())
                        dofs[x.Name.ToUpperInvariant()] = (x.Value.Number("stiffness", 0), x.Value.Number("damping", 0));
                Core(() => link.Edit(m =>
                {
                    PropertyDefs.SetLink(m, name, a.Text("type", "LINEAR"), dofs);
                    var kept = StructureExtras.Fields.Keys.Where(StructureExtras.IsLinkTable).Select(t => StructureExtras.Get(m, t, name)).FirstOrDefault(r => r.Count > 0) ?? new Dictionary<string, string>();
                    StructureExtras.RemoveLink(m, name);
                    var table = StructureExtras.LinkTable(a.Text("type", "LINEAR"));
                    if (kept.Count > 0) StructureExtras.Set(m, table, name, kept);
                    SetRowExtras(m, a, table, name);
                    return 0;
                }));
                return new JsonObject { ["link"] = name, ["directions"] = dofs.Count };
            }, a => a.Flag("delete") ? $"Delete the link {a.Text("name")}" : $"Define the link {a.Text("name")}"),
        new("assign_frame_data",
            "Assigns to frames (label@story) what Assign > Frame offers: release_i and release_j (any of P V2 V3 T M2 M3; [] takes them off), angle (local axis 2 rotation, degrees), cardinal (insertion point 1 to 11: 10 centroid, 8 top center), offset_i, offset_j and rigid_zone (end length offsets, rigid zone factor 0 to 1), modifiers (AMOD A2MOD A3MOD JMOD I2MOD I3MOD MMOD WMOD), spring (a line spring property; \"\" takes it off) and hinges (Assign > Frame > Hinges: auto hinges from the code's tables, each {at: relative distance 0 to 1, dof: M3 M2 P V2 V3 T P-M2-M3 P-M3 M3-V2}; [] takes them off; for a pushover)." +
            " tables: rows of the program's own assignment tables the other fields do not cover, by full table name, e.g. {\"Frame Assignments - Output Stations\": [{\"StationOpt\": \"Min Stations\", \"MinStations\": \"5\"}]} (property_definitions lists them under assign_tables; [] takes a table off). Only what is given changes.",
            """{"type":"object","required":["objects"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"release_i":{"type":"array","items":{"type":"string"}},"release_j":{"type":"array","items":{"type":"string"}},"angle":{"type":"number"},"cardinal":{"type":"integer"},"offset_i":{"type":"number"},"offset_j":{"type":"number"},"rigid_zone":{"type":"number"},"modifiers":""" + Numbers + ""","spring":{"type":"string"},"hinges":{"type":"array","items":{"type":"object","required":["at","dof"],"properties":{"at":{"type":"number"},"dof":{"type":"string"}}}},"tables":{"type":"object"}}}""",
            Ask.Changes, AssignFrames, a => "Assign frame data"),
        new("assign_shell_data",
            "Assigns to slabs and walls (label@story) what Assign > Shell offers: modifiers (F11MOD F22MOD F12MOD M11MOD M22MOD M12MOD V13MOD V23MOD MMOD WMOD) and spring (an area spring property, e.g. soil under a mat; \"\" takes it off); opening (Assign > Shell > Opening: true makes the area an opening, false a slab or wall again); tables: rows of the program's own assignment tables the other fields do not cover, by full table name, e.g. {\"Area Assignments - Additional Mass\": [{\"AddedMass\": \"0.1\"}]} (property_definitions lists them under assign_tables; [] takes a table off).",
            """{"type":"object","required":["objects"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"modifiers":""" + Numbers + ""","spring":{"type":"string"},"opening":{"type":"boolean"},"tables":{"type":"object"}}}""",
            Ask.Changes, AssignShells, a => "Assign shell data"),
        new("assign_joint_data",
            "Assigns to joints (point@story) what Assign > Joint offers: restraint (any of UX UY UZ RX RY RZ; [] frees the joint) and spring (a point spring property; \"\" takes it off); tables: rows of the program's own assignment tables the other fields do not cover, by full table name, e.g. {\"Joint Assignments - Additional Mass\": [{\"MassXY\": \"2\"}]} (property_definitions lists them under assign_tables; [] takes a table off).",
            """{"type":"object","required":["objects"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"restraint":{"type":"array","items":{"type":"string"}},"spring":{"type":"string"},"tables":{"type":"object"}}}""",
            Ask.Changes, AssignJoints, a => "Assign joint data"),
        new("property_definitions",
            "What the property forms show: material types and each material's values, every section shape with its dimensions, the frame and shell sections with their modifiers, slab and modeling types, the spring properties with where they are used; with objects (label@story), what is assigned to each.",
            """{"type":"object","properties":{"objects":{"type":"array","items":{"type":"string"}}}}""",
            Ask.Never, (a, e) => Definitions(Offline(e).Model, a.Texts("objects"))),
        new("section_properties",
            "The section properties of a frame shape and its dimensions in the model's units (Frame Section Property Data > Show Section Properties): area, torsional constant, moments and product of inertia, shear areas, section and plastic moduli, radii of gyration.",
            """{"type":"object","required":["shape","dims"],"properties":{"shape":{"type":"string"},"dims":""" + Numbers + "}}",
            Ask.Never, (a, e) =>
            {
                var p = CsiProMax.S2k.S2kWriter.PropsOf(a.Need("shape"), Values(a, "dims", true)) ?? throw new ToolInputException("These dimensions do not make a section of that shape.");
                var v = p;
                return new JsonObject
                {
                    ["A"] = v.A, ["J"] = v.J, ["I33"] = v.I33, ["I22"] = v.I22, ["I23"] = v.I23, ["As2"] = v.As2, ["As3"] = v.As3,
                    ["S33Top"] = v.S33Top > 0 ? v.S33Top : v.S33, ["S33Bot"] = v.S33Bot > 0 ? v.S33Bot : v.S33, ["S22Left"] = v.S22Left > 0 ? v.S22Left : v.S22, ["S22Right"] = v.S22Right > 0 ? v.S22Right : v.S22,
                    ["Z33"] = v.Z33, ["Z22"] = v.Z22, ["R33"] = Math.Sqrt(v.I33 / v.A), ["R22"] = Math.Sqrt(v.I22 / v.A),
                };
            }),
    ];

    private static Dictionary<string, string>? Texts(JsonElement a)
    {
        if (!a.TryGetProperty("texts", out var t) || t.ValueKind != JsonValueKind.Object) return null;
        return t.EnumerateObject().ToDictionary(x => x.Name.Trim().ToUpperInvariant(), x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString()!.Trim() : x.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>The 'tables' argument: rows of the program's own tables, by table name; every value as the tables hold it (text).</summary>
    private static Dictionary<string, List<Dictionary<string, string>>>? RowsOf(JsonElement a)
    {
        if (!a.TryGetProperty("tables", out var t) || t.ValueKind == JsonValueKind.Null) return null;
        if (t.ValueKind != JsonValueKind.Object) throw new ToolInputException("tables is {table name: [rows]}.");
        var o = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in t.EnumerateObject())
        {
            var rows = table.Value.ValueKind switch
            {
                JsonValueKind.Array => table.Value.EnumerateArray().ToList(),
                JsonValueKind.Object => [table.Value],
                _ => throw new ToolInputException($"The rows of '{table.Name}' are a list of {{field: value}}."),
            };
            o[table.Name.Trim()] = rows.Select(r => r.ValueKind != JsonValueKind.Object ? throw new ToolInputException($"A row of '{table.Name}' is {{field: value}}.")
                : r.EnumerateObject().ToDictionary(f => f.Name.Trim(), f => f.Value.ValueKind switch
                {
                    JsonValueKind.String => f.Value.GetString() ?? "",
                    JsonValueKind.True => "Yes", JsonValueKind.False => "No",
                    JsonValueKind.Null => "",
                    _ => f.Value.GetRawText(),
                }, StringComparer.Ordinal)).ToList();
        }
        return o;
    }

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
        var hinges = Hinges(a);
        var tables = AssignRows(a);
        if (ri is null && rj is null && angle is null && cardinal is null && oi is null && oj is null && rz is null && mods.Count == 0 && spring is null && hinges is null && tables is null)
            throw new ToolInputException("Give what to assign: release_i, release_j, angle, cardinal, offset_i, offset_j, rigid_zone, modifiers, spring, hinges or tables.");
        if (tables is not null) Core(() => { AssignTables.Set(new LineAssign(), tables); return 0; });
        Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.FrameModifiers, mods); if (hinges is not null) PropertyDefs.CheckHinges(hinges); return 0; });
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
                if (hinges is not null) PropertyDefs.SetHinges(m, label, story, hinges);
                if (tables is not null) AssignTables.Set(x, tables);
            }
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        }));
    }

    private static List<PropertyDefs.HingeDef>? Hinges(JsonElement a)
    {
        if (!a.TryGetProperty("hinges", out var h) || h.ValueKind == JsonValueKind.Null) return null;
        if (h.ValueKind != JsonValueKind.Array) throw new ToolInputException("hinges is a list of {at, dof}.");
        var list = new List<PropertyDefs.HingeDef>();
        foreach (var x in h.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number || !x.TryGetProperty("dof", out var dof) || dof.ValueKind != JsonValueKind.String)
                throw new ToolInputException("Each hinge is {at: 0 to 1, dof: e.g. M3}.");
            list.Add(new PropertyDefs.HingeDef(at.GetDouble(), dof.GetString()!.Trim().ToUpperInvariant()));
        }
        return list;
    }

    private static JsonNode AssignShells(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var m0 = link.Model;
        var objects = Objects(a, (l, s) => m0.AreaAssigns.Exists(x => x.Label == l && x.Story == s), "slab or wall");
        var mods = Values(a, "modifiers", true);
        var spring = Spring(a, m0, "area");
        var tables = AssignRows(a);
        bool? opening = Has(a, "opening") ? a.Flag("opening") : null;
        if (mods.Count == 0 && spring is null && tables is null && opening is null) throw new ToolInputException("Give what to assign: modifiers, spring, opening or tables.");
        if (tables is not null) Core(() => { AssignTables.Set(new AreaAssign(), tables); return 0; });
        Core(() => { PropertyDefs.CheckModifiers(PropertyDefs.ShellModifiers, mods); return 0; });
        return Core(() => link.Edit(m =>
        {
            foreach (var (label, story) in objects)
            {
                var x = m.AreaAssigns.First(y => y.Label == label && y.Story == story);
                if (mods.Count > 0) PropertyDefs.SetModifiers(x.Extra, PropertyDefs.ShellModifiers, mods);
                if (spring is not null) PropertyDefs.Put(x.Extra, "SPRINGPROP", spring.Length == 0 ? null : E2k.E2kText.Quote(spring));
                if (opening is { } op) PropertyDefs.Put(x.Extra, "OPENING", op ? "\"Yes\"" : null);
                if (tables is not null) AssignTables.Set(x, tables);
            }
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        }));
    }

    /// <summary>The tables argument: rows by table name; true and false become Yes and No, as the tables hold them.</summary>
    internal static Dictionary<string, List<Dictionary<string, string>>>? AssignRows(JsonElement a)
    {
        if (!a.TryGetProperty("tables", out var t) || t.ValueKind == JsonValueKind.Null) return null;
        if (t.ValueKind != JsonValueKind.Object) throw new ToolInputException("tables is {table name: [rows]}.");
        var all = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in t.EnumerateObject())
        {
            var rows = table.Value.ValueKind switch
            {
                JsonValueKind.Array => table.Value.EnumerateArray().ToList(),
                JsonValueKind.Object => [table.Value],
                _ => throw new ToolInputException($"'{table.Name}' is a list of rows, each {{field: value}}."),
            };
            all[table.Name] = rows.Select(r => r.ValueKind != JsonValueKind.Object ? throw new ToolInputException($"Each row of '{table.Name}' is {{field: value}}.")
                : r.EnumerateObject().ToDictionary(f => f.Name, f => f.Value.ValueKind switch
                {
                    JsonValueKind.True => "Yes", JsonValueKind.False => "No", JsonValueKind.String => f.Value.GetString()!, JsonValueKind.Null => "",
                    _ => f.Value.GetRawText(),
                }, StringComparer.Ordinal)).ToList();
        }
        return all;
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
        var tables = AssignRows(a);
        if (restraint is null && spring is null && tables is null) throw new ToolInputException("Give what to assign: restraint, spring or tables.");
        if (tables is not null) Core(() => { AssignTables.Set(new PointAssign(), tables); return 0; });
        return Core(() => link.Edit(m =>
        {
            foreach (var (point, story) in objects)
            {
                var x = m.PointAssigns.Find(y => y.Point == point && y.Story == story);
                if (x is null) m.PointAssigns.Add(x = new PointAssign { Point = point, Story = story });
                if (restraint is not null) x.Restraint = restraint.Count == 0 ? null : string.Join(" ", Dofs.Where(restraint.Contains));
                if (spring is not null) PropertyDefs.Put(x.Extra, "SPRINGPROP", spring.Length == 0 ? null : E2k.E2kText.Quote(spring));
                if (tables is not null) AssignTables.Set(x, tables);
            }
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        }));
    }

    private static JsonNode TablesOf(Assign a) => JsonSerializer.SerializeToNode(a.Tables ?? new()) ?? new JsonObject();

    private static JsonObject Numbers_(Dictionary<string, double> d) => new(d.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    /// <summary>The groups an object is in, for its information window (Assignments > Groups).</summary>
    private static JsonArray GroupsOf(StructuralModel m, string type, string label, string story) =>
        new(GroupDefs.Groups(m).Where(g => g.Members.Contains((type, label, story))).Select(g => (JsonNode)g.Name).ToArray());

    private static JsonNode Definitions(StructuralModel m, List<string> objects)
    {
        var springs = PropertyDefs.Springs(m);
        var o = new JsonObject
        {
            ["program"] = m.Target,
            ["material_types"] = new JsonArray(ProgramOptions.MaterialTypes(m.Target).Select(t => (JsonNode)t).ToArray()),
            ["material_values"] = new JsonArray(PropertyDefs.MaterialValues.Select(v => (JsonNode)new JsonObject { ["key"] = v.Key, ["label"] = v.Label }).ToArray()),
            ["material_texts"] = JsonSerializer.SerializeToNode(PropertyDefs.MaterialTexts),
            ["material_nonlinear"] = new JsonObject(PropertyDefs.MaterialTypes.Select(t => KeyValuePair.Create(t, (JsonNode?)new JsonObject
            {
                ["keys"] = new JsonArray(PropertyDefs.NonlinearKeys(t).Select(k => (JsonNode)k).ToArray()),
                ["defaults"] = JsonSerializer.SerializeToNode(PropertyDefs.NonlinearKeys(t).Length > 0 ? PropertyDefs.NonlinearDefaults(t) : new()),
            }))),
            ["material_tables"] = JsonSerializer.SerializeToNode(ProgramTables.MaterialTables),
            ["assign_tables"] = new JsonObject(AssignTables.Tables.Where(t => ProgramOptions.HasAssignTable(m.Target, t.Key)).Select(t => KeyValuePair.Create(t.Key, (JsonNode?)new JsonObject
            {
                ["kind"] = t.Value.Kind, ["many"] = AssignTables.ManyRows.Contains(t.Key),
                ["fields"] = new JsonArray(t.Value.Fields.Select(f => (JsonNode)new JsonArray(f.Key, f.Label)).ToArray()),
            }))),
            ["materials"] = new JsonArray(m.Materials.Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.Name, ["type"] = x.Type, ["grade"] = x.Get("GRADE"),
                ["values"] = new JsonObject(PropertyDefs.MaterialValues.Select(v => v.Key).Concat(PropertyDefs.MaterialNonlinearValues).Where(k => x.Number(k) is not null).Select(k => KeyValuePair.Create(k, (JsonNode?)JsonValue.Create(x.Number(k)!.Value)))),
                ["texts"] = new JsonObject(PropertyDefs.MaterialTexts.Keys.Where(k => x.Get(k) is not null).Select(k => KeyValuePair.Create(k, (JsonNode?)x.Get(k)))),
                ["tables"] = JsonSerializer.SerializeToNode(x.Tables ?? new()),
            }).ToArray()),
            ["shapes"] = new JsonArray(PropertyDefs.Shapes.Where(s => ProgramOptions.HasShape(m.Target, s)).Select(s => (JsonNode)new JsonObject { ["shape"] = s.Name, ["dims"] = new JsonArray(s.Dims.Select(d => (JsonNode)d).ToArray()), ["concrete"] = s.Concrete, ["about"] = s.About }).ToArray()),
            ["frame_sections"] = new JsonArray(m.FrameSections.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["shape"] = s.Shape, ["material"] = s.Material,
                ["dims"] = new JsonObject((PropertyDefs.Shape(s.Shape)?.Dims ?? ["D", "B"]).Where(d => s.Number(d) is not null).Select(d => KeyValuePair.Create(d, (JsonNode?)JsonValue.Create(s.Number(d)!.Value)))),
                ["modifiers"] = Numbers_(PropertyDefs.Modifiers(s.Lines, PropertyDefs.FrameModifiers)),
                ["extras"] = new JsonObject(PropertyDefs.FrameExtras(s).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            }).ToArray()),
            ["frame_extra_fields"] = new JsonObject(PropertyDefs.Shapes.Select(x => KeyValuePair.Create(x.Name, (JsonNode?)new JsonArray(PropertyDefs.FrameExtraFields(x.Name).Select(f => (JsonNode)f).ToArray())))),
            ["shell_sections"] = new JsonArray(m.ShellSections.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["wall"] = s.IsWall, ["material"] = s.Material, ["thickness"] = s.Thickness,
                ["slab_type"] = s.Get("SLABTYPE"), ["modeling"] = s.Get("MODELINGTYPE"), ["one_way"] = s.Get("ONEWAYLOADDIST") == "Yes",
                ["overall_depth"] = s.Number("OVERALLDEPTH"), ["rib_width_top"] = s.Number("SLABRIBWIDTHTOP"), ["rib_width_bottom"] = s.Number("SLABRIBWIDTHBOTTOM"),
                ["rib_spacing"] = s.Number("SLABRIBSPACING"), ["rib_direction"] = s.Number("SLABRIBPARALLELTOAXIS"),
                ["modifiers"] = Numbers_(PropertyDefs.Modifiers(s.Lines, PropertyDefs.ShellModifiers)),
                ["extras"] = new JsonObject(PropertyDefs.ShellExtras(s).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            }).ToArray()),
            ["shell_extra_fields"] = new JsonObject { ["slab"] = new JsonArray(PropertyDefs.ShellExtraFields(false).Select(f => (JsonNode)f).ToArray()), ["wall"] = new JsonArray(PropertyDefs.ShellExtraFields(true).Select(f => (JsonNode)f).ToArray()) },
            ["slab_types"] = new JsonArray(ProgramOptions.SlabTypes(m.Target).Select(t => (JsonNode)t).ToArray()),
            ["modeling_types"] = new JsonArray(ProgramOptions.ModelingTypes(m.Target).Select(t => (JsonNode)t).ToArray()),
            ["frame_modifiers"] = new JsonArray(PropertyDefs.FrameModifiers.Select(t => (JsonNode)t).ToArray()),
            ["shell_modifiers"] = new JsonArray(PropertyDefs.ShellModifiers.Select(t => (JsonNode)t).ToArray()),
            ["hinge_dofs"] = new JsonArray(PropertyDefs.HingeDofs.Select(t => (JsonNode)t).ToArray()),
            ["link_types"] = new JsonArray(PropertyDefs.LinkTypes.Select(t => (JsonNode)t).ToArray()),
            ["links"] = new JsonArray(PropertyDefs.Links(m).Select(l => (JsonNode)new JsonObject
            {
                ["name"] = l.Name, ["type"] = l.Type, ["extras"] = new JsonObject(StructureExtras.Get(m, StructureExtras.LinkTable(l.Type), l.Name).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["dofs"] = new JsonObject(l.Dofs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject { ["stiffness"] = kv.Value.Stiff, ["damping"] = kv.Value.Damp }))),
            }).ToArray()),
            ["springs"] = new JsonArray(springs.Select(s => (JsonNode)new JsonObject
            {
                ["kind"] = s.Kind, ["name"] = s.Name, ["values"] = Numbers_(s.Values), ["used_by"] = PropertyDefs.SpringUsers(m, s.Kind, s.Name).Count(),
                ["extras"] = new JsonObject(StructureExtras.Get(m, StructureExtras.SpringTable(s.Kind), s.Name).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
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
                        ["hinges"] = new JsonArray(PropertyDefs.HingesOf(m, label, story).Select(h => (JsonNode)new JsonObject { ["at"] = h.At, ["dof"] = h.Dof }).ToArray()),
                        ["pier"] = PropertyDefs.Value(l.Extra, "PIER")?.Trim('"') ?? "", ["spandrel"] = PropertyDefs.Value(l.Extra, "SPANDREL")?.Trim('"') ?? "", ["groups"] = GroupsOf(m, "LINE", label, story),
                        ["tables"] = TablesOf(l),
                    });
                }
                else if (m.AreaAssigns.Find(y => y.Label == label && y.Story == story) is { } ar)
                    list.Add(new JsonObject
                    {
                        ["object"] = x, ["kind"] = "shell", ["section"] = ar.Section, ["modifiers"] = Numbers_(PropertyDefs.Modifiers(ar.Extra, PropertyDefs.ShellModifiers)), ["spring"] = PropertyDefs.Value(ar.Extra, "SPRINGPROP") ?? "",
                        ["diaphragm"] = ar.Diaphragm ?? "", ["opening"] = PropertyDefs.Value(ar.Extra, "OPENING") is { } op && op.Trim('"').Equals("Yes", StringComparison.OrdinalIgnoreCase),
                        ["angle"] = Num(PropertyDefs.Value(ar.Extra, "ANG")), ["mesh"] = PropertyDefs.Value(ar.Extra, "OBJMESHTYPE")?.Trim('"') ?? "",
                        ["pier"] = PropertyDefs.Value(ar.Extra, "PIER")?.Trim('"') ?? "", ["spandrel"] = PropertyDefs.Value(ar.Extra, "SPANDREL")?.Trim('"') ?? "", ["groups"] = GroupsOf(m, "AREA", label, story),
                        ["tables"] = TablesOf(ar),
                    });
                else if (m.PointAssigns.Find(y => y.Point == label && y.Story == story) is { } p)
                    list.Add(new JsonObject { ["object"] = x, ["kind"] = "joint", ["restraint"] = p.Restraint ?? "", ["spring"] = PropertyDefs.Value(p.Extra, "SPRINGPROP") ?? "", ["diaphragm"] = p.Diaphragm ?? "", ["groups"] = GroupsOf(m, "POINT", label, story), ["tables"] = TablesOf(p) });
                else if (m.Point(label) is not null)
                    list.Add(new JsonObject { ["object"] = x, ["kind"] = "joint", ["restraint"] = "", ["spring"] = "", ["diaphragm"] = "", ["groups"] = GroupsOf(m, "POINT", label, story), ["tables"] = new JsonObject() });
            }
            o["objects"] = list;
        }
        return o;
    }

    private static double Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}
