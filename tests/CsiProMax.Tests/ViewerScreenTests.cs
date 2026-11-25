using System.Text.Json.Nodes;
using CsiProMax.Assistant;

/// <summary>
/// The field test's look at the Viewer's screen (Field/ViewerScreen.cs): what it expects from a pack, how it reads the
/// Properties tab and the colours of a picture, and that ETABS, SAP2000 and SAFE each run it on their own pack. The
/// browser itself is only on the field test's PC.
/// </summary>
static class ViewerScreenTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        var root = SourceText.Root();

        yield return ("viewer screen: the counts it expects are the pack's columns, beams, braces, floors and walls", () =>
        {
            var content = JsonNode.Parse("""
                {"manifest":{"model":"m"},"model":{
                  "frames":[["C1","column","C",  "S1",0,0,0,0,0,3],["C2","column","C","S1",6,0,0,6,0,3],["B1","beam","B","S1",0,0,3,6,0,3],["X1","brace","X","S1",0,0,0,6,0,3]],
                  "areas":[{"n":"F1","k":"floor","s":"S"},{"n":"W1","k":"wall","s":"W"},{"n":"R1","k":"ramp","s":"R"}]}}
                """)!.AsObject();
            var counts = ViewerScreen.Counts(content);
            Check(counts["Columns"] == 2 && counts["Beams"] == 1 && counts["Braces"] == 1 && counts["Floors"] == 1 && counts["Walls"] == 1,
                "counts: " + string.Join(", ", counts.Select(c => c.Key + "=" + c.Value)));
        });

        yield return ("viewer screen: the Properties tab is read from the page's text", () =>
        {
            var shown = ViewerScreen.Shown("<table><tr><td>Model</td><td>m</td></tr><tr><td>Columns</td><td>12</td></tr><tr><td>Beams</td><td>4</td></tr><tr><td>Floors</td><td>2</td></tr></table>");
            Check(shown.Count == 3 && shown["Columns"] == 12 && shown["Beams"] == 4 && shown["Floors"] == 2, "read: " + string.Join(", ", shown.Select(c => c.Key + "=" + c.Value)));
        });

        yield return ("viewer screen: the ratio scale's colours are told apart from the object type colours", () =>
        {
            foreach (var c in ViewerScreen.RatioColours)
                Check(ViewerScreen.OnRatioScale(c >> 16, (c >> 8) & 0xFF, c & 0xFF), $"ratio colour {c:X6} is not on the scale");
            foreach (var c in ViewerScreen.KindColours)
                Check(!ViewerScreen.OnRatioScale(c >> 16, (c >> 8) & 0xFF, c & 0xFF), $"object type colour {c:X6} counts as a ratio colour");
            Check(!ViewerScreen.OnRatioScale(0x8D, 0x9A, 0xA4) && !ViewerScreen.OnRatioScale(255, 255, 255), "grey floors and white text are not ratio colours");
            // The page's own lists, so a colour changed in the Viewer is changed here too.
            var page = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Viewer", "viewer.html"));
            var ratio = "const ratioCols = [" + string.Join(", ", ViewerScreen.RatioColours.Select(c => $"\"#{c:x6}\"")) + "];";
            Check(page.Contains(ratio, StringComparison.Ordinal), "viewer.html's ratio colours are not " + ratio);
            foreach (var (kind, c) in new[] { "column", "beam", "brace", "other", "wall" }.Zip(ViewerScreen.KindColours))
                Check(page.Contains($"{kind}: \"#{c:x6}\"", StringComparison.Ordinal), $"viewer.html's dark {kind} colour is not #{c:x6}");
        });

        yield return ("viewer screen: the design ratio page picks 'Design ratio' in the Viewer's own Colour by list", () =>
        {
            var html = ViewerScreen.WithColour("<html><body><select id=\"colour\"></select></body></html>", "ratio");
            Check(html.Contains("getElementById('colour')") && html.Contains("s.value='ratio'") && html.IndexOf("<script>", StringComparison.Ordinal) < html.IndexOf("</body>", StringComparison.Ordinal), "no pick before </body>");
            var page = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Viewer", "viewer.html"));
            Check(page.Contains("id=\"colour\"") && page.Contains("<option value=\"ratio\">Design ratio"), "viewer.html has no Colour by list with Design ratio");
        });

        yield return ("viewer screen: ETABS, SAP2000 and SAFE each look at the Viewer with their own pack", () =>
        {
            foreach (var (file, program) in new[] { ("Field/EtabsFieldSteps.cs", "ETABS"), ("Sap2000/SapFieldSteps.cs", "SAP2000"), ("Safe/SafeFieldSteps.cs", "SAFE") })
            {
                var text = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", file));
                var pack = text.IndexOf("the review pack of the scratch model is written", StringComparison.Ordinal);
                var screen = text.IndexOf($"ViewerScreen.Steps(ctx, packed, \"{program}\")", StringComparison.Ordinal);
                Check(pack > 0 && screen > pack, $"{file} does not look at the Viewer after writing its pack");
            }
            var test = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "FieldTest.cs"));
            Check(test.Contains("\".png\""), "the pictures of the screen are not taken into the zip to send");
        });
    }
}
