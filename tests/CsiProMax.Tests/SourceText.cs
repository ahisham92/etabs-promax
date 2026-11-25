using System.Text;

/// <summary>
/// The texts written in the source: every string a C# file holds, with the file and line it starts on. For the tests
/// that hold a rule on what the program says (no shared text names ETABS). It reads the C# well enough for that:
/// plain, verbatim, interpolated and raw strings, with the code inside an interpolation left out (and the strings
/// inside that code read in their turn), and comments and character constants passed over.
/// </summary>
static class SourceText
{
    public sealed record Literal(string File, int Line, string Text, string Opening);

    /// <summary>The repository's folder: the one above the tests' output that holds CsiProMax.sln.</summary>
    public static string Root()
    {
        var at = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(at, "CsiProMax.sln")))
            at = Path.GetDirectoryName(at.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new Exception("CsiProMax.sln was not found above " + AppContext.BaseDirectory);
        return at;
    }

    /// <summary>The C# files under a folder of the repository (not what a build wrote), with their path from the root in forward slashes.</summary>
    public static IEnumerable<(string Path, string Text)> Files(string folder)
    {
        var root = Root();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
            yield return (relative, File.ReadAllText(file));
        }
    }

    public static List<Literal> Literals(string file, string text)
    {
        var found = new List<Literal>();
        var lines = text.Split('\n');
        var line = 1;
        var i = 0;
        Code(text, ref i, ref line, false, found, file, lines);
        return found;
    }

    private static string LineOf(string[] lines, int line) => line - 1 < lines.Length ? lines[line - 1].TrimEnd('\r') : "";

    /// <summary>Code up to the end, or up to the brace that closes an interpolation.</summary>
    private static void Code(string s, ref int i, ref int line, bool hole, List<Literal> found, string file, string[] lines)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                i += 2; continue;
            }
            if (c == '\'')
            {
                // A character constant: 'x', '\n', '\'' or '"'.
                var end = i + 1;
                if (end < s.Length && s[end] == '\\') end += 2; else end++;
                while (end < s.Length && s[end] != '\'' && end - i < 9) end++;
                i = end + 1; continue;
            }
            if (hole)
            {
                if (c == '{' || c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == '}') { if (depth == 0) return; depth--; }
            }
            if (c == '"' || ((c == '$' || c == '@') && Opens(s, i)))
            {
                var dollars = 0; var verbatim = false;
                while (s[i] != '"') { if (s[i] == '$') dollars++; else verbatim = true; i++; }
                var quotes = 0;
                while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
                var start = line;
                if (quotes >= 3 && !verbatim) { i += quotes; found.Add(new(file, start, Raw(s, ref i, ref line, quotes, dollars, found, file, lines), LineOf(lines, start))); }
                else { i++; found.Add(new(file, start, Plain(s, ref i, ref line, dollars > 0, verbatim, found, file, lines), LineOf(lines, start))); }
                continue;
            }
            i++;
        }
    }

    /// <summary>True where $ or @ starts a string: $", @", $@", @$", $$""".</summary>
    private static bool Opens(string s, int i)
    {
        var at = i;
        while (at < s.Length && (s[at] == '$' || s[at] == '@')) at++;
        return at < s.Length && s[at] == '"' && (i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_'));
    }

    private static string Plain(string s, ref int i, ref int line, bool interpolated, bool verbatim, List<Literal> found, string file, string[] lines)
    {
        var text = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n') line++;
            if (verbatim && c == '"' && i + 1 < s.Length && s[i + 1] == '"') { text.Append('"'); i += 2; continue; }
            if (c == '"') { i++; break; }
            if (!verbatim && c == '\\' && i + 1 < s.Length)
            {
                // An escape is not a letter of the text: \n before a word must not join it.
                text.Append(s[i + 1] is '"' or '\'' or '\\' ? s[i + 1] : ' ');
                i += 2; continue;
            }
            if (interpolated && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { text.Append('{'); i += 2; continue; }
                i++;
                Code(s, ref i, ref line, true, found, file, lines);
                i++; text.Append(' '); continue;
            }
            if (interpolated && c == '}' && i + 1 < s.Length && s[i + 1] == '}') { text.Append('}'); i += 2; continue; }
            text.Append(c); i++;
        }
        return text.ToString();
    }

    private static string Raw(string s, ref int i, ref int line, int quotes, int dollars, List<Literal> found, string file, string[] lines)
    {
        var text = new StringBuilder();
        var close = new string('"', quotes);
        var open = new string('{', Math.Max(dollars, 1));
        while (i < s.Length)
        {
            if (string.CompareOrdinal(s, i, close, 0, quotes) == 0) { i += quotes; break; }
            if (s[i] == '\n') line++;
            if (dollars > 0 && string.CompareOrdinal(s, i, open, 0, dollars) == 0 && !(i + dollars < s.Length && s[i + dollars] == '{'))
            {
                i += dollars;
                Code(s, ref i, ref line, true, found, file, lines);
                i += dollars; text.Append(' '); continue;
            }
            text.Append(s[i]); i++;
        }
        return text.ToString();
    }
}
