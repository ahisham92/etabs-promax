using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

/// <summary>
/// The Modeler's Draw Braces, Draw Joints and Edit > Replicate, Move and Divide Frames (EditTools, ModelOps): a copy
/// keeps what the original has, and a refusal leaves the model as it was.
/// </summary>
static class EditTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Edit", [6, 5], [4], 3, 3.2));

    static JsonObject Run(OfflineEtabs link, string tool, object args) =>
        (JsonObject)EditTools.All.Single(x => x.Name == tool).Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static List<string> Names(JsonObject r, string field) => r[field]!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    static void Refused(OfflineEtabs link, string tool, object args, string says)
    {
        var before = ModelFile.ToJson(link.Model);
        try { Run(link, tool, args); }
        catch (ToolInputException ex)
        {
            Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), $"{tool}: '{says}' expected in: {ex.Message}");
            Check(ModelFile.ToJson(link.Model) == before, $"{tool}: a refusal changed the model");
            return;
        }
        throw new Exception($"{tool} took {JsonSerializer.Serialize(args)}");
    }

    /// <summary>The beam of the frame along y = 0 from x = 0 to 6, as label@story.</summary>
    static string FirstBeam(StructuralModel m, string story = "Story1")
    {
        var line = m.Lines.First(l => l.Kind == LineKind.Beam && m.Point(l.PointI)!.Y == 0 && m.Point(l.PointJ)!.Y == 0 && Math.Min(m.Point(l.PointI)!.X, m.Point(l.PointJ)!.X) == 0);
        return line.Label + "@" + story;
    }

    static (string Label, string Story) Split(string name) => (name[..name.IndexOf('@')], name[(name.IndexOf('@') + 1)..]);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("edit: Replicate linear copies a beam with its section, releases and loads; onto an existing beam it is refused", () =>
        {
            var link = Frame();
            var m = link.Model;
            var beam = FirstBeam(m);
            var (label, story) = Split(beam);
            PropertyDefs.SetReleases(m.LineAssigns.First(a => a.Label == label && a.Story == story), ["M3"], ["M3"]);
            m.AddLineLoad(label, story, "Dead", 5);
            var r = Run(link, "replicate_objects", new { objects = new[] { beam }, mode = "linear", dy = 8.0, number = 2 });
            var made = Names(r, "made");
            Check(made.Count == 2, "two copies: " + string.Join(", ", made));
            foreach (var (n, y) in made.Zip(new[] { 8.0, 16.0 }))
            {
                var (l, s) = Split(n);
                var line = m.Line(l)!;
                Check(s == story && m.Point(line.PointI)!.Y == y && m.Point(line.PointJ)!.Y == y, $"{n} at y = {y}");
                var a = m.LineAssigns.Single(x => x.Label == l && x.Story == s);
                Check(a.Section == "B300x600", $"{n} keeps the section");
                Check(PropertyDefs.ReleasesOf(a).I.Contains("M3") && PropertyDefs.ReleasesOf(a).J.Contains("M3"), $"{n} keeps the releases");
                Check(m.LineLoads.Count(x => x.Object == l && x.Story == s) == 1, $"{n} keeps its load");
            }
            Refused(link, "replicate_objects", new { objects = new[] { beam }, mode = "linear", dy = 4.0, number = 1 }, "already");
            Refused(link, "replicate_objects", new { objects = new[] { beam }, mode = "linear", number = 1 }, "dx or dy");
            Refused(link, "replicate_objects", new { objects = new[] { "B999@Story1" }, mode = "linear", dx = 1.0 }, "no frame");
        });

        yield return ("edit: Replicate to stories puts a beam back on a story that lost it, as the same object", () =>
        {
            var link = Frame();
            var m = link.Model;
            var beam = FirstBeam(m);
            var (label, _) = Split(beam);
            ModelOps.Remove(m, new ModelOps.Obj(true, label, "Story2"));
            var lines = m.Lines.Count;
            var made = Names(Run(link, "replicate_objects", new { objects = new[] { beam }, mode = "story", stories = new[] { "Story2" } }), "made");
            Check(made.SequenceEqual([label + "@Story2"]), "the same label on Story2: " + string.Join(", ", made));
            Check(m.Lines.Count == lines, "no new line object");
            Refused(link, "replicate_objects", new { objects = new[] { beam }, mode = "story", stories = new[] { "Story3" } }, "already");
            Refused(link, "replicate_objects", new { objects = new[] { beam }, mode = "story", stories = new[] { "Story9" } }, "no story");
            Refused(link, "replicate_objects", new { objects = new[] { beam }, mode = "story", stories = new[] { "Story1" } }, "already on");
        });

        yield return ("edit: Move takes a slab and its loads to a new place and off the old one", () =>
        {
            var link = Frame();
            var m = link.Model;
            var slab = m.AreaAssigns.First(a => a.Story == "Story1");
            var loads = m.AreaLoads.Count(l => l.Object == slab.Label && l.Story == "Story1");
            var moved = Names(Run(link, "move_objects", new { objects = new[] { slab.Label + "@Story1" }, dy = 10.0 }), "moved");
            var (l2, s2) = Split(moved.Single());
            Check(l2 != slab.Label && s2 == "Story1", "a new slab on Story1: " + moved.Single());
            Check(!m.AreaAssigns.Exists(a => a.Label == slab.Label && a.Story == "Story1"), "the old one is gone");
            Check(m.Area(l2)!.PointNames.All(p => m.Point(p)!.Y >= 10), "it is 10 m on");
            Check(m.AreaLoads.Count(l => l.Object == l2 && l.Story == "Story1") == loads, "its loads went with it");
            Check(m.AreaAssigns.Single(a => a.Label == l2 && a.Story == "Story1").Section == slab.Section, "its section went with it");
        });

        yield return ("edit: Divide Frames makes equal beams; uniform loads go on each, releases stay at the ends; a column or a point load is refused", () =>
        {
            var link = Frame();
            var m = link.Model;
            var beam = FirstBeam(m);
            var (label, story) = Split(beam);
            PropertyDefs.SetReleases(m.LineAssigns.First(a => a.Label == label && a.Story == story), ["M3"], ["T"]);
            m.AddLineLoad(label, story, "Dead", 5);
            // The same beam named twice is divided once (fuzz seed 2381598 crashed: "Sequence contains no matching element").
            var parts = Names(Run(link, "divide_frames", new { objects = new[] { beam, beam }, parts = 3 }), "parts");
            Check(parts.Count == 3, "three parts");
            Check(!m.LineAssigns.Exists(a => a.Label == label && a.Story == story), "the whole beam is gone");
            var xs = parts.Select(p => m.Line(Split(p).Label)!).Select(l => (m.Point(l.PointI)!.X, m.Point(l.PointJ)!.X)).ToList();
            Check(xs.Zip(new[] { (0.0, 2.0), (2.0, 4.0), (4.0, 6.0) }).All(z => Math.Abs(Math.Min(z.First.Item1, z.First.Item2) - z.Second.Item1) < 1e-6 && Math.Abs(Math.Max(z.First.Item1, z.First.Item2) - z.Second.Item2) < 1e-6), "equal thirds: " + string.Join(" ", xs));
            var assigns = parts.Select(p => m.LineAssigns.Single(a => a.Label == Split(p).Label && a.Story == story)).ToList();
            Check(assigns.All(a => a.Section == "B300x600"), "each keeps the section");
            Check(PropertyDefs.ReleasesOf(assigns[0]).I.SequenceEqual(["M3"]) && PropertyDefs.ReleasesOf(assigns[0]).J.Count == 0, "the I release on the first part only");
            Check(PropertyDefs.ReleasesOf(assigns[1]).I.Count == 0 && PropertyDefs.ReleasesOf(assigns[1]).J.Count == 0, "none inside");
            Check(PropertyDefs.ReleasesOf(assigns[2]).J.SequenceEqual(["T"]) && PropertyDefs.ReleasesOf(assigns[2]).I.Count == 0, "the J release on the last part only");
            Check(parts.All(p => m.LineLoads.Count(l => l.Object == Split(p).Label && l.Story == story) == 1), "the uniform load on every part");
            var column = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Column);
            Refused(link, "divide_frames", new { objects = new[] { column.Label + "@" + column.Story }, parts = 2 }, "divides beams");
            Refused(link, "divide_frames", new { objects = new[] { parts[0] }, parts = 30 }, "2 to 20");
            var other = FirstBeam(m, "Story2");
            var (ol, os) = Split(other);
            m.LineLoads.Add(new LineLoad { Object = ol, Story = os, Tokens = E2kTokens("TYPE \"POINTF\" DIR \"GRAV\" LC \"Dead\" FVAL 10 RELDIST 0.5") });
            Refused(link, "divide_frames", new { objects = new[] { other }, parts = 2 }, "cannot be shared out");
        });

        yield return ("edit: Reshape Object moves one end of a beam or one corner of a slab on its own story only, keeping section and loads; a point it does not have is refused", () =>
        {
            var link = Frame();
            var m = link.Model;
            var beam = FirstBeam(m);
            var (label, story) = Split(beam);
            m.AddLineLoad(label, story, "Dead", 5);
            var made = Run(link, "reshape_object", new { @object = beam, from = new[] { 6.0, 0 }, to = new[] { 6.0, 1.5 } })["reshaped"]!.GetValue<string>();
            var (l2, s2) = Split(made);
            Check(s2 == story && l2 != label, "a reshaped beam on the same story: " + made);
            var line = m.Line(l2)!;
            var ends = new[] { m.Point(line.PointI)!, m.Point(line.PointJ)! }.Select(p => (p.X, p.Y)).OrderBy(p => p.X).ToList();
            Check(ends[0] == (0.0, 0.0) && ends[1] == (6.0, 1.5), "the end moved, the other stayed: " + string.Join(" ", ends));
            Check(m.LineAssigns.Single(a => a.Label == l2 && a.Story == story).Section == "B300x600", "the section stayed");
            Check(m.LineLoads.Count(l => l.Object == l2 && l.Story == story) == 1, "the load went with it");
            Check(!m.LineAssigns.Exists(a => a.Label == label && a.Story == story), "the old beam is off Story1");
            Check(m.LineAssigns.Exists(a => a.Label == label && a.Story == "Story2"), "the beam above is untouched");
            var slab = m.AreaAssigns.First(a => a.Story == "Story1");
            var corner = m.Area(slab.Label)!.PointNames.Select(n => m.Point(n)!).First();
            var moved = Run(link, "reshape_object", new { @object = slab.Label + "@Story1", from = new[] { corner.X, corner.Y }, to = new[] { corner.X - 1, corner.Y - 1 } })["reshaped"]!.GetValue<string>();
            Check(m.Area(Split(moved).Label)!.PointNames.Select(n => m.Point(n)!).Any(p => p.X == corner.X - 1 && p.Y == corner.Y - 1), "the slab corner moved");
            Refused(link, "reshape_object", new { @object = made, from = new[] { 3.0, 3 }, to = new[] { 4.0, 4 } }, "no end or corner");
            Refused(link, "reshape_object", new { @object = made, from = new[] { 6.0, 1.5 }, to = new[] { 6.0, 1.5 } }, "where it was");
            Refused(link, "reshape_object", new { @object = made, from = new[] { 6.0, 1.5 }, to = new[] { 0.0, 0 } }, "both its ends");
        });

        yield return ("edit: Draw Braces and Draw Joints add them on each story given", () =>
        {
            var link = Frame();
            var m = link.Model;
            var braces = Names(Run(link, "add_braces", new { braces = new[] { new[] { 0.0, 0, 6, 0 } }, stories = new[] { "Story1", "Story2" }, section = "C500x500" }), "braces");
            Check(braces.Count == 2, "a brace on each story");
            foreach (var b in braces)
            {
                var line = m.Line(Split(b).Label)!;
                Check(line.Kind == LineKind.Brace && line.StoriesBelowI == 1, $"{b} is a brace from the story below");
            }
            Refused(link, "add_braces", new { braces = new[] { new[] { 0.0, 0, 0, 0 } }, stories = new[] { "Story1" }, section = "C500x500" }, "apart in plan");
            Refused(link, "add_braces", new { braces = new[] { new[] { 0.0, 0, 6, 0 } }, stories = new[] { "Story1" }, section = "NOPE" }, "no frame section");
            var joints = Names(Run(link, "add_joints", new { points = new[] { new[] { 3.0, 2.0 } }, stories = new[] { "Story3" } }), "joints");
            var (p, s) = Split(joints.Single());
            Check(m.Point(p) is { X: 3, Y: 2 } && m.PointAssigns.Exists(a => a.Point == p && a.Story == s), "a joint at (3, 2) on Story3");
            Refused(link, "add_joints", new { points = new[] { new[] { 3.0 } }, stories = new[] { "Story3" } }, "2 numbers");
        });
    }

    static List<string> E2kTokens(string text) => CsiProMax.E2k.E2kText.Tokenize(text);
}

/// <summary>A report's text of an answer whose array was filled with Add&lt;T&gt;, as the first JSON written in the run (the
/// Modeler part of the field test, run by itself, failed on select_objects's "failed" list).</summary>
static class ReadableTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("readable: an answer with a text put in by JsonArray.Add<T> is written", () =>
        {
            var failed = new JsonArray();
            failed.Add("no such frame: There is no frame named \"x\".");
            var text = CsiProMax.Assistant.Tools.Readable.Text(new JsonObject { ["failed"] = failed });
            if (!text.Contains("no such frame", StringComparison.Ordinal)) throw new Exception(text);
        });
    }
}
