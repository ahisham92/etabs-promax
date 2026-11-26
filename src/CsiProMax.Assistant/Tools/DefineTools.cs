using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's Define items beyond materials, sections, patterns, cases and combinations (Ahmed 2026-11-26: every
/// button): Reinforcing Bar Sizes, Frame Section > Modify/Show Rebar, and the definitions kept as rows of the program's
/// own tables (generalized displacements, shell uniform load sets, construction sequence, walking vibrations, panel
/// zones, frame releases, slab and wall layers, wall auto select lists, every hinge definition). Modeler only.
/// </summary>
public static class DefineTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Modeler's own Define item (no CSI program behind it). In a live program use its own Define menu.");

    private static T Core<T>(Func<T> work)
    {
        try { return work(); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        catch (EtabsException ex) { throw new ToolInputException(ex.Message); }
    }

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("define_rebar_size",
            "Define > Reinforcing Bar Sizes: adds or changes a bar size (name, area, diameter, in the model's units), or deletes it (delete true).",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"area":{"type":"number"},"diameter":{"type":"number"},"delete":{"type":"boolean"}}}""",
            Ask.Changes, RebarSize, a => $"Rebar size {a.Text("name")}"),
        new("define_frame_rebar",
            "Define > Frame Sections > Modify/Show Rebar: a concrete section's reinforcement. TYPE Column or Beam. Column: LONGBARMATERIAL CONFINEBARMATERIAL PATTERN (R-n3-n2 or C-n) TRANSREINF (TIES or SPIRAL) DESIGNCHECK (DESIGN or CHECK) COVER LONGBARAREA CONFINEBARAREA CONFINEBARSPACING NUMCONFINEBARS3 NUMCONFINEBARS2. Beam: LONGBARMATERIAL CONFINEBARMATERIAL COVERTOP COVERBOTTOM ATI ABI ATJ ABJ. Only what is given changes.",
            """{"type":"object","required":["section","values"],"properties":{"section":{"type":"string"},"values":{"type":"object","additionalProperties":{"type":["string","number"]}}}}""",
            Ask.Changes, FrameRebar, a => $"Rebar of {a.Text("section")}"),
        new("define_table_rows",
            "Define items kept as rows of the program's own definition tables (definition_data lists every table and its fields): generalized displacements, shell uniform load sets, auto construction sequence, walking vibrations, panel zone properties, frame release definitions, slab and wall layers, wall auto select lists and every hinge definition. Sets the rows of one definition by name; rows [] deletes it.",
            """{"type":"object","required":["table","name","rows"],"properties":{"table":{"type":"string"},"name":{"type":"string"},"rows":{"type":"array","items":{"type":"object"}}}}""",
            Ask.Changes, TableRows, a => $"{a.Text("table")}: {a.Text("name")}"),
        new("definition_data",
            "Reads the Define items above: rebar sizes, each concrete section's rebar, the definition tables with their fields, and the rows the model has.",
            """{"type":"object","properties":{}}""",
            Ask.Never, Read),
    ];

    private static JsonNode RebarSize(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var name = a.Need("name").Trim();
        var delete = a.TryGetProperty("delete", out var d) && d.ValueKind == JsonValueKind.True;
        if (delete && !DefineTables.RebarSizes(link.Model).Exists(x => x.Name == name)) throw new ToolInputException($"There is no bar size '{name}'.");
        double? area = delete ? null : a.Number("area"), dia = delete ? null : a.Number("diameter");
        return Core(() => link.Edit(m =>
        {
            DefineTables.SetRebarSize(m, name, area, dia);
            return (JsonNode)new JsonObject { [delete ? "deleted" : "bar"] = name };
        }));
    }

    private static JsonNode FrameRebar(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var section = a.Need("section");
        if (!a.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Object) throw new ToolInputException("values is {keyword: value}.");
        var values = v.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText(), StringComparer.Ordinal);
        if (values.Count == 0) throw new ToolInputException("Give what to change in values.");
        return Core(() => link.Edit(m =>
        {
            DefineTables.SetRebar(m, section, values);
            return (JsonNode)new JsonObject { ["section"] = section, ["rebar"] = Obj(DefineTables.Rebar(m, section)!) };
        }));
    }

    private static JsonNode TableRows(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var table = a.Need("table");
        var name = a.Need("name").Trim();
        if (!a.TryGetProperty("rows", out var r) || r.ValueKind != JsonValueKind.Array) throw new ToolInputException("rows is a list of {field: value}.");
        using var wrap = JsonDocument.Parse(new JsonObject { ["tables"] = new JsonObject { [table] = JsonNode.Parse(r.GetRawText()) } }.ToJsonString());
        var rows = PropertyTools.AssignRows(wrap.RootElement)![table];
        return Core(() => link.Edit(m =>
        {
            DefineTables.Set(m, table, name, rows);
            var full = DefineTables.Tables.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));
            return (JsonNode)new JsonObject { ["table"] = full, ["name"] = name, ["rows"] = m.DefinitionTables?.GetValueOrDefault(full)?.Count(x => x.GetValueOrDefault("Name") == name) ?? 0 };
        }));
    }

    private static JsonObject Obj(IReadOnlyDictionary<string, string> d) => new(d.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

    private static JsonNode Read(JsonElement a, ICsiLink e)
    {
        var m = Offline(e).Model;
        var sections = new JsonObject();
        foreach (var s in m.FrameSections)
            if (DefineTables.Rebar(m, s.Name) is { } r) sections[s.Name] = Obj(r);
        var tables = new JsonObject();
        foreach (var (name, fields) in DefineTables.Tables.Where(t => ProgramOptions.HasDefinitionTable(m.Target, t.Key)))
            tables[name] = new JsonObject
            {
                ["fields"] = new JsonArray(fields.Select(f => (JsonNode)new JsonArray(f.Key, f.Label)).ToArray()),
                ["rows"] = new JsonArray((m.DefinitionTables?.GetValueOrDefault(name) ?? []).Select(x => (JsonNode)Obj(x)).ToArray()),
            };
        return new JsonObject
        {
            ["program"] = m.Target,
            ["rebar_sizes"] = new JsonArray(DefineTables.RebarSizes(m).Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["area"] = x.Area, ["diameter"] = x.Diameter }).ToArray()),
            ["section_rebar"] = sections,
            ["tables"] = tables,
        };
    }
}
