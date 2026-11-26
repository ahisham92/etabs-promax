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
        new("replicate_objects",
            "Edit > Replicate: copies frames, slabs and walls with their sections, assignments, loads and hinges. mode 'linear': 'number' copies, each dx, dy on from the last; mode 'story': onto each of 'stories', in the same place.",
            "{\"type\":\"object\",\"required\":[\"objects\",\"mode\"],\"properties\":{" + Objects + ",\"mode\":{\"type\":\"string\",\"enum\":[\"linear\",\"story\"]},\"dx\":{\"type\":\"number\"},\"dy\":{\"type\":\"number\"},\"number\":{\"type\":\"integer\"},\"stories\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}}",
            Ask.Changes, Replicate, a => $"Replicate {a.Texts("objects").Count} objects"),
        new("move_objects",
            "Edit > Move: moves frames, slabs and walls by dx, dy in plan, with their sections, assignments, loads and hinges.",
            "{\"type\":\"object\",\"required\":[\"objects\"],\"properties\":{" + Objects + ",\"dx\":{\"type\":\"number\"},\"dy\":{\"type\":\"number\"}}}",
            Ask.Changes, MoveObjects, a => $"Move {a.Texts("objects").Count} objects"),
        new("divide_frames",
            "Edit > Edit Frames > Divide Frames: beams into 'parts' equal beams (2 to 20), each keeping the section and assignments; uniform loads go on every part, end releases and offsets stay at the beam's ends.",
            "{\"type\":\"object\",\"required\":[\"objects\",\"parts\"],\"properties\":{" + Objects + ",\"parts\":{\"type\":\"integer\"}}}",
            Ask.Changes, Divide, a => $"Divide {a.Texts("objects").Count} beams into {a.Text("parts")}"),
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
        return Core(() => link.Edit(m => (JsonNode)new JsonObject
        {
            ["made"] = Names(mode switch
            {
                "linear" => ModelOps.ReplicateLinear(m, objects, a.Number("dx", 0), a.Number("dy", 0), a.Whole("number", 1)),
                "story" => ModelOps.ReplicateStories(m, objects, Stories(a, m)),
                _ => throw new ArgumentException("mode is linear or story."),
            }),
        }));
    }

    private static JsonNode MoveObjects(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        return Core(() => link.Edit(m => (JsonNode)new JsonObject { ["moved"] = Names(ModelOps.Move(m, objects, a.Number("dx", 0), a.Number("dy", 0))) }));
    }

    private static JsonNode Divide(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var objects = Find(a, link.Model);
        var parts = a.Whole("parts", 2);
        return Core(() => link.Edit(m => (JsonNode)new JsonObject { ["parts"] = Names(objects.SelectMany(o => ModelOps.Divide(m, o, parts)).ToList()) }));
    }
}
