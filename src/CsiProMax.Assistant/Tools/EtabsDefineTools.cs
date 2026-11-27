using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// ETABS 23.2's Define items that ETABS's own .e2k holds as lines of their own section (Auto Construction Sequence Case,
/// Walking Vibrations, Floor Vibrations > Excitation Sets, Table Named Sets, Named Assignment Sets), and Load Combinations >
/// Convert Combos to Nonlinear Cases. The Engine's forms send each item as the lines ETABS's own .e2k export writes
/// (pictured in field/reference-forms/etabs/e2k/cpm-forms-scratch-define.e2k), so the file the Engine writes is ETABS's own.
/// Modeler only.
/// </summary>
public static class EtabsDefineTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Engine's own Define form (no CSI program behind it). In a live program use its own Define menu.");

    /// <summary>The sections the forms keep, with the sections ETABS writes them after (the first one the model has).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Sections = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["NAMED FRAME MODIFIERS"] = ["FRAME HINGE PROPERTIES", "AREA SPRING PROPERTIES", "LINE SPRING PROPERTIES", "POINT SPRING PROPERTIES", "LINK PROPERTIES", "WALL PROPERTIES", "SLAB PROPERTIES"],
        ["NAMED SHELL MODIFIERS"] = ["NAMED FRAME MODIFIERS", "FRAME HINGE PROPERTIES", "AREA SPRING PROPERTIES", "LINE SPRING PROPERTIES", "POINT SPRING PROPERTIES", "LINK PROPERTIES", "WALL PROPERTIES", "SLAB PROPERTIES"],
        ["NAMED FRAME RELEASES"] = ["NAMED SHELL MODIFIERS", "NAMED FRAME MODIFIERS", "FRAME HINGE PROPERTIES", "AREA SPRING PROPERTIES", "LINE SPRING PROPERTIES", "POINT SPRING PROPERTIES", "LINK PROPERTIES", "WALL PROPERTIES", "SLAB PROPERTIES"],
        ["AUTO CONSTRUCTION SEQUENCE CASE"] = ["LOAD CASES"],
        ["WALKING VIBRATIONS"] = ["LOAD COMBINATIONS", "AUTO CONSTRUCTION SEQUENCE CASE", "LOAD CASES"],
        ["FLOOR VIBRATION EXCITATION SET"] = ["WALKING VIBRATIONS", "LOAD COMBINATIONS", "AUTO CONSTRUCTION SEQUENCE CASE", "LOAD CASES"],
        ["DATABASE TABLE NAMED SETS"] = ["TABLE SETS", "DIMENSION LINES", "SECTION CUTS"],
    };

    /// <summary>The definition tables the Engine's general table form kept the same items in before (rows of an item
    /// there are dropped when the form writes the item as .e2k lines, so it is not sent twice).</summary>
    private static readonly IReadOnlyDictionary<string, string> OldTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AUTO CONSTRUCTION SEQUENCE CASE"] = "Auto Construction Sequence Definition",
        ["WALKING VIBRATIONS"] = "Walking Vibration Definitions",
        ["FLOOR VIBRATION EXCITATION SET"] = "Floor Vibration Excitation Sets",
    };

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("e2k_items",
            "The lines of one of ETABS's .e2k sections the Engine's Define forms keep (AUTO CONSTRUCTION SEQUENCE CASE, WALKING VIBRATIONS, FLOOR VIBRATION EXCITATION SET, DATABASE TABLE NAMED SETS, NAMED FRAME MODIFIERS, NAMED SHELL MODIFIERS, NAMED FRAME RELEASES), each line as its words; and the rows the Engine's table form kept for it before.", // ETABS itself
            """{"type":"object","required":["section"],"properties":{"section":{"type":"string"}}}""",
            Ask.Never, (a, e) => Items(Offline(e).Model, a.Need("section"))),
        new("define_e2k_item",
            "Sets one item of one of those .e2k sections as ETABS's own .e2k writes it: the item's lines (name: the item, the second word of its lines; was: its name now when renamed; no name: the whole section). lines [] deletes it.", // ETABS itself
            """{"type":"object","required":["section","lines"],"properties":{"section":{"type":"string"},"name":{"type":"string"},"was":{"type":"string"},"lines":{"type":"array","items":{"type":"string"}}}}""",
            Ask.Changes, DefineItem, a => $"{a.Text("section")}: {a.Text("name")}"),
        new("convert_combo_to_nonlinear",
            "Define > Load Combinations > Convert Combos to Nonlinear Cases, as ETABS 23.2 does it: the combination becomes a Nonlinear Static load case '<combo>-NL' (its load patterns with their factors, P-Delta, full load, final state, iterative event-to-event), and the combination then holds that case alone.", // ETABS itself
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""",
            Ask.Changes, ConvertCombo, a => $"Convert {a.Text("name")} to a nonlinear case"),
    ];

    private static JsonNode Items(StructuralModel m, string section)
    {
        var heading = Heading(section);
        var raw = m.Passthrough.Find(s => s.Heading.Equals(heading, StringComparison.OrdinalIgnoreCase));
        var o = new JsonObject
        {
            ["section"] = heading,
            ["lines"] = new JsonArray((raw?.Lines ?? []).Where(l => l.Trim().Length > 0).Select(l => (JsonNode)new JsonArray(E2kText.Tokenize(l).Select(t => (JsonNode)t).ToArray())).ToArray()),
        };
        if (OldTable.TryGetValue(heading, out var table) && m.DefinitionTables?.GetValueOrDefault(table) is { Count: > 0 } rows)
            o["table_rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject(r.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))).ToArray());
        return o;
    }

    private static string Heading(string section)
    {
        var h = section.Trim().TrimStart('$').Trim().ToUpperInvariant();
        if (!Sections.ContainsKey(h)) throw new ToolInputException($"The Define forms keep these sections: {string.Join(", ", Sections.Keys)}.");
        return h;
    }

    private static JsonNode DefineItem(JsonElement a, ICsiLink e)
    {
        var heading = Heading(a.Need("section"));
        var name = a.Text("name").Trim();
        var was = a.Text("was").Trim();
        var lines = a.GetProperty("lines").EnumerateArray().Select(x => x.GetString() ?? "").Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        foreach (var l in lines)
        {
            if (l.TrimStart().StartsWith('$')) throw new ToolInputException("Give the item's lines only, without a '$' heading.");
            if (name.Length > 0 && !Mine(l, name)) throw new ToolInputException($"Every line of '{name}' names it as its second word: {l.Trim()}");
        }
        lines = lines.Select(l => "  " + l.Trim()).ToList();
        return Offline(e).Edit<JsonNode>(m =>
        {
            var raw = m.Passthrough.Find(s => s.Heading.Equals(heading, StringComparison.OrdinalIgnoreCase));
            var old = was.Length > 0 ? was : name;
            if (name.Length > 0 && was.Length > 0 && was != name && raw is not null && raw.Lines.Any(l => Mine(l, name)))
                throw new ToolInputException($"'{name}' is already defined.");
            if (raw is null)
            {
                if (lines.Count == 0) return new JsonObject { ["done"] = "nothing to delete" };
                var written = E2kWriter.Write(m).Split('\n').Where(l => l.StartsWith("$ ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var after = Sections[heading].FirstOrDefault(written.Contains) ?? "";
                raw = new RawSection { Heading = heading, After = after };
                // A kept section that followed the anchor now follows this one, so both stay in ETABS's order.
                foreach (var s in m.Passthrough.Where(s => after.Length > 0 && s.After == after)) s.After = heading;
                m.Passthrough.Add(raw);
            }
            int at;
            if (name.Length == 0) { at = 0; raw.Lines.Clear(); }
            else
            {
                at = raw.Lines.FindIndex(l => Mine(l, old));
                raw.Lines.RemoveAll(l => Mine(l, old));
                if (at < 0) at = raw.Lines.Count;
            }
            raw.Lines.InsertRange(at, lines);
            if (raw.Lines.All(l => l.Trim().Length == 0))
            {
                foreach (var s in m.Passthrough.Where(s => s.After == raw.Heading)) s.After = raw.After;
                m.Passthrough.Remove(raw);
            }
            if (OldTable.TryGetValue(heading, out var table) && m.DefinitionTables?.GetValueOrDefault(table) is { } rows)
            {
                rows.RemoveAll(r => name.Length == 0 || r.GetValueOrDefault("Name") == old || r.GetValueOrDefault("Name") == name);
                if (rows.Count == 0) m.DefinitionTables!.Remove(table);
            }
            return new JsonObject { ["done"] = lines.Count == 0 ? $"deleted {old}" : $"{heading}: {(name.Length > 0 ? name : "set")}", ["lines"] = lines.Count };
        });
    }

    /// <summary>A line of the item: its second word is the item's name (DBTABLESET "Set1", NAMEDFRELEASE "FRel1", ...).</summary>
    private static bool Mine(string line, string name) => E2kText.Tokenize(line) is { Count: >= 2 } t && E2kText.Unquote(t[1]) == name;

    private static JsonNode ConvertCombo(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        return Offline(e).Edit<JsonNode>(m =>
        {
            var combo = m.Combos.Find(c => c.Name == name) ?? throw new ToolInputException($"There is no combination named '{name}'.");
            var type = "Linear Add";
            if (combo.Lines.FirstOrDefault(l => l.Count >= 2 && l[0] == "TYPE") is { } tl) type = E2kText.Unquote(tl[1]);
            if (!type.Equals("Linear Add", StringComparison.OrdinalIgnoreCase))
                throw new ToolInputException($"'{name}' is a{(type.StartsWith('E') || type.StartsWith('A') ? "n" : "")} {type} combination: ETABS converts Linear Add combinations only."); // ETABS itself
            // The combination's load patterns with their factors: each linear static case by its patterns, each
            // combination inside it by its own cases.
            var loads = new List<(string Pattern, double Sf)>();
            void Add(string item, double sf, int depth)
            {
                if (depth > 20) throw new ToolInputException($"'{name}' includes itself.");
                if (m.Combos.Find(c => c.Name == item) is { } inner)
                {
                    foreach (var l in inner.Lines.Where(l => l.Count >= 2 && l[0] is "LOADCASE" or "LOADCOMBO"))
                        Add(E2kText.Unquote(l[1]), sf * (E2kText.TryNumber(E2kText.Value(l, "SF"), out var f) ? f : 1), depth + 1);
                    return;
                }
                var lc = m.LoadCases.Find(c => c.Name == item) ?? throw new ToolInputException($"'{name}' names '{item}', which is not a load case.");
                var lt = lc.Lines.FirstOrDefault(l => l.Count >= 2 && l[0] == "TYPE") is { } x ? E2kText.Unquote(x[1]) : "";
                if (!lt.Equals("Linear Static", StringComparison.OrdinalIgnoreCase))
                    throw new ToolInputException($"'{item}' in '{name}' is a {(lt.Length > 0 ? lt : "non static")} case: only combinations of linear static cases become a nonlinear static case.");
                foreach (var l in lc.Lines.Where(l => l.Count >= 2 && l[0] == "LOADPAT"))
                {
                    var p = E2kText.Unquote(l[1]);
                    var f = sf * (E2kText.TryNumber(E2kText.Value(l, "SF"), out var g) ? g : 1);
                    var i = loads.FindIndex(q => q.Pattern == p);
                    if (i >= 0) loads[i] = (p, loads[i].Sf + f); else loads.Add((p, f));
                }
            }
            foreach (var l in combo.Lines.Where(l => l.Count >= 2 && l[0] is "LOADCASE" or "LOADCOMBO"))
                Add(E2kText.Unquote(l[1]), E2kText.TryNumber(E2kText.Value(l, "SF"), out var f) ? f : 1, 0);
            var nl = name + "-NL";
            if (m.LoadCases.Exists(c => c.Name == nl) || m.Combos.Exists(c => c.Name == nl)) throw new ToolInputException($"There is already a load case or combination named '{nl}'.");
            var lines = new List<List<string>> { new() { "TYPE", E2kText.Quote("Nonlinear Static"), "INITCOND", E2kText.Quote("NONE"), "MASSSOURCE", E2kText.Quote("Previous") } };
            foreach (var (p, sf) in loads) lines.Add(["LOADPAT", E2kText.Quote(p), "SF", E2kText.Number(sf)]);
            lines.Add(["NLGEOMTYPE", E2kText.Quote("PDelta")]);
            // ETABS monitors U1 at the first joint of the top story (DConS2-NL: JOINT "1" "Story4").
            var control = new List<string> { "LOADCONTROL", E2kText.Quote("Full"), "DISPLTYPE", E2kText.Quote("Monitored"), "MONITOREDDISPL", E2kText.Quote("Joint"), "DISPLMAG", "0", "DOF", E2kText.Quote("U1") };
            if (m.Stories.Count > 0 && TopJoint(m) is { } j) control.AddRange(["JOINT", E2kText.Quote(j), E2kText.Quote(m.Stories[^1].Name)]);
            lines.Add(control);
            lines.Add(["RESULTSSAVED", E2kText.Quote("Final")]);
            lines.Add(["SOLUTIONSCHEME", E2kText.Quote("Iterative Events"), "MAXTOTALSTEPS", "200", "MAXNULLSTEPS", "50", "MAXEVENTSPERSTEP", "24"]);
            m.LoadCases.Add(new LoadCase { Name = nl, Lines = lines });
            combo.Lines = [combo.Lines.FirstOrDefault(l => l.Count > 0 && l[0] == "TYPE") ?? ["TYPE", E2kText.Quote("Linear Add")], ["LOADCASE", E2kText.Quote(nl), "SF", "1"],
                .. combo.Lines.Where(l => l.Count > 0 && l[0] is not ("TYPE" or "LOADCASE" or "LOADCOMBO"))];
            return new JsonObject { ["done"] = $"{name} is now the nonlinear static case {nl}", ["case"] = nl };
        });
    }

    /// <summary>The first joint (by its label, as ETABS numbers them) with a line or area at the top story.</summary>
    private static string? TopJoint(StructuralModel m)
    {
        var top = m.Stories[^1].Name;
        var used = m.LineAssigns.Where(x => x.Story == top).Select(x => m.Lines.Find(l => l.Label == x.Label)).Where(l => l is not null).SelectMany(l => new[] { l!.PointI, l.PointJ })
            .Concat(m.AreaAssigns.Where(x => x.Story == top).Select(x => m.Areas.Find(r => r.Label == x.Label)).Where(r => r is not null).SelectMany(r => r!.PointNames)).Distinct().ToList();
        return used.OrderBy(p => int.TryParse(p, out var n) ? n : int.MaxValue).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    }
}
