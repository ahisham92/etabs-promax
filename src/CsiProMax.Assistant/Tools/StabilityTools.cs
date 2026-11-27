using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Checks.Stability;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The model checker for what stops an analysis from completing (CsiProMax.Checks.Stability), as a tool of the chat,
/// the Modeler and the Viewer: on the Modeler's model, and on the model open in ETABS, SAP2000 or SAFE, read through
/// the program's API. Nothing is run and nothing is changed; the answer names each problem's objects, where they are
/// (for the 3D view to mark) and what to do.
/// </summary>
public static class StabilityTools
{
    public const string Name = "find_instabilities";

    public static IReadOnlyList<Tool> All { get; } =
    [
        new(Name,
            "Checks the model for what stops its analysis from completing or makes the program call it unstable, without running it: members or areas with no section, a section or material with no stiffness, members too short, joints, members and areas on top of each other, parts that float free of the supports, no supports at all, releases that leave a member or joint free (torsion or axial at both ends), members that pass a joint without joining it (SAP2000), areas that will not mesh, load cases and combinations that name what is not there, and a modal case with no mass. Then a light stiffness check finds mechanisms (a joint or a rigid floor that can move with nothing resisting it). Each finding gives its objects, where they are and a fix. Works on the Modeler's model and on the model open in ETABS, SAP2000 or SAFE. Run it before an analysis, and when an analysis stops or warns of instability. With select true, the objects of the errors are selected in the program.", // ETABS itself
            """
            {"type":"object","properties":{
              "stiffness":{"type":"boolean","description":"Also run the stiffness check (default true). It takes seconds on a building of a few thousand joints."},
              "select":{"type":"boolean","description":"Select the objects of the errors in the program (not in the Modeler), to see them there."},
              "most":{"type":"integer","description":"The most findings to list (default 200)."}}}
            """, Ask.Never, Run, _ => "Check the model for instabilities"),
    ];

    private static JsonNode Run(JsonElement a, ICsiLink e)
    {
        var clock = Stopwatch.StartNew();
        var model = Read(e);
        var report = StabilityCheck.Run(model, new StabilityCheck.Options(Stiffness: a.Flag("stiffness", true), MostFindings: Math.Clamp(a.Whole("most", 200), 1, 1000)));
        var answer = Json(report, e.Program, e is OfflineEtabs);
        answer["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
        if (a.Flag("select") && e is not OfflineEtabs && report.Errors > 0)
        {
            var picked = 0;
            try
            {
                e.ClearSelection();
                foreach (var o in report.Findings.Where(f => f.Level == Level.Error).SelectMany(f => f.Objects).Distinct().Take(500))
                {
                    var kind = o.Kind switch { "joint" => "point", "shell" => "area", _ => "frame" };
                    try { e.Select(kind, o.Name); picked++; }
                    catch (EtabsException) { }
                }
                e.Refresh();
            }
            catch (EtabsException ex) { answer["select_note"] = $"Selecting them did not work: {ex.Message}"; }
            answer["selected"] = picked;
        }
        return answer;
    }

    /// <summary>The 3D window's request (POST check): the tool's answer with ok true, or {"ok":false,"error":...}.</summary>
    public static JsonObject Window(ICsiLink link, byte[] body)
    {
        try
        {
            using var request = JsonDocument.Parse(body.Length == 0 ? "{}"u8.ToArray() : body);
            var answer = (JsonObject)Run(request.RootElement.Clone(), link);
            answer["ok"] = true;
            return answer;
        }
        catch (Exception ex) when (ex is EtabsException or ToolInputException or JsonException or InvalidOperationException)
        {
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>The answer the chat, the Modeler and the Viewer all read.</summary>
    public static JsonObject Json(StabilityCheck.Report r, CsiProgram program, bool modeler)
    {
        var where = modeler ? "the Modeler" : program.Name;
        var summary = r.Errors == 0 && r.Warnings == 0 ? $"No problems found: nothing here should stop the analysis{(r.StiffnessRan ? ", and the stiffness check found no mechanism" : "")}."
            : r.Errors == 0 ? $"No errors: the analysis should complete. {r.Warnings} warning(s) deserve a look."
            : $"{r.Errors} error(s) would stop the analysis or make it unstable, and {r.Warnings} warning(s). Fix the errors first: the list starts with them.";
        return new JsonObject
        {
            ["summary"] = summary,
            ["errors"] = r.Errors, ["warnings"] = r.Warnings,
            ["checked"] = new JsonObject
            {
                ["in"] = where, ["joints"] = r.Joints, ["frames"] = r.Frames, ["areas"] = r.Shells, ["parts"] = r.Parts,
                ["stiffness_check"] = r.StiffnessRan, ["equations"] = r.Equations,
            },
            ["findings"] = new JsonArray(r.Findings.Select(f => (JsonNode)new JsonObject
            {
                ["level"] = f.Level.ToString().ToLowerInvariant(), ["check"] = f.Check, ["message"] = f.Message, ["fix"] = f.Fix,
                ["objects"] = new JsonArray(f.Objects.Select(o => (JsonNode)new JsonObject { ["kind"] = o.Kind, ["name"] = o.Name, ["story"] = o.Story }).ToArray()),
                ["at"] = new JsonArray(f.At.Select(p => (JsonNode)new JsonArray(R(p.X), R(p.Y), R(p.Z))).ToArray()),
            }).ToArray()),
            ["notes"] = new JsonArray(r.Notes.Select(n => (JsonNode)n).ToArray()),
            ["next"] = r.Errors > 0 ? "Fix the errors (each says how), then check again before running the analysis." : "Run the analysis.",
        };
    }

    private static double R(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0;

    /// <summary>The model as the checker sees it: the Modeler's own, or read from the program behind the link.</summary>
    public static AnalysisModel Read(ICsiLink e) => e is OfflineEtabs o ? FromStructural.Read(o.Model, "modeler") : Live.Read(e);

    /// <summary>
    /// Reads a model open in ETABS, SAP2000 or SAFE through the API calls they share: joints and their supports and
    /// springs, frames with their ends, releases and sections, areas with their corners, the sections' stiffness, the
    /// materials, diaphragms, load cases, combinations and the mass source. Each part is read on its own, and one the
    /// program does not answer is left out and said in the notes. A link with no API behind it (the demo) gives its
    /// geometry, joined by position.
    /// </summary>
    internal static class Live
    {
        /// <summary>Above this many objects, what is read object by object (releases, springs) is left out, said in the notes.</summary>
        public const int MostOneByOne = 20000;

        public static AnalysisModel Read(ICsiLink e)
        {
            var info = e.Info();
            var program = e.Program.Key;
            var a = new AnalysisModel
            {
                Program = program, LengthUnit = info.Length, ForceUnit = info.Force,
                JoinsAtIntersections = program != CsiProgram.Sap2000.Key, PlateOnly = program == CsiProgram.Safe.Key,
            };
            var points = e.Points();
            var stories = e.Stories();
            string? StoryAt(double z) => stories.Count == 0 ? null : stories.MinBy(s => Math.Abs(s.Elevation - z)) is { } s && Math.Abs(s.Elevation - z) < 1e-3 * Math.Max(1, Math.Abs(z)) ? s.Name : null;
            var api = HasApi(e);
            if (!api) a.Notes.Add($"This link has no {e.Program.Name} API behind it, so releases, springs and diaphragms were not read; members are joined by position.");

            // Joints and supports.
            var restraints = new Dictionary<string, bool[]>(StringComparer.Ordinal);
            var springs = new HashSet<string>(StringComparer.Ordinal);
            var diaphragms = new Dictionary<string, string>(StringComparer.Ordinal);
            var many = points.Count > MostOneByOne;
            if (many) a.Notes.Add($"The model has {points.Count} joints: supports were read, springs and diaphragms joint by joint were left out.");
            foreach (var p in points)
            {
                try { restraints[p.Name] = e.Restraint(p.Name); }
                catch (EtabsException) { }
                if (!api || many) continue;
                if (CsiTools.Try(e, "read a joint's spring", "PointObj.GetSpring", CsiTools.D(("Name", p.Name))) is { } sp && ApiCall.Value(sp, "k") is JsonArray k && k.Any(v => ApiCall.Number(v) is > 0))
                    springs.Add(p.Name);
                // ETABS: a named point spring property (Assign > Joint > Springs) is not in GetSpring's numbers.
                else if (program == CsiProgram.Etabs.Key && CsiTools.Try(e, "read a joint's spring property", "PointObj.GetSpringAssignment", CsiTools.D(("Name", p.Name))) is { } spa
                         && ApiCall.Value(spa, "SpringProp")?.ToString() is { Length: > 0 } spn && spn != "None")
                    springs.Add(p.Name);
                if (program == CsiProgram.Etabs.Key && CsiTools.Try(e, "read a joint's diaphragm", "PointObj.GetDiaphragm", CsiTools.D(("Name", p.Name))) is { } dp
                    && ApiCall.Value(dp, "DiaphragmName")?.ToString() is { Length: > 0 } dn && !(ApiCall.Value(dp, "DiaphragmOption")?.ToString() ?? "").Contains("Disconnect", StringComparison.OrdinalIgnoreCase))
                    diaphragms[p.Name] = dn;
            }
            if (restraints.Count == 0 && points.Count > 0) a.Notes.Add("The supports could not be read.");

            // Frames, by their end joints.
            var byPosition = new Dictionary<(long, long, long), string>();
            var tol = a.Tolerance;
            foreach (var p in points) byPosition.TryAdd(Key(p.X, p.Y, p.Z, tol), p.Name);
            string? At(double x, double y, double z) => byPosition.TryGetValue(Key(x, y, z, tol), out var n) ? n : null;
            var ends = api ? FrameEnds(e, a) : [];
            var frames = e.Frames();
            var manyFrames = frames.Count > MostOneByOne;
            if (manyFrames) a.Notes.Add($"The model has {frames.Count} frames: their releases were not read one by one.");
            foreach (var f in frames)
            {
                var (i, j) = ends.TryGetValue(f.Name, out var pair) ? pair : (At(f.X1, f.Y1, f.Z1) ?? "", At(f.X2, f.Y2, f.Z2) ?? "");
                var releases = new bool[12];
                var meshed = false;
                var lineSpring = false;
                if (api && !manyFrames)
                {
                    // A line spring (Assign > Frame > Line Springs) holds the frame along its length.
                    if (CsiTools.Try(e, "read a frame's line spring", "FrameObj.GetSpringAssignment", CsiTools.D(("Name", f.Name))) is { } fs
                        && ApiCall.Value(fs, "SpringProp")?.ToString() is { Length: > 0 } fsp && fsp != "None")
                        lineSpring = true;
                    if (CsiTools.Try(e, "read a frame's releases", "FrameObj.GetReleases", CsiTools.D(("Name", f.Name))) is { } rel)
                    {
                        if (ApiCall.Value(rel, "II") is JsonArray ii) for (var k = 0; k < 6 && k < ii.Count; k++) releases[k] = ii[k]?.GetValue<bool>() == true;
                        if (ApiCall.Value(rel, "JJ") is JsonArray jj) for (var k = 0; k < 6 && k < jj.Count; k++) releases[6 + k] = jj[k]?.GetValue<bool>() == true;
                    }
                    if (program == CsiProgram.Sap2000.Key && CsiTools.Try(e, "read a frame's mesh", "FrameObj.GetAutoMesh", CsiTools.D(("Name", f.Name))) is { } mesh)
                        meshed = ApiCall.Value(mesh, "AutoMesh")?.ToString() == "true" && ApiCall.Value(mesh, "AutoMeshAtPoints")?.ToString() != "false";
                }
                var story = f.Story.Length > 0 ? f.Story : null;
                a.Frames.Add(new Frame(f.Name, f.Name, story, f.Kind, i, j, f.Section.Length > 0 ? f.Section : null, releases, meshed || program != CsiProgram.Sap2000.Key, lineSpring));
            }

            // Areas, by their corners.
            var areas = e.Areas();
            foreach (var ar in areas)
            {
                List<string> corners = [];
                if (api && CsiTools.Try(e, "read an area's corners", "AreaObj.GetPoints", CsiTools.D(("Name", ar.Name))) is { } got)
                    corners = CsiTools.Names(got, "Point");
                if (corners.Count == 0) corners = ar.Corners.Select(c => At(c.X, c.Y, c.Z) ?? "").ToList();
                string? diaphragm = null; var spring = false;
                if (api && areas.Count <= MostOneByOne)
                {
                    if (program == CsiProgram.Etabs.Key && CsiTools.Try(e, "read an area's diaphragm", "AreaObj.GetDiaphragm", CsiTools.D(("Name", ar.Name))) is { } d && ApiCall.Value(d, "DiaphragmName")?.ToString() is { Length: > 0 } dn && dn != "None")
                        diaphragm = dn;
                    if (CsiTools.Try(e, "read an area's springs", "AreaObj.GetSpringAssignment", CsiTools.D(("Name", ar.Name))) is { } s && ApiCall.Value(s, "SpringProp")?.ToString() is { Length: > 0 } sp2 && sp2 != "None")
                        spring = true;
                }
                a.Shells.Add(new Shell(ar.Name, ar.Name, null, ar.Kind, corners, ar.Section.Length > 0 ? ar.Section : null, spring, diaphragm));
                if (diaphragm is not null) foreach (var c in corners) diaphragms.TryAdd(c, diaphragm);
            }

            // Links (one joint: to the ground) and, in SAP2000, joint constraints: what they join counts as joined.
            if (api) LinksAndConstraints(e, a, program, diaphragms);

            // SAFE holds the far end of the columns and walls below a slab.
            var held = new HashSet<string>(StringComparer.Ordinal);
            if (program == CsiProgram.Safe.Key)
            {
                var at = points.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
                foreach (var f in a.Frames.Where(f => f.Kind == "column" && at.ContainsKey(f.I) && at.ContainsKey(f.J)))
                    held.Add(at[f.I].Z < at[f.J].Z ? f.I : f.J);
                foreach (var s in a.Shells.Where(s => s.Kind == "wall"))
                {
                    var zs = s.Joints.Where(at.ContainsKey).Select(n => at[n].Z).ToList();
                    if (zs.Count > 0) foreach (var n in s.Joints.Where(n => at.ContainsKey(n) && Math.Abs(at[n].Z - zs.Min()) < 1e-6)) held.Add(n);
                }
                if (held.Count > 0) a.Notes.Add("SAFE holds the far end of each column and wall below a slab; those ends count as supports here.");
            }
            foreach (var p in points)
            {
                var r = restraints.GetValueOrDefault(p.Name) ?? new bool[6];
                if (held.Contains(p.Name)) r = [true, true, true, true, true, true];
                a.Joints.Add(new Joint(p.Name, p.Name, StoryAt(p.Z), p.X, p.Y, p.Z, r, springs.Contains(p.Name), diaphragms.GetValueOrDefault(p.Name)));
            }

            if (api)
            {
                Sections(e, a, frames.Select(f => f.Section).Concat(areas.Select(x => x.Section)));
                Diaphragms(e, a, diaphragms.Values.Distinct());
                Cases(e, a);
            }
            else
            {
                foreach (var s in e.FrameSections()) a.FrameProps[s] = new FrameProp(s, null, null, null, null, null);
                foreach (var s in e.AreaSections()) a.ShellProps[s] = new ShellProp(s, null, null);
            }
            foreach (var p in e.LoadPatterns()) a.Patterns.Add(new Pattern(p.Name, p.Type, p.SelfWeight, HasLoads: true));
            return a;
        }

        private static void LinksAndConstraints(ICsiLink e, AnalysisModel a, string program, Dictionary<string, string> diaphragms)
        {
            if (CsiTools.Try(e, "list the links", "LinkObj.GetNameList") is { } list)
                foreach (var name in CsiTools.Names(list, "MyName"))
                    if (CsiTools.Try(e, "read a link's joints", "LinkObj.GetPoints", CsiTools.D(("Name", name))) is { } pts
                        && ApiCall.Value(pts, "Point1")?.ToString() is { Length: > 0 } p1)
                    {
                        var p2 = ApiCall.Value(pts, "Point2")?.ToString();
                        a.Links.Add(new Link(name, p1, p2 is { Length: > 0 } && p2 != p1 ? p2 : null));
                    }
            if (program != CsiProgram.Sap2000.Key) return;
            // SAP2000's constraints, every joint's at once (ItemType Group, "All"). A diaphragm is a rigid floor; the
            // others (body, weld, equal...) join their joints.
            if (CsiTools.Try(e, "read the joints' constraints", "PointObj.GetConstraint", CsiTools.D(("Name", "All"), ("ItemType", 1))) is not { } got) return;
            var points = CsiTools.Names(got, "PointName");
            var names = CsiTools.Names(got, "ConstraintName");
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Type(string c) => types.TryGetValue(c, out var t) ? t
                : types[c] = CsiTools.Try(e, "read a constraint's type", "ConstraintDef.GetConstraintType", CsiTools.D(("Name", c))) is { } ct ? ApiCall.Value(ct, "ConstraintType")?.ToString() ?? "" : "";
            foreach (var g in points.Zip(names).GroupBy(x => x.Second, StringComparer.OrdinalIgnoreCase))
            {
                var joints = g.Select(x => x.First).Distinct(StringComparer.Ordinal).ToList();
                if (joints.Count < 2) continue;
                if (Type(g.Key) is "2" or "Diaphragm" or "CONSTRAINT_DIAPHRAGM")
                {
                    a.RigidDiaphragms.Add(g.Key);
                    foreach (var j in joints) diaphragms.TryAdd(j, g.Key);
                    continue;
                }
                foreach (var j in joints.Skip(1)) a.Links.Add(new Link(g.Key, joints[0], j, "constraint"));
            }
        }

        private static bool HasApi(ICsiLink e)
        {
            if (e is MemoryEtabs) return false;
            try { e.Raw("check the API is there", _ => null); return true; }
            catch (EtabsException) { return false; }
        }

        private static (long, long, long) Key(double x, double y, double z, double tol) =>
            ((long)Math.Round(x / tol), (long)Math.Round(y / tol), (long)Math.Round(z / tol));

        /// <summary>Each frame's two joints: all at once where the program can (ETABS, SAFE), else frame by frame.</summary>
        private static Dictionary<string, (string, string)> FrameEnds(ICsiLink e, AnalysisModel a)
        {
            var ends = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            try
            {
                var all = (Dictionary<string, (string, string)>?)e.Raw("read the frames' joints", m =>
                {
                    int n = 0; string[] names = [], sections = [], stories = [], p1 = [], p2 = [];
                    double[] x1 = [], y1 = [], z1 = [], x2 = [], y2 = [], z2 = [], angle = [], o1x = [], o2x = [], o1y = [], o2y = [], o1z = [], o2z = []; int[] cardinal = [];
                    var code = Convert.ToInt32(m.FrameObj.GetAllFrames(ref n, ref names, ref sections, ref stories, ref p1, ref p2, ref x1, ref y1, ref z1, ref x2, ref y2, ref z2,
                        ref angle, ref o1x, ref o2x, ref o1y, ref o2y, ref o1z, ref o2z, ref cardinal, "Global"));
                    if (code != 0) return null;
                    var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
                    for (var i = 0; i < n; i++) d[names[i]] = (p1[i], p2[i]);
                    return d;
                });
                if (all is not null) return all;
            }
            catch (Exception ex) when (ex is EtabsException or MissingMemberException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            foreach (var f in e.Frames())
                if (CsiTools.Try(e, "read a frame's joints", "FrameObj.GetPoints", CsiTools.D(("Name", f.Name))) is { } p
                    && ApiCall.Value(p, "Point1")?.ToString() is { Length: > 0 } i && ApiCall.Value(p, "Point2")?.ToString() is { Length: > 0 } j)
                    ends[f.Name] = (i, j);
            if (ends.Count == 0) a.Notes.Add("The frames' joints could not be read: frames are joined by position.");
            return ends;
        }

        private static void Sections(ICsiLink e, AnalysisModel a, IEnumerable<string> used)
        {
            var names = used.Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var frameSections = e.FrameSections();
            var areaSections = e.AreaSections();
            var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in frameSections)
            {
                if (!names.Contains(s)) { a.FrameProps[s] = new FrameProp(s, null, null, null, null, null); continue; }
                var p = CsiTools.Try(e, "read a section's stiffness", "PropFrame.GetSectProps", CsiTools.D(("Name", s)));
                var mat = CsiTools.Try(e, "read a section's material", "PropFrame.GetMaterial", CsiTools.D(("Name", s))) is { } mm ? ApiCall.Value(mm, "MatProp")?.ToString() : null;
                double? N(string key) => p is null ? null : ApiCall.Number(p, key) is var v && double.IsFinite(v) ? v : null;
                double modifier = 1;
                if (CsiTools.Try(e, "read a section's modifiers", "PropFrame.GetModifiers", CsiTools.D(("Name", s))) is { } md && ApiCall.Value(md, "Value") is JsonArray mods && mods.Count >= 6)
                {
                    var area = ApiCall.Number(mods[0]); var i2 = ApiCall.Number(mods[4]); var i3 = ApiCall.Number(mods[5]);
                    if (area <= 0 || i2 <= 0 && i3 <= 0) modifier = 0;
                }
                a.FrameProps[s] = new FrameProp(s, mat, N("Area"), N("I22"), N("I33"), N("Torsion"), Modifier: modifier);
                if (mat is { Length: > 0 }) materials.Add(mat);
            }
            foreach (var s in areaSections)
            {
                string? mat = null; double? thickness = null; var type = "Shell"; var kind = "Slab";
                foreach (var getter in new[] { "GetSlab", "GetWall", "GetShell_1", "GetDeck" })
                {
                    if (CsiTools.Try(e, "read an area section", "PropArea." + getter, CsiTools.D(("Name", s))) is not { } got) continue;
                    mat = ApiCall.Value(got, "MatProp")?.ToString() ?? ApiCall.Value(got, "SlabMaterial")?.ToString();
                    var t = ApiCall.Number(got, "Thickness");
                    if (double.IsFinite(t)) thickness = t;
                    var shell = ApiCall.Value(got, "ShellType")?.ToString() ?? "";
                    type = getter == "GetDeck" || shell.Contains("Membrane", StringComparison.OrdinalIgnoreCase) || shell == "5" && getter == "GetShell_1" || shell == "3" && getter != "GetShell_1" ? "Membrane"
                        : shell.Contains("Plate", StringComparison.OrdinalIgnoreCase) || getter == "GetShell_1" && shell is "3" or "4" ? "Plate" : "Shell";
                    kind = getter == "GetWall" ? "Wall" : "Slab";
                    break;
                }
                // SAFE's slabs are plates whatever their type says: only bending counts there.
                a.ShellProps[s] = new ShellProp(s, mat, thickness, a.PlateOnly ? "Plate" : type, kind);
                if (mat is { Length: > 0 }) materials.Add(mat);
            }
            foreach (var m in e.Materials())
            {
                if (!materials.Contains(m)) { a.Materials[m] = new MaterialProp(m, null, null); continue; }
                var iso = CsiTools.Try(e, "read a material's stiffness", "PropMaterial.GetMPIsotropic", CsiTools.D(("Name", m)));
                var w = CsiTools.Try(e, "read a material's weight", "PropMaterial.GetWeightAndMass", CsiTools.D(("Name", m)));
                double? Num(JsonObject? call, string key) => call is null ? null : ApiCall.Number(call, key) is var v && double.IsFinite(v) ? v : null;
                // A material that is not isotropic (rebar, a uniaxial one) has no GetMPIsotropic answer: its stiffness is not judged.
                a.Materials[m] = new MaterialProp(m, Num(iso, "E"), Num(w, "W"), Num(w, "M"));
            }
        }

        private static void Diaphragms(ICsiLink e, AnalysisModel a, IEnumerable<string> names)
        {
            foreach (var d in names)
            {
                var semi = CsiTools.Try(e, "read a diaphragm", "Diaphragm.GetDiaphragm", CsiTools.D(("Name", d))) is { } got && ApiCall.Value(got, "SemiRigid")?.ToString() == "true";
                if (!semi) a.RigidDiaphragms.Add(d);
            }
        }

        private static void Cases(ICsiLink e, AnalysisModel a)
        {
            foreach (var name in e.LoadCases())
            {
                var type = CsiTools.Try(e, "read a load case's type", "LoadCases.GetTypeOAPI", CsiTools.D(("Name", name))) is { } t ? ApiCall.Value(t, "CaseType")?.ToString() ?? "" : "";
                type = type switch
                {
                    "1" or "LinearStatic" => "Linear Static", "2" or "NonlinearStatic" => "Nonlinear Static", "3" or "Modal" => "Modal", "4" or "ResponseSpectrum" => "Response Spectrum",
                    "5" or "LinearHistory" => "Linear Modal History", "6" or "NonlinearHistory" => "Nonlinear Modal History", "10" or "Buckling" => "Buckling", _ => type,
                };
                var uses = new List<string>();
                var patterns = new List<string>();
                if (type == "Response Spectrum" && CsiTools.Try(e, "read a spectrum's modal case", "LoadCases.ResponseSpectrum.GetModalCase", CsiTools.D(("Name", name))) is { } mc
                    && ApiCall.Value(mc, "ModalCase")?.ToString() is { Length: > 0 } modal)
                    uses.Add(modal);
                if (type == "Linear Static" && CsiTools.Try(e, "read a case's loads", "LoadCases.StaticLinear.GetLoads", CsiTools.D(("Name", name))) is { } loads)
                    patterns.AddRange(CsiTools.Names(loads, "LoadName"));
                a.Cases.Add(new Case(name, type, patterns, uses, []));
            }
            foreach (var name in e.Combos())
                if (CsiTools.Try(e, "read a combination's cases", "RespCombo.GetCaseList", CsiTools.D(("Name", name))) is { } list)
                    a.Combos.Add(new Combo(name, CsiTools.Names(list, "CName")));

            // The mass source: ETABS's one on PropMaterial, or the default of SourceMass.
            if (CsiTools.Try(e, "read the mass source", "PropMaterial.GetMassSource_1") is { } ms)
                a.Mass = new MassSource("the model's", Bool(ms, "IncludeElements"), Bool(ms, "IncludeAddedMass"), Bool(ms, "IncludeLoads") ? Loads(ms, "LoadPat", "SF") : []);
            else if (CsiTools.Try(e, "read the default mass source", "SourceMass.GetDefault") is { } d && ApiCall.Value(d, "Name")?.ToString() is { Length: > 0 } dn
                     && CsiTools.Try(e, "read the mass source", "SourceMass.GetMassSource", CsiTools.D(("Name", dn))) is { } s)
                a.Mass = new MassSource(dn, Bool(s, "MassFromElements"), Bool(s, "MassFromMasses"), Bool(s, "MassFromLoads") ? Loads(s, "LoadPat", "SF") : []);
        }

        private static bool Bool(JsonObject call, string name) => ApiCall.Value(call, name)?.ToString() is "true" or "True";

        private static List<(string, double)> Loads(JsonObject call, string names, string factors)
        {
            var n = CsiTools.Names(call, names);
            var f = ApiCall.Value(call, factors) as JsonArray;
            return n.Select((p, i) => (p, f is not null && i < f.Count ? ApiCall.Number(f[i]) : 1)).ToList();
        }
    }
}
