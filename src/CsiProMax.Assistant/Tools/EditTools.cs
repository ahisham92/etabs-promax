using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's Draw and Edit items beyond columns, beams, floors and walls (Ahmed 2026-11-26: every button ETABS
/// has): braces and joints, and Edit > Replicate, Move and Divide Frames. Modeler only; a copy keeps all the original
/// has (section, assignments, loads, hinges), and each call is one undo step.
/// </summary>
public static class EditTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Modeler's own Draw or Edit item (no CSI program behind it). In a live program use its own Draw and Edit menus.");

    private static T Core<T>(Func<T> work)
    {
        try { return work(); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        catch (EtabsException ex) { throw new ToolInputException(ex.Message); }
    }

    private const string Objects = "\"objects\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Frames, slabs and walls as label@story.\"}";

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("add_braces",
            "Draw > Draw Braces: braces, each from (x1, y1) on the story below to (x2, y2) on the story, on each story given.",
            """{"type":"object","required":["braces","stories","section"],"properties":{"braces":{"type":"array","items":{"type":"array","items":{"type":"number"},"description":"[x1, y1, x2, y2]"}},"stories":{"type":"array","items":{"type":"string"}},"section":{"type":"string"}}}""",
            Ask.Changes, AddBraces, a => $"Draw {a.Items("braces").Count()} braces"),
        new("add_joints",
            "Draw > Draw Joints: joints at plan points on each story given, for supports, springs, loads and links where no member ends.",
            """{"type":"object","required":["points","stories"],"properties":{"points":{"type":"array","items":{"type":"array","items":{"type":"number"},"description":"[x, y]"}},"stories":{"type":"array","items":{"type":"string"}}}}""",
            Ask.Changes, AddJoints, a => $"Draw {a.Items("points").Count()} joints"),
        new("add_frames_by_points",
            "Draw Beam/Column/Brace in the 3-D view: a frame between two points in space, each [x1, y1, z1, x2, y2, z2], both ends at story levels. Its type follows its inclination, by Options > Tolerances (within 1 degree of vertical a column, within 20 degrees of horizontal a beam, else a brace, by default); each is one object however many stories it spans. With 'brace' (Quick Draw Braces) each is a brace whatever its inclination, one with both ends on one level too (bracing in a floor's plane).",
            """{"type":"object","required":["frames","section"],"properties":{"frames":{"type":"array","items":{"type":"array","items":{"type":"number"}},"description":"[x1, y1, z1, x2, y2, z2]"},"section":{"type":"string"},"brace":{"type":"boolean"}}}""",
            Ask.Changes, AddFramesByPoints, a => a.TryGetProperty("brace", out var b) && b.ValueKind == JsonValueKind.True ? $"Draw {a.Items("frames").Count()} braces" : $"Draw {a.Items("frames").Count()} frames"),
        new("add_areas_by_points",
            "Draw Floor/Wall in the 3-D view: an area on its corners in space, each [[x, y, z], ...] in order round its edge, every corner at a story level and all in one plane. Its type follows its inclination, by Options > Tolerances (within 20 degrees of level a floor, within 1 degree of upright a wall, else a ramp, by default); each is one object however many stories it spans.",
            """{"type":"object","required":["areas","section"],"properties":{"areas":{"type":"array","items":{"type":"array","items":{"type":"array","items":{"type":"number"}}},"description":"[[x, y, z], ...], three corners or more"},"section":{"type":"string","description":"The section, for a floor or ramp (and a wall when no wall_section is given)."},"wall_section":{"type":"string","description":"The section an area that comes out a wall takes."}}}""",
            Ask.Changes, AddAreasByPoints, a => $"Draw {a.Items("areas").Count()} areas"),
        new("replicate_objects",
            "Edit > Replicate: copies frames, slabs and walls with their sections, assignments, loads and hinges. mode 'linear': 'number' copies, each dx, dy on from the last; mode 'radial': 'number' copies turned about (cx, cy), each 'angle' degrees on; mode 'mirror': one copy mirrored about the plan line (x1, y1)-(x2, y2); mode 'story': onto each of 'stories', in the same place. delete_original takes the originals off after (Delete Original Objects).",
            "{\"type\":\"object\",\"required\":[\"objects\",\"mode\"],\"properties\":{" + Objects + ",\"mode\":{\"type\":\"string\",\"enum\":[\"linear\",\"radial\",\"mirror\",\"story\"]},\"dx\":{\"type\":\"number\"},\"dy\":{\"type\":\"number\"},\"dz\":{\"type\":\"number\",\"description\":\"linear: up by this each copy, to another story's level (Paste).\"},\"number\":{\"type\":\"integer\"},\"cx\":{\"type\":\"number\"},\"cy\":{\"type\":\"number\"},\"angle\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"y1\":{\"type\":\"number\"},\"x2\":{\"type\":\"number\"},\"y2\":{\"type\":\"number\"},\"stories\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"delete_original\":{\"type\":\"boolean\"},\"skip\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Replicate Assigns options left off the copies, as 'Frames:Hinges', 'Shells:Diaphragms', 'Frames:Distributed' (loads) ...\"}}}",
            Ask.Changes, Replicate, a => $"Replicate {a.Texts("objects").Count} objects"),
        new("move_objects",
            "Edit > Move Joints/Frames/Shells: moves frames, slabs and walls by dx, dy in plan and dz up (dz must reach another story's level), with their sections, assignments, loads and hinges.",
            "{\"type\":\"object\",\"required\":[\"objects\"],\"properties\":{" + Objects + ",\"dx\":{\"type\":\"number\"},\"dy\":{\"type\":\"number\"},\"dz\":{\"type\":\"number\"}}}",
            Ask.Changes, MoveObjects, a => $"Move {a.Texts("objects").Count} objects"),
        new("reverse_frames",
            "Edit > Edit Frames > Reverse Frame Connectivity: each frame named runs from its J end to its I end; end releases, end offsets and hinges change ends with it.",
            "{\"type\":\"object\",\"required\":[\"objects\"],\"properties\":{" + Objects + "}}",
            Ask.Changes, ReverseFrames, a => $"Reverse {a.Texts("objects").Count} frames"),
        new("join_frames",
            "Edit > Edit Frames > Join Frames: beams on one story that run on in a straight line, end to end, with the same loads and nothing else at the joint between, become one beam with the first one's section and assignments.",
            "{\"type\":\"object\",\"required\":[\"objects\"],\"properties\":{" + Objects + "}}",
            Ask.Changes, JoinFrames, a => $"Join {a.Texts("objects").Count} frames"),
        new("align_joints",
            "Edit > Align Joints/Frames/Edges: mode 'x' or 'y' puts each joint ([x, y, story]) on the ordinate 'value', the frames, slabs and walls ending there following; mode 'trim' or 'extend' takes the beams named ('objects') to where they cross the 'boundary' beam.",
            "{\"type\":\"object\",\"required\":[\"mode\"],\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"x\",\"y\",\"nearest\",\"trim\",\"extend\"]},\"joints\":{\"type\":\"array\",\"items\":{\"type\":\"array\"},\"description\":\"[x, y, story] each.\"},\"value\":{\"type\":\"number\"}," + Objects + ",\"boundary\":{\"type\":\"string\"}}}",
            Ask.Changes, AlignJoints, a => $"Align ({a.Text("mode")})"),
        new("replace_floors",
            "Edit > Edit Shells (Divide Shells, Merge Shells, Expand/Shrink, Split Shell Edges, Remove Joints from Shells, Chamfer Slab Corners, Slab Edge Type): the floors named ('objects', one story) become the floors of 'outlines' (each [[x, y], ...]), each keeping the first floor's section, assignments and loads.",
            "{\"type\":\"object\",\"required\":[\"objects\",\"outlines\"],\"properties\":{" + Objects + ",\"outlines\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}}}}",
            Ask.Changes, ReplaceFloors, a => $"Reshape {a.Texts("objects").Count} floors into {a.Items("outlines").Count()}"),
        new("relabel_objects",
            "Edit > Auto Relabel All: frames, floors and walls get the program's labels again (B, C, D, F, W by kind, from the lowest plan point up). An object something else names by its label (a group, an overwrite, a kept table row) keeps its label.",
            """{"type":"object","properties":{}}""",
            Ask.Changes, RelabelObjects, _ => "Auto Relabel All"),
        new("add_template",
            "Edit > Add to Model from Template: adds a 2D Frame (type portal, concentric or eccentric), 2D Truss (sloped, vertical or pratt), 2D Wall (with 'opening' {l,h,b,d} or without) laid along a line from x, y at 'angle' degrees about Z, or a 3D structure (steel_deck, staggered_truss, flat_slab, flat_slab_perimeter_beams, waffle_slab, two_way_or_ribbed_slab) over the model's grid lines, on the stories from 'bottom_story' up to 'top_story' ('story' for a truss). Sections are the model's own: 2D columns/beams/braces, chords/diagonals/verticals or section; 3D 'use' maps column, beam, slab, drop (steel deck: lat_col, lat_bm, grav_col, grav_bm, sec_bm, deck; staggered truss t1..t12) to sections, with the rest in 'quick' as new_model takes it. Lengths in model units.",
            """{"type":"object","required":["kind"],"properties":{"kind":{"type":"string","enum":["2d_frame","2d_truss","2d_wall","steel_deck","staggered_truss","flat_slab","flat_slab_perimeter_beams","waffle_slab","two_way_or_ribbed_slab"]},"type":{"type":"string"},"top_story":{"type":"string"},"bottom_story":{"type":"string"},"story":{"type":"string"},"bays":{"type":"integer"},"bay_width":{"type":"number"},"gap_width":{"type":"number"},"divisions":{"type":"integer"},"length":{"type":"number"},"height":{"type":"number"},"columns":{"type":"string"},"beams":{"type":"string"},"braces":{"type":"string"},"chords":{"type":"string"},"diagonals":{"type":"string"},"verticals":{"type":"string"},"section":{"type":"string"},"opening":{"type":"object"},"restraints":{"type":"string","enum":["none","pinned","fixed"]},"x":{"type":"number"},"y":{"type":"number"},"angle":{"type":"number"},"use":{"type":"object"},"quick":{"type":"object"}}}""",
            Ask.Changes, AddTemplate, a => $"Add a {a.Text("kind")} from a template"),
        new("merge_joints",
            "Edit > Merge Joints: the joints named that lie within 'tolerance' of each other in plan become one; frames, slabs, walls, joint assignments and loads follow it, and a frame left with both ends at one joint is taken off.",
            """{"type":"object","required":["joints","tolerance"],"properties":{"joints":{"type":"array","items":{"type":"string"},"description":"Joint (point) names."},"tolerance":{"type":"number"}}}""",
            Ask.Changes, MergeJoints, a => $"Merge {a.Texts("joints").Count} joints"),
        new("reshape_object",
            "Draw > Reshape Object: moves the end or corner of one frame, slab or wall that stands at 'from' [x, y] in plan to 'to' [x, y], on its own story, keeping its section, assignments, loads and hinges. A column moves whole.",
            "{\"type\":\"object\",\"required\":[\"object\",\"from\",\"to\"],\"properties\":{\"object\":{\"type\":\"string\",\"description\":\"label@story\"},\"from\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}},\"to\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}}",
            Ask.Changes, Reshape, a => $"Reshape {a.Text("object")}"),
        new("divide_frames",
            "Edit > Edit Frames > Divide Frames: beams into 'parts' equal beams (2 to 20), or, with 'points', broken at each plan point [x, y] that lies on a beam between its ends (Break at Intersections); with 'levels', each column, brace, wall or ramp named that spans more than one story broken at every story level between its ends (Divide Frames' Break at Intersections with Story Levels, Divide Shells' Walls and Ramps at Story Levels); each part keeps the section and assignments; uniform loads go on every part, end releases and offsets stay at the frame's ends.",
            "{\"type\":\"object\",\"required\":[\"objects\"],\"properties\":{" + Objects + ",\"parts\":{\"type\":\"integer\"},\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"levels\":{\"type\":\"boolean\"}}}",
            Ask.Changes, Divide, a => a.TryGetProperty("levels", out var l) && l.ValueKind == JsonValueKind.True ? $"Divide {a.Texts("objects").Count} objects at story levels" : $"Divide {a.Texts("objects").Count} beams into {a.Text("parts")}"),
    ];

    private static List<double[]> Numbers(JsonElement a, string name, int count)
    {
        var list = a.Items(name).Select(x => x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().Select(v => v.GetDouble()).ToArray() : []).ToList();
        if (list.Count == 0) throw new ToolInputException($"Give {name}.");
        if (list.Any(x => x.Length != count)) throw new ToolInputException($"Each of {name} is {count} numbers.");
        return list;
    }

    private static List<string> Stories(JsonElement a, StructuralModel m)
    {
        var stories = a.Texts("stories");
        if (stories.Count == 0) throw new ToolInputException("Give the stories.");
        foreach (var s in stories) if (m.StoryIndex(s) < 0) throw new ToolInputException($"There is no story '{s}'.");
        return stories;
    }

    private static JsonNode AddBraces(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var braces = Numbers(a, "braces", 4);
        var stories = Stories(a, link.Model);
        var section = a.Need("section");
        if (!link.Model.FrameSections.Exists(s => s.Name == section)) throw new ToolInputException($"There is no frame section '{section}'.");
        return Core(() => link.Edit(m =>
        {
            var made = new List<string>();
            foreach (var s in stories)
                foreach (var b in braces)
                {
                    if (Math.Abs(b[0] - b[2]) < ModelEdit.Tolerance && Math.Abs(b[1] - b[3]) < ModelEdit.Tolerance) throw new ArgumentException("A brace has two ends apart in plan (one at the same place is a column).");
                    var x = m.AddBrace(b[0], b[1], b[2], b[3], s, section);
                    made.Add(x.Label + "@" + x.Story);
                }
            return (JsonNode)new JsonObject { ["braces"] = new JsonArray(made.Select(x => (JsonNode)x).ToArray()) };
        }));
    }

    private static JsonNode AddJoints(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var points = Numbers(a, "points", 2);
        var stories = Stories(a, link.Model);
        return Core(() => link.Edit(m =>
        {
            var made = new List<string>();
            foreach (var s in stories)
                foreach (var p in points)
                {
                    var point = m.PointAt(p[0], p[1]);
                    // A joint of its own on the story: an assignment line, as ETABS writes a joint drawn by itself.
                    if (!m.PointAssigns.Exists(x => x.Point == point.Name && x.Story == s)) m.PointAssigns.Add(new PointAssign { Point = point.Name, Story = s });
                    made.Add(point.Name + "@" + s);
                }
            return (JsonNode)new JsonObject { ["joints"] = new JsonArray(made.Select(x => (JsonNode)x).ToArray()) };
        }));
    }

    private static JsonNode AddFramesByPoints(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var frames = Numbers(a, "frames", 6);
        var section = a.Need("section");
        var brace = a.TryGetProperty("brace", out var b) && b.ValueKind == JsonValueKind.True;
        if (!link.Model.FrameSections.Exists(s => s.Name == section)) throw new ToolInputException($"There is no frame section '{section}'.");
        return Core(() => link.Edit(m =>
        {
            var made = frames.Select(f => m.AddFrame(new Xyz(f[0], f[1], f[2]), new Xyz(f[3], f[4], f[5]), section, brace)).Select(x => (JsonNode)(x.Label + "@" + x.Story)).ToArray();
            return (JsonNode)new JsonObject { ["frames"] = new JsonArray(made) };
        }));
    }

    private static JsonNode AddAreasByPoints(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        static Xyz Corner(JsonElement c) => c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 3
            ? new Xyz(c[0].GetDouble(), c[1].GetDouble(), c[2].GetDouble()) : throw new ToolInputException("Each corner is [x, y, z].");
        var areas = a.Items("areas").Select(x => x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().Select(Corner).ToList() : throw new ToolInputException("Each area is a list of [x, y, z] corners.")).ToList();
        if (areas.Count == 0) throw new ToolInputException("Give areas.");
        var section = a.Need("section");
        var wall = a.Text("wall_section") is { Length: > 0 } w ? w : null;
        foreach (var name in new[] { section, wall }.OfType<string>())
            if (!link.Model.ShellSections.Exists(s => s.Name == name)) throw new ToolInputException($"There is no slab or wall section '{name}'.");
        return Core(() => link.Edit(m =>
        {
            var diaphragm = m.Diaphragms.FirstOrDefault()?.Name ?? "D1"; // a floor's, as Draw Floor gives it
            var made = areas.Select(c => m.AddArea(c, section, diaphragm, wall)).Select(x => (JsonNode)(x.Label + "@" + x.Story)).ToArray();
            return (JsonNode)new JsonObject { ["areas"] = new JsonArray(made) };
        }));
    }

    private static List<ModelOps.Obj> Find(JsonElement a, StructuralModel m)
    {
        var names = a.Texts("objects");
        if (names.Count == 0) throw new ToolInputException("Name the objects, as label@story.");
        // The same object named twice is one object: divide_frames with B1@Story1 twice crashed on the second (fuzz seed 2381598).
        return Core(() => names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => ModelOps.Find(m, n)).Distinct().ToList());
    }

    private static JsonArray Names(IEnumerable<ModelOps.Obj> list) => new(list.Select(x => (JsonNode)x.ToString()).ToArray());

    private static JsonNode Replicate(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        var mode = a.Need("mode");
        var delete = a.TryGetProperty("delete_original", out var d) && d.ValueKind == JsonValueKind.True;
        return Core(() => link.Edit(m =>
        {
            var made = mode switch
            {
                "linear" => Math.Abs(a.Number("dz", 0)) > 0 ? ModelOps.ReplicateLinear(m, objects, a.Number("dx", 0), a.Number("dy", 0), a.Number("dz", 0), a.Whole("number", 1)) : ModelOps.ReplicateLinear(m, objects, a.Number("dx", 0), a.Number("dy", 0), a.Whole("number", 1)),
                "radial" => ModelOps.ReplicateRadial(m, objects, a.Number("cx", 0), a.Number("cy", 0), a.Number("angle", 90), a.Whole("number", 1)),
                "mirror" => ModelOps.ReplicateMirror(m, objects, a.Number("x1", 0), a.Number("y1", 0), a.Number("x2", 0), a.Number("y2", 1)),
                "story" => ModelOps.ReplicateStories(m, objects, Stories(a, m)),
                _ => throw new ArgumentException("mode is linear, radial, mirror or story."),
            };
            var skip = a.Texts("skip");
            foreach (var o in made) ModelOps.StripCopy(m, o, skip);
            // Delete Original Objects: the copies stay, the objects copied go.
            if (delete) foreach (var o in objects) ModelOps.Remove(m, o);
            return (JsonNode)new JsonObject { ["made"] = Names(made) };
        }));
    }

    private static JsonNode MoveObjects(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        return Core(() => link.Edit(m => (JsonNode)new JsonObject { ["moved"] = Names(ModelOps.Move(m, objects, a.Number("dx", 0), a.Number("dy", 0), a.Number("dz", 0))) }));
    }

    private static JsonNode ReverseFrames(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        return Core(() => link.Edit(m => (JsonNode)new JsonObject { ["reversed"] = Names(objects.Select(o => ModelOps.Reverse(m, o)).ToList()) }));
    }

    private static JsonNode JoinFrames(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        return Core(() => link.Edit(m =>
        {
            var before = objects.Count;
            var left = ModelOps.Join(m, objects);
            if (left.Count >= before) throw new ArgumentException("None of the selected beams run on from each other in a straight line, end to end on one story, with nothing else at the joint between.");
            return (JsonNode)new JsonObject { ["frames"] = Names(left) };
        }));
    }

    private static JsonNode AlignJoints(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var mode = a.Need("mode");
        if (mode is "trim" or "extend")
        {
            var objects = Find(a, link.Model);
            var boundary = Core(() => ModelOps.Find(link.Model, a.Need("boundary")));
            return Core(() => link.Edit(m =>
            {
                var n = ModelOps.TrimExtend(m, objects, boundary, mode == "extend");
                if (n == 0) throw new ArgumentException(mode == "trim" ? "No selected beam crosses the boundary beam's line between its ends." : "No selected beam stops short of the boundary beam's line.");
                return (JsonNode)new JsonObject { ["changed"] = n };
            }));
        }
        var rows = a.Items("joints").Select(x => x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().ToList() : []).ToList();
        if (rows.Count == 0) throw new ToolInputException("Select the joints to align.");
        if (rows.Any(x => x.Count is not (3 or 5) || x[2].ValueKind != JsonValueKind.String || x.Where((_, i) => i != 2).Any(y => y.ValueKind != JsonValueKind.Number))) throw new ToolInputException("Each joint is [x, y, story], or [x, y, story, new x, new y] for mode 'nearest'.");
        if (mode == "nearest")
            return Core(() => link.Edit(m =>
            {
                var n = 0;
                foreach (var r in rows.Where(r => r.Count == 5)) n += ModelOps.AlignJoints(m, [(r[0].GetDouble(), r[1].GetDouble(), r[2].GetString()!)], r[3].GetDouble(), r[4].GetDouble());
                if (n == 0) throw new ArgumentException("Each selected joint is already on the nearest selected frame or edge.");
                return (JsonNode)new JsonObject { ["aligned"] = n };
            }));
        if (mode is not ("x" or "y")) throw new ToolInputException("mode is x, y, nearest, trim or extend.");
        if (!a.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) throw new ToolInputException("Give the ordinate to align to.");
        var joints = rows.Select(x => (x[0].GetDouble(), x[1].GetDouble(), x[2].GetString()!)).ToList();
        return Core(() => link.Edit(m =>
        {
            var n = ModelOps.AlignJoints(m, joints, mode == "x" ? v.GetDouble() : null, mode == "y" ? v.GetDouble() : null);
            return (JsonNode)new JsonObject { ["aligned"] = n };
        }));
    }

    private static JsonNode ReplaceFloors(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        var outlines = a.Items("outlines").Select(o => o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 ? (p[0].GetDouble(), p[1].GetDouble()) : throw new ToolInputException("Each corner is [x, y].")).ToList() : throw new ToolInputException("Each outline is a list of [x, y].")).ToList();
        return Core(() => link.Edit(m => (JsonNode)new JsonObject { ["floors"] = Names(ModelOps.ReplaceFloors(m, objects, outlines.Select(o => (IReadOnlyList<(double, double)>)o).ToList())) }));
    }

    private static JsonNode RelabelObjects(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        return Core(() => link.Edit(m =>
        {
            var (n, kept) = ModelOps.Relabel(m);
            return (JsonNode)new JsonObject { ["relabelled"] = n, ["kept"] = new JsonArray(kept.Select(k => (JsonNode)k).ToArray()) };
        }));
    }

    private static JsonNode AddTemplate(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var kind = a.Need("kind").ToLowerInvariant();
        return Core(() => link.Edit(m =>
        {
            List<string> Range()
            {
                var top = m.StoryIndex(a.Need("top_story")); var bottom = m.StoryIndex(a.Need("bottom_story"));
                if (top < 1 || bottom < 1) throw new ArgumentException("Pick a top and a bottom story above the base.");
                if (bottom > top) throw new ArgumentException("The bottom story is above the top story.");
                return m.Stories.Skip(bottom).Take(top - bottom + 1).Select(s => s.Name).ToList();
            }
            var at = new AddTemplates2D.Place { X = a.Number("x", 0), Y = a.Number("y", 0), Angle = a.Number("angle", 0) };
            AddTemplates2D.Made made;
            switch (kind)
            {
                case "2d_frame":
                    made = AddTemplates2D.Frame(m, a.Text("type", "portal"), Range(), a.Whole("bays", 1), a.Number("bay_width"), a.Number("gap_width", 0),
                        a.Need("columns"), a.Need("beams"), a.Text("braces"), a.Text("restraints", "pinned"), at);
                    break;
                case "2d_truss":
                    made = AddTemplates2D.Truss(m, a.Text("type", "sloped"), a.Need("story"), a.Whole("divisions", 1), a.Number("length"), a.Number("height"),
                        a.Need("chords"), a.Need("diagonals"), a.Text("verticals"), at);
                    break;
                case "2d_wall":
                    AddTemplates2D.Opening? op = null;
                    if (a.TryGetProperty("opening", out var o) && o.ValueKind == JsonValueKind.Object)
                        op = new(o.Number("l", 0), o.Number("h", 0), o.Number("b", 0), o.Number("d", 0));
                    made = AddTemplates2D.Wall(m, Range(), a.Whole("bays", 1), a.Number("bay_width"), a.Need("section"), op, a.Text("restraints", "pinned"), at);
                    break;
                default:
                {
                    var t = ModelerTools.Quick(a, m.Title, kind);
                    if (a.TryGetProperty("use", out var u) && u.ValueKind == JsonValueKind.Object)
                        foreach (var p in u.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) t.Use[p.Name] = v;
                    var areas = m.AreaAssigns.Count; var frames = m.LineAssigns.Count;
                    QuickTemplates.AddTo(m, t, Range(), m.Grids.Where(g => g.Direction == "X").Select(g => g.Coordinate).ToArray(), m.Grids.Where(g => g.Direction == "Y").Select(g => g.Coordinate).ToArray());
                    return (JsonNode)new JsonObject { ["frames"] = m.LineAssigns.Count - frames, ["floors"] = m.AreaAssigns.Count - areas };
                }
            }
            return (JsonNode)new JsonObject
            {
                ["frames"] = new JsonArray(made.Frames.Select(f => (JsonNode)f).ToArray()),
                ["walls"] = new JsonArray(made.Walls.Select(f => (JsonNode)f).ToArray()), ["supports"] = made.Supports,
            };
        }));
    }

    private static JsonNode MergeJoints(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var joints = a.Texts("joints");
        if (joints.Count < 2) throw new ToolInputException("Select two joints or more to merge.");
        return Core(() => link.Edit(m =>
        {
            var (merged, removed) = ModelOps.MergePoints(m, joints, a.Number("tolerance", 0));
            return (JsonNode)new JsonObject { ["merged"] = merged, ["removed"] = Names(removed) };
        }));
    }

    private static JsonNode Reshape(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var o = Core(() => ModelOps.Find(link.Model, a.Need("object")));
        var f = Pair(a, "from"); var t = Pair(a, "to");
        return Core(() => link.Edit(m => (JsonNode)new JsonObject { ["reshaped"] = ModelOps.Reshape(m, o, f[0], f[1], t[0], t[1]).ToString() }));
    }

    private static double[] Pair(JsonElement a, string name)
    {
        var v = a.Items(name).Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
        if (v.Length != 2 || v.Any(double.IsNaN)) throw new ToolInputException($"Give {name} as [x, y].");
        return v;
    }

    private static JsonNode Divide(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        if (a.TryGetProperty("levels", out var lv) && lv.ValueKind == JsonValueKind.True)
            return Core(() => link.Edit(m =>
            {
                // Only the frames, walls and ramps that span more than one story are broken; one selected with them that does not stays.
                bool Many(ModelOps.Obj o) => o.Line ? m.Line(o.Label)!.StoriesBelowI > 1 : m.Area(o.Label)!.StoriesBelow is { Count: > 0 } b && b.Max() - b.Min() > 1;
                var many = objects.Where(Many).ToList();
                if (many.Count == 0) throw new ArgumentException("None of the selected objects spans more than one story: no story level lies between their ends.");
                return (JsonNode)new JsonObject { ["parts"] = Names(many.SelectMany(o => o.Line ? ModelOps.DivideAtLevels(m, o) : ModelOps.DivideAreaAtLevels(m, o)).ToList()) };
            }));
        if (a.TryGetProperty("points", out var pv) && pv.ValueKind == JsonValueKind.Array)
        {
            var pts = Numbers(a, "points", 2);
            return Core(() => link.Edit(m =>
            {
                var made = new List<ModelOps.Obj>();
                foreach (var o in objects)
                {
                    if (!o.Line) continue;
                    var line = m.Line(o.Label)!; var (i, j) = (m.Point(line.PointI)!, m.Point(line.PointJ)!);
                    var (dx, dy) = (j.X - i.X, j.Y - i.Y); var l2 = dx * dx + dy * dy; if (l2 <= 0) continue;
                    var at = pts.Select(p => (t: ((p[0] - i.X) * dx + (p[1] - i.Y) * dy) / l2, p))
                        .Where(x => Math.Abs((x.p[0] - i.X) * dy - (x.p[1] - i.Y) * dx) / Math.Sqrt(l2) <= ModelEdit.Tolerance * 10).Select(x => x.t).ToList();
                    if (at.Any(t => t > 1e-6 && t < 1 - 1e-6)) made.AddRange(ModelOps.DivideAt(m, o, at));
                }
                if (made.Count == 0) throw new ArgumentException("Nothing crosses the selected beams between their ends: they stay whole.");
                return (JsonNode)new JsonObject { ["parts"] = Names(made) };
            }));
        }
        var parts = a.Whole("parts", 2);
        return Core(() => link.Edit(m => (JsonNode)new JsonObject { ["parts"] = Names(objects.SelectMany(o => ModelOps.Divide(m, o, parts)).ToList()) }));
    }
}
