using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Lateral;

/// <summary>
/// The model's mass source, as ETABS writes it under "$ MASS SOURCE": whether the elements' own mass and the added
/// mass count, and the load patterns that count as mass with their factors. With no section, ETABS's default: the
/// elements and the added mass, no loads.
/// </summary>
public sealed record MassSource(string Name, bool Elements, bool AddedMass, IReadOnlyList<(string Pattern, double Factor)> Loads)
{
    public const string Heading = "MASS SOURCE";

    public static MassSource Of(StructuralModel m)
    {
        var lines = m.Passthrough.Find(s => s.Heading == Heading)?.Lines ?? [];
        string? name = null;
        bool elements = true, added = true, loads = false;
        var list = new List<(string, double)>();
        foreach (var raw in lines)
        {
            var t = E2kText.Tokenize(raw);
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
            if (E2kText.Value(rest, "LOADPAT") is { } p && E2kText.TryNumber(E2kText.Value(rest, "SF"), out var sf)) list.Add((p, sf));
        }
        return new MassSource(name ?? "MsSrc1", elements, added, loads ? list : []);
    }

    private static bool Yes(string v) => v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("True", StringComparison.OrdinalIgnoreCase);

    private static string YesNo(bool v) => E2kText.Quote(v ? "Yes" : "No");

    /// <summary>Writes this as the model's only mass source.</summary>
    public void WriteTo(StructuralModel m)
    {
        var lines = new List<string>
        {
            E2kText.Line(["MASSSOURCE", E2kText.Quote(Name), "INCLUDEELEMENTS", YesNo(Elements), "INCLUDEADDEDMASS", YesNo(AddedMass), "INCLUDELOADS", YesNo(Loads.Count > 0),
                "INCLUDEMOVE", YesNo(false), "INCLUDELATERALMASS", YesNo(true), "INCLUDEVERTICALMASS", YesNo(false), "LUMPATSTORIES", YesNo(true), "ISDEFAULT", YesNo(true)]),
        };
        foreach (var (pattern, factor) in Loads)
            lines.Add(E2kText.Line(["MASSSOURCE", E2kText.Quote(Name), "LOADPAT", E2kText.Quote(pattern), "SF", E2kText.Number(factor)]));
        var section = m.Passthrough.Find(s => s.Heading == Heading);
        if (section is null) m.Passthrough.Add(new RawSection { Heading = Heading, After = "ANALYSIS OPTIONS", Lines = lines });
        else section.Lines = lines;
        if (!m.Passthrough.Exists(s => s.Heading == "ANALYSIS OPTIONS") && section is null) m.Passthrough[^1].After = "SHELL OBJECT LOADS";
    }
}
