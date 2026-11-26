using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Modelling by hand in the Modeler's 3D window: its menus, forms and drawing clicks call the very tools the chat
/// calls (so a model built by hand and one built in the chat are the same thing, and either can carry on the other's
/// work), plus undo and redo of the hand edits. It runs on the Modeler's model only; it never reaches a CSI program.
/// </summary>
public static class HandModel
{
    /// <summary>The tools the window may call: the Modeler's model tools. No code, no files outside the model.</summary>
    public static IReadOnlyDictionary<string, Tool> Tools { get; } =
        EtabsTools.All.Concat(ModelerTools.All).Concat(ProgramTools.Modeler).Concat(HandTools.All).Concat(TableTools.All).Concat(LateralTools.All).Concat(CaseTools.All).Concat(ModelTextTools.All).Concat(PropertyTools.All).Concat(StructureTools.All).Concat(ExtraTools.All)
            .Where(t => t.Name is not ("run_analysis" or "run_design"))
            .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private const int Depth = 100;

    private sealed record Step(string Model, string Path, string What);

    private static readonly LinkedList<Step> Undos = new(), Redos = new();
    private static OfflineEtabs? _of;
    private static int _after = -1;

    /// <summary>Forgets the undo history (tests, and a different model behind the window).</summary>
    public static void Reset() { Undos.Clear(); Redos.Clear(); _of = null; _after = -1; }

    private static void Follow(OfflineEtabs link)
    {
        if (!ReferenceEquals(_of, link)) { Reset(); _of = link; }
    }

    /// <summary>The window's request, as JSON: {"tool":name,"args":{...}}, or {"undo":true}, or {"redo":true}.</summary>
    public static JsonObject Handle(ICsiLink link, JsonElement request)
    {
        if (link is not OfflineEtabs model)
            return Answer(false, "Modelling by hand is in the Modeler (no CSI program behind it). In ETABS, use ETABS's own commands.", null); // ETABS itself
        Follow(model);
        if (request.TryGetProperty("undo", out var u) && u.ValueKind == JsonValueKind.True) return Back(model, Undos, Redos, "Undo");
        if (request.TryGetProperty("redo", out var r) && r.ValueKind == JsonValueKind.True) return Back(model, Redos, Undos, "Redo");
        var name = request.TryGetProperty("tool", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        if (!Tools.TryGetValue(name, out var tool)) return Answer(false, $"There is no Modeler command named '{name}'.", model);
        var args = request.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        var changes = tool.Ask != Ask.Never;
        var before = changes ? new Step(ModelFile.ToJson(model.Model), model.Path, Describe(tool, args)) : null;
        var revision = model.Revision;
        JsonNode result;
        try { result = tool.Invoke(args, model); }
        catch (Exception ex) when (ex is ToolInputException or EtabsException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        {
            return Answer(false, ex.Message, model);
        }
        if (before is not null && model.Revision != revision && name is not ("select_objects" or "save_model" or "save_model_as"))
        {
            Undos.AddLast(before);
            while (Undos.Count > Depth) Undos.RemoveFirst();
            Redos.Clear();
            _after = model.Revision;
        }
        var answer = Answer(true, null, model);
        answer["result"] = result;
        return answer;
    }

    private static string Describe(Tool tool, JsonElement args)
    {
        try { return tool.Summary?.Invoke(args) ?? tool.Name.Replace('_', ' '); }
        catch (Exception) { return tool.Name.Replace('_', ' '); }
    }

    private static JsonObject Back(OfflineEtabs model, LinkedList<Step> from, LinkedList<Step> to, string verb)
    {
        if (from.Last is not { } last) return Answer(false, $"Nothing to {verb.ToLowerInvariant()}.", model);
        // The chat changed the model since the last hand edit: stepping back would throw its work away too.
        if (model.Revision != _after)
        {
            Undos.Clear(); Redos.Clear();
            return Answer(false, $"The model was changed in the chat after your last edit, so {verb.ToLowerInvariant()} would undo that too. Ask the chat to change it back, or edit it by hand.", model);
        }
        from.RemoveLast();
        to.AddLast(new Step(ModelFile.ToJson(model.Model), model.Path, last.Value.What));
        model.Restore(ModelFile.FromJson(last.Value.Model), last.Value.Path);
        _after = model.Revision;
        var answer = Answer(true, null, model);
        answer["did"] = verb + ": " + last.Value.What;
        return answer;
    }

    private static JsonObject Answer(bool ok, string? error, OfflineEtabs? model)
    {
        var o = new JsonObject { ["ok"] = ok };
        if (error is not null) o["error"] = error;
        if (model is not null)
        {
            o["rev"] = model.Revision;
            o["undo"] = Undos.Last?.Value.What;
            o["redo"] = Redos.Last?.Value.What;
            o["saved"] = !model.Unsaved;
            o["file"] = model.Path;
        }
        return o;
    }
}
