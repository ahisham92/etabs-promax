using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using EtabsProMax.Tools;
using EtabsProMax.UI;

namespace EtabsProMax.Bugs;

/// <summary>
/// Writes one small bug report zip to Documents\ETABS ProMax\Bugs\Reports: the user's one-line description, a JPEG of
/// ETABS's main window, the add-in and ETABS versions, the model's name, the last activity-log entries (failed calls
/// included) and the assemblies report. Used by the Report bug button (which asks for the description) and by the
/// report_bug tool (Claude passes it). Runs on ETABS's thread.
/// </summary>
internal static class BugReport
{
    private const int MaxActivity = 50, ScreenshotWidth = 1600, MaxDescription = 8000;

    /// <summary>A screenshot taken before any dialog opens, or null with the reason.</summary>
    public static (byte[]? Jpeg, string? Error) Screenshot()
    {
        try { return (ScreenGrab.Window(App.MainWindow, ScreenshotWidth), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Writes the zip and refreshes Bug log.md. Returns the zip's path.</summary>
    public static string Create(EtabsApp app, string description, (byte[]? Jpeg, string? Error) shot, string from, string? details = null)
    {
        description = string.Join(" ", (description ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (description.Length == 0) throw new ArgumentException("Say in one line what went wrong.");
        // Kept whole (bug 72: a report cut at 500 characters lost the cause); the Bug log list shortens it itself.
        if (description.Length > MaxDescription) description = description.Substring(0, MaxDescription) + "…";

        var now = DateTime.Now;
        Directory.CreateDirectory(BugLog.ReportsFolder);
        var (name, id) = BugLog.NameFor(now);
        var file = Path.Combine(BugLog.ReportsFolder, name);
        if (File.Exists(file))
        {
            (name, id) = BugLog.NameFor(now, seconds: true);
            file = Path.Combine(BugLog.ReportsFolder, name);
        }

        var doc = app.ActiveUIDocument?.Document;
        var model = "(no model open)";
        var view = "";
        try
        {
            if (doc is not null) model = doc.PathName.Length > 0 ? $"{doc.Title} ({doc.PathName})" : doc.Title + " (never saved)";
        }
        catch (Exception) { /* a document being closed */ }
        var activity = Activity(MaxActivity);
        var failed = activity.Count(l => l.Contains("\"ok\":false"));

        var md = new StringBuilder();
        md.AppendLine($"# Bug report {id}");
        md.AppendLine();
        md.AppendLine($"**What happens:** {description}");
        md.AppendLine();
        md.AppendLine($"- Reported: {now:yyyy-MM-dd HH:mm} (local time), from {from}");
        md.AppendLine($"- ETABS ProMax: {Update.Updater.CurrentText}");
        md.AppendLine($"- ETABS: {(App.ETABSYear.Length > 0 ? App.ETABSYear : "version not read")}{(App.Demo ? " (demo model, no ETABS)" : "")}");
        md.AppendLine($"- Model: {model}" + (view.Length > 0 ? $", view: {view}" : ""));
        md.AppendLine($"- Screenshot: {(shot.Jpeg is not null ? "screenshot.jpg (ETABS's main window)" : "none (" + shot.Error + ")")}");
        md.AppendLine($"- Activity: the last {activity.Count} tool calls in activity.jsonl ({failed} did not work), oldest first");
        md.AppendLine();
        string assemblies;
        try { assemblies = EtabsProMax.ETABS.AssemblyReport.Describe(); }
        catch (Exception ex) { assemblies = "## Assemblies\n\nCould not be read: " + ex.Message; }

        var partial = file + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Add(string entryName, byte[] data, bool compress = true)
                {
                    var entry = zip.CreateEntry(entryName, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
                var utf8 = new UTF8Encoding(false);
                Add("description.txt", utf8.GetBytes(description));
                Add("report.md", utf8.GetBytes(md.ToString()));
                Add("activity.jsonl", utf8.GetBytes(string.Join("\n", activity) + "\n"));
                Add("assemblies.md", utf8.GetBytes(assemblies));
                if (shot.Jpeg is not null) Add("screenshot.jpg", shot.Jpeg, compress: false);
                if (details is not null) Add("error.txt", utf8.GetBytes(details));
            }
            File.Move(partial, file);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        BugLog.RewriteLocal();
        return file;
    }

    /// <summary>
    /// TritoETABS: a report written by itself when a shared tool fails, with what failed as its description. Never throws;
    /// returns the zip, or null when it couldn't be written.
    /// </summary>
    private static System.Windows.Threading.Dispatcher? _watched;
    private static EtabsApp? _watchedApp;

    /// <summary>
    /// TritoETABS: an exception from our code that reaches ETABS's UI dispatcher (a window's button, an async handler) is
    /// saved as a problem report. Only looked at, never marked handled, so ETABS deals with it as before.
    /// </summary>
    public static void Watch(System.Windows.Threading.Dispatcher? ui, EtabsApp app)
    {
        if (ui is null || _watched is not null) return;
        _watched = ui;
        _watchedApp = app;
        ui.UnhandledException += OnUnhandled;
    }

    public static void Unwatch()
    {
        if (_watched is { } ui) ui.UnhandledException -= OnUnhandled;
        _watched = null;
        _watchedApp = null;
    }

    private static void OnUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        if (!Ours(e.Exception)) return;
        Automatic(_watchedApp, "a TritoETABS window", e.Exception);
    }

    /// <summary>True when the exception went through this add-in's code (other add-ins' failures are theirs).</summary>
    internal static bool Ours(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if ((e.StackTrace ?? "").IndexOf("EtabsProMax.", StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    private static readonly object AutoGate = new();
    private static DateTime _lastAuto = DateTime.MinValue;

    public static string? Automatic(EtabsApp? app, string what, Exception ex)
    {
        if (!App.Shared) return null;
        // One by itself per half minute: a failure that repeats doesn't fill the folder.
        lock (AutoGate)
        {
            if (DateTime.UtcNow - _lastAuto < TimeSpan.FromSeconds(30)) return null;
            _lastAuto = DateTime.UtcNow;
        }
        try
        {
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;
            var line = $"Automatic report: {what} failed with {inner.GetType().Name}: {inner.Message}";
            return app is null ? null : Create(app, line, Screenshot(), "TritoETABS, by itself", inner.ToString());
        }
        catch (Exception) { return null; }
    }

    /// <summary>The last <paramref name="max"/> activity-log lines across the daily files, oldest first.</summary>
    internal static List<string> Activity(int max)
    {
        var lines = new List<string>();
        try
        {
            var folder = Path.GetDirectoryName(ActivityLog.FileFor("2000-01-01"))!;
            if (!Directory.Exists(folder)) return lines;
            foreach (var day in Directory.GetFiles(folder, "*.jsonl").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(30))
            {
                string[] text;
                try
                {
                    using var stream = new FileStream(day, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    text = reader.ReadToEnd().Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
                catch (IOException) { continue; }
                lines.InsertRange(0, text.Skip(Math.Max(0, text.Length - (max - lines.Count))));
                if (lines.Count >= max) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    /// <summary>The Report bug button: screenshot first, then a one-line description, then the zip.</summary>
    public static Result Command(EtabsApp app)
    {
        var shot = Screenshot();
        if (Ask() is not { } description) return Result.Cancelled;
        string file;
        try { file = Create(app, description, shot, App.Shared ? "Report a problem" : "the Report bug button"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            TaskDialog.Show("ETABS ProMax", $"The bug report could not be saved: {ex.Message}");
            return Result.Failed;
        }
        Saved(file);
        return Result.Succeeded;
    }

    /// <summary>Asks for the one line; null when cancelled. Call on ETABS's UI thread.</summary>
    public static string? Ask()
    {
        var where = App.Shared ? BugLog.ReportsFolder.Replace(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents")
            : "Documents\\ETABS ProMax\\Bugs\\Reports";
        var window = new InputsWindow(App.Shared ? "Report a problem" : "Report bug",
            App.Shared ? "One small zip for the person who shared these tools." : "One small zip for the ETABS ProMax developer.",
            "Say in one line what went wrong. The report also holds a picture of the ETABS window, the versions, the model's name, " +
            "the last tool calls and their errors. Nothing goes over the internet: the zip is saved in " + where + "." +
            (App.Shared ? " A copy also goes to the shared tools' update folder when this PC can reach it." : ""),
            [],
            [new InputsWindow.Field("description", "What went wrong", "For example: the tags are placed on top of each other in Level 2.", "string", true, null, null)],
            "Save report");
        return window.ShowDialog() == true ? window.Values["description"] : null;
    }

    /// <summary>Shows the saved zip in its folder (Explorer with the file selected), or says where it is.</summary>
    public static void Saved(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { TaskDialog.Show("ETABS ProMax", $"Bug report saved: {file}"); }
    }

    /// <summary>The Open bug folder button.</summary>
    public static Result OpenFolder()
    {
        string folder;
        try { folder = BugLog.EnsureFolder(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TaskDialog.Show("ETABS ProMax", $"Couldn't create the bug folder: {ex.Message}");
            return Result.Failed;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
            return Result.Succeeded;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskDialog.Show("ETABS ProMax", $"Couldn't open {folder}: {ex.Message}");
            return Result.Failed;
        }
    }
}

/// <summary>Files a bug report when the user says something is a bug.</summary>
public sealed class ReportBugTool : IEtabsTool
{
    public string Name => "report_bug";
    public string Description =>
        "File an ETABS ProMax bug report when the user says something is a bug (\"that's a bug\", \"report this\"). Writes ONE " +
        "small zip to Documents\\ETABS ProMax\\Bugs\\Reports with the description, a picture of the ETABS window, add-in and " +
        "ETABS versions, the model name, the last 50 tool calls with their errors and the loaded-assemblies report. Nothing is " +
        "sent anywhere; the user sends the zip. Write the description yourself from the conversation: one plain line saying " +
        "what the user did, what happened and what they expected, with the tool name. Read-only on the model.";
    public string PropertiesJson => """
    {
      "description": {"type": "string", "description": "One line: what happened, with the tool involved and what was expected."}
    }
    """;
    public string[] Required => ["description"];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var file = BugReport.Create(app, args.String("description"), BugReport.Screenshot(), "the chat (report_bug)");
        var size = new FileInfo(file).Length;
        return ToolResult.Ok(new
        {
            file, size = (size / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
            folder = BugLog.Root,
            next = "Tell the user the report is saved and listed under \"Not sent yet\" in Bug log.md in that folder (Open bug folder on the " +
                   "ETABS ProMax tab), and that they can send the zip to the developer.",
        });
    }
}
