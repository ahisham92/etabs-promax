using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CsiProMax.ETABS;

namespace CsiProMax.Tools;

/// <summary>
/// The catch-all: Claude writes a short C# snippet against ETABS's API and it runs on the open model.
/// This covers anything the dedicated tools don't. ETABS has no undo for API changes, so the read-only variant refuses
/// code that calls anything that changes the model (ReadOnlyCheck).
/// </summary>
public abstract class ETABSCodeToolBase : IEtabsTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool ModifiesModel { get; }

    public virtual string PropertiesJson => """
    {
      "code": {"type": "string", "description": "C# method body. Variables available: sap (the open model: ETABS's cSapModel, used as dynamic, for example sap.FrameObj.GetNameList(ref n, ref names)), log (StringBuilder for notes), args. Every API call returns 0 when it worked: wrap calls as Ok(sap.X.Y(...), \"what it does\") so a refusal stops the code with its reason. Declare ref and out arguments as typed locals first (int n = 0; string[] names = null;). Must end with a return statement; return counts, names or small anonymous objects. Namespaces imported: System, System.Linq, System.Collections.Generic."},
      "description": {"type": "string", "description": "What the code does, in a few words. Shown to the user before it runs."}
    }
    """;

    public string[] Required => ["code", "description"];

    protected static readonly string CodeGuidance =
        " Write a C# method body using ETABS's API (the cSapModel of the running ETABS, as the dynamic variable sap). Use dedicated tools " +
        "first when one fits; use this for anything they can't do. If compilation fails, the errors come back with line numbers so you can " +
        "fix and retry. ETABS has no undo for what the API changes: nothing is rolled back when the code fails part way, so check " +
        "inputs before the first change and return what was changed. Units are the model's present units (get_model_context says which). " +
        $"Keep each call short: a call still running after {CapSeconds} s is stopped at its next loop step. Do big work in batches, one " +
        "call after another. log.AppendLine progress or partial results: wait_for_job shows the end of the log while it runs, and the log " +
        "comes back even if the code stops early. A call that runs past about 45 s returns 'still running as job N'; collect the result " +
        "with wait_for_job.";

    /// <summary>
    /// The longest one code call may hold ETABS's thread. Past it the snippet stops at its next loop step (the checks are
    /// added to every snippet, ScriptSource.Variants) and is rolled back, so no snippet keeps ETABS frozen for minutes
    /// (2026-10-27: a 12-minute dry run froze ETABS until it was closed).
    /// </summary>
    internal const int CapSeconds = 90;

    internal static string CapText(int seconds) =>
        $"Stopped after {seconds} s. What it changed before that stays in the model (ETABS has no undo for API changes): read the " +
        "model back before carrying on. Do the work in smaller steps, one call after another, each finishing well under a minute.";

    public virtual ToolResult Execute(EtabsApp app, ToolArgs args) =>
        Counted(app, RunScript(app, args.String("code"), args.String("description"), ModifiesModel, new Dictionary<string, object?>(), capSeconds: CapSeconds), args);

    /// <summary>The same code written again and again: the third time it worked, it is saved as a tool of its own (CodeRepeats).</summary>
    /// <summary>
    /// Code that worked is also kept with what it did (WorkedCode), so the next chat or job adapts it instead of writing
    /// it again; a call that failed ends with the kept code that did similar work.
    /// </summary>
    protected ToolResult Counted(EtabsApp app, ToolResult result, ToolArgs args)
    {
        var (code, description) = (args.String("code"), args.String("description"));
        if (result.IsError)
            return WorkedCode.PointerFor(description, code) is { Length: > 0 } pointer ? result with { Text = result.Text + "\n\n" + pointer } : result;
        var model = "";
        model = Host.ModelWatch.Current?.Title ?? "";
        WorkedCode.Note(code, description, Name, args.OptBool("own_transactions"), model);
        return CodeRepeats.Note(code, description) is { } tip ? result with { Text = result.Text + "\n\n" + tip } : result;
    }

    /// <summary>
    /// Compiles and runs a snippet, inside one transaction when it changes the model. Learned tools use this too.
    /// With ownTransactions the snippet opens and commits its own transactions (needed for StairsEditScope and other
    /// edit scopes, which cannot start inside an open transaction); a transaction group around it keeps the run
    /// atomic and makes it one Undo step.
    /// </summary>
    internal static ToolResult RunScript(EtabsApp app, string code, string description, bool modifiesModel,
        IDictionary<string, object?> values, bool ownTransactions = false, int capSeconds = 0)
    {
        // The time cap, unless the caller set its own (the field test's per-step cap).
        if (capSeconds <= 0 || Bridge.Jobs.Running is not { Limit: null } capped)
            return RunScriptCore(app, code, description, modifiesModel, values, ownTransactions);
        var limit = new Bridge.StepLimit { Reason = CapText(capSeconds) };
        capped.Limit = limit;
        using var timer = new Timer(_ => limit.TryHit(), null, capSeconds * 1000, Timeout.Infinite);
        try { return RunScriptCore(app, code, description, modifiesModel, values, ownTransactions); }
        finally
        {
            limit.Finish();
            if (capped.Limit == limit) capped.Limit = null;
        }
    }

    private static ToolResult RunScriptCore(EtabsApp app, string code, string description, bool modifiesModel,
        IDictionary<string, object?> values, bool ownTransactions)
    {
        // ETABS can't undo what the API changes, so code offered as read-only is checked for calls that change the model.
        if (!modifiesModel && ReadOnlyCheck.Changes(code) is { } change)
            return ToolResult.Error($"This code calls {change}, which changes the model, so it can't run as a read-only query. Run it with run_etabs_code.");

        var (run, errors) = ScriptCompiler.Compile(code);
        if (run is null) return ToolResult.Error("The code did not compile:\n" + errors);

        var log = new StringBuilder();
        // wait_for_job shows the end of the log as progress while a long snippet runs.
        if (Bridge.Jobs.Running is { } job) job.Log = log;
        object? result;
        try
        {
            result = app.Link.Raw(Trim(description, 60), sap => run(sap, log, values));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"The code failed{(modifiesModel ? ". What it changed before failing stays in the model: read it back before trying again" : "")}. {Unwrap(ex)}\n{log}");
        }
        return ToolResult.Ok(new { description, result = SafeJson(result), log = log.ToString() });
    }

    private static string Unwrap(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        var line = ScriptCompiler.LineOf(ex);
        return $"{ex.GetType().Name}: {ex.Message}{(line is null ? "" : $" (line {line})")}{ScriptSource.RuntimeHint(ex.Message)}";
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    /// <summary>Turns the snippet's return value into JSON, falling back to text for objects that don't serialize.</summary>
    private static JsonElement SafeJson(object? value)
    {
        string json;
        try
        {
            json = value switch
            {
                null => "null",
                _ => JsonSerializer.Serialize(value, new JsonSerializerOptions
                {
                    ReferenceHandler = ReferenceHandler.IgnoreCycles, MaxDepth = 8,
                }),
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException or ArgumentException)
        {
            json = JsonSerializer.Serialize(value?.ToString());
        }
        if (json.Length > 60000) json = JsonSerializer.Serialize(json.Substring(0, 60000) + " …(truncated)");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public sealed class RunETABSQueryCodeTool : ETABSCodeToolBase
{
    public override string Name => "run_etabs_query_code";
    public override string Description =>
        "Run read-only C# against ETABS's API to answer a question the other tools can't (any object, property, " +
        "assignment, load, result or setting). The code is checked first: a call that changes the model is refused here, " +
        "use run_etabs_code for it. Results (forces, reactions, drifts) need a finished analysis." + CodeGuidance;
    public override bool ModifiesModel => false;

    public override string PropertiesJson => """
    {
      "code": {"type": "string", "description": "C# method body. Variables available: sap (the open model: ETABS's cSapModel, used as dynamic, for example sap.FrameObj.GetNameList(ref n, ref names)), log (StringBuilder for notes), args. Every API call returns 0 when it worked: wrap calls as Ok(sap.X.Y(...), \"what it does\") so a refusal stops the code with its reason. Declare ref and out arguments as typed locals first (int n = 0; string[] names = null;). Must end with a return statement; return counts, names or small anonymous objects. Namespaces imported: System, System.Linq, System.Collections.Generic."},
      "description": {"type": "string", "description": "What the code does, in a few words."}
    }
    """;

    public override ToolResult Execute(EtabsApp app, ToolArgs args) =>
        Counted(app, RunScript(app, args.String("code"), args.String("description"), false, new Dictionary<string, object?>(), args.OptBool("own_transactions"),
            CapSeconds), args);
}

public sealed class RunETABSCodeTool : ETABSCodeToolBase
{
    public override string Name => "run_etabs_code";
    public override string Description =>
        "Run C# that changes the model through ETABS's API, for anything the other tools can't do (grids, stories, " +
        "materials, section properties, releases, diaphragms, mass source, load cases, response spectrum and wind " +
        "definitions, design settings, bulk edits...). ETABS has no undo for it: the user sees the code before it runs, " +
        "and what it changed before a failure stays. A locked model (after analysis) must be unlocked first." + CodeGuidance;
    public override bool ModifiesModel => true;

    public override string PropertiesJson => """
    {
      "code": {"type": "string", "description": "C# method body. Variables available: sap (the open model: ETABS's cSapModel, used as dynamic, for example sap.FrameObj.GetNameList(ref n, ref names)), log (StringBuilder for notes), args. Every API call returns 0 when it worked: wrap calls as Ok(sap.X.Y(...), \"what it does\") so a refusal stops the code with its reason. Declare ref and out arguments as typed locals first (int n = 0; string[] names = null;). Must end with a return statement; return counts, names or small anonymous objects. Namespaces imported: System, System.Linq, System.Collections.Generic."},
      "description": {"type": "string", "description": "What the code does, in a few words. Shown to the user before it runs."}
    }
    """;

    public override ToolResult Execute(EtabsApp app, ToolArgs args) =>
        Counted(app, RunScript(app, args.String("code"), args.String("description"), ModifiesModel, new Dictionary<string, object?>(),
            args.OptBool("own_transactions"), CapSeconds), args);
}

/// <summary>Compiles snippets with Roslyn, caching by source so a retried snippet doesn't compile twice.</summary>
internal static class ScriptCompiler
{
    public delegate object? Script(object sap, StringBuilder log, IDictionary<string, object?> args);

    private sealed record Entry(Script Run, object? Context);

    private static readonly Dictionary<string, Entry> Cache = new();
    private static List<MetadataReference>? _references;

    public static (Script? Run, string Errors) Compile(string code) => Compile(code, null, null);

    /// <summary>
    /// Compiles the snippet with stop checks added (ScriptSource.Variants: the most checked version that compiles; the
    /// errors reported are those of the snippet as written). <paramref name="cancel"/> and <paramref name="reason"/>
    /// replace the running job's stop request (the field test's probe); such a compile isn't cached.
    /// </summary>
    internal static (Script? Run, string Errors) Compile(string code, Func<bool>? cancel, Func<string?>? reason)
    {
        var shared = cancel is null;
        if (shared && Cache.TryGetValue(code, out var cached)) return (cached.Run, "");

        // The most checked version first. When it doesn't compile, the snippet as written is tried before the
        // in-between one, so a snippet with a real mistake costs two compiles, not three, and its own errors come back.
        var variants = ScriptSource.Variants(code);
        var order = variants.Count == 3 ? new List<string> { variants[0], variants[2], variants[1] } : variants;
        var errors = "";
        for (var i = 0; i < order.Count; i++)
        {
            var (assembly, context, failed) = Emit(order[i]);
            if (assembly is null)
            {
                if (order[i] == code) return (null, failed);
                errors = failed;
                continue;
            }
            // The snippet as written compiled after a checked version didn't: use the in-between one if it compiles.
            if (order[i] == code && i + 1 < order.Count && Emit(order[i + 1]) is { Assembly: { } better } loops)
                (assembly, context) = (better, loops.Context);
            var type = assembly.GetType("CsiProMaxScript")!;
            var method = type.GetMethod("Run")!;
            // Lets a long snippet stop when the user asks (cancel_job, Stop, the Rescue window) or its time is up: the
            // added checks and CheckCancel() throw, Cancelled() tells, and the message says why.
            type.GetField("CancelProbe")?.SetValue(null, cancel ?? (() => Bridge.Jobs.CancelRequested));
            type.GetField("StopReason")?.SetValue(null, reason is null ? () => Bridge.Jobs.StopReason : new Func<string?>(reason));
            Script run = (sap, log, args) => method.Invoke(null, [sap, log, args]);
            if (!shared) return (run, "");
            if (Cache.Count > 50) Evict();
            Cache[code] = new Entry(run, context);
            return (run, "");
        }
        return (null, errors);
    }

    private static (Assembly? Assembly, object? Context, string Errors) Emit(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(ScriptSource.Wrap(code), new CSharpParseOptions(LanguageVersion.Latest));
        // No member whose signature names ImmutableArray (System.Collections.Immutable) is called from here: the
        // options come from Roslyn's own defaults via With*, and the diagnostics are read as a plain IEnumerable. On
        // ETABS 2024 this DLL and Roslyn could otherwise disagree about which System.Collections.Immutable they use
        // (another add-in's copy), and the CSharpCompilationOptions constructor was then "not found".
        var compilation = CSharpCompilation.Create("CsiProMaxScript_" + Guid.NewGuid().ToString("N"), [tree], References());
        compilation = compilation.WithOptions(compilation.Options
            .WithOutputKind(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithNullableContextOptions(NullableContextOptions.Disable));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(pe, pdb);
        if (!emitted.Success)
        {
            var errors = Diagnostics(emitted)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(15)
                .Select(d => $"line {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}{MemberHint(compilation, d)}");
            return (null, null, string.Join("\n", errors));
        }

        var (assembly, context) = Load(pe.ToArray(), pdb.ToArray());
        return (assembly, context, "");
    }

    /// <summary>
    /// For "'X' does not contain a definition for 'Y'" (CS1061, CS0117): the members of X closest to Y in this ETABS
    /// version's API, so the next try uses a name that exists. "" when none is close or anything goes wrong.
    /// </summary>
    private static string MemberHint(CSharpCompilation compilation, Diagnostic d)
    {
        if (d.Id is not ("CS1061" or "CS0117") || d.Location.SourceTree is not { } tree) return "";
        try
        {
            var node = tree.GetRoot().FindNode(d.Location.SourceSpan);
            var access = node.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>().FirstOrDefault();
            if (access is null) return "";
            var model = compilation.GetSemanticModel(tree);
            var type = model.GetTypeInfo(access.Expression).Type ?? model.GetSymbolInfo(access.Expression).Symbol as ITypeSymbol;
            if (type is null || type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error) return "";
            var names = new List<string>();
            for (var t = type; t is not null && t.SpecialType != Microsoft.CodeAnalysis.SpecialType.System_Object; t = t.BaseType)
                names.AddRange(Members(t).Where(m => m.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public && m.CanBeReferencedByName
                    && m.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or Microsoft.CodeAnalysis.SymbolKind.Method or Microsoft.CodeAnalysis.SymbolKind.Field or Microsoft.CodeAnalysis.SymbolKind.Event).Select(m => m.Name));
            var close = ScriptSource.Closest(access.Name.Identifier.ValueText, names);
            return close.Count > 0 ? $" (closest members of {type.Name}: {string.Join(", ", close)})" : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>A type's members without naming ImmutableArray in this assembly (see Compile).</summary>
    private static IEnumerable<ISymbol> Members(ITypeSymbol type) =>
        (typeof(INamespaceOrTypeSymbol).GetMethod("GetMembers", Type.EmptyTypes)?.Invoke(type, null) as System.Collections.IEnumerable)?.OfType<ISymbol>() ?? [];

    /// <summary>EmitResult.Diagnostics without naming its ImmutableArray type in this assembly (see Compile).</summary>
    private static IEnumerable<Diagnostic> Diagnostics(Microsoft.CodeAnalysis.Emit.EmitResult result) =>
        (result.GetType().GetProperty("Diagnostics")?.GetValue(result) as System.Collections.IEnumerable)?.OfType<Diagnostic>() ?? [];

    /// <summary>Line within the snippet where an exception was thrown, when the stack trace has it.</summary>
    public static int? LineOf(Exception ex)
    {
        var trace = new System.Diagnostics.StackTrace(ex, true);
        foreach (var frame in trace.GetFrames() ?? [])
            if (frame.GetFileName() == "snippet" && frame.GetFileLineNumber() > 0) return frame.GetFileLineNumber();
        return null;
    }

    /// <summary>Loads a compiled snippet. On .NET 8+ also returns its load context, so it can be unloaded later.</summary>
    private static (Assembly Assembly, object? Context) Load(byte[] image, byte[] symbols)
    {
#if NETFRAMEWORK
        // .NET Framework can't unload an assembly short of the whole AppDomain; the cache limit keeps this small.
        return (Assembly.Load(image, symbols), null);
#else
        // Each snippet gets its own collectible context, unloaded when it leaves the cache, so a long session
        // doesn't keep every snippet it ever ran in memory. ETABS API types resolve from the context that loaded ETABS.
        var context = new System.Runtime.Loader.AssemblyLoadContext("CsiProMaxScript", isCollectible: true);
        return (context.LoadFromStream(new MemoryStream(image), new MemoryStream(symbols)), context);
#endif
    }

    /// <summary>Empties the cache. Snippets only run on ETABS's main thread, as does this, so none is running now.</summary>
    private static void Evict()
    {
#if !NETFRAMEWORK
        // Unload only starts the process: a context stays alive while anything (an event handler a snippet
        // registered, say) still references it, and is freed once nothing does.
        foreach (var entry in Cache.Values)
            if (entry.Context is System.Runtime.Loader.AssemblyLoadContext context) context.Unload();
#endif
        Cache.Clear();
    }

    private static List<MetadataReference> References()
    {
        if (_references is not null) return _references;

        // One reference per assembly name; the first one found wins.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var name = Path.GetFileNameWithoutExtension(path!);
            if (!byName.ContainsKey(name)) byName[name] = path!;
        }

#if NETFRAMEWORK
        Add(typeof(object).Assembly.Location);
        var facades = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "Facades", "netstandard.dll");
        Add(facades);
#else
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (var path in tpa.Split(Path.PathSeparator))
                if (Path.GetFileName(path).StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals("Microsoft.CSharp.dll", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                    Add(path);
#endif
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            var name = assembly.GetName().Name ?? "";
            if (name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.CSharp", StringComparison.Ordinal) ||
                name is "mscorlib" or "netstandard" or "ETABSAPI" or "ETABSAPIUI")
            {
                try { Add(assembly.Location); }
                catch (NotSupportedException) { }
            }
        }

        _references = byName.Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        return _references;
    }
}


/// <summary>
/// Whether code offered as a read-only query calls something that changes the model. ETABS's API names say it: reads
/// are Get..., Count and the table reads; everything else on the model (Set..., Add..., Delete..., ChangeName, Save,
/// analysis and design runs, file calls) changes it or the files beside it.
/// </summary>
internal static class ReadOnlyCheck
{
    private static readonly System.Text.RegularExpressions.Regex Call = new(
        @"\.\s*((Set|Add|Delete|Remove|Change|Create|New|Open|Save|Run|Start|Apply|Import|Export|Unlock|Lock|Initialize|Clear|Insert|Replace|Modify|Move|Mirror|Rotate|Divide|Merge|Assign|Design|Verify|Reset)[A-Za-z0-9_]*)\s*\(",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Reads whose names start like a change, and calls on the log or on plain .NET lists that are not ETABS's.
    private static readonly HashSet<string> Fine = new(StringComparer.Ordinal)
    {
        "Add", "AddRange", "Clear", "Remove", "RemoveAll", "RemoveAt", "Insert", "Replace", "StartsWith", "SetEquals",
    };

    /// <summary>The first changing call's name, or null when the code only reads.</summary>
    public static string? Changes(string code)
    {
        foreach (System.Text.RegularExpressions.Match m in Call.Matches(code))
        {
            var name = m.Groups[1].Value;
            if (Fine.Contains(name)) continue;
            return name;
        }
        return null;
    }
}
