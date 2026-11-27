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
            "Define > Frame Sections > Modify/Show Rebar: a concrete section's reinforcement. TYPE Column or Beam. Column: LONGBARMATERIAL CONFINEBARMATERIAL PATTERN (R-n3-n2 or C-n) TRANSREINF (TIES or SPIRAL) DESIGNCHECK (DESIGN or CHECK) COVER LONGBARAREA CONFINEBARAREA CONFINEBARSPACING NUMCONFINEBARS3 NUMCONFINEBARS2. Beam: LONGBARMATERIAL CONFINEBARMATERIAL COVERTOP COVERBOTTOM ATI ABI ATJ ABJ. Only what is given changes. sizes: a column's bar sizes by name (BarSizeLong, BarSizeCorn, BarSizeConf; definition_data gives them as SIZE:...).",
            """{"type":"object","required":["section","values"],"properties":{"section":{"type":"string"},"values":{"type":"object","additionalProperties":{"type":["string","number"]}},"sizes":{"type":"object","additionalProperties":{"type":"string"}}}}""",
            Ask.Changes, FrameRebar, a => $"Rebar of {a.Text("section")}"),
        new("define_table_rows",
            "Define items kept as rows of the program's own definition tables (definition_data lists every table and its fields): generalized displacements, shell uniform load sets, auto construction sequence, walking vibrations, panel zone properties, frame release definitions, slab and wall layers, wall auto select lists and every hinge definition. Sets the rows of one definition by name; rows [] deletes it.",
            """{"type":"object","required":["table","name","rows"],"properties":{"table":{"type":"string"},"name":{"type":"string"},"rows":{"type":"array","items":{"type":"object"}}}}""",
            Ask.Changes, TableRows, a => $"{a.Text("table")}: {a.Text("name")}"),
        new("program_forms",
            "Analyze, Design and Options menu forms of the model's program, with the program's own items: each design (Steel Frame, Concrete Frame, Composite Beam, Shear Wall, Concrete Slab, ...) with its code and the codes it has, the Analyze options (active degrees of freedom, SAPFire solver, mesh settings, nonlinear hinges, cracking), and the Options menu forms (Preferences: dimensions/tolerances and graphics; Colors: display and output; Miscellaneous Options). With 'table' it gives that form's items (field, the name the program shows, value; blank = the program's default). With 'code' it gives the preferences form of that design's code.",
            """{"type":"object","properties":{"table":{"type":"string"},"design":{"type":"string"},"code":{"type":"string"}}}""",
            Ask.Never, Forms),
        new("set_program_form",
            "Sets a form of the Analyze, Design or Options menu (program_forms lists them): 'design' with 'code' sets that design's code (Design > ... > View/Revise Preferences, Design Code); 'table' with 'values' (item by field or by the name the program shows; blank = the program's default) sets that form's items, e.g. a design code's preferences or Analyze > Set Active Degrees of Freedom.",
            """{"type":"object","properties":{"design":{"type":"string"},"code":{"type":"string"},"table":{"type":"string"},"values":{"type":"object","additionalProperties":{"type":["string","number","boolean"]}}}}""",
            Ask.Changes, SetForm, a => a.Text("table") is { Length: > 0 } t ? t : $"{a.Text("design")} design code {a.Text("code")}"),
        new("definition_data",
            "Reads the Define items above: rebar sizes, each concrete section's rebar, the definition tables with their fields, and the rows the model has.",
            """{"type":"object","properties":{}}""",
            Ask.Never, Read),
    ];

    private static JsonObject FormJson(StructuralModel m, string table) => new()
    {
        ["table"] = table,
        ["items"] = new JsonArray(ProgramForms.Values(m, table).Select(v => (JsonNode)new JsonObject { ["field"] = v.Field.Key, ["name"] = v.Field.Label, ["value"] = v.Value }).ToArray()),
    };

    private static JsonNode Forms(JsonElement a, ICsiLink e)
    {
        var m = Offline(e).Model;
        if (a.Text("table") is { Length: > 0 } table) return Core(() => FormJson(m, table));
        if (a.Text("design") is { Length: > 0 } kind)
        {
            var d = Core(() => ProgramForms.Design(m.Target, kind));
            var code = a.Text("code") is { Length: > 0 } c ? c : ProgramForms.Code(m, kind);
            if (code.Length == 0) code = ProgramForms.Codes(m.Target, kind).FirstOrDefault() ?? "";
            return Core(() => FormJson(m, d.TablePrefix + code));
        }
        return new JsonObject
        {
            ["designs"] = new JsonArray(ProgramForms.Designs(m.Target).Select(d => (JsonNode)new JsonObject
            {
                ["design"] = d.Kind, ["name"] = d.Name, ["code"] = ProgramForms.Code(m, d.Kind), ["table_prefix"] = d.TablePrefix,
                ["codes"] = new JsonArray(ProgramForms.Codes(m.Target, d.Kind).Select(c => (JsonNode)c).ToArray()),
                ["overwrites"] = new JsonArray(ProgramForms.OverwriteTables(m.Target, d.Kind, ProgramForms.Code(m, d.Kind) is { Length: > 0 } oc ? oc : ProgramForms.Codes(m.Target, d.Kind).FirstOrDefault() ?? "").Select(t => (JsonNode)t).ToArray()),
            }).ToArray()),
            ["analysis"] = new JsonArray(ProgramForms.AnalysisForms(m.Target).Select(f => (JsonNode)new JsonObject { ["item"] = f.Item, ["table"] = f.Table, ["set"] = ProgramForms.Values(m, f.Table).Count(v => v.Value.Length > 0) }).ToArray()),
            ["options"] = new JsonArray(ProgramForms.OptionForms(m.Target).Select(f => (JsonNode)new JsonObject { ["group"] = f.Group, ["item"] = f.Item, ["table"] = f.Table, ["set"] = ProgramForms.Values(m, f.Table).Count(v => v.Value.Length > 0) }).ToArray()),
        };
    }

    private static JsonNode SetForm(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var table = a.Text("table") ?? "";
        var kind = a.Text("design") ?? "";
        var code = a.Text("code") ?? "";
        if (table.Length == 0 && (kind.Length == 0 || code.Length == 0)) throw new ToolInputException("Give 'table' with 'values', or 'design' with 'code'.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                values[p.Name] = p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString() ?? "", JsonValueKind.True => "Yes", JsonValueKind.False => "No", _ => p.Value.ToString() };
        if (table.Length > 0 && values.Count == 0) throw new ToolInputException("Give the form's 'values'.");
        // Checked whole on a copy first: a refusal leaves the model as it was.
        Core(() =>
        {
            var copy = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            if (kind.Length > 0 && code.Length > 0) ProgramForms.SetCode(copy, kind, code);
            if (table.Length > 0) ProgramForms.Set(copy, table, values);
            return 0;
        });
        return Core(() => link.Edit(m =>
        {
            var o = new JsonObject();
            if (kind.Length > 0 && code.Length > 0) { ProgramForms.SetCode(m, kind, code); o["design"] = kind; o["code"] = ProgramForms.Code(m, kind); }
            if (table.Length > 0) { ProgramForms.Set(m, table, values); o["form"] = FormJson(m, table); }
            return (JsonNode)o;
        }));
    }

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
        var sizes = a.TryGetProperty("sizes", out var z) && z.ValueKind == JsonValueKind.Object
            ? z.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : throw new ToolInputException($"sizes.{p.Name} is a bar size's name.")) : null;
        return Core(() => link.Edit(m =>
        {
            DefineTables.SetRebar(m, section, values);
            if (sizes is not null) DefineTables.SetBarSizes(m, section, sizes);
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
            var full = DefineTables.FullName(table)!;
            if (full == DefineTables.SapFrameGeneral || full == DefineTables.SapAreaSections) DefineTables.SyncSapSection(m, full, name);
            else if (full == DefineTables.SapLinkGeneral) DefineTables.SyncSapLink(m, name);
            return (JsonNode)new JsonObject { ["table"] = full, ["name"] = name, ["rows"] = m.DefinitionTables?.GetValueOrDefault(full)?.Count(x => x.GetValueOrDefault(DefineTables.KeyField(full)) == name) ?? 0 };
        }));
    }

    private static JsonObject Obj(IReadOnlyDictionary<string, string> d) => new(d.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

    private static JsonNode Read(JsonElement a, ICsiLink e)
    {
        var m = Offline(e).Model;
        var sections = new JsonObject();
        foreach (var s in m.FrameSections)
            if (DefineTables.Rebar(m, s.Name) is { } r) sections[s.Name] = Obj(new Dictionary<string, string>(r).Concat(DefineTables.BarSizes(s).Select(kv => KeyValuePair.Create("SIZE:" + kv.Key, kv.Value))).ToDictionary());
        var tables = new JsonObject();
        // The shear wall overwrites only for the code the shear wall design has now (each code has its own table).
        var walls = ProgramForms.Designs(m.Target).Any(d => d.Kind == "shear_wall") ? ProgramForms.OverwriteTables(m.Target, "shear_wall", ProgramForms.Code(m, "shear_wall") is { Length: > 0 } wc ? wc : ProgramForms.Codes(m.Target, "shear_wall").FirstOrDefault() ?? "") : [];
        foreach (var (name, fields) in DefineTables.Tables.Where(t => ProgramOptions.HasDefinitionTable(m.Target, t.Key) && (!ProgramForms.IsWallOverwrite(t.Key) || walls.Contains(t.Key))))
            tables[name] = new JsonObject
            {
                ["fields"] = new JsonArray(fields.Select(f => (JsonNode)new JsonArray(f.Key, f.Label)).ToArray()),
                ["rows"] = new JsonArray((m.DefinitionTables?.GetValueOrDefault(name) ?? []).Select(x => (JsonNode)Obj(x)).ToArray()),
            };
        // SAP2000's own load tables (Define > Load Patterns, Load Cases, Load Combinations) the model has rows of.
        if (m.Target == ProgramOptions.Sap2000)
            foreach (var (name, rows) in (m.DefinitionTables ?? []).Where(t => SapLoadTables.Has(t.Key)))
                tables[name] = new JsonObject
                {
                    ["fields"] = new JsonArray(SapLoadTables.Fields[name].Select(f => (JsonNode)new JsonArray(f, f)).ToArray()),
                    ["rows"] = new JsonArray(rows.Select(x => (JsonNode)Obj(x)).ToArray()),
                };
        return new JsonObject
        {
            ["program"] = m.Target,
            ["rebar_sizes"] = new JsonArray(DefineTables.RebarSizes(m).Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["area"] = x.Area, ["diameter"] = x.Diameter }).ToArray()),
            ["section_rebar"] = sections,
            ["tables"] = tables,
            // SAP2000's load tables' fields (its load forms lay them out), the key first.
            ["sap_load_fields"] = m.Target == ProgramOptions.Sap2000 ? new JsonObject(SapLoadTables.Fields.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)new JsonArray(f.Value.Select(x => (JsonNode)x).ToArray())))) : null,
        };
    }
}
