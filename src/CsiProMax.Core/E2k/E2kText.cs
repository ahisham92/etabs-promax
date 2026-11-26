using System.Globalization;
using System.Text;
using CsiProMax.Model;

namespace CsiProMax.E2k;

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

/// <summary>
/// ETABS 23's names for a member's or shell's own property modifiers on LINEASSIGN and AREAASSIGN lines: it writes
/// "PROPMODI33 0.35", only for those not 1, where a section takes "I3MOD 0.35" (its own .$et, 2026-11-26). ETABS reads
/// none of the section names on an assignment, so the Modeler's member modifiers were lost (0.4.55 ETABS run).
/// </summary>
public static class EtabsModifiers
{
    public static readonly IReadOnlyDictionary<string, string> Frame = new Dictionary<string, string>
    {
        ["AMOD"] = "PROPMODA", ["A2MOD"] = "PROPMODA2", ["A3MOD"] = "PROPMODA3", ["JMOD"] = "PROPMODT",
        ["I2MOD"] = "PROPMODI22", ["I3MOD"] = "PROPMODI33", ["MMOD"] = "PROPMODM", ["WMOD"] = "PROPMODW",
    };

    public static readonly IReadOnlyDictionary<string, string> Area = new Dictionary<string, string>
    {
        ["F11MOD"] = "PROPMODF11", ["F22MOD"] = "PROPMODF22", ["F12MOD"] = "PROPMODF12", ["M11MOD"] = "PROPMODM11", ["M22MOD"] = "PROPMODM22",
        ["M12MOD"] = "PROPMODM12", ["V13MOD"] = "PROPMODV13", ["V23MOD"] = "PROPMODV23", ["MMOD"] = "PROPMODM", ["WMOD"] = "PROPMODW",
    };

    /// <summary>An assignment's lines with the modifiers by ETABS's names, those of 1 left out; a line left empty goes.</summary>
    public static List<List<string>> ToEtabs(List<List<string>> lines, IReadOnlyDictionary<string, string> names)
    {
        var o = new List<List<string>>();
        foreach (var l in lines)
        {
            var line = new List<string>();
            for (var i = 0; i < l.Count; i++)
            {
                if (i + 1 < l.Count && names.TryGetValue(l[i], out var etabs) && E2kText.TryNumber(l[i + 1], out var v))
                {
                    if (Math.Abs(v - 1) > 1e-12) { line.Add(etabs); line.Add(l[i + 1]); }
                    i++;
                    continue;
                }
                line.Add(l[i]);
            }
            if (line.Count > 0) o.Add(line);
        }
        return o;
    }

    /// <summary>ETABS's modifier names on an assignment line read back as the model's own (I3MOD...).</summary>
    public static void FromEtabs(List<string> tokens, IReadOnlyDictionary<string, string> names)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
            if (names.FirstOrDefault(kv => kv.Value == tokens[i]) is { Key: { } mine }) tokens[i] = mine;
    }
}

/// <summary>
/// The Modeler's auto hinges ("HINGEASSIGN label story AUTO dof RELDIST x") as ETABS 23 writes and reads them: an
/// ASCE 41-17 table item for the member's kind and material, with its own lines (ETABS 23's .$et, 2026-11-26). ETABS read
/// none of the Modeler's own form, so its frames had no hinges (0.4.55 ETABS run).
/// </summary>
public static class EtabsHinges
{
    /// <summary>The ASCE 41-17 table item for a member, and the degrees of freedom its table offers (ETABS's spelling, no hyphens).</summary>
    public static (string Item, string[] Dofs)? ItemFor(LineKind kind, bool concrete) => (kind, concrete) switch
    {
        (LineKind.Beam, true) => ("Concrete Beams", ["M2", "M3"]),
        (LineKind.Column, true) => ("Concrete Columns", ["M2", "M3", "M2M3", "PM2", "PM3", "PM2M3"]),
        (LineKind.Beam, false) => ("Steel Beams", ["M2", "M3"]),
        // Not yet seen in an ETABS file: named as ETABS names the other items.
        (LineKind.Column, false) => ("Steel Columns", ["M2", "M3", "PM2", "PM3", "PM2M3"]),
        (LineKind.Brace, false) => ("Steel Braces", ["P"]),
        _ => null,
    };

    /// <summary>The hinge lines in ETABS's form. A hinge ETABS has no table for is left out, and said in <paramref name="notes"/>.</summary>
    public static List<string> ToEtabs(StructuralModel m, IReadOnlyList<string> lines, List<string>? notes = null)
    {
        var o = new List<string>();
        var started = new HashSet<(string, string)>();
        foreach (var raw in lines)
        {
            var t = E2kText.Tokenize(raw);
            var auto = t.IndexOf("AUTO");
            var rel = t.IndexOf("RELDIST");
            if (t.Count < 3 || t[0] != "HINGEASSIGN" || auto < 0 || rel < 0 || auto + 1 >= t.Count || rel + 1 >= t.Count) { o.Add(raw); continue; }
            string label = E2kText.Unquote(t[1]), story = E2kText.Unquote(t[2]), dof = E2kText.Unquote(t[auto + 1]).Replace("-", ""), at = t[rel + 1];
            var line = m.Lines.Find(l => l.Label == label);
            var section = m.LineAssigns.Find(a => a.Label == label && a.Story == story)?.Section;
            var material = m.FrameSections.Find(s => s.Name == section)?.Material;
            var concrete = m.Materials.Find(x => x.Name == material)?.IsConcrete ?? false;
            if (line is null || ItemFor(line.Kind, concrete) is not { } item || !item.Dofs.Contains(dof))
            {
                notes?.Add($"The {E2kText.Unquote(t[auto + 1])} hinge on {label} at {story} has no ASCE 41-17 table in ETABS for a {(concrete ? "concrete" : "steel")} {line?.Kind.ToString().ToLowerInvariant() ?? "member"}, so it was left out: assign it in ETABS (Assign > Frame > Hinges).");
                continue;
            }
            string head = $"  HINGEASSIGN {E2kText.Quote(label)}  {E2kText.Quote(story)}  ";
            if (started.Add((label, story))) o.Add(head + "HINGEDISTRTYPE \"User Defined\"");
            var column = line.Kind == LineKind.Column;
            o.Add(head + $"AUTOHINGETYPE \"ASCE41-17\"  TABLEITEM {E2kText.Quote(item.Item)}  DOF {E2kText.Quote(dof)}  "
                + (column && concrete ? "COLUMNBEHAVIOR \"Not Controlled\"  " : "") + (concrete ? "STRLIMRATIO 1.1 " : "")
                + $"LOCATIONTYPE \"RelDist\"  RDISTANCE {at} LENGTHOWTYPE \"None\"  SAVEINDIVFIBER \"No\"");
            if (!column) o.Add(head + "HYSTYPE \"Isotropic\"");
        }
        return o;
    }
}
