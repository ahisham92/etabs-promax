using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// The tools that are different in SAP2000. SAP2000 has no stories, so members go in by their end coordinates in 3D,
/// supports and loads go on joints, and results are read through SAP2000's analysis results (joint displacements,
/// member forces, base reactions, modes) and its design summaries. The ETABS tools that only make sense with stories
/// (add_columns, add_beams, add_slabs, add_walls, the story checks) are replaced by ones that say what to use instead.
/// Everything else (sections by name, selections, tables, combinations, analysis, saving) is shared with ETABS.
/// </summary>
public static class SapTools
{
    private const string NoStories = "SAP2000 has no stories.";

    private static Tool NotHere(string name, string instead) =>
        new(name, $"Not in SAP2000 ({NoStories.TrimEnd('.')}). {instead}", """{"type":"object","properties":{}}""", Ask.Never,
            (_, _) => throw new ToolInputException($"{NoStories} {instead}"));

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("get_model_context",
            "The open SAP2000 model at a glance: file, units, SAP2000's version, how many joints, frames (by vertical, level or sloping), areas and supports, the bounds of the model in X, Y and Z, the section, material, load pattern, load case, combination and group names, and what is selected. Call it once at the start of a conversation.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => Context(e)),

        new("query_joints",
            "Lists joints with their coordinates and supports. Filter by names, by a Z level (z, with tolerance), by supported joints only, or by what is selected.",
            """
            {"type":"object","properties":{
              "names":{"type":"array","items":{"type":"string"}},
              "z":{"type":"number","description":"Only joints at this elevation."},
              "tolerance":{"type":"number","description":"For z. Default 0.001 in model units."},
              "supported_only":{"type":"boolean"},
              "selected_only":{"type":"boolean"},
              "limit":{"type":"integer","description":"Default 100."}}}
            """, Ask.Never, QueryJoints),

        new("add_joints",
            "Adds joints at points in 3D, [x, y, z] in model units. A point on an existing joint gives that joint back.",
            """{"type":"object","required":["points"],"properties":{"points":{"type":"array","items":{"type":"array","items":{"type":"number"}}}}}""",
            Ask.Changes, AddJoints, a => $"Add {Count(a, "points")} joints"),

        new("add_frames",
            "Adds frame members (beams, columns, braces, truss members) between two points in 3D: each line is [x1, y1, z1, x2, y2, z2] in model units, or give 'joints' as pairs of joint names. Ends that fall on a joint join it.",
            """
            {"type":"object","required":["section"],"properties":{
              "lines":{"type":"array","items":{"type":"array","items":{"type":"number"}},"description":"[x1, y1, z1, x2, y2, z2] each."},
              "joints":{"type":"array","items":{"type":"array","items":{"type":"string"}},"description":"[jointI, jointJ] each."},
              "section":{"type":"string","description":"A frame section name from get_model_context."}}}
            """, Ask.Changes, AddFrames, a => $"Add {Count(a, "lines") + Count(a, "joints")} frames ({a.Text("section")})"),

        new("add_areas",
            "Adds area objects (slabs, walls, shells, plates): each outline is three or more [x, y, z] corners in order around the edge, in model units.",
            """
            {"type":"object","required":["outlines","section"],"properties":{
              "outlines":{"type":"array","items":{"type":"array","items":{"type":"array","items":{"type":"number"}}}},
              "section":{"type":"string","description":"An area section name from get_model_context."}}}
            """, Ask.Changes, AddAreas, a => $"Add {Count(a, "outlines")} areas ({a.Text("section")})"),

        new("add_material",
            "Adds a material: concrete (fc), steel or rebar (fy, fu), with its modulus of elasticity, Poisson's ratio, thermal coefficient and unit weight, in the model's units.",
            """
            {"type":"object","required":["name","type","elastic_modulus"],"properties":{
              "name":{"type":"string"},"type":{"type":"string","enum":["concrete","steel","rebar","other"]},
              "elastic_modulus":{"type":"number"},"poisson":{"type":"number","description":"Default 0.2 for concrete, 0.3 for steel."},
              "thermal":{"type":"number","description":"Default 1e-5 for concrete, 1.17e-5 for steel."},
              "unit_weight":{"type":"number","description":"Weight per volume. Default 0."},
              "fc":{"type":"number"},"fy":{"type":"number"},"fu":{"type":"number"}}}
            """, Ask.Changes, AddMaterial, a => $"Add the {a.Text("type")} material {a.Text("name")}"),

        new("add_frame_section",
            "Adds a frame section: rectangle (depth, width), circle (diameter), i_section (depth, flange width, flange and web thickness), tube (depth, width, flange and web thickness) or pipe (diameter, wall thickness), in the model's length unit, of a material in the model.",
            """
            {"type":"object","required":["name","shape","material"],"properties":{
              "name":{"type":"string"},"shape":{"type":"string","enum":["rectangle","circle","i_section","tube","pipe"]},
              "material":{"type":"string"},
              "depth":{"type":"number"},"width":{"type":"number"},"diameter":{"type":"number"},
              "flange_thickness":{"type":"number"},"web_thickness":{"type":"number"},"wall_thickness":{"type":"number"}}}
            """, Ask.Changes, AddFrameSection, a => $"Add the {a.Text("shape")} section {a.Text("name")}"),

        new("add_area_section",
            "Adds an area section: a shell (thin or thick), a plate or a membrane, with a thickness in the model's length unit and a material in the model.",
            """
            {"type":"object","required":["name","material","thickness"],"properties":{
              "name":{"type":"string"},"material":{"type":"string"},"thickness":{"type":"number"},
              "type":{"type":"string","enum":["shell_thin","shell_thick","plate_thin","plate_thick","membrane"],"description":"Default shell_thin."}}}
            """, Ask.Changes, AddAreaSection, a => $"Add the area section {a.Text("name")}"),

        new("set_restraints",
            "Sets joint supports: fixed, pinned, roller (only UZ) or free, or give 'dofs' (any of UX UY UZ RX RY RZ). At named joints, at every joint on a level (z), or at the lowest joints of the model (at_lowest).",
            """
            {"type":"object","properties":{
              "type":{"type":"string","enum":["fixed","pinned","roller","free"]},
              "dofs":{"type":"array","items":{"type":"string","enum":["UX","UY","UZ","RX","RY","RZ"]}},
              "joints":{"type":"array","items":{"type":"string"}},
              "z":{"type":"number"},"at_lowest":{"type":"boolean"}}}
            """, Ask.Changes, SetRestraints, a => $"Set {(a.Text("type").Length > 0 ? a.Text("type") : "custom")} supports"),

        new("set_joint_loads",
            "Puts forces and moments on joints in a load pattern, in global directions and model units (FZ is up, so gravity is negative).",
            """
            {"type":"object","required":["joints","pattern"],"properties":{
              "joints":{"type":"array","items":{"type":"string"}},"pattern":{"type":"string"},
              "fx":{"type":"number"},"fy":{"type":"number"},"fz":{"type":"number"},"mx":{"type":"number"},"my":{"type":"number"},"mz":{"type":"number"},
              "add":{"type":"boolean","description":"Add to the load already there. Default: replace it."}}}
            """, Ask.Changes, SetJointLoads, a => $"Load {Count(a, "joints")} joints in {a.Text("pattern")}"),

        new("set_member_loads",
            "Puts a uniform load on frames (force per length) or areas (force per area) in a given direction: gravity (down, positive), x, y or z (global), or local_1, local_2, local_3. For plain gravity loads set_loads does the same.",
            """
            {"type":"object","required":["pattern","value","direction"],"properties":{
              "frames":{"type":"array","items":{"type":"string"}},"areas":{"type":"array","items":{"type":"string"}},
              "pattern":{"type":"string"},"value":{"type":"number"},
              "direction":{"type":"string","enum":["gravity","x","y","z","local_1","local_2","local_3"]},
              "add":{"type":"boolean"}}}
            """, Ask.Changes, SetMemberLoads, a => $"Load {Count(a, "frames") + Count(a, "areas")} objects with {a.Number("value", 0)} ({a.Text("direction")}) in {a.Text("pattern")}"),

        new("set_releases",
            "Releases frame end moments: 'pinned' frees both bending moments (M2, M3) at the chosen ends, 'fixed' restores them. Ends: i, j or both.",
            """
            {"type":"object","required":["frames","type"],"properties":{
              "frames":{"type":"array","items":{"type":"string"}},
              "type":{"type":"string","enum":["pinned","fixed"]},
              "ends":{"type":"string","enum":["i","j","both"],"description":"Default both."}}}
            """, Ask.Changes, SetReleases, a => $"Make {Count(a, "frames")} frame ends {a.Text("type")}"),

        new("delete_joints",
            "Deletes joints that no frame or area uses. The engineer is always asked first.",
            """{"type":"object","required":["joints"],"properties":{"joints":{"type":"array","items":{"type":"string"}}}}""",
            Ask.Always, DeleteJoints, a => $"Delete {Count(a, "joints")} joints"),

        new("joint_displacements",
            "Joint displacements and rotations from SAP2000's analysis, for load cases or combinations. Gives the largest of each direction with its joint and case, and per joint up to 'limit' rows. Needs a finished analysis.",
            """
            {"type":"object","properties":{
              "joints":{"type":"array","items":{"type":"string"},"description":"Default: every joint."},
              "cases":{"type":"array","items":{"type":"string"}},"combinations":{"type":"array","items":{"type":"string"}},
              "limit":{"type":"integer","description":"Rows to list (default 50), largest first."}}}
            """, Ask.Never, JointDisplacements),

        new("frame_forces",
            "Frame member forces (P, V2, V3, T, M2, M3) from SAP2000's analysis at the output stations, for load cases or combinations. Gives the largest of each with its member, station and case, and per member its extremes. Needs a finished analysis.",
            """
            {"type":"object","properties":{
              "frames":{"type":"array","items":{"type":"string"},"description":"Default: every frame."},
              "cases":{"type":"array","items":{"type":"string"}},"combinations":{"type":"array","items":{"type":"string"}},
              "limit":{"type":"integer","description":"Members to list (default 50), largest moment first."}}}
            """, Ask.Never, FrameForces),

        new("base_reactions",
            "The total base reactions (FX, FY, FZ, MX, MY, MZ) from SAP2000's analysis for load cases or combinations, and the support reactions per joint when 'per_joint' is true. Needs a finished analysis.",
            """
            {"type":"object","properties":{
              "cases":{"type":"array","items":{"type":"string"}},"combinations":{"type":"array","items":{"type":"string"}},
              "per_joint":{"type":"boolean"}}}
            """, Ask.Never, BaseReactions),

        new("modal_results",
            "Periods, frequencies and modal participating mass ratios from SAP2000's modal case, and whether the cumulative mass reaches the target (default 90%) in UX and UY. Needs a finished analysis.",
            """{"type":"object","properties":{"case":{"type":"string","description":"Default: every modal case."},"target":{"type":"number","description":"Default 0.9."}}}""",
            Ask.Never, ModalResults),

        new("check_displacement",
            "Checks lateral (UX, UY) and vertical (UZ) joint displacements against limits: lateral against the model's height over 'lateral_limit' (default 500), vertical against 'vertical_limit' in model units when given. Reads SAP2000's results; nothing is computed but the comparison.",
            """
            {"type":"object","properties":{
              "cases":{"type":"array","items":{"type":"string"}},"combinations":{"type":"array","items":{"type":"string"}},
              "lateral_limit":{"type":"number","description":"Height divided by this is the allowed lateral displacement. Default 500."},
              "vertical_limit":{"type":"number","description":"Allowed vertical displacement in model units."}}}
            """, Ask.Never, CheckDisplacement),

        new("run_design",
            "Runs SAP2000's steel and concrete frame design (its own codes, as the model is set) on the analysed model. The engineer is always asked first.",
            """{"type":"object","properties":{"steel":{"type":"boolean","description":"Default true."},"concrete":{"type":"boolean","description":"Default true."}}}""",
            Ask.Always, RunDesign, _ => "Run steel and concrete frame design"),

        new("design_ratios",
            "Design results from SAP2000's design summaries: steel frames' demand/capacity ratios (with the governing combination and the members over 1.0), and concrete columns' PMM ratios or reinforcing and beams' reinforcing. Needs run_design first.",
            """
            {"type":"object","properties":{
              "frames":{"type":"array","items":{"type":"string"},"description":"Default: every frame."},
              "limit":{"type":"integer","description":"Members to list (default 50), highest ratio first."}}}
            """, Ask.Never, DesignRatios),

        new("check_report",
            "Runs SAP2000's checks in one go and gives one report: modal mass participation, lateral and vertical displacement, base reactions and design ratios. Each part says pass, fail, reported (values with no limit to hold them against) or not available (and why). Reads SAP2000's results only.",
            """
            {"type":"object","properties":{
              "cases":{"type":"array","items":{"type":"string"}},"combinations":{"type":"array","items":{"type":"string"}},
              "lateral_limit":{"type":"number","description":"Height over this is the allowed lateral displacement. Default 500."},
              "vertical_limit":{"type":"number"}}}
            """, Ask.Never, Report),

        NotHere("add_columns", "Use add_frames with both ends' x, y and z."),
        NotHere("add_beams", "Use add_frames with both ends' x, y and z."),
        NotHere("add_slabs", "Use add_areas with each corner's x, y and z."),
        NotHere("add_walls", "Use add_areas with each corner's x, y and z."),
        NotHere("check_drift", "Use check_displacement (lateral displacement against height), or read_table with \"Joint Displacements\"."),
        NotHere("check_torsion", "Compare joint displacements at the corners of a level with joint_displacements."),
        NotHere("check_story_irregularity", "Read the masses and stiffnesses with read_table, or compare levels with joint_displacements and base_reactions."),
    ];

    /// <summary>All of SAP2000's checks in one report, as the review pack and check_report give it.</summary>
    public static JsonNode Report(JsonElement a, ICsiLink e)
    {
        var info = e.Info();
        var parts = new (string Name, Func<JsonNode> Run, Func<JsonNode, string> Status)[]
        {
            ("modal", () => ModalResults(default, e), n => n["passes"]?.GetValue<bool>() == true ? Verdict.Pass : Verdict.Fail),
            ("displacement", () => CheckDisplacement(a, e), DisplacementStatus),
            ("base_reactions", () => BaseReactions(a, e), _ => Verdict.Reported),
            ("design_ratios", () => DesignRatios(default, e), n => n["steel"]?["over_1"] is JsonArray { Count: > 0 } ? Verdict.Fail : Verdict.Reported),
        };
        var checks = new JsonObject();
        var summary = new JsonArray();
        foreach (var (name, run, status) in parts)
        {
            JsonNode node;
            string state;
            // The one rule (Verdict): nothing passes or fails on a model that has not been analysed.
            if (Verdict.NotAnalysed(e, name.Replace('_', ' ')) is { } notRun) { node = notRun; state = Verdict.NotAvailable; }
            else
                try { node = run(); state = status(node); node["status"] = state; }
                // One check that cannot be made (also a call this version of the API does not have) is said in its place; the others still answer.
                catch (Exception ex) when (ex is EtabsException or ToolInputException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { state = Verdict.NotAvailable; node = new JsonObject { ["status"] = state, ["note"] = ex.Message }; }
            checks[name] = node;
            summary.Add($"{name.Replace('_', ' ')}: {state}");
        }
        return new JsonObject
        {
            ["program"] = "SAP2000", ["model"] = info.File, ["units"] = $"{info.Force}, {info.Length}", ["analysis_run"] = info.Locked,
            ["summary"] = summary, ["checks"] = checks,
            ["note"] = "Every value is read from SAP2000's own analysis and design results. Limits that were not given are the defaults named in each check; confirm them against the project's code.",
        };
    }

    /// <summary>
    /// The displacement check's verdict: fail when a limit is passed, pass when a limit was there to hold it against
    /// and held, and reported (no verdict) when there was no limit at all (a level model has no height for H/500, and
    /// the vertical limit is only checked when it is given).
    /// </summary>
    internal static string DisplacementStatus(JsonNode n)
    {
        bool? lateral = n["lateral"]?["allowed"] is null ? null : n["lateral"]?["passes"]?.GetValue<bool>();
        bool? vertical = n["vertical"]?["allowed"] is null ? null : n["vertical"]?["passes"]?.GetValue<bool>();
        if (lateral == false || vertical == false) return Verdict.Fail;
        return lateral == true || vertical == true ? Verdict.Pass : Verdict.Reported;
    }

    // ---- Helpers ----

    private static int Count(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    private static double R(double v, int digits = 6) => Math.Round(v, digits);
    private static JsonArray List(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());
    private static string[] None => [];
    private static double[] NoNumbers => [];

    private static void Ok(object result, string what)
    {
        if (Convert.ToInt32(result) != 0) throw new EtabsException($"SAP2000 refused to {what}.");
    }

    private static (double X, double Y, double Z) Xyz(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3) throw new ToolInputException("A point is [x, y, z].");
        return (p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble());
    }

    // ---- Model ----

    private static JsonNode Context(ICsiLink e)
    {
        var info = e.Info();
        var frames = e.Frames();
        var areas = e.Areas();
        var points = e.Points();
        var result = new JsonObject
        {
            ["program"] = "SAP2000", ["file"] = info.File, ["version"] = info.Version,
            ["units"] = $"{info.Force}, {info.Length}, {info.Temperature}", ["analysis_run"] = info.Locked,
            ["joints"] = points.Count,
            ["frames"] = new JsonObject
            {
                ["vertical"] = frames.Count(f => f.Kind == "column"), ["level"] = frames.Count(f => f.Kind == "beam"), ["sloping"] = frames.Count(f => f.Kind == "brace"),
            },
            ["areas"] = new JsonObject
            {
                ["level"] = areas.Count(a => a.Kind == "floor"), ["upright"] = areas.Count(a => a.Kind == "wall"), ["other"] = areas.Count(a => a.Kind == "shell"),
            },
        };
        if (points.Count > 0)
            result["bounds"] = new JsonObject
            {
                ["x"] = new JsonArray(R(points.Min(p => p.X)), R(points.Max(p => p.X))),
                ["y"] = new JsonArray(R(points.Min(p => p.Y)), R(points.Max(p => p.Y))),
                ["z"] = new JsonArray(R(points.Min(p => p.Z)), R(points.Max(p => p.Z))),
                ["z_levels"] = new JsonArray(points.Select(p => R(p.Z, 4)).Distinct().OrderBy(z => z).Take(60).Select(z => (JsonNode)z).ToArray()),
            };
        result["frame_sections"] = List(e.FrameSections());
        result["area_sections"] = List(e.AreaSections());
        result["materials"] = List(e.Materials());
        result["load_patterns"] = new JsonArray(e.LoadPatterns().Select(p => (JsonNode)$"{p.Name} ({p.Type}, self weight {p.SelfWeight})").ToArray());
        result["load_cases"] = List(e.LoadCases());
        result["combinations"] = List(e.Combos());
        try
        {
            result["groups"] = List((IEnumerable<string>)e.Raw("list the groups", m =>
            {
                int n = 0; string[] names = None;
                Ok(m.GroupDef.GetNameList(ref n, ref names), "list the groups");
                return names.Take(n).ToArray();
            })!);
        }
        catch (EtabsException) { }
        var selected = e.Selected();
        result["selected"] = new JsonObject
        {
            ["count"] = selected.Count,
            ["by_kind"] = new JsonObject(selected.GroupBy(s => s.Kind).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
        };
        return result;
    }

    private static JsonNode QueryJoints(JsonElement a, ICsiLink e)
    {
        var names = a.Texts("names").ToHashSet(StringComparer.Ordinal);
        var selected = a.Flag("selected_only") ? e.Selected().Where(s => s.Kind == "point").Select(s => s.Name).ToHashSet(StringComparer.Ordinal) : null;
        var tolerance = a.Number("tolerance", 0.001);
        double? z = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("z", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetDouble() : null;
        var limit = a.Whole("limit", 100);
        var supportedOnly = a.Flag("supported_only");
        var rows = new JsonArray();
        var total = 0;
        foreach (var p in e.Points())
        {
            if (names.Count > 0 && !names.Contains(p.Name)) continue;
            if (selected is not null && !selected.Contains(p.Name)) continue;
            if (z is { } level && Math.Abs(p.Z - level) > tolerance) continue;
            bool[]? r = null;
            if (supportedOnly || rows.Count < limit) r = e.Restraint(p.Name);
            if (supportedOnly && !r!.Any(x => x)) continue;
            total++;
            if (rows.Count >= limit) continue;
            rows.Add(new JsonObject { ["name"] = p.Name, ["x"] = R(p.X), ["y"] = R(p.Y), ["z"] = R(p.Z), ["support"] = Support(r!) });
        }
        return new JsonObject { ["count"] = total, ["joints"] = rows };
    }

    private static string Support(bool[] r)
    {
        if (r.All(x => x)) return "fixed";
        if (r.Take(3).All(x => x) && !r.Skip(3).Any(x => x)) return "pinned";
        if (!r.Any(x => x)) return "free";
        string[] dofs = ["UX", "UY", "UZ", "RX", "RY", "RZ"];
        return string.Join(" ", dofs.Where((_, i) => r[i]));
    }

    private static JsonNode AddJoints(JsonElement a, ICsiLink e)
    {
        var added = new JsonArray();
        foreach (var p in a.Items("points"))
        {
            var (x, y, z) = Xyz(p);
            added.Add((string)e.Raw("add a joint", m =>
            {
                string name = "";
                Ok(m.PointObj.AddCartesian(x, y, z, ref name, "", "Global", false, 0), $"add a joint at ({x}, {y}, {z})");
                return name;
            })!);
        }
        e.Refresh();
        return new JsonObject { ["added"] = added.Count, ["joints"] = added };
    }

    private static JsonNode AddFrames(JsonElement a, ICsiLink e)
    {
        var section = a.Need("section");
        var names = new JsonArray();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            foreach (var l in lines.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() < 6) throw new ToolInputException("A line is [x1, y1, z1, x2, y2, z2].");
                names.Add(e.AddFrame(l[0].GetDouble(), l[1].GetDouble(), l[2].GetDouble(), l[3].GetDouble(), l[4].GetDouble(), l[5].GetDouble(), section));
            }
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("joints", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
            foreach (var pair in pairs.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) throw new ToolInputException("Joints go in pairs: [jointI, jointJ].");
                string i = pair[0].GetString() ?? "", j = pair[1].GetString() ?? "";
                names.Add((string)e.Raw("add a frame", m =>
                {
                    string name = "";
                    Ok(m.FrameObj.AddByPoint(i, j, ref name, section, ""), $"add a frame from joint {i} to joint {j} with section {section}");
                    return name;
                })!);
            }
        if (names.Count == 0) throw new ToolInputException("Give 'lines' or 'joints'.");
        e.Refresh();
        return new JsonObject { ["added"] = names.Count, ["frames"] = names };
    }

    private static JsonNode AddAreas(JsonElement a, ICsiLink e)
    {
        var section = a.Need("section");
        var names = new JsonArray();
        foreach (var outline in a.Items("outlines"))
        {
            var corners = outline.EnumerateArray().Select(Xyz).ToList();
            if (corners.Count < 3) throw new ToolInputException("An area needs three corners or more.");
            names.Add(e.AddArea(corners, section));
        }
        e.Refresh();
        return new JsonObject { ["added"] = names.Count, ["areas"] = names };
    }

    private static JsonNode AddMaterial(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        var type = a.Need("type").ToLowerInvariant();
        var modulus = a.Number("elastic_modulus");
        var steelLike = type is "steel" or "rebar";
        var poisson = a.Number("poisson", steelLike ? 0.3 : 0.2);
        var thermal = a.Number("thermal", steelLike ? 1.17e-5 : 1e-5);
        var weight = a.Number("unit_weight", 0);
        var kind = type switch { "steel" => 1, "concrete" => 2, "rebar" => 6, "other" => 3, _ => throw new ToolInputException("type is concrete, steel, rebar or other.") };
        var notes = new JsonArray();
        e.Raw("add a material", m =>
        {
            Ok(m.PropMaterial.SetMaterial(name, kind, -1, "", ""), $"add the material {name}");
            if (type == "rebar") Ok(m.PropMaterial.SetMPUniaxial(name, modulus, thermal, 0), $"set the stiffness of {name}");
            else Ok(m.PropMaterial.SetMPIsotropic(name, modulus, poisson, thermal, 0), $"set the stiffness of {name}");
            if (weight > 0) Ok(m.PropMaterial.SetWeightAndMass(name, 1, weight, 0), $"set the unit weight of {name}");
            try
            {
                if (type == "concrete" && a.Number("fc", 0) is var fc and > 0)
                    Ok(m.PropMaterial.SetOConcrete_1(name, fc, false, 0, 1, 2, 0.0022, 0.005, -0.1, 0, 0, 0), $"set the strength of {name}");
                else if (type == "steel" && a.Number("fy", 0) is var fy and > 0)
                {
                    var fu = a.Number("fu", fy * 1.25);
                    Ok(m.PropMaterial.SetOSteel_1(name, fy, fu, fy * 1.1, fu * 1.1, 1, 1, 0.02, 0.14, 0.2, -0.1, 0), $"set the strength of {name}");
                }
                else if (type == "rebar" && a.Number("fy", 0) is var ry and > 0)
                {
                    var ru = a.Number("fu", ry * 1.25);
                    Ok(m.PropMaterial.SetORebar_1(name, ry, ru, ry * 1.1, ru * 1.1, 1, 1, 0.01, 0.09, -0.1, false, 0), $"set the strength of {name}");
                }
            }
            catch (Exception ex) when (ex is EtabsException or MissingMethodException or MissingMemberException)
            {
                notes.Add($"The strength could not be set through the API ({ex.Message}). Set it in SAP2000: Define > Materials > {name} > Modify.");
            }
            return null;
        });
        var result = new JsonObject { ["added"] = name, ["type"] = type };
        if (notes.Count > 0) result["notes"] = notes;
        return result;
    }

    private static JsonNode AddFrameSection(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        var material = a.Need("material");
        var shape = a.Need("shape").ToLowerInvariant();
        e.Raw("add a frame section", m =>
        {
            object result = shape switch
            {
                "rectangle" => m.PropFrame.SetRectangle(name, material, a.Number("depth"), a.Number("width"), -1, "", ""),
                "circle" => m.PropFrame.SetCircle(name, material, a.Number("diameter"), -1, "", ""),
                "i_section" => m.PropFrame.SetISection(name, material, a.Number("depth"), a.Number("width"), a.Number("flange_thickness"), a.Number("web_thickness"),
                    a.Number("width"), a.Number("flange_thickness"), -1, "", ""),
                "tube" => m.PropFrame.SetTube(name, material, a.Number("depth"), a.Number("width"), a.Number("flange_thickness"), a.Number("web_thickness"), -1, "", ""),
                "pipe" => m.PropFrame.SetPipe(name, material, a.Number("diameter"), a.Number("wall_thickness"), -1, "", ""),
                _ => throw new ToolInputException("shape is rectangle, circle, i_section, tube or pipe."),
            };
            Ok(result, $"add the section {name} (is {material} a material in the model?)");
            return null;
        });
        return new JsonObject { ["added"] = name, ["shape"] = shape, ["material"] = material };
    }

    private static JsonNode AddAreaSection(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        var material = a.Need("material");
        var thickness = a.Number("thickness");
        var type = a.Text("type", "shell_thin");
        var kind = type switch
        {
            "shell_thin" => 1, "shell_thick" => 2, "plate_thin" => 3, "plate_thick" => 4, "membrane" => 5,
            _ => throw new ToolInputException("type is shell_thin, shell_thick, plate_thin, plate_thick or membrane."),
        };
        e.Raw("add an area section", m =>
        {
            Ok(m.PropArea.SetShell_1(name, kind, true, material, 0.0, thickness, thickness, -1, "", ""), $"add the area section {name} (is {material} a material in the model?)");
            return null;
        });
        return new JsonObject { ["added"] = name, ["type"] = type, ["thickness"] = thickness };
    }

    private static bool[] Dofs(JsonElement a)
    {
        var dofs = a.Texts("dofs");
        if (dofs.Count > 0)
        {
            string[] order = ["UX", "UY", "UZ", "RX", "RY", "RZ"];
            return order.Select(d => dofs.Contains(d, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        return a.Need("type").ToLowerInvariant() switch
        {
            "fixed" => [true, true, true, true, true, true],
            "pinned" => [true, true, true, false, false, false],
            "roller" => [false, false, true, false, false, false],
            "free" => [false, false, false, false, false, false],
            _ => throw new ToolInputException("type is fixed, pinned, roller or free, or give dofs."),
        };
    }

    private static JsonNode SetRestraints(JsonElement a, ICsiLink e)
    {
        var value = Dofs(a);
        var joints = a.Texts("joints");
        if (joints.Count == 0)
        {
            var points = e.Points();
            if (points.Count == 0) throw new ToolInputException("The model has no joints yet.");
            double? z = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("z", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetDouble() : null;
            if (z is null && !a.Flag("at_lowest")) throw new ToolInputException("Give joints, z, or at_lowest.");
            var level = z ?? points.Min(p => p.Z);
            joints = points.Where(p => Math.Abs(p.Z - level) <= 1e-3).Select(p => p.Name).ToList();
        }
        foreach (var j in joints) e.SetRestraint(j, value);
        e.Refresh();
        return new JsonObject { ["set"] = joints.Count, ["support"] = Support(value), ["joints"] = List(joints.Take(200)) };
    }

    private static JsonNode SetJointLoads(JsonElement a, ICsiLink e)
    {
        var pattern = a.Need("pattern");
        var joints = a.Texts("joints");
        double[] values = [a.Number("fx", 0), a.Number("fy", 0), a.Number("fz", 0), a.Number("mx", 0), a.Number("my", 0), a.Number("mz", 0)];
        var replace = !a.Flag("add");
        e.Raw("load joints", m =>
        {
            foreach (var j in joints)
            {
                double[] v = values.ToArray();
                Ok(m.PointObj.SetLoadForce(j, pattern, ref v, replace, "Global", 0), $"load joint {j} in {pattern}");
            }
            return null;
        });
        e.Refresh();
        return new JsonObject { ["loaded"] = joints.Count, ["pattern"] = pattern };
    }

    private static JsonNode SetMemberLoads(JsonElement a, ICsiLink e)
    {
        var pattern = a.Need("pattern");
        var value = a.Number("value");
        var dir = a.Need("direction") switch
        {
            "gravity" => 10, "x" => 4, "y" => 5, "z" => 6, "local_1" => 1, "local_2" => 2, "local_3" => 3,
            _ => throw new ToolInputException("direction is gravity, x, y, z, local_1, local_2 or local_3."),
        };
        var replace = !a.Flag("add");
        var frames = a.Texts("frames");
        var areas = a.Texts("areas");
        e.Raw("load members", m =>
        {
            foreach (var f in frames)
                Ok(m.FrameObj.SetLoadDistributed(f, pattern, 1, dir, 0.0, 1.0, value, value, dir is 1 or 2 or 3 ? "Local" : "Global", true, replace, 0), $"load frame {f}");
            foreach (var ar in areas)
                Ok(m.AreaObj.SetLoadUniform(ar, pattern, value, dir, replace, dir is 1 or 2 or 3 ? "Local" : "Global", 0), $"load area {ar}");
            return null;
        });
        e.Refresh();
        return new JsonObject { ["frames"] = frames.Count, ["areas"] = areas.Count, ["pattern"] = pattern, ["value"] = value };
    }

    private static JsonNode SetReleases(JsonElement a, ICsiLink e)
    {
        var frames = a.Texts("frames");
        var pinned = a.Need("type") == "pinned";
        var ends = a.Text("ends", "both");
        e.Raw("release frame ends", m =>
        {
            foreach (var f in frames)
            {
                // P V2 V3 T M2 M3 at each end.
                bool[] i = [false, false, false, false, pinned && ends != "j", pinned && ends != "j"];
                bool[] j = [false, false, false, false, pinned && ends != "i", pinned && ends != "i"];
                double[] si = new double[6], sj = new double[6];
                Ok(m.FrameObj.SetReleases(f, ref i, ref j, ref si, ref sj, 0), $"release the ends of frame {f} (both ends of a member cannot release torsion or the same force)");
            }
            return null;
        });
        e.Refresh();
        return new JsonObject { ["frames"] = frames.Count, ["type"] = pinned ? "pinned" : "fixed", ["ends"] = ends };
    }

    private static JsonNode DeleteJoints(JsonElement a, ICsiLink e)
    {
        var deleted = new JsonArray(); var kept = new JsonArray();
        foreach (var j in a.Texts("joints"))
        {
            try { e.Delete("point", j); deleted.Add(j); }
            catch (EtabsException ex) { kept.Add($"{j}: {ex.Message}"); }
        }
        e.Refresh();
        return new JsonObject { ["deleted"] = deleted, ["not_deleted"] = kept };
    }

    // ---- Results ----

    /// <summary>Chooses the cases and combinations for output: the ones asked for, or every case and combination.</summary>
    private static void Output(dynamic m, ICsiLink e, JsonElement a, bool everything = true)
    {
        var cases = a.Texts("cases");
        var combos = a.Texts("combinations");
        Ok(m.Results.Setup.DeselectAllCasesAndCombosForOutput(), "clear the output selection");
        if (cases.Count == 0 && combos.Count == 0 && everything) { cases = e.LoadCases().ToList(); combos = e.Combos().ToList(); }
        foreach (var c in cases) Ok(m.Results.Setup.SetCaseSelectedForOutput(c, true), $"choose the load case {c} for output (is it in the model?)");
        foreach (var c in combos) Ok(m.Results.Setup.SetComboSelectedForOutput(c, true), $"choose the combination {c} for output (is it in the model?)");
    }

    private static string Step(string type, double num) => type is null or "" ? "" : num != 0 ? $"{type} {num}" : type;

    private sealed record DisplRow(string Joint, string Case, string Step, double U1, double U2, double U3, double R1, double R2, double R3);

    private static List<DisplRow> Displacements(ICsiLink e, JsonElement a)
    {
        var joints = a.Texts("joints");
        return (List<DisplRow>)e.Raw("read joint displacements", m =>
        {
            Output(m, e, a);
            var rows = new List<DisplRow>();
            // One call for the whole model (the group ALL), or one per joint asked for.
            var targets = joints.Count > 0 ? joints.Select(j => (j, 0)).ToList() : [("ALL", 2)];
            foreach (var (name, item) in targets)
            {
                int n = 0; string[] obj = None, elm = None, cases = None, steps = None; double[] num = NoNumbers;
                double[] u1 = NoNumbers, u2 = NoNumbers, u3 = NoNumbers, r1 = NoNumbers, r2 = NoNumbers, r3 = NoNumbers;
                Ok(m.Results.JointDispl(name, item, ref n, ref obj, ref elm, ref cases, ref steps, ref num, ref u1, ref u2, ref u3, ref r1, ref r2, ref r3),
                    "read joint displacements (has the analysis been run?)");
                for (var i = 0; i < n; i++) rows.Add(new DisplRow(obj[i], cases[i], Step(steps[i], num[i]), u1[i], u2[i], u3[i], r1[i], r2[i], r3[i]));
            }
            return rows;
        })!;
    }

    private static JsonNode JointDisplacements(JsonElement a, ICsiLink e)
    {
        var rows = Displacements(e, a);
        if (rows.Count == 0) throw new ToolInputException("SAP2000 has no displacements for these cases: run the analysis first (run_analysis), or check the case names.");
        var limit = a.Whole("limit", 50);
        JsonObject Max(string dir, Func<DisplRow, double> pick)
        {
            var top = rows.MaxBy(r => Math.Abs(pick(r)))!;
            return new JsonObject { ["value"] = R(pick(top)), ["joint"] = top.Joint, ["case"] = top.Case, ["step"] = top.Step };
        }
        return new JsonObject
        {
            ["units"] = Units(e), ["rows"] = rows.Count,
            ["largest"] = new JsonObject
            {
                ["U1"] = Max("U1", r => r.U1), ["U2"] = Max("U2", r => r.U2), ["U3"] = Max("U3", r => r.U3),
                ["R1"] = Max("R1", r => r.R1), ["R2"] = Max("R2", r => r.R2), ["R3"] = Max("R3", r => r.R3),
            },
            ["list"] = new JsonArray(rows.OrderByDescending(r => Math.Sqrt(r.U1 * r.U1 + r.U2 * r.U2 + r.U3 * r.U3)).Take(limit).Select(r => (JsonNode)new JsonObject
            {
                ["joint"] = r.Joint, ["case"] = r.Case, ["step"] = r.Step, ["U1"] = R(r.U1), ["U2"] = R(r.U2), ["U3"] = R(r.U3), ["R1"] = R(r.R1), ["R2"] = R(r.R2), ["R3"] = R(r.R3),
            }).ToArray()),
        };
    }

    private static string Units(ICsiLink e)
    {
        var info = e.Info();
        return $"{info.Force}, {info.Length}";
    }

    private sealed record ForceRow(string Frame, double Station, string Case, string Step, double P, double V2, double V3, double T, double M2, double M3);

    private static JsonNode FrameForces(JsonElement a, ICsiLink e)
    {
        var frames = a.Texts("frames");
        var rows = (List<ForceRow>)e.Raw("read frame forces", m =>
        {
            Output(m, e, a);
            var list = new List<ForceRow>();
            var targets = frames.Count > 0 ? frames.Select(f => (f, 0)).ToList() : [("ALL", 2)];
            foreach (var (name, item) in targets)
            {
                int n = 0; string[] obj = None, elm = None, cases = None, steps = None; double[] objSta = NoNumbers, elmSta = NoNumbers, num = NoNumbers;
                double[] p = NoNumbers, v2 = NoNumbers, v3 = NoNumbers, t = NoNumbers, m2 = NoNumbers, m3 = NoNumbers;
                Ok(m.Results.FrameForce(name, item, ref n, ref obj, ref objSta, ref elm, ref elmSta, ref cases, ref steps, ref num, ref p, ref v2, ref v3, ref t, ref m2, ref m3),
                    "read frame forces (has the analysis been run?)");
                for (var i = 0; i < n; i++) list.Add(new ForceRow(obj[i], objSta[i], cases[i], Step(steps[i], num[i]), p[i], v2[i], v3[i], t[i], m2[i], m3[i]));
            }
            return list;
        })!;
        if (rows.Count == 0) throw new ToolInputException("SAP2000 has no frame forces for these cases: run the analysis first (run_analysis), or check the names.");
        JsonObject Max(Func<ForceRow, double> pick)
        {
            var top = rows.MaxBy(r => Math.Abs(pick(r)))!;
            return new JsonObject { ["value"] = R(pick(top), 4), ["frame"] = top.Frame, ["station"] = R(top.Station, 4), ["case"] = top.Case, ["step"] = top.Step };
        }
        var limit = a.Whole("limit", 50);
        var perFrame = rows.GroupBy(r => r.Frame).Select(g => new
        {
            Frame = g.Key,
            P = g.MaxBy(r => Math.Abs(r.P))!, V = g.MaxBy(r => Math.Max(Math.Abs(r.V2), Math.Abs(r.V3)))!,
            M = g.MaxBy(r => Math.Max(Math.Abs(r.M2), Math.Abs(r.M3)))!, T = g.MaxBy(r => Math.Abs(r.T))!,
        }).OrderByDescending(x => Math.Max(Math.Abs(x.M.M2), Math.Abs(x.M.M3))).Take(limit);
        return new JsonObject
        {
            ["units"] = Units(e), ["rows"] = rows.Count,
            ["largest"] = new JsonObject
            {
                ["P"] = Max(r => r.P), ["V2"] = Max(r => r.V2), ["V3"] = Max(r => r.V3), ["T"] = Max(r => r.T), ["M2"] = Max(r => r.M2), ["M3"] = Max(r => r.M3),
            },
            ["by_frame"] = new JsonArray(perFrame.Select(x => (JsonNode)new JsonObject
            {
                ["frame"] = x.Frame,
                ["P"] = R(x.P.P, 4), ["P_case"] = x.P.Case,
                ["V2"] = R(x.V.V2, 4), ["V3"] = R(x.V.V3, 4), ["V_case"] = x.V.Case,
                ["M2"] = R(x.M.M2, 4), ["M3"] = R(x.M.M3, 4), ["M_case"] = x.M.Case, ["M_station"] = R(x.M.Station, 4),
                ["T"] = R(x.T.T, 4),
            }).ToArray()),
        };
    }

    private static JsonNode BaseReactions(JsonElement a, ICsiLink e)
    {
        var result = (JsonObject)e.Raw("read base reactions", m =>
        {
            Output(m, e, a);
            int n = 0; string[] cases = None, steps = None; double[] num = NoNumbers;
            double[] fx = NoNumbers, fy = NoNumbers, fz = NoNumbers, mx = NoNumbers, my = NoNumbers, mz = NoNumbers;
            double gx = 0, gy = 0, gz = 0;
            Ok(m.Results.BaseReact(ref n, ref cases, ref steps, ref num, ref fx, ref fy, ref fz, ref mx, ref my, ref mz, ref gx, ref gy, ref gz),
                "read base reactions (has the analysis been run?)");
            var total = new JsonArray();
            for (var i = 0; i < n; i++)
                total.Add(new JsonObject
                {
                    ["case"] = cases[i], ["step"] = Step(steps[i], num[i]),
                    ["FX"] = R(fx[i], 4), ["FY"] = R(fy[i], 4), ["FZ"] = R(fz[i], 4), ["MX"] = R(mx[i], 4), ["MY"] = R(my[i], 4), ["MZ"] = R(mz[i], 4),
                });
            var o = new JsonObject { ["units"] = Units(e), ["about"] = new JsonArray(R(gx), R(gy), R(gz)), ["base_reactions"] = total };
            if (a.Flag("per_joint"))
            {
                int k = 0; string[] obj = None, elm = None, jc = None, js = None; double[] jn = NoNumbers;
                double[] f1 = NoNumbers, f2 = NoNumbers, f3 = NoNumbers, m1 = NoNumbers, m2 = NoNumbers, m3 = NoNumbers;
                Ok(m.Results.JointReact("ALL", 2, ref k, ref obj, ref elm, ref jc, ref js, ref jn, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3), "read joint reactions");
                var joints = new JsonArray();
                for (var i = 0; i < k && joints.Count < 500; i++)
                {
                    if (Math.Abs(f1[i]) + Math.Abs(f2[i]) + Math.Abs(f3[i]) + Math.Abs(m1[i]) + Math.Abs(m2[i]) + Math.Abs(m3[i]) < 1e-9) continue;
                    joints.Add(new JsonObject
                    {
                        ["joint"] = obj[i], ["case"] = jc[i], ["F1"] = R(f1[i], 4), ["F2"] = R(f2[i], 4), ["F3"] = R(f3[i], 4),
                        ["M1"] = R(m1[i], 4), ["M2"] = R(m2[i], 4), ["M3"] = R(m3[i], 4),
                    });
                }
                o["joint_reactions"] = joints;
            }
            return o;
        })!;
        if (result["base_reactions"] is JsonArray { Count: 0 })
            throw new ToolInputException("SAP2000 has no reactions for these cases: run the analysis first (run_analysis), or check the case names.");
        return result;
    }

    private static JsonNode ModalResults(JsonElement a, ICsiLink e)
    {
        var target = a.Number("target", 0.9);
        var only = a.Text("case");
        return (JsonNode)e.Raw("read the modal results", m =>
        {
            Ok(m.Results.Setup.DeselectAllCasesAndCombosForOutput(), "clear the output selection");
            var modal = only.Length > 0 ? [only] : e.LoadCases().ToList();
            foreach (var c in modal) m.Results.Setup.SetCaseSelectedForOutput(c, true);
            int n = 0; string[] cases = None, steps = None; double[] num = NoNumbers, period = NoNumbers;
            double[] ux = NoNumbers, uy = NoNumbers, uz = NoNumbers, sux = NoNumbers, suy = NoNumbers, suz = NoNumbers;
            double[] rx = NoNumbers, ry = NoNumbers, rz = NoNumbers, srx = NoNumbers, sry = NoNumbers, srz = NoNumbers;
            Ok(m.Results.ModalParticipatingMassRatios(ref n, ref cases, ref steps, ref num, ref period, ref ux, ref uy, ref uz, ref sux, ref suy, ref suz,
                ref rx, ref ry, ref rz, ref srx, ref sry, ref srz), "read the modal mass ratios (has the analysis been run with a modal case?)");
            if (n == 0) throw new ToolInputException("SAP2000 has no modal results: the model needs a modal load case, and the analysis run.");
            var modes = new JsonArray();
            for (var i = 0; i < n; i++)
                modes.Add(new JsonObject
                {
                    ["case"] = cases[i], ["mode"] = (int)num[i], ["period"] = R(period[i], 4), ["frequency"] = period[i] > 0 ? R(1 / period[i], 4) : 0,
                    ["UX"] = R(ux[i], 4), ["UY"] = R(uy[i], 4), ["UZ"] = R(uz[i], 4), ["RZ"] = R(rz[i], 4),
                    ["sum_UX"] = R(sux[i], 4), ["sum_UY"] = R(suy[i], 4), ["sum_UZ"] = R(suz[i], 4),
                });
            double lastX = sux[n - 1], lastY = suy[n - 1];
            return new JsonObject
            {
                ["modes"] = modes,
                ["first_period"] = R(period[0], 4),
                ["mass_ux"] = R(lastX, 4), ["mass_uy"] = R(lastY, 4), ["target"] = target,
                ["passes"] = lastX >= target && lastY >= target,
                ["note"] = lastX >= target && lastY >= target
                    ? $"The modes reach {target:P0} of the mass in UX and UY."
                    : $"The modes reach {lastX:P0} in UX and {lastY:P0} in UY, under {target:P0}: add modes to the modal case (Define > Load Cases > MODAL).",
            };
        })!;
    }

    private static JsonNode CheckDisplacement(JsonElement a, ICsiLink e)
    {
        var rows = Displacements(e, a);
        if (rows.Count == 0) throw new ToolInputException("SAP2000 has no displacements for these cases: run the analysis first (run_analysis), or check the case names.");
        var points = e.Points();
        var height = points.Count > 0 ? points.Max(p => p.Z) - points.Min(p => p.Z) : 0;
        var ratio = a.Number("lateral_limit", 500);
        var lateralAllowed = height > 0 ? height / ratio : double.PositiveInfinity;
        var lat = rows.MaxBy(r => Math.Max(Math.Abs(r.U1), Math.Abs(r.U2)))!;
        var latValue = Math.Max(Math.Abs(lat.U1), Math.Abs(lat.U2));
        var vert = rows.MaxBy(r => Math.Abs(r.U3))!;
        var result = new JsonObject
        {
            ["units"] = Units(e),
            ["height"] = R(height, 4),
            ["lateral"] = new JsonObject
            {
                ["largest"] = R(latValue), ["direction"] = Math.Abs(lat.U1) >= Math.Abs(lat.U2) ? "UX" : "UY", ["joint"] = lat.Joint, ["case"] = lat.Case,
                ["allowed"] = double.IsInfinity(lateralAllowed) ? null : R(lateralAllowed), ["limit"] = $"H/{ratio}",
                ["passes"] = latValue <= lateralAllowed, ["ratio"] = double.IsInfinity(lateralAllowed) ? null : R(latValue / lateralAllowed, 3),
            },
            ["vertical"] = new JsonObject { ["largest"] = R(vert.U3), ["joint"] = vert.Joint, ["case"] = vert.Case },
        };
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("vertical_limit", out var vl) && vl.ValueKind == JsonValueKind.Number)
        {
            ((JsonObject)result["vertical"]!)["allowed"] = vl.GetDouble();
            ((JsonObject)result["vertical"]!)["passes"] = Math.Abs(vert.U3) <= vl.GetDouble();
            var over = rows.Where(r => Math.Abs(r.U3) > vl.GetDouble()).Select(r => r.Joint).Distinct().Take(100).ToList();
            ((JsonObject)result["vertical"]!)["joints_over"] = List(over);
        }
        result["note"] = "Displacements are SAP2000's, from its analysis; vertical displacement here is the joint's own, not relative to the member ends.";
        return result;
    }

    private static JsonNode RunDesign(JsonElement a, ICsiLink e)
    {
        var done = new JsonArray();
        var notes = new JsonArray();
        e.Raw("run the design", m =>
        {
            if (a.Flag("steel", true))
                try { Ok(m.DesignSteel.StartDesign(), "run steel design"); done.Add("steel"); }
                catch (EtabsException ex) { notes.Add(ex.Message + " (no steel frames, or no steel design combinations?)"); }
            if (a.Flag("concrete", true))
                try { Ok(m.DesignConcrete.StartDesign(), "run concrete design"); done.Add("concrete"); }
                catch (EtabsException ex) { notes.Add(ex.Message + " (no concrete frames, or no concrete design combinations?)"); }
            return null;
        });
        return new JsonObject { ["designed"] = done, ["notes"] = notes, ["next"] = "Read the results with design_ratios." };
    }

    private static JsonNode DesignRatios(JsonElement a, ICsiLink e)
    {
        var frames = a.Texts("frames");
        var limit = a.Whole("limit", 50);
        var result = new JsonObject();
        e.Raw("read the design results", m =>
        {
            var targets = frames.Count > 0 ? frames.Select(f => (f, 0)).ToList() : [("ALL", 2)];
            var steel = new List<(string Frame, double Ratio, string Type, string Combo, string Error)>();
            var columns = new List<(string Frame, double Ratio, double Area, string Combo, string Error)>();
            var beams = new List<(string Frame, double Top, double Bottom, double Shear, string Error)>();
            foreach (var (name, item) in targets)
            {
                try
                {
                    int n = 0; string[] fn = None, combo = None, err = None, warn = None; double[] ratio = NoNumbers, loc = NoNumbers; int[] rtype = [];
                    if (Convert.ToInt32(m.DesignSteel.GetSummaryResults(name, ref n, ref fn, ref ratio, ref rtype, ref loc, ref combo, ref err, ref warn, item)) == 0)
                        for (var i = 0; i < n; i++)
                            steel.Add((fn[i], ratio[i], rtype.Length > i ? rtype[i] switch { 1 => "PMM", 2 => "major shear", 3 => "minor shear", 4 => "major beam-column capacity", 5 => "minor beam-column capacity", _ => "other" } : "", combo[i], err[i]));
                }
                catch (Exception ex) when (ex is EtabsException or MissingMethodException or System.Runtime.InteropServices.COMException) { }
                try
                {
                    int n = 0; string[] fn = None, pc = None, vc = None, wc = None, err = None, warn = None;
                    double[] loc = NoNumbers, area = NoNumbers, ratio = NoNumbers, av = NoNumbers, aw = NoNumbers; int option = 0;
                    if (Convert.ToInt32(m.DesignConcrete.GetSummaryResultsColumn(name, ref n, ref fn, ref option, ref loc, ref pc, ref area, ref ratio, ref vc, ref av, ref wc, ref aw, ref err, ref warn, item)) == 0)
                        for (var i = 0; i < n; i++) columns.Add((fn[i], ratio[i], area[i], pc[i], err[i]));
                }
                catch (Exception ex) when (ex is EtabsException or MissingMethodException or System.Runtime.InteropServices.COMException) { }
                try
                {
                    int n = 0; string[] fn = None, tc = None, bc = None, vc = None, tlc = None, ttc = None, err = None, warn = None;
                    double[] loc = NoNumbers, top = NoNumbers, bottom = NoNumbers, shear = NoNumbers, tl = NoNumbers, tt = NoNumbers;
                    if (Convert.ToInt32(m.DesignConcrete.GetSummaryResultsBeam(name, ref n, ref fn, ref loc, ref tc, ref top, ref bc, ref bottom, ref vc, ref shear, ref tlc, ref tl, ref ttc, ref tt, ref err, ref warn, item)) == 0)
                        for (var i = 0; i < n; i++) beams.Add((fn[i], top[i], bottom[i], shear[i], err[i]));
                }
                catch (Exception ex) when (ex is EtabsException or MissingMethodException or System.Runtime.InteropServices.COMException) { }
            }
            if (steel.Count > 0)
            {
                var worst = steel.GroupBy(s => s.Frame).Select(g => g.MaxBy(s => s.Ratio)).OrderByDescending(s => s.Ratio).ToList();
                result["steel"] = new JsonObject
                {
                    ["members"] = worst.Count, ["max_ratio"] = R(worst[0].Ratio, 3), ["at"] = worst[0].Frame, ["combination"] = worst[0].Combo,
                    ["over_1"] = List(worst.Where(s => s.Ratio > 1).Select(s => s.Frame).Take(200)),
                    ["with_errors"] = List(worst.Where(s => !string.IsNullOrWhiteSpace(s.Error)).Select(s => $"{s.Frame}: {s.Error}").Take(50)),
                    ["list"] = new JsonArray(worst.Take(limit).Select(s => (JsonNode)new JsonObject { ["frame"] = s.Frame, ["ratio"] = R(s.Ratio, 3), ["governs"] = s.Type, ["combination"] = s.Combo }).ToArray()),
                };
            }
            if (columns.Count > 0)
            {
                var worst = columns.GroupBy(s => s.Frame).Select(g => g.MaxBy(s => Math.Max(s.Ratio, s.Area))).OrderByDescending(s => s.Ratio).ThenByDescending(s => s.Area).ToList();
                result["concrete_columns"] = new JsonObject
                {
                    ["members"] = worst.Count,
                    ["note"] = "A PMM ratio is given for columns checked against their reinforcement; designed columns give the reinforcing area needed instead.",
                    ["with_errors"] = List(worst.Where(s => !string.IsNullOrWhiteSpace(s.Error)).Select(s => $"{s.Frame}: {s.Error}").Take(50)),
                    ["list"] = new JsonArray(worst.Take(limit).Select(s => (JsonNode)new JsonObject { ["frame"] = s.Frame, ["pmm_ratio"] = R(s.Ratio, 3), ["rebar_area"] = R(s.Area, 6), ["combination"] = s.Combo }).ToArray()),
                };
            }
            if (beams.Count > 0)
            {
                var worst = beams.GroupBy(s => s.Frame).Select(g => (Frame: g.Key, Top: g.Max(x => x.Top), Bottom: g.Max(x => x.Bottom), Shear: g.Max(x => x.Shear),
                    Error: g.Select(x => x.Error).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "")).OrderByDescending(s => Math.Max(s.Top, s.Bottom)).ToList();
                result["concrete_beams"] = new JsonObject
                {
                    ["members"] = worst.Count,
                    ["with_errors"] = List(worst.Where(s => s.Error.Length > 0).Select(s => $"{s.Frame}: {s.Error}").Take(50)),
                    ["list"] = new JsonArray(worst.Take(limit).Select(s => (JsonNode)new JsonObject { ["frame"] = s.Frame, ["top_area"] = R(s.Top, 6), ["bottom_area"] = R(s.Bottom, 6), ["shear_area_per_length"] = R(s.Shear, 6) }).ToArray()),
                };
            }
            return null;
        });
        if (result.Count == 0) throw new ToolInputException("SAP2000 has no design results: run the analysis, then run_design, and check that the frames have steel or concrete sections.");
        result["units"] = Units(e);
        return result;
    }
}
