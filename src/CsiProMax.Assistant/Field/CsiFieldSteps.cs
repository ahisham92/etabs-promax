using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Lateral;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test of the tools every program has through the same API (<see cref="CsiTools"/>), on the scratch model
/// of whichever program the part is testing: the whole API, units, properties, load definitions, table editing,
/// groups (and ETABS's piers), the SAP2000 tools the other programs share, the lateral loads with a second analysis,
/// the results through the API, the response spectrum's scaling, unlocking, checkpoints, stories and a building made
/// in one call. Each change is read back from the program. Run last in each program's part, before its coverage
/// check; the building it makes at the end replaces the scratch model, which is closed without saving.
/// </summary>
public static class CsiFieldSteps
{
    private const string NoApi = "the stand-in has no program API behind it (a live program is needed)";

    /// <summary>edit_table's arguments: rows whose field equals a value, and the field to set on them.</summary>
    /// <summary>The program's answer to a call (its "return"), or -1 when it gave none.</summary>
    private static int Answer(JsonObject call) => call["return"] is JsonValue r && r.TryGetValue<int>(out var v) ? v : -1;

    private static string Edit(string table, string action, string matchField, string matchValue, string? setField, string? setValue)
    {
        var a = new JsonObject { ["table"] = table, ["action"] = action, ["match"] = new JsonObject { [matchField] = matchValue } };
        if (setField is not null) a["set"] = new JsonObject { [setField] = setValue };
        return a.ToJsonString();
    }

    private static string Cut(string text) => text.Length > 300 ? text[..300] + "..." : text;

    public static void Run(FieldContext ctx)
    {
        var link = ctx.Link;
        var program = link.Program;
        var name = program.Name;
        var isSafe = program.Key == CsiProgram.Safe.Key;
        var tools = CsiTools.For(program);
        ctx.Offer(tools);
        var offered = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var live = link is not OfflineEtabs and not MemoryEtabs;

        JsonNode? T(string tool, string json = "{}", Action<JsonNode>? check = null, string? step = null)
        {
            if (!live) { ctx.Covered(tool); ctx.Skip(step ?? "tool " + tool, NoApi); return null; }
            return ctx.Tool(tool, json, check, step);
        }
        void No(string tool, string json, string saying, string step)
        {
            if (!live) { ctx.Covered(tool); ctx.Skip(step, NoApi); return; }
            ctx.Refused(tool, json, saying, step);
        }

        // ---- what needs no API: this PC, the redraw, what changed ----
        ctx.Tool("list_programs", "{}", a =>
        {
            FieldContext.Expect(a["programs"] is JsonArray { Count: 3 } list && list.Count(p => p!["this_window"]!.GetValue<bool>()) == 1, "the three programs, one of them this window's, were expected: " + a.ToJsonString());
            FieldContext.Expect(a["this_window_works_in"]!.GetValue<string>() == name, $"this part tests {name}; the tool says {a["this_window_works_in"]}");
        });
        ctx.Tool("refresh_view", "{}", a => FieldContext.Expect(a["refreshed"]?.GetValue<bool>() == true, "no redraw: " + a.ToJsonString()));

        // ---- what the Modeler wrote into this program's file: its load cases, and for SAP2000 its table fields ----
        if (live && !isSafe && ctx.Files.TryGetValue(program.Key, out var written) && File.Exists(written))
        {
            ctx.Step($"{name} opened the Modeler's response spectrum, history, nonlinear static and buckling cases", () =>
            {
                var have = link.LoadCases().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = ModelerFieldSteps.Cases.Where(c => !have.Contains(c)).ToList();
                FieldContext.Expect(missing.Count == 0, $"{name} has no {string.Join(", ", missing)} from the Modeler's file; it has {string.Join(", ", have)}");
                return "all there";
            });
            ctx.Step($"{name} opened the Modeler's steel, section shapes, modifiers, releases and springs", () =>
            {
                var missing = new List<string>();
                if (!link.Materials().Contains("CPM-S355")) missing.Add("the material CPM-S355");
                var sections = link.FrameSections().ToHashSet(StringComparer.OrdinalIgnoreCase);
                missing.AddRange(ModelerFieldSteps.Shapes.Where(x => !sections.Contains(x)).Select(x => "the section " + x));
                // Each assignment shows as rows in one of the program's own tables (ETABS and SAP2000 name them differently).
                var tables = link.Tables();
                foreach (var (what, words) in new[] { ("releases", new[] { "RELEASE" }), ("frame modifiers", new[] { "FRAME", "MODIFIER" }), ("area modifiers", new[] { "AREA", "MODIFIER" }), ("insertion points", new[] { "INSERTION" }), ("joint springs", new[] { "SPRING" }) })
                {
                    // SAP2000 26's modifier assignment tables have no "Assign" in their names (0.4.42): "Frame Property
                    // Modifiers" and "Area Stiffness Modifiers", the names its .s2k uses.
                    string[] sapNames = ["Frame Property Modifiers", "Area Stiffness Modifiers"];
                    var found = tables.Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                        && (t.Name.Contains("ASSIGN", StringComparison.OrdinalIgnoreCase) || sapNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase))).ToList();
                    var rows = 0;
                    foreach (var t in found) { try { rows += link.ReadTable(t.Key, null, null).Rows.Count; } catch (Exception) { /* the API's 0 stands */ } }
                    if (rows == 0) missing.Add($"{what} (no rows in {(found.Count == 0 ? "any table named like it" : string.Join(", ", found.Select(t => t.Name)))})");
                }
                FieldContext.Expect(missing.Count == 0, $"{name} has no {string.Join("; ", missing)} from the Modeler's file");
                return "all there";
            });
            // Material Property Data in full: the nonlinear data came by the file's keywords (ETABS) or tables (SAP2000), the
            // damping and notes by the program's tables (ETABS: put in by CSI ProMax after opening the .e2k).
            ctx.Step($"{name} opened the Modeler's material damping, notes and nonlinear data", () =>
            {
                var tables = link.Tables();
                string Field(string ending, string field)
                {
                    var t = tables.FirstOrDefault(x => x.Name.StartsWith("Material Properties", StringComparison.OrdinalIgnoreCase) && x.Name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                        ?? throw new EtabsException($"{name} has no material table ending '{ending}'");
                    var read = link.ReadTable(t.Key, null, null);
                    int Col(string f) => read.Fields.ToList().FindIndex(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
                    var (mi, fi) = (Col("Material"), Col(field));
                    if (fi < 0) throw new EtabsException($"'{t.Name}' has no field {field}: {string.Join(", ", read.Fields)}");
                    var row = read.Rows.FirstOrDefault(r => mi >= 0 && mi < r.Length && r[mi] == "CPM-S355")
                        ?? throw new EtabsException($"'{t.Name}' has no row for CPM-S355");
                    return fi < row.Length ? row[fi] : "";
                }
                var wrong = new List<string>();
                void Want(string ending, string field, Func<string, bool> ok, string what)
                {
                    try { var v = Field(ending, field); if (!ok(v)) wrong.Add($"{what}: {field} is '{v}'"); }
                    catch (EtabsException ex) { wrong.Add($"{what}: {ex.Message}"); }
                }
                static bool Near(string v, double x) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && Math.Abs(d - x) < 1e-6;
                Want("Damping Parameters", "ModalRatio", v => Near(v, 0.02), "damping");
                Want("General", "Notes", v => v.Contains("CPM field test steel"), "notes");
                Want("Steel Data", "SHard", v => Near(v, 0.02), "strain at hardening");
                Want("Acceptance Criteria", "CPTens", v => Near(v, 0.06), "acceptance criteria");
                FieldContext.Expect(wrong.Count == 0, string.Join(" | ", wrong));
                return "all there";
            });
            // Frame sections of the shapes the .e2k has no line for (Frame Section Property Data, every ETABS 23.2 shape): ETABS
            // takes them from their own tables after the .e2k opens, and reads them back with the Modeler's values.
            if (program.Key == CsiProgram.Etabs.Key)
                ctx.Step($"{name} opened the Modeler's trapezoid and nonprismatic sections as those shapes, with their values", () =>
                {
                    var tables = link.Tables();
                    TableData Read(string table) => link.ReadTable((tables.FirstOrDefault(t => t.Name.Equals(table, StringComparison.OrdinalIgnoreCase)) ?? throw new EtabsException($"{name} has no table '{table}'")).Key, null, null);
                    string Value(TableData t, string section, string field)
                    {
                        int n = t.Fields.ToList().FindIndex(x => x == "Name"), f = t.Fields.ToList().FindIndex(x => x.Equals(field, StringComparison.OrdinalIgnoreCase));
                        var row = t.Rows.FirstOrDefault(r => n >= 0 && n < r.Length && r[n] == section) ?? throw new EtabsException($"no row for {section}");
                        return f >= 0 && f < row.Length ? row[f] : throw new EtabsException($"no field {field}");
                    }
                    var wrong = new List<string>();
                    try { var v = Value(Read("Frame Section Property Definitions - Concrete Trapezoid"), "CPM-TRAP", "t2b"); if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) || Math.Abs(b - 0.3) > 1e-6) wrong.Add($"CPM-TRAP's bottom width is '{v}', not 0.3"); }
                    catch (EtabsException ex) { wrong.Add("CPM-TRAP: " + ex.Message); }
                    try { var t = Read("Frame Section Property Definitions - Nonprismatic"); var n = t.Fields.ToList().IndexOf("Name"); var rows = t.Rows.Count(r => n >= 0 && n < r.Length && r[n] == "CPM-NP"); if (rows != 2) wrong.Add($"CPM-NP has {rows} segments, not 2"); }
                    catch (EtabsException ex) { wrong.Add("CPM-NP: " + ex.Message); }
                    FieldContext.Expect(wrong.Count == 0, string.Join(" | ", wrong));
                    return "both there, as their own shapes";
                });
            // ETABS's Define > Load Patterns and Define > Load Cases forms of the Engine, read back from ETABS field by field.
            if (program.Key == CsiProgram.Etabs.Key) EtabsFormRoundTrip.Check(ctx);
            // Assign > Frame, Shell and Joint > Additional Mass and Frame > Output Stations, kept by the Modeler as rows of ETABS's
            // assignment tables: in ETABS put in through its tables after opening the .e2k; in SAP2000 the mass by the .s2k's
            // tables and the stations by its API after the file opens.
            if (program.Key != CsiProgram.Safe.Key)
                ctx.Step($"{name} opened the Modeler's added mass on a beam, a floor and a joint, and the beam's output stations", () =>
                {
                    var tables = link.Tables();
                    var etabs = program.Key == CsiProgram.Etabs.Key;
                    var wrong = new List<string>();
                    foreach (var (own, sap, field, sapField, value) in new[] { ("Frame Assignments - Additional Mass", "Frame Added Mass Assignments", "AddedMass", "MassPerLen", 0.05), ("Area Assignments - Additional Mass", "Area Added Mass Assignments", "AddedMass", "MassPerArea", 0.02),
                                 ("Joint Assignments - Additional Mass", "Joint Added Mass Assignments", "MassXY", "Mass1", 1.5), ("Frame Assignments - Output Stations", "Frame Output Station Assignments", "MinStations", "MinNumSta", 7.0) })
                    {
                        var want = etabs ? own : sap;
                        var t = tables.FirstOrDefault(x => string.Equals(x.Name, want, StringComparison.OrdinalIgnoreCase));
                        if (t is null) { wrong.Add($"{name} has no table '{want}'"); continue; }
                        var read = link.ReadTable(t.Key, null, null);
                        var col = (read.Fields ?? []).ToList().FindIndex(x => string.Equals(x, etabs ? field : sapField, StringComparison.OrdinalIgnoreCase));
                        if (col < 0) { wrong.Add($"'{want}' has no field {(etabs ? field : sapField)}: {string.Join(", ", read.Fields)}"); continue; }
                        if (!(read.Rows ?? []).Any(r => r is not null && col < r.Length && double.TryParse(r[col], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && Math.Abs(d - value) < 1e-6))
                            wrong.Add($"'{want}' has no row with {value} ({read.Rows.Count} rows)");
                    }
                    FieldContext.Expect(wrong.Count == 0, string.Join(" | ", wrong));
                    return "all there";
                });
            // SAP2000's shell edge release, the auto edge constraint set after it, and the joint panel zone (Ahmed 2026-11-26).
            if (program.Key == CsiProgram.Sap2000.Key)
                ctx.Step($"{name} opened the Modeler's shell edge release, edge constraint (after the release) and joint panel zone", () =>
                {
                    var tables = link.Tables();
                    var wrong = new List<string>();
                    foreach (var (table, field, value) in new[] { ("Area Edge Release Assignments", "ReleasedDOF", "Moment"), ("Area Edge Constraint Assignments", "Constrained", "Yes"), ("Joint Panel Zone Assignments", "PZConnect", "Beams to Other Objects") })
                    {
                        var t = tables.FirstOrDefault(x => string.Equals(x.Name, table, StringComparison.OrdinalIgnoreCase));
                        if (t is null) { wrong.Add($"{name} has no table '{table}'"); continue; }
                        var read = link.ReadTable(t.Key, null, null);
                        var col = (read.Fields ?? []).ToList().FindIndex(x => string.Equals(x, field, StringComparison.OrdinalIgnoreCase));
                        if (col < 0) { wrong.Add($"'{table}' has no field {field}: {string.Join(", ", read.Fields ?? [])}"); continue; }
                        if (!(read.Rows ?? []).Any(r => r is not null && col < r.Length && string.Equals(r[col], value, StringComparison.OrdinalIgnoreCase)))
                            wrong.Add($"'{table}' has no row with {field} {value} ({read.Rows?.Count ?? 0} rows)");
                    }
                    FieldContext.Expect(wrong.Count == 0, string.Join(" | ", wrong));
                    return "all there";
                });
            ctx.Step($"{name} opened the Modeler's groups, links" + (program.Key == CsiProgram.Etabs.Key ? ", diaphragms and pier labels" : ""), () =>
            {
                string[] Names(string what, Func<dynamic, string[]> read) => (string[])(link.Raw(what, m => read(m)) ?? Array.Empty<string>());
                var missing = new List<string>();
                var groups = Names("list the groups", m => { int n = 0; string[] names = []; m.GroupDef.GetNameList(ref n, ref names); return names ?? []; });
                if (!groups.Contains("CPM-G")) missing.Add($"the group CPM-G (it has {string.Join(", ", groups)})");
                var links = Names("list the link properties", m => { int n = 0; string[] names = []; m.PropLink.GetNameList(ref n, ref names); return names ?? []; });
                if (!links.Contains("CPM-LINK")) missing.Add($"the link property CPM-LINK (it has {string.Join(", ", links)})");
                if (program.Key == CsiProgram.Etabs.Key)
                {
                    var diaphragms = Names("list the diaphragms", m => { int n = 0; string[] names = []; m.Diaphragm.GetNameList(ref n, ref names); return names ?? []; });
                    if (!diaphragms.Contains("CPM-D2")) missing.Add($"the diaphragm CPM-D2 (it has {string.Join(", ", diaphragms)})");
                    var piers = Names("list the pier labels", m => { int n = 0; string[] names = []; m.PierLabel.GetNameList(ref n, ref names); return names ?? []; });
                    if (!piers.Contains("CPM-P1")) missing.Add($"the pier CPM-P1 (it has {string.Join(", ", piers)})");
                }
                FieldContext.Expect(missing.Count == 0, $"{name} has no {string.Join("; ", missing)} from the Modeler's file");
                return "all there";
            });
            // The CSI APIs read hinges and cannot assign them, so the Modeler's hinges go in by the .e2k. Which hinge tables
            // and fields the program has is said either way, so its own words can be written next time when the file's are not.
            string HingeTables() { try { return HingeTablesOf(); } catch (Exception ex) { return "they could not be read: " + ex.Message; } }
            string HingeTablesOf() => string.Join(" | ", link.Tables().Where(t => t.Name.Contains("Hinge", StringComparison.OrdinalIgnoreCase) && t.Name.Contains("Assign", StringComparison.OrdinalIgnoreCase)).Select(t =>
            {
                var fields = (string[])(link.Raw($"read the fields of {t.Name}", m =>
                {
                    int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                    m.DatabaseTables.GetAllFieldsInTable(t.Key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
                    return keys ?? [];
                }) ?? Array.Empty<string>());
                return $"{t.Name}: {string.Join(", ", fields)}";
            }));
            ctx.Step($"{name} opened the Modeler's frame hinges", () =>
                {
                    var hinges = 0; JsonObject? last = null;
                    foreach (var f in link.Frames())
                    {
                        var call = (JsonObject)link.Raw("read a frame's hinges", m => ApiCall.Invoke((object)m, "FrameObj.GetHingeAssigns", CsiTools.D(("Name", f.Name))))!;
                        // ETABS answers 1 for a name it does not know (the label "B1" instead of its unique name "10": the
                        // hinges were there, 2026-11-26 experiment A): ask again by the unique name of label and story.
                        if (Answer(call) != 0 && f.Story.Length > 0
                            && CsiTools.Try(link, "find the frame's unique name", "FrameObj.GetNameFromLabel", CsiTools.D(("Label", f.Name), ("Story", f.Story), ("Name", ""))) is { } named
                            && ApiCall.Value(named, "Name")?.ToString() is { Length: > 0 } unique && unique != f.Name)
                            call = (JsonObject)link.Raw("read a frame's hinges", m => ApiCall.Invoke((object)m, "FrameObj.GetHingeAssigns", CsiTools.D(("Name", unique))))!;
                        last = call;
                        if (Answer(call) is 0 or -1)
                            hinges += ApiCall.Value(call, "NumberHinges") is JsonValue nh && nh.TryGetValue<int>(out var count) ? count
                                : call.Where(kv => kv.Key != "return").Select(kv => kv.Value is JsonArray a ? a.Count : 0).DefaultIfEmpty(0).Max();
                    }
                    // SAP2000 26's GetHingeAssigns answers 0 even for hinges its own model has (0.4.40): its "01 - Overview"
                    // table lists them, so that is read too.
                    if (hinges == 0 && link.Tables().FirstOrDefault(t => t.Name.StartsWith("Frame Hinge Assigns 01", StringComparison.OrdinalIgnoreCase)) is { } overview)
                        try { hinges = link.ReadTable(overview.Key, null, null).Rows.Count; } catch (EtabsException) { }
                    // The .s2k and the .e2k carry the concrete beam's two flexural hinges: neither program has an auto
                    // table for its shear hinge (ETABS 23's ASCE 41-17 concrete beam takes M2 and M3 only, 2026-11-26).
                    var written = 2;
                    FieldContext.Expect(hinges >= written, $"{name} has {hinges} hinges from the Modeler's file, {written} were written (a frame's answer: {last?.ToJsonString()}; its hinge tables: {HingeTables()})");
                    return $"{hinges} hinges";
                });
            if (program.Key == CsiProgram.Sap2000.Key)
                ctx.Step("SAP2000 knows every table and field the Modeler writes into the .s2k", () =>
                {
                    var tables = CsiProMax.CsiTables.CsiTableText.Parse(File.ReadAllText(written));
                    var known = link.Tables().ToDictionary(t => t.Name.ToUpperInvariant(), t => t.Key);
                    var wrong = new List<string>();
                    foreach (var (table, rows) in tables)
                    {
                        if (!known.TryGetValue(table.ToUpperInvariant(), out var key))
                        {
                            // The program's own tables that share the first word, so the right name can be put in.
                            var word = table.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                            wrong.Add($"no table '{table}' (its tables starting '{word}': {string.Join(", ", known.Keys.Where(k => k.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Take(40))})");
                            continue;
                        }
                        var fields = (string[])link.Raw($"read the fields of {table}", m =>
                        {
                            int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                            m.DatabaseTables.GetAllFieldsInTable(key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
                            return keys ?? [];
                        })!;
                        var set = fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var bad = rows.SelectMany(r => r.Keys).Distinct().Where(f => !set.Contains(f)).ToList();
                        if (bad.Count > 0) wrong.Add($"'{table}' has no {string.Join(", ", bad)} (its fields: {string.Join(", ", fields)})");
                    }
                    FieldContext.Expect(wrong.Count == 0, string.Join(" | ", wrong));
                    return $"{tables.Count} tables, every field known";
                });
        }
        // ---- the model must be changeable for the rest (a locked SAP2000 refused the pattern added by hand, 0.4.17) ----
        var locked = Read(ctx, "whether the model is locked", () => link.Info().Locked, false);
        // The Viewer is checked first, while the results are still there (SAP2000's Viewer had no result to show, 0.4.20).
        if (locked) ctx.WithResults();
        if (locked) T("unlock_model", "{}", a => FieldContext.Expect(a["locked"]?.GetValue<bool>() == false, "still locked: " + a.ToJsonString()), "tool unlock_model on the analysed scratch model");
        ctx.Tool("what_changed", """{"reset":true}""", a => FieldContext.Expect(a["baseline"]?.GetValue<string>() == "set", "the starting point was not set: " + a.ToJsonString()), "tool what_changed sets its starting point");
        var marker = Read(ctx, "a load pattern added by hand for what_changed", () => { link.AddLoadPattern("CPM-BYHAND", "Other", 0); return true; }, false);
        ctx.Tool("what_changed", "{}", a =>
        {
            FieldContext.Expect(!marker || a["definitions"]?["load_patterns"]?["added"]?.ToJsonString().Contains("CPM-BYHAND") == true, "the pattern added by hand is not reported: " + a.ToJsonString());
        }, "tool what_changed reports a change made outside the tools");
        ctx.Tool("list_checkpoints", "{}", a => FieldContext.Expect(Int(a["count"]) >= 0, "no count: " + a.ToJsonString()), "tool list_checkpoints before any");

        // ---- the whole API ----
        T("search_api", """{"query":"PointObj SetRestraint"}""", a =>
        {
            FieldContext.Expect(Int(a["methods_in_api"]) > 300, $"only {a["methods_in_api"]} methods were found in {name}'s API");
            FieldContext.Expect(a["methods"] is JsonArray { Count: > 0 } m && m.Any(x => x!["path"]!.GetValue<string>() == "PointObj.SetRestraint"), "PointObj.SetRestraint was not found: " + a.ToJsonString());
        });
        T("describe_api", """{"path":"PointObj.SetRestraint"}""", a =>
            FieldContext.Expect(a["overloads"]?[0]?["parameters"] is JsonArray { Count: >= 2 } p && p.Any(x => x!["direction"]!.GetValue<string>() == "ref"), "the parameters, with the restraint given by ref, were expected: " + a.ToJsonString()));
        No("describe_api", """{"path":"PointObj.NoSuchMethod"}""", "no", "tool describe_api says no to a method that is not there");
        var points = Read(ctx, "the points", () => link.Points().Count, 0);
        T("call_api", """{"path":"PointObj.GetNameList"}""", a =>
        {
            FieldContext.Expect(a["return"]?.GetValue<int>() == 0, $"{name} answered {a["return"]}");
            var names = a["values"]?.AsObject().Select(v => v.Value).OfType<JsonArray>().FirstOrDefault();
            FieldContext.Expect(names is not null && names.Count == points, $"the link reads {points} points; the API call gave {names?.Count}");
        }, "tool call_api reads the point names");
        T("call_api", """{"calls":[{"path":"GetModelIsLocked"},{"path":"FrameObj.Count"}]}""", a =>
            FieldContext.Expect(Int(a["calls"]) == 2 && a["results"] is JsonArray { Count: 2 } r && r.All(x => x?["return"] is not null || x?["error"] is not null), "two answers were expected: " + a.ToJsonString()), "tool call_api, two calls in one go");
        No("call_api", """{"path":"FrameObj.Delete","args":{"Name":"1"}}""", "only reads", "tool call_api says no to a call that changes the model");


        // ---- units, properties, load definitions, design codes ----
        var info = Read(ctx, "the units", link.Info, new ModelInfo("", "", "kN", "m", "C", false));
        var units = Read(ctx, "the units' name", () => ComEtabs.UnitNames[Units.Code(info.Force, info.Length) - 1], "kN_m_C");
        T("set_units", """{"force":"kN","length":"mm"}""", a => FieldContext.Expect(link.Info().Length == "mm", $"the units were set to kN, mm; {name} says {link.Info().Force}, {link.Info().Length}"), "tool set_units to kN, mm");
        T("set_units", $$"""{"units":{{Q(units)}}}""", a => FieldContext.Expect(link.Info().Length == info.Length, $"the units were set back to {units}; {name} says {link.Info().Force}, {link.Info().Length}"), "tool set_units back to the model's own");
        No("set_units", """{"units":"furlong_C"}""", "not one of", "tool set_units says no to units that are not a set");
        T("list_properties", """{"kind":"all"}""", a =>
        {
            var mats = a["materials"] as JsonArray;
            FieldContext.Expect(mats is { Count: > 0 } && mats.Any(m => m!["name"]!.GetValue<string>() == "CPM-C35" && m["e"]?.GetValue<double>() > 0), "CPM-C35 with its modulus was expected: " + mats?.ToJsonString());
            FieldContext.Expect(a["frame_sections"] is JsonArray f && (isSafe || f.Any(s => s!["name"]!.GetValue<string>() == "CPM-C400" && s["shape"]!.GetValue<string>().Length > 0)), "CPM-C400 with its shape was expected: " + a["frame_sections"]?.ToJsonString());
            FieldContext.Expect(a["area_sections"] is JsonArray s2 && s2.Any(s => s!["kind"] is not null), "an area section with its kind was expected: " + a["area_sections"]?.ToJsonString());
        });
        T("list_load_definitions", "{}", a =>
        {
            FieldContext.Expect(a["load_patterns"] is JsonArray p && p.Any(x => x!["name"]!.GetValue<string>() == "CPM-SDL"), "CPM-SDL was expected: " + a["load_patterns"]?.ToJsonString());
            FieldContext.Expect(a["combinations"] is JsonArray c && c.Any(x => x!["name"]!.GetValue<string>() == "CPM-ULS" && x["cases"] is JsonArray { Count: > 0 }), "CPM-ULS with its cases was expected: " + a["combinations"]?.ToJsonString());
            FieldContext.Expect(a["load_cases"] is JsonArray l && l.All(x => x!["type"] is not null), "every load case with its type was expected: " + a["load_cases"]?.ToJsonString());
        });
        T("design_codes", "{}", a => FieldContext.Expect(a["codes"] is JsonObject c && c.Any(kv => kv.Value?.GetValue<string>() is { Length: > 0 } v && v != "not in this program"), $"no design code was read from {name}: " + a.ToJsonString()));

        // ---- groups, and ETABS's piers ----
        var frames = Read(ctx, "the frames", () => link.Frames().Select(f => f.Name).Take(2).ToList(), []);
        var areas = Read(ctx, "the areas", () => link.Areas().Select(x => x.Name).Take(2).ToList(), []);
        T("assign_group", $$"""{"group":"CPM-G1","frames":{{List(frames)}},"areas":{{List(areas)}}}""", a =>
        {
            FieldContext.Expect(Int(a["done"]) == frames.Count + areas.Count && a["failed"]!.AsArray().Count == 0, "not every object went in the group: " + a.ToJsonString());
            FieldContext.Expect(Int(a["in_group_now"]) >= frames.Count + areas.Count, "the group read back holds fewer: " + a.ToJsonString());
        });
        T("assign_group", $$"""{"group":"CPM-G1","areas":{{List(areas.Take(1))}},"remove":true}""", a => FieldContext.Expect(Int(a["done"]) == Math.Min(1, areas.Count), "not taken out: " + a.ToJsonString()), "tool assign_group takes an object out");
        if (offered.Contains("assign_pier_spandrel"))
        {
            var wall = Read(ctx, "a wall", () => link.Areas().FirstOrDefault(x => x.Kind == "wall")?.Name, null);
            if (wall is null) { ctx.Covered("assign_pier_spandrel"); ctx.Fail("tool assign_pier_spandrel", "the scratch model has no wall to label"); }
            else
            {
                T("assign_pier_spandrel", $$"""{"kind":"pier","label":"CPM-P2","areas":[{{Q(wall)}}]}""", a => FieldContext.Expect(Int(a["done"]) == 1, "the wall is not pier CPM-P2 when read back: " + a.ToJsonString()), "tool assign_pier_spandrel, a pier");
                T("assign_pier_spandrel", $$"""{"kind":"spandrel","label":"CPM-SP1","areas":[{{Q(wall)}}]}""", a => FieldContext.Expect(Int(a["done"]) == 1, "the wall is not spandrel CPM-SP1 when read back: " + a.ToJsonString()), "tool assign_pier_spandrel, a spandrel");
            }
        }
        if (offered.Contains("add_area_section"))
        {
            T("add_area_section", """{"name":"CPM-S150","kind":"slab","material":"CPM-C35","thickness":0.15}""", _ => FieldContext.Expect(link.AreaSections().Contains("CPM-S150"), "CPM-S150 is not in the model"), "tool add_area_section, a slab");
            T("add_area_section", """{"name":"CPM-W250","kind":"wall","material":"CPM-C35","thickness":0.25}""", _ => FieldContext.Expect(link.AreaSections().Contains("CPM-W250"), "CPM-W250 is not in the model"), "tool add_area_section, a wall");
        }

        // ---- table editing: the load patterns' table ----
        var table = Read(ctx, "the load pattern table's name", () =>
            (link.Tables().FirstOrDefault(t => t.Name.Equals("Load Pattern Definitions", StringComparison.OrdinalIgnoreCase))
             ?? link.Tables().FirstOrDefault(t => t.Name.StartsWith("Load Pattern Definitions", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Auto", StringComparison.OrdinalIgnoreCase)))?.Name, null);
        if (table is null) { ctx.Covered("edit_table"); if (live) ctx.Fail("tool edit_table", $"{name} lists no 'Load Pattern Definitions' table to edit"); else ctx.Skip("tool edit_table", NoApi); }
        else
        {
            string? nameField = null, weightField = null;
            T("edit_table", $$"""{"table":{{Q(table)}},"action":"fields"}""", a =>
            {
                var fields = a["fields"]!.AsArray().Select(f => f!["key"]!.GetValue<string>()).ToList();
                nameField = fields.FirstOrDefault(f => f.Equals("Name", StringComparison.OrdinalIgnoreCase) || f.Equals("LoadPat", StringComparison.OrdinalIgnoreCase) || f.Contains("Pattern", StringComparison.OrdinalIgnoreCase));
                weightField = fields.FirstOrDefault(f => f.Contains("Self", StringComparison.OrdinalIgnoreCase));
                FieldContext.Expect(nameField is not null && weightField is not null, "a name and a self weight field were expected: " + string.Join(", ", fields));
            }, "tool edit_table gives a table's fields");
            T("edit_table", $$"""{"table":{{Q(table)}},"action":"read","limit":50}""", a => FieldContext.Expect(Int(a["rows_total"]) >= 1, "no rows: " + a.ToJsonString()), "tool edit_table reads a table's rows");
            if (nameField is not null && weightField is not null)
            {
                T("edit_table", Edit(table, "update", nameField, "CPM-SDL", weightField, "0.5"), a =>
                {
                    FieldContext.Expect(a["applied"]?.GetValue<bool>() == true, "the edit was not applied: " + a.ToJsonString());
                    var back = link.LoadPatterns().First(p => p.Name == "CPM-SDL").SelfWeight;
                    FieldContext.Expect(Math.Abs(back - 0.5) < 1e-6, $"CPM-SDL's self weight was set to 0.5 through the table; {name} says {back}");
                }, "tool edit_table updates a row, read back");
                T("edit_table", Edit(table, "update", nameField, "CPM-SDL", weightField, "0"), null, "tool edit_table sets the row back");
                No("edit_table", Edit(table, "delete", nameField, "No such pattern", null, null), "No row", "tool edit_table says no to deleting rows that are not there");
            }
        }

        // ---- the checker for what stops the analysis, on the scratch model as it is (what it finds is listed) ----
        var before = T(StabilityTools.Name, "{}", a =>
        {
            FieldContext.Expect(Int(a["checked"]?["joints"]) > 0 && a["findings"] is JsonArray, "the checker read no joints: " + Cut(a.ToJsonString()));
            FieldContext.Expect(a["checked"]?["in"]?.GetValue<string>() == name, $"the checker says it read {a["checked"]?["in"]}, and this is {name}");
        }, "tool find_instabilities on the scratch model");

        // ---- the SAP2000 tools the other programs share ----
        if (offered.Contains("add_frames"))
        {
            T("add_material", """{"name":"CPM-STEEL","type":"steel","elastic_modulus":200000000,"fy":345000,"unit_weight":77}""", _ => FieldContext.Expect(link.Materials().Contains("CPM-STEEL"), "CPM-STEEL is not in the model"));
            T("add_frame_section", """{"name":"CPM-I300","shape":"i_section","material":"CPM-STEEL","depth":0.3,"width":0.15,"flange_thickness":0.01,"web_thickness":0.007}""", _ => FieldContext.Expect(link.FrameSections().Contains("CPM-I300"), "CPM-I300 is not in the model"));
            // Above the base level: ETABS gives a joint there the model's base restraint and reuses it (0.4.55 ETABS run: a
            // pinned joint at 60,0,0 left by an earlier run, so the two stood on a pin and were not floating).
            var joints = Names(T("add_joints", """{"points":[[60,0,0.5],[60,0,2.5]]}""", a => FieldContext.Expect(Int(a["added"]) == 2, "two joints were asked for: " + a.ToJsonString())), "joints");
            var made = Names(T("add_frames", """{"lines":[[60,0,0.5,60,0,2.5],[60,0,0.5,63,0,2.5]],"section":"CPM-I300"}""", a =>
            {
                var names = Names(a, "frames");
                var kinds = link.Frames().Where(f => names.Contains(f.Name)).Select(f => f.Kind).OrderBy(k => k).ToList();
                FieldContext.Expect(names.Count == 2 && string.Join(",", kinds) == "brace,column", "a column and a brace were asked for; the model has: " + string.Join(", ", kinds));
            }, "tool add_frames, a column and a brace by coordinates"), "frames");
            // They stand on nothing: the checker must find them floating, named, beside what it found before.
            if (made.Count == 2 && before is not null)
                T(StabilityTools.Name, """{"stiffness":true}""", a =>
                {
                    var floating = a["findings"]!.AsArray().Where(f => f!["check"]!.GetValue<string>() == "floating").ToList();
                    FieldContext.Expect(floating.Any(f => made.All(m => f!["objects"]!.AsArray().Any(o => o!["name"]!.GetValue<string>() == m))),
                        $"the column and brace that stand on nothing ({string.Join(", ", made)}) were not found floating: " + Cut(a["findings"]!.ToJsonString()));
                    FieldContext.Expect(Int(a["errors"]) > Int(before["errors"]), "the checker found no more errors with the floating frames than without them");
                }, "tool find_instabilities finds the frames that stand on nothing");
            else if (live) ctx.Fail("tool find_instabilities finds the frames that stand on nothing", "the frames or the first check were not there");
            if (made.Count == 2)
            {
                T("set_releases", $$"""{"frames":[{{Q(made[1])}}],"type":"pinned"}""", null, "tool set_releases pins the brace");
                T("set_member_loads", $$"""{"frames":[{{Q(made[1])}}],"pattern":"CPM-SDL","value":1,"direction":"gravity"}""", null, "tool set_member_loads on the brace");
            }
            else foreach (var t in new[] { "set_releases", "set_member_loads" }) { ctx.Covered(t); if (live) ctx.Fail("tool " + t, "the frames to try it on were not added"); else ctx.Skip("tool " + t, NoApi); }
            if (joints.Count == 2) T("set_joint_loads", $$"""{"joints":[{{Q(joints[1])}}],"pattern":"CPM-SDL","fz":-10}""", null, "tool set_joint_loads on the column's top");
            else { ctx.Covered("set_joint_loads"); if (live) ctx.Fail("tool set_joint_loads", "the joints were not added"); else ctx.Skip("tool set_joint_loads", NoApi); }
            // They stand on nothing: out again before the analysis, the frames, then their joints.
            if (made.Count > 0) T("delete_objects", $$"""{"frames":{{List(made)}}}""", null, "tool delete_objects takes the test frames out again");
            var loose = Read(ctx, "the test joints", () => link.Points().Where(p => Math.Abs(p.X - 60) < 1e-4 || Math.Abs(p.X - 63) < 1e-4).Select(p => p.Name).ToList(), []);
            T("delete_joints", $$"""{"joints":{{List(loose.Count > 0 ? loose : ["no such joint"])}}}""", _ => FieldContext.Expect(!link.Points().Any(p => Math.Abs(p.X - 60) < 1e-4 || Math.Abs(p.X - 63) < 1e-4), "a test joint is still in the model"), "tool delete_joints takes the test joints out");
        }

        // ---- lateral loads, a second analysis, results through the API, the spectrum's scaling ----
        var analysed = false;
        if (offered.Contains("add_seismic_load"))
        {
            T("set_mass_source", """{"name":"CPM-MS","loads":[{"pattern":"CPM-SDL","factor":1}]}""", a => FieldContext.Expect(a["from_loads"] is not JsonArray l || l.Any(x => x!.GetValue<string>() == "CPM-SDL"), "CPM-SDL is not in the mass source when read back: " + a.ToJsonString()));
            T("set_modal_case", """{"name":"CPM-MODAL","max_modes":6}""", a => FieldContext.Expect(Int(a["max_modes"]) == 6, "6 modes were asked for: " + a.ToJsonString()));
            T("assign_diaphragm", """{"name":"CPM-D","all_floors":true}""", a => FieldContext.Expect(Int(a["done"]) > 0 || a["diaphragms"] is JsonArray { Count: > 0 }, "no floor took the diaphragm: " + a.ToJsonString()));
            T("add_seismic_load", """{"name":"CPM-ELFX","direction":"x","method":"user","coefficient":0.1,"k":1}""", a =>
                FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-ELFX" && p.Type == "Seismic") && a["read_back"] is not null, "CPM-ELFX should be a seismic pattern with its coefficient read back: " + a.ToJsonString()), "tool add_seismic_load, a user coefficient");
            T("add_seismic_load", """{"name":"CPM-ELFY","direction":"y","r":8,"omega":3,"cd":5.5,"ss":1.0,"s1":0.4,"site_class":"D"}""", a =>
                FieldContext.Expect(a["read_back"] is JsonObject r && r.ToJsonString().Contains('8'), "ASCE 7-16 with R 8 should read back: " + a.ToJsonString()), "tool add_seismic_load, ASCE 7-16");
            No("add_seismic_load", """{"name":"CPM-SDL","direction":"x","method":"user","coefficient":0.1}""", "is there as", "tool add_seismic_load says no to a pattern of another type");
            T("add_response_spectrum", """{"case":"CPM-RS","function":"CPM-ASCE716","direction":"x","ss":1.0,"s1":0.4,"site_class":"D","r":8,"modal_case":"CPM-MODAL"}""", a =>
                FieldContext.Expect(a["read_back"]?["loads"] is JsonArray l && l.Any(x => x!.GetValue<string>() == "U1"), "the case should read back a U1 load: " + a.ToJsonString()));
            if (live)
            {
                ctx.Step($"{name} analyses the model with the seismic loads, the spectrum and the mass source", () =>
                {
                    ctx.Call("run_analysis", "{}");
                    FieldContext.Expect(link.Info().Locked, $"the analysis answered, and {name} does not say the model is analysed");
                    return "analysed";
                });
                analysed = Read(ctx, "whether the model is analysed", () => link.Info().Locked, false);
            }
        }
        else if (live && !Read(ctx, "whether the model is analysed", () => link.Info().Locked, false))
        {
            ctx.Step($"{name} analyses the scratch model again", () => { ctx.Call("run_analysis", "{}"); return link.Info().Locked ? "analysed" : "not analysed"; });
            analysed = Read(ctx, "whether the model is analysed", () => link.Info().Locked, false);
        }
        var none = analysed ? "" : " (no analysis: it must still answer)";
        var status = T("analysis_status", "{}", a => FieldContext.Expect(!analysed || Int(a["finished"]) > 0, "the model is analysed and no case is finished: " + a.ToJsonString()));
        // Analyze > Set Load Cases to Run (Ahmed 2026-11-27): a case set not to run leaves the program's run list, then
        // every case runs again (the Engine's Run sets the same flags before it analyses).
        if (offered.Contains("set_run_cases"))
        {
            var one = (status?["cases"] as JsonArray)?.Select(c => c?["case"]?.ToString()).LastOrDefault(c => c is { Length: > 0 }) ?? "Dead";
            T("set_run_cases", JsonSerializer.Serialize(new { all = true, not_run = new[] { one } }), a =>
                FieldContext.Expect(a["set_to_run"] is JsonArray r && !r.Any(x => x?.ToString() == one), $"{one} should not be set to run: " + Cut(a.ToJsonString())), "tool set_run_cases leaves a case out");
            T("set_run_cases", """{"all":true}""", a =>
                FieldContext.Expect(a["set_to_run"] is JsonArray r && r.Any(x => x?.ToString() == one), $"{one} should run again: " + Cut(a.ToJsonString())), "tool set_run_cases runs every case again");
        }
        if (offered.Contains("joint_displacements"))
        {
            var results = analysed ? (Action<JsonNode, string>)((a, key) => FieldContext.Expect(Int(a["rows"]) > 0 || a[key] is JsonArray { Count: > 0 }, "no results after the analysis: " + Cut(a.ToJsonString()))) : null;
            T("joint_displacements", """{"limit":5}""", results is null ? null : a => results(a, "list"), "tool joint_displacements" + none);
            T("frame_forces", """{"limit":5}""", results is null ? null : a => results(a, "list"), "tool frame_forces" + none);
            T("base_reactions", "{}", results is null ? null : a => results(a, "base_reactions"), "tool base_reactions" + none);
            if (offered.Contains("modal_results")) T("modal_results", "{}", analysed ? a => FieldContext.Expect(a["modes"] is JsonArray { Count: > 0 }, "no modes: " + a.ToJsonString()) : null, "tool modal_results" + none);
        }
        if (offered.Contains("scale_response_spectrum"))
        {
            if (analysed)
            {
                T("scale_response_spectrum", """{"rs_case":"CPM-RS","elf_case":"CPM-ELFX","direction":"x"}""", a =>
                    FieldContext.Expect(a["rs_base_shear"]?.GetValue<double>() > 0 && a["elf_base_shear"]?.GetValue<double>() > 0, "both base shears were expected: " + a.ToJsonString()), "tool scale_response_spectrum compares");
                T("scale_response_spectrum", """{"rs_case":"CPM-RS","elf_case":"CPM-ELFX","direction":"x","apply":true}""", a =>
                    FieldContext.Expect(a["applied"]?.GetValue<bool>() == true || a["passes"]?.GetValue<bool>() == true, "it should scale, or say there is nothing to scale: " + a.ToJsonString()), "tool scale_response_spectrum scales");
            }
            else No("scale_response_spectrum", """{"rs_case":"CPM-RS","elf_case":"CPM-ELFX","direction":"x"}""", "analysis", "tool scale_response_spectrum says the analysis must run first");
        }
        if (Read(ctx, "whether the model is locked", () => link.Info().Locked, false) || !locked)
            T("unlock_model", "{}", a => FieldContext.Expect(a["locked"]?.GetValue<bool>() == false, "still locked: " + a.ToJsonString()));
        if (offered.Contains("add_wind_load"))
            T("add_wind_load", """{"name":"CPM-WX","speed":110,"angle":0,"exposure":"C"}""", a =>
                FieldContext.Expect(link.LoadPatterns().Any(p => p.Name == "CPM-WX" && p.Type == "Wind"), "CPM-WX should be a wind pattern: " + a.ToJsonString()));

        // ---- every seismic and wind code the program lists, through its own table for the code ----
        // Ahmed: wind and seismic "should cover all options as csi programs gives them", even where the API has no call.
        if (!isSafe)
        {
            ctx.Offer(LateralTools.All);
            foreach (var kind in new[] { "seismic", "wind" })
            {
                var listed = T("lateral_codes", $$"""{"kind":"{{kind}}"}""", a => FieldContext.Expect(Int(a["count"]) >= 1, $"{name} lists no {kind} codes: " + a.ToJsonString()), $"tool lateral_codes lists {name}'s {kind} codes");
                var codes = listed?["codes"] is JsonArray arr ? arr.Select(c => c!.GetValue<string>()).ToList() : [];
                var pattern = kind == "wind" ? "CPM-AUTO-W" : "CPM-AUTO-S";
                if (codes.Count > 0)
                    T("lateral_codes", $$"""{"kind":"{{kind}}","code":{{Q(codes[0])}},"pattern":"{{pattern}}"}""", a => FieldContext.Expect(a["inputs"] is JsonArray { Count: > 0 }, $"{codes[0]} has no inputs: " + a.ToJsonString()), $"tool lateral_codes gives the inputs of {kind} {codes[0]}");
                foreach (var code in codes)
                    // SAP2000 26: by its own Set call, or, for a code its API has none for, by the patterns' table and
                    // the code's table in one import (0.4.40 and 0.4.42 field tests, and the probe on Ahmed's PC).
                    T("set_lateral_load", $$"""{"name":"{{pattern}}","kind":"{{kind}}","code":{{Q(code)}}}""", a =>
                        FieldContext.Expect(a["read_back"] is JsonObject { Count: > 0 }, $"{code} was not read back from its table: " + a.ToJsonString()), $"tool set_lateral_load, {kind} {code}");
                if (codes.Count > 0)
                    T("set_lateral_load", $$"""{"name":"{{pattern}}","kind":"{{kind}}","code":"None"}""", null, $"tool set_lateral_load takes the {kind} code off {pattern}");
            }
            if (live && link.LoadPatterns().Any(p => p.Name == "CPM-AUTO-S"))
                T("set_lateral_load", """{"name":"CPM-AUTO-S","code":"ASCE 7-16","values":{"R":"6"}}""", a =>
                    FieldContext.Expect(a["read_back"]?["R"]?.ToString() is "6" or "6.0", "R 6 was not read back: " + a.ToJsonString()), "tool set_lateral_load gives ASCE 7-16 its R, read back");
            No("set_lateral_load", """{"name":"CPM-AUTO-S","code":"ASCE 7-99"}""", "no seismic code", "tool set_lateral_load says no to a code the program does not list");
        }

        // ---- checkpoints of the scratch model's file ----
        // The open model is told by its name without the file type: ETABS 23 answers its working file (name.$et) for a
        // model saved as name.edb, which failed both comparisons below in the 0.4.17 run.
        var scratchFile = Read(ctx, "the scratch model's file", () => link.Info().File, "");
        string? checkpoint = null;
        T("save_checkpoint", """{"name":"cpm-cp1"}""", a =>
        {
            checkpoint = a["file"]!.GetValue<string>();
            FieldContext.Expect(File.Exists(checkpoint), "the checkpoint's file is not there: " + a.ToJsonString());
            FieldContext.Expect(ScratchProgram.SameName(link.Info().File, scratchFile), $"{name} should have the scratch model open again after the checkpoint; it has {link.Info().File}");
        });
        ctx.Tool("list_checkpoints", "{}", a => FieldContext.Expect(!live || Int(a["count"]) >= 1, "the checkpoint is not listed: " + a.ToJsonString()));
        var after = live && Read(ctx, "a pattern added after the checkpoint", () => { link.AddLoadPattern("CPM-AFTER-CP", "Other", 0); return true; }, false);
        T("restore_checkpoint", """{"name":"cpm-cp1"}""", _ =>
            FieldContext.Expect(!after || !link.LoadPatterns().Any(p => p.Name == "CPM-AFTER-CP"), "the pattern added after the checkpoint is still there"));
        No("restore_checkpoint", """{"name":"no-such-checkpoint"}""", "no checkpoint", "tool restore_checkpoint says no to a checkpoint that is not there");

        // ---- opening a model file in the program (a tester's SAFE 20 chat could not open E:\Pro max\LEVEL 29.FDB, 2026-11-26) ----
        // The checkpoint's copy, then the scratch model's own file again: both through File.OpenFile, as the engineer's would be.
        var ownFile = scratchFile.Length > 0 ? Path.ChangeExtension(scratchFile, CsiTools.ModelFiles(program)[0]) : "";
        if (checkpoint is { } copyFile && ownFile.Length > 0 && File.Exists(ownFile))
        {
            T("open_model", JsonSerializer.Serialize(new { path = copyFile }), _ =>
                FieldContext.Expect(ScratchProgram.SameName(link.Info().File, copyFile), $"{name} should have the checkpoint's copy open; it has {link.Info().File}"),
                $"tool open_model opens a {CsiTools.ModelFiles(program)[0]} file in {name}");
            T("open_model", JsonSerializer.Serialize(new { path = ownFile }), _ =>
                FieldContext.Expect(ScratchProgram.SameName(link.Info().File, scratchFile), $"{name} should have the scratch model open again; it has {link.Info().File}"),
                "tool open_model opens the scratch model's own file again");
        }
        else ctx.Skip($"tool open_model opens a {CsiTools.ModelFiles(program)[0]} file in {name}", live ? "the checkpoint's copy or the scratch model's file is not there to open" : NoApi);
        ctx.Refused("open_model", JsonSerializer.Serialize(new { path = Path.Combine(ctx.Folder, "not a model.rvt") }), "opens", "tool open_model says no to a file that is not a model of this program");

        // ---- a model of its own: stories (ETABS), then a building in one call ----
        if (offered.Contains("set_stories"))
        {
            if (live)
                ctx.Step($"a blank {name} model for the stories", () =>
                {
                    link.Raw("start a blank model", m => { object model = m; ApiCall.Must(model, "InitializeNewModel", CsiTools.D(("Units", 6)), "start a new model"); return ApiCall.Must(model, "File.NewBlank", null, "start a blank model"); });
                    return "blank";
                });
            T("set_stories", """{"stories":[{"name":"CPM-L1","height":3.5},{"name":"CPM-L2","height":3}]}""", a =>
                FieldContext.Expect(link.Stories().Any(s => s.Name == "CPM-L2" && Math.Abs(s.Elevation - 6.5) < 1e-3), "CPM-L2 at 6.5 was expected: " + a.ToJsonString()));
        }
        T("generate_building", """{"spans_x":[6,6],"spans_y":[5],"stories":2,"story_height":3.2}""", a =>
        {
            if (isSafe)
                FieldContext.Expect(Int(a["slabs"]) == 2 && Int(a["supports"]) == 6 && link.Areas().Count == 2, "SAFE: 2 slabs on 6 supports were expected: " + a.ToJsonString());
            else
            {
                FieldContext.Expect(Int(a["columns"]) == 12 && Int(a["beams"]) == 14 && Int(a["slabs"]) == 4 && Int(a["supports"]) == 6, "12 columns, 14 beams, 4 slabs and 6 supports were expected: " + a.ToJsonString());
                FieldContext.Expect(link.Frames().Count == 26 && link.Areas().Count == 4, $"the new model should hold 26 frames and 4 areas; {name} has {link.Frames().Count} and {link.Areas().Count}");
            }
        });

        // The scratch model back in the window, under its own name, for the parts that follow (CSI ProMax's own tools).
        if (live && checkpoint is not null && scratchFile.Length > 0)
            ctx.Step($"the scratch model is open again in the scratch {name} window", () =>
            {
                CsiTools.OpenFile(link, "open the checkpoint of the scratch model", checkpoint);
                CsiTools.Call(link, "save it as the scratch model", "File.Save", CsiTools.D(("FileName", scratchFile)));
                FieldContext.Expect(ScratchProgram.SameName(link.Info().File, scratchFile), $"{name} has {link.Info().File} open, not the scratch model");
                return Path.GetFileName(scratchFile);
            });
    }
}
