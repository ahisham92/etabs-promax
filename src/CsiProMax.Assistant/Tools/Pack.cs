using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The review pack (.cpmx): one zip with the model as ETABS has it, the result and design tables ETABS produced, the
/// checks made from them, and review comments. A reviewer opens it in the CSI ProMax Viewer (one HTML file, nothing
/// to install, no ETABS). Results are copied from ETABS's tables as they are; nothing is analysed here.
/// </summary>
public static class Pack
{
    public const int Version = 1;
    public const int DefaultMostRows = 60000;
    /// <summary>Design tables are taken longer: a concrete beam table has a row per station of every beam.</summary>
    public const int DesignMostRows = 200000;

    /// <summary>The result tables a pack takes when they have data. Design summaries are added by name.</summary>
    public static readonly string[] UsualTables =
    [
        "Story Definitions", "Modal Participating Mass Ratios", "Modal Periods And Frequencies", "Base Reactions", "Story Drifts",
        "Story Max Over Avg Displacements", "Story Max Over Avg Drifts", "Story Stiffness", "Story Forces", "Mass Summary by Story",
        "Centers Of Mass And Rigidity", "Joint Displacements", "Joint Reactions", "Diaphragm Center Of Mass Displacements",
        // What the Viewer draws force and moment diagrams from.
        "Element Forces - Beams", "Element Forces - Columns", "Element Forces - Braces",
    ];

    public sealed record Built(byte[] Zip, JsonObject Content, int Frames, int Areas, IReadOnlyList<string> Tables, IReadOnlyList<string> Notes);

    /// <param name="extraTables">More tables to take, by name or key (for example "Element Forces - Beams").</param>
    public static Built Build(ICsiLink e, IReadOnlyList<string> extraTables, IReadOnlyList<string> cases, IReadOnlyList<string> combos,
        JsonObject? checks, JsonArray? comments, int mostRows = DefaultMostRows, string by = "", bool withTables = true,
        IReadOnlyList<string>? designFiles = null)
    {
        var info = e.Info();
        var notes = new List<string>();
        var file = info.File.Replace('\\', '/').Split('/').Last();
        var manifest = new JsonObject
        {
            ["format"] = "cpmx", ["version"] = Version,
            ["model"] = file.Length > 0 ? Path.GetFileNameWithoutExtension(file) : e is OfflineEtabs { Model.Title.Length: > 0 } own ? own.Model.Title : "Unsaved model",
            ["file"] = info.File, ["etabs"] = info.Version, ["program"] = e.Program.Name,
            ["units"] = new JsonObject { ["force"] = info.Force, ["length"] = info.Length, ["temperature"] = info.Temperature },
            ["analysis_run"] = info.Locked,
            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["by"] = by,
            ["made_with"] = "CSI ProMax",
        };
        var frames = e.Frames();
        var areas = e.Areas();
        var points = e.Points();
        var model = new JsonObject
        {
            ["stories"] = new JsonArray(e.Stories().Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["elevation"] = R(s.Elevation), ["height"] = R(s.Height) }).ToArray()),
            ["frames"] = new JsonArray(frames.Select(f => (JsonNode)new JsonArray(f.Name, f.Kind, f.Section, f.Story, R(f.X1), R(f.Y1), R(f.Z1), R(f.X2), R(f.Y2), R(f.Z2))).ToArray()),
            ["areas"] = new JsonArray(areas.Select(a => (JsonNode)new JsonObject
            {
                ["n"] = a.Name, ["k"] = a.Kind, ["s"] = a.Section,
                ["c"] = new JsonArray(a.Corners.SelectMany(c => new[] { (JsonNode)R(c.X), R(c.Y), R(c.Z) }).ToArray()),
            }).ToArray()),
            ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(p.Name, R(p.X), R(p.Y), R(p.Z))).ToArray()),
            ["frame_sections"] = Texts(e.FrameSections()), ["area_sections"] = Texts(e.AreaSections()), ["materials"] = Texts(e.Materials()),
            ["load_patterns"] = new JsonArray(e.LoadPatterns().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["self_weight"] = p.SelfWeight }).ToArray()),
            ["load_cases"] = Texts(e.LoadCases()), ["combinations"] = Texts(e.Combos()),
        };

        Drawing.AddTo(model, e, points);

        var known = withTables ? e.Tables() : [];
        var wanted = new List<TableName>();
        foreach (var name in UsualTables)
            if (known.FirstOrDefault(t => !t.Empty && (ResultTools.Norm(t.Name) == ResultTools.Norm(name) || ResultTools.Norm(t.Key) == ResultTools.Norm(name))) is { } t) wanted.Add(t);
        wanted.AddRange(known.Where(t => !t.Empty && (DesignResults.IsDesignTable(t.Name) || DesignResults.IsWallTable(t.Name))));
        wanted.AddRange(known.Where(t => !t.Empty && ProgramTools.PackTable(e.Program, t.Name)));
        foreach (var name in extraTables)
        {
            var t = known.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase));
            if (t is null) notes.Add($"There is no table '{name}', so it was left out.");
            else wanted.Add(t);
        }
        if (cases.Count == 0 && combos.Count == 0) { cases = e.LoadCases(); combos = e.Combos(); }
        var tables = new JsonObject();
        foreach (var t in wanted.DistinctBy(t => t.Key))
        {
            TableData data;
            try { data = e.ReadTable(t.Key, cases, combos); }
            catch (EtabsException ex) { notes.Add($"'{t.Name}' could not be read ({ex.Message})."); continue; }
            if (data.Rows.Count == 0) continue;
            tables[t.Name] = TableJson(t.Name, data, DesignResults.IsDesignTable(t.Name) ? Math.Max(mostRows, DesignMostRows) : mostRows, notes, null);
        }
        if (withTables && tables.Count <= 1 && !info.Locked) notes.Add("The analysis has not been run, so the pack holds the model without results.");
        if (withTables) manifest["design"] = AddDesign(e, info, known, tables, designFiles ?? [], Math.Max(mostRows, DesignMostRows), notes);
        if (withTables && (areas.Any(a => a.Kind == "wall") || tables.Select(t => t.Key).Any(n => DesignResults.FamilyOf(n) is { On: "pier" or "spandrel" })))
            model["wall_labels"] = DesignResults.WallLabels(e, known, notes);
        else if (e is OfflineEtabs) model["wall_labels"] = DesignResults.WallLabels(e, [], notes);

        var content = new JsonObject { ["manifest"] = manifest, ["model"] = model, ["checks"] = checks, ["comments"] = comments ?? [], ["tables"] = tables };
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip, "manifest.json", manifest);
            Add(zip, "model.json", model);
            if (checks is not null) Add(zip, "checks.json", checks);
            Add(zip, "comments.json", comments ?? []);
            var n = 0;
            foreach (var (_, table) in tables) Add(zip, $"tables/{++n:000}.json", table!);
        }
        return new Built(stream.ToArray(), content, frames.Count, areas.Count, tables.Select(t => t.Key).ToList(), notes);
    }

    private static JsonObject TableJson(string name, TableData data, int most, List<string> notes, string? from)
    {
        var table = new JsonObject
        {
            ["name"] = name, ["fields"] = Texts(data.Fields),
            ["rows"] = new JsonArray(data.Rows.Take(most).Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)v).ToArray())).ToArray()),
        };
        if (from is not null) table["from"] = from;
        if (data.Rows.Count > most)
        {
            table["left_out"] = data.Rows.Count - most;
            notes.Add($"'{name}' has {data.Rows.Count} rows; the first {most} are in the pack. Choose fewer load cases to take it whole.");
        }
        return table;
    }

    /// <summary>
    /// The design results beyond the tables that had data: the API's own summaries for the families no table gave,
    /// then tables exported from the program into files (given, or beside the model). Says per family where its
    /// results came from, and what to do in the program for a family that has none.
    /// </summary>
    private static JsonObject AddDesign(ICsiLink e, ModelInfo info, IReadOnlyList<TableName> known, JsonObject tables, IReadOnlyList<string> files, int most, List<string> notes)
    {
        var from = new Dictionary<string, List<string>>();
        void Took(string family, string what) { if (!from.TryGetValue(family, out var l)) from[family] = l = []; if (!l.Contains(what)) l.Add(what); }
        foreach (var (name, _) in tables) if (DesignResults.FamilyOf(name) is { } f) Took(f.Key, "the program's design tables");
        var have = new HashSet<string>(from.Keys);
        var apiNotes = new List<string>();
        foreach (var (name, data) in DesignResults.FromApi(e, have, apiNotes))
        {
            tables[name] = TableJson(name, data, most, notes, "api");
            Took(DesignResults.FamilyOf(name)!.Key, "the API's design summary");
        }
        // Exported files fill what is still missing: a family the live tables have is not replaced by an older file.
        var paths = files.Concat(DesignResults.ExportsBeside(info.File)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in paths)
        {
            List<(string Name, TableData Data)> read;
            try { read = DesignResults.ReadExport(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
            {
                if (files.Contains(path, StringComparer.OrdinalIgnoreCase)) notes.Add($"The file {path} could not be read ({ex.Message}).");
                continue;
            }
            var file = Path.GetFileName(path);
            foreach (var (name, data) in read)
            {
                var family = DesignResults.FamilyOf(name);
                var walls = DesignResults.IsWallTable(name);
                if (family is null && !walls || data.Rows.Count == 0 || tables.ContainsKey(name)) continue;
                if (family is not null && have.Contains(family.Key)) continue;
                tables[name] = TableJson(name, data, most, notes, "file: " + file);
                if (family is not null) Took(family.Key, $"the exported file {file} ({File.GetLastWriteTime(path):yyyy-MM-dd HH:mm})");
            }
        }
        foreach (var family in from.Keys.Where(k => !have.Contains(k) && from[k].Any(w => w.StartsWith("the exported", StringComparison.Ordinal))))
            notes.Add($"{DesignResults.All.First(f => f.Key == family).Name} came from {string.Join(", ", from[family])}: check it is from the model as it is now.");
        // Families the model has tables for, but empty: the design was not run (or not kept) in the program.
        var waiting = known.Where(t => t.Empty && DesignResults.FamilyOf(t.Name) is { } f && !from.ContainsKey(f.Key)).Select(t => DesignResults.FamilyOf(t.Name)!).DistinctBy(f => f.Key).ToList();
        foreach (var f in waiting) notes.Add(HowToGet(f, e.Program));
        notes.AddRange(apiNotes.Take(3));
        var o = new JsonObject();
        foreach (var (k, list) in from) o[k] = new JsonObject { ["name"] = DesignResults.All.First(f => f.Key == k).Name, ["from"] = new JsonArray(list.Select(x => (JsonNode)x).ToArray()) };
        if (waiting.Count > 0) o["not_run"] = new JsonArray(waiting.Select(f => (JsonNode)f.Name).ToArray());
        return o;
    }

    /// <summary>What to do in the program to have a family's results in the next pack.</summary>
    internal static string HowToGet(DesignResults.Family f, CsiProgram program)
    {
        var menu = f.Key switch
        {
            "pier" or "spandrel" => "Design > Shear Wall Design > Start Design/Check",
            "steel" => "Design > Steel Frame Design > Start Design/Check",
            "concrete" => "Design > Concrete Frame Design > Start Design/Check",
            "composite" => "Design > Composite Beam Design > Start Design/Check",
            "compcol" => "Design > Composite Column Design > Start Design/Check",
            "joist" => "Design > Steel Joist Design > Start Design/Check",
            "slab" or "punching" => "Design > Concrete Slab Design > Start Design/Check",
            "connection" => "Design > Steel Connection Design",
            _ => "the Design menu",
        };
        return $"{f.Name} has no results in {program.Name} yet: run {menu} there, then make the pack again. If {program.Name} still gives none, " +
               "export the design tables (File > Export > tables to Excel) into the model's folder with the model's name: the next pack takes them from that file.";
    }

    private static double R(double v) => Math.Round(v, 5);
    private static JsonArray Texts(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());

    private static void Add(ZipArchive zip, string name, JsonNode node)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Reads a pack back: its content as the Viewer gets it.</summary>
    public static JsonObject Read(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        JsonNode? Text(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return JsonNode.Parse(reader.ReadToEnd());
        }
        var content = new JsonObject
        {
            ["manifest"] = Text("manifest.json") ?? throw new InvalidDataException("This is not a CSI ProMax pack (it has no manifest)."),
            ["model"] = Text("model.json") ?? throw new InvalidDataException("This is not a CSI ProMax pack (it has no model)."),
            ["checks"] = Text("checks.json"),
            ["comments"] = Text("comments.json") ?? new JsonArray(),
        };
        var tables = new JsonObject();
        foreach (var entry in zip.Entries.Where(en => en.FullName.StartsWith("tables/", StringComparison.Ordinal)).OrderBy(en => en.FullName, StringComparer.Ordinal))
            if (Text(entry.FullName) is JsonObject t && t["name"] is { } name) tables[name.GetValue<string>()] = t;
        content["tables"] = tables;
        return content;
    }

    /// <summary>The Viewer page. With <paramref name="content"/> the pack is inside it, so the reviewer opens one file.</summary>
    public static string ViewerHtml(JsonObject? content = null)
    {
        using var stream = typeof(Pack).Assembly.GetManifestResourceStream("CsiProMax.viewer.html")
            ?? throw new InvalidOperationException("The Viewer page is missing from this build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = reader.ReadToEnd();
        if (content is null) return html;
        const string slot = "<script id=\"pack\" type=\"application/json\"></script>";
        if (!html.Contains(slot, StringComparison.Ordinal)) throw new InvalidOperationException("The Viewer page has no place for a pack.");
        // Inside a script element only "</" and "<!--" can end or confuse it.
        var json = content.ToJsonString().Replace("</", "<\\/", StringComparison.Ordinal).Replace("<!--", "<\\u0021--", StringComparison.Ordinal);
        return html.Replace(slot, "<script id=\"pack\" type=\"application/json\">" + json + "</script>", StringComparison.Ordinal);
    }

    /// <summary>
    /// The Viewer page for the model that is open now, served on this PC: it asks for the model again whenever the
    /// revision moves, and keeps the reader's view.
    /// </summary>
    public static string LiveViewerHtml(JsonObject content, int revision)
    {
        var html = ViewerHtml(content);
        // cpmPull asks at once (after a hand edit); otherwise every second and a half.
        var live = "<script>window.cpmLive=true;(function(){let rev=" + revision.ToString(CultureInfo.InvariantCulture) + ";window.cpmPull=async()=>{try{const r=await fetch('model?rev='+rev,{cache:'no-store'});" +
                   "if(r.status!==200)return;const j=await r.json();rev=j.rev;cpmLoad(j.pack,true);}catch(e){}};setInterval(window.cpmPull,1500);})();</script>";
        return html.Replace("</body>", live + "</body>", StringComparison.Ordinal);
    }

    /// <summary>The comments in a pack (.cpmx) or in a comments file the Viewer saved (.json).</summary>
    public static (string Model, JsonArray Comments) ReadComments(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            var content = Read(bytes);
            return (content["manifest"]?["model"]?.GetValue<string>() ?? "", content["comments"]!.DeepClone().AsArray());
        }
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        if (JsonNode.Parse(text) is not JsonObject o || o["comments"] is not JsonArray list)
            throw new InvalidDataException("This file has no review comments. Give a pack (.cpmx) or the comments file the Viewer saved.");
        return (o["model"]?.GetValue<string>() ?? "", list.DeepClone().AsArray());
    }
}
