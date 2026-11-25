using EtabsProMax.Assistant.Etabs;
using EtabsProMax.Chat;

namespace EtabsProMax.ETABS;

/// <summary>
/// The open model's own names for the home screen's prompt blanks (Chat.PromptSlots): its stories (by elevation), load
/// patterns, combinations and sections. A part that can't be read is left out, so its blank stays for the user to type.
/// </summary>
internal static class PromptChoices
{
    public static Dictionary<string, List<string>> Read(EtabsApp app)
    {
        var choices = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string slot, Func<IEnumerable<string>> read, bool keepOrder = false)
        {
            try { if (PromptSlots.Tidy(read(), keepOrder) is { Count: > 0 } names) choices[slot] = names; }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { }
        }
        Add("story", () => app.Link.Stories().OrderBy(s => s.Elevation).Select(s => s.Name), keepOrder: true);
        Add("level", () => app.Link.Stories().OrderBy(s => s.Elevation).Select(s => s.Name), keepOrder: true);
        Add("load_pattern", () => app.Link.LoadPatterns().Select(p => p.Name));
        Add("combination", () => app.Link.Combos());
        Add("load_case", () => app.Link.LoadCases());
        Add("frame_section", () => app.Link.FrameSections());
        Add("area_section", () => app.Link.AreaSections());
        Add("material", () => app.Link.Materials());
        return choices;
    }
}
