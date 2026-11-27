using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Checks.Stability;

/// <summary>
/// Small models broken on purpose, one fault each, with the check that must find it. The unit tests and the field
/// test run the checker on each, so every kind of fault it promises to find is shown to be found, and the sound model
/// is shown to give no errors. Also offered in the Modeler as examples (File > New from example... "broken").
/// </summary>
public static class BrokenModels
{
    /// <param name="Check">The check that must report it (StabilityCheck.Checks). "" for the sound model, which must report no error.</param>
    /// <param name="Object">A label the finding must name (point, frame or area label), or "".</param>
    /// <param name="Program">The program's rules it is checked under (etabs joins members where they cross; sap2000 does not).</param>
    public sealed record Case(string Name, string What, string Check, string Object, Func<StructuralModel> Build, string Program = "modeler");

    public static IReadOnlyList<Case> All { get; } =
    [
        new("sound", "A sound 2 x 2 bay, 2-story concrete frame", "", "", () => Frame()),
        new("no-supports", "Nothing holds the building to the ground", "supports", "", () =>
        {
            var m = Frame();
            m.PointAssigns.Clear();
            return m;
        }),
        new("floating-beam", "A beam drawn away from the building, touching nothing", "floating", "B99", () =>
        {
            var m = Frame();
            var b = m.AddBeam(30, 30, 36, 30, "Story1", "B300x600");
            Relabel(m, b, "B99");
            return m;
        }),
        new("missing-section", "A column uses a section that is not defined", "definitions", "C1", () =>
        {
            var m = Frame();
            m.LineAssigns.First(a => a.Label == "C1").Section = "C999";
            return m;
        }),
        new("zero-modulus", "The concrete has a modulus of elasticity of 0", "definitions", "", () =>
        {
            var m = Frame();
            var concrete = m.Materials.First(x => x.Name == ModelEdit.DefaultConcrete);
            foreach (var line in concrete.Lines)
                for (var i = 0; i + 1 < line.Count; i++) if (line[i] == "E") line[i + 1] = "0";
            return m;
        }),
        new("zero-length", "A beam whose two ends are the same place", "geometry", "B98", () =>
        {
            var m = Frame();
            var p = m.PointAt(0, 0);
            var q = new PlanPoint { Name = "900", X = p.X, Y = p.Y };
            m.Points.Add(q);
            m.Lines.Add(new LineObject { Label = "B98", Kind = LineKind.Beam, PointI = p.Name, PointJ = q.Name });
            m.LineAssigns.Add(new LineAssign { Label = "B98", Story = "Story1", Section = "B300x600", Extra = [ModelEdit.LineAssignDefaults(LineKind.Beam)] });
            return m;
        }),
        new("duplicate-beam", "Two beams drawn on top of each other", "duplicates", "B97", () =>
        {
            var m = Frame();
            var first = m.Lines.First(l => l.Kind == LineKind.Beam);
            m.Lines.Add(new LineObject { Label = "B97", Kind = LineKind.Beam, PointI = first.PointI, PointJ = first.PointJ });
            m.LineAssigns.Add(new LineAssign { Label = "B97", Story = "Story1", Section = "B300x600", Extra = [ModelEdit.LineAssignDefaults(LineKind.Beam)] });
            return m;
        }),
        new("torsion-released", "A beam released for torsion at both ends", "releases", "B1", () =>
        {
            var m = Frame();
            Release(m, "B1", "Story1", "TI TJ");
            return m;
        }),
        new("pinned-mechanism", "A bare frame pinned at the base with every beam pinned at both ends: it sways freely", "mechanism", "", () =>
        {
            var m = Frame(slabs: false, pinnedBase: true);
            foreach (var a in m.LineAssigns.Where(a => m.Line(a.Label)?.Kind == LineKind.Beam)) Release(m, a.Label, a.Story, "M2I M3I M2J M3J");
            return m;
        }),
        new("floor-no-lateral", "Rigid floors on columns pinned at both ends: the floors can slide and turn", "mechanism", "", () =>
        {
            var m = Frame(pinnedBase: true);
            foreach (var a in m.LineAssigns.Where(a => m.Line(a.Label)?.Kind == LineKind.Column)) Release(m, a.Label, a.Story, "M2I M3I M2J M3J");
            foreach (var a in m.LineAssigns.Where(a => m.Line(a.Label)?.Kind == LineKind.Beam)) Release(m, a.Label, a.Story, "M2I M3I M2J M3J");
            // Membrane floors carry nothing out of plane: the frame alone must stand sideways, and it cannot.
            foreach (var s in m.ShellSections)
                foreach (var line in s.Lines)
                    for (var i = 0; i + 1 < line.Count; i++) if (line[i] == "MODELINGTYPE") line[i + 1] = "\"Membrane\"";
            return m;
        }),
        new("no-mass", "A modal case with no mass: weightless materials and a mass source from elements only", "mass", "", () =>
        {
            var m = Frame();
            foreach (var mat in m.Materials)
                foreach (var line in mat.Lines)
                    for (var i = 0; i + 1 < line.Count; i++) if (line[i] is "WEIGHTPERVOLUME" or "MASSPERVOLUME") line[i + 1] = "0";
            return m;
        }),
        new("combo-missing-case", "A load combination adds a load case that does not exist", "cases", "", () =>
        {
            var m = Frame();
            m.AddCombo("ULS1", ("Dead", 1.4), ("Wind X", 1.6));
            return m;
        }),
        new("spectrum-missing-modal", "A response spectrum case built on a modal case that does not exist", "cases", "", () =>
        {
            var m = Frame();
            m.LoadCases.Add(new LoadCase { Name = "RSX", Lines = [["TYPE", "\"Response Spectrum\""], ["MODALCASE", "\"Modal 2\""]] });
            return m;
        }),
        new("column-on-beam-sap2000", "A column standing on the middle of a beam that SAP2000 does not divide there", "connectivity", "", () =>
        {
            var m = Frame();
            m.AddColumn(3, 0, "Story2", "C500x500");
            return m;
        }, "sap2000"),
    ];

    /// <summary>The sound frame the broken ones start from: 2 x 2 bays of 6 m, 2 stories of 3 m, slabs on a rigid diaphragm.</summary>
    public static StructuralModel Frame(bool slabs = true, bool pinnedBase = false)
    {
        var m = Templates.ConcreteFrame("Checker sample", [6, 6], [6, 6], 2, 3);
        if (!slabs)
        {
            var floors = m.Areas.Select(a => a.Label).ToHashSet();
            m.Areas.Clear(); m.AreaAssigns.Clear();
            m.AreaLoads.RemoveAll(l => floors.Contains(l.Object));
        }
        if (pinnedBase) foreach (var a in m.PointAssigns) a.Restraint = "UX UY UZ";
        return m;
    }

    private static void Release(StructuralModel m, string label, string story, string releases)
    {
        var a = m.LineAssigns.First(x => x.Label == label && x.Story == story);
        if (a.Extra.Count == 0) a.Extra.Add([]);
        var i = a.Extra[0].IndexOf("RELEASE");
        if (i >= 0) a.Extra[0].RemoveRange(i, 2);
        a.Extra[0].AddRange(["RELEASE", E2kText.Quote(releases)]);
    }

    private static void Relabel(StructuralModel m, LineAssign a, string label)
    {
        var line = m.Line(a.Label)!;
        foreach (var x in m.LineAssigns.Where(x => x.Label == line.Label)) x.Label = label;
        line.Label = label;
    }
}
