using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// ETABS 23.2's Detailing > Bar Selection and Curtailment Rule forms in the Engine (beam, slab/mat/footing, column stack
/// and wall stack rules, with their sub-forms: development length, curtailment, rebar selection, longitudinal rebars,
/// confinement, splices, pier and spandrel rules). ETABS keeps these in its model database only: its .e2k and its database
/// tables have no line or table for them (pictures and table list in field/reference-forms/etabs/Detailing and
/// field/table-keys), so the Engine keeps each rule as the form's values in the model file ("model|" is no program's
/// table). Lengths are in metres. Modeler only.
/// </summary>
public static class DetailingTools
{
    public const string KeptName = "model|DETAILING RULES";

    /// <summary>The rules, one per Bar Selection and Curtailment Rule item of the menu.</summary>
    public static readonly string[] Forms = ["beam", "slab", "column", "wall"];

    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Engine's own Detailing form (no CSI program behind it). In a live program use its own Detailing menu.");

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("detailing_rules",
            "The Detailing > Bar Selection and Curtailment Rule values the model holds (beam, slab, column, wall): each rule as its form's values; a rule not set has ETABS's defaults.", // ETABS itself
            """{"type":"object","properties":{}}""",
            Ask.Never, (_, e) => Read(Offline(e).Model)),
        new("set_detailing_rules",
            "Sets one Detailing > Bar Selection and Curtailment Rule (form: beam, slab, column or wall) to its form's values (an object, lengths in metres); values null puts it back to ETABS's defaults.", // ETABS itself
            """{"type":"object","required":["form"],"properties":{"form":{"type":"string","enum":["beam","slab","column","wall"]},"values":{"type":["object","null"]}}}""",
            Ask.Changes, Set, a => $"Detailing rule: {a.Text("form")}"),
    ];

    public static JsonObject Read(StructuralModel m)
    {
        var rules = new JsonObject();
        foreach (var row in m.Kept?.GetValueOrDefault(KeptName) ?? [])
        {
            var form = row.GetValueOrDefault("Form") ?? "";
            if (!Forms.Contains(form)) continue;
            try { rules[form] = JsonNode.Parse(row.GetValueOrDefault("Values") ?? "{}"); } catch (JsonException) { }
        }
        return new JsonObject { ["rules"] = rules };
    }

    private static JsonNode Set(JsonElement a, ICsiLink e)
    {
        var form = a.Need("form");
        if (!Forms.Contains(form)) throw new ToolInputException("form is one of: " + string.Join(", ", Forms) + ".");
        string? values = null;
        if (a.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object) values = v.GetRawText();
        else if (a.TryGetProperty("values", out v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new ToolInputException("values is the form's values as an object, or null for ETABS's defaults.");
        return Offline(e).Edit<JsonNode>(m =>
        {
            m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
            var rows = m.Kept.TryGetValue(KeptName, out var had) ? had : [];
            rows.RemoveAll(r => r.GetValueOrDefault("Form") == form);
            if (values is not null) rows.Add(new Dictionary<string, string> { ["Form"] = form, ["Values"] = values });
            rows.Sort((x, y) => Array.IndexOf(Forms, x["Form"]).CompareTo(Array.IndexOf(Forms, y["Form"])));
            if (rows.Count == 0) m.Kept.Remove(KeptName); else m.Kept[KeptName] = rows;
            return new JsonObject { ["done"] = values is null ? $"{form}: ETABS's defaults" : $"{form}: set" };
        });
    }
}
