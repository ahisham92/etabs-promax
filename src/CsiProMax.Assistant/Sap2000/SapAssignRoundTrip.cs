using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// The Engine's SAP2000 Assign forms, proved in SAP2000 itself (Ahmed 2026-11-27: every form must work exactly as
/// SAP2000's): in the Modeler's part a beam and a joint get the values the Assign > Frame and Assign > Joint forms set
/// (SAP2000's own assignment table rows, through assign_frame_data and assign_joint_data, as the forms send them), the
/// .s2k is written, and in the scratch SAP2000 each table must have a row holding every value the form set. The values
/// are unusual on purpose, so the row that matches is the object's. A form thread adds its tables to <see cref="Frame"/>
/// or <see cref="Joint"/>.
/// </summary>
public static class SapAssignRoundTrip
{
    /// <summary>(form, table, row by SAP2000's field keys) set on one beam.</summary>
    public static readonly (string Form, string Table, Dictionary<string, string> Row)[] Frame =
    [
        ("Assign > Frame > Local Axes (Advanced)", "Frame Local Axes Assignments 2 - Advanced",
            R(("LocalPlane", "12"), ("PlOption1", "Coord Dir"), ("PlCoordSys", "GLOBAL"), ("CoordDir1", "+Y"), ("CoordDir2", "+X"))),
        ("Assign > Frame > Material Temperatures", "Frame Material Temperatures", R(("Temp", "23.5"))),
        ("Assign > Frame > Automatic Frame Mesh", "Frame Auto Mesh Assignments", R(("AutoMesh", "Yes"), ("AtJoints", "Yes"), ("AtFrames", "No"), ("NumSegments", "3"), ("MaxLength", "0"), ("MaxDegrees", "0"))),
        ("Assign > Frame > Rebar Ratio for Creep Analysis", "Frame Rebar Ratio For Creep Analysis Assignments", R(("IsUser", "Yes"), ("UserRatio", "0.0137"))),
        ("Assign > Frame > End (Length) Offsets", "Frame Offset Along Length Assignments", R(("Type", "User"), ("LengthI", "0.137"), ("LengthJ", "0.163"), ("RigidFactor", "0.45"))),
    ];

    /// <summary>(form, table, row by SAP2000's field keys) set on one joint.</summary>
    public static readonly (string Form, string Table, Dictionary<string, string> Row)[] Joint =
    [
        ("Assign > Joint > Merge Number", "Joint Merge Number Assignments", R(("MergeNumber", "7"))),
        ("Assign > Joint > Local Axes", "Joint Local Axes Assignments 1 - Typical", R(("AngleA", "17"), ("AngleB", "0"), ("AngleC", "0"), ("AdvanceAxes", "No"))),
        ("Assign > Joint > Masses (As Weight)", "Joint Added Mass By Weight Assignments", R(("CoordSys", "GLOBAL"), ("Weight1", "2.75"), ("Weight2", "2.75"), ("Weight3", "0"), ("WMI1", "0"), ("WMI2", "0"), ("WMI3", "0"))),
        ("Assign > Joint Patterns", "Joint Pattern Assignments", R(("Pattern", "CPM-JPAT"), ("Value", "3.25"))),
        ("Assign > Joint > Springs", "Joint Spring Assignments 1 - Uncoupled", R(("CoordSys", "GLOBAL"), ("U1", "1537"), ("U2", "1537"), ("U3", "0"), ("R1", "0"), ("R2", "0"), ("R3", "0"))),
    ];

    private static Dictionary<string, string> R(params (string Key, string Value)[] fields) => fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    private static JsonObject Tables(IEnumerable<(string Form, string Table, Dictionary<string, string> Row)> list) =>
        new(list.Select(x => KeyValuePair.Create(x.Table, (JsonNode?)new JsonArray(new JsonObject(x.Row.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))))));

    /// <summary>In the Modeler's part, before the .s2k is written: a beam and a joint get each form's values.</summary>
    public static void Define(FieldContext ctx)
    {
        var m = (ctx.Link as OfflineEtabs)?.Model;
        if (m is null) return;
        var beam = m.LineAssigns.FirstOrDefault(a => m.Line(a.Label)?.Kind == LineKind.Beam);
        if (beam is not null)
        {
            var len = Physical.Frames(m).FirstOrDefault(f => f.Label == beam.Label && f.Story == beam.Story)?.Length ?? 0;
            if (len > 0.6)
                ctx.Tool("assign_frame_data", new JsonObject { ["objects"] = new JsonArray(beam.Label + "@" + beam.Story), ["offset_i"] = 0.137, ["offset_j"] = 0.163, ["rigid_zone"] = 0.45, ["tables"] = Tables(Frame) }.ToJsonString(),
                    null, "SAP2000 Assign > Frame forms: a beam gets each form's values (advanced axes, material temperature, auto mesh, creep rebar ratio, end offsets)");
        }
        var story = beam?.Story ?? m.Stories.LastOrDefault()?.Name;
        var point = beam is not null ? m.Line(beam.Label)?.PointI : m.Points.FirstOrDefault()?.Name;
        if (story is not null && point is not null)
            ctx.Tool("assign_joint_data", new JsonObject { ["objects"] = new JsonArray(point + "@" + story), ["tables"] = Tables(Joint) }.ToJsonString(),
                null, "SAP2000 Assign > Joint forms: a joint gets each form's values (merge number, local axes, mass by weight, joint pattern, springs)");
    }

    /// <summary>In the scratch SAP2000 that opened the .s2k: each table has a row with every value the form set.</summary>
    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        foreach (var (form, table, row) in Frame.Concat(Joint))
            ctx.Step($"SAP2000 form {form}: SAP2000 holds every value the Engine's form set", () =>
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no SAP2000 table to read back");
                TableData data;
                try { data = link.ReadTable(table, null, null); }
                catch (EtabsException ex) { FieldContext.Expect(false, $"{table}: SAP2000 did not read it back ({ex.Message})"); throw; }
                var fields = data.Fields.ToArray();
                int Col(string f) => Array.FindIndex(fields, x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
                var missing = row.Keys.Where(f => Col(f) < 0).ToList();
                FieldContext.Expect(missing.Count == 0, $"{table} has no field {string.Join(", ", missing)} ({string.Join(", ", fields)})");
                var best = data.Rows.Select(r => (r, wrong: row.Where(kv => !Same(kv.Value, Col(kv.Key) < r.Length ? r[Col(kv.Key)] : null)).Select(kv => $"{kv.Key} form {kv.Value}, SAP2000 {r[Col(kv.Key)]}").ToList()))
                    .OrderBy(x => x.wrong.Count).FirstOrDefault();
                FieldContext.Expect(best.r is not null, $"{table}: SAP2000 has no rows");
                FieldContext.Expect(best.wrong.Count == 0, $"{table}: the nearest row differs: {string.Join("; ", best.wrong)}");
                return $"{table}: a row with every value the form set";
            });
    }

    private static bool Same(string want, string? got)
    {
        if (got is null) return false;
        if (double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        return string.Equals(want.Trim(), got.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
