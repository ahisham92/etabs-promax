using System.IO;
using System.Text;

namespace CsiProMax.Tools;

/// <summary>
/// PDF work that needs no ETABS, done with PDFium (<see cref="PdfEngine"/>, the engine that also draws PDF pages): the
/// text of every page (what the chat reads from a PDF too large to send whole), and splitting a PDF into parts of at
/// most a given size (on a large PDF in the chat). All of it runs on the PDF thread, off ETABS's thread.
/// </summary>
internal static class PdfFiles
{
    /// <summary>The page count, or null when the file can't be opened (damaged, or locked with a password).</summary>
    public static int? PageCount(string pdf)
    {
        try { return PdfEngine.Run(() => { using var doc = PdfEngine.Doc.Open(pdf); return doc.Pages; }); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    public sealed record TextResult(int Pages, int Characters, int PagesWithText, string? Error)
    {
        /// <summary>True when most pages have no text layer (scans, or drawings exported as pictures).</summary>
        public bool MostlyPictures => Error is null && PagesWithText * 2 < Pages;
    }

    /// <summary>
    /// Writes the text of every page to <paramref name="target"/>, each page headed "=== Page N of M ===" so a page can be
    /// found with a search. A page whose text can't be read is noted and skipped.
    /// </summary>
    public static TextResult WriteText(string pdf, string target, CancellationToken ct = default)
    {
        try
        {
            return PdfEngine.Run(() =>
            {
                using var doc = PdfEngine.Doc.Open(pdf);
                var total = doc.Pages;
                int characters = 0, withText = 0;
                using var output = new StreamWriter(target, false, new UTF8Encoding(false));
                output.WriteLine($"Text of {Path.GetFileName(pdf)}, {total} page{(total == 1 ? "" : "s")}, read by CSI ProMax.");
                for (var n = 1; n <= total; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    output.WriteLine();
                    output.WriteLine($"=== Page {n} of {total} ===");
                    var text = PdfEngine.Text(doc, n - 1)?.Trim();
                    if (text is null) text = "(the text of this page couldn't be read)";
                    else if (text.Length == 0) text = "(no text on this page: a picture or a drawing)";
                    else { withText++; characters += text.Length; }
                    output.WriteLine(text);
                }
                return new TextResult(total, characters, withText, null);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new TextResult(0, 0, 0, ex.Message);
        }
    }

    public sealed record Part(string File, int FirstPage, int LastPage, long Bytes);

    /// <summary>
    /// Splits <paramref name="pdf"/> into parts of at most <paramref name="maxBytes"/>, in page order, saved in
    /// <paramref name="folder"/> as "name - part 1 (pages 1-40).pdf". A part's pages are guessed from the average page
    /// size and cut down until it fits; a single page larger than the limit becomes a part of its own.
    /// A part also has at most <paramref name="maxPages"/> pages. <paramref name="progress"/> gets the last page done and the page count.
    /// </summary>
    public static List<Part> Split(string pdf, long maxBytes, string folder, Action<int, int>? progress = null, CancellationToken ct = default, int maxPages = int.MaxValue)
    {
        if (maxBytes < 1024 * 1024) throw new ArgumentException("The part size is at least 1 MB.");
        return PdfEngine.Run(() =>
        {
            PdfEngine.Doc doc;
            try { doc = PdfEngine.Doc.Open(pdf); }
            catch (PdfEngine.OpenException ex)
            {
                throw new ArgumentException($"{ex.Message} If it has a password, save an unlocked copy first.");
            }
            using (doc)
            {
                var total = doc.Pages;
                Directory.CreateDirectory(folder);
                var name = Path.GetFileNameWithoutExtension(pdf);
                var parts = new List<Part>();
                var perPage = Math.Max(1.0, new FileInfo(pdf).Length / (double)Math.Max(1, total));
                var first = 1;
                while (first <= total)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = Math.Max(1, Math.Min(Math.Min(total - first + 1, maxPages), (int)Math.Min(int.MaxValue, maxBytes * 0.9 / perPage)));
                    var file = Path.Combine(folder, $"{name} - part {parts.Count + 1}.pdf");
                    long size;
                    while (true)
                    {
                        size = PdfEngine.Write(Enumerable.Range(first - 1, count).Select(i => new PdfEngine.Out(doc, i, 0)).ToList(), file, ct: ct);
                        if (size <= maxBytes || count == 1) break;
                        ct.ThrowIfCancellationRequested();
                        count = Math.Max(1, Math.Min(count - 1, (int)(count * (maxBytes * 0.9 / size))));
                    }
                    var last = first + count - 1;
                    var named = Path.Combine(folder, $"{name} - part {parts.Count + 1} (pages {(first == last ? first.ToString() : $"{first}-{last}")}).pdf");
                    File.Move(file, named, true);
                    parts.Add(new Part(named, first, last, size));
                    progress?.Invoke(last, total);
                    first = last + 1;
                }
                return parts;
            }
        });
    }

    public sealed record ChatParts(string Folder, int Pages, List<Part> Parts, string? TextFile, TextResult Text);

    /// <summary>
    /// A large PDF made ready for the chat: split into parts the chat's helpers read whole (at most
    /// <paramref name="maxBytes"/> and 100 pages each), its text page by page, and "parts.json" (the original's name, its
    /// pages and each part's pages, which read_pdf_part reads). Kept in a folder under <paramref name="root"/> named by
    /// the file's path, size and time, so the same file attached again uses the same parts, and their helpers' chats.
    /// </summary>
    public static ChatParts ForChat(string pdf, string root, long maxBytes, CancellationToken ct = default)
    {
        var info = new FileInfo(pdf);
        string hash;
        using (var sha = System.Security.Cryptography.SHA1.Create())
            hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes($"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))
                .Take(4).Select(b => b.ToString("x2")));
        var name = Path.GetFileNameWithoutExtension(pdf);
        var folder = Path.Combine(root, $"{(name.Length > 60 ? name.Substring(0, 60) : name)} parts {hash}");
        var manifest = Path.Combine(folder, "parts.json");
        var textFile = Path.Combine(folder, name + " - text.txt");
        if (Read(manifest, folder) is { } kept && File.Exists(textFile))
        {
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); // kept while it is used (attachments are cleaned after two weeks)
            return kept with { TextFile = textFile };
        }
        Directory.CreateDirectory(folder);
        var text = WriteText(pdf, textFile, ct);
        var parts = Split(pdf, maxBytes, folder, null, ct, maxPages: 100);
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = Path.GetFileName(pdf),
            ["pages"] = parts.Count == 0 ? 0 : parts[^1].LastPage,
            ["parts"] = new System.Text.Json.Nodes.JsonArray(parts.Select(p => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["file"] = Path.GetFileName(p.File), ["first"] = p.FirstPage, ["last"] = p.LastPage,
            }).ToArray()),
        };
        File.WriteAllText(manifest, json.ToJsonString(), new UTF8Encoding(false));
        return new ChatParts(folder, parts.Count == 0 ? 0 : parts[^1].LastPage, parts, text.Error is null ? textFile : null, text);
    }

    private static ChatParts? Read(string manifest, string folder)
    {
        try
        {
            if (!File.Exists(manifest)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            var r = doc.RootElement;
            var parts = r.GetProperty("parts").EnumerateArray().Select(p =>
            {
                var file = Path.Combine(folder, p.GetProperty("file").GetString() ?? "");
                return new Part(file, p.GetProperty("first").GetInt32(), p.GetProperty("last").GetInt32(), File.Exists(file) ? new FileInfo(file).Length : -1);
            }).ToList();
            if (parts.Count == 0 || parts.Any(p => p.Bytes < 0)) return null;
            var pages = r.GetProperty("pages").GetInt32();
            return new ChatParts(folder, pages, parts, null, new TextResult(pages, 0, pages, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
