using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test's steps for starting the Modeler from its desktop shortcut (Ahmed, 2026-11-26: "When I double click
/// the modeller it does not open", the Viewer's shortcut did). The Modeler keeps one window per PC by a name
/// (CsiProMax.modeler): a second start only asked the open one to come forward and ended. When the one holding the name
/// had no window (closing after an update, or left behind), nothing opened. Now a start that gets no answer ends a
/// holder with no window and opens itself (Program.Handover). Here: the shortcut names a CSI ProMax that is there, the
/// Modeler opens as the shortcut starts it, and a second double-click is answered by the open one.
/// </summary>
public static class ModelerStartFieldSteps
{
    /// <summary>The Modeler shortcut's start, as Shortcuts makes it, for this very version.</summary>
    internal static IReadOnlyList<string> Arguments(string dll) => [dll, "--modeler", "--this-version", "--no-update"];

    /// <param name="open">Stands in for the real starts (the tests): the first start's outcome, then the second's.</param>
    public static void Run(FieldContext ctx, bool full, Func<(WindowFieldSteps.Seen First, WindowFieldSteps.Seen Again)>? open = null)
    {
        ctx.Step("the Modeler knows which copy holds its window, even after a process id is reused", () =>
        {
            var now = DateTime.UtcNow;
            var line = Program.Holder.Line(4242, now);
            FieldContext.Expect(Program.Holder.Parse(line) is { Pid: 4242 } held && held.Ticks == now.Ticks, "the holder file is not read back as written: " + line);
            FieldContext.Expect(Program.Holder.Parse("") is null && Program.Holder.Parse("12") is null && Program.Holder.Parse("x|y") is null, "a broken holder file is taken for a process");
            FieldContext.Expect(Program.Holder.File("CsiProMax.modeler").EndsWith("CsiProMax.modeler.pid", StringComparison.Ordinal), "the holder file is not beside errors.txt");
            return "a start that gets no answer finds the copy holding the Modeler, and ends it only when it has no window";
        });

        // Run from the Modeler itself: its own window must hold the model, with the chat beside it (not the chat alone).
        if (open is null && OperatingSystem.IsWindows() && CsiProMax.App.Modeler)
            ctx.Step("this Modeler window holds its model view, with the chat on its right", () => ThisWindow());

        if (open is null && !OperatingSystem.IsWindows())
        {
            ctx.Skip("the Modeler shortcut names a CSI ProMax that is on this PC", "Windows only");
            ctx.Skip("the Modeler opens from its shortcut with its model view inside, and a second double-click is answered", "Windows only");
            return;
        }
        if (open is null)
            ctx.Step("the Modeler shortcut names a CSI ProMax that is on this PC", () => Shortcut());
        if (!full && open is null) return;
        ctx.Step("the Modeler opens from its shortcut with its model view inside, and a second double-click is answered", () =>
        {
            var (first, again) = (open ?? Twice)();
            if (first.NotTried) throw new FieldSkipException(first.Detail);
            FieldContext.Expect(first.Ok, "the first start: " + first.Detail);
            FieldContext.Expect(again.Ok, "the second start: " + again.Detail);
            return first.Detail + "; " + again.Detail;
        });
    }

    // Apart, so that WPF's types are only reached on Windows, in the Modeler.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string ThisWindow()
    {
        var answer = "";
        CsiProMax.App.Ui?.Invoke(() =>
        {
            var main = Host.MainWindow.Current;
            FieldContext.Expect(main is not null, "the Modeler has no window");
            FieldContext.Expect(main!.HasModelView, "the Modeler's window does not hold its model view (it shows the chat alone)");
            answer = "the model fills the window" + (CsiProMax.AiMode.On ? main.ChatShown ? ", the chat is on its right" : ", the chat is closed (View > Show the chat)" : "; the AI chat is off");
        });
        return answer.Length > 0 ? answer : throw new FieldSkipException("the Modeler's window could not be asked");
    }

    /// <summary>Each Modeler shortcut (desktop, Start menu): its program and its CsiProMax.dll must be there, and it must start the Modeler.</summary>
    private static string Shortcut()
    {
        var links = Host.Shortcuts.Folders().Where(f => f.Length > 0).Select(f => Path.Combine(f, Host.Shortcuts.ModelerName + ".lnk")).Where(File.Exists).ToList();
        if (links.Count == 0) throw new FieldSkipException("this PC has no Modeler shortcut (View > Make the Modeler and Viewer shortcuts again makes it)");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new FieldSkipException("Windows' shortcut reader is not available on this PC");
        dynamic shell = Activator.CreateInstance(type)!;
        var seen = new List<string>();
        try
        {
            foreach (var file in links)
            {
                dynamic link = shell.CreateShortcut(file);
                string target = link.TargetPath, arguments = link.Arguments, folder = link.WorkingDirectory;
                FieldContext.Expect(File.Exists(target), $"{file} starts {target}, which is not on this PC");
                var dll = arguments.Trim().StartsWith('"') ? arguments.Trim()[1..].Split('"')[0] : arguments.Trim().Split(' ')[0];
                FieldContext.Expect(File.Exists(dll), $"{file} starts {dll}, which is not on this PC (reinstall, or View > Make the Modeler and Viewer shortcuts again)");
                FieldContext.Expect(arguments.Contains("--modeler", StringComparison.Ordinal), $"{file} does not start the Modeler: {arguments}");
                FieldContext.Expect(folder.Length == 0 || Directory.Exists(folder), $"{file} starts in {folder}, which is not on this PC");
                seen.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}: {target} {arguments}");
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return string.Join("; ", seen);
    }

    /// <summary>The Modeler started as its shortcut does, then started again while it is open; both closed afterwards.</summary>
    private static (WindowFieldSteps.Seen First, WindowFieldSteps.Seen Again) Twice()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CsiProMax.dll");
        if (!File.Exists(dll) || Environment.ProcessPath is not { } host) return (new WindowFieldSteps.Seen(false, $"CsiProMax.dll was not found in {AppContext.BaseDirectory}"), new WindowFieldSteps.Seen(false, "not started"));
        if (CsiProMax.App.Modeler || Process.GetProcessesByName("dotnet").Any(p => { try { return p.Id != Environment.ProcessId && p.MainWindowTitle.StartsWith("CSI ProMax Modeler", StringComparison.Ordinal); } catch (InvalidOperationException) { return false; } finally { p.Dispose(); } }))
            return (new WindowFieldSteps.Seen(false, "a Modeler is open on this PC, so the test cannot start its own. Close it and run the test again.", NotTried: true), new WindowFieldSteps.Seen(false, "not started"));
        var notes = Program.NoteFile;
        var before = File.Exists(notes) ? new FileInfo(notes).Length : 0;
        ProcessStartInfo Info()
        {
            var info = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var argument in Arguments(dll)) info.ArgumentList.Add(argument);
            return info;
        }
        using var first = Process.Start(Info());
        if (first is null) return (new WindowFieldSteps.Seen(false, "the Modeler could not be started"), new WindowFieldSteps.Seen(false, "not started"));
        try
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            var title = "";
            while (DateTime.UtcNow < until && title.Length == 0)
            {
                Thread.Sleep(500);
                first.Refresh();
                if (first.HasExited) return (new WindowFieldSteps.Seen(false, $"it ended by itself with code {first.ExitCode} and no window. {WindowFieldSteps.Wrote(notes, before)}"), new WindowFieldSteps.Seen(false, "not started"));
                if (first.MainWindowHandle != IntPtr.Zero) title = first.MainWindowTitle;
            }
            if (!title.StartsWith("CSI ProMax Modeler", StringComparison.Ordinal))
                return (new WindowFieldSteps.Seen(false, title.Length == 0 ? $"no window showed within 90 seconds. {WindowFieldSteps.Wrote(notes, before)}" : $"the window is named '{title}'"), new WindowFieldSteps.Seen(false, "not started"));
            // The window must be the Modeler itself: its model view drawn inside it, not the chat alone (2026-11-26).
            var drawn = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < drawn && !Host.WebPages.Shown(first.Id).Contains(nameof(Host.WebPages.Kind.ModelerView))) Thread.Sleep(500);
            if (!Host.WebPages.Shown(first.Id, remove: true).Contains(nameof(Host.WebPages.Kind.ModelerView)))
                return (new WindowFieldSteps.Seen(false, $"'{title}' opened, but its model view (3D, plan, Draw/Define/Assign) was not drawn inside it within 60 seconds. {WindowFieldSteps.Wrote(notes, before)}"), new WindowFieldSteps.Seen(false, "not started"));
            Thread.Sleep(3000);
            var opened = new WindowFieldSteps.Seen(true, $"'{title}' opened with its model view inside");
            // The second double-click: the open Modeler answers, and the new start ends within seconds.
            var watch = Stopwatch.StartNew();
            using var again = Process.Start(Info());
            if (again is null) return (opened, new WindowFieldSteps.Seen(false, "the second start could not be made"));
            if (!again.WaitForExit(30000)) { try { again.Kill(true); } catch (InvalidOperationException) { } return (opened, new WindowFieldSteps.Seen(false, "the second start did not end within 30 seconds")); }
            first.Refresh();
            if (first.HasExited) return (opened, new WindowFieldSteps.Seen(false, "the open Modeler ended when it was started again"));
            if (watch.Elapsed > TimeSpan.FromSeconds(7)) return (opened, new WindowFieldSteps.Seen(false, $"the open Modeler took {watch.Elapsed.TotalSeconds:0} seconds to answer (it should come forward at once). {WindowFieldSteps.Wrote(notes, before)}"));
            var said = WindowFieldSteps.Wrote(notes, before);
            return (opened, said.Length > 0 ? new WindowFieldSteps.Seen(false, "it wrote an error: " + said) : new WindowFieldSteps.Seen(true, $"started again, the open Modeler answered in {watch.Elapsed.TotalSeconds:0.0} s"));
        }
        finally
        {
            try
            {
                if (!first.HasExited)
                {
                    first.CloseMainWindow();
                    if (!first.WaitForExit(20000)) first.Kill(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
