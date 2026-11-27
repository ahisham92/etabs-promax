using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// ETABS 23.2's Design > Steel Connection Design > View/Revise Preferences (with its Plate Depth Schedule and Default
/// Connection Type), Design > Live Load Reduction Factors, Set Lateral Displacement Targets and Set Time Period Targets,
/// from the pictures in field/reference-forms/etabs/Design (70-99b).
/// - Live load reduction: ETABS's .e2k keeps it as the CONTROLS line RLLF (METHOD "ASCE7-10" USEDEFAULTMIN "YES" in every
///   ETABS export pictured). The form's whole state is kept in the .cpm ("model|LIVE LOAD REDUCTION"); the RLLF line is
///   written for the tributary area (design code) method, whose words the exports show. The words of the other methods
///   (user parameters, user curves, by stories) are not in any export yet (asked of the capture thread): until they are,
///   the line is left as it was.
/// - Steel connection preferences: ETABS has no database table for them and its .e2k exports pictured have no line for
///   them, so they are kept in the .cpm ("model|STEEL CONNECTION PREFERENCES"), in inches as ETABS's AISC forms show them.
/// - Lateral displacement and time period targets are ETABS's own tables "Design Optimization - ..." (DefineTables).
/// Modeler only.
/// </summary>
public static class EtabsDesignTools
{
    public const string LlrfKept = "model|LIVE LOAD REDUCTION";
    public const string ConnectionKept = "model|STEEL CONNECTION PREFERENCES";

    /// <summary>Tributary Area (Based on Design Code): the codes of ETABS 23.2's list (picture 91), in its order.</summary>
    public static readonly string[] LlrfCodes = ["ASCE7-95", "ASCE7-05", "ASCE7-10", "AS/NZS 1170.1-2002", "Chinese GB 50009-2012", "Eurocode 1991:2002", "Hong Kong COP 2011", "Indian IS 875-1987", "NBCC95", "NBCC2005", "NBCC2010", "UBC97", "KBC 2009"];

    /// <summary>The methods of the form, as the radio buttons read.</summary>
    public static readonly string[] LlrfMethods = ["None", "Code", "User", "Curves", "Stories"];

    /// <summary>Steel Connection Design's codes (picture 73).</summary>
    public static readonly string[] ConnectionCodes = ["AISC 360-10", "AISC 360-16", "AISC 360-22"];

    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Engine's own Design form (no CSI program behind it). In a live program use its own Design menu.");

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("design_settings",
            "ETABS's Design menu settings the Engine's forms keep: Live Load Reduction Factors (method, code, user parameters, minimum factors, application to columns, curves, stories supported; and the .e2k's RLLF line), Steel Connection Design preferences (code, each tab/type item's value, plate depth schedule), and the lateral displacement and time period targets.", // ETABS itself
            """{"type":"object","properties":{}}""",
            Ask.Never, (a, e) => Read(Offline(e).Model)),
        new("set_live_load_reduction",
            "Design > Live Load Reduction Factors, as ETABS 23.2's form: method None, Code (Tributary Area, Based on Design Code: code), User (User Parameters: rate r per area, min_area Amin), Curves (User Defined Curves by tributary area: curves [{dl_ll, points [[area, factor]]}]) or Stories (User Defined by Stories Supported: stories [[number, reduction]]); default_min (Use default minimum factors), single and multiple (minimum factors), all_forces (Application to Columns: All Forces/Components rather than Axial Load Only). rate per m², areas in m².", // ETABS itself
            """{"type":"object","required":["method"],"properties":{"method":{"type":"string","enum":["None","Code","User","Curves","Stories"]},"code":{"type":"string"},"rate":{"type":"number"},"min_area":{"type":"number"},"default_min":{"type":"boolean"},"single":{"type":"number"},"multiple":{"type":"number"},"all_forces":{"type":"boolean"},"curves":{"type":"array","items":{"type":"object"}},"stories":{"type":"array","items":{"type":"array","items":{"type":"number"}}}}}""",
            Ask.Changes, SetLlrf, a => $"Live load reduction: {a.Text("method")}"),
        new("set_connection_preferences",
            "Design > Steel Connection Design > View/Revise Preferences, as ETABS 23.2's form: code (AISC 360-10, -16, -22), items [{group, item, value}] where group is the tab and type (\"Beam-Beam|Partial Depth\", \"Beam-Column|Beam Column Moment Major Axis\", \"Column-Footing\"), item the row's name as the form shows it and value its value (lengths in inches); value \"\" puts the item back to the program's default. Group \"Combinations\": Select Design Combinations, item a load combination, value Yes. schedule: the Plate Depth Schedule {mode, horizontal, partial} (inches). Only what is given changes.", // ETABS itself
            """{"type":"object","properties":{"code":{"type":"string"},"items":{"type":"array","items":{"type":"object"}},"schedule":{"type":"object"}}}""",
            Ask.Changes, SetConnection, a => $"Steel connection preferences {a.Text("code")}"),
    ];

    private static JsonObject Obj(IReadOnlyDictionary<string, string> d) => new(d.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

    private static List<Dictionary<string, string>> Kept(StructuralModel m, string key) => m.Kept?.GetValueOrDefault(key) ?? [];

    private static JsonNode Read(StructuralModel m)
    {
        var rllf = m.Controls.FirstOrDefault(IsRllf);
        return new JsonObject
        {
            ["rllf_line"] = rllf is null ? null : new JsonArray(E2kText.Tokenize(rllf).Select(t => (JsonNode)t).ToArray()),
            ["live_load_reduction"] = new JsonArray(Kept(m, LlrfKept).Select(r => (JsonNode)Obj(r)).ToArray()),
            ["connection"] = new JsonArray(Kept(m, ConnectionKept).Select(r => (JsonNode)Obj(r)).ToArray()),
            ["displacement_targets"] = new JsonArray((m.DefinitionTables?.GetValueOrDefault(DefineTables.DisplacementTargets) ?? []).Select(r => (JsonNode)Obj(r)).ToArray()),
            ["period_targets"] = new JsonArray((m.DefinitionTables?.GetValueOrDefault(DefineTables.PeriodTargets) ?? []).Select(r => (JsonNode)Obj(r)).ToArray()),
        };
    }

    private static bool IsRllf(string line) => E2kText.Tokenize(line) is { Count: > 0 } t && t[0].Equals("RLLF", StringComparison.OrdinalIgnoreCase);

    private static string N(double v) => E2kText.Number(v);

    private static JsonNode SetLlrf(JsonElement a, ICsiLink e)
    {
        var method = LlrfMethods.FirstOrDefault(x => x.Equals(a.Need("method"), StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolInputException($"method is one of {string.Join(", ", LlrfMethods)}.");
        var code = a.Text("code").Trim();
        if (method == "Code")
        {
            if (code.Length == 0) code = "ASCE7-10";
            code = LlrfCodes.FirstOrDefault(c => c.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolInputException($"code is one of {string.Join(", ", LlrfCodes)}.");
        }
        var defaultMin = a.Flag("default_min", method == "Code");
        double single = a.Number("single", 0.5), multiple = a.Number("multiple", 0.4);
        if (single is < 0 or > 1 || multiple is < 0 or > 1) throw new ToolInputException("The minimum factors are between 0 and 1.");
        double rate = a.Number("rate", 0.08), minArea = a.Number("min_area", 0);
        if (method == "User" && (rate < 0 || minArea < 0)) throw new ToolInputException("The rate and the minimum tributary area are not negative.");
        var rows = new List<Dictionary<string, string>>
        {
            new(StringComparer.Ordinal)
            {
                ["Row"] = "Form", ["Method"] = method, ["Code"] = code, ["Rate"] = N(rate), ["MinArea"] = N(minArea),
                ["DefaultMin"] = defaultMin ? "Yes" : "No", ["Single"] = N(single), ["Multiple"] = N(multiple),
                ["Columns"] = a.Flag("all_forces") ? "All Forces/Components" : "Axial Load Only",
            },
        };
        if (a.TryGetProperty("curves", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            var n = 0;
            foreach (var c in cs.EnumerateArray())
            {
                n++;
                var dl = c.Number("dl_ll", 1);
                if (!c.TryGetProperty("points", out var ps) || ps.ValueKind != JsonValueKind.Array) throw new ToolInputException($"Curve {n} has no points.");
                foreach (var p in ps.EnumerateArray())
                {
                    var xy = p.EnumerateArray().Select(x => x.GetDouble()).ToList();
                    if (xy.Count < 2) throw new ToolInputException($"A point of curve {n} is [area, factor].");
                    rows.Add(new(StringComparer.Ordinal) { ["Row"] = "Curve", ["Curve"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture), ["DLLL"] = N(dl), ["Area"] = N(xy[0]), ["Factor"] = N(xy[1]) });
                }
            }
        }
        if (a.TryGetProperty("stories", out var ss) && ss.ValueKind == JsonValueKind.Array)
            foreach (var s in ss.EnumerateArray())
            {
                var x = s.EnumerateArray().Select(v => v.GetDouble()).ToList();
                if (x.Count < 2) throw new ToolInputException("A row of stories supported is [number of stories, reduction].");
                rows.Add(new(StringComparer.Ordinal) { ["Row"] = "Stories", ["Number"] = N(x[0]), ["Reduction"] = N(x[1]) });
            }
        if (method == "Curves" && !rows.Exists(r => r["Row"] == "Curve")) throw new ToolInputException("User Defined Curves needs a curve: Define... and add its points.");
        if (method == "Stories" && !rows.Exists(r => r["Row"] == "Stories")) throw new ToolInputException("User Defined (By Stories Supported) needs its rows: Define... and add them.");
        return Offline(e).Edit<JsonNode>(m =>
        {
            m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
            m.Kept[LlrfKept] = rows;
            var wrote = false;
            if (method == "Code")
            {
                // ETABS's own words (every export pictured): RLLF  METHOD "ASCE7-10"  USEDEFAULTMIN "YES"
                var line = $"  RLLF  METHOD {E2kText.Quote(code)}  USEDEFAULTMIN {E2kText.Quote(defaultMin ? "YES" : "NO")}  ";
                var at = m.Controls.FindIndex(IsRllf);
                if (at >= 0) m.Controls[at] = line; else m.Controls.Add(line);
                wrote = true;
            }
            return new JsonObject { ["done"] = $"live load reduction: {method}{(code.Length > 0 && method == "Code" ? " " + code : "")}", ["e2k_line"] = wrote };
        });
    }

    /// <summary>The kept connection preferences as group|item → value (the code row has group "").</summary>
    public static Dictionary<string, string> ConnectionValues(StructuralModel m) =>
        Kept(m, ConnectionKept).GroupBy(r => (r.GetValueOrDefault("Group") ?? "") + "|" + (r.GetValueOrDefault("Item") ?? ""))
            .ToDictionary(g => g.Key, g => g.Last().GetValueOrDefault("Value") ?? "", StringComparer.Ordinal);

    private static JsonNode SetConnection(JsonElement a, ICsiLink e)
    {
        var sets = new List<(string Group, string Item, string Value)>();
        if (a.Text("code") is { Length: > 0 } code)
            sets.Add(("", "Design Code", ConnectionCodes.FirstOrDefault(c => c.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new ToolInputException($"code is one of {string.Join(", ", ConnectionCodes)}.")));
        if (a.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                var group = it.Need("group").Trim();
                if (!(group.StartsWith("Beam-Beam|", StringComparison.Ordinal) || group.StartsWith("Beam-Column|", StringComparison.Ordinal) || group is "Column-Footing" or "Combinations"))
                    throw new ToolInputException($"group is the tab and type, as \"Beam-Beam|Partial Depth\", \"Beam-Column|Beam Column Shear Major Axis\" or \"Column-Footing\" (or \"Combinations\": item a load combination, value Yes): {group}");
                var value = it.TryGetProperty("value", out var v) ? v.ValueKind == JsonValueKind.Number ? v.ToString() : v.GetString() ?? "" : "";
                sets.Add((group, it.Need("item").Trim(), value.Trim()));
            }
        if (a.TryGetProperty("schedule", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            var mode = s.Text("mode");
            if (mode.Length > 0)
            {
                string[] modes = ["Auto", "Horizontal", "Partial Depth with Top", "Full Depth", "Partial Depth"];
                sets.Add(("Schedule", "Default Mode", modes.FirstOrDefault(x => x.Equals(mode, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolInputException($"mode is one of {string.Join(", ", modes)}.")));
            }
            double? h = s.TryGetProperty("horizontal", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetDouble() : null;
            double? p = s.TryGetProperty("partial", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetDouble() : null;
            if (h is < 0 || p is < 0) throw new ToolInputException("The plate schedule's depths are not negative.");
            if (h is { } hh && p is { } pp && pp < hh) throw new ToolInputException("The partial plate depth is the horizontal plate's or more.");
            if (h is { } x) sets.Add(("Schedule", "Horizontal", N(x)));
            if (p is { } y) sets.Add(("Schedule", "Partial", N(y)));
        }
        if (sets.Count == 0) throw new ToolInputException("Give code, items or schedule.");
        return Offline(e).Edit<JsonNode>(m =>
        {
            m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
            var rows = m.Kept.TryGetValue(ConnectionKept, out var l) ? l : m.Kept[ConnectionKept] = [];
            foreach (var (g, i, v) in sets)
            {
                rows.RemoveAll(r => r.GetValueOrDefault("Group") == g && r.GetValueOrDefault("Item") == i);
                if (v.Length > 0) rows.Add(new(StringComparer.Ordinal) { ["Group"] = g, ["Item"] = i, ["Value"] = v });
            }
            if (rows.Count == 0) m.Kept.Remove(ConnectionKept);
            return new JsonObject { ["done"] = $"{sets.Count} connection preference{(sets.Count == 1 ? "" : "s")} set", ["kept"] = rows.Count };
        });
    }
}
