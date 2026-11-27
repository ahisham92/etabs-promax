using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CsiProMax.Assistant.Safe;

/// <summary>
/// Which .f2k a SAFE opens through its API with its objects. SAFE 23.1 (0.4.56) and SAFE 22.7 (0.4.66, tester's PCs)
/// answered 0 to File.OpenFile on the Modeler's .f2k and held an empty model; nobody at hand has SAFE to try why. When
/// that happens the field test opens a few forms of the same file, and SAFE's own export as the control, and the report
/// says which held objects: SAFE's own export empty too means its File.OpenFile takes no .f2k at all (the file is then
/// not the fault); one form full and the Modeler's not names what the writer must change.
/// </summary>
public static class F2kProbe
{
    /// <summary>The control: SAFE 23.1's own export of the RC-PN example (lb, in), as a tester's PC wrote it (2026-11-26).</summary>
    public const string ControlResource = "CsiProMax.safe-own-export.f2k";

    /// <summary>The forms tried, each with its name and its text, from the Modeler's file and the running SAFE's version.</summary>
    public static IReadOnlyList<(string Name, string Text)> Forms(string modelerText, string safeVersion)
    {
        var level = Standard(modelerText);
        var versioned = AtVersion(level, safeVersion);
        var header = SafeHeader(versioned);
        return
        [
            ("ProgLevel Standard", level),
            ($"Version {safeVersion} and ProgLevel Standard", versioned),
            ("as SAFE writes its own (first line, version, level)", header),
            ("only the slabs, their points and properties", CoreOnly(header)),
        ];
    }

    /// <summary>SAFE's own export, the control, from the assembly; null when it is not in it.</summary>
    public static string? Control()
    {
        using var stream = typeof(F2kProbe).Assembly.GetManifestResourceStream(ControlResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>PROGRAM CONTROL's ProgLevel set to Standard (SAFE 23.1's own PT export says Standard).</summary>
    public static string Standard(string text) => Control(text, "ProgLevel", "Standard");

    /// <summary>PROGRAM CONTROL's Version set to the running SAFE's.</summary>
    public static string AtVersion(string text, string version) => version.Length == 0 ? text : Control(text, "Version", version);

    /// <summary>The first line as SAFE writes its own ("File ... saved ..."), and a line with one space between the tables.</summary>
    public static string SafeHeader(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && !lines[0].StartsWith("TABLE:", StringComparison.Ordinal))
            lines[0] = $"File C:\\CSI ProMax\\cpm-field-test.fdb saved {DateTime.Now.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture)}";
        for (var i = 0; i < lines.Count; i++) if (lines[i].Length == 0) lines[i] = " ";
        return string.Join("\r\n", lines).TrimEnd() + "\r\n";
    }

    /// <summary>The tables a slab needs and nothing else: control, grids, materials, slab properties, points, floors, their properties, load patterns.</summary>
    public static string CoreOnly(string text)
    {
        var keep = new[] { "PROGRAM CONTROL", "GRID DEFINITIONS", "MATERIAL PROPERTIES", "AREA SECTION PROPERTY DEFINITIONS", "SLAB PROPERTY DEFINITIONS",
            "LOAD PATTERN DEFINITIONS", "POINT OBJECT CONNECTIVITY", "FLOOR OBJECT CONNECTIVITY", "AREA ASSIGNMENTS - SECTION PROPERTIES" };
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder();
        var keeping = true;
        foreach (var line in lines)
        {
            if (line.StartsWith("TABLE:", StringComparison.Ordinal))
            {
                var name = line[6..].Trim().Trim('"');
                keeping = keep.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            }
            else if (line.StartsWith("END TABLE DATA", StringComparison.Ordinal)) { output.Append(line).Append("\r\n"); break; } // nor the Modeler's own data after it
            if (keeping) output.Append(line).Append("\r\n");
        }
        return output.ToString().TrimEnd() + "\r\n";
    }

    private static string Control(string text, string key, string value)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].StartsWith("TABLE:", StringComparison.Ordinal) || !lines[i].Contains("\"PROGRAM CONTROL\"", StringComparison.OrdinalIgnoreCase)) continue;
            var quoted = value.Contains(' ') ? "\"" + value + "\"" : value;
            lines[i + 1] = Regex.Replace(lines[i + 1], $"(?<=(^|\\s)){key}=(\"[^\"]*\"|\\S+)", $"{key}={quoted}");
            break;
        }
        return string.Join("\r\n", lines);
    }
}
