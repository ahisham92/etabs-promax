using CsiProMax.CsiTables;
using CsiProMax.F2k;
using CsiProMax.Model;

/// <summary>
/// The Engine's SAFE forms (Ahmed 2026-11-27: every SAFE form identical and working as SAFE's): what a form sets is a
/// row of SAFE's own table (SafeTables, SAFE's field names from its own .f2k files), it goes into the .f2k over the rows
/// the writer makes, changes only the fields the form set, and never reaches ETABS or SAP2000.
/// </summary>
static class SafeFormTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    static Dictionary<string, string> R(params (string, string)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("SAFE forms: SAFE's own tables and fields are known, from SAFE's own files", () =>
        {
            Check(SafeTables.All.Count >= 70, $"SAFE's tables: {SafeTables.All.Count}");
            foreach (var t in new[] { "Slab Property Definitions", "Load Pattern Definitions", "Load Combination Definitions", "Material Properties - Concrete Data", "Concrete Slab Design Preferences - ACI 318-19", "Tendon Section Properties" })
                Check(SafeTables.FormTables.Contains(t), "a SAFE form table is missing: " + t);
            Check(!SafeTables.FormTables.Any(t => t.Contains("Connectivity", StringComparison.OrdinalIgnoreCase) || t.Contains("Assignments", StringComparison.OrdinalIgnoreCase)), "object tables are not form tables");
            Check(SafeTables.All["Slab Property Definitions"].Any(f => f.Key == "Thickness" && f.Label == "Slab Thickness"), "the slab thickness has SAFE's name and its API key");
            Check(SafeTables.Seen["Load Pattern Definitions"]["Type"].Contains("Prestress-Final"), "the choices SAFE wrote are kept");
            Check(SafeTables.KeyField(SafeTables.Prefix + "Slab Property Definitions") == "Name" && SafeTables.KeyField(SafeTables.Prefix + "Concrete Slab Design Preferences - ACI 318-19") is null, "keyed and one-row tables");
        });

        yield return ("SAFE forms: a form's values go into the .f2k over the written rows, by SAFE's field names or API keys", () =>
        {
            var file = "safe-RC-PN-v23.f2k";
            var text = Sample(file);
            var before = CsiTableText.Parse(text);
            var m = F2kReader.Read(text, file).Model;
            var slab = before["SLAB PROPERTY DEFINITIONS"][0];
            var name = slab["Name"];
            // A field by its API key (Thickness) and one by SAFE's name; a new pattern; the slab design preferences row.
            DefineTables.Set(m, SafeTables.Prefix + "Slab Property Definitions", name, [R(("Thickness", "9.5"), ("m11 Modifier", "0.35"))]);
            DefineTables.Set(m, SafeTables.Prefix + "Load Pattern Definitions", "CPM-SF-LIVE", [R(("Type", "Live"), ("Self Weight Multiplier", "0"))]);
            DefineTables.Set(m, "safe | concrete slab design preferences - aci 318-19", "", [R(("CoverTop", "1.25"))]);
            DefineTables.Set(m, SafeTables.Prefix + "Load Combination Definitions", "CPM-SF-COMB", [R(("Type", "Linear Add"), ("Load Name", "DEAD"), ("SF", "1.2")), R(("Type", "Linear Add"), ("Load Name", "LIVE"), ("SF", "1.6"))]);
            var after = CsiTableText.Parse(F2kWriter.Write(m).Text);
            var s = after["SLAB PROPERTY DEFINITIONS"].Single(r => r["Name"] == name);
            Check(s["Slab Thickness"] == "9.5" && s["m11 Modifier"] == "0.35", "the form's slab fields: " + string.Join(" ", s.Select(kv => kv.Key + "=" + kv.Value)));
            foreach (var (k, v) in slab.Where(kv => kv.Key is not ("Slab Thickness" or "m11 Modifier")))
                Check(s.GetValueOrDefault(k) == v, $"the slab's {k} stays SAFE's ({v}), it is {s.GetValueOrDefault(k)}");
            Check(after["LOAD PATTERN DEFINITIONS"].Any(r => r["Name"] == "CPM-SF-LIVE" && r["Type"] == "Live"), "the new pattern is in the .f2k");
            Check(after["LOAD PATTERN DEFINITIONS"].Count == before["LOAD PATTERN DEFINITIONS"].Count + 1, "the other patterns stay");
            var pref = after.First(t => t.Key.StartsWith("CONCRETE SLAB DESIGN PREFERENCES", StringComparison.Ordinal)).Value;
            Check(pref.Count == 1 && pref[0]["CoverTop"] == "1.25", "the slab design preferences' cover: " + string.Join(" ", pref[0].Select(kv => kv.Key + "=" + kv.Value)));
            Check(after["LOAD COMBINATION DEFINITIONS"].Count(r => r["Name"] == "CPM-SF-COMB") == 2, "a combination's two rows");
            // Tables the forms did not touch are SAFE's, row for row.
            foreach (var t in before.Keys.Where(t => t is not ("SLAB PROPERTY DEFINITIONS" or "LOAD PATTERN DEFINITIONS" or "LOAD COMBINATION DEFINITIONS") && !t.StartsWith("CONCRETE SLAB DESIGN PREFERENCES", StringComparison.Ordinal)
                && !t.EndsWith("SUMMARY", StringComparison.Ordinal) && !t.StartsWith("MATERIAL LIST", StringComparison.Ordinal)))
                Check(after.TryGetValue(t, out var rows) && rows.Count == before[t].Count, t + " stays SAFE's");
            // Kept through the .cpm.
            var j = ModelFile.FromJson(ModelFile.ToJson(m));
            Check(CsiTableText.Parse(F2kWriter.Write(j).Text)["SLAB PROPERTY DEFINITIONS"].Single(r => r["Name"] == name)["Slab Thickness"] == "9.5", "the form's value is kept in the .cpm");
            // Taking the rows out brings SAFE's own back.
            DefineTables.Set(m, SafeTables.Prefix + "Slab Property Definitions", name, []);
            Check(CsiTableText.Parse(F2kWriter.Write(m).Text)["SLAB PROPERTY DEFINITIONS"].Single(r => r["Name"] == name)["Slab Thickness"] == slab["Slab Thickness"], "no rows: SAFE's own thickness again");
        });

        yield return ("SAFE forms: a field SAFE's table does not have is refused, and SAFE's rows never go to ETABS or SAP2000", () =>
        {
            var m = F2kReader.Read(Sample("safe-PT-SL-v23.f2k"), "safe-PT-SL-v23.f2k").Model;
            var refused = false;
            try { DefineTables.Set(m, SafeTables.Prefix + "Slab Property Definitions", "X", [R(("Nope", "1"))]); }
            catch (ArgumentException) { refused = true; }
            Check(refused && m.DefinitionTables?.ContainsKey(SafeTables.Prefix + "Slab Property Definitions") != true, "a field SAFE's table has not is refused and nothing is kept");
            DefineTables.Set(m, SafeTables.Prefix + "Group Definitions", "CPM-G", [R(("Color", "Red"))]);
            Check(ProgramOptions.HasDefinitionTable(ProgramOptions.Safe, SafeTables.Prefix + "Group Definitions"), "SAFE has it");
            Check(!ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, SafeTables.Prefix + "Group Definitions") && !ProgramOptions.HasDefinitionTable(ProgramOptions.Sap2000, SafeTables.Prefix + "Group Definitions"), "ETABS and SAP2000 do not");
            Check(DefineTables.FullName("safe | group definitions") == SafeTables.Prefix + "Group Definitions" && DefineTables.FullName("Group Definitions") != SafeTables.Prefix + "Group Definitions", "names");
        });
    }
}
