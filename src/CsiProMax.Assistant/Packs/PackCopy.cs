using System.Diagnostics;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Packs;

/// <summary>
/// The pack read from a copy (Ahmed 2026-11-27: the pack must not lock his program or PC). The saved model and every
/// file beside it with its name (the analysis results ETABS, SAP2000 and SAFE keep there) are copied into a folder of
/// CSI ProMax's own; a hidden program of the same kind opens the copy, the pack is read from it, and that program closes
/// at once, giving its licence back. The engineer's window is never asked anything while the pack is read.
/// It is also the way round SAP2000 26.3's heap corruption when a long session reads result tables (0.4.58 to 0.4.64
/// field tests): the copy is read in a fresh process.
/// When the copy cannot be read (no second program can start, the results did not come with the file), it says why
/// and the caller reads the engineer's window as before.
/// </summary>
public static class PackCopy
{
    /// <summary>Where the copies are made: a folder per read, removed after it.</summary>
    public static string Root { get; set; } = Path.Combine(Path.GetTempPath(), "CsiProMax pack copies");

    /// <summary>Starts the hidden program on the copy (the tests put a stand-in here).</summary>
    public static Func<CsiProgram, FieldContext, string, IScratchWindow?> Start { get; set; } = (p, ctx, f) => ScratchProgram.Start(p, ctx, f, asIs: true);

    /// <summary>The model file and the files beside it with its name (Model.EDB, Model.Y01, Model.LOG...), not the review pack's own.</summary>
    public static IReadOnlyList<string> FilesOf(string modelFile)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(modelFile))!;
        var stem = Path.GetFileNameWithoutExtension(modelFile);
        return Directory.EnumerateFiles(folder, stem + ".*")
            .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                        && !Path.GetExtension(f).Equals(".cpmx", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(f).Equals(".html", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Copies a file the program may still hold open (read with others writing it).</summary>
    internal static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    public sealed record Outcome<T>(T? Value, string Said, TimeSpan Took);

    /// <summary>
    /// Copies <paramref name="modelFile"/> with its results, opens the copy in a hidden <paramref name="program"/>, runs
    /// <paramref name="read"/> on that program's link, and closes it. Value is null (with the reason in Said) when the
    /// copy could not be read; nothing is ever changed in the engineer's files.
    /// </summary>
    public static Outcome<T> Read<T>(CsiProgram program, string modelFile, Func<ICsiLink, T> read) where T : class
    {
        var watch = Stopwatch.StartNew();
        var folder = Path.Combine(Root, $"{Path.GetFileNameWithoutExtension(modelFile)} {DateTime.Now:yyyyMMdd-HHmmss-fff}");
        IScratchWindow? window = null;
        try
        {
            if (!File.Exists(modelFile)) return new(null, $"{Path.GetFileName(modelFile)} is not saved there, so there is nothing to copy", watch.Elapsed);
            Directory.CreateDirectory(folder);
            foreach (var f in FilesOf(modelFile)) CopyShared(f, Path.Combine(folder, Path.GetFileName(f)));
            var copy = Path.Combine(folder, Path.GetFileName(modelFile));
            var ctx = new FieldContext(program, folder, new OfflineEtabs());
            window = Start(program, ctx, copy);
            if (window is null)
                return new(null, $"a hidden {program.Name} could not open the copy ({string.Join(" ", ctx.Steps.Where(s => !s.Ok && !s.Skipped).Select(s => s.Name + ": " + s.Detail))})", watch.Elapsed);
            if (window is ScratchProgram sp) sp.Hide();
            bool locked;
            try { locked = window.Link.Info().Locked; } catch (EtabsException) { locked = false; }
            if (!locked) return new(null, $"the copy opened in a hidden {program.Name} without its analysis results", watch.Elapsed);
            return new(read(window.Link), $"read from a copy in a hidden {program.Name}", watch.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EtabsException or InvalidOperationException)
        {
            return new(null, $"the copy could not be read ({ex.Message})", watch.Elapsed);
        }
        finally
        {
            if (window is not null)
            {
                try { window.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                try { window.PutBackPlugIns(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                window.Dispose();
            }
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed with the temp folder */ }
        }
    }
}
