using CsiProMax.Assistant;
using CsiProMax.Assistant.Etabs;

/// <summary>What the full field test of 0.4.8 found on the field-test PC on 2026-11-25 (ETABS 23, SAP2000 26).</summary>
static class FieldRun048Tests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("field test 0.4.8: ETABS naming the saved scratch model by its .$et still counts as the scratch model", () =>
        {
            var folder = Path.Combine("C:", "Users", "x", "AppData", "Local", "CsiProMax", "Field test", "ETABS 2026-11-25 181331");
            Check(ScratchProgram.SameName(Path.Combine(folder, "cpm-field-test-949e09.$et"), Path.Combine(folder, "cpm-field-test-949e09.edb")),
                "the .$et ETABS names after saving the .EDB was taken for another model");
            Check(ScratchProgram.SameName("C:/a/CPM-FIELD-TEST-949E09.EDB", @"C:\a\cpm-field-test-949e09.edb"), "case and slashes");
            Check(!ScratchProgram.SameName("C:/a/Tower-B.$et", "C:/a/cpm-field-test-949e09.edb"), "the engineer's model was taken for the scratch model");
            Check(!ScratchProgram.SameName("C:/a/cpm-field-test-111111.edb", "C:/a/cpm-field-test-949e09.edb"), "another run's scratch model was taken for this one's");
        });

        yield return ("field test 0.4.8: units come from GetPresentUnits when GetPresentUnits_2 gives none (SAP2000 26)", () =>
        {
            Check(ComEtabs.Units(6) == (4, 6, 2), "kN_m_C");
            Check(ComEtabs.Units(10) == (3, 6, 2), "N_m_C");
            Check(ComEtabs.Units(4) == (2, 2, 1), "kip_ft_F");
            Check(ComEtabs.Units(13) == (4, 5, 2), "kN_cm_C");
            Check(ComEtabs.Units(0) == (0, 0, 0) && ComEtabs.Units(99) == (0, 0, 0), "an unknown number gives nothing");
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Etabs", "ComEtabs.cs"));
            var two = source.IndexOf("GetPresentUnits_2(", StringComparison.Ordinal);
            var one = source.IndexOf("Units(Convert.ToInt32(m.GetPresentUnits()))", StringComparison.Ordinal);
            Check(two > 0 && one > two, "Info does not fall back to GetPresentUnits after GetPresentUnits_2");
        });
    }
}
