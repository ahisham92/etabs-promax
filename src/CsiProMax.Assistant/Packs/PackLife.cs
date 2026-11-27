using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Packs;

/// <summary>
/// The review pack's life beside its model, one rule for every way it is made (Ahmed 2026-11-26 19:02): the pack is
/// there while the model has results, as a CSI program's model is locked, and gone as soon as it has none. A run or a
/// design writes it (or replaces it); a change to the model, or Unlock, removes it at once, as the program deletes its
/// results. A reviewer's comments are never lost with it: they are kept in "&lt;model&gt; comments.json" beside it and go
/// into the next pack. A pack another window has open is replaced or removed without harm: it is written beside its
/// place and moved in (a reader never gets half a file), and a file held open is tried again for a few seconds.
/// </summary>
public static class PackLife
{
    /// <summary>How many times a file that is held open is tried again, and how long between tries.</summary>
    public static int Tries { get; set; } = 10;
    public static TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The comments kept beside a pack: "&lt;model&gt; comments.json", the Viewer's own comments file format.</summary>
    public static string CommentsFile(string packFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packFile))!, Path.GetFileNameWithoutExtension(packFile) + " comments.json");

    /// <summary>The comments for the next pack: those in the pack there now and those kept beside it, each once.</summary>
    public static JsonArray Comments(string packFile)
    {
        var all = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { packFile, CommentsFile(packFile) })
        {
            if (!File.Exists(file)) continue;
            JsonArray list;
            try { list = Pack.ReadComments(file).Comments; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { continue; }
            foreach (var c in list)
                if (c is not null && seen.Add(c.ToJsonString())) all.Add(c.DeepClone());
        }
        return all;
    }

    /// <summary>Writes the pack and its Viewer page, replacing what is there, with the comments kept carried in.</summary>
    public static void Write(string packFile, string pageFile, byte[] zip, string page)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(packFile))!);
        Replace(packFile, zip);
        if (pageFile.Length > 0) Replace(pageFile, new UTF8Encoding(false).GetBytes(page));
    }

    /// <summary>
    /// Removes the pack and its page (the model has no results any more). Its comments are kept beside it first. What
    /// could not be removed (held open for longer than the tries) is given back, to be said plainly; never thrown.
    /// </summary>
    public static List<string> Remove(string packFile, string pageFile)
    {
        var left = new List<string>();
        if (File.Exists(packFile))
        {
            var comments = Comments(packFile);
            if (comments.Count > 0)
                try { Replace(CommentsFile(packFile), new UTF8Encoding(false).GetBytes(new JsonObject { ["model"] = Path.GetFileNameWithoutExtension(packFile), ["comments"] = comments }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add($"{Path.GetFileName(CommentsFile(packFile))} ({ex.Message}); the pack is kept so its comments are not lost"); return left; }
        }
        foreach (var file in new[] { packFile, pageFile })
            if (file.Length > 0 && !Try(() => { if (File.Exists(file)) File.Delete(file); }))
                left.Add(Path.GetFileName(file) + " (it is open in another program)");
        return left;
    }

    /// <summary>Written beside its place and moved in: a reader never gets half a file. Tried again while another program holds it.</summary>
    /// <summary>Writes the pack and its Viewer page made from <paramref name="content"/>, streamed (a large model's page is gigabytes).</summary>
    public static void Write(string packFile, string pageFile, byte[] zip, System.Text.Json.Nodes.JsonObject content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(packFile))!);
        Replace(packFile, zip);
        if (pageFile.Length > 0) Replace(pageFile, temp => CsiProMax.Assistant.Tools.Pack.WriteViewerHtml(temp, content));
    }

    public static void Replace(string target, byte[] bytes) => Replace(target, temp => File.WriteAllBytes(temp, bytes));

    /// <summary>As <see cref="Replace(string, byte[])"/>, with the new file written by <paramref name="write"/> to the path it is given.</summary>
    public static void Replace(string target, Action<string> write)
    {
        var temp = target + ".writing";
        write(temp);
        Exception? last = null;
        for (var i = 0; i < Math.Max(1, Tries); i++)
        {
            try { File.Move(temp, target, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex; Thread.Sleep(Pause); }
        }
        try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        throw new IOException($"{Path.GetFileName(target)} could not be replaced: another program keeps it open ({last?.Message}). Close it there and run again.", last);
    }

    private static bool Try(Action act)
    {
        for (var i = 0; i < Math.Max(1, Tries); i++)
        {
            try { act(); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(Pause); }
        }
        return false;
    }
}
