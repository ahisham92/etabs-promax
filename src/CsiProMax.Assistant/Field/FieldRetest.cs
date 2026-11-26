using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test that does not repeat what already passed (Ahmed, 2026-11-25: "ignore for testing things that passed
/// and just check the new things in addition to the things failed or not tested"). The steps of one program's part
/// build on each other (the model, its analysis, its design), so the choice is made by part: ETABS, SAP2000, SAFE on
/// their scratch models, and CSI ProMax's own features. A part is run again when it has never passed on this PC, when a
/// step of it failed last time, or when its code changed since the version it passed on (new tools and their steps
/// change it). A part left out is named in the report with the version it passed on, and the steps of it that could not
/// be tested are listed again with their reasons.
/// </summary>
public static class FieldRetest
{
    /// <summary>The part of CSI ProMax's own features (the chat's tools, the files it makes), run on one scratch window.</summary>
    public const string Panel = "panel";

    /// <summary>What one part came to the last time it ran on this PC.</summary>
    public sealed record Part(string Version, string Print, int Passed, int Failed, IReadOnlyList<(string Step, string Why)> NotTested);

    /// <summary>The file with every part's last result on this PC.</summary>
    public static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "Field test", "last-results.json");

    /// <summary>The source files whose change means a part must be tried again, by the start of their path.</summary>
    private static readonly Dictionary<string, string[]> Sources = new()
    {
        ["etabs"] = ["Etabs/", "Tools/", "Field/ScratchExtras.cs", "Field/EtabsFieldSteps.cs", "Field/CsiFieldSteps.cs", "Field/ViewerCheck.cs", "Field/GeoProMaxFieldSteps.cs"],
        ["sap2000"] = ["Sap2000/", "Etabs/", "Tools/", "Field/ScratchExtras.cs", "Field/CsiFieldSteps.cs", "Field/ViewerCheck.cs"],
        ["safe"] = ["Safe/", "Etabs/", "Tools/", "Field/CsiFieldSteps.cs", "Field/ViewerCheck.cs", "Field/GeoProMaxFieldSteps.cs"],
        [Panel] = ["Panel/", "Field/PanelFieldSteps.cs"],
    };

    private static Dictionary<string, string>? _files;

    /// <summary>Each source file of this build with the hash of its text, written into CsiProMax.dll when it was built.</summary>
    public static IReadOnlyDictionary<string, string> Files()
    {
        if (_files is not null) return _files;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = typeof(FieldRetest).Assembly.GetManifestResourceStream("CsiProMax.source-prints.txt");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (line.Split('|') is [var path, var hash] && path.Length > 0) files[path.Replace('\\', '/')] = hash;
        }
        return _files = files;
    }

    /// <summary>One value for the code of a part: it changes when any of the part's source files does. Empty when the build carries no hashes.</summary>
    public static string Print(string part, IReadOnlyDictionary<string, string>? files = null)
    {
        files ??= Files();
        if (!Sources.TryGetValue(part, out var starts)) return "";
        var mine = files.Where(f => starts.Any(s => f.Key.StartsWith(s, StringComparison.OrdinalIgnoreCase))).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (mine.Count == 0) return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", mine.Select(f => f.Key.ToLowerInvariant() + "|" + f.Value))))).ToLowerInvariant()[..16];
    }

    /// <summary>Whether a part is run again, and the reason in words for the report.</summary>
    public static (bool Run, string Why) Decide(Part? last, string print)
    {
        if (last is null) return (true, "it has not been run on this PC yet");
        if (last.Failed > 0) return (true, $"{last.Failed} step{(last.Failed == 1 ? "" : "s")} of it failed in the run of version {last.Version}");
        // Steps left out only because the program's window went away (a crash, a hang) were never tried: they are not
        // carried over as "not tested" from run to run, the part is tried again (0.4.46 run, 2026-11-26).
        if (last.NotTested.Count(n => Interrupted(n.Why)) is > 0 and var cut)
            return (true, $"{cut} step{(cut == 1 ? "" : "s")} of it {(cut == 1 ? "was" : "were")} not run in version {last.Version} because the program's window stopped answering");
        if (print.Length == 0 || last.Print.Length == 0) return (true, "this build does not say which of its code changed");
        if (!string.Equals(print, last.Print, StringComparison.Ordinal)) return (true, $"its code changed since version {last.Version}");
        return (false, $"all {last.Passed} steps of it passed in version {last.Version} and its code is unchanged since");
    }

    /// <summary>Whether a step's "not tested" reason is that the program went away mid-run, in the words FieldContext and FieldTest use.</summary>
    internal static bool Interrupted(string why) =>
        why.Contains("stopped answering", StringComparison.OrdinalIgnoreCase) || why.Contains("no longer answers", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, Part> Load(string? file = null)
    {
        var parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
        try
        {
            file ??= File;
            if (!System.IO.File.Exists(file)) return parts;
            if (JsonNode.Parse(System.IO.File.ReadAllText(file))?["parts"] is not JsonObject saved) return parts;
            foreach (var (key, value) in saved)
            {
                if (value is not JsonObject p) continue;
                var notTested = (p["not_tested"] as JsonArray ?? []).Select(n => ((string?)n?["step"] ?? "", (string?)n?["why"] ?? "")).Where(n => n.Item1.Length > 0).ToList();
                parts[key] = new Part((string?)p["version"] ?? "", (string?)p["print"] ?? "", (int?)p["passed"] ?? 0, (int?)p["failed"] ?? 0, notTested);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { parts.Clear(); }
        return parts;
    }

    /// <summary>Keeps the result of each part that ran; the parts that did not run keep what they had.</summary>
    public static void Save(IReadOnlyDictionary<string, Part> ran, string? file = null)
    {
        if (ran.Count == 0) return;
        try
        {
            file ??= File;
            var all = Load(file);
            foreach (var (key, part) in ran) all[key] = part;
            var parts = new JsonObject();
            foreach (var (key, p) in all.OrderBy(a => a.Key, StringComparer.Ordinal))
                parts[key] = new JsonObject
                {
                    ["version"] = p.Version, ["print"] = p.Print, ["passed"] = p.Passed, ["failed"] = p.Failed,
                    ["not_tested"] = new JsonArray(p.NotTested.Select(n => (JsonNode)new JsonObject { ["step"] = n.Step, ["why"] = n.Why }).ToArray()),
                };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, new JsonObject { ["parts"] = parts }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next run is a full one for these parts */ }
    }

    /// <summary>What a part came to in this run, from its steps.</summary>
    public static Part Of(IEnumerable<FieldStep> steps, string version, string print)
    {
        var mine = steps.ToList();
        return new Part(version, print, mine.Count(s => s.Ok && !s.Skipped), mine.Count(s => !s.Ok),
            mine.Where(s => s.Skipped).Select(s => (s.Name, s.Detail)).ToList());
    }

    /// <summary>The steps written for a part that is not run again: one line that says so, and what it could not test.</summary>
    public static void LeftOut(FieldContext ctx, string name, Part last, string why)
    {
        ctx.Note($"{name}: not run again", $"{why}. Run the full test to try all of it again.");
        foreach (var (step, reason) in last.NotTested)
            ctx.Skip(step, reason.EndsWith(')') && reason.Contains("(as in the run of version", StringComparison.Ordinal) ? reason : $"{reason} (as in the run of version {last.Version})");
    }
}
