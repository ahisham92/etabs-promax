using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>What the programs' parts of the field test share: small helpers, and the checks every program's link must pass.</summary>
public static class FieldParts
{
    /// <summary>A text as a JSON string, for a tool's arguments.</summary>
    public static string Q(string text) => JsonSerializer.Serialize(text);

    /// <summary>Names as a JSON list.</summary>
    public static string List(IEnumerable<string> names) => "[" + string.Join(",", names.Select(Q)) + "]";

    /// <summary>A whole number from a tool's answer; -1 when it is not there.</summary>
    public static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : node is JsonValue d && d.TryGetValue<double>(out var x) ? (int)Math.Round(x) : -1;

    /// <summary>The names in a tool's answer under <paramref name="key"/> (the objects it added).</summary>
    public static List<string> Names(JsonNode? answer, string key = "names") =>
        answer?[key] is JsonArray list ? list.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() : [];

    /// <summary>The section name of the scratch part of a program.</summary>
    public static string ScratchSection(CsiProgram program) => $"{program.Name} on a scratch model (a second {program.Name} window)";

    /// <summary>
    /// Every read the tools make through the link, each as its own step, with every field of every row looked at: a
    /// name, a section, a kind or a coordinate that comes back empty or out of place is found here, not three tools later.
    /// </summary>
    public static void LinkReads(FieldContext ctx, bool stories = true)
    {
        var link = ctx.Link;
        ctx.Step("link Info: file, version, units", () =>
        {
            var info = link.Info();
            FieldContext.Expect(info.Version.Length > 0, "the program's version came back empty");
            FieldContext.Expect(info.Force.Length > 0 && info.Length.Length > 0, $"the units came back empty (force '{info.Force}', length '{info.Length}')");
            return info with { File = info.File.Replace('\\', '/').Split('/').Last() };
        });
        if (stories)
            ctx.Step("link Stories: names, elevations going up, heights", () =>
            {
                var rows = link.Stories();
                FieldContext.Expect(rows.Count >= 2, $"a base and at least one story were expected, there are {rows.Count}");
                FieldContext.Expect(rows.All(r => r.Name.Length > 0) && rows.Select(r => r.Name).Distinct().Count() == rows.Count, "a story has no name, or two have the same");
                for (var i = 1; i < rows.Count; i++)
                    FieldContext.Expect(rows[i].Elevation > rows[i - 1].Elevation && Math.Abs(rows[i].Elevation - rows[i - 1].Elevation - rows[i].Height) < 1e-4,
                        $"story {rows[i].Name} is at {rows[i].Elevation} with height {rows[i].Height}, above {rows[i - 1].Name} at {rows[i - 1].Elevation}");
                return rows;
            });
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ctx.Step("link FrameSections, AreaSections, Materials", () =>
        {
            var (frame, area, materials) = (link.FrameSections(), link.AreaSections(), link.Materials());
            FieldContext.Expect(frame.Count > 0 && area.Count > 0 && materials.Count > 0, $"{frame.Count} frame sections, {area.Count} area sections, {materials.Count} materials");
            FieldContext.Expect(frame.Concat(area).Concat(materials).All(n => !string.IsNullOrWhiteSpace(n)), "a section or material has an empty name");
            foreach (var s in frame.Concat(area)) sections.Add(s);
            return new { frame_sections = frame, area_sections = area, materials };
        });
        ctx.Step("link Frames: every frame's name, section, kind and ends", () =>
        {
            var rows = link.Frames();
            FieldContext.Expect(rows.Count > 0, "the model has no frames");
            FieldContext.Expect(rows.Select(r => r.Name).Distinct().Count() == rows.Count && rows.All(r => r.Name.Length > 0), "a frame has no name, or two have the same");
            var bad = rows.Where(r => r.Kind is not ("column" or "beam" or "brace") || r.Kind != EtabsWords.FrameKind(r.X1, r.Y1, r.Z1, r.X2, r.Y2, r.Z2)).Take(5).ToList();
            FieldContext.Expect(bad.Count == 0, "the kind does not fit the ends: " + string.Join("; ", bad.Select(r => $"{r.Name} {r.Kind} ({r.X1},{r.Y1},{r.Z1})-({r.X2},{r.Y2},{r.Z2})")));
            var unknown = rows.Where(r => r.Section.Length == 0 || (sections.Count > 0 && !sections.Contains(r.Section))).Take(5).ToList();
            FieldContext.Expect(unknown.Count == 0, "a frame's section is empty or not one of the model's sections: " + string.Join("; ", unknown.Select(r => $"{r.Name} '{r.Section}'")));
            if (stories)
            {
                var known = link.Stories().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var lost = rows.Where(r => !known.Contains(r.Story)).Take(5).ToList();
                FieldContext.Expect(lost.Count == 0, "a frame's story is empty or not one of the model's stories: " + string.Join("; ", lost.Select(r => $"{r.Name} '{r.Story}'")));
            }
            return new { count = rows.Count, columns = rows.Count(r => r.Kind == "column"), beams = rows.Count(r => r.Kind == "beam"), braces = rows.Count(r => r.Kind == "brace"), first = rows.Take(2) };
        });
        ctx.Step("link Areas: every area's name, section, kind and corners", () =>
        {
            var rows = link.Areas();
            FieldContext.Expect(rows.Count > 0, "the model has no areas");
            FieldContext.Expect(rows.Select(r => r.Name).Distinct().Count() == rows.Count && rows.All(r => r.Name.Length > 0), "an area has no name, or two have the same");
            var bad = rows.Where(r => r.Kind is not ("floor" or "wall") || r.Corners.Count < 3).Take(5).ToList();
            FieldContext.Expect(bad.Count == 0, "an area has no kind or fewer than three corners: " + string.Join("; ", bad.Select(r => $"{r.Name} '{r.Kind}' {r.Corners.Count} corners")));
            var unknown = rows.Where(r => r.Section.Length == 0 || (sections.Count > 0 && !sections.Contains(r.Section))).Take(5).ToList();
            FieldContext.Expect(unknown.Count == 0, "an area's section is empty or not one of the model's sections: " + string.Join("; ", unknown.Select(r => $"{r.Name} '{r.Section}'")));
            return new { count = rows.Count, floors = rows.Count(r => r.Kind == "floor"), walls = rows.Count(r => r.Kind == "wall"), first = rows.Take(1) };
        });
        ctx.Step("link Points and Restraint: every point's name and place, a support read back", () =>
        {
            var rows = link.Points();
            FieldContext.Expect(rows.Count > 0, "the model has no points");
            FieldContext.Expect(rows.Select(r => r.Name).Distinct().Count() == rows.Count && rows.All(r => r.Name.Length > 0), "a point has no name, or two have the same");
            FieldContext.Expect(rows.All(r => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Z)), "a point's coordinate is not a number");
            var lowest = rows.OrderBy(r => r.Z).First();
            var restraint = link.Restraint(lowest.Name);
            FieldContext.Expect(restraint.Length == 6, $"a restraint has {restraint.Length} values, not 6");
            return new { count = rows.Count, lowest = lowest, its_restraint = restraint };
        });
        ctx.Step("link LoadPatterns, LoadCases, Combos", () =>
        {
            var (patterns, cases, combos) = (link.LoadPatterns(), link.LoadCases(), link.Combos());
            FieldContext.Expect(patterns.Count > 0 && cases.Count > 0, $"{patterns.Count} load patterns and {cases.Count} load cases");
            var known = EtabsWords.PatternTypes.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var odd = patterns.Where(p => p.Name.Length == 0 || !known.Contains(p.Type)).ToList();
            FieldContext.Expect(odd.Count == 0, "a load pattern has no name or a type the tools do not know: " + string.Join("; ", odd.Select(p => $"{p.Name} '{p.Type}'")));
            return new { patterns, cases, combos };
        });
        ctx.Step("link Tables: the program's table list", () =>
        {
            var tables = link.Tables();
            FieldContext.Expect(tables.Count > 0, "the program listed no tables");
            FieldContext.Expect(tables.All(t => t.Key.Length > 0 && t.Name.Length > 0), "a table has no key or no name");
            return new { count = tables.Count, with_data = tables.Count(t => !t.Empty), first = tables.Take(8).Select(t => t.Name) };
        });
        ctx.Step("link Select, Selected, ClearSelection", () =>
        {
            var frame = link.Frames()[0].Name;
            link.ClearSelection();
            link.Select("frame", frame);
            var selected = link.Selected();
            link.ClearSelection();
            FieldContext.Expect(selected.Count == 1 && selected[0] == ("frame", frame), $"frame {frame} was selected; the selection read back as: {string.Join(", ", selected.Select(s => s.Kind + " " + s.Name))}");
            FieldContext.Expect(link.Selected().Count == 0, "the selection was not cleared");
            return $"frame {frame} selected, read back, cleared";
        });
    }

    /// <summary>
    /// After the result tools ran: one line that fails naming every check that answered "not available" with its
    /// reason, so a table name or field the program calls differently is listed under "Not working".
    /// </summary>
    public static void Verdicts(FieldContext ctx, string name, IEnumerable<(string Check, JsonNode? Answer)> answers) =>
        ctx.Step(name, () =>
        {
            var given = answers.Where(a => a.Answer is not null).ToList();
            var none = given.Where(a => a.Answer!["status"]?.GetValue<string>() == "not available")
                .Select(a => $"{a.Check}: {a.Answer!["note"]?.GetValue<string>() ?? "no reason given"}").ToList();
            FieldContext.Expect(none.Count == 0, $"{none.Count} of {given.Count} checks had nothing to read. " + string.Join(" | ", none));
            return string.Join(", ", given.Select(a => $"{a.Check}: {a.Answer!["status"]?.GetValue<string>() ?? "reported"}"));
        });

    /// <summary>Runs a tool that must say no in this program, and counts it as covered.</summary>
    public static void NotHere(FieldContext ctx, string tool, string saying) => ctx.Refused(tool, "{}", saying, $"tool {tool} says what to use instead");
}
