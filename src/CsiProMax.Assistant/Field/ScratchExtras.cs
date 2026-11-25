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
    public const string LinkProperty = "CPM-LIN", Group = "CPM-CUT", SectionCut = "CPM-SC", Pushover = "CPM-PUSH", Function = "CPM-TH", History = "CPM-MHL";

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
            Must(link, "define a section cut by the group", "SectCut.SetByGroup", ("Name", SectionCut), ("GroupName", Group), ("MyType", 1));
            return $"{SectionCut} by group {Group}";
        });
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
        if (isEtabs) ctx.Skip(history, $"{name} 23's API has no call to define a time history function or the steps of a history case (cFunctionTH has no methods); they go in only through {name}'s tables, by hand");
        else
        {
            var modal = Read(ctx, "whether the model has a modal case", () => HasModalCase(link), false);
            if (!modal) ctx.Skip(history, $"the scratch model has no modal case for {name} to build the history on");
            else
                ctx.Step(history, () =>
                {
                    Must(link, "define a time history function", "Func.FuncTH.SetUser", ("Name", Function), ("NumberItems", 5), ("MyTime", new[] { 0.0, 0.1, 0.2, 0.3, 0.4 }), ("Value", new[] { 0.0, 1.0, 0.0, -1.0, 0.0 }));
                    Must(link, "add a linear modal history case", "LoadCases.ModHistLinear.SetCase", ("Name", History));
                    Must(link, "give the history case its load", "LoadCases.ModHistLinear.SetLoads", ("Name", History), ("NumberLoads", 1), ("LoadType", new[] { "Accel" }), ("LoadName", new[] { "U1" }),
                        ("Func", new[] { Function }), ("SF", new[] { 1.0 }), ("Tf", new[] { 1.0 }), ("At", new[] { 0.0 }), ("CSys", new[] { "Global" }), ("Ang", new[] { 0.0 }));
                    Must(link, "give the history case its steps", "LoadCases.ModHistLinear.SetTimeStep", ("Name", History), ("NStep", 20), ("Dt", 0.02));
                    return $"{History}: {Function} as ground acceleration in X, 20 steps of 0.02 s";
                });
        }
        ctx.Skip("hinges on a beam (for the Viewer's hinge results)", $"{name}'s API reads hinges and has no call to assign one; they are assigned only in {name}'s own forms or tables");
        ctx.Skip("a tendon or a cable (for the Viewer's tendon and cable results)", isEtabs ? $"{name} 23's API has no call to draw a tendon, and {name} has no cables" : "not built into the scratch model yet: a cable needs a nonlinear case of its own to stand");
    }

    private static bool HasModalCase(ICsiLink link) => link.LoadCases().Any(c => link.Raw("read a load case's type", m =>
    {
        var call = ApiCall.Invoke((object)m, "LoadCases.GetTypeOAPI", D(("Name", c)));
        return ApiCall.Worked(call) && ApiCall.Value(call, "CaseType")?.ToString() is { } type && (type.Contains("Modal", StringComparison.OrdinalIgnoreCase) || type == "3") && !type.Contains("Hist", StringComparison.OrdinalIgnoreCase);
    }) is true);

    /// <summary>
    /// After the analysis: the program's tables hold rows for what <see cref="Add"/> put in, by the names each
    /// program gives them. These are the tables the review pack takes for the Viewer's displays.
    /// </summary>
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
