using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CsiProMax.Tools;

/// <summary>A minimal .xlsx writer: sheets of text, numbers and formulas, header row in bold and frozen. Pure, so the tests run it.</summary>
internal static class Xlsx
{
    /// <summary>
    /// A formula cell (without the leading =), with the value it gives now so readers that don't calculate (and the
    /// CSV) still see it; Excel recalculates on open. Cached is a number or text.
    /// </summary>
    public sealed record Formula(string Expression, object? Cached);

    public static void Write(string path, IEnumerable<(string Name, IList<object?[]> Rows)> sheets)
    {
        var list = sheets.ToList();
        if (File.Exists(path)) File.Delete(path);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        var names = new List<string>();
        foreach (var s in list)
        {
            var n = new string(s.Name.Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray()).Trim();
            if (n.Length == 0) n = "Sheet";
            if (n.Length > 31) n = n.Substring(0, 31);
            var unique = n;
            for (var i = 2; names.Contains(unique, StringComparer.OrdinalIgnoreCase); i++) unique = n.Substring(0, Math.Min(n.Length, 28)) + " " + i;
            names.Add(unique);
        }
        Entry("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            string.Concat(names.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
            "</Types>");
        Entry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Entry("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
            string.Concat(names.Select((n, i) => $"<sheet name=\"{Esc(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets>" +
            (list.Any(x => x.Rows.Any(r => r.Any(c => c is Formula))) ? "<calcPr fullCalcOnLoad=\"1\"/>" : "") + "</workbook>");
        Entry("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
            $"<Relationship Id=\"rId{names.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Entry("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            // 2 and 3: text that looks like a formula (=, +, -, @), kept as text even when the cell is edited (Excel's ' prefix).
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" quotePrefix=\"1\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" quotePrefix=\"1\"/></cellXfs>" +
            "</styleSheet>");
        for (var i = 0; i < list.Count; i++)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            var columns = list[i].Rows.Count == 0 ? 0 : list[i].Rows.Max(r => r.Length);
            if (columns > 0) sb.Append($"<cols><col min=\"1\" max=\"{columns}\" width=\"18\" customWidth=\"1\"/></cols>");
            sb.Append("<sheetData>");
            for (var r = 0; r < list[i].Rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                var row = list[i].Rows[r];
                for (var c = 0; c < row.Length; c++)
                {
                    var cell = row[c];
                    if (cell is null) continue;
                    var at = Column(c) + (r + 1);
                    var style = r == 0 ? " s=\"1\"" : "";
                    if (cell is Formula fm)
                    {
                        var expr = Esc(fm.Expression.TrimStart('='));
                        if (fm.Cached is double or float or int or long or decimal && Convert.ToDouble(fm.Cached, CultureInfo.InvariantCulture) is var fv && !double.IsNaN(fv) && !double.IsInfinity(fv))
                            sb.Append($"<c r=\"{at}\"{style}><f>{expr}</f><v>{fv.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        else sb.Append($"<c r=\"{at}\"{style} t=\"str\"><f>{expr}</f><v>{Esc(Convert.ToString(fm.Cached, CultureInfo.InvariantCulture) ?? "")}</v></c>");
                    }
                    else if (cell is double or float or int or long or decimal)
                    {
                        var d = Convert.ToDouble(cell, CultureInfo.InvariantCulture);
                        if (double.IsNaN(d) || double.IsInfinity(d)) continue;
                        sb.Append($"<c r=\"{at}\"{style}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        // Text from the model or a file is never a formula: an inline string is not calculated, and one that
                        // starts like a formula also gets the quote-prefix style, so editing the cell doesn't turn it into one.
                        var text = Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "";
                        if (FormulaLike(text)) style = r == 0 ? " s=\"3\"" : " s=\"2\"";
                        sb.Append($"<c r=\"{at}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            Entry($"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
        }
    }

    /// <summary>
    /// The cells of one sheet (default the first, in the workbook's tab order) as text, row by row: shared strings, inline
    /// strings, numbers and the cached value of formulas. Gaps are filled with empty cells (rows Excel left out as empty
    /// are not returned). Throws InvalidDataException when the file is not a workbook that can be read.
    /// </summary>
    public static List<List<string>> Read(string path, int sheet = 1)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var shared = new List<string>();
            if (Entry(zip, "xl/sharedStrings.xml") is { } ss)
                using (var st = ss.Open())
                    shared = System.Xml.Linq.XDocument.Load(st).Root!.Elements(ns + "si")
                        .Select(si => string.Concat(si.Descendants(ns + "t").Where(x => !x.Ancestors(ns + "rPh").Any()).Select(x => x.Value))).ToList();
            var entry = SheetEntry(zip, sheet) ?? throw new InvalidDataException($"The workbook has no sheet {sheet}.");
            using var stream = entry.Open();
            var rows = new List<List<string>>();
            foreach (var row in System.Xml.Linq.XDocument.Load(stream).Descendants(ns + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(ns + "c"))
                {
                    var col = 0;
                    foreach (var ch in ((string?)c.Attribute("r") ?? "").ToUpperInvariant().TakeWhile(ch => ch is >= 'A' and <= 'Z'))
                        col = Math.Min(MaxColumns + 1, col * 26 + (ch - 'A' + 1));
                    if (col > MaxColumns) continue; // past Excel's last column: not a real cell
                    while (col > 0 && cells.Count < col - 1) cells.Add("");
                    var value = c.Element(ns + "v")?.Value ?? string.Concat(c.Descendants(ns + "t").Where(x => !x.Ancestors(ns + "rPh").Any()).Select(x => x.Value));
                    if ((string?)c.Attribute("t") == "s")
                        value = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
                    if (col > 0 && cells.Count >= col) cells[col - 1] = value; // a repeated reference: the last one wins
                    else cells.Add(value);
                }
                rows.Add(cells);
            }
            return rows;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or NullReferenceException or NotSupportedException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} can't be read as an Excel workbook ({ex.Message}). Save it again from Excel as .xlsx, or as CSV.", ex);
        }
    }

    private const int MaxColumns = 16384;

    private static System.IO.Compression.ZipArchiveEntry? Entry(System.IO.Compression.ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => e.FullName.TrimStart('/').Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The worksheet part of the nth sheet in tab order (workbook.xml and its relationships), else sheetN.xml.</summary>
    private static System.IO.Compression.ZipArchiveEntry? SheetEntry(System.IO.Compression.ZipArchive zip, int sheet)
    {
        try
        {
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            System.Xml.Linq.XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            System.Xml.Linq.XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
            if (Entry(zip, "xl/workbook.xml") is { } wb && Entry(zip, "xl/_rels/workbook.xml.rels") is { } rels)
            {
                string? id;
                using (var st = wb.Open())
                    id = (string?)System.Xml.Linq.XDocument.Load(st).Descendants(ns + "sheet").Skip(sheet - 1).FirstOrDefault()?.Attribute(rel + "id");
                if (id is not null)
                {
                    string? target;
                    using (var st = rels.Open())
                        target = (string?)System.Xml.Linq.XDocument.Load(st).Root?.Elements(pkg + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == id)?.Attribute("Target");
                    if (target is not null)
                    {
                        var name = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
                        if (Entry(zip, name.Replace("/./", "/")) is { } found) return found;
                    }
                }
            }
        }
        catch (System.Xml.XmlException) { /* fall back to the usual part name */ }
        return Entry(zip, $"xl/worksheets/sheet{sheet}.xml");
    }

    /// <summary>
    /// Text a spreadsheet would read as a formula: it starts with =, +, -, @ (or a tab or return before one). A plain
    /// number such as -12.5 is not.
    /// </summary>
    public static bool FormulaLike(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var t = s!.TrimStart('\t', '\r', '\n', ' ');
        if (t.Length == 0 || "=+-@".IndexOf(t[0]) < 0) return false;
        return !(t.Length == s.Length && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
    }

    /// <summary>A, B, ... Z, AA, AB ... for a 0-based column.</summary>
    public static string Column(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '&') sb.Append("&amp;");
            else if (ch == '<') sb.Append("&lt;");
            else if (ch == '>') sb.Append("&gt;");
            else if (ch == '"') sb.Append("&quot;");
            else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
