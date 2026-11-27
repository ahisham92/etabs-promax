using System.Text;
using CsiProMax.Model;
using static CsiProMax.E2k.E2kText;
using S = CsiProMax.E2k.E2kSections;

namespace CsiProMax.E2k;

/// <summary>Writes a <see cref="StructuralModel"/> as an ETABS text file (.e2k), which licensed ETABS opens with
/// File > Import > ETABS .e2k Text File.</summary>
public static class E2kWriter
{
    /// <summary>The time written on the first line; null (the default) means now. Tests set it to compare files.</summary>
    public static DateTime? SavedAt { get; set; }

    public static void WriteFile(StructuralModel model, string path) => File.WriteAllText(path, Write(model, path));

    public static string Write(StructuralModel model, string? path = null)
    {
        var text = new StringBuilder();
        // The first line as ETABS writes it: the file's path and when it was saved.
        text.Append("$ File ").Append(path is null ? "model.e2k" : Path.GetFullPath(path)).Append(" saved ")
            .Append((SavedAt ?? DateTime.Now).ToString("M/d/yyyy h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        // The log closes the file (its last line is END, and ETABS reads nothing after it): always written last.
        var log = model.Passthrough.Where(p => p.Heading == "LOG").ToList();
        // The Modeler's auto hinges go as ETABS writes them (it reads no other form).
        var passthrough = model.Passthrough.Except(log).Select(r => r.Heading == PropertyDefs.HingeHeading
            ? new RawSection { Heading = r.Heading, After = r.After, Lines = EtabsHinges.ToEtabs(model, r.Lines) } : r).ToList();
        // Tendons as ETABS of this version reads them (ETABS 23 drops the whole file for a tendon with no support points).
        passthrough = SlabDefs.ForEtabs(model, passthrough);
        // ETABS 23 cannot read a group's colour line ("Error reading line 348. Line Ignored. GROUP "CPM-G" COLOR "Blue"",
        // a box that waits for OK on every open, 2026-11-26): its own export writes GROUP "name" alone. The model keeps the colour.
        passthrough = passthrough.Select(r => r.Heading != GroupDefs.Heading ? r
            : new RawSection { Heading = r.Heading, After = r.After, Lines = r.Lines.Select(l => Tokenize(l) is { Count: >= 3 } t && t[0] == "GROUP" && t[2] == "COLOR" ? "  GROUP " + t[1] : l).ToList() }).ToList();
        EmitPassthroughAfter(text, passthrough, "");

        var anchors = new List<string>();
        foreach (var heading in S.Order)
        {
            // A section the source file had starts a new run: first put back the kept sections that followed the
            // previous run. A section it did not have joins the run it follows, ahead of those kept sections.
            if (model.SourceHeadings.Count == 0 || model.SourceHeadings.Contains(heading))
            {
                foreach (var anchor in anchors) EmitPassthroughAfter(text, passthrough, anchor);
                anchors.Clear();
            }
            anchors.Add(heading);
            var lines = SectionLines(model, heading).ToList();
            if (model.UnparsedLines.TryGetValue(heading, out var unparsed)) lines.AddRange(unparsed);
            if (lines.Count > 0 || AlwaysWritten.Contains(heading)) Section(text, heading, lines);
        }
        foreach (var anchor in anchors) EmitPassthroughAfter(text, passthrough, anchor);
        // Sections whose place in the file is not known (the section they followed is not in this model).
        foreach (var section in passthrough.ToList()) Section(text, section.Heading, section.Lines);
        foreach (var section in log) Section(text, section.Heading, section.Lines);

        text.Append("\r\n$ ").Append(S.End).Append("\r\n");
        return text.ToString();
    }

    /// <summary>Headings ETABS writes even when nothing is under them.</summary>
    private static readonly string[] AlwaysWritten = [S.PointLoads, S.FrameLoads, S.ShellLoads];

    private static void EmitPassthroughAfter(StringBuilder text, List<RawSection> left, string heading)
    {
        // A kept section may itself be followed by another kept section: follow the chain.
        for (var next = left.Find(s => s.After == heading); next is not null; next = left.Find(s => s.After == heading))
        {
            left.Remove(next);
            Section(text, next.Heading, next.Lines);
            EmitPassthroughAfter(text, left, next.Heading);
        }
    }

    private static void Section(StringBuilder text, string heading, IEnumerable<string> lines)
    {
        text.Append("\r\n$ ").Append(heading).Append("\r\n");
        foreach (var line in lines) text.Append(line).Append("\r\n");
    }

    private static IEnumerable<string> SectionLines(StructuralModel m, string heading)
    {
        switch (heading)
        {
            case S.Program:
                yield return Line(["PROGRAM", Quote("ETABS"), "VERSION", Quote(m.ProgramVersion)]);
                break;
            case S.Controls:
                yield return Line(["UNITS", Quote(m.Units.Force), Quote(m.Units.Length), Quote(m.Units.Temperature)]);
                if (m.Title.Length > 0) yield return Line(["TITLE2", Quote(m.Title)]);
                foreach (var line in m.Controls) yield return line;
                break;
            case S.Stories:
                for (var i = m.Stories.Count - 1; i >= 0; i--)
                {
                    var s = m.Stories[i];
                    yield return i == 0
                        ? Line(["STORY", Quote(s.Name), "ELEV", Number(s.Elevation)], s.Extra)
                        : Line(["STORY", Quote(s.Name), "HEIGHT", Number(s.Height)], s.Extra);
                }
                break;
            case S.Grids:
                // The grid system goes even with no grid lines: ETABS 23.2 writes it that way ("GRIDSYSTEM "G1"  TYPE "CARTESIAN"
                // BUBBLESIZE 1.25" alone) and rejects a file with no $ GRIDS ("Error importing from file. May not be a valid ETABS
                // 23.2.0 text file", Ahmed's real model in the Engine, 2026-11-27).
                yield return Line(["GRIDSYSTEM", Quote(m.GridSystem.Length > 0 ? m.GridSystem : "G1")],
                    m.GridSystemExtra.Count > 0 ? m.GridSystemExtra : ["TYPE", Quote("CARTESIAN"), "BUBBLESIZE", "1.25"]);
                foreach (var g in m.Grids)
                    yield return Line(["GRID", Quote(m.GridSystem), "LABEL", Quote(g.Label), "DIR", Quote(g.Direction), "COORD", Number(g.Coordinate)], g.Extra);
                break;
            case S.Diaphragms:
                foreach (var d in m.Diaphragms) yield return Line(["DIAPHRAGM", Quote(d.Name), "TYPE", d.Type]);
                break;
            case S.Materials:
                foreach (var line in DefinitionLines("MATERIAL", m.Materials)) yield return line;
                break;
            case S.FrameSections:
                foreach (var line in DefinitionLines("FRAMESECTION", m.FrameSections)) yield return line;
                break;
            case S.SlabProperties:
                foreach (var line in DefinitionLines("SHELLPROP", m.ShellSections.Where(s => !s.IsWall && !IsDeck(s)))) yield return line;
                break;
            case S.DeckProperties:
                foreach (var line in DefinitionLines("SHELLPROP", m.ShellSections.Where(IsDeck))) yield return line;
                break;
            case S.WallProperties:
                foreach (var line in DefinitionLines("SHELLPROP", m.ShellSections.Where(s => s.IsWall))) yield return line;
                break;
            case S.Points:
                foreach (var p in m.Points)
                    yield return p.Below == 0
                        ? Line(["POINT", Quote(p.Name), Number(p.X), Number(p.Y)])
                        : Line(["POINT", Quote(p.Name), Number(p.X), Number(p.Y), Number(p.Below)]);
                break;
            case S.Lines:
                foreach (var l in m.Lines)
                    yield return Line(["LINE", Quote(l.Label), l.Kind.ToString().ToUpperInvariant(), Quote(l.PointI), Quote(l.PointJ), l.StoriesBelowI.ToString()]);
                break;
            case S.Areas:
                foreach (var a in m.Areas)
                    yield return Line(
                        ["AREA", Quote(a.Label), a.Kind switch { AreaKind.Wall => "PANEL", AreaKind.Ramp => "RAMP", _ => "FLOOR" }, a.PointNames.Count.ToString()],
                        a.PointNames.Select(Quote), a.StoriesBelow.Select(o => o.ToString()));
                break;
            case S.PointAssigns:
                foreach (var a in m.PointAssigns)
                    foreach (var line in AssignLines(["POINTASSIGN", Quote(a.Point), Quote(a.Story)],
                                 [.. Pair("RESTRAINT", a.Restraint), .. Pair("DIAPH", a.Diaphragm)], a.Extra))
                        yield return line;
                break;
            case S.LineAssigns:
                foreach (var a in m.LineAssigns)
                    foreach (var line in AssignLines(["LINEASSIGN", Quote(a.Label), Quote(a.Story)], [.. Pair("SECTION", a.Section)], EtabsModifiers.ToEtabs(a.Extra, EtabsModifiers.Frame)))
                        yield return line;
                break;
            case S.AreaAssigns:
                foreach (var a in m.AreaAssigns)
                    foreach (var line in AssignLines(["AREAASSIGN", Quote(a.Label), Quote(a.Story)],
                                 [.. Pair("SECTION", a.Section), .. Pair("DIAPH", a.Diaphragm)], EtabsModifiers.ToEtabs(a.Extra, EtabsModifiers.Area)))
                        yield return line;
                break;
            case S.LoadPatterns:
                foreach (var p in m.LoadPatterns)
                    yield return Line(["LOADPATTERN", Quote(p.Name), "TYPE", Quote(p.Type), "SELFWEIGHT", Number(p.SelfWeight)], p.Extra);
                break;
            case S.PointLoads:
                foreach (var l in m.PointLoads) yield return Line(["POINTLOAD", Quote(l.Object), Quote(l.Story)], l.Tokens);
                break;
            case S.FrameLoads:
                foreach (var l in m.LineLoads) yield return Line(["LINELOAD", Quote(l.Object), Quote(l.Story)], l.Tokens);
                break;
            case S.ShellLoads:
                foreach (var l in m.AreaLoads) yield return Line(["AREALOAD", Quote(l.Object), Quote(l.Story)], l.Tokens);
                break;
            case S.LoadCases:
                foreach (var c in m.LoadCases)
                    foreach (var line in c.Lines) yield return Line(["LOADCASE", Quote(c.Name)], line);
                break;
            case S.Combos:
                foreach (var c in m.Combos)
                    foreach (var line in c.Lines) yield return Line(["COMBO", Quote(c.Name)], line);
                break;
        }
    }

    private static bool IsDeck(ShellSection s) => string.Equals(s.Get("PROPTYPE"), "Deck", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Pair(string key, string? value) => value is null ? [] : [key, Quote(value)];

    private static IEnumerable<string> DefinitionLines(string keyword, IEnumerable<Definition> items)
    {
        foreach (var item in items)
            foreach (var line in item.Lines)
                yield return Line([keyword, Quote(item.Name)], line);
    }

    private static IEnumerable<string> AssignLines(string[] head, List<string> named, List<List<string>> extra)
    {
        if (named.Count == 0 && extra.Count == 0) yield break;
        yield return Line(head, named, extra.Count > 0 ? extra[0] : []);
        foreach (var more in extra.Skip(1)) yield return Line(head, more);
    }
}
