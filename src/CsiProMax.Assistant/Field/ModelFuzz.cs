using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.F2k;
using CsiProMax.Model;
using CsiProMax.S2k;

namespace CsiProMax.Assistant;

/// <summary>
/// Robustness for the Modeler (Ahmed 2026-11-25: the standalone must never crash or corrupt the model): every Modeler
/// tool is called many times with input built from its own schema and then bent out of shape: wrong types, empty and
/// overlong names, quotes, negative, huge and tiny numbers, names that are not in the model, and names that are.
/// After each call:
/// <list type="bullet">
/// <item>a refusal is a <see cref="ToolInputException"/> or <see cref="EtabsException"/> (a plain message), never any
/// other exception, and leaves the model exactly as it was;</item>
/// <item>a call that went through leaves a model that still saves as the Modeler's own file, writes and reads back as .e2k, and writes as
/// .s2k and .f2k without throwing.</item>
/// </list>
/// The same seed gives the same calls, so a finding can be run again.
/// </summary>
public static class ModelFuzz
{
    public sealed record Finding(string Tool, string Args, string What);

    /// <summary>Tools left out: they write or open files, or start something outside the model.</summary>
    public static bool Fuzzed(Tool t)
    {
        if (t.Name.Contains("save", StringComparison.Ordinal) || t.Name.Contains("open", StringComparison.Ordinal) || t.Name.Contains("export", StringComparison.Ordinal)
            || t.Name.Contains("import", StringComparison.Ordinal) || t.Name.Contains("pack", StringComparison.Ordinal) || t.Name.Contains("learn", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(t.Schema);
            return !(doc.RootElement.TryGetProperty("properties", out var p) && p.EnumerateObject().Any(x => x.Name is "folder" or "path" or "file" or "files" or "xlsx"));
        }
        catch (JsonException) { return false; }
    }

    public static List<Finding> Run(Func<OfflineEtabs> fresh, IEnumerable<Tool> tools, int callsPerTool, int seed, TimeSpan? budget = null)
    {
        var findings = new List<Finding>();
        var until = DateTime.UtcNow + (budget ?? TimeSpan.FromMinutes(5));
        var random = new Random(seed);
        foreach (var tool in tools.Where(Fuzzed).DistinctBy(t => t.Name))
        {
            if (DateTime.UtcNow > until) { findings.Add(new Finding(tool.Name, "", "not reached: the time for the robustness run was up")); break; }
            var link = fresh();
            for (var i = 0; i < callsPerTool; i++)
            {
                var names = Names(link.Model);
                var args = Make(tool.Schema, names, random);
                var text = args.ToJsonString();
                var before = ModelFile.ToJson(link.Model);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    tool.Invoke(doc.RootElement, link);
                }
                catch (Exception ex) when (ex is ToolInputException or EtabsException)
                {
                    if (ModelFile.ToJson(link.Model) != before) { findings.Add(new Finding(tool.Name, text, "refused (" + Cut(ex.Message) + ") but the model changed")); link = fresh(); }
                    continue;
                }
                catch (Exception ex)
                {
                    findings.Add(new Finding(tool.Name, text, $"{ex.GetType().Name}: {Cut(ex.Message)}" + (ModelFile.ToJson(link.Model) != before ? "; the model changed" : "")));
                    link = fresh();
                    continue;
                }
                if (Broken(link.Model) is { } why) { findings.Add(new Finding(tool.Name, text, why)); link = fresh(); }
            }
        }
        return findings;
    }

    /// <summary>Tools picked at random, one after the other on the same model, so that edits meet each other (a
    /// section deleted under a frame, a story renamed under a load). The model is started again only after a finding.</summary>
    public static List<Finding> RunMixed(Func<OfflineEtabs> fresh, IEnumerable<Tool> tools, int calls, int seed, TimeSpan? budget = null)
    {
        var findings = new List<Finding>();
        var until = DateTime.UtcNow + (budget ?? TimeSpan.FromMinutes(5));
        var random = new Random(seed);
        var list = tools.Where(Fuzzed).DistinctBy(t => t.Name).ToList();
        var link = fresh();
        for (var i = 0; i < calls && DateTime.UtcNow < until; i++)
        {
            var tool = list[random.Next(list.Count)];
            var text = Make(tool.Schema, Names(link.Model), random).ToJsonString();
            var before = ModelFile.ToJson(link.Model);
            try
            {
                using var doc = JsonDocument.Parse(text);
                tool.Invoke(doc.RootElement, link);
            }
            catch (Exception ex) when (ex is ToolInputException or EtabsException)
            {
                if (ModelFile.ToJson(link.Model) != before) { findings.Add(new Finding(tool.Name, text, "refused (" + Cut(ex.Message) + ") but the model changed")); link = fresh(); }
                continue;
            }
            catch (Exception ex)
            {
                findings.Add(new Finding(tool.Name, text, $"call {i}: {ex.GetType().Name}: {Cut(ex.Message)}"));
                link = fresh();
                continue;
            }
            if (Broken(link.Model) is { } why) { findings.Add(new Finding(tool.Name, text, $"call {i}: {why}")); link = fresh(); }
            // A model that has grown past what a test needs is started again.
            if (link.Model.Points.Count > 3000 || link.Model.Stories.Count > 60) link = fresh();
        }
        return findings;
    }

    /// <summary>Why the model no longer saves or writes, or null when it does.</summary>
    public static string? Broken(StructuralModel m)
    {
        string step = "save as the Modeler's own file";
        try
        {
            ModelFile.FromJson(ModelFile.ToJson(m));
            step = "write as .e2k"; var e2k = E2kWriter.Write(m);
            step = "read its .e2k back"; E2kReader.Read(e2k);
            step = "write as .s2k"; S2kWriter.Write(m, "fuzz.s2k");
            if (m.Stories.Count > 1 && m.AreaAssigns.Count > 0)
            {
                step = "write as .f2k";
                try { F2kWriter.Write(m); }
                catch (ArgumentException) { /* a floor SAFE cannot take is said in words */ }
            }
            return null;
        }
        catch (Exception ex) { return $"the model no longer could {step}: {ex.GetType().Name}: {Cut(ex.Message)}"; }
    }

    private static string Cut(string s) => s.Length <= 240 ? s : s[..240] + "...";

    /// <summary>Names the model has, so that some calls aim at real objects: stories, points, label@story, sections, patterns.</summary>
    private static List<string> Names(StructuralModel m)
    {
        var list = new List<string>();
        list.AddRange(m.Stories.Select(s => s.Name));
        list.AddRange(m.LineAssigns.Take(6).Select(x => x.Label + "@" + x.Story));
        list.AddRange(m.AreaAssigns.Take(4).Select(x => x.Label + "@" + x.Story));
        list.AddRange(m.PointAssigns.Take(4).Select(x => x.Point + "@" + x.Story));
        if (m.Lines.Count > 0) list.Add(m.Lines[0].PointI + "@" + m.Stories[^1].Name);
        list.AddRange(m.FrameSections.Take(4).Select(s => s.Name));
        list.AddRange(m.ShellSections.Take(3).Select(s => s.Name));
        list.AddRange(m.Materials.Take(3).Select(s => s.Name));
        list.AddRange(m.LoadPatterns.Take(4).Select(p => p.Name));
        list.AddRange(m.LoadCases.Take(3).Select(c => c.Name));
        list.AddRange(m.Combos.Take(2).Select(c => c.Name));
        list.AddRange(m.Grids.Take(3).Select(g => g.Label));
        return list;
    }

    private static readonly string[] Odd = ["", " ", "\"", "a\"b", "NOPE", "NOPE@NOPE", "@", "x@", "@x", "Story1", "B1@Story1", "C1", "عمود", "M3", "UX", "Dead", "Linear Static", new string('x', 300), "-1", "1e9", "null", "../x"];
    private static readonly double[] Numbers = [0, -1, 1, 0.5, 3, 6, 1e-12, 1000, 1e9, 1e300, -1e300, 12345.678];
    private static readonly int[] Wholes = [0, -1, 1, 2, 3, 11, 50, 1000];

    private static JsonObject Make(string schema, List<string> names, Random r)
    {
        using var doc = JsonDocument.Parse(schema);
        return Value(doc.RootElement, names, r, 0) as JsonObject ?? [];
    }

    private static T Pick<T>(IReadOnlyList<T> list, Random r) => list[r.Next(list.Count)];

    private static JsonNode? Value(JsonElement s, List<string> names, Random r, int depth)
    {
        var type = s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : s.TryGetProperty("properties", out _) ? "object" : null;
        // Now and then the wrong kind of value altogether.
        if (depth > 0 && r.Next(12) == 0) return Pick<JsonNode?>([null, JsonValue.Create("x"), JsonValue.Create(-1), JsonValue.Create(true), new JsonArray(), new JsonObject()], r)?.DeepClone();
        if (s.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array && r.Next(5) > 0)
            return JsonNode.Parse(Pick(e.EnumerateArray().ToList(), r).GetRawText());
        switch (type)
        {
            case "string": return JsonValue.Create(r.Next(2) == 0 && names.Count > 0 ? Pick(names, r) : Pick(Odd, r));
            case "number": return JsonValue.Create(Pick(Numbers, r));
            case "integer": return JsonValue.Create(Pick(Wholes, r));
            case "boolean": return JsonValue.Create(r.Next(2) == 0);
            case "array":
            {
                var a = new JsonArray();
                var n = Pick([0, 1, 1, 2, 3, 5], r);
                for (var i = 0; i < n && depth < 4; i++)
                    a.Add(s.TryGetProperty("items", out var items) ? Value(items, names, r, depth + 1) : JsonValue.Create(Pick(names.Count > 0 ? names : [.. Odd], r)));
                return a;
            }
            case "object":
            {
                var o = new JsonObject();
                if (depth > 4) return o;
                var required = s.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array ? req.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
                if (s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in props.EnumerateObject())
                        if (required.Contains(p.Name) ? r.Next(10) > 0 : r.Next(2) == 0) o[p.Name] = Value(p.Value, names, r, depth + 1);
                }
                else
                {
                    // A free-form object (modifiers, values, dofs): keys the forms use, and some they do not.
                    string[] keys = ["I3MOD", "AMOD", "JMOD", "M11MOD", "F11MOD", "UX", "UZ", "U1", "U3", "R3", "D", "B", "TF", "TW", "Ss", "S1", "SiteClass", "temperature", "value", "distance", "NOPE", ""];
                    for (var i = Pick([0, 1, 2, 3], r); i > 0; i--)
                        o[Pick(keys, r)] = r.Next(4) == 0 ? new JsonObject { ["stiffness"] = Pick(Numbers, r), ["damping"] = Pick(Numbers, r) } : JsonValue.Create(Pick(Numbers, r));
                }
                return o;
            }
            default: return JsonValue.Create(Pick(Odd, r));
        }
    }
}
