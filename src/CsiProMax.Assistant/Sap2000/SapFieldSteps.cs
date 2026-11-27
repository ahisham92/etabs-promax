using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant.Sap2000;

/// <summary>
/// SAP2000's part of the field test. <see cref="Modeler"/> writes the Modeler's model as a .s2k; <see cref="Scratch"/>
/// then works on the scratch model in the test's own SAP2000 window (<see cref="FieldContext.Link"/>): what SAP2000
/// read from the file, every read of the link, every SAP2000 tool with real arguments and its change read back, then
/// SAP2000's own analysis and design and every result tool. It ends with the tools that were not run, which must be none.
/// </summary>
public static class SapFieldSteps
{
    /// <summary>In the Modeler's part: the model saved as a SAP2000 text file, for the scratch window to open.</summary>
    /// <summary>The SAP2000 function forms' tables the Modeler writes (Ahmed 2026-11-27: every form works as in SAP2000):
    /// one function of several codes and types, each with its form's own default parameters, written into the .s2k and read
    /// back from SAP2000's own table, field by field.</summary>
    internal static readonly (string Name, string Kind, string Table, Dictionary<string, string> Values, double[][] Points)[] Functions =
    [
        ("CPM-RS-ASCE716", "spectrum", "Function - Response Spectrum - ASCE 7-16", new() { ["Ss"] = "2.29", ["S1"] = "0.869", ["TL"] = "8", ["SiteClass"] = "B", ["Fa"] = "0.9", ["Fv"] = "0.8", ["SDS"] = "1.374", ["SD1"] = "0.4635" }, [[0, 0.5496], [0.0675, 1.374], [0.3373, 1.374], [1, 0.4635], [8, 0.0579]]),
        ("CPM-RS-EC8", "spectrum", "Function - Response Spectrum - EuroCode8 2004", new() { ["Country"] = "CEN Default", ["Direction"] = "Horizontal", ["Ag"] = "0.4", ["SpecType"] = "1", ["GroundType"] = "B", ["SoilFact"] = "1.2", ["Tb"] = "0.15", ["Tc"] = "0.5", ["Td"] = "2", ["LBFact"] = "0.2", ["BehaveFact"] = "2" }, [[0, 0.32], [0.15, 0.6], [0.5, 0.6], [2, 0.15], [4, 0.08]]),
        ("CPM-RS-UBC97", "spectrum", "Function - Response Spectrum - UBC97", new() { ["Ca"] = "0.36", ["Cv"] = "0.54" }, [[0, 0.36], [0.12, 0.9], [0.6, 0.9], [1, 0.54], [4, 0.135]]),
        ("CPM-TH-SINE", "history", "Function - Time History - Sine", new() { ["Period"] = "1", ["StepsPerCyc"] = "4", ["NumCycles"] = "2", ["Amplitude"] = "1" }, [[0, 0], [0.25, 1], [0.5, 0], [0.75, -1], [1, 0], [1.25, 1], [1.5, 0], [1.75, -1], [2, 0]]),
        ("CPM-TH-RAMP", "history", "Function - Time History - Ramp", new() { ["RampTime"] = "1", ["Amplitude"] = "1", ["MaxTime"] = "20" }, [[0, 0], [1, 1], [20, 1]]),
        ("CPM-PSD", "psd", "Function - Power Spectral Density - User", new(), [[1, 0.1], [10, 0.2], [20, 0.05]]),
        ("CPM-SS", "steady", "Function - Steady State - User", new(), [[0.5, 1], [5, 1], [10, 0.5]]),
    ];

    public static void Modeler(FieldContext ctx)
    {
        SapFormRoundTrip.Define(ctx);
        SapFormRoundTrip.AssignAreas(ctx);
        // The SAP2000 function forms' functions, so SAP2000 reads them back (Scratch: "SAP2000 read the function forms' tables").
        foreach (var (name, kind, table, values, points) in Functions)
            ctx.Tool("define_function", new JsonObject
            {
                ["name"] = name, ["kind"] = kind, ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(p[0], p[1])).ToArray()), ["sap_table"] = table,
                ["sap_values"] = new JsonObject(values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            }.ToJsonString(), null, $"tool define_function, SAP2000's {table.Split(" - ").Last()} {(kind == "spectrum" ? "response spectrum" : kind == "history" ? "time history" : kind)} form");
        var answer = ctx.Tool("save_model_s2k", $$"""{"name":"{{ModelerFieldSteps.ModelName}}","folder":{{Q(ctx.Folder)}}}""", a =>
        {
            var file = a["saved"]!.GetValue<string>();
            FieldContext.Expect(File.Exists(file) && new FileInfo(file).Length > 1000, $"the file {file} was not written");
            FieldContext.Expect(Int(a["joints"]) > 0 && Int(a["frames"]) == ctx.Written.Frames && Int(a["areas"]) == ctx.Written.Areas,
                $"the model has {ctx.Written.Frames} frames and {ctx.Written.Areas} areas; the .s2k has {a["frames"]} and {a["areas"]} (left out: {a["left_out"]?.ToJsonString()})");
        });
        if (answer?["saved"]?.GetValue<string>() is not { } s2k) return;
        ctx.Files[CsiProgram.Sap2000.Key] = s2k;
        ctx.Facts["sap2000.joints"] = Int(answer["joints"]);
        ctx.Facts["sap2000.frames"] = Int(answer["frames"]);
        ctx.Facts["sap2000.areas"] = Int(answer["areas"]);
    }

    public static void Scratch(FieldContext ctx)
    {
        var snapshots = ResultTools.SnapshotRoot;
        ResultTools.SnapshotRoot = Path.Combine(ctx.Folder, "snapshots", "sap2000");
        try { Steps(ctx); }
        finally { ResultTools.SnapshotRoot = snapshots; }
    }

    private static bool At(double a, double b) => Math.Abs(a - b) < 1e-4;

    private static double Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var x) ? x : double.NaN;

    private static string Short(JsonNode a)
    {
        var text = a.ToJsonString();
        return text.Length > 400 ? text[..400] + "..." : text;
    }

    /// <summary>
    /// Reads something back through SAP2000's own API. A read-back that cannot be made fails the check, as one SAP2000
    /// refuses does: the change is not proved otherwise (Ahmed, 2026-11-26).
    /// </summary>
    private static T Back<T>(ICsiLink link, string what, Func<dynamic, object?> read)
    {
        if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no API to read the change back through");
        try { return (T)link.Raw(what, read)!; }
        catch (EtabsException ex) when (ex.Message.Contains("SAP2000 refused to", StringComparison.Ordinal)) { throw new FieldCheckException(ex.Message); }
        // A change that cannot be read back is not proved: a failure, by name (Ahmed, 2026-11-26: only a missing program or licence is not tested).
        catch (EtabsException ex) { throw new FieldCheckException($"the change could not be read back from SAP2000 ({ex.Message})"); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { throw new FieldCheckException($"the change could not be read back from SAP2000 ({ex.Message})"); }
    }

    private static void Ok(object result, string what) => FieldContext.Expect(Convert.ToInt32(result) == 0, $"SAP2000 refused to {what}");

    /// <summary>The distributed loads on a frame in CPM-TEST, by their first value.</summary>
    private static List<double> FrameLoads(ICsiLink link, string frame) => Back<List<double>>(link, $"read the loads on frame {frame}", m =>
    {
        int n = 0; string[] names = [], patterns = [], systems = []; int[] types = [], dirs = [];
        double[] rd1 = [], rd2 = [], d1 = [], d2 = [], v1 = [], v2 = [];
        Ok(m.FrameObj.GetLoadDistributed(frame, ref n, ref names, ref patterns, ref types, ref systems, ref dirs, ref rd1, ref rd2, ref d1, ref d2, ref v1, ref v2, 0), $"read the loads on frame {frame}");
        return Enumerable.Range(0, n).Where(i => patterns[i] == "CPM-TEST").Select(i => v1[i]).ToList();
    });

    /// <summary>The uniform loads on an area in CPM-TEST.</summary>
    private static List<double> AreaLoads(ICsiLink link, string area) => Back<List<double>>(link, $"read the loads on area {area}", m =>
    {
        int n = 0; string[] names = [], patterns = [], systems = []; int[] dirs = []; double[] values = [];
        Ok(m.AreaObj.GetLoadUniform(area, ref n, ref names, ref patterns, ref systems, ref dirs, ref values, 0), $"read the loads on area {area}");
        return Enumerable.Range(0, n).Where(i => patterns[i] == "CPM-TEST").Select(i => values[i]).ToList();
    });

    /// <summary>A frame's end releases, U1 U2 U3 R1 R2 R3 at each end.</summary>
    private static (bool[] I, bool[] J) Releases(ICsiLink link, string frame) => Back<(bool[], bool[])>(link, $"read the releases of frame {frame}", m =>
    {
        bool[] ii = new bool[6], jj = new bool[6]; double[] sv = new double[6], ev = new double[6];
        Ok(m.FrameObj.GetReleases(frame, ref ii, ref jj, ref sv, ref ev), $"read the releases of frame {frame}");
        return (ii, jj);
    });

    /// <summary>A joint's force load in CPM-TEST: F1 F2 F3 M1 M2 M3, or null when it has none.</summary>
    private static double[]? JointLoad(ICsiLink link, string joint) => Back<double[]?>(link, $"read the loads on joint {joint}", m =>
    {
        int n = 0; string[] names = [], patterns = [], systems = []; int[] steps = [];
        double[] f1 = [], f2 = [], f3 = [], m1 = [], m2 = [], m3 = [];
        Ok(m.PointObj.GetLoadForce(joint, ref n, ref names, ref patterns, ref steps, ref systems, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, 0), $"read the loads on joint {joint}");
        var i = Array.IndexOf(patterns, "CPM-TEST");
        return i < 0 || i >= n ? null : new[] { f1[i], f2[i], f3[i], m1[i], m2[i], m3[i] };
    });

    private static void Steps(FieldContext ctx)
    {
        var link = ctx.Link;
        ctx.ClearTools();
        ctx.Offer(EtabsTools.All);
        ctx.Offer(SapTools.All);

        // ---- what SAP2000 read from the file the Modeler wrote ----
        // The function forms: each function in SAP2000's own table of its code or type, every parameter as the Engine wrote it.
        foreach (var (name, kind, fnTable, values, points) in Functions)
            ctx.Step($"SAP2000 read the Engine's {fnTable.Replace("Function - ", "")} function, every field", () =>
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no table to read back");
                TableData t;
                try { t = link.ReadTable(fnTable, null, null); }
                catch (EtabsException ex) { throw new FieldCheckException($"SAP2000 did not give its table \"{fnTable}\" ({ex.Message})"); }
                var col = t.Fields.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.OrdinalIgnoreCase);
                FieldContext.Expect(col.ContainsKey("Name"), $"SAP2000's \"{fnTable}\" has no Name field: {string.Join(", ", t.Fields)}");
                var rows = t.Rows.Where(r => r[col["Name"]] == name).ToList();
                FieldContext.Expect(rows.Count > 0, $"SAP2000 has no {name} in \"{fnTable}\" (it has {string.Join(", ", t.Rows.Select(r => r[col["Name"]]).Distinct())})");
                static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase)
                    || double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) <= 1e-3 * Math.Max(1, Math.Abs(x));
                var off = values.Where(kv => !col.TryGetValue(kv.Key, out var c) || !Same(rows[0][c] ?? "", kv.Value)).Select(kv => $"{kv.Key}: Engine {kv.Value}, SAP2000 {(col.TryGetValue(kv.Key, out var c2) ? rows[0][c2] : "(no field)")}").ToList();
                FieldContext.Expect(off.Count == 0, $"{name}: " + string.Join("; ", off));
                if (kind is "psd" or "steady" or "history" && fnTable.EndsWith("User", StringComparison.OrdinalIgnoreCase) || kind is "psd" or "steady")
                    FieldContext.Expect(rows.Count == points.Length, $"{name}: the Engine wrote {points.Length} points, SAP2000 has {rows.Count}");
                return $"{rows.Count} rows; " + string.Join(", ", values.Select(kv => kv.Key + "=" + rows[0][col[kv.Key]]));
            });

        ctx.Step("SAP2000 read every joint, frame and area the Modeler wrote", () =>
        {
            var (joints, frames, areas) = (link.Points().Count, link.Frames().Count, link.Areas().Count);
            var (j, f, a) = ((int)ctx.Facts.GetValueOrDefault("sap2000.joints"), (int)ctx.Facts.GetValueOrDefault("sap2000.frames"), (int)ctx.Facts.GetValueOrDefault("sap2000.areas"));
            FieldContext.Expect(joints == j && frames == f && areas == a, $"the .s2k has {j} joints, {f} frames and {a} areas; SAP2000 has {joints}, {frames} and {areas}");
            return $"{joints} joints, {frames} frames, {areas} areas";
        });
        ctx.Step("the material, sections, load pattern and combination came across", () =>
        {
            var missing = new List<string>();
            void Need(IEnumerable<string> have, string name, string what) { if (!have.Contains(name, StringComparer.OrdinalIgnoreCase)) missing.Add($"{what} {name}"); }
            Need(link.Materials(), "CPM-C35", "material");
            Need(link.FrameSections(), "CPM-C400", "frame section");
            Need(link.FrameSections(), "CPM-B250x500", "frame section");
            Need(link.AreaSections(), "CPM-S180", "area section");
            Need(link.LoadPatterns().Select(p => p.Name), "CPM-SDL", "load pattern");
            Need(link.Combos(), "CPM-ULS", "combination");
            FieldContext.Expect(missing.Count == 0, "not in SAP2000 after the import: " + string.Join(", ", missing));
            return "all there";
        });

        // ---- each SAP2000 form of the Engine, read back from SAP2000 field by field ----
        SapFormRoundTrip.Check(ctx);
        SapFormRoundTrip.CheckAssigns(ctx);

        // ---- every read of the link ----
        LinkReads(ctx, stories: false);

        // ---- SAP2000's own reads ----
        ctx.Tool("get_model_context", "{}", a => FieldContext.Expect(a.ToJsonString().Contains("CPM-C400", StringComparison.Ordinal), "the context does not name the section CPM-C400"));
        ctx.Tool("query_joints", """{"limit":3}""", a => FieldContext.Expect(Int(a["count"]) == link.Points().Count && a["joints"] is JsonArray { Count: 3 }, $"every joint counted and 3 listed were expected: {a["count"]}"));
        ctx.Tool("query_joints", """{"z":0,"supported_only":true}""", a => FieldContext.Expect(Int(a["count"]) >= 9, $"the 9 fixed column bases at z = 0 were expected: {a["count"]}"), "tool query_joints, the supports at a level");

        // ---- definitions ----
        ctx.Tool("add_material", """{"name":"CPM-SAP-C30","type":"concrete","elastic_modulus":27000000,"unit_weight":25,"fc":30000}""", _ => FieldContext.Expect(link.Materials().Contains("CPM-SAP-C30"), "the material is not in SAP2000"), "tool add_material concrete");
        ctx.Tool("add_material", """{"name":"CPM-SAP-S355","type":"steel","elastic_modulus":200000000,"unit_weight":77,"fy":355000,"fu":470000}""", _ => FieldContext.Expect(link.Materials().Contains("CPM-SAP-S355"), "the material is not in SAP2000"), "tool add_material steel");
        foreach (var (name, json) in new[]
        {
            ("CPM-R300x600", """{"name":"CPM-R300x600","shape":"rectangle","material":"CPM-SAP-C30","depth":0.6,"width":0.3}"""),
            ("CPM-D500", """{"name":"CPM-D500","shape":"circle","material":"CPM-SAP-C30","diameter":0.5}"""),
            ("CPM-I400", """{"name":"CPM-I400","shape":"i_section","material":"CPM-SAP-S355","depth":0.4,"width":0.2,"flange_thickness":0.015,"web_thickness":0.01}"""),
            ("CPM-T200", """{"name":"CPM-T200","shape":"tube","material":"CPM-SAP-S355","depth":0.2,"width":0.2,"flange_thickness":0.01,"web_thickness":0.01}"""),
            ("CPM-P219", """{"name":"CPM-P219","shape":"pipe","material":"CPM-SAP-S355","diameter":0.219,"wall_thickness":0.008}"""),
        })
            ctx.Tool("add_frame_section", json, _ => FieldContext.Expect(link.FrameSections().Contains(name), $"the section {name} is not in SAP2000"), $"tool add_frame_section {name}");
        ctx.Tool("add_area_section", """{"name":"CPM-SH200","material":"CPM-SAP-C30","thickness":0.2,"type":"shell_thin"}""", _ => FieldContext.Expect(link.AreaSections().Contains("CPM-SH200"), "the area section is not in SAP2000"));

        // ---- a small frame of its own, each member read back ----
        List<string> joints = [], columns = [], beams = [], areas = [], spare = [];
        ctx.Tool("add_joints", """{"points":[[30,0,0],[36,0,0],[36,5,0],[30,5,0],[48,0,0]]}""", a =>
        {
            joints = Names(a, "joints");
            FieldContext.Expect(joints.Count == 5 && joints.All(j => link.Points().Any(p => p.Name == j)), "5 joints were asked for: " + a.ToJsonString());
        });
        ctx.Tool("add_frames", """{"section":"CPM-R300x600","lines":[[30,0,0,30,0,3],[36,0,0,36,0,3],[36,5,0,36,5,3],[30,5,0,30,5,3]]}""", a =>
        {
            columns = Names(a, "frames");
            var made = link.Frames().Where(f => columns.Contains(f.Name)).ToList();
            FieldContext.Expect(made.Count == 4 && made.All(f => f.Kind == "column" && f.Section == "CPM-R300x600"), "4 columns with CPM-R300x600 were expected; SAP2000 has: " + string.Join("; ", made.Select(f => $"{f.Name} {f.Kind} {f.Section}")));
        }, "tool add_frames by coordinates");
        var tops = Read(ctx, "the joints at the top of the new columns", () => link.Points().Where(p => At(p.Z, 3) && p.X > 29.9 && p.X < 36.1).OrderBy(p => p.Y).ThenBy(p => p.X).Select(p => p.Name).ToList(), []);
        if (tops.Count == 4)
            ctx.Tool("add_frames", $$"""{"section":"CPM-I400","joints":[[{{Q(tops[0])}},{{Q(tops[1])}}],[{{Q(tops[1])}},{{Q(tops[3])}}],[{{Q(tops[3])}},{{Q(tops[2])}}],[{{Q(tops[2])}},{{Q(tops[0])}}]]}""", a =>
            {
                beams = Names(a, "frames");
                var made = link.Frames().Where(f => beams.Contains(f.Name)).ToList();
                FieldContext.Expect(made.Count == 4 && made.All(f => f.Kind == "beam" && f.Section == "CPM-I400" && At(f.Z1, 3)), "4 beams at z = 3 with CPM-I400 were expected; SAP2000 has: " + string.Join("; ", made.Select(f => $"{f.Name} {f.Kind} {f.Section} {f.Z1}")));
            }, "tool add_frames by joint names");
        else ctx.Fail("tool add_frames by joint names", $"the 4 column tops were expected at z = 3; SAP2000 has {tops.Count} joints there");
        ctx.Tool("add_areas", """{"section":"CPM-SH200","outlines":[[[30,0,3],[36,0,3],[36,5,3],[30,5,3]]]}""", a =>
        {
            areas = Names(a, "areas");
            var made = link.Areas().Where(x => areas.Contains(x.Name)).ToList();
            FieldContext.Expect(made.Count == 1 && made[0].Section == "CPM-SH200" && made[0].Corners.Count == 4, "one area with 4 corners and CPM-SH200 was expected");
        });
        ctx.Tool("add_frames", """{"section":"CPM-P219","lines":[[48,0,0,48,0,3]]}""", a => { spare = Names(a, "frames"); FieldContext.Expect(spare.Count == 1, "one frame was asked for: " + a.ToJsonString()); }, "tool add_frames, one to delete again");
        if (beams.Count > 0)
            ctx.Tool("set_section", $$"""{"frames":[{{Q(beams[0])}}],"section":"CPM-T200"}""", _ => FieldContext.Expect(link.Frames().First(f => f.Name == beams[0]).Section == "CPM-T200", "the frame does not have CPM-T200 when read back from SAP2000"), "tool set_section on a frame");
        if (areas.Count > 0)
            ctx.Tool("set_section", $$"""{"areas":[{{Q(areas[0])}}],"section":"CPM-S180"}""", _ => FieldContext.Expect(link.Areas().First(x => x.Name == areas[0]).Section == "CPM-S180", "the area does not have CPM-S180 when read back from SAP2000"), "tool set_section on an area");

        // ---- supports, loads, releases ----
        var feet = joints.Take(4).ToList();
        if (feet.Count == 4)
        {
            ctx.Tool("set_restraints", $$"""{"type":"pinned","joints":{{List(feet)}}}""", _ => FieldContext.Expect(feet.All(j => link.Restraint(j).SequenceEqual([true, true, true, false, false, false])), "not pinned when read back from SAP2000"), "tool set_restraints pinned at named joints");
            ctx.Tool("set_restraints", $$"""{"dofs":["UX","UY","UZ","RZ"],"joints":[{{Q(feet[0])}}]}""", _ => FieldContext.Expect(link.Restraint(feet[0]).SequenceEqual([true, true, true, false, false, true]), "UX UY UZ RZ were set; SAP2000 has " + string.Join(" ", link.Restraint(feet[0]))), "tool set_restraints by degrees of freedom");
        }
        ctx.Tool("set_restraints", """{"type":"fixed","at_lowest":true}""", _ => FieldContext.Expect(feet.All(j => link.Restraint(j).All(r => r)), "the lowest joints are not fixed when read back from SAP2000"), "tool set_restraints fixed at the lowest joints");
        if (joints.Count == 5)
        {
            ctx.Tool("set_restraints", $$"""{"type":"roller","joints":[{{Q(joints[4])}}]}""", _ => FieldContext.Expect(link.Restraint(joints[4]).SequenceEqual([false, false, true, false, false, false]), "a roller holds UZ only; SAP2000 has " + string.Join(" ", link.Restraint(joints[4]))), "tool set_restraints roller");
            ctx.Tool("set_restraints", """{"type":"fixed","z":0}""", _ => FieldContext.Expect(link.Restraint(joints[4]).All(r => r), "the joint at z = 0 is not fixed when read back"), "tool set_restraints at a level");
        }
        ctx.Tool("add_load_pattern", """{"name":"CPM-TEST","type":"Live","self_weight":0}""", _ => FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-TEST") && link.LoadCases().Contains("CPM-TEST"), "the pattern CPM-TEST, or its load case, is not in SAP2000"));
        if (beams.Count > 1 && areas.Count > 0)
        {
            ctx.Tool("set_loads", $$"""{"frames":{{List(beams)}},"areas":{{List(areas)}},"pattern":"CPM-TEST","value":3}""", a => FieldContext.Expect(Int(a["done"]) == beams.Count + areas.Count && a["failed"]!.AsArray().Count == 0, "not every object was loaded: " + a.ToJsonString()));
            ctx.Tool("set_member_loads", $$"""{"frames":[{{Q(beams[1])}}],"pattern":"CPM-TEST","value":2,"direction":"x"}""",
                _ => FieldContext.Expect(FrameLoads(link, beams[1]).Any(l => At(Math.Abs(l), 2)), "no load of 2 in CPM-TEST on the frame when read back from SAP2000: " + string.Join(", ", FrameLoads(link, beams[1]))),
                "tool set_member_loads on a frame, along x");
            ctx.Tool("set_member_loads", $$"""{"areas":{{List(areas)}},"pattern":"CPM-TEST","value":1,"direction":"local_3","add":true}""",
                _ => FieldContext.Expect(AreaLoads(link, areas[0]).Any(l => At(Math.Abs(l), 1)), "no load of 1 in CPM-TEST on the area when read back from SAP2000: " + string.Join(", ", AreaLoads(link, areas[0]))),
                "tool set_member_loads on an area, local 3");
            ctx.Tool("set_releases", $$"""{"frames":[{{Q(beams[1])}}],"type":"pinned","ends":"j"}""", _ =>
            {
                var (i, j) = Releases(link, beams[1]);
                FieldContext.Expect(j[4] && j[5] && !i.Any(r => r), $"R2 and R3 released at the j end only were expected; SAP2000 has i [{string.Join(" ", i)}], j [{string.Join(" ", j)}]");
            }, "tool set_releases pinned at one end");
            ctx.Tool("set_releases", $$"""{"frames":[{{Q(beams[1])}}],"type":"fixed","ends":"both"}""", _ =>
            {
                var (i, j) = Releases(link, beams[1]);
                FieldContext.Expect(!i.Any(r => r) && !j.Any(r => r), $"no releases were expected; SAP2000 has i [{string.Join(" ", i)}], j [{string.Join(" ", j)}]");
            }, "tool set_releases fixed again");
        }
        else ctx.Fail("tools set_loads, set_member_loads and set_releases", "the beams and the area to load were not added");
        if (tops.Count > 0)
            ctx.Tool("set_joint_loads", $$"""{"joints":[{{Q(tops[0])}}],"pattern":"CPM-TEST","fz":-5,"fx":1,"my":0.5}""", _ =>
            {
                var f = JointLoad(link, tops[0]);
                FieldContext.Expect(f is { } v && At(v[0], 1) && At(v[2], -5) && At(v[4], 0.5), $"F1 = 1, F3 = -5 and M2 = 0.5 in CPM-TEST were expected; SAP2000 has {(f is null ? "no load" : string.Join(" ", f))}");
            });
        var first = Read(ctx, "the load cases", () => link.LoadCases().FirstOrDefault(c => c.Equals("Dead", StringComparison.OrdinalIgnoreCase)) ?? link.LoadCases().FirstOrDefault(), null) ?? "DEAD";
        ctx.Tool("add_combination", $$"""{"name":"CPM-TEST-COMBO","cases":[{"case":{{Q(first)}},"factor":1.2},{"case":"CPM-TEST","factor":1.6}]}""", _ => FieldContext.Expect(link.Combos().Contains("CPM-TEST-COMBO"), "the combination is not in SAP2000"));

        // ---- reading ----
        if (columns.Count > 0 && areas.Count > 0)
        {
            ctx.Tool("select_objects", $$"""{"frames":[{{Q(columns[0])}}],"areas":[{{Q(areas[0])}}]}""", _ => FieldContext.Expect(link.Selected().Count == 2, $"2 objects were selected; SAP2000 has {link.Selected().Count}"));
            ctx.Tool("query_objects", """{"selected_only":true}""", a => FieldContext.Expect(Int(a["count"]) == 2, $"2 objects are selected; the answer has {a["count"]}"), "tool query_objects, the selection only");
            ctx.Tool("query_joints", """{"selected_only":true}""", null, "tool query_joints, the selection only");
            ctx.Tool("select_objects", "{}", _ => FieldContext.Expect(link.Selected().Count == 0, "the selection was not cleared"), "tool select_objects with nothing clears the selection");
        }
        ctx.Tool("query_objects", """{"section":"CPM-R300x600"}""", a => FieldContext.Expect(Int(a["count"]) == 4, $"4 frames with CPM-R300x600 were expected: {a["count"]}"), "tool query_objects by section");
        ctx.Tool("list_tables", "{}", a => FieldContext.Expect(Int(a["count"]) >= 3, $"SAP2000's tables with data were expected: {a["count"]}"));
        if (Read(ctx, "the tables", () => link.Tables().FirstOrDefault(t => !t.Empty), null) is { } table)
            ctx.Tool("read_table", $$"""{"table":{{Q(table.Key)}},"limit":3}""", a => FieldContext.Expect(a["fields"] is JsonArray { Count: > 0 }, "the table has no fields"), $"tool read_table {table.Name}");
        else ctx.Fail("tool read_table", "SAP2000 listed no table with data");

        // ---- what SAP2000 has no stories for ----
        foreach (var name in new[] { "add_columns", "add_beams", "add_slabs", "add_walls", "check_drift", "check_torsion", "check_story_irregularity" }) NotHere(ctx, name, "SAP2000 has no stories");
        if (feet.Count > 0)
            ctx.Tool("set_supports", $$"""{"type":"fixed","points":[{{Q(feet[0])}}]}""", _ => FieldContext.Expect(link.Restraint(feet[0]).All(r => r), "the joint is not fixed when read back from SAP2000"), "tool set_supports at a named joint");
        else ctx.Tool("set_supports", """{"type":"fixed","at_base":true}""", null, "tool set_supports (the shared tool, as SAP2000 answers it)");

        // ---- deleting, a snapshot, saving ----
        if (spare.Count > 0)
            ctx.Tool("delete_objects", $$"""{"frames":{{List(spare)}}}""", _ => FieldContext.Expect(!link.Frames().Any(f => f.Name == spare[0]), "the deleted frame is still in SAP2000"));
        else ctx.Fail("tool delete_objects", "the frame to delete was not added");
        var loose = Read(ctx, "the joints the deleted frame left", () => link.Points().Where(p => At(p.X, 48)).Select(p => p.Name).ToList(), []);
        if (loose.Count > 0)
            ctx.Tool("delete_joints", $$"""{"joints":{{List(loose)}}}""", _ => FieldContext.Expect(!link.Points().Any(p => At(p.X, 48)), "the joints at x = 48 are still in SAP2000"));
        else ctx.Tool("delete_joints", """{"joints":["no such joint"]}""", null, "tool delete_joints (the spare frame's joints went with it)");
        ctx.Tool("model_snapshot", """{"name":"cpm-before-analysis"}""");
        ctx.Tool("save_model", "{}", a => FieldContext.Expect(a["saved"]!.GetValue<string>().Replace('\\', '/').Split('/').Last().StartsWith(ModelerFieldSteps.ModelName, StringComparison.OrdinalIgnoreCase), $"it saved '{a["saved"]}', which is not the scratch model"));

        // ---- SAP2000's own analysis and design ----
        // What the Viewer needs to show every display: a spring, a link, a section cut, a pushover and a history case.
        ScratchExtras.Add(ctx);
        NoVerdictBeforeAnalysis(ctx);
        ctx.Tool("run_analysis", "{}", _ => FieldContext.Expect(link.Info().Locked, "the analysis answered, but SAP2000 does not say the model is analysed (locked)"));
        var analysed = false;
        try { analysed = link.Info().Locked; }
        catch (EtabsException) { }
        // Once SAP2000 has analysed and designed, each result must hold numbers; without an analysis each tool must still answer.
        Action<JsonNode>? Has(Func<JsonNode, bool> holds, string what) => analysed ? a => FieldContext.Expect(holds(a), what + ": " + Short(a)) : null;
        ctx.Tool("run_design", """{"steel":true,"concrete":true}""", Has(a => a["designed"] is JsonArray { Count: > 0 }, "SAP2000 designed nothing"));
        var none = analysed ? "" : " (no analysis: it must still answer)";
        ctx.Tool("joint_displacements", """{"limit":5}""", Has(a => Int(a["rows"]) > 0 && a["list"] is JsonArray { Count: > 0 }, "no joint displacements after the analysis"), "tool joint_displacements" + none);
        ctx.Tool("frame_forces", """{"limit":5}""", Has(a => Int(a["rows"]) > 0, "no frame forces after the analysis"), "tool frame_forces" + none);
        ctx.Tool("base_reactions", """{"per_joint":true}""", Has(a =>
            a["base_reactions"] is JsonArray { Count: > 0 } total && a["joint_reactions"] is JsonArray { Count: > 0 }
            && total.Any(r => r?["case"]?.GetValue<string>() == "CPM-TEST" && (Math.Abs(Num(r["FX"])) + Math.Abs(Num(r["FZ"]))) > 1e-6),
            "base reactions for every case, reactions at the supports, and a reaction to the loads in CPM-TEST were expected"), "tool base_reactions" + none);
        ctx.Tool("modal_results", "{}", Has(a => a["modes"] is JsonArray { Count: > 0 } && Num(a["first_period"]) > 0, "modes with a first period above 0 were expected"), "tool modal_results" + none);
        ctx.Tool("check_displacement", """{"lateral_limit":500,"vertical_limit":0.02}""", Has(a => a["lateral"]?["largest"] is not null && a["vertical"]?["passes"] is not null, "the lateral largest and the vertical check were expected"), "tool check_displacement" + none);
        ctx.Tool("design_ratios", """{"limit":10}""", Has(a => Int(a["steel"]?["members"]) > 0 || Int(a["concrete_columns"]?["members"]) > 0 || Int(a["concrete_beams"]?["members"]) > 0,
            "design results for the steel beams or the concrete columns were expected"), "tool design_ratios" + none);
        JsonObject? report = null;
        ctx.Tool("check_report", """{"lateral_limit":500}""", a => report = a["checks"] as JsonObject, "tool check_report" + none);
        if (analysed && report is not null)
            Verdicts(ctx, "every SAP2000 check had its results to read", report.Select(c => (c.Key, c.Value)).ToList());
        ctx.Tool("results_overview", "{}", null, "tool results_overview (the ETABS tool, as SAP2000 answers it)"); // ETABS itself
        ctx.Tool("check_modal", "{}", null, "tool check_modal (the ETABS tool, as SAP2000 answers it)"); // ETABS itself
        if (analysed) ScratchExtras.Check(ctx);
        if (analysed) ctx.Step("SAP2000 tables with data after the analysis and design", () => link.Tables().Where(t => !t.Empty).Select(t => t.Name).ToList());

        // ---- snapshots and the review pack ----
        ctx.Tool("list_snapshots", "{}", a => FieldContext.Expect(Int(a["count"]) >= 1, "the snapshot is not listed"));
        ctx.Tool("compare_snapshots", """{"from":"cpm-before-analysis"}""");
        // Left out of a rerun of what failed, as in ETABS (Ahmed 2026-11-27: what he says of ETABS holds for all three).
        if (ctx.NoPack) ctx.LeftOutOfRerun("the review pack of the scratch model is written");
        else
        {
            // Read from a copy in a fresh hidden SAP2000, never in this long session: SAP2000 26.3 corrupted its heap and
            // died reading result tables here four times (0.4.58, 0.4.59, 0.4.60, 0.4.64), while a fresh process read them
            // all (the Engine's runs). It is also the way a pack is written after a run, so the 3 minutes cover it all.
            var pack = PackSpeedField.FromCopy(ctx, link, "the review pack of the scratch model is written (from a copy in a hidden SAP2000, closed after, within 3 minutes)");
            if (pack is not null)
                ctx.Step("the pack from the copy holds the scratch model's frames and areas", () =>
                {
                    FieldContext.Expect(pack.Frames == link.Frames().Count && pack.Areas == link.Areas().Count, $"the pack holds {pack.Frames} frames and {pack.Areas} areas, the model has {link.Frames().Count} and {link.Areas().Count}");
                    return new { frames = pack.Frames, areas = pack.Areas, tables = pack.Tables, notes = pack.Notes };
                });
        }

        // ---- the tools every program has through the same API (the whole API, lateral loads, checkpoints, a building) ----
        CsiFieldSteps.Run(ctx);

        // (No table-speed probe in SAP2000's long session: its reads take about 0.1 s each (0.4.64), and reading result
        // tables in this session is what corrupted its heap; its pack is timed from a copy above.)

        // ---- an engineer's SAP2000 that opened an .s2k (last: from here on this window takes no table import itself) ----
        // SAP2000 26 refuses every table import in a session that opened an .s2k (experiment N, 2026-11-26), as an
        // engineer's window is after File > Open of the Modeler's .s2k: set_lateral_load must still give a code that has
        // no API call, by making the change in a second SAP2000 (SapFreshProcess).
        if (link is not OfflineEtabs and not MemoryEtabs)
        {
            var own = Read(ctx, "the scratch model's file", () => link.Info().File, "");
            var tiny = Path.Combine(ctx.Folder, "cpm-field-test-text.s2k");
            if (own.Length > 0 && ctx.Step("the scratch SAP2000 opens an .s2k, then the scratch model again (as an engineer's window that opened the Modeler's .s2k)", () =>
            {
                File.WriteAllText(tiny, TinyS2k);
                link.Raw("save the scratch model", m => m.File.Save(own));
                link.Raw("open an .s2k text file", m => m.File.OpenFile(tiny));
                link.Raw("open the scratch model again", m => m.File.OpenFile(own));
                link.Refresh();
                FieldContext.Expect(ScratchProgram.SameName(link.Info().File, own), $"SAP2000 has {link.Info().File} open, not the scratch model");
                return Path.GetFileName(own);
            }) is not null)
                ctx.Tool("set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"Dominican Republic R-001"}""", a =>
                    FieldContext.Expect(a["read_back"] is JsonObject { Count: > 0 } && a["second_sap2000"] is not null && link.LoadPatterns().Count > 1,
                        "Dominican Republic R-001 should be given in a second SAP2000 and read back here, with the other patterns kept: " + a.ToJsonString()),
                    "tool set_lateral_load in a SAP2000 that opened an .s2k: the change is made in a second SAP2000");
        }

        ctx.Coverage("every SAP2000 tool was run");
    }

    /// <summary>A two-joint, two-pattern .s2k: opening it is enough for SAP2000 26 to refuse every later table import (experiment N5).</summary>
    internal const string TinyS2k = """
        File cpm-field-test-text.s2k

        TABLE:  "PROGRAM CONTROL"
           ProgramName=SAP2000   Version=26.3.0   CurrUnits="KN, m, C"   SteelCode="AISC 360-16"   ConcCode="ACI 318-19"

        TABLE:  "JOINT COORDINATES"
           Joint=1   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0   Y=0   Z=0   SpecialJt=No
           Joint=2   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0   Y=0   Z=3   SpecialJt=No

        TABLE:  "LOAD PATTERN DEFINITIONS"
           LoadPat=DEAD   DesignType=Dead   SelfWtMult=1
           LoadPat=CPM-AUTO-S   DesignType=Quake   SelfWtMult=0

        END TABLE DATA
        """;
}
