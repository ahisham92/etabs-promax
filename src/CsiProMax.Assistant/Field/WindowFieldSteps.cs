using System.Diagnostics;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test's own look at the real window, both ways a PC can have it: with the AI chat and without it
/// (install-no-ai.ps1, or "Turn the AI chat off"). CSI ProMax is started a second time on its demo model, as
/// tests/window.Tests.ps1 does on every release build, with no CSI program behind it: the window must open with its
/// title, still be there a few seconds later, and write nothing into errors.txt. The test closes its own copy again.
/// It never touches the window the test was started from, and it changes no setting on this PC (--no-ai is for that
/// one start only).
/// </summary>
public static class WindowFieldSteps
{
    /// <summary>How one start went. Ok false: Detail says what was seen. NotTried: it could not be tried here, and Detail says why.</summary>
    public sealed record Seen(bool Ok, string Detail, bool NotTried = false);

    /// <param name="openViewer">Stands in for the Viewer's own start (the tests). Without <paramref name="open"/> either, the real one runs.</param>
    public static void Run(FieldContext ctx, Func<bool, Seen>? open = null, Func<Seen>? openViewer = null)
    {
        foreach (var ai in new[] { true, false })
        {
            var name = ai ? "the CSI ProMax window opens with the AI chat" : "the CSI ProMax window opens without the AI chat";
            if (open is null && !OperatingSystem.IsWindows()) { ctx.Skip(name, "the window is drawn on Windows only"); continue; }
            ctx.Step(name, () =>
            {
                var seen = (open ?? Open)(ai);
                if (seen.NotTried) throw new FieldSkipException(seen.Detail);
                FieldContext.Expect(seen.Ok, seen.Detail);
                return seen.Detail;
            });
        }
        // The Viewer on its own, as its desktop shortcut starts it (--viewer): a CSI ProMax window, not a browser tab.
        if (open is not null && openViewer is null) return;
        const string viewer = "the Viewer opens in a CSI ProMax window of its own (not the browser)";
        if (openViewer is null && !OperatingSystem.IsWindows()) { ctx.Skip(viewer, "the window is drawn on Windows only"); return; }
        ctx.Step(viewer, () =>
        {
            var seen = (openViewer ?? OpenViewer)();
            if (seen.NotTried) throw new FieldSkipException(seen.Detail);
            FieldContext.Expect(seen.Ok, seen.Detail);
            return seen.Detail;
        });
    }

    /// <summary>The Viewer shortcut's start: this very version, with no update on the way.</summary>
    internal static IReadOnlyList<string> ViewerArguments(string dll) => [dll, "--viewer", "--this-version", "--no-update"];

    /// <summary>The arguments of the second start: the demo model, this very version, no update on the way, and the chat on or off.</summary>
    internal static IReadOnlyList<string> Arguments(string dll, bool ai) =>
        ai ? [dll, "--demo", "--this-version", "--no-update"] : [dll, "--demo", "--this-version", "--no-update", "--no-ai"];

    private static Seen Open(bool ai) => Start(dll => Arguments(dll, ai), "CSI ProMax", demo: true);

    private static Seen OpenViewer() => Start(ViewerArguments, Host.WebPages.Title(Host.WebPages.Kind.Viewer, ""), demo: false);

    /// <param name="demo">The demo window, of which a PC has one at a time: a start that only brings the open one forward is "not tested".</param>
    private static Seen Start(Func<string, IReadOnlyList<string>> arguments, string titled, bool demo)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CsiProMax.dll");
        if (!File.Exists(dll) || Environment.ProcessPath is not { } host) return new Seen(false, $"CsiProMax.dll was not found in {AppContext.BaseDirectory}, so a second window could not be started");
        var notes = Program.NoteFile;
        var before = File.Exists(notes) ? new FileInfo(notes).Length : 0;
        var info = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in arguments(dll)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        if (process is null) return new Seen(false, "the second CSI ProMax could not be started");
        try
        {
            var title = "";
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(500);
                process.Refresh();
                if (process.HasExited)
                {
                    // One demo window per PC: a second start only brings the open one forward and ends with 0.
                    var wrote = Wrote(notes, before);
                    if (!demo && process.ExitCode == 0 && wrote.Length == 0)
                        return new Seen(false, "it ended with no window of its own: this PC has no WebView2 Runtime, so the page went to Edge's app window or the browser instead");
                    return process.ExitCode == 0 && wrote.Length == 0
                        ? new Seen(false, "a CSI ProMax window on the demo model is already open on this PC, so a second one could not be started. Close it and run the test again.", NotTried: true)
                        : new Seen(false, $"it ended by itself with code {process.ExitCode} before its window showed. {wrote}");
                }
                if (process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle.Length > 0) { title = process.MainWindowTitle; break; }
            }
            if (title.Length == 0) return new Seen(false, $"no window showed within 90 seconds. {Wrote(notes, before)}");
            if (!title.StartsWith(titled, StringComparison.Ordinal)) return new Seen(false, $"the window that showed is named '{title}', not {titled}");
            // Time to draw, and for anything that fails just after the window shows.
            Thread.Sleep(8000);
            process.Refresh();
            if (process.HasExited) return new Seen(false, $"the window opened and then ended with code {process.ExitCode}. {Wrote(notes, before)}");
            var said = Wrote(notes, before);
            return said.Length > 0 ? new Seen(false, "the window opened, but it wrote an error while starting and drawing. " + said) : new Seen(true, $"'{title}' opened, drew and wrote no error; closed again");
        }
        finally
        {
            // The test's own copy only: asked to close, and ended if it does not.
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(8000)) process.Kill(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>What was added to errors.txt since <paramref name="before"/> (its length then), or "".</summary>
    internal static string Wrote(string notes, long before)
    {
        try
        {
            if (!File.Exists(notes) || new FileInfo(notes).Length <= before) return "";
            using var stream = new FileStream(notes, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(before, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            return text.Length == 0 ? "" : "errors.txt says: " + (text.Length > 1500 ? text[..1500] + "..." : text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
