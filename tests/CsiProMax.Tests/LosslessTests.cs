using System.Globalization;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>ETABS in and out with nothing lost (Ahmed 2026-11-27: "it should be nothing lost whenever we get into etabs or
/// out ... it should be exactly the same"). Every ETABS file among the samples is read and written back, also through
/// the .cpm, through a units change and back, and twice; every word and number of every section must still be there.
/// samples/etabs23-stress-lossless.e2k holds the forms the other samples do not: several grid systems, words after a
/// point's, line's, area's or diaphragm's own values, null lines and areas, a number of 1.2E-10.</summary>
static class LosslessTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    static IEnumerable<string> EtabsSamples() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.e2k").Select(Path.GetFileName).OrderBy(n => n)!;

    // A number by its value (9 significant digits), so "4" and "4.0" are one word; text as written.
    static string Word(string token)
    {
        var u = token.Trim('"');
        return double.TryParse(u, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (d == 0 ? "0" : d.ToString("G9", CultureInfo.InvariantCulture)) : token;
    }

    /// <summary>Each section's words, counted, by the keyword of their line. An assignment's name and story are counted
    /// once per object (ETABS may spread one object's assignment over several lines; it is written on fewer).</summary>
    public static Dictionary<string, Dictionary<string, int>> Words(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, int>>();
        var heading = "";
        var seen = new HashSet<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.TrimStart().StartsWith('$'))
            {
                heading = line.TrimStart()[1..].Trim();
                if (heading.StartsWith("File ", StringComparison.OrdinalIgnoreCase)) heading = "";
                continue;
            }
            if (line.Trim().Length == 0 || heading.Length == 0) continue;
            if (!sections.TryGetValue(heading, out var words)) sections[heading] = words = [];
            var t = E2kText.Tokenize(line);
            var skip = 0;
            if (t[0].EndsWith("ASSIGN", StringComparison.Ordinal) && t.Count >= 3 && !seen.Add(heading + "|" + t[0] + "|" + t[1] + "|" + t[2])) skip = 3;
            foreach (var w in t.Skip(skip)) { var k = t[0] + " " + Word(w); words[k] = words.GetValueOrDefault(k) + 1; }
        }
        return sections;
    }

    /// <summary>What of <paramref name="before"/> is not in <paramref name="after"/>, as "[section] keyword word xN".</summary>
    public static List<string> Lost(string before, string after)
    {
        var a = Words(before); var b = Words(after);
        var lost = new List<string>();
        foreach (var (heading, words) in a)
        {
            var other = b.GetValueOrDefault(heading) ?? [];
            foreach (var (w, n) in words)
                if (other.GetValueOrDefault(w) < n) lost.Add($"[{heading}] {w} x{n - other.GetValueOrDefault(w)}");
        }
        return lost;
    }

    static void Same(string name, string before, StructuralModel model, string how)
    {
        // A table set's ETABS-internal "~LLRF" lines are left out on purpose: ETABS 23.2 cannot read them back (KBC 2009 Ex002).
        before = string.Join("\n", before.Split('\n').Where(l => !E2kWriter.InternalTableSetName(l.TrimEnd('\r'))));
        var lost = Lost(before, E2kWriter.Write(model));
        Check(lost.Count == 0, $"{name} {how}: {lost.Count} word(s) lost: {string.Join(" | ", lost.Take(12))}");
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("lossless: every ETABS sample is written back with every word and number of every section", () =>
        {
            var names = EtabsSamples().ToList();
            Check(names.Count >= 8 && names.Contains("etabs23-stress-lossless.e2k"), "the ETABS samples: " + string.Join(", ", names));
            foreach (var name in names) { var text = Sample(name); Same(name, text, E2kReader.Read(text), "read and written"); }
        });

        yield return ("lossless: through the Modeler's own file (.cpm), read again from its own .e2k, and through a units change and back", () =>
        {
            foreach (var name in EtabsSamples())
            {
                var text = Sample(name);
                Same(name, text, ModelFile.FromJson(ModelFile.ToJson(E2kReader.Read(text))), "through the .cpm");
                Same(name, text, E2kReader.Read(E2kWriter.Write(E2kReader.Read(text))), "written, read and written again");
                var m = E2kReader.Read(text);
                var own = m.Units;
                var shown = ModelUnits.Convert(m, new Units { Force = "kip", Length = "ft", Temperature = "F" });
                Same(name, text, ModelUnits.Convert(shown, own), "shown in kip, ft and saved in its own units");
            }
        });

        yield return ("lossless: a model's other grid systems keep their own lines, and a cylindrical system's angles do not change with the units", () =>
        {
            var m = E2kReader.Read(Sample("etabs23-stress-lossless.e2k"));
            Check(m.GridSystem == "G1" && m.MoreGridSystems.Count == 1 && m.MoreGridSystems[0].Name == "G2", "two grid systems: " + m.GridSystem + ", " + string.Join(", ", m.MoreGridSystems.Select(s => s.Name)));
            Check(m.Grids.Count(g => g.System is null) == 2 && m.Grids.Count(g => g.System == "G2") == 2, "each system keeps its lines");
            var text = E2kWriter.Write(m);
            Check(text.Contains("GRID  \"G2\"  LABEL  \"R1\"") && text.Contains("GRID  \"G1\"  LABEL  \"A\""), "each line is written under its own system:\n" + text);
            Check(text.IndexOf("GRIDSYSTEM  \"G2\"", StringComparison.Ordinal) < text.IndexOf("GRID  \"G2\"", StringComparison.Ordinal), "a system is written before its lines");
            var feet = ModelUnits.Convert(m, new Units { Force = "kip", Length = "ft", Temperature = "F" });
            Check(Math.Abs(feet.Grids.Single(g => g.Label == "T1").Coordinate - 30) < 1e-9, "30 degrees stay 30 degrees");
            Check(Math.Abs(feet.Grids.Single(g => g.Label == "R1").Coordinate - 5 / 0.3048) < 1e-6, "a radius is a length");
            var origin = feet.MoreGridSystems[0].Extra;
            var at = origin.IndexOf("ORIGIN");
            Check(Math.Abs(double.Parse(origin[at + 1], CultureInfo.InvariantCulture) - 10 / 0.3048) < 1e-6 && Math.Abs(double.Parse(origin[at + 2], CultureInfo.InvariantCulture) - 20 / 0.3048) < 1e-6, "the origin is in feet: " + string.Join(" ", origin));
        });

        yield return ("lossless: numbers go back as read, a very small one too; a line no version knows is kept, never the whole file refused", () =>
        {
            Check(E2kText.Number(1.23456789012345E-10) == (1.23456789012345E-10).ToString("R", CultureInfo.InvariantCulture), "1.2E-10: " + E2kText.Number(1.23456789012345E-10));
            Check(E2kText.Number(0.1 + 0.2) == "0.3" && E2kText.Number(4) == "4" && E2kText.Number(-2.5) == "-2.5", "plain numbers as before");
            var text = Sample("etabs23-stress-lossless.e2k").Replace("  POINT \"1\"  0  0 ", "  POINT \"1\"  0  0 \n  POINT \"bad\"  zero  0\n  AREA \"F9\"  FLOOR  4  \"1\"  \"2\"");
            var m = E2kReader.Read(text);
            Check(m.Points.Exists(p => p.Name == "1") && !m.Points.Exists(p => p.Name == "bad"), "the good points are read");
            var back = E2kWriter.Write(m);
            Check(back.Contains("POINT \"bad\"  zero  0") && back.Contains("AREA \"F9\"  FLOOR  4  \"1\"  \"2\""), "lines it could not read go back as they were");
        });

        yield return ("lossless: a model of 20,000 points, frames and areas, each with two assignment lines, reads in one pass", () =>
        {
            var text = new System.Text.StringBuilder("$ PROGRAM INFORMATION\n  PROGRAM \"ETABS\" VERSION \"23.2.0\"\n$ CONTROLS\n  UNITS \"KN\" \"M\" \"C\"\n$ STORIES - IN SEQUENCE FROM TOP\n  STORY \"S1\" HEIGHT 3\n  STORY \"Base\" ELEV 0\n$ POINT COORDINATES\n");
            for (var i = 0; i < 20000; i++) text.Append($"  POINT \"{i}\" {i} 0\n");
            text.Append("$ LINE CONNECTIVITIES\n");
            for (var i = 0; i < 20000; i++) text.Append($"  LINE \"B{i}\" BEAM \"{i}\" \"{(i + 1) % 20000}\" 0\n");
            text.Append("$ POINT ASSIGNS\n");
            for (var i = 0; i < 20000; i++) text.Append($"  POINTASSIGN \"{i}\" \"S1\" DIAPH \"D1\"\n  POINTASSIGN \"{i}\" \"S1\" SPRINGPROP \"P\"\n");
            text.Append("$ LINE ASSIGNS\n");
            for (var i = 0; i < 20000; i++) text.Append($"  LINEASSIGN \"B{i}\" \"S1\" SECTION \"B\"\n  LINEASSIGN \"B{i}\" \"S1\" MINNUMSTA 3\n");
            text.Append("$ END OF MODEL FILE\n");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var m = E2kReader.Read(text.ToString());
            Check(watch.Elapsed.TotalSeconds < 10, $"read in {watch.Elapsed.TotalSeconds:0.0} s");
            Check(m.PointAssigns.Count == 20000 && m.LineAssigns.Count == 20000 && m.PointAssigns[7].Extra.Count == 1, "one assignment per object, every line kept");
            Check(Lost(text.ToString(), E2kWriter.Write(m)).Count == 0, "nothing lost");
        });
    }
}
