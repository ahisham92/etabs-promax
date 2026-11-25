using System.Text.RegularExpressions;

namespace CsiProMax.UI;

/// <summary>
/// What Sketch ProMax's job pane lends the chat: the element ids found in a step's result, and "select them". Revit's
/// elements have numeric ids; ETABS names its objects ("B12", "C4", "F3"), and results name them under "frames", "areas",
/// "names", "added" and the like, so a click on a finished step selects those in ETABS by name.
/// </summary>
internal static class Pane
{
    private static readonly Regex Lists = new(@"""(?<key>frames|areas|names|added|changed|columns|beams|braces|floors|walls|slabs|objects)""\s*:\s*\[(?<list>[^\]]*)\]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Quoted = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);

    /// <summary>Sketch ProMax collects numeric ids here; ETABS has none, so nothing is added (see <see cref="Names"/>).</summary>
    public static void Collect(string text, List<long> ids) { }

    /// <summary>The object names a result lists, at most 200, in order, without repeats.</summary>
    public static List<(string Kind, string Name)> Names(string text)
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Lists.Matches(text))
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var kind = key is "areas" or "floors" or "walls" or "slabs" ? "area" : "frame";
            foreach (Match q in Quoted.Matches(m.Groups["list"].Value))
            {
                var name = q.Groups[1].Value;
                if (name.Length == 0 || name.Length > 40 || !seen.Add(kind + "\n" + name)) continue;
                found.Add((kind, name));
                if (found.Count >= 200) return found;
            }
        }
        return found;
    }

    /// <summary>Selects objects in ETABS: "frame:B1,area:F2" (a bare name is taken as a frame). Returns how many.</summary>
    public static int Select(EtabsApp app, string list)
    {
        var n = 0;
        app.Link.ClearSelection();
        foreach (var part in list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf(':');
            var (kind, name) = at > 0 ? (part.Substring(0, at), part.Substring(at + 1)) : ("frame", part);
            try { app.Link.Select(kind.Trim(), name.Trim()); n++; }
            catch (CsiProMax.Assistant.Etabs.EtabsException) { /* gone since: the others are still selected */ }
        }
        app.Link.Refresh();
        return n;
    }
}
