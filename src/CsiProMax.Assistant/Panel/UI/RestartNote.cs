using CsiProMax.Update;

namespace CsiProMax.UI;

/// <summary>
/// "Open CSI ProMax again to use the new version", once an update is downloaded: shown at the top of the chat and in
/// the update window. (Sketch ProMax's note is about Revit's ribbon and pane, which only change when Revit restarts.)
/// </summary>
internal static class RestartNote
{
    public static event Action? Changed;

    /// <summary>The note, or null when this is the newest version on this PC.</summary>
    public static string? Text => Updater.Staged is { } staged
        ? $"CSI ProMax {staged} is downloaded. Close this window and open CSI ProMax again from ETABS's Tools menu to use it."
        : null;

    /// <summary>An update was just downloaded (or the check found nothing new): show or clear the note.</summary>
    public static void Refresh() => Changed?.Invoke();
}
