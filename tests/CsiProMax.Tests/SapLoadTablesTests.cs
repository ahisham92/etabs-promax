using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// SAP2000's own load forms (Define > Load Patterns with every Auto Lateral code, Load Cases of every type and their
/// sub-forms, Load Combinations): every field kept as SAP2000's table rows (SapLoadTables), laid over the tables the
/// .s2k writer makes from the model, read back from the .s2k, following renames and dropped with what they belong to.
/// </summary>
static class SapLoadTablesTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Sap()
    {
        var link = new OfflineEtabs(Templates.ConcreteFrame("Sap", [6, 5], [4], 2, 3.2));
        link.Model.Target = "sap2000";
        return link;
    }

    static JsonObject Run(OfflineEtabs link, string tool, object args) =>
        (JsonObject)DefineTools.All.Concat(HandTools.All).Concat(CaseTools.All).Single(x => x.Name == tool)
            .Invoke(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static List<Dictionary<string, string>> Rows(string s2k, string table) =>
        CsiProMax.CsiTables.CsiTableText.Parse(s2k).GetValueOrDefault(table) ?? [];

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("sap2000 load forms: a pattern's SAP2000 type and auto seismic code with its parameters go into the .s2k and come back", () =>
        {
            var link = Sap();
            Run(link, "define_load_pattern", new { name = "EQX", type = "Seismic", self_weight = 0 });
            Run(link, "define_load_pattern", new { name = "WAVE1", type = "Other", self_weight = 0 });
            Run(link, "define_table_rows", new { table = "Load Pattern Definitions", name = "EQX", rows = new[] { new { DesignType = "Quake", AutoLoad = "ASCE 7-16", SelfWtMult = "0" } } });
            Run(link, "define_table_rows", new { table = "Load Pattern Definitions", name = "WAVE1", rows = new[] { new { DesignType = "Wave", SelfWtMult = "0" } } });
            Run(link, "define_table_rows", new { table = "Auto Seismic - ASCE 7-16", name = "EQX", rows = new[] { new { Dir = "X", R = "8", Ss = "1.5" } } });
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            var eqx = Rows(s2k, "LOAD PATTERN DEFINITIONS").Single(r => r["LoadPat"] == "EQX");
            Check(eqx["DesignType"] == "Quake" && eqx["AutoLoad"] == "ASCE 7-16", "EQX is Quake with ASCE 7-16: " + string.Join(" ", eqx));
            Check(Rows(s2k, "LOAD PATTERN DEFINITIONS").Single(r => r["LoadPat"] == "WAVE1")["DesignType"] == "Wave", "WAVE1 keeps SAP2000's Wave type");
            var code = Rows(s2k, "AUTO SEISMIC - ASCE 7-16");
            Check(code.Count == 1 && code[0]["R"] == "8" && code[0]["Dir"] == "X", "the code's row: " + s2k);
            // Back from the file (its tables, not the model carried with it), and out again: the same once.
            var back = S2kReader.Read(s2k, "a").Model;
            Check(back.LoadPatterns.Single(p => p.Name == "EQX").Type == "Seismic", "EQX reads back as Seismic");
            var again = S2kWriter.Write(back, "b.s2k").Text;
            Check(Rows(again, "AUTO SEISMIC - ASCE 7-16").Count == 1 && Rows(again, "AUTO SEISMIC - ASCE 7-16")[0]["Ss"] == "1.5", "the code's row once after the round trip");
            Check(again.Split("TABLE:  \"AUTO SEISMIC - ASCE 7-16\"").Length - 1 <= 1 && Rows(again, "LOAD PATTERN DEFINITIONS").Single(r => r["LoadPat"] == "EQX")["AutoLoad"] == "ASCE 7-16", "AutoLoad kept");
            var data = Run(link, "definition_data", new { });
            Check(data["tables"]!["Auto Seismic - ASCE 7-16"]!["rows"]!.AsArray().Count == 1 && data["sap_load_fields"]!["Auto Wind - ASCE 7-16"] is not null, "definition_data gives the rows and every table's fields");
        });

        yield return ("sap2000 load forms: the model's own fields win in the tables it makes, SAP2000's other fields come from the forms", () =>
        {
            var link = Sap();
            var pattern = link.Model.LoadPatterns[0].Name;
            Run(link, "define_load_case", new { name = "NLS", type = "Nonlinear Static", loads = new[] { new { pattern, factor = 1.0 } }, geometric = "P-Delta" });
            Run(link, "define_table_rows", new { table = "Case - Static 4 - Nonlinear Parameters", name = "NLS", rows = new[] { new { GeoNonLin = "None", MaxTotal = "500", SolScheme = "Iterative Events" } } });
            Run(link, "define_table_rows", new { table = "Case - Static 2 - Nonlinear Load Application", name = "NLS", rows = new[] { new { LoadApp = "Displ Ctrl", DisplType = "Conjugate", TargetDispl = "0.2", MonitorDOF = "U1", MonitorJt = "1" } } });
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            var p4 = Rows(s2k, "CASE - STATIC 4 - NONLINEAR PARAMETERS").Single(r => r["Case"] == "NLS");
            Check(p4["GeoNonLin"] == "P-Delta" && p4["MaxTotal"] == "500" && p4["SolScheme"] == "Iterative Events", "STATIC 4: " + string.Join(" ", p4));
            Check(Rows(s2k, "CASE - STATIC 2 - NONLINEAR LOAD APPLICATION").Single()["LoadApp"] == "Displ Ctrl", "STATIC 2 written");
            // A case type the model has no place for (Multi-step Static) lives in the rows alone, and comes back.
            Run(link, "define_table_rows", new { table = "Load Case Definitions", name = "MS1", rows = new[] { new { Type = "LinMultiStep", InitialCond = "Zero", DesignType = "Other", RunCase = "Yes" } } });
            Run(link, "define_table_rows", new { table = "Case - Multistep Static 1 - Load Assignments", name = "MS1", rows = new[] { new { LoadType = "Load pattern", LoadName = pattern, LoadSF = "1", StepRange = "All" } } });
            s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(Rows(s2k, "LOAD CASE DEFINITIONS").Count(r => r["Case"] == "MS1" && r["Type"] == "LinMultiStep") == 1, "MS1 in Load Case Definitions");
            var back = S2kReader.Read(s2k, "a").Model;
            Check(Rows(S2kWriter.Write(back, "b.s2k").Text, "CASE - MULTISTEP STATIC 1 - LOAD ASSIGNMENTS").Count == 1, "MS1's loads after the round trip");
        });

        yield return ("sap2000 load forms: the rows follow a renamed pattern and go with a deleted one; never sent to ETABS", () =>
        {
            var link = Sap();
            Run(link, "define_load_pattern", new { name = "W1", type = "Wind", self_weight = 0 });
            Run(link, "define_table_rows", new { table = "Load Pattern Definitions", name = "W1", rows = new[] { new { DesignType = "Wind", AutoLoad = "ASCE 7-16" } } });
            Run(link, "define_table_rows", new { table = "Auto Wind - ASCE 7-16", name = "W1", rows = new[] { new { WindSpeed = "45", Exposure = "C" } } });
            Run(link, "define_load_pattern", new { name = "W2", was = "W1", type = "Wind", self_weight = 0, position = 0 });
            Check(link.Model.LoadPatterns[0].Name == "W2", "position 0 puts W2 first");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(Rows(s2k, "AUTO WIND - ASCE 7-16").Single()["LoadPat"] == "W2", "the code's row follows the new name");
            Run(link, "delete_definition", new { kind = "load_pattern", name = "W2" });
            s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(Rows(s2k, "AUTO WIND - ASCE 7-16").Count == 0 && !Rows(s2k, "LOAD PATTERN DEFINITIONS").Any(r => r["LoadPat"] == "W2"), "the deleted pattern's rows are not written");
            Check(!ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, "Load Pattern Definitions") && !ProgramOptions.HasDefinitionTable(ProgramOptions.Etabs, "Case - Static 1 - Load Assignments"), "SAP2000's load tables never go to ETABS");
        });
    }
}
