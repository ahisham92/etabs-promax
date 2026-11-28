using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

/// <summary>
/// A frame or wall drawn across story levels stays one object (the user's choice, 2026-11-28); Divide Frames' and Divide
/// Shells' story-levels choices (divide_frames 'levels') make it one per story, keeping what it has.
/// </summary>
static class LevelDivideTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static JsonObject Run(OfflineEtabs link, string tool, object args) =>
        (JsonObject)EditTools.All.Single(x => x.Name == tool).Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static List<string> Names(JsonObject r, string field) => r[field]!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    static (string Label, string Story) Split(string name) => (name[..name.IndexOf('@')], name[(name.IndexOf('@') + 1)..]);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("edit: a brace drawn over three stories is one object; Divide Frames at story levels makes it one per story, its end releases at its own ends", () =>
        {
            var link = new OfflineEtabs(Templates.ConcreteFrame("Levels", [6, 5], [4], 3, 3.2));
            var m = link.Model;
            var made = Names(Run(link, "add_frames_by_points", new { frames = new[] { new[] { 0.0, 0, 0, 6, 4, 9.6 } }, section = "C500x500", brace = true }), "frames");
            Check(made.Count == 1, "one brace: " + string.Join(", ", made));
            var (label, story) = Split(made[0]);
            Check(story == "Story3" && m.Line(label)!.Kind == LineKind.Brace && m.Line(label)!.StoriesBelowI == 3, $"a brace three stories deep, on Story3: {made[0]}");
            PropertyDefs.SetReleases(m.LineAssigns.First(a => a.Label == label && a.Story == story), ["M3"], ["M2"]);
            var parts = Names(Run(link, "divide_frames", new { objects = made, levels = true }), "parts");
            Check(parts.Select(p => Split(p).Story).SequenceEqual(["Story1", "Story2", "Story3"]), "one part per story: " + string.Join(", ", parts));
            Check(!m.LineAssigns.Exists(a => a.Label == label), "the brace over three stories is gone");
            for (var k = 0; k < 3; k++)
            {
                var (l, s) = Split(parts[k]);
                var line = m.Line(l)!; var a = m.LineAssigns.First(x => x.Label == l && x.Story == s);
                Check(line.Kind == LineKind.Brace && line.StoriesBelowI == 1 && a.Section == "C500x500", $"{parts[k]} is a one-story brace of the section");
                var (ri, rj) = PropertyDefs.ReleasesOf(a);
                Check(ri.SequenceEqual(k == 0 ? ["M3"] : []) && rj.SequenceEqual(k == 2 ? ["M2"] : []), $"{parts[k]}: releases {string.Join(" ", ri)} / {string.Join(" ", rj)}");
            }
            // A part spans one story now: dividing it again is refused, and the model stays as it is.
            var before = ModelFile.ToJson(m);
            try { Run(link, "divide_frames", new { objects = new[] { parts[0] }, levels = true }); throw new Exception("a one-story brace was divided at the levels"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("spans more than one story") && ModelFile.ToJson(m) == before, ex.Message); }
        });

        yield return ("edit: Quick Draw Braces in a plan's grid bay draws braces in the floor's plane, kept as braces on their story", () =>
        {
            var link = new OfflineEtabs(Templates.ConcreteFrame("Levels", [6, 5], [4], 3, 3.2));
            var m = link.Model;
            var made = Names(Run(link, "add_frames_by_points", new { frames = new[] { new[] { 0.0, 0, 6.4, 6, 4, 6.4 }, new[] { 6.0, 0, 6.4, 0, 4, 6.4 } }, section = "C500x500", brace = true }), "frames");
            Check(made.Count == 2 && made.All(n => Split(n).Story == "Story2"), "two braces on Story2: " + string.Join(", ", made));
            foreach (var n in made) { var line = m.Line(Split(n).Label)!; Check(line.Kind == LineKind.Brace && line.StoriesBelowI == 0, $"{n}: a brace with both ends on its story"); }
            // Without 'brace' the same level line is a beam, as Draw Beam/Column/Brace gives it.
            var beam = Names(Run(link, "add_frames_by_points", new { frames = new[] { new[] { 6.0, 0, 3.2, 11, 4, 3.2 } }, section = "C500x500" }), "frames");
            Check(m.Line(Split(beam[0]).Label)!.Kind == LineKind.Beam, "a level frame drawn without 'brace' is a beam");
        });

        yield return ("edit: a wall drawn over two stories is one object; Divide Shells' Walls and Ramps at Story Levels makes it one wall per story with its section", () =>
        {
            var link = new OfflineEtabs(Templates.ConcreteFrame("Levels", [6, 5], [4], 3, 3.2));
            var m = link.Model;
            m.AddWallSection("W200", ModelEdit.DefaultConcrete, 0.2);
            var corners = new[] { new[] { 0.0, 4, 0 }, new[] { 6.0, 4, 0 }, new[] { 6.0, 4, 6.4 }, new[] { 0.0, 4, 6.4 } };
            var made = Names(Run(link, "add_areas_by_points", new { areas = new[] { corners }, section = "S200", wall_section = "W200" }), "areas");
            Check(made.Count == 1, "one wall: " + string.Join(", ", made));
            var (label, story) = Split(made[0]);
            Check(story == "Story2" && m.Area(label)!.Kind == AreaKind.Wall && m.Area(label)!.StoriesBelow.Max() == 2, $"a wall two stories high, on Story2: {made[0]}");
            var parts = Names(Run(link, "divide_frames", new { objects = made, levels = true }), "parts");
            Check(parts.Select(p => Split(p).Story).SequenceEqual(["Story1", "Story2"]), "one part per story: " + string.Join(", ", parts));
            Check(!m.AreaAssigns.Exists(a => a.Label == label), "the wall over two stories is gone");
            foreach (var p in parts)
            {
                var (l, s) = Split(p);
                var area = m.Area(l)!; var a = m.AreaAssigns.First(x => x.Label == l && x.Story == s);
                Check(area.Kind == AreaKind.Wall && area.StoriesBelow.SequenceEqual([1, 1, 0, 0]) && a.Section == "W200", $"{p}: a one-story wall of W200 ({string.Join(",", area.StoriesBelow)}, {a.Section})");
            }
        });
    }
}
