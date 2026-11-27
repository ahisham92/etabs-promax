using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using CsiProMax.Assistant.Etabs;
using CsiProMax.CsiTables;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// ETABS 23.2's File > Export items in the Engine (Ahmed 2026-11-27: every form identical, every button working as in
/// ETABS). Each writes a copy: the model open in the Engine keeps its own file, name and units.
/// - export_e2k: File > Export > ETABS .e2k Text File, in the units of ETABS's Consistent Units form.
/// - export_tables_file: File > Export > ETABS Database Tables to Text / XML.
/// - write_export_file: the geometry files the Engine's page makes of the model it draws (.dxf, .igs, .stl, .obj,
///   .ifc, .sdnf, .html), written where Windows' Save As picker said.
/// </summary>
public static class FileMenuTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This export works in the CSI ProMax Engine. In ETABS itself, use its own File > Export."); // ETABS itself

    /// <summary>The files write_export_file may write: the Engine's export formats, nothing else.</summary>
    public static readonly string[] GeometryExtensions = [".dxf", ".dwg", ".igs", ".iges", ".stl", ".obj", ".ifc", ".sdnf", ".stp", ".step", ".html", ".htm", ".mtl"];

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("export_e2k",
            "Writes the model as an ETABS text file (.e2k) at 'path', as ETABS's File > Export > ETABS .e2k Text File does, in the units of its Consistent Units form ('length' in, ft, mm, cm or m; 'force' lb, kip, N, kN, kgf or tonf; 'temperature' F or C; default the model's own). The model open keeps its own file and units.", // ETABS itself
            """{"type":"object","required":["path"],"properties":{"path":{"type":"string"},"length":{"type":"string"},"force":{"type":"string"},"temperature":{"type":"string","enum":["F","C"]}}}""",
            Ask.Always, ExportE2k, a => $"Export the model to {Path.GetFileName(a.Text("path"))}"),
        new("export_table_list",
            "Every database table the model can be exported as (File > Export > Database Tables), with its row count: the model's own tables and the program's tables it keeps.",
            """{"type":"object","properties":{}}""",
            Ask.Never, (_, e) => new JsonObject { ["tables"] = new JsonArray(ModelTables.Export(Offline(e).ForSave).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["rows"] = t.Rows.Count }).ToArray()) }),
        new("export_tables_file",
            "Writes the model's database tables to a file at 'path', as ETABS's File > Export > ETABS Database Tables to Excel (format excel, .xlsx), Access (format access, .accdb or .mdb, needs the Microsoft Access Database Engine), Text (format text: each table as TABLE: \"name\" then one line of Field=Value pairs per row) or XML (format xml). 'tables' picks some by name (default every table with rows); 'patterns' keeps only those load patterns' rows; 'selected' keeps only the rows of those objects (Selection Only); 'open' editor or word opens the file after export.", // ETABS itself
            """{"type":"object","required":["path","format"],"properties":{"path":{"type":"string"},"format":{"type":"string","enum":["excel","access","text","xml"]},"tables":{"type":"array","items":{"type":"string"}},"patterns":{"type":"array","items":{"type":"string"}},"selected":{"type":"array","items":{"type":"string"}},"open":{"type":"string","enum":["","file","editor","word"]}}}""",
            Ask.Always, ExportTables, a => $"Export the model's tables to {Path.GetFileName(a.Text("path"))}"),
        new("write_export_file",
            "Writes a file the Engine's page made of the model (File > Export: .dxf, .igs, .stl, .obj, .ifc, .sdnf, .html) at 'path'. Only those file types.",
            """{"type":"object","required":["path","text"],"properties":{"path":{"type":"string"},"text":{"type":"string"}}}""",
            Ask.Always, WriteExport, a => $"Write {Path.GetFileName(a.Text("path"))}"),
    ];

    private static string FilePath(JsonElement a, string ext)
    {
        var path = a.Need("path").Trim().Trim('"');
        if (!Path.IsPathRooted(path)) throw new ToolInputException("'path' is the file's full path.");
        if (ext.Length > 0 && !path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) path += ext;
        return path;
    }

    private static void Write(string path, Action<string> write)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); write(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new EtabsException($"{path} could not be written: {ex.Message}" + (ex is IOException ? " If the file is open in another program, close it there first." : "")); }
    }

    private static readonly Dictionary<string, string> Lengths = new(StringComparer.OrdinalIgnoreCase) { ["in"] = "in", ["inch"] = "in", ["ft"] = "ft", ["mm"] = "mm", ["cm"] = "cm", ["m"] = "m" };
    private static readonly Dictionary<string, string> Forces = new(StringComparer.OrdinalIgnoreCase) { ["lb"] = "lb", ["kip"] = "kip", ["n"] = "N", ["kn"] = "KN", ["kgf"] = "kgf", ["tonf"] = "tonf", ["ton"] = "tonf" };

    private static JsonNode ExportE2k(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var path = FilePath(a, ".e2k");
        var model = o.ForSave;
        var force = a.Text("force"); var length = a.Text("length"); var temperature = a.Text("temperature");
        if (force.Length > 0 || length.Length > 0 || temperature.Length > 0)
        {
            var to = new CsiProMax.Model.Units
            {
                Force = force.Length == 0 ? model.Units.Force : Forces.TryGetValue(force, out var f) ? f : throw new ToolInputException($"'force' {force} is not one of lb, kip, N, kN, kgf, tonf."),
                Length = length.Length == 0 ? model.Units.Length : Lengths.TryGetValue(length, out var l) ? l : throw new ToolInputException($"'length' {length} is not one of in, ft, mm, cm, m."),
                Temperature = temperature.Length == 0 ? model.Units.Temperature : temperature.ToUpperInvariant(),
            };
            if (!ModelUnits.Same(to, model.Units))
            {
                try { model = ModelUnits.Convert(model, to); }
                catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
            }
            else model.Units.Temperature = to.Temperature;
        }
        Write(path, p => E2kWriter.WriteFile(model, p));
        return new JsonObject { ["saved"] = path, ["units"] = $"{model.Units.Force}, {model.Units.Length}, {model.Units.Temperature}" };
    }

    private static List<string> Texts(JsonElement a, string key) =>
        a.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];

    // The fields that name the object a row is of, and the load pattern it is of, as the programs' tables name them.
    private static readonly string[] ObjectFields = ["UniqueName", "Label", "Frame", "Area", "Joint", "Point", "Object", "Link", "Tendon", "Wall", "Floor", "Beam", "Column"];
    private static readonly string[] PatternFields = ["LoadPattern", "LoadPat", "Pattern"];

    /// <summary>The tables picked, with only the rows of the load patterns and objects asked for (ETABS's Select Load
    /// Patterns and Selection Only).</summary>
    public static List<ModelTable> Tables(StructuralModel m, IReadOnlyCollection<string> picked, IReadOnlyCollection<string> patterns, IReadOnlyCollection<string> selected)
    {
        var tables = ModelTables.Export(m, picked);
        if (picked.Count == 0) tables = tables.Where(x => x.Rows.Count > 0).ToList();
        if (tables.Count == 0) throw new ToolInputException("No table was picked.");
        List<ModelTable> Keep(List<ModelTable> list, string[] fields, IReadOnlyCollection<string> only)
        {
            if (only.Count == 0) return list;
            var set = only.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return list.Select(t =>
            {
                var at = fields.Select(t.Field).FirstOrDefault(i => i >= 0, -1);
                return at < 0 ? t : t with { Rows = t.Rows.Where(r => at < r.Count && set.Contains(r[at])).ToList() };
            }).ToList();
        }
        return Keep(Keep(tables, PatternFields, patterns), ObjectFields, selected);
    }

    private static JsonNode ExportTables(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var format = a.Text("format").ToLowerInvariant();
        var path = FilePath(a, format switch { "xml" => ".xml", "excel" => ".xlsx", "access" => "", _ => ".txt" });
        if (format == "access" && !path.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)) path += ".accdb";
        var tables = Tables(o.ForSave, Texts(a, "tables"), Texts(a, "patterns"), Texts(a, "selected"));
        Write(path, p =>
        {
            switch (format)
            {
                case "excel": TableTools.Write(p, tables); break;
                case "access": Access(p, tables); break;
                case "xml": File.WriteAllText(p, Xml(o.Model.Title, tables), new UTF8Encoding(false)); break;
                default: File.WriteAllText(p, Text(Path.GetFileName(p), tables), new UTF8Encoding(false)); break;
            }
        });
        var open = a.Text("open");
        if (open.Length > 0 && OperatingSystem.IsWindows())
        {
            try
            {
                var start = open == "word" ? new System.Diagnostics.ProcessStartInfo("winword.exe", "\"" + path + "\"") { UseShellExecute = true }
                    : open == "editor" ? new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true }
                    : new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
                System.Diagnostics.Process.Start(start)?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return new JsonObject { ["saved"] = path, ["tables"] = new JsonArray(tables.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["rows"] = x.Rows.Count }).ToArray()) };
    }

    /// <summary>An Access database of the tables (one per table, its fields as text, the ones with units as numbers), made
    /// through the Microsoft Access Database Engine (ACE) that Office and the CSI programs install.</summary>
    private static void Access(string path, List<ModelTable> tables)
    {
        if (!OperatingSystem.IsWindows()) throw new ToolInputException("An Access database is written on Windows only.");
        var catalogType = Type.GetTypeFromProgID("ADOX.Catalog");
        var connectionType = Type.GetTypeFromProgID("ADODB.Connection");
        if (catalogType is null || connectionType is null) throw new ToolInputException("Windows has no ADO here, so the Access database cannot be written.");
        if (File.Exists(path)) File.Delete(path);
        var provider = "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=" + path + ";" + (path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) ? "Jet OLEDB:Engine Type=5;" : "");
        dynamic? catalog = null, connection = null;
        try
        {
            catalog = Activator.CreateInstance(catalogType)!;
            try { catalog.Create(provider); }
            catch (Exception ex) { throw new ToolInputException("The Microsoft Access Database Engine is not installed, so the Access database cannot be written (" + ex.Message + ")."); }
            connection = Activator.CreateInstance(connectionType)!;
            connection.Open(provider);
            static string Name(string n) => "[" + new string(n.Select(c => c is '.' or '!' or '[' or ']' or '`' ? '_' : c).ToArray()) + "]";
            foreach (var t in tables)
            {
                var name = t.Name.Length > 64 ? t.Name[..64] : t.Name;
                var numeric = t.Fields.Select((field, i) => i < t.Units.Count && t.Units[i].Length > 0 && t.Rows.All(r => i >= r.Count || r[i].Length == 0 || double.TryParse(r[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))).ToList();
                connection.Execute($"CREATE TABLE {Name(name)} ({string.Join(", ", t.Fields.Select((f, i) => Name(f) + (numeric[i] ? " DOUBLE" : " MEMO")))})");
                foreach (var row in t.Rows)
                {
                    var values = t.Fields.Select((_, i) => i >= row.Count || row[i].Length == 0 ? "NULL" : numeric[i] ? row[i] : "'" + row[i].Replace("'", "''") + "'");
                    connection.Execute($"INSERT INTO {Name(name)} VALUES ({string.Join(", ", values)})");
                }
            }
        }
        finally
        {
            try { connection?.Close(); } catch (Exception) { }
            if (connection is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(connection);
            if (catalog is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(catalog);
        }
    }

    /// <summary>CSI's text table layout (as in .$et and .s2k files): TABLE: "name", then one line per row of Field=Value.</summary>
    public static string Text(string title, IEnumerable<ModelTable> tables) =>
        CsiTableText.Document("File " + title, tables.Select(t => CsiTableText.Table(t.Name,
            t.Rows.Select(row => (IReadOnlyList<(string Key, string Value)>)t.Fields.Select((f, i) => (f, i < row.Count ? row[i] : "")).Where(p => p.Item2.Length > 0).ToList()))));

    /// <summary>The tables as XML: one element per table with its fields' units, one Row element per row.</summary>
    public static string Xml(string title, IEnumerable<ModelTable> tables)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            w.WriteStartElement("ETABSDatabaseTables");
            w.WriteAttributeString("Model", title);
            foreach (var t in tables)
            {
                w.WriteStartElement("Table");
                w.WriteAttributeString("Name", t.Name);
                w.WriteStartElement("Fields");
                for (var i = 0; i < t.Fields.Count; i++)
                {
                    w.WriteStartElement("Field");
                    w.WriteAttributeString("Name", t.Fields[i]);
                    if (i < t.Units.Count && t.Units[i].Length > 0) w.WriteAttributeString("Units", t.Units[i]);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                foreach (var row in t.Rows)
                {
                    w.WriteStartElement("Row");
                    for (var i = 0; i < t.Fields.Count && i < row.Count; i++)
                        if (row[i].Length > 0) w.WriteAttributeString(XmlConvert.EncodeLocalName(t.Fields[i]), row[i]);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    private static JsonNode WriteExport(JsonElement a, ICsiLink e)
    {
        Offline(e);
        var path = FilePath(a, "");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!GeometryExtensions.Contains(ext)) throw new ToolInputException($"write_export_file writes only {string.Join(", ", GeometryExtensions)} files, not {(ext.Length > 0 ? ext : "a file with no extension")}.");
        var text = a.Need("text");
        Write(path, p => File.WriteAllText(p, text, new UTF8Encoding(false)));
        return new JsonObject { ["saved"] = path, ["bytes"] = new FileInfo(path).Length };
    }
}
