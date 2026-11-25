using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The field test looks at the Viewer's screen, not only at the pack it reads. The pack of each program's scratch model
/// is put into the Viewer page (as export_pack does for a reviewer) and into the live 3D view's page (as View > Open the
/// 3D view serves it), each page is opened in the PC's own Edge or Chrome without a window (headless, in a browser
/// profile of the test's own folder), and two things are read back:
///   - the page's text once it has drawn: the model's name in its title strip, and the counts of columns, beams,
///     braces, floors and walls in its Properties tab, which must match the program's;
///   - a picture of the screen (kept in the test's folder, so it goes in the zip to send): the model must be drawn in
///     the view windows, and with design results, coloured by design ratio.
/// The same steps run for ETABS, SAP2000 and SAFE: the Viewer is one page for all three, so what is fixed in it is
/// fixed for all of them.
/// </summary>
internal static class ViewerScreen
{
    /// <summary>The screen size the pages are drawn at.</summary>
    public const int Width = 1400, Height = 900;

    private static readonly TimeSpan Most = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Fewer drawn pixels than this and the views count as empty. Measured on this PC with Edge at 1400 x 900: the empty
    /// Viewer gives about 80 (the coloured fringe of its "Open a pack" text), a two-story frame of 18 objects about 6,600.
    /// </summary>
    public const int MostlyEmpty = 1000;

    /// <summary>Edge first (it is on every Windows 10 and 11), then Chrome: the first one that is there.</summary>
    public static string? Browser()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        }.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var exe in new[] { Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine("Google", "Chrome", "Application", "chrome.exe") })
            foreach (var root in roots)
                if (Path.Combine(root!, exe) is var path && File.Exists(path))
                    return path;
        return null;
    }

    /// <summary>
    /// The Viewer's steps for one program's pack: the review pack's page, the live 3D view's page, and with design
    /// results the page coloured by design ratio.
    /// </summary>
    public static void Steps(FieldContext ctx, JsonObject content, string program)
    {
        const string what = "the Viewer's screen";
        if (Browser() is not { } browser)
        {
            ctx.Skip($"{what} with the {program} pack", "no Edge or Chrome was found on this PC to draw the Viewer page; open the pack's viewer page by hand and look at it");
            return;
        }
        var folder = Path.Combine(ctx.Folder, "Viewer");
        Directory.CreateDirectory(folder);
        var name = ModelerFieldSteps.ModelName + " " + program;
        var expected = Counts(content);
        // Design ratios to colour by: a design table with rows in the pack.
        var designed = content["tables"] is JsonObject tables && tables.Any(t => DesignResults.IsDesignTable(t.Key) && t.Value?["rows"] is JsonArray { Count: > 0 });

        ctx.Step($"{what}: the {program} review pack's page draws the model", () =>
            Look(browser, folder, name + " viewer", Pack.ViewerHtml(content), content, expected));
        ctx.Step($"{what}: the {program} live 3D view draws the model", () =>
            Look(browser, folder, name + " 3D view", Pack.LiveViewerHtml(content, 0), content, expected));
        if (designed)
            ctx.Step($"{what}: the {program} model coloured by design ratio", () =>
                Look(browser, folder, name + " design ratio", WithColour(Pack.ViewerHtml(content), "ratio"), content, expected, colours: true));
        else ctx.Skip($"{what}: the {program} model coloured by design ratio", "the pack holds no design results (see the design steps above)");
    }

    /// <summary>The page's own colour list set to <paramref name="by"/> once it has loaded, as a person would pick it.</summary>
    internal static string WithColour(string html, string by)
    {
        var pick = "<script>addEventListener('load',function(){setTimeout(function(){var s=document.getElementById('colour');" +
                   "if(!s)return;for(var i=0;i<s.options.length;i++)if(s.options[i].value==='" + by + "'){s.value='" + by + "';" +
                   "s.dispatchEvent(new Event('change',{bubbles:true}));document.body.setAttribute('data-cpm-colour','" + by + "');}},400);});</script>";
        return html.Replace("</body>", pick + "</body>", StringComparison.Ordinal);
    }

    /// <summary>Columns, beams, braces, floors and walls in the pack, by the names the Viewer's Properties tab uses.</summary>
    internal static Dictionary<string, int> Counts(JsonObject content)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Columns"] = 0, ["Beams"] = 0, ["Braces"] = 0, ["Floors"] = 0, ["Walls"] = 0 };
        if (content["model"]?["frames"] is JsonArray frames)
            foreach (var f in frames)
                if (f is JsonArray { Count: > 1 } row && Label(row[1]?.GetValue<string>()) is { } label) counts[label]++;
        if (content["model"]?["areas"] is JsonArray areas)
            foreach (var a in areas)
                if (Label(a?["k"]?.GetValue<string>()) is { } label) counts[label]++;
        return counts;
    }

    private static string? Label(string? kind) => kind?.ToLowerInvariant() switch
    {
        "column" => "Columns", "beam" => "Beams", "brace" => "Braces", "floor" or "slab" => "Floors", "wall" => "Walls",
        _ => null,
    };

    /// <summary>Opens one page without a window, keeps its picture and text, and checks both.</summary>
    private static object Look(string browser, string folder, string name, string html, JsonObject content, Dictionary<string, int> expected, bool colours = false)
    {
        var page = Path.Combine(folder, name + ".html");
        File.WriteAllText(page, html, new UTF8Encoding(false));
        var png = Path.Combine(folder, name + ".png");
        if (File.Exists(png)) File.Delete(png);
        var url = new Uri(page).AbsoluteUri;
        Run(browser, folder, [$"--screenshot={png}", url], out _);
        Run(browser, folder, ["--dump-dom", url], out var dom);
        File.WriteAllText(Path.Combine(folder, name + ".dom.txt"), Text(dom), new UTF8Encoding(false));

        FieldContext.Expect(dom.Length > 1000, $"the page gave no text back ({dom.Length} characters): it did not open");
        FieldContext.Expect(!dom.Contains("No pack open", StringComparison.Ordinal), "the page says 'No pack open': the pack inside it was not read");
        var model = content["manifest"]?["model"]?.GetValue<string>() ?? "";
        FieldContext.Expect(model.Length == 0 || Regex.IsMatch(dom, "id=\"meta\"[^>]*>[^<]*" + Regex.Escape(System.Net.WebUtility.HtmlEncode(model))),
            $"the title strip does not name the model '{model}'");
        var shown = Shown(dom);
        var wrong = expected.Where(e => e.Value > 0 || shown.ContainsKey(e.Key))
            .Where(e => !shown.TryGetValue(e.Key, out var n) || n != e.Value)
            .Select(e => $"{e.Key}: the model has {e.Value}, the Viewer shows {(shown.TryGetValue(e.Key, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}")
            .ToList();
        FieldContext.Expect(wrong.Count == 0, "the Properties tab does not match the model: " + string.Join("; ", wrong));

        FieldContext.Expect(File.Exists(png) && new FileInfo(png).Length > 5000, "no picture of the screen was taken");
        var drawn = Drawn(png);
        var objects = expected.Values.Sum();
        FieldContext.Expect(objects == 0 || drawn.Model >= MostlyEmpty, $"the view windows are empty: {drawn.Model} pixels of the model were drawn (picture: Viewer\\{name}.png)");
        if (colours)
        {
            FieldContext.Expect(dom.Contains("data-cpm-colour=\"ratio\"", StringComparison.Ordinal), "the Viewer has no 'Design ratio' in its Colour by list");
            FieldContext.Expect(drawn.Ratio >= MostlyEmpty / 4, $"the model is not coloured by design ratio: {drawn.Ratio} of {drawn.Model} drawn pixels are in the ratio scale's colours (picture: Viewer\\{name}.png)");
        }
        return new { picture = "Viewer\\" + name + ".png", model_pixels = drawn.Model, ratio_pixels = drawn.Ratio, properties = shown };
    }

    /// <summary>The counts in the Properties tab: rows "Columns | 12".</summary>
    internal static Dictionary<string, int> Shown(string dom)
    {
        var shown = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(dom, @"<td>(Columns|Beams|Braces|Floors|Walls)</td><td>(\d+)</td>"))
            shown.TryAdd(m.Groups[1].Value, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        return shown;
    }

    private static string Text(string dom) => Regex.Replace(Regex.Replace(dom, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", ""), "<[^>]+>", " ");

    /// <summary>The design ratio scale's colours in the Viewer (viewer.html, ratioCols): up to 0.5, 0.7, 0.9, 1.0, above.</summary>
    internal static readonly int[] RatioColours = [0x38B6E8, 0x35C24A, 0xF0D824, 0xF3961C, 0xE8291C];

    /// <summary>The object type colours of the dark views (columns, beams, braces, others, walls), told apart from the ratio scale.</summary>
    internal static readonly int[] KindColours = [0x2F8FF7, 0xF0CF34, 0xFF8A3C, 0xC79BFF, 0xD4625C];

    /// <summary>True when the colour is within reach of the ratio scale and nearer it than any object type colour.</summary>
    internal static bool OnRatioScale(int r, int g, int b)
    {
        static int Distance(int c, int r, int g, int b) => Math.Abs((c >> 16) - r) + Math.Abs(((c >> 8) & 0xFF) - g) + Math.Abs((c & 0xFF) - b);
        var toRatio = RatioColours.Min(c => Distance(c, r, g, b));
        return toRatio <= 60 && toRatio < KindColours.Min(c => Distance(c, r, g, b));
    }

    /// <summary>
    /// What is drawn in the view windows (the middle of the screen, left of the Properties tab and below the toolbars):
    /// strongly coloured pixels, three in a row (the background, the grid and the floors are black or grey), and how many
    /// of them are in the design ratio scale's colours rather than the object types'.
    /// </summary>
    internal static (int Model, int Ratio) Drawn(string png)
    {
        using var stream = File.OpenRead(png);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        int x0 = w * 19 / 100, x1 = w * 77 / 100, y0 = h * 12 / 100, y1 = h * 96 / 100;
        int model = 0, ratio = 0;
        bool Coloured(int i)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            return max >= 90 && max - min >= 70;
        }
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1 - 2; x++)
            {
                var i = y * stride + x * 4;
                // Three coloured pixels in a row: a drawn line or face, not the coloured fringe of smoothed text.
                if (!Coloured(i) || !Coloured(i + 4) || !Coloured(i + 8)) continue;
                model++;
                if (OnRatioScale(pixels[i + 2], pixels[i + 1], pixels[i])) ratio++;
            }
        return (model, ratio);
    }

    /// <summary>The browser's own profile for the test: never the person's, and outside the test's folder so none of it goes in the zip.</summary>
    private static string Profile => Path.Combine(Path.GetTempPath(), "CsiProMax-field-test-browser");

    /// <summary>The browser without a window, in a profile of its own, given up on after a minute and a half.</summary>
    private static void Run(string browser, string folder, IEnumerable<string> extra, out string output)
    {
        var info = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[]
                 {
                     "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--hide-scrollbars",
                     "--user-data-dir=" + Profile, $"--window-size={Width},{Height}", "--virtual-time-budget=10000",
                 }.Concat(extra))
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The browser did not start: " + browser);
        var read = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Most.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new TimeoutException($"the browser did not finish drawing the page within {Most.TotalSeconds:0} seconds");
        }
        output = read.Result;
    }
}
