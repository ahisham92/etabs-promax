using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Lateral;

/// <summary>
/// The model's mass source, as ETABS writes it under "$ MASS SOURCE": whether the elements' own mass and the added
/// mass count, and the load patterns that count as mass with their factors. With no section, ETABS's default: the
/// elements and the added mass, no loads.
/// </summary>
public sealed record MassSource(string Name, bool Elements, bool AddedMass, IReadOnlyList<(string Pattern, double Factor)> Loads,
    bool Lateral = true, bool Vertical = false, bool Lump = true, bool Move = false, double MoveX = 0, double MoveY = 0)
{
    public const string Heading = "MASS SOURCE";

    /// <summary>ETABS 23's table of the mass source, where the move ratios (Adjust Diaphragm Lateral Mass to Move Mass
    /// Centroid by) are kept: the .e2k has a keyword for the option (INCLUDEMOVE) but none for the ratios.</summary>
    public const string Table = "Mass Source Definition";

    public static MassSource Of(StructuralModel m)
    {
        var lines = m.Passthrough.Find(s => s.Heading == Heading)?.Lines ?? [];
        string? name = null;
        bool elements = true, added = true, loads = false, lateral = true, vertical = false, lump = true, move = false;
        var list = new List<(string, double)>();
        foreach (var raw in lines)
        {
            var t = E2kText.Tokenize(raw);
            // ETABS 23 writes each pattern on a line of its own, by position: MASSSOURCELOAD "MsSrc1" "Dead" 1
            // (Ahmed's real model, 2026-11-27); taken once the source's own line has named the source.
            if (t.Count >= 3 && t[0].Equals("MASSSOURCELOAD", StringComparison.OrdinalIgnoreCase))
            {
                if (E2kText.Unquote(t[1]) == (name ?? E2kText.Unquote(t[1]))) list.Add((E2kText.Unquote(t[2]), t.Count > 3 && E2kText.TryNumber(t[3], out var f) ? f : 1));
                continue;
            }
            if (t.Count < 2 || !t[0].Equals("MASSSOURCE", StringComparison.OrdinalIgnoreCase)) continue;
            var here = E2kText.Unquote(t[1]);
            // The default source is the one that counts; the first one when none says so.
            if (name is not null && here != name) continue;
            var rest = t.Skip(2).ToList();
            if (E2kText.Value(rest, "ISDEFAULT") is { } d && d.Equals("No", StringComparison.OrdinalIgnoreCase) && lines.Any(l => l.Contains("ISDEFAULT \"Yes\"", StringComparison.OrdinalIgnoreCase))) continue;
            name ??= here;
            if (E2kText.Value(rest, "INCLUDEELEMENTS") is { } e) elements = Yes(e);
            if (E2kText.Value(rest, "INCLUDEADDEDMASS") is { } a) added = Yes(a);
            if (E2kText.Value(rest, "INCLUDELOADS") is { } l) loads = Yes(l);
            if (E2kText.Value(rest, "INCLUDELATERALMASS") is { } lm) lateral = Yes(lm);
            if (E2kText.Value(rest, "INCLUDEVERTICALMASS") is { } vm) vertical = Yes(vm);
            if (E2kText.Value(rest, "LUMPATSTORIES") is { } ls) lump = Yes(ls);
            if (E2kText.Value(rest, "INCLUDEMOVE") is { } mv) move = Yes(mv);
            if (E2kText.Value(rest, "LOADPAT") is { } p && E2kText.TryNumber(E2kText.Value(rest, "SF"), out var sf)) list.Add((p, sf));
        }
        name ??= "MsSrc1";
        double rx = 0, ry = 0;
        if (move && m.DefinitionTables?.GetValueOrDefault(Table)?.Find(r => r.GetValueOrDefault("Name") == name) is { } row)
        {
            E2kText.TryNumber(row.GetValueOrDefault("RatioX"), out rx);
            E2kText.TryNumber(row.GetValueOrDefault("RatioY"), out ry);
        }
        return new MassSource(name, elements, added, loads ? list : [], lateral, vertical, lump, move, rx, ry);
    }

    private static bool Yes(string v) => v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("True", StringComparison.OrdinalIgnoreCase);

    private static string YesNo(bool v) => E2kText.Quote(v ? "Yes" : "No");

    /// <summary>Writes this as the model's only mass source.</summary>
    public void WriteTo(StructuralModel m)
    {
        var lines = new List<string>
        {
            E2kText.Line(["MASSSOURCE", E2kText.Quote(Name), "INCLUDEELEMENTS", YesNo(Elements), "INCLUDEADDEDMASS", YesNo(AddedMass), "INCLUDELOADS", YesNo(Loads.Count > 0),
                "INCLUDEMOVE", YesNo(Move), "INCLUDELATERALMASS", YesNo(Lateral), "INCLUDEVERTICALMASS", YesNo(Vertical), "LUMPATSTORIES", YesNo(Lump), "ISDEFAULT", YesNo(true)]),
        };
        foreach (var (pattern, factor) in Loads)
            lines.Add(E2kText.Line(["MASSSOURCELOAD", E2kText.Quote(Name), E2kText.Quote(pattern), E2kText.Number(factor)]));
        var section = m.Passthrough.Find(s => s.Heading == Heading);
        if (section is null) m.Passthrough.Add(new RawSection { Heading = Heading, After = "ANALYSIS OPTIONS", Lines = lines });
        else section.Lines = lines;
        if (!m.Passthrough.Exists(s => s.Heading == "ANALYSIS OPTIONS") && section is null) m.Passthrough[^1].After = "SHELL OBJECT LOADS";
        // The move ratios, by ETABS's table and field names (put into ETABS through that table).
        m.DefinitionTables?.Remove(Table);
        if (Move)
        {
            m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
            // One row per load pattern, as the program's table has them, so putting it back loses none.
            m.DefinitionTables[Table] = (Loads.Count > 0 ? Loads.Select(l => l.Pattern) : new[] { "" }).Select(p => new Dictionary<string, string>
                { ["Name"] = Name, ["LoadPattern"] = p, ["MoveMass"] = "Yes", ["RatioX"] = E2kText.Number(MoveX), ["RatioY"] = E2kText.Number(MoveY) }).ToList();
        }
        if (m.DefinitionTables is { Count: 0 }) m.DefinitionTables = null;
    }
}
