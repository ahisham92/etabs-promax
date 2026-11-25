using CsiProMax.Chat;

namespace CsiProMax.ETABS;

/// <summary>
/// For the chat's model note (Chat.ModelNote): what is selected in ETABS now, read with the model's file every few
/// seconds (Host.ModelWatch), so the next message can say "the 12 selected beams" without a tool call. ETABS's API has no
/// change events, so the note doesn't count changes made by hand, as Sketch ProMax's does in Revit.
/// </summary>
internal static class ChatModelWatch
{
    private static readonly Dictionary<string, ModelNote.Facts> Models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chat answered (its turn ended).</summary>
    public static void Mark(string modelKey)
    {
        if (modelKey.Length == 0) return;
        lock (Models)
        {
            if (!Models.TryGetValue(modelKey, out var facts)) Models[modelKey] = facts = new ModelNote.Facts();
            facts.Known = false; // changes by hand aren't known in ETABS: the note never claims "nothing changed"
            facts.Changed.Clear();
            facts.Deleted = 0;
        }
    }

    /// <summary>The note for the next message about <paramref name="modelKey"/> ("" when nothing is known).</summary>
    public static string Note(string modelKey, bool changes = true)
    {
        lock (Models)
        {
            if (!Models.TryGetValue(modelKey, out var facts)) return "";
            try { return ModelNote.Text(facts, changes); }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>How many objects are selected in <paramref name="modelKey"/>'s model; -1 when not known.</summary>
    public static int Selected(string modelKey)
    {
        lock (Models) return Models.TryGetValue(modelKey, out var facts) && facts.SelectionKnown ? facts.SelectedCount : -1;
    }

    /// <summary>The selection as just read from ETABS (Host.ModelWatch, off the window's thread).</summary>
    internal static void SelectionRead(string modelKey, IReadOnlyList<(string Kind, string Name)> selected)
    {
        if (modelKey.Length == 0) return;
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, _) in selected)
        {
            var name = kind switch { "frame" => "Frames", "area" => "Floors and walls", "point" => "Joints", _ => "other" };
            kinds[name] = kinds.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        lock (Models)
        {
            if (!Models.TryGetValue(modelKey, out var facts)) Models[modelKey] = facts = new ModelNote.Facts();
            facts.SelectionKnown = true;
            facts.SelectedCount = selected.Count;
            facts.SelectedIds = [];
            facts.SelectedKinds = kinds;
            facts.SelectedNames = selected.Take(ModelNote.MaxIds).Select(s => s.Name).ToList();
        }
    }

    internal static void Forget(string modelKey)
    {
        lock (Models) Models.Remove(modelKey);
    }
}
