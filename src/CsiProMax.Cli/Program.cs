using System.Globalization;
using CsiProMax.Checks;
using CsiProMax.E2k;
using CsiProMax.Model;

const string Help = """
    cpm: CSI ProMax models without ETABS.

      cpm new-frame <out> --x 6,6,6 --y 5,5 --stories 4 [--height 3.2] [--sdl 2] [--live 3] [--title "Name"]
            A regular concrete frame building (kN, m), ready to import into ETABS.
      cpm convert <in> <out>     Between ETABS text files (.e2k, .$et) and CSI ProMax models (.cpm), either way.
      cpm info <file>            What is in a model.
      cpm check <file>           Completeness and connectivity checks. Exit code 1 when there are errors.
      cpm quantities <file>      Lengths, areas and concrete volumes by section.

    <out> ending in .cpm is a CSI ProMax model; anything else is written as an ETABS text file.
    In ETABS: File > Import > ETABS .e2k Text File.
    """;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?") { Console.WriteLine(Help); return 0; }
    switch (args[0])
    {
        case "new-frame":
        {
            var output = Positional(1, "the file to write");
            var model = Templates.ConcreteFrame(
                Option("--title") ?? Path.GetFileNameWithoutExtension(output),
                Numbers(Option("--x") ?? throw new UserError("Give the bay sizes along X, e.g. --x 6,6,6.")),
                Numbers(Option("--y") ?? throw new UserError("Give the bay sizes along Y, e.g. --y 5,5.")),
                (int)Number(Option("--stories") ?? throw new UserError("Give the number of stories, e.g. --stories 4."), "--stories"),
                Number(Option("--height") ?? "3.2", "--height"),
                Number(Option("--sdl") ?? "2", "--sdl"), Number(Option("--live") ?? "3", "--live"));
            Save(model, output);
            Console.WriteLine($"Wrote {output}: {Summary(model)}");
            return 0;
        }
        case "convert":
        {
            var model = Load(Positional(1, "the file to read"));
            var output = Positional(2, "the file to write");
            Save(model, output);
            Console.WriteLine($"Wrote {output}: {Summary(model)}");
            return 0;
        }
        case "info":
        {
            var model = Load(Positional(1, "the file to read"));
            Console.WriteLine($"{(model.Title.Length > 0 ? model.Title : "(no title)")}  |  units {model.Units.Force}, {model.Units.Length}  |  ETABS file version {model.ProgramVersion}");
            Console.WriteLine(Summary(model));
            Console.WriteLine("Stories, top first:");
            for (var i = model.Stories.Count - 1; i >= 0; i--)
                Console.WriteLine($"  {model.Stories[i].Name,-16} elevation {F(model.Elevation(i))}");
            Console.WriteLine($"Load patterns: {string.Join(", ", model.LoadPatterns.Select(p => p.Name))}");
            Console.WriteLine($"Combinations: {string.Join(", ", model.Combos.Select(c => c.Name))}");
            if (model.Passthrough.Count > 0)
                Console.WriteLine($"Kept as read (not interpreted): {string.Join(", ", model.Passthrough.Select(s => s.Heading.ToLowerInvariant()))}");
            return 0;
        }
        case "check":
        {
            var findings = ModelChecks.Run(Load(Positional(1, "the file to read")));
            foreach (var f in findings.OrderBy(f => f.Severity))
                Console.WriteLine($"{(f.Severity == Severity.Error ? "ERROR  " : "warning")}  {f.Check,-12}  {f.Message}");
            var errors = findings.Count(f => f.Severity == Severity.Error);
            Console.WriteLine(findings.Count == 0 ? "No findings." : $"{errors} error(s), {findings.Count - errors} warning(s).");
            return errors > 0 ? 1 : 0;
        }
        case "quantities":
        {
            var model = Load(Positional(1, "the file to read"));
            var l = model.Units.Length.ToLowerInvariant();
            Console.WriteLine($"{"Kind",-8} {"Section",-24} {"Material",-12} {"Count",6} {"Length " + l,12} {"Area " + l + "2",12} {"Volume " + l + "3",12}");
            foreach (var r in Quantities.Run(model))
                Console.WriteLine($"{r.Kind,-8} {r.Section,-24} {r.Material,-12} {r.Count,6} {(r.Length > 0 ? F(r.Length) : ""),12} {(r.Area > 0 ? F(r.Area) : ""),12} {(r.Volume is { } v ? F(v) : "size unknown"),12}");
            return 0;
        }
        default:
            throw new UserError($"There is no command '{args[0]}'.\n\n{Help}");
    }
}
catch (Exception ex) when (ex is UserError or E2kFormatException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

string Positional(int index, string what) =>
    index < args.Length && !args[index].StartsWith("--") ? args[index] : throw new UserError($"Give {what}.\n\n{Help}");

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    if (at < 0) return null;
    return at + 1 < args.Length ? args[at + 1] : throw new UserError($"{name} needs a value.");
}

static double Number(string text, string name) =>
    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new UserError($"{name}: '{text}' is not a number.");

static double[] Numbers(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => Number(t, "bay size")).ToArray();

static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

static bool IsEpm(string path) => path.EndsWith(ModelFile.Extension, StringComparison.OrdinalIgnoreCase);

static StructuralModel Load(string path)
{
    if (!File.Exists(path)) throw new UserError($"There is no file '{path}'.");
    return IsEpm(path) ? ModelFile.Load(path) : E2kReader.ReadFile(path);
}

static void Save(StructuralModel model, string path)
{
    if (IsEpm(path)) ModelFile.Save(model, path); else E2kWriter.WriteFile(model, path);
}

static string Summary(StructuralModel m)
{
    var frames = Physical.Frames(m).ToList(); var areas = Physical.Areas(m).ToList();
    return $"{m.Stories.Count - 1} stories, {frames.Count(f => f.Kind == LineKind.Column)} columns, {frames.Count(f => f.Kind == LineKind.Beam)} beams, " +
           $"{frames.Count(f => f.Kind == LineKind.Brace)} braces, {areas.Count(a => a.Kind == AreaKind.Floor)} slabs, {areas.Count(a => a.Kind == AreaKind.Wall)} walls.";
}

internal sealed class UserError(string message) : Exception(message);
