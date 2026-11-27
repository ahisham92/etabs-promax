using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>
/// ETABS 23.2's Define > Load Combinations, Auto Construction Sequence Case, Walking Vibrations, Floor Vibrations, Table
/// Named Sets and Named Assignment Sets in the Engine: each item goes into the .e2k as ETABS's own export writes it
/// (field/reference-forms/etabs/e2k/cpm-forms-scratch-define.e2k), and reads back the same.
/// </summary>
static class EtabsDefineFormTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("EtabsDefine", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args)
    {
        var t = EtabsDefineTools.All.Concat(HandTools.All).Concat(CaseTools.All).Single(x => x.Name == tool);
        return (JsonObject)t.Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);
    }

    static List<string> Section(string e2k, string heading)
    {
        var lines = e2k.Replace("\r", "").Split('\n').ToList();
        var at = lines.IndexOf("$ " + heading);
        if (at < 0) return [];
        return lines.Skip(at + 1).TakeWhile(l => !l.StartsWith("$ ", StringComparison.Ordinal)).Where(l => l.Trim().Length > 0).ToList();
    }

    static List<string> Headings(string e2k) => e2k.Replace("\r", "").Split('\n').Where(l => l.StartsWith("$ ", StringComparison.Ordinal)).Select(l => l[2..]).ToList();

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("ETABS Detailing rules: kept with the model (no .e2k line, as ETABS's .e2k has none), read back, undone, back to defaults", () =>
        {
            var link = Frame(); var m = link.Model;
            JsonObject Do(string tool, object args) => (JsonObject)DetailingTools.All.Single(x => x.Name == tool).Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);
            var before = E2kWriter.Write(m);
            Do("set_detailing_rules", new { form = "column", values = new { main = new { vert = "auto" }, splice = new { kicker = 0.1 } } });
            Do("set_detailing_rules", new { form = "beam", values = new { main = new { curt = "rules" } } });
            var r = Do("detailing_rules", new { })["rules"]!;
            Check(r["column"]?["main"]?["vert"]?.GetValue<string>() == "auto" && r["column"]?["splice"]?["kicker"]?.GetValue<double>() == 0.1, "column rule: " + r.ToJsonString());
            Check(r["beam"]?["main"]?["curt"]?.GetValue<string>() == "rules", "beam rule: " + r.ToJsonString());
            Check(m.Kept![DetailingTools.KeptName].Select(x => x["Form"]).SequenceEqual(["beam", "column"]), "rows in the menu's order");
            Check(E2kWriter.Write(m) == before, "the .e2k does not change (ETABS keeps detailing rules in its database only)");
            Do("set_detailing_rules", new { form = "beam", values = (object?)null });
            Check(Do("detailing_rules", new { })["rules"]!["beam"] is null, "beam back to defaults");
            var refused = false;
            try { Do("set_detailing_rules", new { form = "slabs", values = new { } }); } catch (ToolInputException) { refused = true; }
            Check(refused, "an unknown rule is refused");
        });
        yield return ("ETABS Define: walking vibration, excitation set and construction sequence go out as ETABS's own .e2k lines, in ETABS's order", () =>
        {
            var link = Frame(); var m = link.Model;
            Run(link, "define_e2k_item", new { section = "WALKING VIBRATIONS", name = "Walk1", lines = new[] {
                "WALKINGVIBRATION \"Walk1\"  STORY \"Story1\"  USERMODALCASE \"Modal\"  MODALDAMPING 0.03  PERSONWEIGHT 165  PEAKLOADFACTOR 1.4  WALKINGFREQUENCY 2  FORWARDSPEED 60  DURATIONOFIMPACT 0.45  PEAKACCELTHRESHHOLDOPTION \"User\"  PEAKACCELTHRESHHOLD 1",
                "WALKINGVIBRATIONPOINT \"Walk1\"  0  144", "WALKINGVIBRATIONPOINT \"Walk1\"  288  144" } });
            Run(link, "define_e2k_item", new { section = "FLOOR VIBRATION EXCITATION SET", name = "exset", lines = new[] {
                "FLOORVIBRATIONEXCITATIONSET  \"exset\"  STORY \"Story1\"  TYPE \"Walking\"  EXCITATIONPOINT \"1\"  RESPONSEPOINT \"2\"  MODALCASE \"Modal\"  STEADYSTATEFUNC \"UnifSS\"  HYSTERETICDAMPINGSTIFFNESSCOEF 0.05  LASTFREQUENCY 20" } });
            Run(link, "define_e2k_item", new { section = "AUTO CONSTRUCTION SEQUENCE CASE", lines = new[] {
                "AUTOCONSTRUCTIONCASE NAME  \"AutoSeq\"  NUMBERSTORIES  1 EXCLUDEDGROUP  \"All\"  USEINDESIGNCOMBOS  \"Yes\"", "AUTOCONSTRUCTIONCASE LOADPAT  \"Dead\"  1" } });
            var e2k = E2kWriter.Write(m);
            var h = Headings(e2k);
            Check(Section(e2k, "WALKING VIBRATIONS").Count == 3 && Section(e2k, "WALKING VIBRATIONS")[0].StartsWith("  WALKINGVIBRATION \"Walk1\""), "walking lines: " + string.Join(" | ", Section(e2k, "WALKING VIBRATIONS")));
            Check(h.IndexOf("AUTO CONSTRUCTION SEQUENCE CASE") == h.IndexOf("LOAD CASES") + 1, "the construction sequence follows LOAD CASES: " + string.Join(", ", h));
            Check(h.IndexOf("FLOOR VIBRATION EXCITATION SET") == h.IndexOf("WALKING VIBRATIONS") + 1 && h.IndexOf("WALKING VIBRATIONS") > h.IndexOf("LOAD CASES"), "walking then floor vibration: " + string.Join(", ", h));
            // Read back and written again: the same lines (lossless).
            var again = E2kWriter.Write(E2kReader.Read(e2k));
            foreach (var s in new[] { "WALKING VIBRATIONS", "FLOOR VIBRATION EXCITATION SET", "AUTO CONSTRUCTION SEQUENCE CASE" })
                Check(Section(again, s).SequenceEqual(Section(e2k, s)), s + " reads back the same");
            // Renamed, then deleted: the item's lines only.
            Run(link, "define_e2k_item", new { section = "WALKING VIBRATIONS", name = "Walk2", was = "Walk1", lines = new[] { "WALKINGVIBRATION \"Walk2\"  STORY \"Story1\"", "WALKINGVIBRATIONPOINT \"Walk2\"  0  0" } });
            Check(Section(E2kWriter.Write(m), "WALKING VIBRATIONS").All(l => l.Contains("\"Walk2\"")), "renamed");
            Run(link, "define_e2k_item", new { section = "WALKING VIBRATIONS", name = "Walk2", lines = Array.Empty<string>() });
            Check(!Headings(E2kWriter.Write(m)).Contains("WALKING VIBRATIONS"), "an empty section goes");
            Check(Headings(E2kWriter.Write(m)).Contains("FLOOR VIBRATION EXCITATION SET"), "the section that followed it stays");
            bool refused = false; try { Run(link, "define_e2k_item", new { section = "TABLE SETS", name = "x", lines = new[] { "X \"x\"" } }); } catch (ToolInputException) { refused = true; }
            Check(refused, "a section the forms do not keep is refused");
            refused = false; try { Run(link, "define_e2k_item", new { section = "WALKING VIBRATIONS", name = "W", lines = new[] { "WALKINGVIBRATION \"Other\"" } }); } catch (ToolInputException) { refused = true; }
            Check(refused, "a line of another item is refused");
        });

        yield return ("ETABS Define: named assignment sets and table named sets keep ETABS's lines; e2k_items gives them back as words", () =>
        {
            var link = Frame(); var m = link.Model;
            Run(link, "define_e2k_item", new { section = "NAMED FRAME MODIFIERS", name = "FMod1", lines = new[] { "NAMEDFMODIFIER  \"FMod1\"  PROPMODI33 0.35" } });
            Run(link, "define_e2k_item", new { section = "NAMED SHELL MODIFIERS", name = "ShMod1", lines = new[] { "NAMEDSHMODIFIER  \"ShMod1\"  PROPMODM11 0.25" } });
            Run(link, "define_e2k_item", new { section = "NAMED FRAME RELEASES", name = "FRel1", lines = new[] { "NAMEDFRELEASE  \"FRel1\"  RELEASE \"M3J\"", "NAMEDFRELEASE  \"FRel1\"" } });
            Run(link, "define_e2k_item", new { section = "DATABASE TABLE NAMED SETS", name = "DBNSet1", lines = new[] { "DBTABLESET \"DBNSet1\"  UNFORMATTED  \"No\"  EXPOSEALL  \"No\"", "DBTABLESETTABLEKEY \"DBNSet1\"  \"Story Definitions\"", "DBTABLESETLOADPATTERN \"DBNSet1\"  \"Dead\"" } });
            var e2k = E2kWriter.Write(m); var h = Headings(e2k);
            Check(h.IndexOf("NAMED SHELL MODIFIERS") == h.IndexOf("NAMED FRAME MODIFIERS") + 1 && h.IndexOf("NAMED FRAME RELEASES") == h.IndexOf("NAMED SHELL MODIFIERS") + 1, "named sets in ETABS's order: " + string.Join(", ", h));
            Check(Section(e2k, "NAMED FRAME RELEASES").Count == 2, "the release keeps both lines");
            var back = (JsonArray)Run(link, "e2k_items", new { section = "NAMED FRAME MODIFIERS" })["lines"]!;
            Check(back.Count == 1 && back[0]![1]!.GetValue<string>() == "\"FMod1\"" && back[0]![3]!.GetValue<string>() == "0.35", "e2k_items: " + back.ToJsonString());
            var again = E2kWriter.Write(E2kReader.Read(e2k));
            foreach (var s in new[] { "NAMED FRAME MODIFIERS", "NAMED SHELL MODIFIERS", "NAMED FRAME RELEASES", "DATABASE TABLE NAMED SETS" })
                Check(Section(again, s).SequenceEqual(Section(e2k, s)), s + " reads back the same");
        });

        yield return ("ETABS Define: combination notes, default design combos not written as COMBO lines, and Convert Combos to Nonlinear Cases", () =>
        {
            var link = Frame(); var m = link.Model;
            var dead = m.LoadPatterns.First(p => p.Type == "Dead").Name; var live = m.LoadPatterns.First(p => p.Type.Contains("Live")).Name;
            Run(link, "define_combination", new { name = "Comb1", type = "Linear Add", notes = "service", cases = new[] { new { @case = dead, factor = 1.2 }, new { @case = live, factor = 1.6 } } });
            Run(link, "define_combination", new { name = "DConS1", type = "Linear Add", auto = true, cases = new[] { new { @case = dead, factor = 1.4 } } });
            var e2k = E2kWriter.Write(m);
            var combos = Section(e2k, "LOAD COMBINATIONS");
            Check(combos.Any(l => l.Contains("\"Comb1\"")) && !combos.Any(l => l.Contains("\"DConS1\"")), "user combos only in the .e2k: " + string.Join(" | ", combos));
            var rows = ModelerTables.ComboRows(m);
            Check(rows.Count == 3 && rows.Any(r => r["Name"] == "DConS1" && r["Is Auto"] == "Yes") && rows.Any(r => r["Name"] == "Comb1" && r["Notes"] == "service"), "the table rows carry the notes and the auto combo");
            bool refused = false; try { Run(link, "define_combination", new { name = "DConS1", was = "DConS1", type = "Linear Add", cases = new[] { new { @case = dead, factor = 1 } } }); } catch (ToolInputException) { refused = true; }
            Check(refused, "an auto combination is not editable");
            Run(link, "convert_combo_to_nonlinear", new { name = "Comb1" });
            var nl = m.LoadCases.Single(c => c.Name == "Comb1-NL");
            Check(nl.Lines[0].Contains("\"Nonlinear Static\"") && nl.Lines.Count(l => l[0] == "LOADPAT") == 2, "the case holds the combination's patterns: " + string.Join(" | ", nl.Lines.Select(l => string.Join(" ", l))));
            var c1 = m.Combos.Single(c => c.Name == "Comb1");
            Check(c1.Lines.Count(l => l[0] is "LOADCASE" or "LOADCOMBO") == 1 && c1.Lines.Any(l => l.Count > 1 && l[1] == "\"Comb1-NL\""), "the combination holds the new case alone");
            var back = E2kReader.Read(E2kWriter.Write(m));
            Check(back.LoadCases.Exists(c => c.Name == "Comb1-NL") && back.Combos.Exists(c => c.Name == "Comb1"), "both read back");
            Run(link, "define_combination", new { name = "Env1", type = "Envelope", cases = new[] { new { @case = "Comb1", factor = 1.0 } } });
            refused = false; try { Run(link, "convert_combo_to_nonlinear", new { name = "Env1" }); } catch (ToolInputException) { refused = true; }
            Check(refused, "an Envelope is not converted");
        });
    }
}
