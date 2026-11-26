using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's safety net (Edit > Model Text): the model as the program's own text file (.e2k), one section at a
/// time, which can be read and changed by hand. Anything the forms and tools do not offer yet can still be put in
/// here, in the program's own words. A change is only taken when the whole file still reads back, nothing is lost
/// that was not edited, and writing it again gives the same model. Modeler only.
/// </summary>
public static class ModelTextTools
{
    private const string EndHeading = "END OF MODEL FILE";

    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("Model Text is the Modeler's own item (no CSI program behind it). In a live program use edit_table.");

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("model_text",
            "The model as the program's text file (.e2k), by section (Edit > Model Text). With no section: the list of sections with their line counts. With a section heading (e.g. \"LOAD CASES\", \"FRAME OBJECT LOADS\", \"FUNCTIONS\"): that section's lines, as edit_model_text takes them.",
            """{"type":"object","properties":{"section":{"type":"string"}}}""",
            Ask.Never, (a, e) => Read(Offline(e).Model, a.Text("section"))),
        new("edit_model_text",
            "Replaces the lines of one section of the model's text file (.e2k), or adds a new section, in the program's own syntax (Edit > Model Text). The safety net for anything no form or tool offers yet. The whole file must still read, every other section stays as it was, and the model must write back the same; otherwise nothing changes and the reason is given. check_only: only say what would change. An empty text removes the section.",
            """{"type":"object","required":["section","text"],"properties":{"section":{"type":"string"},"text":{"type":"string"},"check_only":{"type":"boolean"}}}""",
            Ask.Changes, Edit, a => $"Edit the model text of {a.Text("section")}"),
    ];

    // ---- the text, by section ----

    private sealed record Section(string Heading, List<string> Lines);

    /// <summary>The file's sections in order. The lines before the first heading (the file's name line) come first, under "".</summary>
    private static List<Section> Split(string text)
    {
        var list = new List<Section> { new("", []) };
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.TrimStart();
            if (t.StartsWith('$') && !(list.Count == 1 && t[1..].Trim().StartsWith("File ", StringComparison.OrdinalIgnoreCase)))
                list.Add(new(t[1..].Trim(), []));
            else list[^1].Lines.Add(line);
        }
        // A section ends with the blank line the writer puts before the next heading: that is not one of its lines.
        foreach (var s in list) while (s.Lines.Count > 0 && s.Lines[^1].Trim().Length == 0) s.Lines.RemoveAt(s.Lines.Count - 1);
        return list;
    }

    private static string Join(List<Section> sections)
    {
        var text = new StringBuilder();
        foreach (var s in sections)
        {
            if (s.Heading.Length > 0) text.Append("$ ").Append(s.Heading).Append("\r\n");
            foreach (var l in s.Lines) text.Append(l).Append("\r\n");
            if (s.Heading != EndHeading) text.Append("\r\n");
        }
        return text.ToString();
    }

    private static Section? Find(List<Section> sections, string heading) =>
        sections.FirstOrDefault(s => s.Heading.Length > 0 && string.Equals(s.Heading, Normal(heading), StringComparison.OrdinalIgnoreCase));

    private static string Normal(string heading) => heading.Trim().TrimStart('$').Trim();

    private static JsonNode Read(StructuralModel m, string section)
    {
        var sections = Split(E2kWriter.Write(m));
        if (section.Trim().Length == 0)
            return new JsonObject
            {
                ["sections"] = new JsonArray(sections.Where(s => s.Heading.Length > 0 && s.Heading != EndHeading)
                    .Select(s => (JsonNode)new JsonObject { ["section"] = s.Heading, ["lines"] = s.Lines.Count }).ToArray()),
                ["note"] = "Read a section with model_text {section}, and change it with edit_model_text. Any section the program's .e2k has can be added, in its own syntax.",
            };
        var found = Find(sections, section);
        if (found is null)
            return new JsonObject
            {
                ["section"] = Normal(section).ToUpperInvariant(), ["lines"] = 0, ["text"] = "", ["exists"] = false,
                ["note"] = $"The model text has no such section yet; edit_model_text adds it. Its sections are: {string.Join(", ", sections.Where(s => s.Heading.Length > 0 && s.Heading != EndHeading).Select(s => s.Heading))}.",
            };
        return new JsonObject { ["section"] = found.Heading, ["lines"] = found.Lines.Count, ["text"] = string.Join("\n", found.Lines), ["exists"] = true };
    }

    // ---- the change ----

    private static JsonNode Edit(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var heading = Normal(a.Need("section")).ToUpperInvariant();
        if (heading.Length == 0) throw new ToolInputException("Give the section's heading, e.g. \"LOAD CASES\".");
        if (heading == EndHeading || heading.StartsWith("FILE ", StringComparison.Ordinal))
            throw new ToolInputException($"'{heading}' is the file's own frame, not a section of the model.");
        if (heading == "PROGRAM INFORMATION")
            throw new ToolInputException("PROGRAM INFORMATION names the program version the file is for; the Modeler sets it. Change the units or title with the model's own tools.");
        var lines = a.Text("text").Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        var other = lines.FirstOrDefault(l => l.TrimStart().StartsWith('$'));
        if (other is not null)
            throw new ToolInputException($"The text has a heading line ('{other.Trim()}'). Give one section at a time: its lines only, without its '$' heading.");

        var old = link.Model;
        var oldText = E2kWriter.Write(old);
        var sections = Split(oldText);
        var target = Find(sections, heading);
        var was = target?.Lines.Count ?? 0;
        if (target is null && lines.Count == 0) throw new ToolInputException($"The model text has no section '{heading}' to remove.");
        if (target is null)
        {
            var end = sections.FindIndex(s => s.Heading == EndHeading);
            sections.Insert(end < 0 ? sections.Count : end, new Section(heading, lines));
        }
        else if (lines.Count == 0) sections.Remove(target);
        else { target.Lines.Clear(); target.Lines.AddRange(lines); }
        var text = Join(sections);

        StructuralModel next;
        try { next = E2kReader.Read(text); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
        {
            throw new ToolInputException($"Nothing was changed: the model text no longer reads ({ex.Message}). Check the line it names against the program's .e2k syntax.");
        }
        Carry(old, next);

        // Every section not edited must come out as it went in; and the model must write back the same, so that a save
        // and a reopen give the model now on screen.
        var after = Split(E2kWriter.Write(next));
        var lost = new List<string>();
        foreach (var s in Split(oldText).Where(s => s.Heading.Length > 0 && !string.Equals(s.Heading, heading, StringComparison.OrdinalIgnoreCase)))
        {
            var now = Find(after, s.Heading);
            if (now is null) { if (s.Lines.Count > 0) lost.Add(s.Heading + " is gone"); continue; }
            if (!now.Lines.SequenceEqual(s.Lines)) lost.Add(s.Heading + " changed");
        }
        // The point loads a code's seismic or wind load makes are worked out again, so that section may change by itself.
        lost.RemoveAll(l => l.StartsWith("POINT OBJECT LOADS", StringComparison.Ordinal) && next.LoadPatterns.Any(p => p.Auto is not null));
        if (lost.Count > 0)
            throw new ToolInputException($"Nothing was changed: the edit would also change other sections ({string.Join("; ", lost)}). Check the names in the text, e.g. a story, point, section or pattern that another section uses.");
        // Nothing may be left pointing at what is not there (a line at a point, an assign at a line, a load at a pattern):
        // the model's own checks may find no error the model did not have before.
        var errors = Errors(next);
        if (errors is null) throw new ToolInputException("Nothing was changed: the model text reads, but the model it gives cannot be checked. Check the names the text uses.");
        var had = Errors(old) ?? [];
        var fresh = errors.Where(x => !had.Contains(x)).Take(5).ToList();
        if (fresh.Count > 0)
            throw new ToolInputException($"Nothing was changed: the edit would leave the model broken ({string.Join(" ", fresh)}).");
        var again = E2kReader.Read(E2kWriter.Write(next));
        Carry(next, again);
        if (!Same(after, Split(E2kWriter.Write(again))))
            throw new ToolInputException("Nothing was changed: the Modeler could not write this text back the same way it read it. Write it as the program's own .e2k export writes it.");

        var mine = Find(after, heading);
        var kept = next.UnparsedLines.TryGetValue(heading, out var u) ? u.Count : 0;
        var result = new JsonObject
        {
            ["section"] = heading,
            ["lines_before"] = was,
            ["lines_after"] = mine?.Lines.Count ?? 0,
            ["changes"] = Changes(old, next),
            ["applied"] = false,
        };
        if (kept > 0) result["kept_as_written"] = $"{kept} line(s) of this section are not read by the Modeler; they are kept word for word and go into the file for the program to read.";
        else if (next.Passthrough.Any(p => p.Heading == heading))
            result["kept_as_written"] = "The Modeler keeps this section word for word; it goes into the file for the program to read.";
        if (a.Flag("check_only")) { result["note"] = "Only checked: nothing was changed."; return result; }
        link.Restore(next, link.Path);
        result["applied"] = true;
        return result;
    }

    /// <summary>The same sections with the same lines (the file's name and date line aside).</summary>
    private static bool Same(List<Section> a, List<Section> b)
    {
        var x = a.Where(s => s.Heading.Length > 0).ToList();
        var y = b.Where(s => s.Heading.Length > 0).ToList();
        return x.Count == y.Count && x.Zip(y).All(p => p.First.Heading == p.Second.Heading && p.First.Lines.SequenceEqual(p.Second.Lines));
    }

    private static HashSet<string>? Errors(StructuralModel m)
    {
        try { return CsiProMax.Checks.ModelChecks.Run(m).Where(f => f.Severity == CsiProMax.Checks.Severity.Error).Select(f => f.Message).ToHashSet(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>What the .e2k does not hold, carried over from the model before: the codes behind the seismic and wind loads, and the title.</summary>
    private static void Carry(StructuralModel from, StructuralModel to)
    {
        foreach (var p in to.LoadPatterns)
            if (from.LoadPatterns.FirstOrDefault(q => q.Name == p.Name) is { Auto: not null } q && string.Equals(q.Type, p.Type, StringComparison.OrdinalIgnoreCase))
                p.Auto = q.Auto;
        if (to.Title.Length == 0) to.Title = from.Title;
        // What the .e2k has no place for (SAP2000 constraints and moving loads, SAFE strips) is not in the text.
        to.Extras ??= from.Extras;
    }

    private static JsonObject Changes(StructuralModel a, StructuralModel b)
    {
        var o = new JsonObject();
        void Count(string what, int x, int y) { if (x != y) o[what] = $"{x} -> {y}"; }
        Count("stories", a.Stories.Count, b.Stories.Count);
        Count("grids", a.Grids.Count, b.Grids.Count);
        Count("materials", a.Materials.Count, b.Materials.Count);
        Count("frame_sections", a.FrameSections.Count, b.FrameSections.Count);
        Count("shell_sections", a.ShellSections.Count, b.ShellSections.Count);
        Count("points", a.Points.Count, b.Points.Count);
        Count("lines", a.Lines.Count, b.Lines.Count);
        Count("areas", a.Areas.Count, b.Areas.Count);
        Count("load_patterns", a.LoadPatterns.Count, b.LoadPatterns.Count);
        Count("point_loads", a.PointLoads.Count, b.PointLoads.Count);
        Count("frame_loads", a.LineLoads.Count, b.LineLoads.Count);
        Count("shell_loads", a.AreaLoads.Count, b.AreaLoads.Count);
        Count("load_cases", a.LoadCases.Count, b.LoadCases.Count);
        Count("combinations", a.Combos.Count, b.Combos.Count);
        Count("other_sections", a.Passthrough.Count, b.Passthrough.Count);
        return o;
    }
}
