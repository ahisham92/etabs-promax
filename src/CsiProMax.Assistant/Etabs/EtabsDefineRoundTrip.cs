using System.Globalization;
using System.Text.Json.Nodes;
using CsiProMax.E2k;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The Engine's ETABS 23.2 Define > Load Combinations (notes, a default design combination), Auto Construction Sequence Case,
/// Walking Vibrations, Floor Vibrations > Excitation Sets, Table Named Sets and Named Assignment Sets, proved in ETABS
/// itself: in the Modeler's part each is set as its form sets it (ETABS's own .e2k lines through define_e2k_item, a
/// combination through define_combination), the .e2k is written, and in the scratch ETABS each one is read back from
/// ETABS's tables. A failure names the item and the field, which is how the words marked UNVERIFIED in the forms
/// (viewer.html, etd*) are corrected.
/// </summary>
public static class EtabsDefineRoundTrip
{
    private static string Q(string s) => E2kText.Quote(s);

    /// <summary>In the Modeler's part, before the .e2k is written.</summary>
    public static void Define(FieldContext ctx, OfflineEtabs modeler)
    {
        var m = modeler.Model;
        if (m.Target is "sap2000" or "safe") return;
        var dead = m.LoadPatterns.FirstOrDefault(p => p.Type == "Dead")?.Name ?? m.LoadPatterns.FirstOrDefault()?.Name ?? "Dead";
        var modal = m.LoadCases.FirstOrDefault(c => c.Lines.Any(l => l.Count >= 2 && l[0] == "TYPE" && E2kText.Unquote(l[1]).StartsWith("Modal", StringComparison.OrdinalIgnoreCase)))?.Name;
        var top = m.Stories.Count > 0 ? m.Stories[^1].Name : "Story1";
        var joints = m.LineAssigns.Where(x => x.Story == top).Select(x => m.Lines.Find(l => l.Label == x.Label)).Where(l => l is not null).SelectMany(l => new[] { l!.PointI, l.PointJ }).Distinct().Take(2).ToList();
        void Item(string section, string? name, params string[] lines)
        {
            var json = new JsonObject { ["section"] = section, ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()) };
            if (name is not null) json["name"] = name;
            ctx.Tool("define_e2k_item", json.ToJsonString(), null, $"ETABS form {section}: {name ?? "set"} written as ETABS's own .e2k lines"); // ETABS itself
        }
        ctx.Tool("define_combination", new JsonObject { ["name"] = "CPM-CMBN", ["type"] = "Linear Add", ["notes"] = "CPM combination notes", ["cases"] = new JsonArray(new JsonObject { ["case"] = dead, ["factor"] = 1.2 }) }.ToJsonString(),
            null, "ETABS form Load Combination Data: a combination with notes"); // ETABS itself
        ctx.Tool("define_combination", new JsonObject { ["name"] = "DConS99", ["type"] = "Linear Add", ["auto"] = true, ["cases"] = new JsonArray(new JsonObject { ["case"] = dead, ["factor"] = 1.4 }) }.ToJsonString(),
            null, "ETABS form Add Default Design Combinations: a default design combination (not converted)"); // ETABS itself
        Item("AUTO CONSTRUCTION SEQUENCE CASE", null, $"AUTOCONSTRUCTIONCASE NAME  {Q("AutoSeq")}  NUMBERSTORIES  1 EXCLUDEDGROUP  {Q("All")}  USEINDESIGNCOMBOS  {Q("Yes")}", $"AUTOCONSTRUCTIONCASE LOADPAT  {Q(dead)}  1");
        if (modal is not null)
        {
            Item("WALKING VIBRATIONS", "CPM-WALK", $"WALKINGVIBRATION {Q("CPM-WALK")}  STORY {Q(top)}  USERMODALCASE {Q(modal)}  MODALDAMPING 0.03  PERSONWEIGHT 0.734  PEAKLOADFACTOR 1.4  WALKINGFREQUENCY 2  FORWARDSPEED 1.524  DURATIONOFIMPACT 0.45  PEAKACCELTHRESHHOLDOPTION {Q("User")}  PEAKACCELTHRESHHOLD 1",
                $"WALKINGVIBRATIONPOINT {Q("CPM-WALK")}  0  0", $"WALKINGVIBRATIONPOINT {Q("CPM-WALK")}  3  0");
            if (joints.Count == 2)
                Item("FLOOR VIBRATION EXCITATION SET", "CPM-EX", $"FLOORVIBRATIONEXCITATIONSET  {Q("CPM-EX")}  STORY {Q(top)}  TYPE {Q("Walking")}  EXCITATIONPOINT {Q(joints[0])}  RESPONSEPOINT {Q(joints[1])}  MODALCASE {Q(modal)}  STEADYSTATEFUNC {Q("UnifSS")}  HYSTERETICDAMPINGSTIFFNESSCOEF 0.05  LASTFREQUENCY 20");
        }
        ctx.Tool("e2k_items", """{"section":"AUTO CONSTRUCTION SEQUENCE CASE"}""", a => FieldContext.Expect(a["lines"] is JsonArray { Count: 2 }, "the construction sequence does not read back as its 2 lines: " + a.ToJsonString()),
            "ETABS form Auto Construction Sequence Load Case: its lines read back"); // ETABS itself
        ctx.Tool("define_combination", new JsonObject { ["name"] = "CPM-CNV", ["type"] = "Linear Add", ["cases"] = new JsonArray(new JsonObject { ["case"] = dead, ["factor"] = 1.0 }) }.ToJsonString(), null, "ETABS form Load Combination Data: a combination to convert"); // ETABS itself
        ctx.Tool("convert_combo_to_nonlinear", """{"name":"CPM-CNV"}""", _ => FieldContext.Expect(m.LoadCases.Exists(c => c.Name == "CPM-CNV-NL"), "no CPM-CNV-NL case was made"),
            "ETABS form Load Combinations: Convert Combos to Nonlinear Cases"); // ETABS itself
        Item("NAMED FRAME MODIFIERS", "CPM-FMOD", $"NAMEDFMODIFIER  {Q("CPM-FMOD")}  PROPMODI33 0.35");
        Item("NAMED SHELL MODIFIERS", "CPM-SHMOD", $"NAMEDSHMODIFIER  {Q("CPM-SHMOD")}  PROPMODM11 0.25");
        Item("NAMED FRAME RELEASES", "CPM-FREL", $"NAMEDFRELEASE  {Q("CPM-FREL")}  RELEASE {Q("M3J")}", $"NAMEDFRELEASE  {Q("CPM-FREL")}");
        Item("DATABASE TABLE NAMED SETS", "CPM-DBSET", $"DBTABLESET {Q("CPM-DBSET")}  ISUSERBASEREAC  {Q("No")}  ISALLMODES  {Q("No")}  STARTMODE  1 ENDMODE  12 ISALLBUCKLINGMODES  {Q("No")}  STARTBUCKLINGMODE  1 ENDBUCKLINGMODE  6 MULTISTEP  {Q("Stepped")}  NLSTATIC  {Q("Envelopes")}  MODALHIST  {Q("Envelopes")}  DIRECTHIST  {Q("Envelopes")}  LOADCOMBO  {Q("Envelopes")}  UNFORMATTED  {Q("No")}  EXPOSEALL  {Q("No")}",
            $"DBTABLESETTABLEKEY {Q("CPM-DBSET")}  {Q("Story Definitions")}", $"DBTABLESETLOADPATTERN {Q("CPM-DBSET")}  {Q(dead)}");
    }

    /// <summary>(form, table or a word its name holds, item, fields on the item's first row).</summary>
    private static readonly (string Form, string Table, string Name, (string Field, string Value)[] Fields)[] Checks =
    [
        ("Load Combination Data (notes)", "Load Combination Definitions", "CPM-CMBN", [("Notes", "CPM combination notes"), ("SF", "1.2")]),
        ("Add Default Design Combinations (auto)", "Load Combination Definitions", "DConS99", [("Is Auto", "Yes"), ("SF", "1.4")]),
        ("Auto Construction Sequence Load Case", "Auto Construction Sequence Definition", "AutoSeq", [("Is Active", "Yes"), ("Number Stories", "1"), ("Exclude Group", "All"), ("Replace Dead", "Yes")]),
        ("Walking Vibration Data", "Walking Vibration Definitions", "CPM-WALK", [("Modal Case Option", "User Specified"), ("Damping Ratio", "0.03"), ("Load Factor", "1.4"), ("Threshold Option", "User"), ("User Threshold", "1")]),
        ("Edit Excitation Set", "Floor Vibration Excitation Sets", "CPM-EX", [("Excitation Type", "Walking"), ("Function", "UnifSS"), ("Last Frequency", "20")]),
        ("Named Frame Property Modifier Data", "*Named*Frame*Modifier*", "CPM-FMOD", []),
        ("Named Shell Property Modifier Data", "*Named*Shell*Modifier*", "CPM-SHMOD", []),
        ("Named Frame Release Data", "*Named*Release*", "CPM-FREL", []),
        ("Define Database Table Named Sets", "Database Table Named Set Definitions", "CPM-DBSET", []),
    ];

    /// <summary>In the scratch ETABS that opened the .e2k: every item read back from ETABS's tables.</summary>
    public static void Check(FieldContext ctx)
    {
        var link = ctx.Link;
        foreach (var (form, table, name, fields) in Checks)
            ctx.Step($"ETABS form {form}: ETABS holds {name} as the Engine's form set it", () => // ETABS itself
            {
                if (!FieldTest.IsLive(link)) throw new FieldSkipException("no CSI program is behind this stand-in, so there is no ETABS table to read back"); // ETABS itself
                var tables = link.Tables();
                var words = table.Split('*', StringSplitOptions.RemoveEmptyEntries);
                var found = table.Contains('*')
                    ? tables.Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList()
                    : tables.Where(t => t.Name.Equals(table, StringComparison.OrdinalIgnoreCase)).ToList();
                if (found.Count == 0)
                    throw new FieldCheckException($"ETABS lists no table '{table}' now (it lists a table only once it holds data): {name} did not reach ETABS, or its table is named otherwise. Tables like it: {string.Join("; ", tables.Where(t => words.Any(w => w.Length > 4 && t.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Select(t => t.Name).Take(8))}"); // ETABS itself
                foreach (var t in found)
                {
                    TableData data;
                    try { data = link.ReadTable(t.Key, null, null); }
                    catch (EtabsException) { continue; }
                    var f = data.Fields.ToArray();
                    var row = data.Rows.FirstOrDefault(r => r.Length > 0 && r.Contains(name));
                    if (row is null) continue;
                    var wrong = new List<string>();
                    foreach (var (field, value) in fields)
                    {
                        var i = Array.FindIndex(f, x => Same(x, field));
                        var got = i >= 0 && i < row.Length ? row[i] : null;
                        if (!SameValue(value, got)) wrong.Add($"{field}: form {value}, ETABS {got ?? "(no field; fields " + string.Join(", ", f) + ")"}"); // ETABS itself
                    }
                    FieldContext.Expect(wrong.Count == 0, $"{t.Name} {name}: " + string.Join("; ", wrong));
                    return $"{t.Name}: {name}, {fields.Length} fields the same";
                }
                throw new FieldCheckException($"ETABS's {string.Join(", ", found.Select(t => t.Name))} has no row for {name}"); // ETABS itself
            });
    }

    /// <summary>A field by its key or by the name Show Tables heads it with ("Is Auto" = IsAuto).</summary>
    private static bool Same(string field, string want) =>
        string.Equals(field.Replace(" ", ""), want.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private static bool SameValue(string want, string? got)
    {
        if (got is null) return false;
        if (double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        return string.Equals(want.Trim(), got.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
