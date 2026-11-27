using CsiProMax.Model;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// Opens a program's own model file (ETABS .edb) in CSI ProMax with nothing lost. The program opens a copy hidden, gives
/// its .e2k and every model table; CSI ProMax reads the .e2k and writes its own; a second hidden program opens that, and
/// what its tables then lack is kept in the model (<see cref="CarriedTables"/>) and put back after the text file every
/// time the model goes into the program again. The engineer's file is only read.
/// </summary>
public static class ProgramModelOpen
{
    /// <summary>The programs whose own model file this opens: those whose API exports their text file.</summary>
    public static bool Takes(string path) => Path.GetExtension(path).Equals(".edb", StringComparison.OrdinalIgnoreCase);

    public sealed record Result(StructuralModel Model, IReadOnlyList<string> Notes);

    public static Result Open(string path, Action<string>? say = null)
    {
        var program = CsiProgram.Etabs;
        var folder = Path.Combine(Path.GetTempPath(), "CsiProMax", "open " + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(path));
        File.Copy(path, copy, overwrite: true);
        File.SetAttributes(copy, FileAttributes.Normal);
        var notes = new List<string>();

        // A: the program's own model, as it is.
        say?.Invoke($"Opening a copy of {Path.GetFileName(path)} in {program.Name}, in the background");
        Dictionary<string, TableData> before;
        string text;
        var ctxA = new FieldContext(program, Path.Combine(folder, "as is"), new OfflineEtabs());
        Directory.CreateDirectory(ctxA.Folder);
        using (var window = ScratchProgram.Start(program, ctxA, copy, asIs: true) ?? throw new EtabsException($"{program.Name} could not open {Path.GetFileName(path)}: {Failed(ctxA)}"))
        {
            try
            {
                before = RealModelField.ModelTables(window.Link);
                text = RealModelField.TextFor(program, window.Link, copy, folder);
            }
            finally { window.Close(); }
        }
        var model = E2k.E2kReader.ReadFile(text);
        model.Target = program.Key;

        // B: CSI ProMax's own text file of it, opened the same way; what its tables lack is carried.
        say?.Invoke($"Checking that {program.Name} gets the whole model back from CSI ProMax's file");
        var mine = Path.Combine(folder, "through CSI ProMax", Path.GetFileNameWithoutExtension(path) + ".e2k");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        E2k.E2kWriter.WriteFile(model, mine);
        var ctxB = new FieldContext(program, Path.Combine(folder, "through CSI ProMax"), new OfflineEtabs());
        using (var window = ScratchProgram.Start(program, ctxB, mine) ?? throw new EtabsException($"{program.Name} could not open CSI ProMax's file of the model: {Failed(ctxB)}"))
        {
            try
            {
                var after = RealModelField.ModelTables(window.Link);
                model.Carried = ProgramCarry.Measure(program, before, after);
            }
            finally { window.Close(); }
        }
        if (model.Carried is { } c)
            notes.Add($"{c.Count} row(s) of {c.Rows.Count} {program.Name} table(s) that its .e2k does not carry are kept, and go back in through its tables on every run: {string.Join(", ", c.Rows.Keys.Take(12))}");
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new Result(model, notes);
    }

    private static string Failed(FieldContext ctx) => string.Join(" ", ctx.Steps.Where(s => !s.Ok && !s.Skipped).Select(s => s.Name + ": " + s.Detail));
}
