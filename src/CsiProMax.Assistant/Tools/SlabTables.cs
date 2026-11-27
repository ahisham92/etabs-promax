using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// What of a Modeler model ETABS's .e2k cannot carry, put into the running ETABS or SAFE through its own tables once
/// it has opened the model: slab line releases ("Area Assignments - Slab Line Releases", on the null line found where
/// the Modeler drew it) and slab rebar ("Slab Rebar Object Geometry" and "Slab Rebar Property Assignments"). ETABS
/// 23.2 writes no line release into its .e2k and has no API call for one (Ahmed's PC, 2026-11-26, the "J" export).
/// </summary>
public static class SlabTables
{
    /// <summary>What ETABS Plus 23.2 answers to every slab rebar object, said with the refusal.</summary>
    public const string RebarRefused = "ETABS Plus 23.2 refuses every slab rebar object (Ahmed's PC, 2026-11-26: both tables answered 1 with an empty log and no licence message); ETABS Ultimate and SAFE take them."; // ETABS itself

    /// <summary>Whether the model has anything only the program's tables take.</summary>
    public static bool Any(StructuralModel m) => SlabDefs.LineReleases(m).Count + SlabDefs.SlabRebars(m).Count > 0;

    /// <summary>Puts the model's slab line releases and slab rebar into the program behind <paramref name="link"/>; one line for each, what it did.</summary>
    public static List<string> PutInto(ICsiLink link, StructuralModel m)
    {
        var said = new List<string>();
        if (link.Program.Key == CsiProgram.Sap2000.Key || !Any(m)) return said;
        foreach (var r in SlabDefs.LineReleases(m))
        {
            var unique = LineIn(link, m, r.Line);
            if (unique is null) { said.Add($"The slab line release on {r.Line} was left out: {link.Program.Name} has no line where the Modeler's null line {r.Line} is."); continue; }
            try
            {
                EditableTables.Put(link, SlabDefs.LineReleaseTable, "UniqueName", [SlabDefs.LineReleaseRow(r, unique)], $"release the slab along {r.Line}");
                said.Add($"The slab line release on {r.Line} went into {link.Program.Name}'s table '{SlabDefs.LineReleaseTable}' (line {unique}).");
            }
            catch (EtabsException ex) { said.Add($"{link.Program.Name} did not take the slab line release on {r.Line}: {ex.Message}"); }
        }
        var (done, refused) = PutRebar(link, m);
        said.AddRange(done.Select(d => $"The slab rebar {d} went into {link.Program.Name}'s slab rebar tables."));
        said.AddRange(refused);
        return said;
    }

    /// <summary>The program's name of the line at the place of the Modeler's null line <paramref name="line"/>, or null.</summary>
    public static string? LineIn(ICsiLink link, StructuralModel m, string line)
    {
        var l = SlabDefs.NullLines(m).Find(x => x.Name == line);
        if (l is null) return null;
        var z = m.Elevation(m.StoryIndex(l.Story));
        // The program's null lines are not frames to Frames() (0.4.60 full test: the release row went to ETABS with an
        // empty UniqueName, "Frame object  is not defined"): every line object is read, null lines first.
        var tolerance = Tolerance(m);
        bool Near(double a, double b) => Math.Abs(a - b) <= tolerance;
        try
        {
            if (link.Raw("list every line object", api => Etabs.ComEtabs.AllFrames(api)) is IReadOnlyList<FrameRow> all)
            {
                var at = all.Where(f => Near(f.Z1, z) && Near(f.Z2, z)
                    && ((Near(f.X1, l.I[0]) && Near(f.Y1, l.I[1]) && Near(f.X2, l.J[0]) && Near(f.Y2, l.J[1]))
                        || (Near(f.X1, l.J[0]) && Near(f.Y1, l.J[1]) && Near(f.X2, l.I[0]) && Near(f.Y2, l.I[1]))))
                    .OrderBy(f => Etabs.ComEtabs.IsNullLine(f.Section) ? 0 : 1).ToList();
                if (at.Count > 0) return at[0].Name;
            }
        }
        catch (Exception ex) when (ex is EtabsException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException or NotSupportedException) { /* a link without the program's API */ }
        var (frames, _) = EngineRun.Find(link, new EngineRun.Shapes([(l.I[0], l.I[1], z, l.J[0], l.J[1], z)], []), tolerance);
        return frames.FirstOrDefault();
    }

    /// <summary>The model's slab rebar into the program's two slab rebar tables: the names that went in, and what was refused.</summary>
    public static (List<string> Done, List<string> Refused) PutRebar(ICsiLink link, StructuralModel m)
    {
        var done = new List<string>(); var refused = new List<string>();
        if (link.Program.Key == CsiProgram.Sap2000.Key) return (done, refused);
        foreach (var b in SlabDefs.SlabRebars(m))
        {
            var (geometry, bars) = SlabDefs.SlabRebarRows(b);
            try
            {
                EditableTables.Put(link, SlabDefs.SlabRebarGeometryTable, "Name", [geometry], $"draw the slab rebar {b.Name}");
                EditableTables.Put(link, SlabDefs.SlabRebarPropertyTable, "Name", [bars], $"give the slab rebar {b.Name} its bars");
                done.Add(b.Name);
            }
            catch (EtabsException ex) { refused.Add($"{link.Program.Name} did not take the slab rebar {b.Name}: {ex.Message}" + (link.Program.Key == CsiProgram.Etabs.Key ? " " + RebarRefused : "")); }
        }
        return (done, refused);
    }

    /// <summary>1 mm in the model's length unit.</summary>
    private static double Tolerance(StructuralModel m) => m.Units.Length.ToUpperInvariant() switch
    {
        var u when u.Contains("IN") => 0.04, var u when u.Contains("FT") => 0.0033, var u when u.Contains("MM") => 1, var u when u.Contains("CM") => 0.1, _ => 0.001,
    };
}
