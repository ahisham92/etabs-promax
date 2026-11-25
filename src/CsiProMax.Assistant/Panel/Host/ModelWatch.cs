using System.IO;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Host;

/// <summary>
/// Which model is open in the attached ETABS. ETABS's API has no event for it, so the model's file is read every few
/// seconds on a background thread (never the window's own), and a change is told on the window's thread. Each model
/// keeps its own chats, as in Sketch ProMax.
/// </summary>
internal static class ModelWatch
{
    internal sealed record Model(string Path, string Title);

    private static Timer? _timer;
    private static ICsiLink? _link;
    private static int _busy;

    /// <summary>The model open now; null before ETABS answered once.</summary>
    public static Model? Current { get; private set; }

    /// <summary>True when the last change looked like Save As: a model with no file got one, and nothing else changed.</summary>
    public static bool SavedAs { get; private set; }

    /// <summary>Why ETABS didn't answer the last time; null when it did.</summary>
    public static string? Problem { get; private set; }

    /// <summary>Raised on the window's thread with the model's file ("" when never saved) and its title.</summary>
    public static event Action<string, string>? Changed;

    public static void Start(ICsiLink link)
    {
        _link = link;
        Read();
        _timer = new Timer(_ => Read(), null, 3000, 3000);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>The title ETABS shows for a model file: its name without the extension, or "Untitled".</summary>
    internal static string TitleOf(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? "Untitled" : System.IO.Path.GetFileNameWithoutExtension(path.Trim()); }
        catch (ArgumentException) { return "Untitled"; }
    }

    internal static void Read()
    {
        if (_link is not { } link || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var file = link.Info().File ?? "";
            Problem = null;
            var next = new Model(file, TitleOf(file));
            var was = Current;
            // What is selected in ETABS, for the chat's model note; not while a tool is working in ETABS.
            if (!CsiProMax.App.ETABSWorking)
                try { ETABS.ChatModelWatch.SelectionRead(Chat.ChatPane.ModelKey(next.Path, next.Title), link.Selected()); }
                catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { }
            if (was is not null && string.Equals(was.Path, next.Path, StringComparison.OrdinalIgnoreCase)) return;
            SavedAs = was is { Path.Length: 0 } && next.Path.Length > 0;
            Current = next;
            var ui = CsiProMax.App.Ui;
            if (ui is null) Changed?.Invoke(next.Path, next.Title);
            else ui.BeginInvoke(() => Changed?.Invoke(next.Path, next.Title));
        }
        catch (Exception ex) when (ex is EtabsException or InvalidOperationException or IOException)
        {
            Problem = ex.Message;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
