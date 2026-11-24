using EtabsProMax.Model;

namespace EtabsProMax.Checks;

/// <summary>One line of the take-off: a section, how many members use it, and their total length, area and volume
/// in the model's units (null where the section's size is not known, e.g. a steel shape from a catalogue).</summary>
public sealed record QuantityRow(string Kind, string Section, string Material, int Count, double Length, double Area, double? Volume);

public static class Quantities
{
    /// <summary>Gross quantities by member kind and section, centre line to centre line (no deduction where members cross).</summary>
    public static List<QuantityRow> Run(StructuralModel m)
    {
        var rows = new List<QuantityRow>();
        var frameSections = m.FrameSections.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
        var shellSections = m.ShellSections.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());

        foreach (var g in Physical.Frames(m).GroupBy(x => (x.Kind, Section: x.Section ?? "")).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Section))
        {
            frameSections.TryGetValue(g.Key.Section, out var section);
            var length = g.Sum(x => x.Length);
            rows.Add(new QuantityRow(g.Key.Kind.ToString(), g.Key.Section, section?.Material ?? "", g.Count(), length, 0,
                section?.Area is { } area ? area * length : null));
        }
        foreach (var g in Physical.Areas(m).GroupBy(x => (x.Kind, Section: x.Section ?? "")).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Section))
        {
            shellSections.TryGetValue(g.Key.Section, out var section);
            var area = g.Sum(x => x.Area);
            rows.Add(new QuantityRow(g.Key.Kind.ToString(), g.Key.Section, section?.Material ?? "", g.Count(), 0, area,
                section?.Thickness is { } thickness ? thickness * area : null));
        }
        return rows;
    }
}
