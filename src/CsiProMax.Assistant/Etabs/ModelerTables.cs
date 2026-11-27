using CsiProMax.Model;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// What the Modeler's forms hold that ETABS's .e2k has no keyword for (a material's damping, notes, time dependence,
/// user stress-strain curve, ...; an object's added mass, output stations, mesh options, edge releases, ...) is kept in the .cpm beside the .e2k as rows of ETABS's own tables. When CSI ProMax
/// opens that .e2k in ETABS, those rows go in through ETABS's database tables, so nothing the forms held is lost.
/// SAP2000 and SAFE get them in their own text files (.s2k, .f2k), which are tables already.
/// </summary>
public static class ModelerTables
{
    /// <summary>The Modeler's .cpm beside a program file, or null.</summary>
    public static string? ModelerFileBeside(string programFile)
    {
        var cpm = Path.ChangeExtension(programFile, ModelFile.Extension);
        return File.Exists(cpm) ? cpm : null;
    }

    /// <summary>
    /// Puts the table rows of the Modeler's model at <paramref name="cpm"/> into the program behind <paramref name="e"/>,
    /// table by table; says what went in, and what the program refused, without stopping at the first refusal.
    /// </summary>
    public static (List<string> Done, List<string> Refused) Apply(ICsiLink e, string cpm)
    {
        var model = ModelFile.Load(cpm);
        if (e.Program.Key == CsiProgram.Sap2000.Key) return ApplySap(e, model);
        var done = new List<string>(); var refused = new List<string>();
        foreach (var (table, rows) in ProgramTables.MaterialRows(model).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            try
            {
                EditableTables.Put(e, table, "Material", rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList(), $"put the Modeler's '{table}' in");
                done.Add($"{table}: {rows.Count} row{(rows.Count == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{table}: {ex.Message}"); }
        }
        // The definitions the .e2k has no keyword for (generalized displacements, hinges, layers, ...), by name.
        foreach (var (table, rows) in (model.DefinitionTables ?? []).Where(t => ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, t.Key)).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            try
            {
                EditableTables.Put(e, table, "Name", rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList(), $"put the Modeler's '{table}' in");
                done.Add($"{table}: {rows.Count} row{(rows.Count == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{table}: {ex.Message}"); }
        }
        // A frame section's other fields (colour, notes, notional size, fillet radius, ...), in its shape's table by name.
        foreach (var g in model.FrameSections.Where(x => PropertyDefs.FrameExtras(x).Count > 0).GroupBy(x => x.Shape))
        {
            var table = "Frame Section Property Definitions - " + g.Key;
            try
            {
                EditableTables.Put(e, table, "Name", g.Select(x => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(PropertyDefs.FrameExtras(x)) { ["Name"] = x.Name }).ToList(), $"put the Modeler's '{table}' in");
                done.Add($"{table}: {g.Count()} row{(g.Count() == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{table}: {ex.Message}"); }
        }
        // A slab or wall section's other fields (notional size, colour, notes, a wall's rigid zone), in its table by name.
        foreach (var g in model.ShellSections.Where(x => PropertyDefs.ShellExtras(x).Keys.Except(PropertyDefs.SapOnlyShellFields).Any()).GroupBy(PropertyDefs.ShellTable))
        {
            try
            {
                EditableTables.Put(e, g.Key, "Name", g.Select(x => (IReadOnlyDictionary<string, string>)PropertyDefs.ShellExtras(x).Where(kv => !PropertyDefs.SapOnlyShellFields.Contains(kv.Key))
                    .Append(KeyValuePair.Create("Name", x.Name)).ToDictionary(kv => kv.Key, kv => kv.Value)).ToList(), $"put the Modeler's '{g.Key}' in");
                done.Add($"{g.Key}: {g.Count()} row{(g.Count() == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{g.Key}: {ex.Message}"); }
        }
        // The objects' assignments the .e2k has no keyword for, by story and label (AssignTables). A new row names its
        // object's UniqueName too, read from the program's summary of that kind of object.
        var unique = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string> UniqueOf(string table)
        {
            var summary = table.Split(" - ")[0] + " - Summary";
            if (unique.TryGetValue(summary, out var map)) return map;
            map = new(StringComparer.Ordinal);
            try
            {
                var t = e.ReadTable(summary, null, null);
                int s = IndexOf(t, "Story"), l = IndexOf(t, "Label"), u = IndexOf(t, "UniqueName");
                if (s >= 0 && l >= 0 && u >= 0) foreach (var r in t.Rows) map.TryAdd(r[s] + "@" + r[l], r[u]);
            }
            catch (EtabsException) { }
            return unique[summary] = map;
        }
        foreach (var (table, rows) in AssignTables.Rows(model).Where(t => ProgramOptions.HasAssignTable(ProgramOptions.Etabs, t.Key)).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            try
            {
                var names = UniqueOf(table);
                foreach (var r in rows) if (names.TryGetValue(r["Story"] + "@" + r["Label"], out var u)) r["UniqueName"] = u;
                // A slab line release is on a null line, which no summary lists: the program's line where the Modeler drew it.
                if (table.Equals(SlabDefs.LineReleaseTable, StringComparison.OrdinalIgnoreCase))
                    foreach (var r in rows.Where(r => !r.ContainsKey("UniqueName")))
                        if (Tools.SlabTables.LineIn(e, model, r["Label"]) is { } line) r["UniqueName"] = line;
                EditableTables.Put(e, table, "Story|Label", rows.Select(r => (IReadOnlyDictionary<string, string>)r).ToList(), $"put the Modeler's '{table}' in");
                done.Add($"{table}: {rows.Count} row{(rows.Count == 1 ? "" : "s")}");
            }
            catch (EtabsException ex) { refused.Add($"{table}: {ex.Message}"); }
        }
        // Slab rebar: objects of their own, which the .e2k has no keyword for either.
        var (bars, no) = Tools.SlabTables.PutRebar(e, model);
        done.AddRange(bars.Select(b => $"slab rebar {b}"));
        refused.AddRange(no);
        return (done, refused);
    }

    /// <summary>True when the Modeler's model at <paramref name="cpm"/> holds rows that go in after the file opens.</summary>
    public static bool HasRows(string cpm, CsiProgram program)
    {
        var model = ModelFile.Load(cpm);
        return AssignTables.Rows(model).Count > 0 || (program.Key == CsiProgram.Etabs.Key && (model.Materials.Any(x => x.Tables is not null) || (model.DefinitionTables ?? []).Keys.Any(k => ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, k))))
            || (program.Key != CsiProgram.Sap2000.Key && SlabDefs.SlabRebars(model).Count > 0);
    }

    private static bool Yes(IReadOnlyDictionary<string, string> r, string k) => r.TryGetValue(k, out var v) && v.Equals("Yes", StringComparison.OrdinalIgnoreCase);
    private static double Num(IReadOnlyDictionary<string, string> r, string k) =>
        r.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>
    /// SAP2000: the materials and the assignments SAP2000's .s2k has tables for came in with the file; the assignments
    /// it takes only by its API (output stations, auto mesh, material overwrites, edge constraints, thickness overwrites
    /// and joint offsets of shells) are set here, on the frames and areas named as the .s2k names them (label_story).
    /// What has no SAP2000 counterpart (ETABS's floor meshing, splices, beam connection types, cracking, creep, slab line
    /// releases and ribs) has nothing to set there.
    /// </summary>
    private static (List<string> Done, List<string> Refused) ApplySap(ICsiLink e, StructuralModel model)
    {
        var done = new List<string>(); var refused = new List<string>();
        void Call(string what, Func<dynamic, object> call)
        {
            try
            {
                var answer = Convert.ToInt32(e.Raw("set " + what, m => call(m)));
                if (answer == 0) done.Add(what); else refused.Add($"{what}: {e.Program.Name} answered {answer}");
            }
            catch (EtabsException ex) { refused.Add($"{what}: {ex.Message}"); }
        }
        foreach (var a in model.LineAssigns.Where(x => x.Tables is not null))
        {
            var name = CsiProMax.S2k.S2kWriter.Name(a.Label, a.Story);
            var st = AssignTables.Row(a, "Frame Assignments - Output Stations");
            if (st.Count > 0)
            {
                var max = (st.GetValueOrDefault("StationOpt") ?? "").Contains("Max", StringComparison.OrdinalIgnoreCase);
                Call($"output stations of {name}", m => m.FrameObj.SetOutputStations(name, max ? 1 : 2, Num(st, "MaxSpacing"), Math.Max(1, (int)Num(st, "MinStations")), false, false, 0));
            }
            var mesh = AssignTables.Row(a, "Frame Assignments - Frame Auto Mesh Options");
            if (mesh.Count > 0)
                Call($"auto mesh of {name}", m => m.FrameObj.SetAutoMesh(name, Yes(mesh, "AutoMesh"), Yes(mesh, "AtIntJoints"), Yes(mesh, "AtIntersect"), Yes(mesh, "MinNumber") ? Math.Max(1, (int)Num(mesh, "NumSegments")) : 0, Yes(mesh, "MaxLength") ? Num(mesh, "SegLength") : 0, 0));
            if (AssignTables.Row(a, "Frame Assignments - Material Overwrites").GetValueOrDefault("Material") is { Length: > 0 } fmat)
                Call($"material overwrite of {name}", m => m.FrameObj.SetMaterialOverwrite(name, fmat, 0));
        }
        foreach (var a in model.AreaAssigns.Where(x => x.Tables is not null))
        {
            var name = CsiProMax.S2k.S2kWriter.Name(a.Label, a.Story);
            var corners = model.Area(a.Label)?.PointNames.Count ?? 0;
            if (AssignTables.Row(a, "Area Assignments - Material Overwrites").GetValueOrDefault("Material") is { Length: > 0 } amat)
                Call($"material overwrite of {name}", m => m.AreaObj.SetMaterialOverwrite(name, amat, 0));
            var ec = AssignTables.Row(a, "Area Assignments - Auto Edge Constraints");
            if (ec.Count > 0)
            {
                var on = !(ec.GetValueOrDefault("Constraint") ?? "").Equals("None", StringComparison.OrdinalIgnoreCase) && !(ec.GetValueOrDefault("Constraint") ?? "").Equals("No", StringComparison.OrdinalIgnoreCase);
                Call($"edge constraint of {name}", m => m.AreaObj.SetEdgeConstraint(name, on, 0));
            }
            if (a.Tables!.TryGetValue("Area Assignments - Thickness Overwrites", out var thick) && corners > 0)
            {
                var t = new double[corners];
                foreach (var r in thick) { var k = (int)Num(r, "PointNumber") - 1; if (k >= 0 && k < corners) t[k] = Num(r, "Thickness"); }
                Call($"thickness overwrites of {name}", m => { var arr = t; return m.AreaObj.SetThickness(name, 1, "", 1.0, ref arr, 0); });
            }
            var ins = AssignTables.Row(a, "Area Assignments - Insertion Point");
            if (ins.Count > 0 && corners > 0)
            {
                var off = Enumerable.Repeat(Num(ins, "Offset3"), corners).ToArray();
                Call($"joint offsets of {name}", m => { var arr = off; return m.AreaObj.SetOffsets(name, 1, "", 1.0, ref arr, 0); });
            }
            foreach (var (table, many) in new[] { ("Area Assignments - Floor Auto Mesh Options", true), ("Area Assignments - Wall Auto Mesh Options", false) })
            {
                var r = AssignTables.Row(a, table);
                if (r.Count == 0) continue;
                var option = r.GetValueOrDefault("MeshOption") ?? "";
                int n1 = (int)Num(r, many ? "N1" : "Nh"), n2 = (int)Num(r, many ? "N2" : "Nv");
                // SAP2000's mesh types: 0 none, 1 into n1 x n2 objects, 6 the general divide tool (ETABS's own auto mesh).
                var type = option.Contains("No Auto", StringComparison.OrdinalIgnoreCase) ? 0 : n1 > 0 && n2 > 0 ? 1 : 6;
                Call($"auto mesh of {name}", m => m.AreaObj.SetAutoMesh(name, type, Math.Max(1, n1), Math.Max(1, n2), 0.0, 0.0, false, false, false, 0.0, Num(r, "SubmeshSize"), false, false, Yes(r, "Restraints"), false, "ALL", Yes(r, "Submesh"), Num(r, "SubmeshSize"), 0));
            }
        }
        return (done, refused);
    }

    private static int IndexOf(TableData t, string field) => t.Fields.ToList().FindIndex(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
}
