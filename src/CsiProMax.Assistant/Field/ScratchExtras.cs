using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using static CsiProMax.Assistant.FieldParts;

namespace CsiProMax.Assistant;

/// <summary>
/// What the scratch model is given before its first analysis so that the Viewer has something to show for every
/// display it offers (Ahmed 2026-11-25: the field test covers everything; 13 Viewer items were greyed out in the
/// 0.4.33 run because the scratch models had no springs, links, section cuts, pushover or history cases).
/// Each piece goes in through the program's own API, by the shapes read from ETABS 23 and SAP2000 26 on a real PC,
/// and is one step in the report. <see cref="Check"/> then says, after the analysis, which of the program's tables
/// have rows for them. What a program's API cannot do (hinges in both, time history functions and tendons in
/// ETABS 23) is said as not tested, with the reason.
/// </summary>
public static class ScratchExtras
{
    public const string LinkProperty = "CPM-LIN", Group = "CPM-CUT", SectionCut = "CPM-SC", Pushover = "CPM-PUSH", Function = "CPM-TH", History = "CPM-MHL", Tendon = "CPM-TEN", ModalForHistory = "CPM-MODAL-H", Brace = "CPM-BR";

    private const string NoApi = "the stand-in has no program API behind it (a live program is needed)";

    private static Dictionary<string, object?> D(params (string Name, object? Value)[] values) => CsiTools.D(values);

    private static JsonObject Must(ICsiLink link, string what, string path, params (string Name, object? Value)[] values) =>
        (JsonObject)link.Raw(what, m => ApiCall.Must((object)m, path, D(values), what))!;

    /// <summary>Before the first analysis: a spring, a link, a section cut, a nonlinear static case and a history case.</summary>
    public static void Add(FieldContext ctx)
    {
        var link = ctx.Link;
        var name = link.Program.Name;
        var isEtabs = link.Program.Key == CsiProgram.Etabs.Key;
        if (link is OfflineEtabs or MemoryEtabs)
        {
            ctx.Skip($"{name}'s scratch model gets a spring, a link, a section cut, a pushover case and a history case", NoApi);
            return;
        }
        var points = Read(ctx, "the joints", link.Points, []);
        var top = points.Count == 0 ? [] : points.Where(p => Math.Abs(p.Z - points.Max(q => q.Z)) < 1e-6).OrderBy(p => p.X).ThenBy(p => p.Y).Select(p => p.Name).Take(2).ToList();
        var columns = Read(ctx, "the columns", () => link.Frames().Where(f => f.Kind == "column").Select(f => f.Name).Take(2).ToList(), []);
        if (top.Count < 2 || columns.Count < 2)
        {
            ctx.Fail($"{name}'s scratch model gets a spring, a link, a section cut, a pushover case and a history case", $"two top joints and two columns were needed; the model gave {top.Count} and {columns.Count}");
            return;
        }

        ctx.Step($"a spring on joint {top[0]} (for the Viewer's spring results)", () =>
        {
            Must(link, "give a joint a spring", "PointObj.SetSpring", ("Name", top[0]), ("K", new double[] { 10, 10, 10, 0, 0, 0 }), ("ItemType", 0), ("IsLocalCSys", false), ("Replace", true));
            return "10 in X, Y and Z";
        });
        ctx.Step($"a linear link between joints {top[0]} and {top[1]} (for the Viewer's link results)", () =>
        {
            Must(link, "define a linear link property", "PropLink.SetLinear", ("Name", LinkProperty), ("DOF", new[] { true, false, false, false, false, false }), ("Fixed", new bool[6]),
                ("Ke", new double[] { 500, 0, 0, 0, 0, 0 }), ("Ce", new double[6]), ("DJ2", 0.0), ("DJ3", 0.0), ("KeCoupled", false), ("CeCoupled", false));
            var made = Must(link, "draw a link between two joints", "LinkObj.AddByPoint", ("Point1", top[0]), ("Point2", top[1]), ("Name", ""), ("IsSingleJoint", false), ("PropName", LinkProperty), ("UserName", ""));
            return $"link {ApiCall.Value(made, "Name")} with {LinkProperty}";
        });
        ctx.Step($"a section cut through columns {columns[0]} and {columns[1]} (for the Viewer's section cut forces)", () =>
        {
            Must(link, "make a group", "GroupDef.SetGroup", ("Name", Group));
            foreach (var column in columns)
                Must(link, "put a column in the group", "FrameObj.SetGroupAssign", ("Name", column), ("GroupName", Group), ("Remove", false), ("ItemType", 0));
            var how = ApiWorkarounds.SectionCutByGroup(link, SectionCut, Group);
            return $"{SectionCut} by group {Group}, {how}";
        });
        if (isEtabs)
        {
            // A diagonal member, so ETABS lists "Element Forces - Braces" for the scratch model (Ahmed, 2026-11-26: a table
            // the Viewer takes must be tested, not left out because the model had no member of its kind).
            var low = points.Count == 0 ? double.NaN : points.Min(p => p.Z);
            var upper = points.First(p => p.Name == top[1]);
            var foot = points.Where(p => Math.Abs(p.Z - low) < 1e-6 && (Math.Abs(p.X - upper.X) > 1e-6 || Math.Abs(p.Y - upper.Y) > 1e-6)).OrderBy(p => Math.Abs(p.X - upper.X) + Math.Abs(p.Y - upper.Y)).FirstOrDefault();
            if (foot is null) ctx.Fail($"a brace from the base to joint {top[1]} (for the brace forces table)", "the model has no base joint away from that joint");
            else ctx.Step($"a brace from base joint {foot.Name} to joint {top[1]} (for the brace forces table)", () =>
            {
                var section = Must(link, "read a column's section", "FrameObj.GetSection", ("Name", columns[0]), ("PropName", ""), ("SAuto", ""));
                var made = Must(link, "draw a brace between two joints", "FrameObj.AddByPoint", ("Point1", foot.Name), ("Point2", top[1]), ("Name", ""), ("PropName", ApiCall.Value(section, "PropName")), ("UserName", Brace));
                return $"brace {ApiCall.Value(made, "Name")} of {ApiCall.Value(section, "PropName")}";
            });
        }
        ctx.Step($"a nonlinear static case pushed to a displacement, every state saved (for the Viewer's pushover curve)", () =>
        {
            var patterns = link.LoadPatterns();
            var pattern = (patterns.FirstOrDefault(p => p.Type == "Seismic") ?? patterns.First()).Name;
            Must(link, "add a nonlinear static case", "LoadCases.StaticNonlinear.SetCase", ("Name", Pushover));
            Must(link, "give the case its load", "LoadCases.StaticNonlinear.SetLoads", ("Name", Pushover), ("NumberLoads", 1), ("LoadType", new[] { "Load" }), ("LoadName", new[] { pattern }), ("SF", new[] { 1.0 }));
            Must(link, "push the case to a displacement", "LoadCases.StaticNonlinear.SetLoadApplication", ("Name", Pushover), ("LoadControl", 2), ("DispType", 2), ("Displ", 0.1), ("Monitor", 1), ("DOF", 1), ("PointName", top[0]), ("GDispl", ""));
            Must(link, "save every state of the case", "LoadCases.StaticNonlinear.SetResultsSaved", ("Name", Pushover), ("SaveMultipleSteps", true), ("MinSavedStates", 10), ("MaxSavedStates", 100), ("PositiveOnly", true));
            return $"{Pushover}: {pattern} until joint {top[0]} moves 0.1 in X";
        });

        // A history case stands on a modal case; without one the program's analysis would stop on it.
        const string history = "a time history function and a linear modal history case (for the Viewer's plot functions)";
        if (isEtabs)
        {
            // ETABS 23's API cannot define a history function (cFunctionTH has no methods), so the function and the case go
            // in through ETABS's own tables, by the keys and fields of its "Table and Field Keys.xml" (read on Ahmed's PC,
            // 2026-11-26), unless the Modeler's file brought a case already.
            var there = Read(ctx, "the history cases", () => CasesOfType(link, "Hist"), []);
            if (there.Count > 0) ctx.Step(history, () => $"already in the model from the Modeler's file: {string.Join(", ", there)}");
            else
                ctx.Step(history, () =>
                {
                    var modalCase = ModalCaseOf(link);
                    if (modalCase is null)
                    {
                        Must(link, "add a modal case for the history case", "LoadCases.ModalEigen.SetCase", ("Name", ModalForHistory));
                        modalCase = ModalForHistory;
                    }
                    double[] times = [0.0, 0.1, 0.2, 0.3, 0.4], values = [0.0, 1.0, 0.0, -1.0, 0.0];
                    EditableTables.Put(link, HistoryFunctionTable, "Name",
                        times.Select((t, i) => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["Name"] = Function, ["Time"] = Num(t), ["Value"] = Num(values[i]) }).ToList(),
                        "define a time history function through its table");
                    EditableTables.Put(link, HistoryCaseTable, "Name",
                        [new Dictionary<string, string> { ["Name"] = History, ["LoadType"] = "Acceleration", ["LoadName"] = "U1", ["Function"] = Function, ["LoadSF"] = "1", ["ModalCase"] = modalCase, ["MotionType"] = "Transient", ["NumSteps"] = "20", ["StepSize"] = "0.02" }],
                        "add a linear modal history case through its table");
                    var made = CasesOfType(link, "Hist");
                    FieldContext.Expect(made.Contains(History, StringComparer.OrdinalIgnoreCase), $"{name} took the tables but lists no history case {History} (its history cases: {string.Join(", ", made)})");
                    return $"{History}: {Function} as ground acceleration in X on {modalCase}, 20 steps of 0.02 s, through the tables '{HistoryFunctionTable}' and '{HistoryCaseTable}'";
                });
        }
        else
        {
            var modal = Read(ctx, "whether the model has a modal case", () => HasModalCase(link), false);
            ctx.Step(history, () =>
                {
                    // The history case stands on a modal case: one is added when the model has none (Ahmed, 2026-11-26:
                    // only a missing program or licence leaves a step untested).
                    if (!modal)
                    {
                        Must(link, "add a modal case for the history case", "LoadCases.ModalEigen.SetCase", ("Name", ModalForHistory));
                        ApiWorkarounds.SetNumberModes(link, "ModalEigen", ModalForHistory, 6, 1);
                    }
                    Must(link, "define a time history function", "Func.FuncTH.SetUser", ("Name", Function), ("NumberItems", 5), ("MyTime", new[] { 0.0, 0.1, 0.2, 0.3, 0.4 }), ("Value", new[] { 0.0, 1.0, 0.0, -1.0, 0.0 }));
                    Must(link, "add a linear modal history case", "LoadCases.ModHistLinear.SetCase", ("Name", History));
                    Must(link, "give the history case its load", "LoadCases.ModHistLinear.SetLoads", ("Name", History), ("NumberLoads", 1), ("LoadType", new[] { "Accel" }), ("LoadName", new[] { "U1" }),
                        ("Func", new[] { Function }), ("SF", new[] { 1.0 }), ("Tf", new[] { 1.0 }), ("At", new[] { 0.0 }), ("CSys", new[] { "Global" }), ("Ang", new[] { 0.0 }));
                    Must(link, "give the history case its steps", "LoadCases.ModHistLinear.SetTimeStep", ("Name", History), ("NStep", 20), ("Dt", 0.02));
                    if (!modal) Must(link, "build the history case on that modal case", "LoadCases.ModHistLinear.SetModalCase", ("Name", History), ("ModalCase", ModalForHistory));
                    return $"{History}: {Function} as ground acceleration in X, 20 steps of 0.02 s";
                });
        }
        // Hinges: no API assigns them, so they come with the Modeler's file ("... opened the Modeler's frame hinges").
        Try(ctx, "a tendon section (for the Viewer's tendon and cable results)", () =>
        {
            var material = link.Materials().FirstOrDefault(x => x.Contains("416", StringComparison.Ordinal) || x.Contains("Tendon", StringComparison.OrdinalIgnoreCase)) ?? link.Materials().First();
            Must(link, "define a tendon section", "PropTendon.SetProp", ("Name", Tendon), ("MatProp", material), ("ModelingOption", 1), ("Area", 0.0001));
            if (isEtabs) return $"{Tendon} of {material} (the API of {name} 23 cannot draw a tendon; the section is in its table)";
            var pts = link.Points();
            var i = pts.First(q => q.Name == top[0]); var j = pts.First(q => q.Name == top[1]);
            var made = Must(link, "draw a tendon", "TendonObj.AddByCoord", ("XI", i.X), ("YI", i.Y), ("ZI", i.Z), ("XJ", j.X), ("YJ", j.Y), ("ZJ", j.Z), ("Name", ""), ("PropName", Tendon), ("UserName", ""), ("CSys", "Global"));
            return $"tendon {ApiCall.Value(made, "Name")} with {Tendon} of {material}, modelled as loads";
        });
        Shapes(ctx);
    }

    /// <summary>
    /// A piece of the scratch model that is there only so the Viewer has something to show, by an API call nobody has
    /// run on a real program yet: when the program refuses it, the step is not tested, with the program's own words,
    /// and the model is as it was. A failure here would say CSI ProMax is wrong, and it is the test's setup that is.
    /// </summary>
    private static void Try(FieldContext ctx, string step, Func<string> body) => ctx.Step(step, () =>
    {
        try { return body(); }
        catch (Exception ex) when (ex is EtabsException or MissingMethodException or ToolInputException or InvalidOperationException)
        {
            throw new FieldSkipException($"{ctx.Link.Program.Name} did not take it ({ex.Message})");
        }
    });

    /// <summary>The tables a hinge, a history case and a tendon are written through, with their fields as the program
    /// describes them, in a file beside the report: what the next change to this file is written from.</summary>
    private static void Shapes(FieldContext ctx)
    {
        var link = ctx.Link;
        string[] words = ["Hinge Assign", "Assignments - Hinge", "Time History", "Tendon", "Cable Section", "Section Cut"];
        ctx.Step($"the fields of {link.Program.Name}'s hinge, history, tendon and section cut tables are written down", () =>
        {
            var text = new System.Text.StringBuilder();
            foreach (var t in link.Tables().Where(t => words.Any(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(120))
            {
                text.AppendLine($"## {t.Name} (key {t.Key}, import {t.Import}, {(t.Empty ? "empty" : "has rows")})");
                try
                {
                    text.AppendLine((string)link.Raw("read a table's fields", m =>
                    {
                        int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                        m.DatabaseTables.GetAllFieldsInTable(t.Key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable);
                        keys ??= []; about ??= []; importable ??= [];
                        return string.Join("\n", Enumerable.Range(0, keys.Length).Select(i => $"- {keys[i]}{(i < importable.Length && !importable[i] ? " (not importable)" : "")}: {(i < about.Length ? about[i]?.Trim() : "")}"));
                    })!);
                    if (!t.Empty)
                    {
                        var data = link.ReadTable(t.Key, null, null);
                        foreach (var row in data.Rows.Take(2)) text.AppendLine("  row: " + string.Join(" | ", data.Fields.Zip(row, (f, v) => $"{f}={v}")));
                    }
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { text.AppendLine("  could not be read: " + ex.Message); }
            }
            var file = Path.Combine(Path.GetDirectoryName(FieldRetest.File)!, $"table-shapes-{link.Program.Key}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text.ToString());
            return file;
        });
    }

    private static List<string> CasesOfType(ICsiLink link, string word) => link.LoadCases().Where(c => link.Raw("read a load case's type", m =>
    {
        var call = ApiCall.Invoke((object)m, "LoadCases.GetTypeOAPI", D(("Name", c)));
        return ApiCall.Worked(call) && ApiCall.Value(call, "CaseType")?.ToString() is { } type && type.Contains(word, StringComparison.OrdinalIgnoreCase);
    }) is true).ToList();

    /// <summary>ETABS 23's tables for a user time history function and a linear modal history case.</summary>
    public const string HistoryFunctionTable = "Functions - Time History - User Defined", HistoryCaseTable = "Load Case Definitions - Time History - Linear Modal";

    private static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The first modal (not history) case of the model, or null.</summary>
    private static string? ModalCaseOf(ICsiLink link) => link.LoadCases().FirstOrDefault(c => link.Raw("read a load case's type", m =>
    {
        var call = ApiCall.Invoke((object)m, "LoadCases.GetTypeOAPI", D(("Name", c)));
        return ApiCall.Worked(call) && ApiCall.Value(call, "CaseType")?.ToString() is { } type && (type.Contains("Modal", StringComparison.OrdinalIgnoreCase) || type == "3") && !type.Contains("Hist", StringComparison.OrdinalIgnoreCase);
    }) is true);

    private static bool HasModalCase(ICsiLink link) => link.LoadCases().Any(c => link.Raw("read a load case's type", m =>
    {
        var call = ApiCall.Invoke((object)m, "LoadCases.GetTypeOAPI", D(("Name", c)));
        return ApiCall.Worked(call) && ApiCall.Value(call, "CaseType")?.ToString() is { } type && (type.Contains("Modal", StringComparison.OrdinalIgnoreCase) || type == "3") && !type.Contains("Hist", StringComparison.OrdinalIgnoreCase);
    }) is true);

    /// <summary>
    /// After the analysis: the program's tables hold rows for what <see cref="Add"/> put in, by the names each
    /// program gives them. These are the tables the review pack takes for the Viewer's displays.
    /// </summary>
    /// <summary>
    /// SAFE's scratch model before its analysis: a modal case and a section cut, which SAFE 23.1 has tables for
    /// ("Modal Periods And Frequencies", "Section Cut Forces - Analysis"), so the Viewer's mode shapes, modal plot and
    /// section cut forces are tested on SAFE too (0.4.56 tester run: they were greyed out on an empty pack).
    /// </summary>
    public static void AddSafe(FieldContext ctx)
    {
        var link = ctx.Link;
        if (link is OfflineEtabs or MemoryEtabs) { ctx.Skip("SAFE's scratch model gets a modal case and a section cut", NoApi); return; }
        ctx.Step($"a modal case {SafeModal} of 6 modes (for the Viewer's mode shapes and modal plot)", () =>
        {
            Must(link, "add a modal case", "LoadCases.ModalEigen.SetCase", ("Name", SafeModal));
            return $"{SafeModal}, {ApiWorkarounds.SetNumberModes(link, "ModalEigen", SafeModal, 6, 1)}";
        });
        ctx.Step($"a section cut through a floor (for the Viewer's section cut forces)", () =>
        {
            var floor = link.Areas().FirstOrDefault(a => a.Kind == "floor")?.Name ?? throw new FieldCheckException("the scratch model has no floor to cut");
            Must(link, "make a group", "GroupDef.SetGroup", ("Name", Group));
            Must(link, "put a floor in the group", "AreaObj.SetGroupAssign", ("Name", floor), ("GroupName", Group), ("Remove", false), ("ItemType", 0));
            return $"{SectionCut} by group {Group} (floor {floor}), {ApiWorkarounds.SectionCutByGroup(link, SectionCut, Group)}";
        });
    }

    public const string SafeModal = "CPM-MODAL";

    /// <summary>After SAFE's analysis: its modal and section cut tables have rows for what <see cref="AddSafe"/> put in.</summary>
    public static void CheckSafe(FieldContext ctx)
    {
        var link = ctx.Link;
        if (link is OfflineEtabs or MemoryEtabs) return;
        var known = Read(ctx, "the tables", link.Tables, []);
        var cases = Read(ctx, "the load cases", link.LoadCases, []);
        foreach (var (what, table) in new[] { ("the modal case", "Modal Periods And Frequencies"), ("the section cut's forces", "Section Cut Forces - Analysis") })
            ctx.Step($"SAFE table {table} has rows for {what}", () =>
            {
                var found = known.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)) ?? throw new EtabsException($"SAFE lists no table '{table}'.");
                var data = link.ReadTable(found.Key, cases, []);
                FieldContext.Expect(data.Rows.Count > 0, $"'{table}' has no rows after the analysis ({data.Fields.Count} fields)");
                return $"{data.Rows.Count} rows";
            });
    }

    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        var name = link.Program.Name;
        if (link is OfflineEtabs or MemoryEtabs) return;
        var isEtabs = link.Program.Key == CsiProgram.Etabs.Key;
        var wanted = new List<(string What, string Table)>
        {
            ("the spring", isEtabs ? "Joint Assignments - Springs" : "Joint Spring Assignments 1 - Uncoupled"),
            ("the link's forces", "Element Forces - Links"),
            ("the link's deformations", "Element Deformations - Links"),
            ("the section cut's forces", "Section Cut Forces - Analysis"),
        };
        var known = Read(ctx, "the tables", link.Tables, []);
        var cases = Read(ctx, "the load cases", link.LoadCases, []);
        var combos = Read(ctx, "the combinations", link.Combos, []);
        foreach (var (what, table) in wanted)
            ctx.Step($"{name} table {table} has rows for {what}", () =>
            {
                var found = known.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)) ?? throw new EtabsException($"{name} lists no table '{table}'.");
                var data = link.ReadTable(found.Key, cases, combos);
                FieldContext.Expect(data.Rows.Count > 0, $"'{table}' has no rows after the analysis ({data.Fields.Count} fields)");
                return $"{data.Rows.Count} rows";
            });
    }
}
