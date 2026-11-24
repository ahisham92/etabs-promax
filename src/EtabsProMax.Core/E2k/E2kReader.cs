using EtabsProMax.Model;
using static EtabsProMax.E2k.E2kText;
using S = EtabsProMax.E2k.E2kSections;

namespace EtabsProMax.E2k;

/// <summary>Reads an ETABS text file (.e2k or .$et, File > Export > ETABS .e2k Text File) into a <see cref="StructuralModel"/>.
/// What this version doesn't understand is kept as read, so writing the model back loses nothing.</summary>
public static class E2kReader
{
    public static StructuralModel ReadFile(string path) => Read(File.ReadAllText(path));

    public static StructuralModel Read(string text)
    {
        var model = new StructuralModel { GridSystemExtra = [] };
        var storiesTopFirst = new List<Story>();
        var heading = "";
        var previous = "";
        RawSection? raw = null;
        var sawHeading = false;

        foreach (var fullLine in text.Split('\n'))
        {
            var line = fullLine.TrimEnd('\r', ' ', '\t');
            if (line.TrimStart().StartsWith('$'))
            {
                var name = line.TrimStart()[1..].Trim();
                if (name.StartsWith("File ", StringComparison.OrdinalIgnoreCase) && !sawHeading) continue;
                sawHeading = true;
                if (heading.Length > 0) previous = heading;
                heading = S.Canonical(name);
                raw = null;
                if (heading == S.End) break;
                if (S.IsKnown(heading)) model.SourceHeadings.Add(heading);
                else
                {
                    raw = new RawSection { Heading = heading, After = previous };
                    model.Passthrough.Add(raw);
                }
                continue;
            }
            if (line.Trim().Length == 0) continue;
            if (raw is not null) { raw.Lines.Add(line); continue; }
            if (heading.Length == 0) continue;
            if (!ReadLine(model, storiesTopFirst, line))
            {
                if (!model.UnparsedLines.TryGetValue(heading, out var list)) model.UnparsedLines[heading] = list = [];
                list.Add(line);
            }
        }

        if (!sawHeading) throw new E2kFormatException("This is not an ETABS text file: it has no '$' section headings.");
        storiesTopFirst.Reverse();
        model.Stories = storiesTopFirst;
        if (model.Stories.Count > 0 && model.Stories[0].Height != 0)
            model.Stories.Insert(0, new Story { Name = "Base" });
        return model;
    }

    private static bool ReadLine(StructuralModel model, List<Story> stories, string line)
    {
        var t = Tokenize(line);
        if (t.Count == 0) return true;
        var rest = t.Skip(1).ToList();
        string Name(int index) => index < rest.Count ? Unquote(rest[index]) : throw new E2kFormatException($"A name is missing in: {line.Trim()}");

        switch (t[0])
        {
            case "PROGRAM":
                model.ProgramVersion = Value(rest, "VERSION") ?? model.ProgramVersion;
                return true;
            case "UNITS" when rest.Count >= 2:
                model.Units = new Units
                {
                    Force = Unquote(rest[0]), Length = Unquote(rest[1]),
                    Temperature = rest.Count > 2 ? Unquote(rest[2]) : "C",
                };
                return true;
            case "TITLE2":   // the name of the model; TITLE1 (the company) is kept as read
                model.Title = Name(0);
                return true;
            case "STORY":
            {
                var extra = rest.Skip(1).ToList();
                var height = Take(extra, "HEIGHT");
                var elevation = Take(extra, "ELEV");
                stories.Add(new Story
                {
                    Name = Name(0),
                    Height = height is null ? 0 : ToNumber(height, line),
                    Elevation = elevation is null ? 0 : ToNumber(elevation, line),
                    Extra = extra,
                });
                return true;
            }
            case "GRIDSYSTEM":
                model.GridSystem = Name(0);
                model.GridSystemExtra = rest.Skip(1).ToList();
                return true;
            case "GRID":
            {
                var extra = rest.Skip(1).ToList();
                var label = Take(extra, "LABEL"); var dir = Take(extra, "DIR"); var coord = Take(extra, "COORD");
                if (label is null || dir is null || coord is null) return false;
                model.Grids.Add(new GridLine { Label = label, Direction = dir, Coordinate = ToNumber(coord, line), Extra = extra });
                return true;
            }
            case "DIAPHRAGM":
                model.Diaphragms.Add(new Diaphragm { Name = Name(0), Type = Value(rest, "TYPE") ?? "RIGID" });
                return true;
            case "MATERIAL":
                Definition(model.Materials, Name(0)).Lines.Add(rest.Skip(1).ToList());
                return true;
            case "FRAMESECTION":
                Definition(model.FrameSections, Name(0)).Lines.Add(rest.Skip(1).ToList());
                return true;
            case "SHELLPROP":
                Definition(model.ShellSections, Name(0)).Lines.Add(rest.Skip(1).ToList());
                return true;
            case "POINT" when rest.Count >= 3:
                model.Points.Add(new PlanPoint
                {
                    Name = Name(0), X = ToNumber(rest[1], line), Y = ToNumber(rest[2], line),
                    Below = rest.Count > 3 ? ToNumber(rest[3], line) : 0,
                });
                return true;
            case "LINE" when rest.Count >= 5:
            {
                if (!Enum.TryParse<LineKind>(rest[1], true, out var kind)) return false;
                model.Lines.Add(new LineObject
                {
                    Label = Name(0), Kind = kind, PointI = Name(2), PointJ = Name(3), StoriesBelowI = (int)ToNumber(rest[4], line),
                });
                return true;
            }
            case "AREA" when rest.Count >= 3:
            {
                AreaKind kind;
                switch (rest[1].ToUpperInvariant())
                {
                    case "FLOOR": kind = AreaKind.Floor; break;
                    case "PANEL": kind = AreaKind.Wall; break;
                    case "RAMP": kind = AreaKind.Ramp; break;
                    default: return false;
                }
                var count = (int)ToNumber(rest[2], line);
                if (rest.Count < 3 + 2 * count) throw new E2kFormatException($"An area needs {count} points and {count} story offsets: {line.Trim()}");
                model.Areas.Add(new AreaObject
                {
                    Label = Name(0), Kind = kind,
                    PointNames = rest.Skip(3).Take(count).Select(Unquote).ToList(),
                    StoriesBelow = rest.Skip(3 + count).Take(count).Select(o => (int)ToNumber(o, line)).ToList(),
                });
                return true;
            }
            case "POINTASSIGN" when rest.Count >= 2:
            {
                var extra = rest.Skip(2).ToList();
                var assign = model.PointAssigns.Find(a => a.Point == Name(0) && a.Story == Name(1));
                if (assign is null) model.PointAssigns.Add(assign = new PointAssign { Point = Name(0), Story = Name(1) });
                assign.Restraint = Take(extra, "RESTRAINT") ?? assign.Restraint;
                assign.Diaphragm = Take(extra, "DIAPH") ?? assign.Diaphragm;
                if (extra.Count > 0) assign.Extra.Add(extra);
                return true;
            }
            case "LINEASSIGN" when rest.Count >= 2:
            {
                var extra = rest.Skip(2).ToList();
                var assign = model.LineAssigns.Find(a => a.Label == Name(0) && a.Story == Name(1));
                if (assign is null) model.LineAssigns.Add(assign = new LineAssign { Label = Name(0), Story = Name(1) });
                assign.Section = Take(extra, "SECTION") ?? assign.Section;
                if (extra.Count > 0) assign.Extra.Add(extra);
                return true;
            }
            case "AREAASSIGN" when rest.Count >= 2:
            {
                var extra = rest.Skip(2).ToList();
                var assign = model.AreaAssigns.Find(a => a.Label == Name(0) && a.Story == Name(1));
                if (assign is null) model.AreaAssigns.Add(assign = new AreaAssign { Label = Name(0), Story = Name(1) });
                assign.Section = Take(extra, "SECTION") ?? assign.Section;
                assign.Diaphragm = Take(extra, "DIAPH") ?? assign.Diaphragm;
                if (extra.Count > 0) assign.Extra.Add(extra);
                return true;
            }
            case "LOADPATTERN":
            {
                var extra = rest.Skip(1).ToList();
                var type = Take(extra, "TYPE"); var self = Take(extra, "SELFWEIGHT");
                model.LoadPatterns.Add(new LoadPattern
                {
                    Name = Name(0), Type = type ?? "Other", SelfWeight = self is null ? 0 : ToNumber(self, line), Extra = extra,
                });
                return true;
            }
            case "POINTLOAD" when rest.Count >= 2:
                model.PointLoads.Add(new PointLoad { Object = Name(0), Story = Name(1), Tokens = rest.Skip(2).ToList() });
                return true;
            case "LINELOAD" when rest.Count >= 2:
                model.LineLoads.Add(new LineLoad { Object = Name(0), Story = Name(1), Tokens = rest.Skip(2).ToList() });
                return true;
            case "AREALOAD" when rest.Count >= 2:
                model.AreaLoads.Add(new AreaLoad { Object = Name(0), Story = Name(1), Tokens = rest.Skip(2).ToList() });
                return true;
            case "LOADCASE":
            {
                var item = model.LoadCases.Find(c => c.Name == Name(0));
                if (item is null) model.LoadCases.Add(item = new LoadCase { Name = Name(0) });
                item.Lines.Add(rest.Skip(1).ToList());
                return true;
            }
            case "COMBO":
            {
                var item = model.Combos.Find(c => c.Name == Name(0));
                if (item is null) model.Combos.Add(item = new LoadCombo { Name = Name(0) });
                item.Lines.Add(rest.Skip(1).ToList());
                return true;
            }
            default:
                return false;
        }
    }

    private static T Definition<T>(List<T> list, string name) where T : Definition, new()
    {
        var item = list.Find(d => d.Name == name);
        if (item is null) list.Add(item = new T { Name = name });
        return item;
    }
}
