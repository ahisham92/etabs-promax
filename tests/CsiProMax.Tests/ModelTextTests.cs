using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>
/// Edit > Model Text (ModelTextTools): the safety net for anything no form offers yet. A section of the .e2k is read and
/// changed by hand; a change that breaks the file or the model is refused and changes nothing.
/// </summary>
static class ModelTextTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Text", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args)
    {
        var t = ModelTextTools.All.Concat(LateralTools.All).Concat(CaseTools.All).Single(x => x.Name == tool);
        return (JsonObject)t.Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);
    }

    static void Refused(OfflineEtabs link, object args, string says)
    {
        var before = ModelFile.ToJson(link.Model);
        try { Run(link, "edit_model_text", args); }
        catch (ToolInputException ex)
        {
            Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), $"'{says}' expected in: {ex.Message}");
            Check(ModelFile.ToJson(link.Model) == before, "a refused edit changed the model");
            return;
        }
        throw new Exception($"edit_model_text took {JsonSerializer.Serialize(args)}");
    }

    static string Text(ICsiLink link, string section) => Run(link, "model_text", new { section })["text"]!.GetValue<string>();

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("model text: every section is listed, and writing a section back as it is changes nothing", () =>
        {
            var link = Frame();
            var list = Run(link, "model_text", new { })["sections"]!.AsArray().Select(s => s!["section"]!.GetValue<string>()).ToList();
            foreach (var h in new[] { "STORIES - IN SEQUENCE FROM TOP", "POINT COORDINATES", "LINE CONNECTIVITIES", "LOAD PATTERNS", "LOAD CASES" })
                Check(list.Contains(h), h + " is listed: " + string.Join(", ", list));
            E2kWriter.SavedAt = new DateTime(2026, 1, 1);
            var before = E2kWriter.Write(link.Model);
            foreach (var h in list.Where(h => h != "PROGRAM INFORMATION"))
            {
                var text = Text(link, h);
                if (text.Length == 0) continue;
                var a = Run(link, "edit_model_text", new { section = h, text });
                Check(a["changes"]!.AsObject().Count == 0, h + " written back as it is changed: " + a.ToJsonString());
            }
            var now = E2kWriter.Write(link.Model);
            E2kWriter.SavedAt = null;
            Check(now == before, "the model text is the same after writing every section back: " + string.Join(" | ", now.Split('\n').Except(before.Split('\n')).Take(4)) + " <> " + string.Join(" | ", before.Split('\n').Except(now.Split('\n')).Take(4)));
        });

        yield return ("model text: a combination and an unknown section written by hand go in, and undo takes them out", () =>
        {
            var link = Frame();
            HandModel.Reset();
            var combos = Text(link, "LOAD COMBINATIONS");
            var text = (combos.Length > 0 ? combos + "\n" : "") + "  COMBO \"TXT\"  TYPE \"Linear Add\"\n  COMBO \"TXT\"  LOADCASE \"Dead\"  SF 1.4";
            var check = Run(link, "edit_model_text", new { section = "LOAD COMBINATIONS", text, check_only = true });
            Check(check["applied"]!.GetValue<bool>() == false && !link.Combos().Contains("TXT"), "check_only changes nothing");
            var r = HandModel.Handle(link, JsonDocument.Parse(JsonSerializer.Serialize(new { tool = "edit_model_text", args = new { section = "LOAD COMBINATIONS", text } })).RootElement);
            Check(r["ok"]!.GetValue<bool>() && link.Combos().Contains("TXT"), "the combination is in the model: " + r.ToJsonString());
            Check(ViaE2k(link.Model).Combos.Any(c => c.Name == "TXT"), "and in the .e2k");
            HandModel.Handle(link, JsonDocument.Parse(JsonSerializer.Serialize(new { tool = "edit_model_text", args = new { section = "GROUPS", text = "  GROUP \"G-TXT\"  COLOR \"Red\"" } })).RootElement);
            Check(E2kWriter.Write(link.Model).Contains("GROUP \"G-TXT\""), "an unknown section is kept word for word");
            Check(Run(link, "model_text", new { section = "groups" })["text"]!.GetValue<string>().Contains("G-TXT"), "and read back by any case");
            HandModel.Handle(link, JsonDocument.Parse("{\"undo\":true}").RootElement);
            Check(link.Combos().Contains("TXT") && !E2kWriter.Write(link.Model).Contains("G-TXT"), "undo takes the section out again");
        });

        yield return ("model text: text that breaks the file or the model is refused and changes nothing", () =>
        {
            var link = Frame();
            var lines = Text(link, "LINE CONNECTIVITIES").Split('\n');
            Refused(link, new { section = "LINE CONNECTIVITIES", text = lines[0] }, "Nothing was changed");
            Refused(link, new { section = "POINT COORDINATES", text = "" }, "Nothing was changed");
            Refused(link, new { section = "LOAD CASES", text = "$ LOAD COMBINATIONS\n  COMBO \"X\" TYPE \"Linear Add\"" }, "heading line");
            Refused(link, new { section = "END OF MODEL FILE", text = "x" }, "frame");
            Refused(link, new { section = "PROGRAM INFORMATION", text = "x" }, "version");
            Refused(link, new { section = "NO SUCH SECTION", text = "" }, "no section");
            var p = Text(link, "POINT COORDINATES").Split('\n');
            Refused(link, new { section = "POINT COORDINATES", text = string.Join("\n", p.Append(p[0])) }, "Nothing was changed");
        });

        yield return ("model text: a code seismic load keeps its code through a text edit, and its story forces follow", () =>
        {
            var link = Frame();
            Run(link, "set_lateral_load", new { name = "EQX", kind = "seismic", code = "ASCE 7-16", values = new { Dir = "X", R = "8", I = "1", Ss = "1.5", S1 = "0.6", SiteClass = "D" } });
            var cases = Text(link, "LOAD CASES");
            Run(link, "edit_model_text", new { section = "LOAD CASES", text = cases });
            var p = link.Model.LoadPatterns.Single(x => x.Name == "EQX");
            Check(p.Auto is not null && p.Auto.Code == "ASCE 7-16" && p.Auto.Forces.Count > 0, "EQX still has its code and story forces");
        });
    }

    static StructuralModel ViaE2k(StructuralModel m) => E2kReader.Read(E2kWriter.Write(m));
}
