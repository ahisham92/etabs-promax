using CsiProMax.E2k;

namespace CsiProMax.Model;

/// <summary>
/// ETABS 23.2's Section Designer sections as its .e2k has them, under "$ SECTION DESIGNER SECTIONS": a header line
/// (SDSECTION "SD1" TYPE "FRAME" DESIGNTYPE ... NUMSHAPES n ANGLE a), then each shape's lines (SHAPE i MATERIAL ...
/// SHAPETYPE ..., its EDGE, CORNER and TIEBARSIZE lines). The lines are kept as ETABS wrote them, so a section the
/// Engine does not change goes back exactly; the Section Designer window rewrites a section's lines as a whole.
/// </summary>
public static class SectionDesigner
{
    public const string Heading = "SECTION DESIGNER SECTIONS";

    static bool Of(string line, string name) => E2kText.Tokenize(line) is { Count: >= 2 } t && t[0] == "SDSECTION" && E2kText.Unquote(t[1]) == name;

    /// <summary>The section's lines without SDSECTION and its name, each as its tokens.</summary>
    public static List<List<string>> Lines(StructuralModel m, string name) =>
        m.Passthrough.Find(s => s.Heading == Heading)?.Lines.Where(l => Of(l, name)).Select(l => E2kText.Tokenize(l).Skip(2).ToList()).ToList() ?? [];

    public static bool Has(StructuralModel m, string name) => Lines(m, name).Count > 0;

    /// <summary>Puts the section's lines in place of the ones it had (none takes them away), where it had them.</summary>
    public static void Set(StructuralModel m, string name, IReadOnlyList<IReadOnlyList<string>> lines, string? was = null)
    {
        foreach (var l in lines)
        {
            if (l.Count == 0) throw new ArgumentException("A Section Designer line is empty.");
            for (var i = 0; i + 1 < l.Count; i++)
                if (l[i] is "MATERIAL" or "REBARMATERIAL" && !m.Materials.Exists(x => x.Name == E2kText.Unquote(l[i + 1])))
                    throw new ArgumentException($"There is no material '{E2kText.Unquote(l[i + 1])}'.");
        }
        var section = m.Passthrough.Find(s => s.Heading == Heading);
        if (section is null)
        {
            if (lines.Count == 0) return;
            section = new RawSection { Heading = Heading, After = "FRAME SECTIONS" };
            m.Passthrough.Add(section);
        }
        var at = section.Lines.FindIndex(l => Of(l, was ?? name));
        section.Lines.RemoveAll(l => Of(l, was ?? name) || Of(l, name));
        if (at < 0 || at > section.Lines.Count) at = section.Lines.Count;
        section.Lines.InsertRange(at, lines.Select(l => "  SDSECTION " + E2kText.Quote(name) + "  " + string.Join("  ", l)));
        if (section.Lines.Count == 0) m.Passthrough.Remove(section);
    }
}
