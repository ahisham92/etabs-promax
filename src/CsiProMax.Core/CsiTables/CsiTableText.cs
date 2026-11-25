using System.Globalization;
using System.Text;

namespace CsiProMax.CsiTables;

/// <summary>
/// The table text that SAP2000 (.s2k), SAFE (.f2k) and CSiBridge (.b2k) read and write: a header line, then tables,
/// each a <c>TABLE:  "NAME"</c> line followed by one line per row of <c>Key=Value</c> pairs, and <c>END TABLE DATA</c>.
/// A long row may go on over several lines that end in " _". Values with a space, comma, equals sign or quote, and
/// empty values, are quoted. Numbers are written with a decimal point whatever the Windows language.
/// </summary>
public static class CsiTableText
{
    private const string Gap = "   ";

    public static string Value(string text)
    {
        if (text.Length == 0) return "\"\"";
        return text.IndexOfAny([' ', ',', '=', '"', '\t']) >= 0 ? "\"" + text.Replace("\"", "'") + "\"" : text;
    }

    public static string Number(double value)
    {
        if (Math.Abs(value) < 1e-12) return "0";
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>One table: its heading (upper-cased) and one line per row, ended by a blank line. Values are given as they
    /// should read; text that needs quotes is quoted here.</summary>
    public static string Table(string name, IEnumerable<IReadOnlyList<(string Key, string Value)>> rows)
    {
        var text = new StringBuilder();
        text.Append("TABLE:  \"").Append(name.ToUpperInvariant()).Append("\"\r\n");
        foreach (var row in rows)
        {
            text.Append(Gap);
            text.Append(string.Join(Gap, row.Select(p => p.Key + "=" + Value(p.Value))));
            text.Append("\r\n");
        }
        text.Append("\r\n");
        return text.ToString();
    }

    /// <summary>A whole file: the header line, the tables, and END TABLE DATA.</summary>
    public static string Document(string firstLine, IEnumerable<string> tables)
    {
        var text = new StringBuilder();
        text.Append(firstLine).Append("\r\n \r\n");
        foreach (var table in tables) text.Append(table);
        text.Append("END TABLE DATA\r\n");
        return text.ToString();
    }

    /// <summary>Reads table text back: each table (by name, any case) to its rows, each row its keys to values (unquoted).</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> Parse(string text)
    {
        var tables = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, string>>? rows = null;
        var pending = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (pending.Length > 0 || line.EndsWith(" _", StringComparison.Ordinal))
            {
                if (line.EndsWith(" _", StringComparison.Ordinal)) { pending.Append(line[..^2]).Append(' '); continue; }
                pending.Append(line);
                line = pending.ToString();
                pending.Clear();
            }
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase))
            {
                var name = trimmed[6..].Trim().Trim('"');
                if (!tables.TryGetValue(name, out rows)) tables[name] = rows = [];
                continue;
            }
            if (trimmed.StartsWith("END TABLE DATA", StringComparison.OrdinalIgnoreCase)) { rows = null; continue; }
            if (rows is null) continue;
            var pairs = Pairs(trimmed);
            if (pairs.Count > 0) rows.Add(pairs);
        }
        return tables;
    }

    /// <summary>The Key=Value pairs of one row line; quoted values may hold spaces.</summary>
    public static Dictionary<string, string> Pairs(string line)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            var eq = line.IndexOf('=', i);
            if (eq < 0) break;
            var key = line[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < line.Length && line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0) end = line.Length;
                value = line[(i + 1)..end];
                i = Math.Min(line.Length, end + 1);
            }
            else
            {
                var end = i;
                while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
                value = line[i..end];
                i = end;
            }
            if (key.Length > 0) pairs[key] = value;
        }
        return pairs;
    }
}
