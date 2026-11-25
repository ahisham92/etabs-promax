using System.Globalization;
using System.Text;

namespace CsiProMax.CsiTables;

/// <summary>
/// The table text that SAP2000 (.s2k) and SAFE (.f2k) read and write: a heading line per table,
/// <c>TABLE:  "NAME"</c>, then one line per row of <c>Key=Value</c> pairs, three spaces apart, and
/// <c>END TABLE DATA</c> at the end. A value with a space, a comma or a quote is written in double quotes.
/// </summary>
public static class CsiTableText
{
    /// <summary>A value as the table text writes it: quoted when it has a space, comma, equals sign or quote, or is empty.</summary>
    public static string Value(string value)
    {
        if (value.Length == 0) return "\"\"";
        return value.IndexOfAny([' ', ',', '=', '"', '\t']) >= 0 ? "\"" + value.Replace("\"", "'", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>A number as the table text writes it: invariant culture, up to 10 significant digits, no exponent for ordinary sizes.</summary>
    public static string Number(double value)
    {
        if (Math.Abs(value) < 1e-12) return "0";
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>One table: its heading and one line per row.</summary>
    public static string Table(string name, IEnumerable<IReadOnlyList<(string Key, string Value)>> rows)
    {
        var text = new StringBuilder();
        text.Append("TABLE:  \"").Append(name.ToUpperInvariant()).Append("\"\r\n");
        foreach (var row in rows)
        {
            text.Append("   ");
            text.Append(string.Join("   ", row.Select(p => p.Key + "=" + Value(p.Value))));
            text.Append("\r\n");
        }
        return text.Append("\r\n").ToString();
    }

    /// <summary>A whole file: a first line saying what wrote it, the tables, and END TABLE DATA.</summary>
    public static string Document(string firstLine, IEnumerable<string> tables)
    {
        var text = new StringBuilder();
        text.Append(firstLine).Append("\r\n\r\n");
        foreach (var table in tables) text.Append(table);
        return text.Append("END TABLE DATA\r\n").ToString();
    }

    /// <summary>
    /// Reads table text back: each table by its heading (upper case) with its rows as key to value, in order. Lines
    /// that end in " _" carry on to the next line. Values come back without their quotes.
    /// </summary>
    public static Dictionary<string, List<Dictionary<string, string>>> Parse(string text)
    {
        var tables = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, string>>? current = null;
        var pending = new StringBuilder();
        foreach (var raw in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.EndsWith(" _", StringComparison.Ordinal)) { pending.Append(line[..^2]).Append(' '); continue; }
            line = pending.Append(line).ToString().Trim();
            pending.Clear();
            if (line.Length == 0 || line.StartsWith('$')) continue;
            if (line.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[6..].Trim().Trim('"');
                if (!tables.TryGetValue(name, out current)) tables[name] = current = [];
                continue;
            }
            if (line.StartsWith("END TABLE DATA", StringComparison.OrdinalIgnoreCase)) break;
            current?.Add(Pairs(line));
        }
        return tables;
    }

    /// <summary>The Key=Value pairs of one row line; a quoted value may hold spaces.</summary>
    public static Dictionary<string, string> Pairs(string line)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                var close = line.IndexOf('"', i + 1);
                if (close < 0) close = line.Length;
                value = line[(i + 1)..close];
                i = close + 1;
            }
            else
            {
                var end = i;
                while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
                value = line[i..end];
                i = end;
            }
            if (key.Length > 0) row[key] = value;
        }
        return row;
    }
}
