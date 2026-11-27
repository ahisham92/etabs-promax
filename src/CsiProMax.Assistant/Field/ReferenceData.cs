using System.Diagnostics;
using System.Text;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant;

/// <summary>
/// The "needed things" a full field test gathers from each installed program while its scratch window is open, so
/// that nobody has to be asked to export them by hand (Ahmed, 2026-11-26: "everything you need actually from the any
/// program you can include it as part of the bugs ... but don't report it as bugs report it as needed things").
///
/// It is not a test: nothing here is a step, passes or fails. Each item writes small files into
/// "Reference data/&lt;program&gt;" in the test's work folder, which the zip to send takes with it, and one line into
/// gathered.md there (what was written, or why it could not be). It reads only: the scratch model, the program's own
/// lists, and copies of files of the program's installation. It never opens, saves or changes an original file.
///
/// To have the test bring something more, add an <see cref="Item"/> to <see cref="Items"/>: its name, the programs
/// it is for, and what it writes. An item that throws is noted and the others still run.
/// </summary>
public static class ReferenceData
{
    public const string FolderName = "Reference data";

    /// <summary>What one item works with: the program, the link to its scratch window, and where to write.</summary>
    public sealed class Gather(CsiProgram program, ICsiLink link, string folder, string modelPath = "", int processId = 0)
    {
        /// <summary>The scratch window's process (0 when not known): whose message windows an item may answer.</summary>
        public int ProcessId { get; } = processId;

        /// <summary>The scratch model's own file in the test's folder ("" when not known).</summary>
        public string ModelPath { get; } = modelPath;

        public CsiProgram Program { get; } = program;
        public ICsiLink Link { get; } = link;
        public string Folder { get; } = folder;

        /// <summary>Writes one file of this program's reference data; gives its name back.</summary>
        public string Write(string name, string text)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, name), text, new UTF8Encoding(false));
            return name;
        }

        /// <summary>The folder the program is installed in ("" when it cannot be told).</summary>
        public string InstallFolder()
        {
            try { return Path.GetDirectoryName(EtabsApi.FindApiFile(Program)) ?? ""; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
        }
    }

    /// <param name="Name">What it gathers, in a few words.</param>
    /// <param name="For">The programs it is for; null: every program.</param>
    /// <param name="Run">Writes its files and says in one line what it wrote.</param>
    public sealed record Item(string Name, Func<CsiProgram, bool>? For, Func<Gather, string> Run);

    /// <summary>Everything gathered. Any thread adds what it needs here.</summary>
    public static List<Item> Items { get; } =
    [
        new("the program, its version and its files", null, ProgramFacts),
        new("every table with its fields", null, TablesAndFields),
        new("the Verification examples the program came with", null, VerificationExamples),
        new("every table of the scratch model that has data", null, ScratchTables),
        new("the status of every load case after the analysis", null, CaseStatus),
        // Before the text copy, so the program's own syntax for them is in it (their .e2k words are not known yet).
        new("a slab line release and a slab rebar object, added to the scratch model through the program's tables", p => p.Key is "etabs" or "safe", SlabExtras),
        new("the program's own text copy of the scratch model", null, OwnTextCopy),
        new("the files of the scratch folder", null, ScratchFiles),
        new("what the program holds of the Modeler's file", null, Holds),
        new("the calls the program's API has", null, ApiCalls),
        new("SAP2000's own values for an area edge release and a joint panel zone", p => p.Key == "sap2000", EdgeReleaseAndPanelZone),
        // Opens two small files of the Modeler's in the scratch window, then the scratch model again.
        new("whether the program imports the Modeler's files that carry the model after END TABLE DATA", p => p.Key is "sap2000" or "safe", ImportsCarriedModel),
        new("the program's own forms, pictured beside the Engine's (Modeler window\\Form pictures\\Engine forms side by side.html)", null, ReferenceForms.Gather),
        // Last: it replaces the scratch model in the scratch window with a new blank one (never saved).
        new("the program's defaults, from a new blank model", null, BlankDefaults),
    ];

    /// <summary>Longest one item may read tables for; what is left is said.</summary>
    internal static TimeSpan TableLimit { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs every item for one program and writes gathered.md. Never throws and adds no step: what could not be
    /// gathered is a line in gathered.md, not a failure of the test. Gives the lines back.
    /// </summary>
    public static IReadOnlyList<string> Run(string workFolder, CsiProgram program, ICsiLink link, string modelPath = "", int processId = 0)
    {
        var lines = new List<string>();
        try
        {
            var gather = new Gather(program, link, Path.Combine(workFolder, FolderName, program.Name), modelPath, processId);
            foreach (var item in Items.ToList())
            {
                if (item.For is { } only && !only(program)) continue;
                try { lines.Add($"- {item.Name}: {item.Run(gather)}"); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"- {item.Name}: not gathered ({ex.GetType().Name}: {Short(ex.Message)})"); }
            }
            gather.Write("gathered.md", $"# Reference data from {program.Name}\n\nNeeded things the field test gathered by itself. These are not test results and not bugs.\n\n{string.Join("\n", lines)}\n");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"- nothing could be written ({ex.GetType().Name}: {Short(ex.Message)})"); }
        return lines;
    }

    /// <summary>The report's section: what was gathered for each program, read from the work folder. "" when nothing was.</summary>
    public static string Section(string? workFolder)
    {
        if (workFolder is null) return "";
        var root = Path.Combine(workFolder, FolderName);
        if (!Directory.Exists(root)) return "";
        var sb = new StringBuilder();
        sb.AppendLine("## Reference data (needed things, not bugs)").AppendLine();
        sb.AppendLine("The test gathered these from each program by itself, so nobody has to export them by hand. They are in the zip under files/Reference data. Nothing here is a test result.").AppendLine();
        foreach (var folder in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"{Path.GetFileName(folder)}:").AppendLine();
            var said = Path.Combine(folder, "gathered.md");
            var lines = File.Exists(said) ? File.ReadAllLines(said).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList() : [];
            foreach (var line in lines) sb.AppendLine(line);
            if (lines.Count == 0) sb.AppendLine("- nothing was gathered");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Short(string text) => text.Length <= 300 ? text : text[..300] + "...";

    private static string Csv(params string?[] cells) => string.Join(",", cells.Select(c => "\"" + (c ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\""));

    // ---- The items ---------------------------------------------------------------------------------------------------

    private static string ProgramFacts(Gather g)
    {
        var info = g.Link.Info();
        var sb = new StringBuilder();
        sb.AppendLine($"program: {g.Program.Name}");
        sb.AppendLine($"version: {info.Version}");
        sb.AppendLine($"units of the scratch model: {info.Force}, {info.Length}, {info.Temperature}");
        var folder = g.InstallFolder();
        sb.AppendLine($"installed in: {(folder.Length == 0 ? "not found" : folder)}");
        try
        {
            var api = EtabsApi.FindApiFile(g.Program);
            sb.AppendLine($"API file: {api} {(File.Exists(api) ? FileVersionInfo.GetVersionInfo(api).FileVersion : "(not found)")}");
            if (File.Exists(api)) sb.AppendLine($"API runs on: {(EtabsApi.RunsOnFramework(api) ? "the .NET Framework (reached through the plug-in's pipe or Windows PowerShell)" : ".NET (called directly)")}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine("API file: not read (" + ex.GetType().Name + ")"); }
        sb.AppendLine($"reached through: {(g.Link is ComEtabs com ? com.Via : g.Link is Sap2000.SapLink { Inner: ComEtabs inner } ? inner.Via : g.Link.GetType().Name)}");
        if (folder.Length > 0 && Directory.Exists(folder))
        {
            // The program's own files with their versions: which build this is, without anything of its licence.
            foreach (var file in Directory.EnumerateFiles(folder, "*.*").Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".dll")
                         .Where(f => Path.GetFileName(f).StartsWith(g.Program.ProcessName, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("CSi", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(40))
                try { sb.AppendLine($"file: {Path.GetFileName(file)} {FileVersionInfo.GetVersionInfo(file).FileVersion}"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next file */ }
        }
        return g.Write("program.txt", sb.ToString());
    }

    private static string TablesAndFields(Gather g)
    {
        var tables = g.Link.Tables();
        var list = new StringBuilder(Csv("table key", "table name", "empty in the scratch model", "import type")).AppendLine();
        foreach (var t in tables) list.AppendLine(Csv(t.Key, t.Name, t.Empty ? "yes" : "no", t.Import.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        g.Write("tables.csv", list.ToString());

        var fields = new StringBuilder(Csv("table key", "field key", "field name", "units", "importable", "description")).AppendLine();
        var watch = Stopwatch.StartNew();
        int read = 0, refused = 0;
        foreach (var t in tables)
        {
            if (watch.Elapsed > TableLimit) break;
            try
            {
                var rows = (List<string>)g.Link.Raw("read the fields of " + t.Key, m =>
                {
                    int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                    if (Convert.ToInt32(m.DatabaseTables.GetAllFieldsInTable(t.Key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable)) != 0) return new List<string>();
                    keys ??= [];
                    return Enumerable.Range(0, keys.Length).Select(i => Csv(t.Key, keys[i], names?.ElementAtOrDefault(i), units?.ElementAtOrDefault(i),
                        importable is { } can && i < can.Length ? (can[i] ? "yes" : "no") : "", about?.ElementAtOrDefault(i))).ToList();
                })!;
                if (rows.Count == 0) { refused++; continue; }
                foreach (var row in rows) fields.AppendLine(row);
                read++;
            }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { refused++; }
        }
        g.Write("table fields.csv", fields.ToString());
        var left = tables.Count - read - refused;
        return $"tables.csv ({tables.Count} tables), table fields.csv (the fields of {read}" + (refused > 0 ? $"; {refused} gave none" : "") + (left > 0 ? $"; {left} not read within {TableLimit.TotalMinutes:0} minutes" : "") + ")";
    }

    /// <summary>The text model files worth a copy, and the most that is copied.</summary>
    private static readonly string[] TextModels = [".e2k", ".$et", ".s2k", ".$2k", ".f2k", ".$fk"];
    internal const long MostCopied = 8 * 1024 * 1024, MostOne = 2 * 1024 * 1024;

    private static string VerificationExamples(Gather g)
    {
        var install = g.InstallFolder();
        var folder = install.Length == 0 ? "" : Path.Combine(install, "Verification");
        if (folder.Length == 0 || !Directory.Exists(folder)) return $"no Verification folder beside {g.Program.Name} ({(install.Length == 0 ? "its folder was not found" : install)})";
        return CopyExamples(g, folder);
    }

    /// <summary>
    /// Lists every file of the examples folder, and copies its text model files (the originals are only read): the
    /// slab, post-tensioning and punching ones first, then the rest while there is room.
    /// </summary>
    internal static string CopyExamples(Gather g, string folder)
    {
        var all = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var list = new StringBuilder(Csv("file", "bytes")).AppendLine();
        foreach (var file in all)
            try { list.AppendLine(Csv(Path.GetRelativePath(folder, file), new FileInfo(file).Length.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next file */ }
        g.Write("verification files.csv", list.ToString());

        bool First(string f) => new[] { "PT", "SL", "PN", "Slab", "Punch", "Tendon" }.Any(w => Path.GetFileName(f).Contains(w, StringComparison.OrdinalIgnoreCase));
        var texts = all.Where(f => TextModels.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).OrderBy(f => First(f) ? 0 : 1).ToList();
        long total = 0; var copied = 0;
        var target = Path.Combine(g.Folder, "Verification");
        foreach (var file in texts)
        {
            try
            {
                var size = new FileInfo(file).Length;
                if (size > MostOne || total + size > MostCopied) continue;
                var to = Path.Combine(target, Path.GetRelativePath(folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
                total += size; copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next file */ }
        }
        // The slab, post-tensioning and punching example models themselves (a few, small): what the program ships for
        // tendons, strips and punching, for whoever has the program to open a copy.
        var models = all.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".edb" or ".sdb" or ".fdb" && First(f)
                                    && new[] { "PT-SL", "RC-PN", "PT_SL", "RC_PN" }.Any(w => Path.GetFileName(f).Contains(w, StringComparison.OrdinalIgnoreCase))).Take(4).ToList();
        var binaries = 0;
        foreach (var file in models)
        {
            try
            {
                var size = new FileInfo(file).Length;
                if (size > 6 * 1024 * 1024 || total + size > MostCopied * 2) continue;
                var to = Path.Combine(target, Path.GetRelativePath(folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
                total += size; binaries++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next file */ }
        }
        return $"verification files.csv ({all.Count} files listed), {copied} of {texts.Count} text model files and {binaries} slab and punching example models copied into Verification ({total / 1024} KB)";
    }

    /// <summary>Most rows of one table, and most bytes of all the tables, that the dump writes.</summary>
    internal const int MostRows = 5000;
    internal const long MostTables = 30 * 1024 * 1024;

    private static string SafeName(string key) => new string(key.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')' ? c : '_').ToArray()).Trim();

    /// <summary>Every table the scratch model has data in, each as a CSV with the program's own field keys: definitions, assignments, preferences, overwrites, cases, options, results, design.</summary>
    private static string ScratchTables(Gather g)
    {
        var tables = g.Link.Tables().Where(t => !t.Empty).ToList();
        var watch = Stopwatch.StartNew();
        long bytes = 0; int written = 0, refused = 0, cut = 0;
        var folder = Path.Combine(g.Folder, "tables");
        Directory.CreateDirectory(folder);
        foreach (var t in tables)
        {
            if (watch.Elapsed > TableLimit || bytes > MostTables) break;
            try
            {
                var data = g.Link.ReadTable(t.Key, null, null);
                if (data.Fields.Count == 0) { refused++; continue; }
                var sb = new StringBuilder(Csv([.. data.Fields])).AppendLine();
                foreach (var row in data.Rows.Take(MostRows)) sb.AppendLine(Csv(row));
                if (data.Rows.Count > MostRows) { sb.AppendLine(Csv($"... {data.Rows.Count - MostRows} more rows not written")); cut++; }
                var text = sb.ToString();
                File.WriteAllText(Path.Combine(folder, SafeName(t.Key) + ".csv"), text, new UTF8Encoding(false));
                bytes += text.Length; written++;
            }
            catch (Exception ex) when (ex is EtabsException or InvalidOperationException or InvalidCastException or IOException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { refused++; }
        }
        var left = tables.Count - written - refused;
        return $"tables/*.csv ({written} of {tables.Count} tables with data" + (refused > 0 ? $"; {refused} gave nothing" : "") + (cut > 0 ? $"; {cut} cut at {MostRows} rows" : "") + (left > 0 ? "; " + left + " not read (time or size)" : "") + ")";
    }

    private static string CaseStatus(Gather g)
    {
        var lines = (List<string>)g.Link.Raw("read the status of the load cases", m =>
        {
            int n = 0; string[] names = []; int[] status = [];
            var answer = Convert.ToInt32(m.Analyze.GetCaseStatus(ref n, ref names, ref status));
            var said = new List<string> { Csv("case", "status (1 not run, 2 could not start, 3 not finished, 4 finished)"), };
            if (answer != 0) { said.Add(Csv("GetCaseStatus answered " + answer, "")); return said; }
            for (var i = 0; i < (names?.Length ?? 0); i++) said.Add(Csv(names![i], status is { } st && i < st.Length ? st[i].ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
            return said;
        })!;
        return g.Write("case status.csv", string.Join("\n", lines) + "\n") + $" ({lines.Count - 1} cases)";
    }

    /// <summary>
    /// The scratch model saved once more under its own name, so the program writes its text copy beside it (.$et, .$2k,
    /// .$fk): the program's own syntax for everything the test put into the model. Only ever the test's own scratch file.
    /// </summary>
    private static string OwnTextCopy(Gather g)
    {
        var file = g.ModelPath;
        if (file.Length == 0 || !Path.GetFileName(file).Contains("cpm-field-test", StringComparison.OrdinalIgnoreCase)) return "not written: the scratch model's own file is not known, and no other file is ever saved";
        var answer = Convert.ToInt32(g.Link.Raw("save the scratch model", m => m.File.Save(file)));
        var folder = Path.GetDirectoryName(file) ?? "";
        var stem = Path.GetFileNameWithoutExtension(file);
        var texts = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, stem + ".*").Where(f => TextModels.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList() : [];
        return $"the save answered {answer}; text copies beside the scratch model: {(texts.Count == 0 ? "none" : string.Join(", ", texts))}";
    }

    /// <summary>
    /// The Modeler writes the whole model after END TABLE DATA as "$ " lines (EmbeddedModel), where SAP2000 and SAFE
    /// are taken to stop reading. This opens the same small model twice, with those lines and without, in the scratch
    /// window, and says what each import answered and holds, with any log the program wrote beside the file. Never
    /// saves; the scratch model is opened again after.
    /// </summary>
    private static string ImportsCarriedModel(Gather g)
    {
        // An import can wait on its log ("Access Database Import Log") or a message box: only with the scratch window's
        // own process known can those be answered while the import runs.
        if (g.ProcessId == 0 || !OperatingSystem.IsWindows()) return "not tried: the scratch window's process is not known, and an import may wait for a person";
        var safe = g.Program.Key == "safe";
        var m = CsiProMax.Model.Templates.ConcreteFrame("Carried", [6, 6], [5], 1, 3.2);
        m.Target = safe ? "safe" : "sap2000";
        var text = safe ? CsiProMax.F2k.F2kWriter.Write(m).Text : CsiProMax.S2k.S2kWriter.Write(m).Text;
        var ext = safe ? ".f2k" : ".s2k";
        var dir = Path.Combine(g.Folder, "import check");
        Directory.CreateDirectory(dir);
        var with = Path.Combine(dir, "with model lines" + ext);
        var without = Path.Combine(dir, "without model lines" + ext);
        File.WriteAllText(with, CsiProMax.CsiTables.EmbeddedModel.Attach(text, m), new UTF8Encoding(false));
        File.WriteAllText(without, text, new UTF8Encoding(false));
        var c = System.Globalization.CultureInfo.InvariantCulture;
        string Count(Func<dynamic, object> read) { try { return Convert.ToInt32(g.Link.Raw("count", read)).ToString(c); } catch (Exception ex) when (ex is not OutOfMemoryException) { return "?"; } }
        string Try(string file)
        {
            var before = Directory.EnumerateFiles(dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string answer;
            // The program's windows are answered while the import runs, and what they said is kept: the import log's
            // errors and any message SAFE or SAP2000 shows (the same answers the scratch window gets in its steps).
            var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var stop = new CancellationTokenSource();
            var answering = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try { foreach (var w in ScratchDialogs.CloseNotices(g.ProcessId, g.Program)) said.Enqueue(w); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                    try { await Task.Delay(500, stop.Token); } catch (OperationCanceledException) { }
                }
            });
            try { answer = Convert.ToInt32(g.Link.Raw("import " + Path.GetFileName(file), x => x.File.OpenFile(file))).ToString(c); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { answer = ex.GetType().Name + " " + Short(ex.Message); }
            finally
            {
                // A last look for a window shown just as the import came back.
                try { foreach (var w in ScratchDialogs.CloseNotices(g.ProcessId, g.Program)) said.Enqueue(w); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                stop.Cancel();
                try { answering.Wait(2000); } catch (AggregateException) { }
            }
            if (!said.IsEmpty) answer += "; answered: " + Short(string.Join(" / ", said));
            var held = $"frames {Count(x => x.FrameObj.Count())}, areas {Count(x => x.AreaObj.Count())}, points {Count(x => x.PointObj.Count())}";
            // What the program wrote beside the file while importing it (its import log and the like).
            var logs = Directory.EnumerateFiles(dir).Where(f => !before.Contains(f) && Path.GetExtension(f).ToUpperInvariant() is ".LOG" or ".TXT" or ".$2K" or ".$FK" or ".OUT").ToList();
            var log = string.Join(" / ", logs.Select(f => { try { var t = File.ReadAllText(f); return Path.GetFileName(f) + ": " + Short(t.Replace("\r", " ").Replace("\n", " ")); } catch (IOException) { return Path.GetFileName(f); } }));
            return $"{Path.GetFileName(file)}: OpenFile answered {answer}; {held}{(log.Length > 0 ? "; log " + log : "")}";
        }
        var a = Try(with);
        var b = Try(without);
        if (g.ModelPath.Length > 0 && File.Exists(g.ModelPath))
            try { g.Link.Raw("open the scratch model again", x => x.File.OpenFile(g.ModelPath)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        g.Write("import check.txt", a + Environment.NewLine + b + Environment.NewLine);
        return a + " | " + b + $" | the Modeler's model: {m.LineAssigns.Count} frames, {m.AreaAssigns.Count} areas";
    }

    /// <summary>
    /// SAP2000: an edge release on one area edge and a panel zone on one joint of the scratch model, set by the API, and
    /// the rows SAP2000 then shows in "Area Edge Release Assignments" and "Joint Panel Zone Assignments": its own field
    /// values, for the Modeler to carry those two assignments. Only on the test's own scratch model, never saved after.
    /// </summary>
    private static string EdgeReleaseAndPanelZone(Gather g)
    {
        var said = new List<string>();
        string First(string kind) => (string)g.Link.Raw($"list the {kind}s", m =>
        {
            int n = 0; string[] names = [];
            var answer = kind == "area" ? m.AreaObj.GetNameList(ref n, ref names) : m.PointObj.GetNameList(ref n, ref names);
            return n > 0 && names is { Length: > 0 } ? names[0] : "";
        });
        try
        {
            var area = First("area");
            if (area.Length == 0) said.Add("no area");
            else
            {
                // SAP2000 26's API: SetEdgeRelease is not in every version; each try is said with its answer.
                foreach (var (what, call) in new (string, Func<dynamic, object>)[]
                {
                    ("AreaObj.SetEdgeRelease", m => { var r = new bool[] { true, false, false, false }; return m.AreaObj.SetEdgeRelease(area, 1, ref r, 0); }),
                })
                    try { said.Add($"{what}: {g.Link.Raw(what, call)}"); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { said.Add($"{what}: {ex.GetType().Name} {ex.Message}"); }
            }
            var joint = First("point");
            if (joint.Length == 0) said.Add("no joint");
            else
                try { said.Add("PointObj.SetPanelZone: " + g.Link.Raw("set a panel zone", m => m.PointObj.SetPanelZone(joint, 1, 0.0, 0.0, 0.0, "", 1, 1, 0.0, 0))); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { said.Add($"PointObj.SetPanelZone: {ex.GetType().Name} {ex.Message}"); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { said.Add($"{ex.GetType().Name} {ex.Message}"); }
        var sb = new StringBuilder();
        foreach (var key in new[] { "Area Edge Release Assignments", "Joint Panel Zone Assignments" })
        {
            sb.AppendLine(Csv("table", key));
            try
            {
                var t = g.Link.ReadTable(key, null, null);
                sb.AppendLine(Csv([.. t.Fields]));
                foreach (var r in t.Rows.Take(50)) sb.AppendLine(Csv([.. r]));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine(Csv("not read", ex.Message)); }
        }
        return g.Write("sap2000 edge release and panel zone.csv", sb.ToString()) + " (" + string.Join("; ", said) + ")";
    }

    private static string ScratchFiles(Gather g)
    {
        var folder = Path.GetDirectoryName(g.ModelPath) ?? "";
        if (folder.Length == 0 || !Directory.Exists(folder)) return "not written: the scratch folder is not known";
        var sb = new StringBuilder(Csv("file", "bytes", "written")).AppendLine();
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(3000))
            try { var info = new FileInfo(file); sb.AppendLine(Csv(Path.GetRelativePath(folder, file), info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))); n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next file */ }
        return g.Write("scratch folder files.csv", sb.ToString()) + $" ({n} files)";
    }

    /// <summary>Counts of what the program holds after it opened the Modeler's file, to set beside the file itself (an import that came in empty or short shows here).</summary>
    private static string Holds(Gather g)
    {
        var sb = new StringBuilder(Csv("what", "count or answer")).AppendLine();
        void Line(string what, Func<string> read)
        {
            try { sb.AppendLine(Csv(what, read())); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine(Csv(what, "not read: " + ex.GetType().Name + ": " + Short(ex.Message))); }
        }
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Line("frames (FrameObj.Count)", () => Convert.ToInt32(g.Link.Raw("count the frames", m => m.FrameObj.Count())).ToString(c));
        Line("areas (AreaObj.Count)", () => Convert.ToInt32(g.Link.Raw("count the areas", m => m.AreaObj.Count())).ToString(c));
        Line("points (PointObj.Count)", () => Convert.ToInt32(g.Link.Raw("count the points", m => m.PointObj.Count())).ToString(c));
        Line("tendons (TendonObj.Count)", () => Convert.ToInt32(g.Link.Raw("count the tendons", m => m.TendonObj.Count())).ToString(c));
        Line("links (LinkObj.Count)", () => Convert.ToInt32(g.Link.Raw("count the links", m => m.LinkObj.Count())).ToString(c));
        Line("stories", () => g.Link.Stories().Count.ToString(c));
        Line("load patterns", () => string.Join(" | ", g.Link.LoadPatterns().Select(p => p.Name)));
        Line("materials (PropMaterial.GetNameList)", () => Names(g, m => { int n = 0; string[] names = []; m.PropMaterial.GetNameList(ref n, ref names); return names; }));
        Line("frame sections (PropFrame.GetNameList)", () => Names(g, m => { int n = 0; string[] names = []; m.PropFrame.GetNameList(ref n, ref names); return names; }));
        Line("area sections (PropArea.GetNameList)", () => Names(g, m => { int n = 0; string[] names = []; m.PropArea.GetNameList(ref n, ref names); return names; }));
        Line("design strips (DesignStrip.GetNameList)", () => Names(g, m => { int n = 0; string[] names = []; m.DesignStrip.GetNameList(ref n, ref names); return names; }));
        Line("load cases (LoadCases.GetNameList)", () => Names(g, m => { int n = 0; string[] names = []; m.LoadCases.GetNameList(ref n, ref names); return names; }));
        Line("locked (GetModelIsLocked)", () => Convert.ToBoolean(g.Link.Raw("read the lock", m => m.GetModelIsLocked())) ? "yes" : "no");
        Line("model file (GetModelFilename)", () => Convert.ToString(g.Link.Raw("read the file name", m => m.GetModelFilename(true))) ?? "");
        return g.Write("what the program holds.csv", sb.ToString());
    }

    private static string Names(Gather g, Func<dynamic, string[]?> read)
    {
        var names = (string[]?)g.Link.Raw("read a list of names", m => read(m)) ?? [];
        return names.Length + ": " + string.Join(" | ", names.Take(200));
    }

    /// <summary>Every interface of the program's API with its methods, read from its API file: which calls this version has (SectCut, SetNumberModes and the like).</summary>
    private static string ApiCalls(Gather g)
    {
        var api = EtabsApi.FindApiFile(g.Program);
        if (!File.Exists(api)) return "not written: the API file was not found";
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => { try { return !a.IsDynamic && string.Equals(a.Location, api, StringComparison.OrdinalIgnoreCase); } catch (NotSupportedException) { return false; } });
        if (loaded is null)
        {
            // A .NET Framework API file is not loaded into this process: its names are read without running it.
            if (EtabsApi.RunsOnFramework(api)) return MetadataCalls(g, api);
            loaded = System.Reflection.Assembly.LoadFrom(api);
        }
        Type[] types;
        try { types = loaded.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
        var sb = new StringBuilder();
        var n = 0;
        foreach (var type in types.Where(t => t.IsInterface).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.AppendLine(type.Name);
            foreach (var method in type.GetMethods().Select(m => $"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})").Distinct().OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(method);
            n++;
        }
        return g.Write("api calls.txt", sb.ToString()) + $" ({n} interfaces)";
    }

    private static string MetadataCalls(Gather g, string api)
    {
        using var stream = File.OpenRead(api);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if ((type.Attributes & System.Reflection.TypeAttributes.Interface) == 0) continue;
            sb.AppendLine(md.GetString(type.Name));
            foreach (var name in type.GetMethods().Select(h => md.GetString(md.GetMethodDefinition(h).Name)).Distinct().OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine("  " + name);
            n++;
        }
        return g.Write("api calls.txt", sb.ToString()) + $" ({n} interfaces, names only: a .NET Framework file)";
    }

    // ---- slab line releases and slab rebar (the slabs thread) ------------------------------------------------------
    // Not here on purpose: opening the example models in the scratch window. An older example can put up a message box
    // (upgrade, save changes) that no one answers, which would hold the window past its close and turn a needed thing
    // into a failure. The examples' own files are copied by CopyExamples instead.

    /// <summary>
    /// One slab line release (on a null line drawn across the first floor) and one slab rebar object, put into the
    /// scratch model through "Area Assignments - Slab Line Releases", "Slab Rebar Object Geometry" and "Slab Rebar
    /// Property Assignments". The fields the program edits, the rows sent, its apply log and the tables read back go to
    /// slab extras.txt; the text copy that follows has the program's own syntax for both. Only the test's own model.
    /// </summary>
    private static string SlabExtras(Gather g)
    {
        if (g.ModelPath.Length == 0) return "not made: only in the test's own scratch window";
        var sb = new StringBuilder();
        var c = System.Globalization.CultureInfo.InvariantCulture;
        string N(double v) => v.ToString("0.####", c);
        var floor = g.Link.Areas().FirstOrDefault(a => a.Kind == "floor" && a.Corners.Count >= 3);
        if (floor is null) return g.Write("slab extras.txt", "The scratch model has no floor to put them on.\n") + " (no floor)";
        double x0 = floor.Corners.Min(p => p.X), x1 = floor.Corners.Max(p => p.X), y0 = floor.Corners.Min(p => p.Y), y1 = floor.Corners.Max(p => p.Y), z = floor.Corners.Average(p => p.Z);
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, w = x1 - x0, h = y1 - y0;
        sb.AppendLine($"floor {floor.Name} ({floor.Section}): x {N(x0)} to {N(x1)}, y {N(y0)} to {N(y1)}, z {N(z)}");
        var said = new List<string>();

        // The line release goes on a null line across the floor.
        var line = "";
        try { line = g.Link.AddFrame(cx, y0 + 0.2 * h, z, cx, y1 - 0.2 * h, z, "None"); sb.AppendLine($"null line {line} drawn at x {N(cx)}"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine("null line not drawn: " + ex.GetType().Name + ": " + Short(ex.Message)); }
        var (label, story) = LabelOf(g, line);
        if (line.Length > 0)
            said.Add("line release " + Apply(g, sb, "Area Assignments - Slab Line Releases", "UniqueName", new()
            {
                ["UniqueName"] = line, ["Story"] = story, ["Label"] = label, ["ShearL"] = "Yes", ["MomentL"] = "Yes", ["ShearR"] = "No", ["MomentR"] = "Yes",
            }));
        if (story.Length == 0) story = g.Link.Stories().Skip(1).FirstOrDefault()?.Name ?? "";
        said.Add("slab rebar " + Apply(g, sb, "Slab Rebar Object Geometry", "Name", new()
        {
            ["Name"] = "CPM-SR1", ["Story"] = story, ["X1"] = N(x0 + 0.25 * w), ["Y1"] = N(cy), ["X2"] = N(x1 - 0.25 * w), ["Y2"] = N(cy),
            ["VertOffset"] = "0", ["WidthLeft"] = N(0.1 * h), ["WidthRight"] = N(0.1 * h), ["Layer"] = "A",
        }));
        // The program may make the rebar's property row by itself: read it first, and only add one when it did not.
        var props = Read(g, sb, "Slab Rebar Property Assignments");
        if (!props.Rows.Any(r => r.Contains("CPM-SR1")))
        {
            var bar = First(g, m => { int n = 0; string[] names = []; m.PropRebar.GetNameList(ref n, ref names); return names; });
            var steel = First(g, m => { int n = 0; string[] names = []; m.PropMaterial.GetNameList(ref n, ref names, 6); return names; });
            said.Add("its property " + Apply(g, sb, "Slab Rebar Property Assignments", "Name", new()
            {
                ["Name"] = "CPM-SR1", ["RebarSize"] = bar, ["Material"] = steel, ["BarNumType"] = "Total Bars", ["TotalBars"] = "4", ["MaxSpacing"] = "",
            }));
        }
        foreach (var t in new[] { "Area Assignments - Slab Line Releases", "Slab Rebar Object Geometry", "Slab Rebar Property Assignments" }) Read(g, sb, t);
        return g.Write("slab extras.txt", sb.ToString()) + " (" + string.Join("; ", said) + ")";
    }

    private static string First(Gather g, Func<dynamic, string[]?> read)
    {
        try { return ((string[]?)g.Link.Raw("read a list of names", m => read(m)))?.FirstOrDefault() ?? ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

    private static (string Label, string Story) LabelOf(Gather g, string name)
    {
        if (name.Length == 0) return ("", "");
        try
        {
            return ((string, string))g.Link.Raw("read the null line's label", m => { string label = "", story = ""; m.FrameObj.GetLabelFromName(name, ref label, ref story); return (label, story); })!;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ("", ""); }
    }

    /// <summary>Adds one row to a table through the program's table editing (EditableTables.Put, with its guards); says what the program answered.</summary>
    private static string Apply(Gather g, StringBuilder sb, string table, string keyField, Dictionary<string, string> values)
    {
        sb.AppendLine().AppendLine($"== {table}: add a row");
        var row = values.Where(v => v.Value.Length > 0).ToDictionary(v => v.Key, v => v.Value);
        sb.AppendLine("row: " + string.Join(", ", row.Select(v => v.Key + "=" + v.Value)));
        try
        {
            var answer = EditableTables.Put(g.Link, table, keyField, [row], "add a row to " + table);
            sb.AppendLine("taken: " + answer);
            return "taken";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine("not taken: " + ex.GetType().Name + ": " + ex.Message); return "not taken (" + ex.GetType().Name + ")"; }
    }

    private static TableData Read(Gather g, StringBuilder sb, string table)
    {
        sb.AppendLine().AppendLine($"== {table}: read back");
        try
        {
            var data = g.Link.ReadTable(table, null, null);
            sb.AppendLine(Csv([.. data.Fields]));
            foreach (var row in data.Rows.Take(50)) sb.AppendLine(Csv(row));
            return data;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine("not read: " + ex.GetType().Name + ": " + Short(ex.Message)); return new TableData([], []); }
    }

    /// <summary>
    /// A new blank model in the scratch window (the scratch model was saved before; this one is never saved), and every
    /// table it has data in: the defaults each Define form starts from (materials, sections, cases, preferences, options).
    /// </summary>
    private static string BlankDefaults(Gather g)
    {
        if (g.ModelPath.Length == 0) return "not made: only in the test's own scratch window";
        var answer = Convert.ToInt32(g.Link.Raw("start a blank model", m => { m.InitializeNewModel(); return m.File.NewBlank(); }));
        if (answer != 0) return $"not made: NewBlank answered {answer}";
        var blank = new Gather(g.Program, g.Link, Path.Combine(g.Folder, "blank model"), "");
        return "blank model/" + ScratchTables(blank);
    }
}
