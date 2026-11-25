using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.CsiTables;
using CsiProMax.Model;
using CsiProMax.S2k;
using CsiProMax.Tools;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The model's tables in Excel, the way SAP2000 exports and imports them (File > Export > Excel, File > Import > Excel):
/// one sheet per table, row 1 "TABLE:  name", row 2 the field names, row 3 the units, then the data. Importing reads
/// every table in the workbook back: each one replaces what the model has of it, and tables not in the workbook stay
/// as they are. Modeler only.
/// </summary>
public static class TableTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("The model's tables go to and from Excel in the Modeler. In a CSI program, use its own File > Export and Import.");

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("model_tables",
            "The model's interactive database tables that export to Excel and import back (stories, grids, materials, sections, load patterns, combinations, points, frames, areas, assignments, supports and loads), each with its row count.",
            """{"type":"object","properties":{}}""",
            Ask.Never, (_, e) => List(Offline(e).Model)),
        new("export_tables_excel",
            "Exports the model's tables to an Excel workbook (.xlsx) the way SAP2000 does: one sheet per table with the TABLE title, field names, units and data rows. 'tables' picks some by name (default all). 'format' sap2000 writes SAP2000's own tables of the model (joints, frames, areas, as in the .s2k) instead, for SAP2000's File > Import > Excel. Default folder: the model's own folder.",
            """{"type":"object","properties":{"name":{"type":"string","description":"File name without extension. Default: the model's title and 'tables'."},"folder":{"type":"string"},"tables":{"type":"array","items":{"type":"string"}},"format":{"type":"string","enum":["model","sap2000"]}}}""",
            Ask.Always, Export, a => $"Export the model's tables to {(a.Text("name").Length > 0 ? a.Text("name") : "Excel")}.xlsx"),
        new("import_tables_excel",
            "Imports a workbook of the model's tables (as export_tables_excel writes them, edited in Excel): each table in it replaces the same table in the model; tables not in it stay as they are. Values in other units are converted. Give 'path', or 'data' (the .xlsx as base64) with 'file_name'. Nothing changes if a table can't be read; the answer names the table, the Excel row and the field.",
            """{"type":"object","properties":{"path":{"type":"string"},"data":{"type":"string","description":"The workbook's bytes, base64."},"file_name":{"type":"string"}}}""",
            Ask.Always, Import, a => $"Import the model's tables from {(a.Text("file_name").Length > 0 ? a.Text("file_name") : a.Text("path").Length > 0 ? Path.GetFileName(a.Text("path")) : "Excel")} (replaces the tables it holds)"),
    ];

    private static JsonObject List(StructuralModel m)
    {
        var counts = ModelTables.Counts(m);
        return new JsonObject
        {
            ["tables"] = new JsonArray(ModelTables.Names.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["rows"] = counts.GetValueOrDefault(n) }).ToArray()),
            ["units"] = new JsonObject { ["force"] = ModelTables.UnitText(m, "F"), ["length"] = ModelTables.UnitText(m, "L") },
        };
    }

    // ---- Excel ----

    /// <summary>Writes the tables as SAP2000 lays them out in Excel. Numbers go in as numbers.</summary>
    public static void Write(string path, IReadOnlyList<ModelTable> tables)
    {
        var sheets = tables.Select(t =>
        {
            var rows = new List<object?[]> { new object?[] { "TABLE:  " + t.Name }, t.Fields.Cast<object?>().ToArray(), t.Units.Cast<object?>().ToArray() };
            foreach (var row in t.Rows)
                rows.Add(row.Select((cell, i) => (object?)(Numeric(t, i) && double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : cell)).ToArray());
            return (t.Name, (IList<object?[]>)rows);
        });
        Xlsx.Write(path, sheets);
    }

    private static readonly HashSet<string> NumberFields = new(StringComparer.OrdinalIgnoreCase) { "SF", "SelfWtMult", "U", "NumPoints", "IStoriesBelow" };

    private static bool Numeric(ModelTable t, int column) =>
        column < t.Fields.Count && (t.Units.ElementAtOrDefault(column) is { Length: > 0 } || NumberFields.Contains(t.Fields[column]));

    /// <summary>Every table in the workbook: a sheet's "TABLE:" row, then field names, units and data down to the
    /// first empty row. A sheet may hold more than one table, one under another.</summary>
    public static List<ModelTable> Read(string path)
    {
        var tables = new List<ModelTable>();
        using var book = XlsxBook.Read(path);
        foreach (var sheet in book.SheetNames)
        {
            var grid = new SortedDictionary<int, SortedDictionary<int, string>>();
            foreach (var (col, row, text, _) in book.Cells(sheet))
            {
                if (!grid.TryGetValue(row, out var cells)) grid[row] = cells = [];
                cells[col] = text;
            }
            List<string> RowAt(int r)
            {
                if (!grid.TryGetValue(r, out var cells) || cells.Count == 0) return [];
                var list = new List<string>();
                for (var c = 0; c <= cells.Keys.Max(); c++) list.Add(cells.TryGetValue(c, out var v) ? v : "");
                return list;
            }
            var rowNumbers = grid.Keys.ToList();
            for (var k = 0; k < rowNumbers.Count; k++)
            {
                var title = RowAt(rowNumbers[k]);
                var first = title.FirstOrDefault(c => c.Trim().Length > 0)?.Trim() ?? "";
                if (!first.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase)) continue;
                var name = first[6..].Trim().Trim('"').Trim();
                var at = rowNumbers[k];
                var fields = RowAt(at + 1).Select(f => f.Trim()).ToList();
                while (fields.Count > 0 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1);
                var units = RowAt(at + 2);
                var data = new List<List<string>>();
                for (var r = at + 3; ; r++)
                {
                    var row = RowAt(r);
                    if (row.All(c => c.Trim().Length == 0)) break;
                    if (row.FirstOrDefault(c => c.Trim().Length > 0)?.Trim().StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase) == true) break;
                    data.Add(row);
                }
                tables.Add(new ModelTable(name, fields, units, data, at + 3));
            }
        }
        return tables;
    }

    /// <summary>SAP2000's own tables of the model, as the .s2k holds them, with units where the field has one.</summary>
    public static List<ModelTable> SapTables(StructuralModel m)
    {
        var parsed = CsiTableText.Parse(S2kWriter.Write(m).Text);
        var tables = new List<ModelTable>();
        foreach (var (name, rows) in parsed)
        {
            var fields = new List<string>();
            foreach (var row in rows) foreach (var key in row.Keys) if (!fields.Contains(key)) fields.Add(key);
            var units = fields.Select(f => SapUnits.TryGetValue(f, out var kind) ? ModelTables.UnitText(m, kind) : "").ToList();
            tables.Add(new ModelTable(Title(name), fields, units, rows.Select(r => fields.Select(f => r.GetValueOrDefault(f) ?? "").ToList()).ToList()));
        }
        return tables;
    }

    private static readonly Dictionary<string, string> SapUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XorR"] = "L", ["Y"] = "L", ["Z"] = "L", ["GlobalX"] = "L", ["GlobalY"] = "L", ["GlobalZ"] = "L", ["XRYZCoord"] = "L", ["AbsDistA"] = "L", ["AbsDistB"] = "L",
        ["t3"] = "L", ["t2"] = "L", ["tf"] = "L", ["tw"] = "L", ["t2b"] = "L", ["tfb"] = "L", ["Thickness"] = "L", ["BendThick"] = "L", ["Area"] = "L2", ["Length"] = "L",
        ["F1"] = "F", ["F2"] = "F", ["F3"] = "F", ["M1"] = "F-L", ["M2"] = "F-L", ["M3"] = "F-L", ["FOverLA"] = "F/L", ["FOverLB"] = "F/L", ["UnifLoad"] = "F/L2",
        ["Fc"] = "F/L2", ["E1"] = "F/L2", ["G12"] = "F/L2", ["Fy"] = "F/L2", ["Fu"] = "F/L2", ["UnitWeight"] = "F/L3",
    };

    private static string Title(string upper) =>
        string.Join(" ", upper.Split(' ').Select(w => w.Length <= 1 || w.Any(char.IsDigit) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    // ---- the tools ----

    private static string FileName(string name) =>
        new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '-' : c).ToArray()).Trim();

    private static JsonNode Export(JsonElement a, ICsiLink e)
    {
        var offline = Offline(e);
        var m = offline.Model;
        var sap = a.Text("format").Equals("sap2000", StringComparison.OrdinalIgnoreCase);
        var picked = a.TryGetProperty("tables", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
        var unknown = sap ? [] : picked.Where(p => !ModelTables.Names.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) throw new ToolInputException($"There is no table named {string.Join(", ", unknown.Select(u => "'" + u + "'"))}. Tables: {string.Join(", ", ModelTables.Names)}.");
        var tables = sap ? SapTables(m) : ModelTables.Export(m, picked);
        if (sap && picked.Count > 0) tables = tables.Where(x => picked.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (tables.Count == 0) throw new ToolInputException("No table was picked.");

        var name = a.Text("name");
        if (name.Length == 0) name = (m.Title.Length > 0 ? m.Title : "Model") + (sap ? " SAP2000 tables" : " tables");
        name = FileName(name);
        if (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var folder = a.Text("folder");
        if (folder.Length == 0) folder = offline.Path.Length > 0 && Path.GetDirectoryName(offline.Path) is { Length: > 0 } own ? own : OfflineEtabs.DefaultFolder;
        var path = Path.Combine(folder, name + ".xlsx");
        try
        {
            Directory.CreateDirectory(folder);
            Write(path, tables);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new EtabsException($"The tables could not be saved to {path}: {ex.Message}" + (ex is IOException ? " If the workbook is open in Excel, close it there first." : ""));
        }
        return new JsonObject
        {
            ["saved"] = path,
            ["tables"] = new JsonArray(tables.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["rows"] = x.Rows.Count }).ToArray()),
            ["next"] = sap
                ? "In SAP2000: File > Import > Excel Spreadsheet (.xlsx), choose this file." // SAP2000 itself
                : "Edit the rows in Excel (keep the first three rows of each sheet: the TABLE title, the field names and the units), save, then File > Import > Tables from Excel in the Modeler.",
        };
    }

    private static JsonNode Import(JsonElement a, ICsiLink e)
    {
        var offline = Offline(e);
        var path = a.Text("path").Trim().Trim('"');
        var data = a.Text("data");
        string? temp = null;
        try
        {
            if (data.Length > 0)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]); }
                catch (FormatException) { throw new ToolInputException("The workbook didn't come through (its data is not base64)."); }
                temp = Path.Combine(Path.GetTempPath(), "cpm-tables-" + Guid.NewGuid().ToString("N") + ".xlsx");
                File.WriteAllBytes(temp, bytes);
                path = temp;
            }
            if (path.Length == 0) throw new ToolInputException("Give the workbook: 'path', or 'data' with 'file_name'.");
            if (!File.Exists(path)) throw new ToolInputException($"There is no file {path}.");
            List<ModelTable> tables;
            try { tables = Read(path); }
            catch (InvalidDataException ex) { throw new ToolInputException(ex.Message); }
            catch (IOException ex) { throw new ToolInputException($"{Path.GetFileName(path)} could not be read: {ex.Message} If it is open in Excel, save it and try again."); }
            if (tables.Count == 0) throw new ToolInputException("The workbook has no tables: each sheet starts with a row 'TABLE:  name', then the field names, the units and the data (as File > Export > Tables to Excel writes them).");

            // Read into a copy, and keep it only when every table read.
            var copy = ModelFile.FromJson(ModelFile.ToJson(offline.Model));
            ModelTables.ImportResult result;
            try { result = ModelTables.Import(copy, tables); }
            catch (ArgumentException ex) { throw new ToolInputException(ex.Message + " Nothing was changed."); }
            if (result.Read.Count == 0) throw new ToolInputException("None of the workbook's tables is one the Modeler reads, so nothing was changed. " + string.Join(" ", result.Notes));
            offline.Restore(copy, offline.Path);
            return new JsonObject
            {
                ["read"] = new JsonArray(result.Read.Select(x => (JsonNode)x).ToArray()),
                ["notes"] = new JsonArray(result.Notes.Take(60).Select(x => (JsonNode)x).ToArray()),
                ["checks"] = new JsonArray(CsiProMax.Checks.ModelChecks.Run(copy).Take(20).Select(f => (JsonNode)$"{f.Severity}: {f.Message}").ToArray()),
            };
        }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
