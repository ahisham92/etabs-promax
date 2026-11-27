using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Reflection;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Safe;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

namespace CsiProMax.Assistant;

/// <summary>The field test's own window of a CSI program on its own scratch model. Tests stand in for it with a model in memory.</summary>
public interface IScratchWindow : IDisposable
{
    /// <summary>The link to the scratch window, and to no other.</summary>
    ICsiLink Link { get; }

    /// <summary>The scratch model's own file, in the test's folder.</summary>
    string ModelPath { get; }

    /// <summary>Closes the window without saving, and says in a sentence what happened.</summary>
    string Close();

    /// <summary>The titles of the windows the scratch program has open now (a message waiting for an answer is one of them).</summary>
    IReadOnlyList<string> OpenWindows() => [];

    /// <summary>Puts back the program's plug-in lists the scratch window emptied. Returns the settings files put back.</summary>
    IReadOnlyList<string> PutBackPlugIns() => [];
}

/// <summary>
/// The windows a scratch program puts up by itself. SAP2000 shows "Analysis Complete - (model)" after an analysis and
/// waits for OK, so run_analysis never came back in the first field test on a real SAP2000. That one window, in the
/// test's own scratch process, is closed for it. Nothing else is: a licence notice or any other message is the
/// engineer's to read and answer, and the report names it.
/// </summary>
public static class ScratchDialogs
{
    /// <summary>True for a window the test may close itself: only the notice that an analysis has finished.</summary>
    public static bool MayClose(string title) => title.TrimStart().StartsWith("Analysis Complete", StringComparison.OrdinalIgnoreCase);

    /// <summary>The visible titled windows of one process.</summary>
    public static List<(IntPtr Window, string Title)> Of(int processId)
    {
        var found = new List<(IntPtr, string)>();
        if (processId == 0 || !OperatingSystem.IsWindows()) return found;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(window)) return true;
            var text = new System.Text.StringBuilder(300);
            if (GetWindowTextW(window, text, text.Capacity) > 0) found.Add((window, text.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Answers each "analysis complete" notice of the process the way a person does: by its own OK button, and only
    /// when the program lets that button be pressed. Returns their titles.
    /// </summary>
    /// <remarks>
    /// Never by closing the window (its X): the analysis windows of ETABS and SAP2000 carry the same title while the
    /// program still works, and closing one made ETABS ask "Program is active. Do you really want to close the
    /// program?" in the middle of its analysis, and left SAP2000 with no analysis window ("Invoke or BeginInvoke cannot
    /// be called on a control until the window handle has been created") in the second full field test of 0.4.9
    /// (2026-11-25). A notice with no OK button to press yet is left alone and looked at again a second later.
    /// </remarks>
    public static List<string> CloseNotices(int processId, CsiProgram? program = null)
    {
        var closed = new List<string>();
        foreach (var (window, title) in Of(processId))
        {
            if (MayClose(title) && Button(window, "OK") is { } ok && IsWindowEnabled(ok) && PostMessageW(ok, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero)) closed.Add(title);
            else if (program is not null && IsProgramBox(title, program) && IsDialog(window) && Button(window, "No") is { } no && IsWindowEnabled(no)
                     && LongWords(window).ToList() is var asked && (IsReiterateQuestion(asked) || IsBridgeDataQuestion(asked))
                     && PostMessageW(no, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                // ETABS 23's steel design by the Direct Analysis Method asks whether to analyse and design again when the
                // stiffness reduction factors changed; it held the 0.4.59 full test 10 minutes. The test's own design is
                // one pass: No.
                // SAP2000 26.3 Plus, opening the .sdb it had just made from the Modeler's .s2k, asks whether to save the file
                // under a new name after removing its bridge data; it held the 0.4.61 full test's SAP2000 part 25 minutes.
                // The scratch file is the test's own: No, and the open goes on.
                closed.Add(IsBridgeDataQuestion(asked) ? $"{title}: asked whether to save the file with its bridge data removed under a new name (No)" : $"{title}: asked whether to reiterate the analysis and design (No)");
            else if (program is not null && IsProgramBox(title, program) && IsDialog(window) && Button(window, "OK") is { } fine && IsWindowEnabled(fine)
                     && LongWords(window).ToList() is var words && !words.Any(w => w.Contains("licen", StringComparison.OrdinalIgnoreCase))
                     && PostMessageW(fine, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                // Every message box of the program itself is answered OK and said (Ahmed, 2026-11-26: "please handle the
                // pop ups"; SAP2000's "Error getting text file version!", "Error reading line"): never a licence message.
                closed.Add($"{title}: {(words.FirstOrDefault(IsFileRefused) ?? string.Join(" ", words.Where(w => w.Trim().Length > 0).Take(3))).Trim()} (OK)");
            // Only the real file picker, told by its Open button: ETABS 23 may also show its open progress under the same
            // title with a Cancel button, and cancelling that stopped the Engine's design-only open of an .edb that was
            // there (0.4.59 full test: "it answered 0 ... Open Model File (Cancel)").
            else if (program is not null && IsOpenFileBox(title) && Button(window, "Open") is not null && Button(window, "Cancel") is { } cancel && IsWindowEnabled(cancel)
                     && PostMessageW(cancel, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                closed.Add($"{title}: {program.Name} asked for a file to open (Cancel)");
            else if (IsMaintenanceReminder(title, Words(window))) closed.AddRange(AnswerMaintenanceReminder([processId]));
            else if (IsStoryPicker(title) && Button(window, "OK") is { } go && IsWindowEnabled(go))
            {
                // All stories first, then OK: the two presses reach the window in this order.
                if (Button(window, "Select All") is { } all && IsWindowEnabled(all)) PostMessageW(all, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                if (PostMessageW(go, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero)) closed.Add(title + " (all stories, OK)");
            }
            else if (IsAnalysisMessages(title) && (Button(window, "OK") ?? Button(window, "Close")) is { } shut && IsWindowEnabled(shut))
            {
                // ETABS 23's "Analysis Messages" window held the 0.4.57 full test for 10 minutes at the Italian NTC 2018 wind
                // load. Its messages are read before it is closed, so the report says what ETABS warned of.
                var said = LongWords(window).Where(w => w.Trim().Length > 0 && !IsAnalysisMessagesControl(w)).Take(12).ToList();
                if (PostMessageW(shut, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                    closed.Add($"{title}: {(said.Count > 0 ? string.Join(" | ", said) : "no message text")} (closed)");
            }
            else if (program is not null && IsLineLog(title) && (Button(window, "OK") is { } logOk ? (logOk, "OK") : Button(window, "Cancel") is { } logCancel ? (logCancel, "Cancel") : default) is ({ } seen, { } pressed) && seen != IntPtr.Zero && IsWindowEnabled(seen))
            {
                // ETABS 23's "Log" window on opening a .e2k ("Following line(s) were duplicated and will be deleted in this
                // model..."): it held both scratch ETABS of the 0.4.58 full test. Its text is read first, so the report says
                // what ETABS found in the file.
                var said = LongWords(window).Where(w => w.Trim().Length > 0 && !IsButton(w, "OK") && !IsButton(w, "Cancel")).Take(12).ToList();
                if (PostMessageW(seen, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                    closed.Add($"{title}: {(said.Count > 0 ? string.Join(" | ", said) : "no text")} ({pressed})");
            }
            else if (IsImportLog(title) && Button(window, "Done") is { } done && IsWindowEnabled(done))
            {
                // Its errors are read before Done is pressed, so the report says what the import did not take.
                var errors = ImportErrors(LongWords(window));
                if (PostMessageW(done, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                    closed.Add(title + " (Done" + (errors.Count > 0 ? "; its errors: " + string.Join(" | ", errors) : "") + ")");
            }
        }
        return closed;
    }

    /// <summary>
    /// True for the story picker ETABS 23 opens when the slab design is started through the API ("Select Stories for
    /// Slab Design", seen 2026-11-25: run_design waited on it for ten minutes). In the test's own scratch window the
    /// test answers it: all stories, OK.
    /// </summary>
    /// <summary>
    /// The program's own file picker ("Open Model File", ETABS 23, 2026-11-26: a scratch ETABS waited on it until Ahmed
    /// pressed Cancel). In the test's own scratch window the test presses Cancel, and the step that caused it fails.
    /// </summary>
    public static bool IsOpenFileBox(string title) => title.Trim().Equals("Open Model File", StringComparison.OrdinalIgnoreCase)
        || title.Trim().Equals("Open", StringComparison.OrdinalIgnoreCase);

    /// <summary>ETABS's "Analysis Messages" window (warnings while it makes auto lateral loads or runs).</summary>
    /// <summary>ETABS's question after a Direct Analysis Method steel design ("...reduction factors... Do you want to
    /// reiterate analysis and design?"). Never a licence message.</summary>
    public static bool IsReiterateQuestion(IEnumerable<string> words)
    {
        var all = string.Join(" ", words);
        return all.Contains("reiterate analysis and design", StringComparison.OrdinalIgnoreCase) && !all.Contains("licen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SAP2000's question on opening a file whose bridge data its licence cannot keep ("This model file has been
    /// modified to remove bridge-related data. Do you want to save this file with a new name?"). Never a licence message.</summary>
    public static bool IsBridgeDataQuestion(IEnumerable<string> words)
    {
        var all = string.Join(" ", words);
        return all.Contains("remove bridge-related data", StringComparison.OrdinalIgnoreCase) && all.Contains("new name", StringComparison.OrdinalIgnoreCase)
            && !all.Contains("licen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Waits until <paramref name="file"/> can be opened by this process alone (no program holds it), at most <paramref name="most"/>.</summary>
    internal static bool WaitUntilFree(string file, TimeSpan most)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { } return true; }
            catch (IOException) when (watch.Elapsed < most) { Thread.Sleep(1000); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>ETABS's plain "Log" window, shown when it opens a .e2k it had to change (duplicated lines).</summary>
    public static bool IsLineLog(string title) => title.Trim().Equals("Log", StringComparison.OrdinalIgnoreCase);

    public static bool IsAnalysisMessages(string title) => title.Trim().Equals("Analysis Messages", StringComparison.OrdinalIgnoreCase);

    /// <summary>The window's own controls, left out of what it said.</summary>
    private static bool IsAnalysisMessagesControl(string text) => text.Trim() is "Show Log" or "New Messages Only" or "errors" or "Information" or "Error or Warning" or "Warning" or "Error" or "Write to Text File" or "OK" or "Close";

    /// <summary>A message box of the program itself: titled with its name alone ("SAFE").</summary>
    public static bool IsProgramBox(string title, CsiProgram program) => string.Equals(title.Trim(), program.Name, StringComparison.OrdinalIgnoreCase)
        // ETABS 23 titles its file errors "Warning" ("Error in performing miOpen", Ahmed's PC 2026-11-26).
        || string.Equals(title.Trim(), "Warning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The program's word that it could not read a file it was given ("Error decompressing file. May not be a valid
    /// ETABS .EDB file.", SAFE 20 given an .f2k, 2026-11-26; "Error in performing miOpen", ETABS 23 in a box titled
    /// "Warning", 2026-11-26). In the test's own scratch window it is answered OK, so
    /// the call comes back and the step can say why. Never a licence message.
    /// </summary>
    /// <summary>The program's own "Open Model File" box, which the test cancelled: it did not get the file it was given.</summary>
    public static bool AskedForFile(string text) => text.Contains("asked for a file to open", StringComparison.OrdinalIgnoreCase);

    public static bool IsFileRefused(string text) =>
        (text.Contains("Error decompressing file", StringComparison.OrdinalIgnoreCase) || text.Contains("Error in performing miOpen", StringComparison.OrdinalIgnoreCase)
            // ETABS 23 on a line of a text file it cannot read: "Error reading line 348. Line Ignored." (2026-11-26).
            || text.Contains("Line Ignored", StringComparison.OrdinalIgnoreCase)
            // ETABS 23.2 on a whole .e2k it rejects: "Error importing from file. May not be a valid ETABS 23.2.0 text file"
            // (Ahmed's real model in the Engine, 2026-11-27; File.OpenFile still answered 0 and left a blank model).
            || text.Contains("Error importing from file", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not be a valid", StringComparison.OrdinalIgnoreCase)
            // The file picker the test cancelled (IsOpenFileBox): the call that opened it did not get its file.
            || text.Contains("asked for a file to open", StringComparison.OrdinalIgnoreCase))
        && !text.Contains("licen", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SAP2000's "Maintenance Expiring" reminder ("License Available. Session Created. Maintenance expiring on
    /// 5-Jul-2019."), shown at every start on Ahmed's PC. It says a licence WAS given, so the test answers it itself
    /// (Ahmed, 2026-11-26: "find a way that this maintenance expiring not to wait for me please"): "Do not show this
    /// message until next expiration period", then OK. Any other licence message (none free, an error) is never this.
    /// </summary>
    public static bool IsMaintenanceReminder(string title, IEnumerable<string> words)
    {
        var all = string.Join(" ", words);
        return title.Trim().Equals("Maintenance Expiring", StringComparison.OrdinalIgnoreCase)
            && all.Contains("License Available", StringComparison.OrdinalIgnoreCase) && all.Contains("Maintenance expiring on", StringComparison.OrdinalIgnoreCase)
            && !all.Contains("in use", StringComparison.OrdinalIgnoreCase) && !all.Contains("not available", StringComparison.OrdinalIgnoreCase)
            && !all.Contains("Status ", StringComparison.OrdinalIgnoreCase) && !all.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The choice the test makes on the reminder: not again until the next expiration period.</summary>
    public static bool IsNotUntilNextPeriod(string label) =>
        label.Replace("&", "").Contains("until next expiration period", StringComparison.OrdinalIgnoreCase);

    /// <summary>Answers SAP2000's maintenance reminder in the given processes (the test's own): returns what it answered.</summary>
    public static List<string> AnswerMaintenanceReminder(IEnumerable<int> processIds)
    {
        var answered = new List<string>();
        foreach (var id in processIds)
            foreach (var (window, title) in Of(id))
            {
                if (!IsMaintenanceReminder(title, Words(window)) || Button(window, "OK") is not { } ok || !IsWindowEnabled(ok)) continue;
                IntPtr? later = null;
                EnumChildWindows(window, (child, _) =>
                {
                    var label = new System.Text.StringBuilder(200);
                    if (GetWindowTextW(child, label, label.Capacity) > 0 && IsNotUntilNextPeriod(label.ToString()) && IsWindowVisible(child)) { later = child; return false; }
                    return true;
                }, IntPtr.Zero);
                if (later is { } choice) PostMessageW(choice, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                if (PostMessageW(ok, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero))
                    answered.Add(title.Trim() + (later is null ? " (OK)" : " (not until the next expiration period, OK)"));
            }
        return answered;
    }

    public static bool IsStoryPicker(string title) => title.TrimStart().StartsWith("Select Stories for Slab Design", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the import log SAP2000 shows when it opens a .s2k it had something to say about ("Access Database Import
    /// Log", with Done). Seen on Ahmed's PC on 2026-11-26: the 0.4.36 run opened the Modeler's cpm-field-test.s2k (1 error,
    /// 13 warnings) and the log waited all night for a person. In the test's own scratch window the test reads its errors
    /// and presses Done.
    /// </summary>
    public static bool IsImportLog(string title)
    {
        var t = title.Trim();
        return t.EndsWith("Import Log", StringComparison.OrdinalIgnoreCase) && (t.Contains("Database", StringComparison.OrdinalIgnoreCase) || t.Contains("Import Log", StringComparison.OrdinalIgnoreCase))
            && !t.Contains("licen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The errors of an import log's text (not its counts), at most ten. SAP2000 26.3 heads each error with a line of its
    /// own ("*** Error Message 1 ***") and gives the message on the lines after it: the 0.4.65 SAP2000 run kept only ten
    /// such headers, so nobody could tell what the import did not take. A header now carries the lines up to the next
    /// "***" line with it.
    /// </summary>
    public static List<string> ImportErrors(IEnumerable<string> texts)
    {
        var lines = texts.SelectMany(t => t.Split('\n')).Select(l => l.Trim()).ToList();
        var found = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"^\*+\s*Error Message\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var body = new List<string>();
                for (var j = i + 1; j < lines.Count && !lines[j].StartsWith("***", StringComparison.Ordinal) && body.Count < 6; j++)
                    if (lines[j].Length > 0) body.Add(lines[j]);
                var said = string.Join(" ", body);
                found.Add(said.Length == 0 ? l : said.Length > 300 ? said[..300] + "..." : said);
                continue;
            }
            if (l.StartsWith("***", StringComparison.Ordinal)) continue;
            if (l.Contains("error", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Number of errors", StringComparison.OrdinalIgnoreCase)
                && !l.StartsWith("Import Control for Errors", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+\s+errors?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !l.Equals("Error", StringComparison.OrdinalIgnoreCase) && !l.Equals("Error or Warning", StringComparison.OrdinalIgnoreCase)
                && !found.Any(f => f.Contains(l, StringComparison.Ordinal)))
                found.Add(l);
        }
        return found.Distinct().Take(10).ToList();
    }

    /// <summary>The whole texts of a window's child controls, read with WM_GETTEXT so a long log comes whole.</summary>
    private static List<string> LongWords(IntPtr window)
    {
        var words = new List<string>();
        if (!OperatingSystem.IsWindows()) return words;
        EnumChildWindows(window, (child, _) =>
        {
            var text = new System.Text.StringBuilder(200000);
            if (SendMessageTimeoutW(child, 0x000D /* WM_GETTEXT */, new IntPtr(text.Capacity), text, 2 /* SMTO_ABORTIFHUNG */, 2000, out _) != IntPtr.Zero && text.Length > 0)
                words.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return words;
    }

    /// <summary>The label of a button as a person reads it: without the "&amp;" of its access key.</summary>
    public static bool IsButton(string label, string text) => string.Equals(label.Replace("&", "").Trim(), text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The window's own visible button with this text, or null.</summary>
    private static IntPtr? Button(IntPtr window, string text)
    {
        IntPtr? found = null;
        if (!OperatingSystem.IsWindows() || window == IntPtr.Zero) return null;
        EnumChildWindows(window, (child, _) =>
        {
            var label = new System.Text.StringBuilder(64);
            if (GetWindowTextW(child, label, label.Capacity) > 0 && IsButton(label.ToString(), text) && IsWindowVisible(child))
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// True for the one question the test answers itself: the program asking whether it may really close ("Program is
    /// active. Do you really want to close the program?"), which it shows when the test closes its own scratch window
    /// while the program is still busy. Told by the question's own words; a licence message is never this.
    /// </summary>
    public static bool IsCloseQuestion(IEnumerable<string> words) =>
        words.Any(w => w.Contains("Program is active", StringComparison.OrdinalIgnoreCase) && w.Contains("close the program", StringComparison.OrdinalIgnoreCase));

    /// <summary>The words on a window: the texts of its child controls.</summary>
    public static List<string> Words(IntPtr window)
    {
        var words = new List<string>();
        if (!OperatingSystem.IsWindows()) return words;
        EnumChildWindows(window, (child, _) =>
        {
            var text = new System.Text.StringBuilder(600);
            if (GetWindowTextW(child, text, text.Capacity) > 0) words.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return words;
    }

    /// <summary>
    /// Answers the "really close the program?" question of the process, as its own buttons do: Yes while the test is
    /// closing its own scratch window, No at any other time (the test goes on in that window). Returns how many were answered.
    /// </summary>
    public static int AnswerCloseQuestion(int processId, bool yes)
    {
        var answered = 0;
        foreach (var (window, _) in Of(processId))
            if (IsCloseQuestion(Words(window)) && PostMessageW(window, 0x0111 /* WM_COMMAND */, new IntPtr(CloseAnswer(yes)), IntPtr.Zero)) answered++;
        return answered;
    }

    /// <summary>The button the test presses on the close question: Yes (6) only while it is closing the window itself, else No (7).</summary>
    public static int CloseAnswer(bool closing) => closing ? 6 : 7;

    /// <summary>What Windows says of a window: " (cannot be clicked: another window of the program holds it)", " (not answering)", or nothing.</summary>
    public static string State(IntPtr window)
    {
        if (!OperatingSystem.IsWindows()) return "";
        var said = new List<string>();
        if (!IsWindowEnabled(window)) said.Add("cannot be clicked: the program has it disabled");
        if (IsHungAppWindow(window)) said.Add("not answering");
        return said.Count == 0 ? "" : " (" + string.Join(", ", said) + ")";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder name, int most);

    /// <summary>A dialog box (class #32770), such as a message box: never the program's main window, whose title is its name alone while it starts or opens a file.</summary>
    public static bool IsDialog(IntPtr window) { var name = new System.Text.StringBuilder(64); return GetClassNameW(window, name, name.Capacity) > 0 && name.ToString() == "#32770"; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
    private delegate bool Each(IntPtr window, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(Each each, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, Each each, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, System.Text.StringBuilder text, int most);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr w, System.Text.StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
}

/// <summary>
/// A window of the CSI program that the field test starts for itself, licensed as usual, on a scratch model: never a
/// window the engineer has open. It opens the model file the Modeler wrote, saves it as a scratch model in the test's
/// folder, hands out a link to exactly that window, and closes the window without saving when the test is done.
/// Every call to the window is made on the link's one thread.
/// </summary>
public sealed class ScratchProgram : IScratchWindow
{
    /// <summary>How long the program may take to start before the test gives up on it (a licence message may be waiting).</summary>
    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>How long closing the scratch window may take before the test gives up on it.</summary>
    public static TimeSpan CloseTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long opening the model file, or saving the scratch model, may take.</summary>
    public static TimeSpan FileTimeout { get; set; } = TimeSpan.FromMinutes(3);

    private readonly CsiProgram _program;
    private ComEtabs? _com;
    private dynamic? _app;
    /// <summary>The pipe to a .NET Framework program's scratch window (Windows PowerShell holds it), else null.</summary>
    private ApiPipe? _pipe;
    /// <summary>The window stopped answering: its thread is still waiting on it, so nothing more can be asked of it.</summary>
    private bool _left;
    private bool _closed;
    private Dictionary<string, string> _plugIns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings files whose plug-in list the scratch window had emptied, and that were put back.</summary>
    public IReadOnlyList<string> PlugInListsPutBack { get; private set; } = [];

    public IReadOnlyList<string> PutBackPlugIns()
    {
        if (_plugIns.Count == 0) return PlugInListsPutBack;
        var back = KeptPlugIns.PutBack(_plugIns);
        if (back.Count > 0) PlugInListsPutBack = PlugInListsPutBack.Concat(back).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return PlugInListsPutBack;
    }

    /// <summary>The test is closing its own window: only then is the program's "really close?" question answered.</summary>
    private volatile bool _closing;
    private volatile bool _closeAnswered;
    /// <summary>How often the program asked whether to really close while the test was still working in it, and the test answered No.</summary>
    private int _keptOpen;
    public int KeptOpen => _keptOpen;

    private ScratchProgram(CsiProgram program) => _program = program;
    private bool _asIs;

    /// <summary>When this test asked the program to start: a process older than that is never the test's.</summary>
    private DateTime _startedAt;
    private Timer? _watch;
    private readonly List<string> _noticesClosed = [];

    /// <summary>The titles of the windows the scratch program has open now, each with whether it can be clicked and whether it answers.</summary>
    /// A window other than the program's main one also gives its words (what a message box says), so a box that held the
    /// scratch window (ETABS 20 opening a checkpoint, 0.4.53) can be answered by the test next time.
    public IReadOnlyList<string> OpenWindows() =>
        ScratchDialogs.Of(ProcessId).Select(w => w.Title + ScratchDialogs.State(w.Window) + Said(w.Window)).ToList();

    private static string Said(IntPtr window)
    {
        try
        {
            var words = string.Join(" / ", ScratchDialogs.Words(window).Where(x => x.Trim().Length > 0).Take(8));
            return words.Length == 0 ? "" : " saying: " + (words.Length > 300 ? words[..300] + "..." : words);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

    public ICsiLink Link { get; private set; } = null!;

    /// <summary>The scratch window's process, 0 when it could not be told apart.</summary>
    public int ProcessId { get; private set; }

    /// <summary>The Modeler's .cpm of the file this window was given, when it opens a converted copy of it.</summary>
    private string? _modelerFile;

    public string ModelPath { get; private set; } = "";

    /// <summary>The API file the program was reached through.</summary>
    public string ApiFile { get; private set; } = "";

    /// <summary>
    /// Starts the program, opens <paramref name="modelFile"/> and saves it as the scratch model. Each stage is a step
    /// in the report. Null when a stage failed: then nothing else is tried, because a change must never reach a model
    /// that is not the test's.
    /// </summary>
    public static IScratchWindow? Start(CsiProgram program, FieldContext ctx, string modelFile) => Start(program, ctx, modelFile, asIs: false);

    /// <summary>
    /// <paramref name="asIs"/>: the file is the program's own model, saved by an earlier window of this kind with its
    /// analysis (CSI ProMax Engine's design on its own): it is opened under its own name and not saved as a new scratch
    /// model, so its results stay with it.
    /// </summary>
    public static IScratchWindow? Start(CsiProgram program, FieldContext ctx, string modelFile, bool asIs)
    {
        // SAP2000 26 refuses every table import (ApplyEditedTables answers 1, with no log, and every load pattern is
        // gone) in a process that has opened an .s2k, even a tiny one, and even after it opens an .sdb again; a new
        // SAP2000 opening the same .sdb takes the same rows (experiment N on Ahmed's PC, 2026-11-26: the 20 table codes
        // of the 0.4.54 field test). So one SAP2000 turns the Modeler's .s2k into a .sdb and closes, and the test
        // works in a second one that opens that .sdb, as an engineer's SAP2000 opening its model file would be.
        // The Modeler's .cpm beside the text file: the .sdb made from it has none beside it, and its rows (SAP2000's output
        // stations, set by the API) were never put in (0.4.57 full test: "'Frame Output Station Assignments' has no row with 7").
        var modelerFile = ModelerTables.ModelerFileBeside(modelFile);
        string? text = null;
        if (OpensTextInOwnProcess(program, modelFile))
        {
            var first = new ScratchProgram(program) { _pass = $" (turning {Path.GetFileName(modelFile)} into a {ModelExtension(program)} first)" };
            string? converted = null;
            try { if (first.Open(ctx, modelFile, convertOnly: true)) converted = first.ModelPath; }
            finally
            {
                try
                {
                    if (first._app is not null)
                        ctx.Step($"close the {program.Name} that opened {Path.GetFileName(modelFile)} ({program.Name} takes no table import in a process that opened a text file)", () =>
                        {
                            var said = first.Close();
                            FieldContext.Expect(said.Contains("closed without saving", StringComparison.Ordinal) && !said.Contains("yourself", StringComparison.Ordinal), said);
                            return said;
                        });
                }
                finally { first.Dispose(); }
            }
            if (converted is null) return null;
            text = modelFile;
            modelFile = converted;
        }
        var scratch = new ScratchProgram(program) { _asIs = asIs, _modelerFile = modelerFile };
        var ok = false;
        try
        {
            ok = scratch.Open(ctx, modelFile);
            if (ok) return scratch;
        }
        finally
        {
            if (!ok)
            {
                // Dispose closes the window when the step could not (the part was given up on meanwhile).
                try { if (scratch._app is not null) CloseStep(ctx, scratch, program); }
                finally { scratch.Dispose(); }
            }
        }
        if (text is not null && !_pinpointing) Pinpoint(ctx, program, text);
        return null;
    }

    /// <summary>True while <see cref="Pinpoint"/> runs: its own opens never pinpoint again.</summary>
    private static volatile bool _pinpointing;

    /// <summary>
    /// The items of the Modeler's .s2k that SAP2000 can save but its next process cannot open again, each tried alone.
    /// 0.4.65 SAP2000 run (Ahmed's PC, 2026-11-27): the .sdb the first SAP2000 saved from the Modeler's .s2k made the
    /// second one show its "Open Model File" box, and the only new content was a beam's partial fixity springs, its
    /// insertion point's Mirror2 and its global joint offsets. Each one is put back alone on a copy without all of them,
    /// so the report names the one SAP2000 cannot reopen.
    /// </summary>
    private static void Pinpoint(FieldContext ctx, CsiProgram program, string s2k)
    {
        string original;
        try { original = File.ReadAllText(s2k); } catch (IOException) { return; }
        var items = ReopenSuspects(original);
        if (items.Count == 0) return;
        var folder = Path.Combine(ctx.Folder, "reopen-check");
        Directory.CreateDirectory(folder);
        var bare = items.Aggregate(original, (t, item) => item.Strip(t));
        var refused = new List<string>();
        _pinpointing = true;
        try
        {
            foreach (var item in items)
            {
                var kept = items.Where(i => i != item).Aggregate(original, (t, i) => i.Strip(t));
                if (kept == bare) continue;
                var file = Path.Combine(folder, "only-" + item.Key + ".s2k");
                File.WriteAllText(file, kept);
                IScratchWindow? window = null;
                try
                {
                    window = Start(program, ctx, file);
                    if (window is null) refused.Add(item.Name);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { refused.Add(item.Name + " (" + ex.Message + ")"); }
                finally
                {
                    if (window is not null) { try { CloseStep(ctx, window, program); } finally { window.Dispose(); } }
                }
            }
        }
        finally { _pinpointing = false; }
        var said = refused.Count > 0
            ? $"{program.Name} saves but cannot open again a model with: {string.Join("; ", refused)} (each tried alone, copies in {folder})"
            : $"{program.Name} opens again a model with each of {string.Join(", ", items.Select(i => i.Name))} alone, so only them together stop it (copies in {folder})";
        ctx.Fail($"which part of the Modeler's {Path.GetExtension(s2k)} {program.Name} cannot open again after saving it", said);
    }

    /// <summary>One item of a .s2k the reopen check puts back alone: a short key, its name for the report, and how to take it out.</summary>
    public sealed record ReopenSuspect(string Key, string Name, Func<string, string> Strip);

    /// <summary>The items <see cref="Pinpoint"/> tries one at a time, those the text has.</summary>
    public static List<ReopenSuspect> ReopenSuspects(string text)
    {
        var all = new List<ReopenSuspect>
        {
            new("partial-fixity", "frame partial fixity springs (FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY)", t =>
                WithoutTable(t, "FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY").Replace("PartialFix=Yes", "PartialFix=No", StringComparison.Ordinal)),
            new("mirror", "frame insertion point Mirror2/Mirror3=Yes", t => InTable(t, "FRAME INSERTION POINT ASSIGNMENTS", l =>
                l.Replace("Mirror2=Yes", "Mirror2=No", StringComparison.Ordinal).Replace("Mirror3=Yes", "Mirror3=No", StringComparison.Ordinal))),
            new("joint-offsets", "frame insertion point joint offsets", t => InTable(t, "FRAME INSERTION POINT ASSIGNMENTS", l =>
                System.Text.RegularExpressions.Regex.Replace(l, @"(JtOffset[XYZ][IJ])=\S+", "$1=0").Replace("CoordSys=GLOBAL", "CoordSys=Local", StringComparison.Ordinal))),
        };
        return all.Where(i => i.Strip(text) != text).ToList();
    }

    /// <summary>The text without the named table (its TABLE line and rows, up to the next table or the end).</summary>
    public static string WithoutTable(string text, string table)
    {
        var lines = text.Split('\n');
        var kept = new List<string>();
        var skip = false;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("END TABLE DATA", StringComparison.OrdinalIgnoreCase))
                skip = t.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase) && t.Contains("\"" + table + "\"", StringComparison.OrdinalIgnoreCase);
            if (!skip) kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>The text with <paramref name="change"/> applied to the rows of the named table only.</summary>
    public static string InTable(string text, string table, Func<string, string> change)
    {
        var lines = text.Split('\n');
        var inside = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("END TABLE DATA", StringComparison.OrdinalIgnoreCase))
                inside = t.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase) && t.Contains("\"" + table + "\"", StringComparison.OrdinalIgnoreCase);
            else if (inside) lines[i] = change(lines[i]);
        }
        return string.Join("\n", lines);
    }

    /// <summary>Closes a scratch window as a step of the report: a window that had to be left open is a failure the tester must see.</summary>
    public static void CloseStep(FieldContext ctx, IScratchWindow window, CsiProgram program) =>
        ctx.Step($"close the scratch {program.Name} window without saving", () =>
        {
            var said = window.Close();
            FieldContext.Expect(said.Contains("closed without saving", StringComparison.Ordinal) && !said.Contains("yourself", StringComparison.Ordinal), said);
            return said;
        });

    /// <summary>
    /// True when the program must open <paramref name="file"/> in a process of its own and work in the model file it
    /// saves from it, in a new process: SAP2000 and a text file (.s2k, .$2k). See <see cref="Start"/>.
    /// </summary>
    public static bool OpensTextInOwnProcess(CsiProgram program, string file) =>
        program.Key == CsiProgram.Sap2000.Key && Path.GetExtension(file).ToLowerInvariant() is ".s2k" or ".$2k";

    /// <summary>Said after the names of the steps of the window that only turns a text file into a model file.</summary>
    private string _pass = "";

    private bool Open(FieldContext ctx, string modelFile, bool convertOnly = false)
    {
        var name = _program.Name;
        Assembly? loaded = null;
        if (ctx.Step($"find {name}'s API file", () =>
        {
            ApiFile = EtabsApi.FindApiFile(_program);
            FieldContext.Expect(File.Exists(ApiFile), $"{name} is not installed on this PC, or its API file was not found ({ApiFile}).");
            loaded = EtabsApi.Load(ApiFile);
            return $"{ApiFile}: {loaded.FullName}";
        }) is null || loaded is not { } api) return false;

        // SAFE 20 and 21 (on the .NET Framework) open only their own .fdb through the API: given the Modeler's .f2k,
        // SAFE 20 showed "Error decompressing file. May not be a valid ETABS .EDB file." and waited for a person
        // (tester's PC, 0.4.47, 2026-11-26). Such a program starts a blank model instead, and the test builds the
        // Modeler's model in it through the API (F2kRebuild), so the editing tools are still tried (Ahmed, 2026-11-26).
        var build = !OpensThroughApi(_program, modelFile, EtabsApi.RunsOnFramework(ApiFile));
        if (build && !BuildsThroughApi(_program, modelFile))
        {
            ctx.Fail($"start a second {name} window", $"{name} on this PC ({Path.GetFileName(Path.GetDirectoryName(ApiFile)) ?? name}) opens only its own {ModelExtension(_program)} files through its API, " +
                $"and the Modeler writes {Path.GetExtension(modelFile)}. To check the Modeler's file by hand: in {name}, File > Import, pick {Path.GetFileName(modelFile)} from the zip. The test has no way yet to build this file through the API.");
            return false;
        }

        // The program's plug-in lists as they are before the scratch window: put back when it has closed (KeptPlugIns).
        _plugIns = KeptPlugIns.Take(_program);
        var before = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).ToHashSet();
        _startedAt = DateTime.Now;
        var com = _com = new ComEtabs(_program, () =>
        {
            var ns = EtabsApi.NamespaceOf(ApiFile);
            dynamic started;
            if (EtabsApi.RunsOnFramework(ApiFile))
            {
                // ETABS 21, SAP2000 25, SAFE 21 and older: their Helper starts the program through .NET Remoting, which
                // .NET 8 does not have ("gave no application object" for SAFE 20, ETABS 20 and SAP2000 24, 2026-11-26).
                // Windows PowerShell starts it with the same Helper, and the calls go over the plug-in's pipe.
                try { _pipe = ApiPipe.ToPowerShell(_program, ApiFile, "start", null); }
                catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new EtabsException($"{name} could not be started ({inner.GetType().Name}: {inner.Message})."); }
                started = _pipe.Root();
            }
            else
            {
                var helper = Activator.CreateInstance(api.GetType(ns + ".Helper", true)!)!;
                object app;
                try
                {
                    app = api.GetType(ns + ".cHelper", true)!.GetMethod("CreateObjectProgID")!.Invoke(helper, [_program.ProgId])
                        ?? throw new EtabsException($"{name} gave no application object.");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new EtabsException($"{name} could not be started ({inner.GetType().Name}: {inner.Message})."); }
                started = new EtabsApi(app, api.GetType(ns + ".cOAPI", true)!);
            }
            _app = started;
            var answer = Convert.ToInt32(started.ApplicationStart());
            if (answer != 0) throw new EtabsException($"{name} refused to start (it answered {answer}).");
            return started.SapModel;
        });
        if (ctx.Step($"start a second {name} window{_pass}", () =>
        {
            // While it starts, the maintenance reminder of a new process of this program (the test's own) is answered.
            using var starting = new Timer(_ =>
            {
                try
                {
                    var fresh = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
                    var said = ScratchDialogs.AnswerMaintenanceReminder(fresh);
                    if (said.Count > 0) lock (_noticesClosed) _noticesClosed.AddRange(said);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next look tries again */ }
            }, null, 1000, 1000);
            Ask(StartTimeout, "start", _ => null,
                $"{name} did not start within {StartTimeout.TotalMinutes:0} minutes. A licence or start-up message may be waiting in its window: answer it yourself, close that window, and run the field test again.");
            var mine = Process.GetProcessesByName(_program.ProcessName).Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
            ProcessId = mine.Count == 1 ? mine[0] : 0;
            if (ProcessId != 0) com.Known = ProcessId;
            if (ProcessId != 0) Remember(ProcessId);
            // From here to the window's end, an "analysis complete" notice of this process is closed as it appears.
            if (ProcessId != 0) _watch = new Timer(_ => { try { lock (_noticesClosed) _noticesClosed.AddRange(ScratchDialogs.CloseNotices(ProcessId, _program)); var closing = _closing; if (ScratchDialogs.AnswerCloseQuestion(ProcessId, closing) > 0) { if (closing) _closeAnswered = true; else Interlocked.Increment(ref _keptOpen); } } catch (Exception ex) when (ex is not OutOfMemoryException) { /* the next look tries again */ } }, null, 1000, 1000);
            return mine.Count == 1 ? $"{name} process {ProcessId}, through {Path.GetFileName(ApiFile)}"
                : $"{name} started through {Path.GetFileName(ApiFile)}; its process could not be told apart ({mine.Count} new {name} processes)";
        }) is null) return false;

        // A name of this run's own: two tests on one PC (it happened) must never take each other's window for theirs.
        var target = _asIs ? Path.GetFullPath(modelFile) : Path.Combine(ctx.Folder, ScratchName() + ModelExtension(_program));
        var text = build ? File.ReadAllText(modelFile) : "";
        bool Blank(string why) => ctx.Step($"start a blank model in it ({why})", StartBlank) is not null;
        string StartBlank()
            {
                var units = F2kRebuild.UnitsOf(text);
                // The story at the file's own level: SAFE 22.7 refused every floor of the Modeler's file (at 3.2 m) in a
                // blank model whose story was at 3 m (tester's SAFE-only run, 0.4.66).
                var height = F2kRebuild.StoryHeightOf(text);
                Ask(FileTimeout, "start a blank model", m =>
                {
                    object model = m;
                    ApiCall.Must(model, "InitializeNewModel", CsiTools.D(("Units", units)), "start a new model");
                    return ApiCall.First(model, ["File.NewBlank", "File.NewGridOnly"], CsiTools.D(("NumberStorys", 1), ("TypicalStoryHeight", height), ("BottomStoryHeight", height), ("NumberLinesX", 2), ("NumberLinesY", 2), ("SpacingX", 6), ("SpacingY", 6)), "start a blank model");
                }, $"{name} did not finish starting a blank model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window.");
                // NewBlank takes no story height in SAFE: the one story is moved to the file's level when it is not there.
                var level = F2kRebuild.FloorLevelOf(text);
                var stories = level is null ? "" : Convert.ToString(Ask(FileTimeout, "put the story at the file's level", m => SafeStory.AtLevel(m, level.Value),
                    $"{name} did not answer within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window.")) ?? "";
                return $"blank, in {ComEtabs.UnitNames[units - 1]}, {stories}";
            }
        if (build && !Blank($"{name} on this PC opens only {ModelExtension(_program)} files through its API, so the Modeler's {Path.GetExtension(modelFile)} is built in it instead")) return false;
        var openedEmpty = false;
        if (!build && ctx.Step($"open {Path.GetFileName(modelFile)} in it{_pass}", () =>
        {
            // A missing file makes the program ask for one in its own file box and wait there (SAP2000, 0.4.57 full test).
            if (!File.Exists(modelFile)) throw new EtabsException($"{Path.GetFileName(modelFile)} is not there ({modelFile}), so it is not given to {name}.");
            // The program that saved it may still hold it for a moment (0.4.60 full test: the second SAP2000 asked for a file
            // to open, for the .sdb the first had just written): it is opened only once no one holds it, at most 60 s.
            ScratchDialogs.WaitUntilFree(modelFile, TimeSpan.FromSeconds(60));
            var answer = Convert.ToInt32(Ask(FileTimeout, "open the file", m => m.File.OpenFile(Path.GetFullPath(modelFile)),
                $"{name} did not finish opening the file within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            // The .sdb the first SAP2000 saved from the Modeler's .s2k may still be held for a moment after that SAP2000
            // closed (0.4.57 candidate run, 2026-11-26: answer 1 once, the run before took it): it is tried again.
            for (var again = 0; answer != 0 && again < 3 && string.Equals(Path.GetExtension(modelFile), ModelExtension(_program), StringComparison.OrdinalIgnoreCase); again++)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                answer = Convert.ToInt32(Ask(FileTimeout, "open the file", m => m.File.OpenFile(Path.GetFullPath(modelFile)),
                    $"{name} did not finish opening the file within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            }
            string[] refused;
            lock (_noticesClosed) refused = _noticesClosed.Where(n => ScratchDialogs.IsFileRefused(n)).ToArray();
            // ETABS 23.2 answered 0 and asked for a file in its own "Open Model File" box, or held "(Untitled)", for an
            // .EDB that was there, just after the program before it had closed with it (0.4.68 full test: the Engine's
            // second run on the analysed model, and the hidden copy of the pack). The file is given again, up to 3 times.
            for (var again = 0; again < 3 && answer == 0 && File.Exists(modelFile) && (refused.Any(ScratchDialogs.AskedForFile) || !Holds(modelFile)); again++)
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
                lock (_noticesClosed) _noticesClosed.RemoveAll(ScratchDialogs.AskedForFile);
                ScratchDialogs.WaitUntilFree(modelFile, TimeSpan.FromSeconds(60));
                answer = Convert.ToInt32(Ask(FileTimeout, "open the file again", m => m.File.OpenFile(Path.GetFullPath(modelFile)),
                    $"{name} did not finish opening the file within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
                Thread.Sleep(TimeSpan.FromSeconds(2));
                lock (_noticesClosed) refused = _noticesClosed.Where(n => ScratchDialogs.IsFileRefused(n)).ToArray();
            }
            if (answer == 0 && refused.Length == 0 && !Holds(modelFile))
                throw new EtabsException($"{name} answered that it opened {Path.GetFileName(modelFile)}, but it holds '{Path.GetFileName(OpenFileName())}' (3 more tries).");
            if (answer != 0 || refused.Length > 0)
                throw new EtabsException($"{name} could not open the file the Modeler wrote (it answered {answer})." + (refused.Length > 0 ? $" It said: {string.Join(" | ", refused)}; the test pressed OK." : ""));
            Ask(FileTimeout, "use the model's own units", m => { CsiProMax.Assistant.Etabs.EditableTables.PresentAsDatabase(m); return 0; }, $"{name} did not answer within {FileTimeout.TotalMinutes:0} minutes.");
            // ETABS 23.2 answered 0 to an .e2k it rejected ("Error importing from file. May not be a valid ETABS 23.2.0 text
            // file") and held its blank default model; the Engine then saved and ran that (Ahmed's real model, 2026-11-27).
            // A text file whose objects did not come in stops here, with the program's own words, never runs on a blank model.
            if (!BuildsThroughApi(_program, modelFile) && TextObjectCount(_program, modelFile) is var expected and > 0)
            {
                var held = Convert.ToInt32(Ask(FileTimeout, "count the objects", m => Count(m, "AreaObj.Count") + Count(m, "FrameObj.Count"), $"{name} did not answer within {FileTimeout.TotalMinutes:0} minutes."));
                if (held == 0)
                {
                    lock (_noticesClosed) refused = _noticesClosed.Where(n => ScratchDialogs.IsFileRefused(n)).ToArray();
                    throw new EtabsException(ImportRejected(name, Path.GetFileName(modelFile), expected, refused));
                }
            }
            // SAFE 23.1 answered 0 to the Modeler's .f2k and held a new default model, with none of the file's slabs,
            // beams or strips (tester's PC, 0.4.56, 2026-11-26). A file the test can build is then built instead.
            if (BuildsThroughApi(_program, modelFile))
            {
                var held = Convert.ToInt32(Ask(FileTimeout, "count the objects", m => Count(m, "AreaObj.Count") + Count(m, "FrameObj.Count"), $"{name} did not answer within {FileTimeout.TotalMinutes:0} minutes."));
                openedEmpty = OpenedEmpty(File.ReadAllText(modelFile), held);
                if (openedEmpty) return $"{name} answered that it opened it, but holds none of its objects: the model is built through the API instead";
            }
            return "opened";
        }) is null) return false;
        if (openedEmpty)
        {
            build = true;
            text = File.ReadAllText(modelFile);
            // Why: the same file in other forms, and SAFE's own export as the control. SAFE 23.1 held nothing of its own
            // export either (tester's SAFE-only run, 0.4.68): its File.OpenFile takes no .f2k, and an engineer's
            // File > Import is what the tables step below does. Only a SAFE that opens its own export and not the
            // Modeler's says the Modeler's file is at fault.
            var probe = _program.Key == CsiProgram.Safe.Key
                ? ctx.Step($"find which .f2k {name} opens through its API (the Modeler's in other forms, and {name}'s own export)", () => Probe(ctx, name, text)) as string
                : null;
            if (probe is not null && ProbeControlEmpty(probe))
                ctx.Step($"{name}'s File.OpenFile takes no .f2k (its own export came in empty too), so the Modeler's file goes in through {name}'s tables, as File > Import does", () => probe);
            else
                ctx.Fail($"{name} opens the Modeler's {Path.GetExtension(modelFile)} with all its objects",
                    $"{name} answered that it opened {Path.GetFileName(modelFile)}, but it holds none of the file's {F2kRebuild.ObjectCount(text)} beams, columns, walls and floors" +
                    (probe is null ? "" : $", while it opens its own export: {probe}") + ". The rest of the test builds the model in a blank model instead.");
            if (!Blank($"{name} opened the Modeler's {Path.GetExtension(modelFile)} as an empty model, so the model is built in it instead")) return false;
        }

        if (!_asIs && ctx.Step($"save it as the scratch model{_pass}", () =>
        {
            var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            if (answer != 0) throw new EtabsException($"{name} could not save the scratch model (it answered {answer}).");
            return target;
        }) is null) return false;
        ModelPath = target;
        if (convertOnly) return true;

        // What the link reads is checked against the scratch file before any change is made through it.
        if (ctx.Step("the link reads the scratch model, and no other", () =>
        {
            var link = ProgramTools.Live(com);
            var file = link.Info().File;
            FieldContext.Expect(SameName(file, target), $"the window that answered has '{Path.GetFileName(file)}' open, not the scratch model, so the test stops here.");
            Link = link;
            return Path.GetFileName(file);
        }) is null) return false;

        // The Modeler's table data (material damping, notes, time dependence, ...) goes into ETABS through its tables, as
        // open_model does for a Modeler .e2k, and the model is saved again.
        if (!build && !_asIs && (_program.Key == CsiProgram.Etabs.Key || _program.Key == CsiProgram.Sap2000.Key) && (_modelerFile ?? ModelerTables.ModelerFileBeside(modelFile)) is { } cpm
            && ModelerTables.HasRows(cpm, _program)
            && ctx.Step($"put the Modeler's data that {name}'s file has no place for in through {name}'s tables and API", () =>
            {
                var (done, refused) = ModelerTables.Apply(Link, cpm);
                var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                    $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
                // ETABS Plus takes no slab rebar object at all (ETABS Ultimate and SAFE do): a limit of this PC's licence,
                // said as not tested, not as a refusal of the Modeler's data.
                var edition = refused.Where(r => r.Contains(Tools.SlabTables.RebarRefused, StringComparison.Ordinal)).ToList();
                if (edition.Count > 0)
                {
                    refused = refused.Except(edition).ToList();
                    ctx.Skip($"{name} takes the Modeler's slab rebar objects", $"this PC's {name} licence (ETABS Plus) has no slab rebar objects; ETABS Ultimate and SAFE take them ({edition.Count} refused)"); // ETABS itself
                }
                FieldContext.Expect(refused.Count == 0, "refused: " + string.Join(" | ", refused));
                return string.Join("; ", done);
            }) is null && _left) return false;

        // The Modeler's model, built in the blank model through the API, and saved: what the program's part then checks.
        if (build && ctx.Step($"build the Modeler's model from {Path.GetFileName(modelFile)} in it through the API", () =>
        {
            // First as SAFE's own File > Import does it, table by table; the object calls only when that brought nothing in.
            JsonNode built;
            JsonObject? tables = null;
            try { tables = F2kRebuild.ImportTables(Link, text); }
            catch (EtabsException ex) { tables = new JsonObject { ["error"] = ex.Message }; }
            var held = Convert.ToInt32(Ask(FileTimeout, "count the objects", m => Count(m, "AreaObj.Count") + Count(m, "FrameObj.Count"), $"{name} did not answer within {FileTimeout.TotalMinutes:0} minutes."));
            var wanted = F2kRebuild.ObjectCount(text);
            if (TablesBroughtAll(held, wanted)) built = new JsonObject { ["through"] = $"{name}'s tables (as File > Import)", ["objects"] = held, ["import"] = tables };
            else
            {
                // Some of the file's objects came in and some did not (SAFE 23.1 on 0.4.69: the columns, no floor or beam):
                // the object calls start again on a blank model, so nothing is there twice.
                string again = "";
                if (held > 0) again = StartBlank();
                if (tables is not null) { tables["objects"] = held; tables["of_the_file"] = wanted; if (again.Length > 0) tables["started_again"] = again; }
                try { built = F2kRebuild.Build(Link, text); }
                catch (EtabsException ex) { throw new EtabsException($"{ex.Message} Before that, {name}'s tables did not bring in every object: {tables?.ToJsonString()}"); }
                if (built is JsonObject o) o["tables_first"] = tables;
            }
            ctx.Facts["safe.built"] = 1;
            var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            if (answer != 0) throw new EtabsException($"{name} could not save the scratch model after building it (it answered {answer}).");
            return built;
        }) is null) return false;

        // SAFE's API reads design strips and has no call to draw them: they go in through its strip table. A strip
        // table SAFE does not take fails this step only; the rest of SAFE's part still runs on the model built above.
        if (build && ctx.Step($"put the Modeler's design strips in through {name}'s table '{F2kRebuild.StripTable}' (its API cannot draw strips)", () =>
        {
            var strips = F2kRebuild.BuildStrips(Link, text);
            ctx.Facts["safe.strips_built"] = strips;
            var answer = Convert.ToInt32(Ask(FileTimeout, "save the scratch model", m => m.File.Save(target),
                $"{name} did not finish saving the scratch model within {FileTimeout.TotalMinutes:0} minutes. A message may be waiting in its window."));
            if (answer != 0) throw new EtabsException($"{name} could not save the scratch model after the strips (it answered {answer}).");
            return $"{name} lists {strips} design strips";
        }) is null && _left) return false;

        // The way the Tools menu reaches a window: by its process id. Checked here, not relied on.
        if (ProcessId != 0)
            ctx.Step($"reach the scratch window by its process id, as the Tools menu does", () =>
            {
                using var byProcess = new ComEtabs(ProcessId, _program);
                var file = byProcess.Info().File;
                FieldContext.Expect(SameName(file, target), $"process {ProcessId} answered with '{Path.GetFileName(file)}', not the scratch model.");
                return $"process {ProcessId} answers with {Path.GetFileName(file)}";
            });
        else ctx.Skip("reach the scratch window by its process id, as the Tools menu does", "its process could not be told apart from the other windows of the program");
        return true;
    }

    /// <summary>The scratch model's name: "cpm-field-test-" and six characters no other run has.</summary>
    public static string ScratchName() => "cpm-field-test-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>One call to the scratch window that may never come back (a message waiting in it): then it is left alone.</summary>
    /// <summary>
    /// Opens each form of the Modeler's .f2k (<see cref="F2kProbe"/>) and SAFE's own export in the scratch window and
    /// says how many objects each held. A probe: it fails only when the window stops answering.
    /// </summary>
    private string Probe(FieldContext ctx, string name, string modelText)
    {
        var most = TimeSpan.FromSeconds(90);
        var late = $"{name} did not answer within {most.TotalSeconds:0} s while the test opened the forms of the file. A message may be waiting in its window.";
        var version = Convert.ToString(Ask(most, "read its version", m => { string v = ""; double n = 0; m.GetVersion(ref v, ref n); return v; }, late)) ?? "";
        var dir = Path.Combine(ctx.Folder, "f2k probe");
        Directory.CreateDirectory(dir);
        var forms = F2kProbe.Forms(modelText, version).ToList();
        if (F2kProbe.Control() is { } own) forms.Insert(0, ($"{name}'s own export of the RC-PN example (the control)", own));
        var said = new List<string>();
        for (var i = 0; i < forms.Count; i++)
        {
            var file = Path.Combine(dir, $"probe-{i + 1}.f2k");
            File.WriteAllText(file, forms[i].Text);
            var answer = Convert.ToInt32(Ask(most, "open the file", m => m.File.OpenFile(file), late));
            var held = Convert.ToInt32(Ask(most, "count the objects", m => Count(m, "AreaObj.Count") + Count(m, "FrameObj.Count"), late));
            said.Add($"{i + 1}. {forms[i].Name}: answered {answer}, holds {held} objects ({Path.GetFileName(file)})");
        }
        return $"{name} {version}: " + string.Join("; ", said);
    }

    /// <summary>Whether the probe's first form, SAFE's own export (the control), came in empty.</summary>
    internal static bool ProbeControlEmpty(string probe) =>
        System.Text.RegularExpressions.Regex.IsMatch(probe, @"1\. [^;]*\(the control\): answered -?\d+, holds 0 objects");

    private object? Ask(TimeSpan most, string what, Func<dynamic, object?> work, string late)
    {
        if (_left || _com is not { } com) throw new EtabsException($"The scratch {_program.Name} window stopped answering earlier.");
        var call = Task.Run(() => com.Raw(what, work));
        try
        {
            if (call.Wait(most)) return call.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw(); }
        _left = true;
        throw new EtabsException(late);
    }

    /// <summary>
    /// The same model by its name: the program may write the folder another way than Windows gave it, and it may name
    /// its working copy beside the model instead of the model (ETABS 23 answers "name.$et" while "name.edb" is open),
    /// so the file type is left out. The scratch name is this run's alone (<see cref="ScratchName"/>).
    /// </summary>
    internal static bool SameName(string a, string b) =>
        string.Equals(Path.GetFileNameWithoutExtension(a.Replace('\\', '/').Split('/').Last()), Path.GetFileNameWithoutExtension(b.Replace('\\', '/').Split('/').Last()), StringComparison.OrdinalIgnoreCase)
        && a.Trim().Length > 0;

    /// <summary>The program's own model file type.</summary>
    /// <summary>
    /// Whether the program can open <paramref name="file"/> through its API's File.OpenFile. Its own model always;
    /// a text file (.e2k, .s2k, .f2k) too, except in SAFE on the .NET Framework (SAFE 20 and 21), which reads every
    /// file as a packed .fdb and stops on a message box.
    /// </summary>
    public static bool OpensThroughApi(CsiProgram program, string file, bool onFramework) =>
        !(program.Key == CsiProgram.Safe.Key && onFramework && !string.Equals(Path.GetExtension(file), ModelExtension(program), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the test can build <paramref name="file"/>'s model through the API in a blank model of a program that
    /// cannot open it (<see cref="OpensThroughApi"/>): a SAFE text file (.f2k), by <see cref="F2kRebuild"/>.
    /// </summary>
    /// <summary>
    /// The program said it opened the text file but holds none of its objects (<paramref name="held"/>: its areas and
    /// frames): SAFE 23.1 does so with the Modeler's .f2k (0.4.56, 2026-11-26).
    /// </summary>
    /// <summary>The file the program has open now ("" when it cannot say).</summary>
    private string OpenFileName()
    {
        try { return Convert.ToString(Ask(TimeSpan.FromMinutes(1), "name the open model", m => (object)(Convert.ToString(m.GetModelFilename(true)) ?? ""), $"{_program.Name} did not answer.")) ?? ""; }
        catch (Exception ex) when (ex is EtabsException or InvalidOperationException) { return ""; }
    }

    /// <summary>Whether the program holds <paramref name="file"/> (a text file opens under its own name or as the model it makes).</summary>
    private bool Holds(string file) => OpenFileName() is var open && (open.Length == 0 || SameName(open, file));

    /// <summary>The objects (frames and areas) an .e2k or .s2k holds; 0 for any other file.</summary>
    public static int TextObjectCount(CsiProgram program, string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".e2k" or ".$et" or ".s2k" or ".$2k")) return 0;
        try
        {
            var text = File.ReadAllText(file);
            if (ext is ".e2k" or ".$et")
            {
                var m = CsiProMax.E2k.E2kReader.Read(text);
                return m.Lines.Count + m.Areas.Count;
            }
            var t = CsiProMax.CsiTables.CsiTableText.Parse(text);
            return new[] { "CONNECTIVITY - FRAME", "CONNECTIVITY - AREA" }.Sum(x => t.TryGetValue(x, out var rows) ? rows.Count : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsiProMax.E2k.E2kFormatException) { return 0; }
    }

    /// <summary>The words of a failed open: the program's own message first, so the Run Report leads with it.</summary>
    public static string ImportRejected(string program, string file, int expected, IReadOnlyList<string> said) =>
        (said.Count > 0 ? $"{program} rejected {file}: {string.Join(" | ", said)}. " : $"{program} rejected {file}. ") +
        $"It answered that it opened it but holds none of its {expected} frames and areas (its blank default model), so nothing runs on it.";

    public static bool OpenedEmpty(string fileText, int held) => held == 0 && F2kRebuild.ObjectCount(fileText) > 0;

    /// <summary>Whether the program holds every beam, column, wall and floor of the file after its tables went in.</summary>
    internal static bool TablesBroughtAll(int held, int wanted) => held > 0 && held >= wanted;

    private static int Count(object model, string path)
    {
        var call = ApiCall.Invoke(model, path);
        return (int)ApiCall.Number(call["return"]);
    }

    public static bool BuildsThroughApi(CsiProgram program, string file) =>
        program.Key == CsiProgram.Safe.Key && string.Equals(Path.GetExtension(file), ".f2k", StringComparison.OrdinalIgnoreCase);

    public static string ModelExtension(CsiProgram program) => program.Key switch
    {
        "sap2000" => ".sdb",
        "safe" => ".fdb",
        _ => ".edb",
    };

    /// <summary>Closes the scratch window without saving. The engineer's own windows are not this object's to close.</summary>
    public string Close()
    {
        var name = _program.Name;
        if (_app is null) return "it was not started";
        if (_closed) return $"the scratch {name} window was already closed";
        // Kept open after a Run and closed by the engineer since (Settings: keep the program open after a Run).
        if (ProcessId != 0 && Gone(TimeSpan.Zero)) { _closed = true; return $"the {name} window had already been closed"; }
        if (_left) return Said($"the scratch {name} window stopped answering, so it was left as it is; close it yourself without saving");
        _closing = true;
        try
        {
            // Three minutes: in the 0.4.28 full test SAP2000 26 took over a minute to close its scratch window after the
            // whole test (the test had given up and named its process, which had ended by the time anyone looked).
            Ask(CloseTimeout, "close the window", _ => { _app.ApplicationExit(false); return null; },
                $"the scratch {name} window did not close within {CloseTimeout.TotalMinutes:0} minutes; close it yourself without saving");
            _closed = true;
            // The program said yes to closing: its process must go too, or it keeps a licence with no window to show for it.
            if (!Gone(TimeSpan.FromSeconds(30))) return Said($"the scratch {name} window was told to close, and its process did not end; end it yourself");
            int notices;
            List<string> titles;
            lock (_noticesClosed) { notices = _noticesClosed.Count; titles = [.. _noticesClosed.GroupBy(t => t).Select(g => g.Count() == 1 ? g.Key : $"{g.Key}, {g.Count()} times")]; }
            return $"the scratch {name} window was closed without saving" + (_closeAnswered ? " (it asked whether to really close, and the test answered Yes)" : "") + (_keptOpen > 0 ? $" (earlier it asked {_keptOpen} time{(_keptOpen == 1 ? "" : "s")} whether to close while the test was working in it, and the test answered No)" : "") + (notices > 0 ? $" (windows of it the test answered itself along the way: {string.Join("; ", titles)})" : "");
        }
        // Whatever the program or the link throws (a window that is no longer there, a call that faulted on another
        // thread): closing says what happened and never stops the test, whose report must still be written.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var why = (ex is AggregateException { InnerException: { } inner } ? inner : ex).Message;
            return Said(_left ? why : $"the scratch {name} window could not be closed ({why}); close it yourself without saving");
        }
    }

    /// <summary>What happened, with what became of a process that stayed: ended by the test, or left for the engineer.</summary>
    private string Said(string what)
    {
        var after = StillRunning();
        if (!after.Contains("the test ended it", StringComparison.Ordinal)) return what + after;
        _closed = true;
        return $"the scratch {_program.Name} window stopped answering" + after;
    }

    /// <summary>True when the program this object started has ended, waiting up to <paramref name="wait"/>; false when it was never started.</summary>
    public bool Ended(TimeSpan wait) => _app is not null && Gone(wait);

    /// <summary>True when the scratch process has ended (or could not be told apart, so nothing is known of it).</summary>
    private bool Gone(TimeSpan wait)
    {
        if (ProcessId == 0) return true;
        try { using var process = Process.GetProcessById(ProcessId); return process.WaitForExit((int)wait.TotalMilliseconds); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    /// <summary>
    /// A scratch process that is still there after its window was left or told to close: the test ends it (Ahmed's
    /// choice, 2026-11-25: "end only its own test copy"), so no licence stays held by a program with no window. Only
    /// the process this object started, and only when <see cref="MayEnd"/> says it can be no one else's. Otherwise
    /// the engineer is told its process id and how to end it.
    /// </summary>
    private string StillRunning()
    {
        if (Gone(TimeSpan.Zero)) return "";
        var hasWindow = false;
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            var titles = ScratchDialogs.Of(ProcessId).Select(w => w.Title).ToList();
            hasWindow = titles.Count > 0;
            if (string.Equals(process.ProcessName, _program.ProcessName, StringComparison.OrdinalIgnoreCase) && MayEnd(process.StartTime, _startedAt, titles))
            {
                process.Kill();
                if (process.WaitForExit(15000))
                    return $". Its process (process id {ProcessId}) no longer answered, so the test ended it: this scratch {_program.Name} was closed without saving and holds no licence now";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* said below */ }
        return Gone(TimeSpan.Zero) ? "" : LeftRunning(_program.Name, _program.ProcessName, ProcessId, hasWindow);
    }

    /// <summary>
    /// True when a process can only be the test's own scratch program: it started after the test asked for it, and
    /// every window it shows is the scratch model's (or it shows none). A window that names anything else keeps it alive.
    /// </summary>
    public static bool MayEnd(DateTime processStarted, DateTime testAsked, IEnumerable<string> titles) =>
        testAsked != default && processStarted >= testAsked.AddSeconds(-2)
        && titles.All(t => t.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the windows of a process show it is a test copy an earlier run left open: one of them names a scratch
    /// model ("cpm-field-test-" and its six characters), and every other one is a window the test answers itself (the
    /// analysis notice, the story picker, the import log). A window that names anything else keeps the process alive.
    /// </summary>
    public static bool IsLeftCopy(IEnumerable<string> titles)
    {
        // Windows the test answers itself also count (ETABS's "Log", the file picker, its own message boxes): the 0.4.58
        // run left two ETABS on their "Log" window and the 0.4.59 run did not end them.
        static bool Scratch(string t) => t.Contains("cpm-field-test-", StringComparison.OrdinalIgnoreCase);
        static bool Answered(string t) => ScratchDialogs.MayClose(t) || ScratchDialogs.IsStoryPicker(t) || ScratchDialogs.IsImportLog(t)
            || ScratchDialogs.IsLineLog(t) || ScratchDialogs.IsOpenFileBox(t) || ScratchDialogs.IsAnalysisMessages(t)
            || t.Trim() is "ETABS" or "SAP2000" or "SAFE"; // ETABS itself: the program's own message boxes
        var all = titles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return all.Any(Scratch) && all.All(t => Scratch(t) || Answered(t));
    }

    /// <summary>
    /// Ends the test copies an earlier run left open (a run that gave up on a window, as the 0.4.10 run did on ETABS 23
    /// with the story picker showing): they hold a licence, and they keep CSI ProMax out of that program's Tools menu
    /// ("ETABS 23 is open"). Only a process <see cref="IsLeftCopy"/> names; it is ended without saving (Ahmed's choice,
    /// 2026-11-25: the test may end "only its own test copy"). Returns one line for each.
    /// </summary>
    /// <summary>The scratch processes the test started, by process id and start time, so a later run knows its own.</summary>
    internal static string StartedFile { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "Field test", "scratch processes.txt");

    private static void Remember(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Directory.CreateDirectory(Path.GetDirectoryName(StartedFile)!);
            File.AppendAllText(StartedFile, $"{processId}|{process.StartTime.ToUniversalTime().Ticks}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException) { /* only a memory */ }
    }

    /// <summary>Whether this process is one the test started (same id, same start time), from <see cref="StartedFile"/>.</summary>
    internal static bool Started(int processId, DateTime startTime, IEnumerable<string> lines) =>
        lines.Select(l => l.Split('|')).Any(p => p.Length == 2 && p[0] == processId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && long.TryParse(p[1], out var ticks) && Math.Abs(ticks - startTime.ToUniversalTime().Ticks) < TimeSpan.TicksPerSecond);

    /// <summary>
    /// A process the test itself started (<see cref="Started"/>) that shows only its scratch model, the Modeler's file it
    /// was opening, or windows the test answers itself: a licence message keeps it alive.
    /// </summary>
    public static bool IsOwnLeftCopy(IEnumerable<string> titles) =>
        titles.Any(t => t.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase)) && titles.Where(t => !string.IsNullOrWhiteSpace(t)).All(t => !t.Contains("licen", StringComparison.OrdinalIgnoreCase)
            && (t.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase) || ScratchDialogs.MayClose(t) || ScratchDialogs.IsStoryPicker(t) || ScratchDialogs.IsImportLog(t)
                || ScratchDialogs.IsLineLog(t) || ScratchDialogs.IsOpenFileBox(t) || ScratchDialogs.IsAnalysisMessages(t) || t.Trim() is "ETABS" or "SAP2000" or "SAFE" // ETABS itself
                || t.TrimStart().StartsWith("ETABS", StringComparison.OrdinalIgnoreCase) || t.TrimStart().StartsWith("SAP2000", StringComparison.OrdinalIgnoreCase) || t.TrimStart().StartsWith("SAFE", StringComparison.OrdinalIgnoreCase))); // ETABS itself

    /// <summary>
    /// The CSI processes started through the API ("...ETABSv1" /A on their command line) whose starter is gone, from
    /// Windows' own process list: an API-started program whose caller ended can serve no one. The 0.4.57 and 0.4.58 runs
    /// left such copies on "(Untitled)" windows (on ETABS's Log and SAP2000's file picker) that no title told apart.
    /// </summary>
    internal static HashSet<int> OrphanedApiCopies()
    {
        var found = new HashSet<int>();
        if (!OperatingSystem.IsWindows()) return found;
        try
        {
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         "Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'ETABS.exe','SAP2000.exe','SAFE.exe' } | ForEach-Object { \"$($_.ProcessId)|$($_.ParentProcessId)|$($_.CommandLine)\" }" }) // ETABS itself
                info.ArgumentList.Add(a);
            using var ps = Process.Start(info);
            if (ps is null) return found;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(15000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (OrphanLine(line, ParentAlive) is { } pid) found.Add(pid);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return found;
    }

    private static bool ParentAlive(int parent)
    {
        try { using var p = Process.GetProcessById(parent); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The process id of a "pid|parent|command line" row when it was started through the API and its parent is gone.</summary>
    internal static int? OrphanLine(string line, Func<int, bool> parentAlive)
    {
        var parts = line.Split('|', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var parent)) return null;
        var command = parts[2];
        var api = System.Text.RegularExpressions.Regex.IsMatch(command, @"""(ETABS|SAP2000|SAFE|CSI)v1""\s+/A\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase); // ETABS itself
        return api && !parentAlive(parent) ? pid : null;
    }

    /// <summary>The windows of an orphaned API copy that may be ended: an empty or scratch model, and windows the test answers itself.</summary>
    public static bool IsOrphanedCopy(IEnumerable<string> titles) =>
        titles.Where(t => !string.IsNullOrWhiteSpace(t)).All(t => !t.Contains("licen", StringComparison.OrdinalIgnoreCase)
            && (t.Contains("(Untitled)", StringComparison.OrdinalIgnoreCase) || t.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase)
                || ScratchDialogs.MayClose(t) || ScratchDialogs.IsStoryPicker(t) || ScratchDialogs.IsImportLog(t) || ScratchDialogs.IsLineLog(t)
                || ScratchDialogs.IsOpenFileBox(t) || ScratchDialogs.IsAnalysisMessages(t) || t.Trim() is "ETABS" or "SAP2000" or "SAFE" // ETABS itself
                || t.Trim().StartsWith("Cancel ", StringComparison.OrdinalIgnoreCase)));

    public static List<string> EndLeftCopies()
    {
        var said = new List<string>();
        if (!OperatingSystem.IsWindows()) return said;
        var orphans = OrphanedApiCopies();
        string[] started;
        try { started = File.Exists(StartedFile) ? File.ReadAllLines(StartedFile) : []; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { started = []; }
        foreach (var program in CsiProgram.All)
        {
            Process[] running;
            try { running = Process.GetProcessesByName(program.ProcessName); }
            catch (InvalidOperationException) { continue; }
            foreach (var process in running)
                using (process)
                    try
                    {
                        var titles = ScratchDialogs.Of(process.Id).Select(w => w.Title).ToList();
                        var own = (Started(process.Id, process.StartTime, started) && IsOwnLeftCopy(titles)) || (orphans.Contains(process.Id) && IsOrphanedCopy(titles));
                        if (!own && !IsLeftCopy(titles)) continue;
                        var name = titles.FirstOrDefault(t => t.Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase)) ?? $"a scratch {program.Name} this test started";
                        process.Kill();
                        said.Add(process.WaitForExit(15000) ? $"'{name}' (process id {process.Id}) was closed without saving" : $"'{name}' (process id {process.Id}) was told to end and is still there");
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* not ours to end, or already gone */ }
        }
        return said;
    }

    public static string LeftRunning(string name, string processName, int processId, bool hasWindow) =>
        $". Its process is still running ({processName}.exe, process id {processId}, {(hasWindow ? "with a window" : "with no window on the screen")}) and holds a {name} licence until it ends: " +
        $"{(hasWindow ? "close that window without saving, or " : "")}end the {processName}.exe with process id {processId} in Task Manager (Details tab). It is the test's own scratch model; nothing of yours is in it";

    /// <summary>
    /// Takes the program's window off the screen (its API's Hide), for CSI ProMax Engine's runs, which use the program
    /// as a solver in the background (Ahmed 2026-11-26). False when the program does not hide; the run goes on either way.
    /// </summary>
    public bool Hide()
    {
        if (_app is null || _closed || _left) return false;
        try
        {
            Ask(TimeSpan.FromSeconds(30), "hide the window", _ => { _app.Hide(); return null; }, $"the {_program.Name} window did not hide within 30 seconds");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>
    /// Hands the program's window, with the model it ran, to the engineer: shown again and no longer answered by
    /// CSI ProMax (its watcher would answer No to the engineer's own "close?" question). For Settings &gt; Runs, keep the
    /// program open after a Run (Ahmed 2026-11-27). False when the window is not there to show.
    /// </summary>
    public bool Reveal()
    {
        if (_app is null || _closed || _left) return false;
        try { _watch?.Dispose(); _watch = null; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try
        {
            Ask(TimeSpan.FromSeconds(30), "show the window", _ => { _app.Unhide(); return null; }, $"the {_program.Name} window did not show within 30 seconds");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>
    /// Puts the hidden program's window back on the screen while the run goes on (its API's Unhide), for the Run
    /// Monitor's Show program window: a box the run does not answer itself (a licence message) is then there to read and
    /// answer. The run keeps answering its own boxes. False when it cannot.
    /// </summary>
    public bool Show()
    {
        if (_app is null || _closed || _left) return false;
        try
        {
            Ask(TimeSpan.FromSeconds(30), "show the window", _ => { _app.Unhide(); return null; }, $"the {_program.Name} window did not come back within 30 seconds");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>The boxes of the program the run answered itself so far, in order (the Run Monitor shows them as they come).</summary>
    public IReadOnlyList<string> Answered() { lock (_noticesClosed) return _noticesClosed.ToList(); }

    /// <summary>
    /// The Run Monitor's Cancel: ends this object's own program process at once, without saving (the run's copy of the
    /// model only; the engineer's model and windows are never this object's). Says what happened.
    /// </summary>
    public string Kill()
    {
        if (ProcessId == 0) return $"the {_program.Name} process could not be told apart, so it was not ended";
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (!string.Equals(process.ProcessName, _program.ProcessName, StringComparison.OrdinalIgnoreCase) || (_startedAt != default && process.StartTime < _startedAt.AddSeconds(-2)))
                return $"process {ProcessId} is not the run's own {_program.Name}, so it was not ended";
            _left = true; _closed = true;
            process.Kill();
            return process.WaitForExit(20000) ? $"{_program.Name} was ended without saving; its licence is free" : $"{_program.Name} was told to end and has not yet (process id {ProcessId})";
        }
        catch (ArgumentException) { _closed = true; return $"{_program.Name} had already ended"; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return $"{_program.Name} could not be ended ({ex.Message})"; }
    }

    /// <summary>True while the program's process is still running.</summary>
    public bool Running => ProcessId != 0 && !Gone(TimeSpan.Zero);

    public void Dispose()
    {
        // Letting go never throws: the report is written after it.
        try { if (_app is not null && !_closed && !_left) Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { _watch?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { PutBackPlugIns(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { _com?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { _pipe?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}

/// <summary>
/// A scratch window must leave the program's plug-in lists as it found them. ETABS 23 and SAP2000 26, started through
/// the API, rewrite their settings file with an empty [PlugIn] section (NumberPlugIns=0): the field test of 0.4.14 on
/// Ahmed's PC emptied SAP2000 26's list as its scratch window started (19:29:42), and a scratch ETABS 23 closed with
/// ApplicationExit emptied ETABS 23's (19:18:05), so Tools &gt; CSI ProMax was gone from both. The lists are read before
/// the window starts and put back, the [PlugIn] section only, once it has closed.
/// </summary>
public static class KeptPlugIns
{
    /// <summary>Each settings file of the program (every version on the PC) with its [PlugIn] section as it is now.</summary>
    public static Dictionary<string, string> Take(CsiProgram program, string? settingsRoot = null)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = settingsRoot ?? ToolsMenu.SettingsRoot;
        try
        {
            if (!Directory.Exists(root)) return kept;
            foreach (var folder in Directory.GetDirectories(root, program.Name + " *"))
                foreach (var ini in Directory.GetFiles(folder, "*.ini"))
                    if (FieldTest.PlugInSection(File.ReadAllText(ini, System.Text.Encoding.Latin1)) is { } section)
                        kept[ini] = section;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return kept;
    }

    /// <summary>Puts back each [PlugIn] section that changed since <see cref="Take"/>. Returns the files put back.</summary>
    public static List<string> PutBack(IReadOnlyDictionary<string, string> kept)
    {
        var back = new List<string>();
        foreach (var (ini, section) in kept)
            try
            {
                if (!File.Exists(ini)) continue;
                var text = File.ReadAllText(ini, System.Text.Encoding.Latin1);
                if (FieldTest.PlugInSection(text) is not { } now || now == section) continue;
                if (Replace(text, section) is not { } fixedText) continue;
                File.WriteAllText(ini, fixedText, System.Text.Encoding.Latin1);
                back.Add(ini);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return back;
    }

    /// <summary>The settings text with its [PlugIn] section replaced by <paramref name="section"/>, in the file's own line ends.</summary>
    internal static string? Replace(string text, string section)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(l => l.Trim().Equals("[PlugIn]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        // The section ends with its last line of text: blank lines after it stay where they were.
        while (end - 1 > start && lines[end - 1].Trim().Length == 0) end--;
        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, section.Split('\n'));
        var joined = string.Join("\n", lines);
        return crlf ? joined.Replace("\n", "\r\n") : joined;
    }
}
