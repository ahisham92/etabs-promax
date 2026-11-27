using CsiProMax.E2k;

namespace CsiProMax.Model;

/// <summary>
/// ETABS's shell uniform load sets (Define > Shell Uniform Load Sets; Assign > Shell Loads > Uniform Load Sets): a set
/// is a list of load patterns with a pressure each, and a slab takes the set by name, as
/// <c>AREALOAD "F248" "LVL 03-TOC" TYPE "UNIFLOADSET" "Offices"</c> under the sets'
/// <c>SHELLUNIFORMLOADSET "Offices" LOADPAT "Live" VALUE 3.5</c>. Every slab load of Ahmed's real model was a set
/// (2026-11-27: the Engine showed its slabs with no load, the check said the model had no mass). The file keeps
/// them word for word; what reads loads sees each set as the plain gravity loads it stands for.
/// </summary>
public static class LoadSets
{
    public const string Heading = "SHELL UNIFORM LOAD SETS";

    /// <summary>The sets by name: each pattern with its pressure, in the file's order.</summary>
    public static Dictionary<string, List<(string Pattern, double Value)>> Sets(StructuralModel m)
    {
        var sets = new Dictionary<string, List<(string, double)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in m.Passthrough.Where(p => p.Heading.Trim().Equals(Heading, StringComparison.OrdinalIgnoreCase)))
            foreach (var t in raw.Lines.Select(E2kText.Tokenize))
            {
                if (t.Count < 2 || !t[0].Equals("SHELLUNIFORMLOADSET", StringComparison.OrdinalIgnoreCase)) continue;
                var name = E2kText.Unquote(t[1]);
                if (!sets.TryGetValue(name, out var list)) sets[name] = list = [];
                if (E2kText.Value(t, "LOADPAT") is { } lp && E2kText.TryNumber(E2kText.Value(t, "VALUE"), out var v)) list.Add((E2kText.Unquote(lp), v));
            }
        return sets;
    }

    /// <summary>The set a load names, or null when it is not a load set.</summary>
    public static string? SetOf(ObjectLoad load)
    {
        var t = load.Tokens;
        var at = t.FindIndex(x => x.Equals("TYPE", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= t.Count || !E2kText.Unquote(t[at + 1]).Equals("UNIFLOADSET", StringComparison.OrdinalIgnoreCase)) return null;
        // The set's name follows the type, bare; a SET keyword before it is taken too.
        var next = at + 2 < t.Count && t[at + 2].Equals("SET", StringComparison.OrdinalIgnoreCase) ? at + 3 : at + 2;
        return next < t.Count ? E2kText.Unquote(t[next]) : null;
    }

    /// <summary>The area loads with each set given as the plain gravity loads it stands for (one per pattern).</summary>
    public static List<AreaLoad> Expanded(StructuralModel m) => Expanded(m, m.AreaLoads);

    public static List<AreaLoad> Expanded(StructuralModel m, IEnumerable<AreaLoad> loads)
    {
        Dictionary<string, List<(string Pattern, double Value)>>? sets = null;
        var all = new List<AreaLoad>();
        foreach (var l in loads)
        {
            if (SetOf(l) is not { } name) { all.Add(l); continue; }
            sets ??= Sets(m);
            if (!sets.TryGetValue(name, out var items)) continue;
            all.AddRange(items.Select(i => new AreaLoad
            {
                Object = l.Object, Story = l.Story,
                Tokens = ["TYPE", E2kText.Quote("UNIFF"), "DIR", E2kText.Quote("GRAV"), "LC", E2kText.Quote(i.Pattern), "FVAL", E2kText.Number(i.Value)],
            }));
        }
        return all;
    }
}
