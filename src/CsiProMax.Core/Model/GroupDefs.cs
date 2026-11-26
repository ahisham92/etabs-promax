using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Model;

/// <summary>A group (Define > Groups): its color and its objects, each a POINT, LINE or AREA with its label and story.</summary>
public sealed record Group(string Name, string Color, List<(string Type, string Label, string Story)> Members);

/// <summary>The model's groups, as the .e2k keeps them under "$ GROUPS": GROUP "name" COLOR "c", and GROUP "name" LINE "B1" "Story1".</summary>
public static class GroupDefs
{
    public const string Heading = "GROUPS";

    public static List<Group> Groups(StructuralModel m)
    {
        var list = new List<Group>();
        foreach (var line in m.Passthrough.Where(r => r.Heading == Heading).SelectMany(r => r.Lines))
        {
            var t = Tokenize(line);
            if (t.Count < 2 || t[0] != "GROUP") continue;
            var name = Unquote(t[1]);
            var at = list.FindIndex(x => x.Name == name);
            if (at < 0) { list.Add(new Group(name, "Red", [])); at = list.Count - 1; }
            if (t.Count >= 4 && t[2] == "COLOR") list[at] = list[at] with { Color = Unquote(t[3]) };
            else if (t.Count >= 5 && t[2] is "POINT" or "LINE" or "AREA") list[at].Members.Add((t[2], Unquote(t[3]), Unquote(t[4])));
        }
        return list;
    }
}
