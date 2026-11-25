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

        yield return ("field test 0.4.8: SAP2000's design summaries read the group ALL, not the selection", () =>
        {
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Sap2000", "SapTools.cs"));
            var at = source.IndexOf("private static JsonNode DesignRatios(", StringComparison.Ordinal);
            var body = source.Substring(at, source.IndexOf("GetSummaryResults(", at, StringComparison.Ordinal) - at);
            Check(body.Contains("[(\"ALL\", 1)]") && !body.Contains("[(\"ALL\", 2)]"), "design_ratios does not ask for the group ALL (eItemType 1)");
        });

        yield return ("field test 0.4.8: a snapshot is taken even when a result table answers with no fields", () =>
        {
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Tools", "ResultTools.cs"));
            var at = source.IndexOf("internal static JsonObject Capture(", StringComparison.Ordinal);
            var body = source.Substring(at, source.IndexOf("private static JsonNode Snapshot(", at, StringComparison.Ordinal) - at);
            Check(body.Contains("ArgumentNullException") && body.Contains("NullReferenceException"), "the snapshot's results part still lets a table with no fields stop the snapshot");
        });

        yield return ("field test 0.4.8: run_learned_tool outside the chat runs a learned tool that only reads", () =>
        {
            var source = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Assistant", "Panel", "Tools", "LearnedTools.cs"));
            var at = source.IndexOf("public sealed class RunLearnedToolTool", StringComparison.Ordinal);
            var body = source.Substring(at, source.IndexOf("public sealed class DeleteLearnedToolTool", at, StringComparison.Ordinal) - at);
            Check(body.Contains("learned.Execute(app,") && body.Contains("learned.ModifiesModel"), "run_learned_tool still answers 'no such tool' to every call outside the chat");
        });
    }
}
