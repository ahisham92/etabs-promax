namespace CsiProMax.Model;

/// <summary>Analyze > Set Load Cases to Run (ETABS 23.2, SAP2000 26.3, SAFE): which load cases the next run leaves
/// out. Every case runs unless it is listed here. Kept in the .cpm ("model|" is no program's table); SAP2000's file
/// gets it as "Load Case Definitions" RunCase=No, and a live run sets each program's own run flags before it starts.</summary>
public static class RunCases
{
    public const string NotRunKept = "model|CASES NOT RUN";

    /// <summary>Every load case of the model with whether it runs, in the model's order (moving cases included).</summary>
    public static List<(string Name, string Type, bool Run)> List(StructuralModel m)
    {
        var off = NotRun(m);
        var all = m.LoadCases.Select(c => (c.Name, Type: E2k.E2kText.Unquote(c.Lines.Select(l => E2k.E2kText.Value(l, "TYPE")).FirstOrDefault(v => v is not null) ?? "")))
            .Concat((m.Extras?.MovingCases ?? []).Select(c => (c.Name, Type: "Moving Load")));
        return all.Select(c => (c.Name, c.Type, !off.Contains(c.Name))).ToList();
    }

    /// <summary>The cases set not to run that the model still has.</summary>
    public static HashSet<string> NotRun(StructuralModel m)
    {
        var names = new HashSet<string>(m.LoadCases.Select(c => c.Name).Concat((m.Extras?.MovingCases ?? []).Select(c => c.Name)), StringComparer.Ordinal);
        return m.Kept is not null && m.Kept.TryGetValue(NotRunKept, out var rows)
            ? rows.Select(r => r.GetValueOrDefault("Case") ?? "").Where(names.Contains).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Whether a case runs.</summary>
    public static bool Runs(StructuralModel m, string name) => !NotRun(m).Contains(name);

    /// <summary>Sets the cases not to run (the rest run). Throws for a name the model has no case of.</summary>
    public static void SetNotRun(StructuralModel m, IEnumerable<string> names)
    {
        var have = List(m).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var list = names.Distinct(StringComparer.Ordinal).ToList();
        if (list.FirstOrDefault(n => !have.Contains(n)) is { } bad) throw new ArgumentException($"The model has no load case {bad}.");
        m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) m.Kept.Remove(NotRunKept);
        else m.Kept[NotRunKept] = list.Select(n => new Dictionary<string, string> { ["Case"] = n }).ToList();
    }

    /// <summary>Sets one case to run or not.</summary>
    public static void Set(StructuralModel m, string name, bool run)
    {
        var off = NotRun(m);
        if (run) off.Remove(name); else off.Add(name);
        SetNotRun(m, off);
    }
}
