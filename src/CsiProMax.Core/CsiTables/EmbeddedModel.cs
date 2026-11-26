using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CsiProMax.Model;

namespace CsiProMax.CsiTables;

/// <summary>
/// The whole model carried inside a .s2k or .f2k that CSI ProMax writes, after END TABLE DATA, where SAP2000 and SAFE
/// stop reading. Opening that file again in the Modeler gives the model back exactly, with everything the program's
/// tables have no place for. The tables' fingerprint goes with it: when the tables were changed after CSI ProMax wrote
/// them (edited by hand, say), the tables are read instead, so what the file says is what opens.
/// </summary>
public static class EmbeddedModel
{
    private const string Mark = "$ CSI ProMax model ";
    private const string End = "$ end of the CSI ProMax model";

    /// <summary>The table text with the model added after it.</summary>
    public static string Attach(string tableText, StructuralModel m)
    {
        var json = Encoding.UTF8.GetBytes(ModelFile.ToJson(m));
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(json);
        var text = Convert.ToBase64String(packed.ToArray());
        var sb = new StringBuilder(tableText.Length + text.Length + text.Length / 100 * 5 + 200);
        sb.Append(tableText);
        if (!tableText.EndsWith('\n')) sb.Append("\r\n");
        sb.Append(Mark).Append(Fingerprint(tableText)).Append("\r\n");
        for (var i = 0; i < text.Length; i += 4000) sb.Append("$ ").Append(text, i, Math.Min(4000, text.Length - i)).Append("\r\n");
        return sb.Append(End).Append("\r\n").ToString();
    }

    /// <summary>The model carried in the file, or null when it has none or its tables were changed since.</summary>
    public static StructuralModel? Read(string text)
    {
        var at = text.LastIndexOf(Mark, StringComparison.Ordinal);
        if (at < 0) return null;
        var lineEnd = text.IndexOf('\n', at);
        if (lineEnd < 0) return null;
        var print = text[(at + Mark.Length)..lineEnd].Trim();
        if (print != Fingerprint(text[..at])) return null;
        var data = new StringBuilder();
        foreach (var raw in text[(lineEnd + 1)..].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(End, StringComparison.Ordinal)) break;
            if (line.StartsWith("$ ", StringComparison.Ordinal)) data.Append(line, 2, line.Length - 2);
        }
        try
        {
            using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(data.ToString())), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return ModelFile.FromJson(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Whether the text carries a model at all (changed or not).</summary>
    public static bool Has(string text) => text.Contains(Mark, StringComparison.Ordinal);

    /// <summary>The tables' fingerprint, with line ends and trailing spaces left out so an editor's saving does not count as a change.</summary>
    private static string Fingerprint(string tables)
    {
        var sb = new StringBuilder(tables.Length);
        foreach (var line in tables.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var t = line.TrimEnd();
            if (t.Length > 0) sb.Append(t).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }
}
