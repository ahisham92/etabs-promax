using System.Globalization;
using System.Text;

namespace EtabsProMax.E2k;

/// <summary>The words of an ETABS text file line: split on spaces, with "quoted text" kept as one word (quotes included,
/// so a line can be written back exactly).</summary>
public static class E2kText
{
    public static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            var start = i;
            if (line[i] == '"')
            {
                i = line.IndexOf('"', i + 1);
                i = i < 0 ? line.Length : i + 1;
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            }
            tokens.Add(line[start..i]);
        }
        return tokens;
    }

    public static string Quote(string text) => "\"" + text.Replace("\"", "'") + "\"";

    public static string Unquote(string token) =>
        token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token[1..^1] : token.TrimStart('"');

    public static bool IsQuoted(string token) => token.Length > 0 && token[0] == '"';

    /// <summary>A number as ETABS writes it: a point for decimals, no thousands separators, no trailing zeros.</summary>
    public static string Number(double value) =>
        value == 0 ? "0" : value.ToString("0.############", CultureInfo.InvariantCulture);

    public static bool TryNumber(string? token, out double value) =>
        double.TryParse(token is null ? null : Unquote(token), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static double ToNumber(string token, string line) =>
        TryNumber(token, out var value) ? value : throw new E2kFormatException($"'{token}' is not a number in: {line.Trim()}");

    /// <summary>The value after a keyword (the first unquoted word equal to it), unquoted; null when it isn't there.</summary>
    public static string? Value(IReadOnlyList<string> tokens, string key)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i] == key) return Unquote(tokens[i + 1]);
        return null;
    }

    /// <summary>Like <see cref="Value"/>, and removes the keyword and its value from the list.</summary>
    public static string? Take(List<string> tokens, string key)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] != key) continue;
            var value = Unquote(tokens[i + 1]);
            tokens.RemoveRange(i, 2);
            return value;
        }
        return null;
    }

    public static string Line(params IEnumerable<string>[] parts)
    {
        var text = new StringBuilder(" ");
        foreach (var part in parts)
            foreach (var token in part) text.Append(' ').Append(' ').Append(token);
        return text.ToString(1, text.Length - 1);
    }
}

public sealed class E2kFormatException(string message) : Exception(message);
