using System.Globalization;
using System.Text;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Safe;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;
using CsiProMax.F2k;

namespace CsiProMax.Assistant;

/// <summary>
/// What the SAFE lossless work needs from a tester's SAFE (Ahmed 2026-11-27: "for SAFE whatever is needed ask for it
/// to be got in the next field test"), gathered into Reference data\SAFE\round trip, one folder per model:
/// A, SAFE's own .f2k of the model (File.ExportFile); B, A read by F2kReader and written back by F2kWriter; C, B put
/// into a new blank model through SAFE's tables (as File &gt; Import does) and exported again; the import log; the diff
/// of A and C table by table, row by row and field by field; an edited B (the first load pattern's self weight 0.5)
/// that must differ from A in that field only; and B through SAFE's own File.ImportFile. Plus SAFE's own .f2k of a
/// blank new model. Only copies are opened; the models are never saved.
/// </summary>
internal static class SafeRoundTrip
{
    /// <summary>Most models taken through the round trip, and the longest it may take in all.</summary>
    internal const int MostModels = 5;
    internal static TimeSpan Limit { get; set; } = TimeSpan.FromMinutes(12);

    /// <summary>Fields that differ on every export (a new GUID each time), left out of the diff.</summary>
    private static readonly string[] Ignored = ["GUID"];

    public static string Gather(ReferenceData.Gather g)
    {
        var root = Path.Combine(g.Folder, "round trip");
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        var types = FileTypes(g.Program);
        File.WriteAllText(Path.Combine(root, "file types.txt"), string.Join("\n", types.Select(t => $"{t.Name} = {t.Value}")) + "\n");
        var text = types.Where(t => Texty(t.Name)).ToList();
        if (text.Count == 0) return $"not gathered: SAFE's API names no text file type for ExportFile ({string.Join(", ", types.Select(t => t.Name))}); see round trip\\file types.txt";
        var info = g.Link.Info();
        lines.Add($"SAFE {info.Version}, API {Path.GetFileName(EtabsApi.FindApiFile(g.Program))}, units {info.Force}, {info.Length}, {info.Temperature}; text file types tried: {string.Join(", ", text.Select(t => $"{t.Name} ({t.Value})"))}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        // SAFE's defaults: its own .f2k of a blank new model in the tester's units.
        try
        {
            Blank(g, UnitsNow(g));
            lines.Add("blank model: " + Export(g, text, Path.Combine(root, "blank model (SAFE's own).f2k")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"blank model: not exported ({ex.GetType().Name}: {Short(ex.Message)})"); }

        foreach (var model in Models(g).Take(MostModels))
        {
            if (watch.Elapsed > Limit) { lines.Add($"{Path.GetFileName(model)}: not taken, out of time ({Limit.TotalMinutes:0} minutes)"); continue; }
            var name = Path.GetFileNameWithoutExtension(model);
            var folder = Path.Combine(root, Safe(name));
            Directory.CreateDirectory(folder);
            try { lines.Add($"{name}: {One(g, text, model, folder)}"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"{name}: stopped ({ex.GetType().Name}: {Short(ex.Message)})"); }
        }
        File.WriteAllText(Path.Combine(root, "round trip.md"), "# SAFE round trip\n\n" + string.Join("\n", lines.Select(l => "- " + l)) + "\n", new UTF8Encoding(false));
        return $"round trip\\round trip.md ({lines.Count - 1} models and the blank one)";
    }

    /// <summary>One model: A, B, C, the import log, the diff, the edited case and File.ImportFile.</summary>
    private static string One(ReferenceData.Gather g, List<(string Name, int Value)> text, string model, string folder)
    {
        // A copy of its own, so the original is only ever read.
        var copy = Path.Combine(folder, Path.GetFileName(model));
        if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(model), StringComparison.OrdinalIgnoreCase)) File.Copy(model, copy, true);
        var opened = Convert.ToInt32(g.Link.Raw("open a copy of the model", m => m.File.OpenFile(copy)), CultureInfo.InvariantCulture);
        if (opened != 0) return $"OpenFile answered {opened}";
        var units = UnitsNow(g);
        var aFile = Path.Combine(folder, "A (SAFE's own).f2k");
        var said = new List<string> { $"units {g.Link.Info().Force}, {g.Link.Info().Length}", "A " + Export(g, text, aFile) };
        if (!File.Exists(aFile)) return string.Join("; ", said);
        var a = File.ReadAllText(aFile);

        var b = F2kWriter.Write(F2kReader.Read(a, Path.GetFileNameWithoutExtension(model)).Model).Text;
        var bFile = Path.Combine(folder, "B (read and written by CSI ProMax).f2k");
        File.WriteAllText(bFile, b);
        File.WriteAllText(Path.Combine(folder, "diff A-B (offline).md"), Diff(a, b, "A", "B"));
        said.Add($"B written, {Count(a, b)} offline");

        // B into a new blank model through the tables, as File > Import does, then exported again.
        Blank(g, units);
        var log = F2kRebuild.ImportTables(g.Link, b);
        File.WriteAllText(Path.Combine(folder, "import log of B.json"), log.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var cFile = Path.Combine(folder, "C (B imported, exported again).f2k");
        said.Add($"B imported (applied {log["applied"]}, {log["errors"]} errors, {log["warnings"]} warnings), C " + Export(g, text, cFile));
        if (File.Exists(cFile))
        {
            var c = File.ReadAllText(cFile);
            File.WriteAllText(Path.Combine(folder, "diff A-C.md"), Diff(a, c, "A", "C"));
            said.Add("A to C: " + Count(a, c));
        }

        // One change: only that field may differ from A.
        if (Edited(b) is { } edited)
        {
            File.WriteAllText(Path.Combine(folder, "B edited (first pattern's self weight 0.5).f2k"), edited);
            Blank(g, units);
            var editedLog = F2kRebuild.ImportTables(g.Link, edited);
            File.WriteAllText(Path.Combine(folder, "import log of B edited.json"), editedLog.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var dFile = Path.Combine(folder, "D (B edited, imported, exported again).f2k");
            said.Add("edited: D " + Export(g, text, dFile));
            if (File.Exists(dFile)) File.WriteAllText(Path.Combine(folder, "diff A-D.md"), Diff(a, File.ReadAllText(dFile), "A", "D"));
        }

        // SAFE's own File.ImportFile of B, each text type and import kind, on a blank model each.
        var tries = new List<string>();
        foreach (var (typeName, type) in text)
            foreach (var kind in new[] { 1, 2, 3 })
            {
                try
                {
                    Blank(g, units);
                    var answer = Convert.ToInt32(g.Link.Raw("import B with File.ImportFile", m => m.File.ImportFile(bFile, type, kind)), CultureInfo.InvariantCulture);
                    var objects = Objects(g);
                    tries.Add($"{typeName}, kind {kind}: answered {answer}, {objects} objects");
                    if (answer == 0 && objects > 0)
                    {
                        var eFile = Path.Combine(folder, $"E (B by File.ImportFile {typeName} {kind}, exported again).f2k");
                        Export(g, text, eFile);
                        if (File.Exists(eFile)) File.WriteAllText(Path.Combine(folder, $"diff A-E {typeName} {kind}.md"), Diff(a, File.ReadAllText(eFile), "A", "E"));
                        goto imported;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { tries.Add($"{typeName}, kind {kind}: {ex.GetType().Name}: {Short(ex.Message)}"); }
            }
        imported:
        File.WriteAllText(Path.Combine(folder, "File.ImportFile tries.txt"), string.Join("\n", tries) + "\n");
        said.Add("File.ImportFile: " + (tries.LastOrDefault() ?? "not tried"));
        return string.Join("; ", said);
    }

    /// <summary>The models: the shipped slab examples copied into Reference data, then the Modeler's own .fdb scratch copy.</summary>
    private static IEnumerable<string> Models(ReferenceData.Gather g)
    {
        var verification = Path.Combine(g.Folder, "Verification");
        var examples = Directory.Exists(verification)
            ? Directory.EnumerateFiles(verification, "*.fdb", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : [];
        foreach (var e in examples) yield return e;
        if (g.ModelPath.Length > 0 && File.Exists(g.ModelPath) && Path.GetExtension(g.ModelPath).Equals(".fdb", StringComparison.OrdinalIgnoreCase)) yield return g.ModelPath;
    }

    /// <summary>SAFE's text export of the open model into <paramref name="file"/>, by the first text type it takes.</summary>
    private static string Export(ReferenceData.Gather g, List<(string Name, int Value)> types, string file)
    {
        var said = new List<string>();
        foreach (var (name, value) in types)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                var answer = Convert.ToInt32(g.Link.Raw("export the model as text", m => m.File.ExportFile(file, value)), CultureInfo.InvariantCulture);
                if (answer == 0 && File.Exists(file) && new FileInfo(file).Length > 0) return $"exported ({name}, {new FileInfo(file).Length / 1024} KB)";
                said.Add($"{name} answered {answer}{(File.Exists(file) ? "" : ", no file")}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { said.Add($"{name}: {ex.GetType().Name}: {Short(ex.Message)}"); }
        }
        return "not exported (" + string.Join("; ", said) + ")";
    }

    /// <summary>A new blank model in the given units (never saved).</summary>
    private static void Blank(ReferenceData.Gather g, int units) => g.Link.Raw("start a blank model", m =>
    {
        object model = m;
        ApiCall.Must(model, "InitializeNewModel", CsiTools.D(("Units", units)), "start a new model");
        return ApiCall.First(model, ["File.NewBlank"], null, "start a blank model");
    });

    private static int UnitsNow(ReferenceData.Gather g)
    {
        var info = g.Link.Info();
        var name = $"{(info.Force == "tonf" ? "Ton" : info.Force)}_{info.Length}_{info.Temperature}";
        var i = Array.FindIndex(ComEtabs.UnitNames, u => string.Equals(u, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i + 1 : 6; // kN_m_C
    }

    private static int Objects(ReferenceData.Gather g) => Convert.ToInt32(g.Link.Raw("count the objects", m =>
    {
        object model = m;
        return (int)(ApiCall.Number(ApiCall.Invoke(model, "AreaObj.Count")["return"]) + ApiCall.Number(ApiCall.Invoke(model, "FrameObj.Count")["return"]));
    }), CultureInfo.InvariantCulture);

    /// <summary>The API's file types (eFileTypeIO), by name and number.</summary>
    internal static List<(string Name, int Value)> FileTypes(CsiProgram program)
    {
        var file = EtabsApi.FindApiFile(program);
        if (!File.Exists(file) || EtabsApi.RunsOnFramework(file)) return [];
        var type = EtabsApi.Load(file).GetType(EtabsApi.NamespaceOf(file) + ".eFileTypeIO");
        return type is null ? [] : Enum.GetValues(type).Cast<object>().Select(v => (Enum.GetName(type, v) ?? "", Convert.ToInt32(v, CultureInfo.InvariantCulture))).ToList();
    }

    /// <summary>A file type that may be SAFE's text file (.f2k).</summary>
    internal static bool Texty(string name) => new[] { "Text", "F2k", "Safe" }.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))
        && !new[] { "Excel", "Access", "XML", "DBTables" }.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>B with the first load pattern's self weight multiplier set to 0.5; null when B has no pattern.</summary>
    internal static string? Edited(string f2k)
    {
        var lines = f2k.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inPatterns = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("TABLE:", StringComparison.Ordinal)) { inPatterns = lines[i].Contains("\"LOAD PATTERN DEFINITIONS\"", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inPatterns || !lines[i].Contains("SelfWtMult=", StringComparison.OrdinalIgnoreCase) && !lines[i].Contains("\"Self Weight Multiplier\"=", StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = System.Text.RegularExpressions.Regex.Replace(lines[i], "((?:SelfWtMult|\"Self Weight Multiplier\")=)\\S+", "${1}0.5");
            return string.Join("\r\n", lines);
        }
        return null;
    }

    /// <summary>Tables, rows and fields that differ, in a line.</summary>
    internal static string Count(string a, string b)
    {
        var (tables, rows, fields) = Compare(a, b, null);
        return tables == 0 ? "the same in every table, row and field" : $"{tables} tables, {rows} rows and {fields} fields differ";
    }

    /// <summary>The difference of two .f2k texts, table by table, row by row and field by field (GUIDs left out).</summary>
    internal static string Diff(string a, string b, string nameA, string nameB)
    {
        var sb = new StringBuilder($"# {nameA} against {nameB}\n\nTables, rows and fields that differ (GUID left out: a new one on every export).\n\n");
        var (tables, rows, fields) = Compare(a, b, sb);
        sb.Insert(0, $"<!-- {tables} tables, {rows} rows, {fields} fields differ -->\n");
        if (tables == 0) sb.AppendLine("The same in every table, row and field.");
        return sb.ToString();
    }

    private static (int Tables, int Rows, int Fields) Compare(string a, string b, StringBuilder? sb)
    {
        var ta = CsiTableText.Parse(a); var tb = CsiTableText.Parse(b);
        int tables = 0, rows = 0, fields = 0;
        foreach (var name in ta.Keys.Union(tb.Keys, StringComparer.OrdinalIgnoreCase).Where(n => n != "PROGRAM CONTROL").OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var ra = ta.GetValueOrDefault(name) ?? []; var rb = tb.GetValueOrDefault(name) ?? [];
            if (ra.Count == 0 && rb.Count == 0) continue;
            var said = new List<string>();
            if (ra.Count == 0 || rb.Count == 0) said.Add(ra.Count == 0 ? $"only in {"the second"} ({rb.Count} rows)" : $"only in the first ({ra.Count} rows)");
            else
            {
                if (ra.Count != rb.Count) said.Add($"{ra.Count} rows against {rb.Count}");
                for (var i = 0; i < Math.Min(ra.Count, rb.Count); i++)
                {
                    var keys = ra[i].Keys.Union(rb[i].Keys, StringComparer.OrdinalIgnoreCase).Where(k => !Ignored.Contains(k, StringComparer.OrdinalIgnoreCase));
                    var off = keys.Where(k => !Same(ra[i].GetValueOrDefault(k), rb[i].GetValueOrDefault(k))).ToList();
                    if (off.Count == 0) continue;
                    rows++; fields += off.Count;
                    if (said.Count < 25) said.Add($"row {i + 1}: " + string.Join(", ", off.Select(k => $"{k}: '{ra[i].GetValueOrDefault(k) ?? "(none)"}' / '{rb[i].GetValueOrDefault(k) ?? "(none)"}'")));
                }
                if (ra.Count != rb.Count) rows += Math.Abs(ra.Count - rb.Count);
            }
            if (said.Count == 0) continue;
            tables++;
            sb?.AppendLine($"## {name}\n").AppendLine(string.Join("\n", said.Select(s => "- " + s))).AppendLine();
        }
        return (tables, rows, fields);
    }

    /// <summary>Two values the same, numbers within a part in a million.</summary>
    private static bool Same(string? x, string? y)
    {
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        return false;
    }

    private static string Safe(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')' or '.' ? c : '_').ToArray()).Trim();
    private static string Short(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
