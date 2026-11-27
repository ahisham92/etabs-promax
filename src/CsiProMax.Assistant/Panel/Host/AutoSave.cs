using System.IO;
using System.Windows.Threading;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;
using CsiProMax.UI;

namespace CsiProMax.Host;

/// <summary>
/// The Modeler's autosave: every minute while the model has changes that are not saved, a copy of it goes to a recovery
/// file (Recovery\modeler.cpm, the model's own file named beside it in modeler.txt). Saving the model removes the copy.
/// A copy still there when the Modeler starts (it crashed, or was closed with changes not saved) is offered back. The
/// model's own file is never written by the autosave. There is one Modeler at a time, so one recovery file.
/// </summary>
internal static class AutoSave
{
    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "Recovery");
    private static string CopyFile => Path.Combine(Folder, "modeler" + ModelFile.Extension);
    private static string SourceFile => Path.Combine(Folder, "modeler.txt");

    private static DispatcherTimer? _timer;
    private static int _copied = -1; // the model's revision in the copy

    /// <summary>Starts the autosave on the window's thread (where the tools change the model).</summary>
    internal static void Start(OfflineEtabs model)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Every };
        _timer.Tick += (_, _) => Save(model);
        _timer.Start();
    }

    /// <summary>The window is closing: changes not saved stay in the recovery file for the next start; none, and it goes.</summary>
    internal static void Stop(OfflineEtabs model)
    {
        _timer?.Stop();
        Save(model);
    }

    /// <summary>Copies the model to the recovery file when it changed since the last copy; removes the copy once it is saved.</summary>
    internal static void Save(OfflineEtabs model)
    {
        if (!model.Unsaved) { Clear(); return; }
        if (model.Revision == _copied || App.Dispatcher.IsRunning) return; // a tool half way through: the next tick
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = CopyFile + ".tmp";
            ModelFile.Save(model.ForSave, temp);
            File.Move(temp, CopyFile, overwrite: true); // never a half-written copy
            File.WriteAllText(SourceFile, model.Path);
            _copied = model.Revision;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CsiProMax.Assistant.Program.NoteFrom("autosave", ex); }
    }

    private static void Clear()
    {
        _copied = -1;
        try { File.Delete(CopyFile); File.Delete(SourceFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>At start: a recovery file left by the last Modeler is offered back (Yes opens it, No throws it away).</summary>
    internal static void Offer(OfflineEtabs model)
    {
        if (!File.Exists(CopyFile)) return;
        StructuralModel kept;
        string source;
        try
        {
            kept = ModelFile.Load(CopyFile);
            source = File.Exists(SourceFile) ? File.ReadAllText(SourceFile).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            CsiProMax.Assistant.Program.NoteFrom("autosave recovery", ex);
            Clear();
            return;
        }
        var when = File.GetLastWriteTime(CopyFile);
        var name = source.Length > 0 ? Path.GetFileName(source) : kept.Title + " (never saved)";
        var answer = TaskDialog.Show(App.Engine,
            $"The Modeler closed on {when:d MMM} at {when:HH:mm} with changes to {name} that were not saved. An autosaved copy has them.\n\n" +
            "Open the autosaved copy now? Save it afterwards to keep it. No throws the copy away.",
            TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogResult.Yes);
        if (answer == TaskDialogResult.Yes) model.Recover(kept, source);
        else Clear();
    }
}
