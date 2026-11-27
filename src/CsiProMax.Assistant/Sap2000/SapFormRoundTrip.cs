using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// The Engine's SAP2000 forms, proved in SAP2000 itself (Ahmed 2026-11-27: every form must work exactly as SAP2000's):
/// in the Modeler's part, values are set as the forms set them (SAP2000's own definition-table rows, through
/// define_table_rows), the .s2k is written, and in the scratch SAP2000 each table is read back from SAP2000's database
/// and every field must hold the value the form set. A form thread adds its tables to <see cref="Forms"/>.
/// </summary>
public static class SapFormRoundTrip
{
    /// <summary>(form, table, definition name, rows): what each form writes, as the form writes it.</summary>
    public static readonly (string Form, string Table, string Name, Dictionary<string, string>[] Rows)[] Forms =
    [
        ("Link/Support Property Data (Linear)", "Link Property Definitions 02 - Linear", "CPM-LLIN",
            [R(("DOF", "U1"), ("Fixed", "No"), ("TransKE", "1000"), ("TransCE", "5")), R(("DOF", "R3"), ("Fixed", "No"), ("RotKE", "200"), ("RotCE", "0"))]),
        ("Link/Support Property Data (Linear)", "Link Property Definitions 01 - General", "CPM-LLIN",
            [R(("LinkType", "Linear"), ("Mass", "0.5"), ("Weight", "4.9"), ("RotInert1", "0"), ("RotInert2", "0"), ("RotInert3", "0"), ("DefLength", "1"), ("DefArea", "1"), ("PDM2I", "0"), ("PDM2J", "0"), ("PDM3I", "0"), ("PDM3J", "0"))]),
        ("Link/Support Property Data (Rubber Isolator)", "Link Property Definitions 07 - Rubber Isolator", "CPM-LRUB",
            [R(("DOF", "U1"), ("Fixed", "No"), ("NonLinear", "No"), ("TransKE", "100000")), R(("DOF", "U2"), ("Fixed", "No"), ("NonLinear", "Yes"), ("TransKE", "1200"), ("TransCE", "0"), ("DJ", "0"), ("TransK", "12000"), ("TransYield", "150"), ("Ratio", "0.1"))]),
        ("Link/Support Property Data (Rubber Isolator)", "Link Property Definitions 01 - General", "CPM-LRUB",
            [R(("LinkType", "Rubber Isolator"), ("Mass", "0"), ("Weight", "0"), ("DefLength", "1"), ("DefArea", "1"), ("PDM2I", "0"), ("PDM2J", "0"), ("PDM3I", "0"), ("PDM3J", "0"))]),
        ("Link/Support Property Data (Damper - Exponential)", "Link Property Definitions 04 - Damper", "CPM-LDMP",
            [R(("DOF", "U1"), ("Fixed", "No"), ("NonLinear", "Yes"), ("TransKE", "0"), ("TransCE", "0"), ("TransK", "50000"), ("TransC", "300"), ("CExp", "0.5"))]),
        ("Link/Support Property Data (Damper - Exponential)", "Link Property Definitions 01 - General", "CPM-LDMP",
            [R(("LinkType", "Damper - Exponential"), ("Mass", "0"), ("Weight", "0"), ("DefLength", "1"), ("DefArea", "1"), ("PDM2I", "0.5"), ("PDM2J", "0.5"), ("PDM3I", "0.5"), ("PDM3J", "0.5"))]),
        ("Frame Hinge Property Data (Moment M3)", "Hinges Def 01 - Overview", "CPM-FH1",
            [R(("AssignHinge", "No"), ("DOFType", "M3"), ("Behavior", "Deformation Controlled"))]),
        ("Frame Hinge Property Data (Moment M3)", "Hinges Def 02 - Noninteracting - Deform Control - General", "CPM-FH1",
            [R(("AssignHinge", "No"), ("DOFType", "M3"), ("Symmetric", "Yes"), ("BeyondE", "Drops To Zero"), ("FDType", "Moment-Rotation"), ("UseYldForce", "Yes"), ("UseYldDispl", "Yes"), ("HysType", "Isotropic"))]),
        ("Frame Hinge Property Data (Moment M3)", "Hinges Def 03 - Noninteracting - Deform Control - Force-Deform", "CPM-FH1",
            [.. new[] { ("E-", "-0.2", "-8"), ("D-", "-0.2", "-6"), ("C-", "-1.25", "-6"), ("B-", "-1", "0"), ("A", "0", "0"), ("B", "1", "0"), ("C", "1.25", "6"), ("D", "0.2", "6"), ("E", "0.2", "8") }
                .Select(p => R(("AssignHinge", "No"), ("DOFType", "M3"), ("FDPoint", p.Item1), ("Force", p.Item2), ("Displ", p.Item3)))]),
        ("Frame Hinge Property Data (Moment M3)", "Hinges Def 04 - Noninteracting - Deform Control - Acceptance", "CPM-FH1",
            [.. new[] { ("IO", "2"), ("LS", "4"), ("CP", "6") }.Select(p => R(("AssignHinge", "No"), ("DOFType", "M3"), ("ACPoint", p.Item1), ("ACPos", p.Item2), ("ACNeg", "-" + p.Item2)))]),
        ("Frequency Dependent Link Property Data", "Link Frequency Dependent Properties 01 - General", "CPM-FD1", [R(("Color", "Cyan"))]),
        ("Frequency Dependent Link Property Data", "Link Frequency Dependent Properties 02 - Details", "CPM-FD1",
            [R(("DOF", "U1"), ("Frequency", "0"), ("TransK", "1000"), ("TransC", "10")), R(("DOF", "U1"), ("Frequency", "5"), ("TransK", "1200"), ("TransC", "12"))]),
        ("Define Pattern Names", "Joint Pattern Definitions", "CPM-JPAT", [R(("Pattern", "CPM-JPAT"))]),
        ("Solid Property Data", "Solid Property Definitions", "CPM-SOLID",
            [R(("Material", "CPM-C35"), ("MatAngleA", "0"), ("MatAngleB", "0"), ("MatAngleC", "0"), ("InComp", "Yes"))]),
        ("Shell Section Data (Plate - Thick)", "Area Section Properties", "CPM-PLATE",
            [R(("Material", "CPM-C35"), ("MatAngle", "0"), ("AreaType", "Shell"), ("Type", "Plate-Thick"), ("Thickness", "0.25"), ("BendThick", "0.25"))]),
    ];

    /// <summary>
    /// (form, the table SAP2000 reads back, assign_shell_data's arguments as the form sends them, what SAP2000 must hold):
    /// SAP2000's Assign > Area forms, set on one area of the field model. {mat} is a concrete material of the model.
    /// </summary>
    public static readonly (string Form, string Table, string Args, Dictionary<string, string> Row)[] AreaAssigns =
    [
        ("Assign Area Stiffness Modifiers", "Area Stiffness Modifiers", """{"modifiers":{"F11MOD":0.5,"F22MOD":0.6,"F12MOD":0.7,"M11MOD":0.25,"M22MOD":0.3,"M12MOD":0.35,"V13MOD":0.8,"V23MOD":0.9,"MMOD":1,"WMOD":1}}""",
            R(("f11", "0.5"), ("f22", "0.6"), ("f12", "0.7"), ("m11", "0.25"), ("m22", "0.3"), ("m12", "0.35"), ("v13", "0.8"), ("v23", "0.9"), ("MassMod", "1"), ("WeightMod", "1"))),
        ("Assign Area Material Overwrites", "Area Section Assignments", """{"tables":{"Area Assignments - Material Overwrites":[{"Material":"{mat}"}]}}""", R(("MatProp", "{mat}"))),
        ("Assign Area Thickness and Joint Offset Overwrites", "Area Overwrites - Thickness", """{"tables":{"Area Overwrites - Thickness":[{"ThickOver":"User","Thick1":"0.3","Thick2":"0.3","Thick3":"0.35","Thick4":"0.35"}]}}""",
            R(("ThickOver", "User"), ("Thick1", "0.3"), ("Thick2", "0.3"), ("Thick3", "0.35"), ("Thick4", "0.35"))),
        ("Assign Area Thickness and Joint Offset Overwrites", "Area Overwrites - Joint Offsets", """{"tables":{"Area Overwrites - Joint Offsets":[{"OffsetOver":"User","Offset1":"0.05","Offset2":"0.05","Offset3":"0","Offset4":"0"}]}}""",
            R(("OffsetOver", "User"), ("Offset1", "0.05"), ("Offset2", "0.05"), ("Offset3", "0"), ("Offset4", "0"))),
        ("Assign Area Local Axes", "Area Local Axes Assignments 1 - Typical", """{"tables":{"Area Assignments - Local Axes":[{"Angle":"30"}]}}""", R(("Angle", "30"))),
        ("Assign Springs to Area Object Face", "Area Spring Assignments", """{"tables":{"Area Spring Assignments":[{"Type":"Simple","Stiffness":"15.5","SimpleType":"Compression Only","Face":"-1","Dir1Type":"Normal To Face","NormalDir":"Inward"}]}}""",
            R(("Type", "Simple"), ("Stiffness", "15.5"), ("SimpleType", "Compression Only"), ("Face", "-1"), ("Dir1Type", "Normal To Face"), ("NormalDir", "Inward"))),
        ("Assign Area Mass", "Area Added Mass Assignments", """{"tables":{"Area Assignments - Additional Mass":[{"AddedMass":"0.03"}]}}""", R(("MassPerArea", "0.03"))),
        ("Assign Area Material Temperature", "Area Material Temperatures", """{"tables":{"Area Material Temperatures":[{"Temp":"20"}]}}""", R(("Temp", "20"))),
        ("Assign Automatic Area Mesh", "Area Auto Mesh Assignments", """{"tables":{"Area Auto Mesh Assignments":[{"MeshType":"Number","Number1":"3","Number2":"2","LocalEdge":"No","LocalFace":"No","SuppEdge":"No","SuppFace":"No","SubMesh":"No"}]}}""",
            R(("MeshType", "Number"), ("Number1", "3"), ("Number2", "2"))),
        ("Assign Area Edge Constraints", "Area Edge Constraint Assignments", """{"tables":{"Area Edge Constraint Assignments":[{"Edge":"1","Constrained":"Yes"},{"Edge":"2","Constrained":"Yes"},{"Edge":"3","Constrained":"Yes"},{"Edge":"4","Constrained":"Yes"}]}}""",
            R(("Edge", "1"), ("Constrained", "Yes"))),
        ("Assign Area Rebar Ratio for Creep Analysis", "Area Rebar Ratio For Creep Analysis Assignments", """{"tables":{"Area Rebar Ratio For Creep Analysis Assignments":[{"UserRatio":"0.004"}]}}""", R(("UserRatio", "0.004"))),
    ];

    /// <summary>The area the Assign > Area forms were set on (its SAP2000 name) and the material put in {mat}.</summary>
    private static (string Area, string Material)? areaUsed;

    /// <summary>In the Modeler's part: each Assign > Area form's values set on the last floor area, as the form sets them.</summary>
    public static void AssignAreas(FieldContext ctx)
    {
        if (ctx.Link is not OfflineEtabs modeler) return;
        var m = modeler.Model;
        var floor = m.AreaAssigns.LastOrDefault(x => m.Area(x.Label)?.Kind == CsiProMax.Model.AreaKind.Floor && m.Area(x.Label)?.PointNames.Count == 4);
        var mat = m.Materials.FirstOrDefault(x => x.IsConcrete)?.Name;
        if (floor is null || mat is null) return;
        areaUsed = (CsiProMax.S2k.S2kWriter.Name(floor.Label, floor.Story), mat);
        foreach (var (form, table, args, _) in AreaAssigns)
        {
            var json = JsonNode.Parse(args.Replace("{mat}", mat))!.AsObject();
            json["objects"] = new JsonArray(floor.Label + "@" + floor.Story);
            ctx.Tool("assign_shell_data", json.ToJsonString(), null, $"SAP2000 form {form}: set on area {floor.Label}@{floor.Story} as the form sets it");
        }
        // Assign > Area > Reverse Local 3, twice: corner 1 stays and the others run the other way, then back as they were.
        var corners = m.Area(floor.Label)!.PointNames.ToList();
        var all = m.AreaAssigns.Where(x => x.Label == floor.Label).Select(x => x.Label + "@" + x.Story).ToList();
        var objects = new JsonObject { ["objects"] = new JsonArray(all.Select(x => (JsonNode)x).ToArray()), ["keep"] = "local" }.ToJsonString();
        ctx.Tool("reverse_objects", objects, _ => FieldContext.Expect(m.Area(floor.Label)!.PointNames[1] == corners[^1], "the corners did not run the other way"), "SAP2000 form Assign Area Reverse Local 3 Axis: the corners run the other way");
        ctx.Tool("reverse_objects", objects, _ => FieldContext.Expect(m.Area(floor.Label)!.PointNames.SequenceEqual(corners), "reversing twice did not give the corners back"), "SAP2000 form Assign Area Reverse Local 3 Axis, again: the corners are back");
    }

    private static Dictionary<string, string> R(params (string Key, string Value)[] fields) => fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    /// <summary>In the Modeler's part, before the .s2k is written: each form's values set as the form sets them.</summary>
    public static void Define(FieldContext ctx)
    {
        foreach (var (form, table, name, rows) in Forms)
        {
            var json = new JsonObject { ["table"] = table, ["name"] = name, ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject(r.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))).ToArray()) };
            ctx.Tool("define_table_rows", json.ToJsonString(), a => FieldContext.Expect(Int(a["rows"]) == rows.Length,
                $"{table}: {name} holds {a["rows"]} rows, the form set {rows.Length}"), $"SAP2000 form {form}: {table} set as the form sets it");
        }
    }

    /// <summary>In the scratch SAP2000 that opened the .s2k: every table read back from SAP2000, field by field.</summary>
    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        foreach (var group in Forms.GroupBy(f => f.Form))
            ctx.Step($"SAP2000 form {group.Key}: SAP2000 holds every value the Engine's form set", () =>
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no SAP2000 table to read back");
                var wrong = new List<string>();
                foreach (var (_, table, name, rows) in group)
                {
                    TableData data;
                    try { data = link.ReadTable(table, null, null); }
                    catch (EtabsException ex) { wrong.Add($"{table}: SAP2000 did not read it back ({ex.Message})"); continue; }
                    var key = Array.FindIndex(data.Fields.ToArray(), f => f.Equals(DefineKey(table), StringComparison.OrdinalIgnoreCase));
                    var mine = data.Rows.Where(r => key >= 0 && key < r.Length && r[key] == name).ToList();
                    if (mine.Count != rows.Length) { wrong.Add($"{table}: SAP2000 has {mine.Count} rows for {name}, the form set {rows.Length}"); continue; }
                    for (var i = 0; i < rows.Length; i++)
                        foreach (var (field, value) in rows[i])
                        {
                            var at = Array.FindIndex(data.Fields.ToArray(), f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
                            var got = at >= 0 && at < mine[i].Length ? mine[i][at] : null;
                            if (!Same(value, got)) wrong.Add($"{table} {name} row {i + 1} {field}: form {value}, SAP2000 {got ?? "(no field)"}");
                        }
                }
                FieldContext.Expect(wrong.Count == 0, string.Join("; ", wrong.Take(12)) + (wrong.Count > 12 ? $"; and {wrong.Count - 12} more" : ""));
                return $"{group.Count()} tables, every field the same";
            });
    }

    /// <summary>In the scratch SAP2000: each Assign > Area form's table read back for that area, field by field.</summary>
    public static void CheckAssigns(FieldContext ctx)
    {
        var link = ctx.Link;
        if (areaUsed is not { } used) return;
        foreach (var group in AreaAssigns.GroupBy(f => f.Form))
            ctx.Step($"SAP2000 form {group.Key}: SAP2000 holds every value the Engine's form set on area {used.Area}", () =>
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no SAP2000 table to read back");
                var wrong = new List<string>();
                foreach (var (_, table, _, row) in group)
                {
                    TableData data;
                    try { data = link.ReadTable(table, null, null); }
                    catch (EtabsException ex) { wrong.Add($"{table}: SAP2000 did not read it back ({ex.Message})"); continue; }
                    var key = Array.FindIndex(data.Fields.ToArray(), f => f.Equals("Area", StringComparison.OrdinalIgnoreCase));
                    var mine = data.Rows.Where(r => key >= 0 && key < r.Length && r[key] == used.Area).ToList();
                    if (mine.Count == 0) { wrong.Add($"{table}: SAP2000 has no row for area {used.Area}"); continue; }
                    // The row of the area that matches best (an edge or face table has several).
                    var first = mine.OrderByDescending(r => row.Count(kv => Same(kv.Value.Replace("{mat}", used.Material), Array.FindIndex(data.Fields.ToArray(), f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) is var at && at >= 0 && at < r.Length ? r[at] : null))).First();
                    foreach (var (field, value0) in row)
                    {
                        var value = value0.Replace("{mat}", used.Material);
                        var at = Array.FindIndex(data.Fields.ToArray(), f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
                        var got = at >= 0 && at < first.Length ? first[at] : null;
                        if (!Same(value, got)) wrong.Add($"{table} {used.Area} {field}: form {value}, SAP2000 {got ?? "(no field)"}");
                    }
                }
                FieldContext.Expect(wrong.Count == 0, string.Join("; ", wrong.Take(12)) + (wrong.Count > 12 ? $"; and {wrong.Count - 12} more" : ""));
                return $"{group.Count()} tables, every field the same";
            });
    }

    private static string DefineKey(string table) => CsiProMax.Model.DefineTables.Tables.TryGetValue(table, out var f) ? f[0].Key : "Name";

    private static bool Same(string want, string? got)
    {
        if (got is null) return false;
        if (double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        return string.Equals(want.Trim(), got.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
