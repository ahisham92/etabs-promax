using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// What the Modeler's Define forms do that the chat's tools don't: change or delete a definition after it was made
/// (stories, grids, materials, sections, load patterns, combinations), the way SAP2000's and ETABS's Define forms do
/// with Add, Modify and Delete. Each takes the whole definition, so a form sends what it shows. Modeler only.
/// </summary>
public static class HandTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("Defining by hand is in the Modeler (no CSI program behind it).");

    private static string N(double v) => E2kText.Number(v);

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("define_stories",
            "Sets the model's stories, bottom first (the base level is kept): each with a name and a height, and 'was' for a story that is renamed or kept. Objects on a story left out are deleted with it.",
            """{"type":"object","required":["stories"],"properties":{"base_name":{"type":"string"},"base_elevation":{"type":"number"},"stories":{"type":"array","items":{"type":"object","required":["name","height"],"properties":{"name":{"type":"string"},"height":{"type":"number"},"was":{"type":"string"}}}}}}""",
            Ask.Changes, DefineStories, a => "Change the story data"),
        new("define_grids",
            "Sets the model's grid lines: each with a label, direction (X: a line at x = coordinate; Y: a line at y = coordinate) and coordinate. Lines left out are deleted.",
            """{"type":"object","required":["grids"],"properties":{"grids":{"type":"array","items":{"type":"object","required":["label","direction","coordinate"],"properties":{"label":{"type":"string"},"direction":{"type":"string","enum":["X","Y"]},"coordinate":{"type":"number"}}}}}}""",
            Ask.Changes, DefineGrids, a => "Change the grid data"),
        new("define_material",
            "Adds a concrete material, or changes one ('was': its name now): fc, modulus of elasticity, unit weight and Poisson's ratio in the model's units.",
            """{"type":"object","required":["name","fc","elastic_modulus"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"fc":{"type":"number"},"elastic_modulus":{"type":"number"},"unit_weight":{"type":"number"},"poisson":{"type":"number"}}}""",
            Ask.Changes, DefineMaterial, a => $"Define the material {Text(a, "name")}"),
        new("define_frame_section",
            "Adds a rectangular concrete column or beam section, or changes one ('was': its name now; members keep it under the new name).",
            """{"type":"object","required":["name","kind","depth","width"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"kind":{"type":"string","enum":["column","beam"]},"depth":{"type":"number"},"width":{"type":"number"},"material":{"type":"string"}}}""",
            Ask.Changes, DefineFrameSection, a => $"Define the frame section {Text(a, "name")}"),
        new("define_shell_section",
            "Adds a slab or wall section, or changes one ('was': its name now; floors and walls keep it under the new name).",
            """{"type":"object","required":["name","kind","thickness"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"kind":{"type":"string","enum":["slab","wall"]},"thickness":{"type":"number"},"material":{"type":"string"}}}""",
            Ask.Changes, DefineShellSection, a => $"Define the section {Text(a, "name")}"),
        new("define_load_pattern",
            "Adds a load pattern (with its load case), or changes one ('was': its name now; its loads, case and combinations follow a new name).",
            """{"type":"object","required":["name","type"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"type":{"type":"string"},"self_weight":{"type":"number"}}}""",
            Ask.Changes, DefineLoadPattern, a => $"Define the load pattern {Text(a, "name")}"),
        new("define_combination",
            "Adds a load combination, or changes one ('was': its name now): its type (Linear Add, Envelope, Absolute Add, SRSS, Range Add) and each load case or combination with its scale factor.",
            """{"type":"object","required":["name","cases"],"properties":{"name":{"type":"string"},"was":{"type":"string"},"type":{"type":"string"},"cases":{"type":"array","items":{"type":"object","required":["case","factor"],"properties":{"case":{"type":"string"},"factor":{"type":"number"}}}}}}""",
            Ask.Changes, DefineCombination, a => $"Define the combination {Text(a, "name")}"),
        new("delete_definition",
            "Deletes a material, frame section, slab or wall section, load pattern (with its loads and case), load case, function or combination. One still in use is not deleted, and the answer says what uses it.",
            """{"type":"object","required":["kind","name"],"properties":{"kind":{"type":"string","enum":["material","frame_section","shell_section","load_pattern","load_case","function","combination"]},"name":{"type":"string"}}}""",
            Ask.Changes, DeleteDefinition, a => $"Delete {Text(a, "kind").Replace('_', ' ')} {Text(a, "name")}"),
        new("model_definitions",
            "Everything the Define forms show: stories, grids, materials, frame and slab/wall sections, load patterns, load cases and combinations with their cases and factors.",
            """{"type":"object","properties":{}}""",
            Ask.Never, (_, e) => Definitions(Offline(e).Model)),
    ];

    private static string Text(JsonElement a, string key) => a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string? Opt(JsonElement a, string key) => Text(a, key) is { Length: > 0 } s ? s.Trim() : null;
    private static double? Num(JsonElement a, string key) => a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static string Need(JsonElement a, string key) => Opt(a, key) ?? throw new ToolInputException($"'{key}' is needed.");
    private static double NeedNum(JsonElement a, string key) => Num(a, key) ?? throw new ToolInputException($"'{key}' is needed, as a number.");

    private static JsonObject Done(string what) => new() { ["done"] = what };

    // ---- what the forms show ----

    public static JsonObject Definitions(StructuralModel m)
    {
        var used = m.LineAssigns.Select(a => a.Section).Concat(m.AreaAssigns.Select(a => a.Section)).Where(s => s is not null).GroupBy(s => s!).ToDictionary(g => g.Key, g => g.Count());
        var concrete = m.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS")?.Lines ?? [];
        bool IsBeam(string name) => concrete.Any(l => l.Contains("\"" + name + "\"", StringComparison.Ordinal) && l.Contains("\"Beam\"", StringComparison.Ordinal));
        return new JsonObject
        {
            ["units"] = new JsonObject { ["force"] = m.Units.Force, ["length"] = m.Units.Length },
            ["base"] = new JsonObject { ["name"] = m.Stories[0].Name, ["elevation"] = m.Stories[0].Elevation },
            ["stories"] = new JsonArray(m.Stories.Skip(1).Select((s, i) => (JsonNode)new JsonObject { ["name"] = s.Name, ["height"] = s.Height, ["elevation"] = m.Elevation(i + 1) }).ToArray()),
            ["grids"] = new JsonArray(m.Grids.Select(g => (JsonNode)new JsonObject { ["label"] = g.Label, ["direction"] = g.Direction, ["coordinate"] = g.Coordinate }).ToArray()),
            ["materials"] = new JsonArray(m.Materials.Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.Name, ["type"] = x.Type, ["fc"] = x.Number("FC"), ["fy"] = x.Number("FY"), ["elastic_modulus"] = x.Number("E"), ["unit_weight"] = x.Number("WEIGHTPERVOLUME"), ["poisson"] = x.Number("U"),
            }).ToArray()),
            ["frame_sections"] = new JsonArray(m.FrameSections.Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.Name, ["shape"] = x.Shape, ["material"] = x.Material, ["depth"] = x.Number("D"), ["width"] = x.Number("B"),
                ["kind"] = IsBeam(x.Name) ? "beam" : "column", ["used"] = used.GetValueOrDefault(x.Name),
                ["editable"] = x.Shape.Equals("Concrete Rectangular", StringComparison.OrdinalIgnoreCase),
            }).ToArray()),
            ["shell_sections"] = new JsonArray(m.ShellSections.Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.Name, ["kind"] = x.IsWall ? "wall" : "slab", ["material"] = x.Material, ["thickness"] = x.Thickness, ["used"] = used.GetValueOrDefault(x.Name),
                ["type"] = x.Get("SLABTYPE") ?? x.Get("PROPTYPE") ?? "",
            }).ToArray()),
            ["load_patterns"] = new JsonArray(m.LoadPatterns.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["type"] = p.Type, ["self_weight"] = p.SelfWeight, ["auto_lateral"] = p.Extra.Count > 0 || p.Auto is not null, ["lateral"] = LateralTools.Lateral(p),
                ["loads"] = m.LineLoads.Count(l => l.Pattern == p.Name) + m.AreaLoads.Count(l => l.Pattern == p.Name) + m.PointLoads.Count(l => l.Pattern == p.Name),
            }).ToArray()),
            ["pattern_types"] = new JsonArray(ModelEdit.LoadPatternTypes.Select(t => (JsonNode)t).ToArray()),
            ["load_cases"] = new JsonArray(m.LoadCases.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["type"] = Value(c.Lines, "TYPE") ?? "" }).ToArray()),
            ["combinations"] = new JsonArray(m.Combos.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["type"] = Value(c.Lines, "TYPE") ?? "Linear Add",
                ["cases"] = new JsonArray(c.Lines.Where(l => l.Count >= 2 && l[0] is "LOADCASE" or "LOADCOMBO").Select(l => (JsonNode)new JsonObject
                {
                    ["case"] = E2kText.Unquote(l[1]), ["factor"] = E2kText.TryNumber(E2kText.Value(l, "SF"), out var f) ? f : 1,
                }).ToArray()),
            }).ToArray()),
        };
    }

    private static string? Value(List<List<string>> lines, string key)
    {
        foreach (var line in lines) if (E2kText.Value(line, key) is { } v) return v;
        return null;
    }

    // ---- stories and grids ----

    private static JsonNode DefineStories(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var rows = a.GetProperty("stories").EnumerateArray().Select(s => (Name: Need(s, "name"), Height: NeedNum(s, "height"), Was: Opt(s, "was"))).ToList();
        if (rows.Count == 0) throw new ToolInputException("A model needs one story above the base at least.");
        var names = rows.Select(r => r.Name).ToList();
        var baseName = Opt(a, "base_name") ?? link.Model.Stories[0].Name;
        if (names.Concat([baseName]).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new ToolInputException($"Two stories are named '{twice.Key}'.");
        if (rows.FirstOrDefault(r => r.Height <= 0) is { Name.Length: > 0 } flat) throw new ToolInputException($"Story '{flat.Name}' needs a height above 0.");
        return link.Edit(m =>
        {
            var oldBase = m.Stories[0];
            var kept = rows.Where(r => r.Was is not null).Select(r => r.Was!).ToHashSet(StringComparer.Ordinal);
            var gone = m.Stories.Skip(1).Select(s => s.Name).Where(n => !kept.Contains(n)).ToHashSet(StringComparer.Ordinal);
            var rename = rows.Where(r => r.Was is not null && r.Was != r.Name).ToDictionary(r => r.Was!, r => r.Name, StringComparer.Ordinal);
            if (baseName != oldBase.Name) rename[oldBase.Name] = baseName;
            // What stood on a story that is gone goes with it, as it does in ETABS.
            var removed = m.LineAssigns.RemoveAll(x => gone.Contains(x.Story)) + m.AreaAssigns.RemoveAll(x => gone.Contains(x.Story));
            m.PointAssigns.RemoveAll(x => gone.Contains(x.Story));
            m.LineLoads.RemoveAll(x => gone.Contains(x.Story)); m.AreaLoads.RemoveAll(x => gone.Contains(x.Story)); m.PointLoads.RemoveAll(x => gone.Contains(x.Story));
            m.Lines.RemoveAll(l => !m.LineAssigns.Exists(x => x.Label == l.Label));
            m.Areas.RemoveAll(l => !m.AreaAssigns.Exists(x => x.Label == l.Label));
            string R(string s) => rename.TryGetValue(s, out var n) ? n : s;
            foreach (var x in m.LineAssigns) x.Story = R(x.Story);
            foreach (var x in m.AreaAssigns) x.Story = R(x.Story);
            foreach (var x in m.PointAssigns) x.Story = R(x.Story);
            foreach (var x in m.LineLoads) x.Story = R(x.Story);
            foreach (var x in m.AreaLoads) x.Story = R(x.Story);
            foreach (var x in m.PointLoads) x.Story = R(x.Story);
            var old = m.Stories.ToDictionary(s => s.Name, StringComparer.Ordinal);
            var list = new List<Story> { new() { Name = baseName, Elevation = Num(a, "base_elevation") ?? oldBase.Elevation, Extra = oldBase.Extra } };
            foreach (var r in rows)
                list.Add(new Story { Name = r.Name, Height = r.Height, Extra = r.Was is not null && old.TryGetValue(r.Was, out var s) ? s.Extra : [] });
            m.Stories = list;
            return Done($"{rows.Count} stories above {baseName}" + (gone.Count > 0 ? $"; {gone.Count} deleted with {removed} objects on them" : ""));
        });
    }

    private static JsonNode DefineGrids(JsonElement a, ICsiLink e)
    {
        var rows = a.GetProperty("grids").EnumerateArray().Select(g => (Label: Need(g, "label"), Dir: Need(g, "direction").ToUpperInvariant(), At: NeedNum(g, "coordinate"))).ToList();
        if (rows.FirstOrDefault(r => r.Dir is not ("X" or "Y")) is { Label.Length: > 0 } bad) throw new ToolInputException($"Grid {bad.Label}: the direction is X or Y.");
        if (rows.GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new ToolInputException($"Two grid lines are labelled '{twice.Key}'.");
        return Offline(e).Edit(m =>
        {
            var old = m.Grids.ToDictionary(g => g.Label + "|" + g.Direction, StringComparer.Ordinal);
            m.Grids = [];
            foreach (var r in rows.OrderBy(r => r.Dir, StringComparer.Ordinal).ThenBy(r => r.At))
            {
                m.AddGrid(r.Label, r.Dir, r.At);
                if (old.TryGetValue(r.Label + "|" + r.Dir, out var was)) m.Grids[^1].Extra = was.Extra;
            }
            return Done($"{rows.Count(r => r.Dir == "X")} X and {rows.Count(r => r.Dir == "Y")} Y grid lines");
        });
    }

    // ---- materials and sections ----

    private static T Existing<T>(List<T> list, string? name, Func<T, string> named, string what) where T : class =>
        name is null ? null! : list.Find(x => named(x) == name) ?? throw new ToolInputException($"There is no {what} named '{name}'.");

    private static void FreeName(IEnumerable<string> names, string name, string? was, string what)
    {
        if (name != was && names.Contains(name, StringComparer.Ordinal)) throw new ToolInputException($"There is already a {what} named '{name}'.");
    }

    private static void Set(List<List<string>> lines, string key, string value)
    {
        foreach (var line in lines)
            for (var i = 0; i + 1 < line.Count; i += 2)
                if (line[i] == key) { line[i + 1] = value; return; }
        if (lines.Count == 0) lines.Add([]);
        lines[^1].Add(key); lines[^1].Add(value);
    }

    private static void RenameIn(StructuralModel m, string heading, string was, string name)
    {
        if (m.Passthrough.Find(s => s.Heading == heading) is not { } raw) return;
        for (var i = 0; i < raw.Lines.Count; i++) raw.Lines[i] = raw.Lines[i].Replace("\"" + was + "\"", "\"" + name + "\"", StringComparison.Ordinal);
    }

    private static JsonNode DefineMaterial(JsonElement a, ICsiLink e)
    {
        var (name, was) = (Need(a, "name"), Opt(a, "was"));
        double fc = NeedNum(a, "fc"), modulus = NeedNum(a, "elastic_modulus");
        if (fc <= 0 || modulus <= 0) throw new ToolInputException("fc and the modulus of elasticity are above 0.");
        return Offline(e).Edit(m =>
        {
            FreeName(m.Materials.Select(x => x.Name), name, was, "material");
            var material = Existing(m.Materials, was, x => x.Name, "material");
            if (material is null)
            {
                m.AddConcrete(name, fc, modulus, Num(a, "unit_weight") ?? 25);
                material = m.Materials[^1];
            }
            else
            {
                if (!material.IsConcrete) throw new ToolInputException($"'{was}' is {material.Type}: the Modeler changes concrete materials only.");
                Set(material.Lines, "FC", N(fc)); Set(material.Lines, "E", N(modulus));
                if (Num(a, "unit_weight") is { } w) Set(material.Lines, "WEIGHTPERVOLUME", N(w));
            }
            if (Num(a, "poisson") is { } u) Set(material.Lines, "U", N(u));
            if (was is not null && was != name)
            {
                material.Name = name;
                Set(material.Lines, "GRADE", E2kText.Quote(name));
                foreach (var s in m.FrameSections.Where(s => s.Material == was)) Set(s.Lines, "MATERIAL", E2kText.Quote(name));
                foreach (var s in m.ShellSections.Where(s => s.Material == was)) Set(s.Lines, "MATERIAL", E2kText.Quote(name));
            }
            return Done(was is null ? $"added {name}" : $"changed {name}");
        });
    }

    private static string Material(StructuralModel m, JsonElement a)
    {
        var material = Opt(a, "material") ?? ModelEdit.DefaultConcrete;
        if (!m.Materials.Exists(x => x.Name == material)) throw new ToolInputException($"There is no material named '{material}'.");
        return material;
    }

    private static JsonNode DefineFrameSection(JsonElement a, ICsiLink e)
    {
        var (name, was, kind) = (Need(a, "name"), Opt(a, "was"), Need(a, "kind"));
        double depth = NeedNum(a, "depth"), width = NeedNum(a, "width");
        if (depth <= 0 || width <= 0) throw new ToolInputException("The depth and width are above 0.");
        return Offline(e).Edit(m =>
        {
            FreeName(m.FrameSections.Select(x => x.Name), name, was, "frame section");
            var material = Material(m, a);
            var section = Existing(m.FrameSections, was, x => x.Name, "frame section");
            if (section is null) { m.AddRectangularSection(name, material, depth, width, kind == "beam"); return Done($"added {name}"); }
            if (!section.Shape.Equals("Concrete Rectangular", StringComparison.OrdinalIgnoreCase))
                throw new ToolInputException($"'{was}' is a {section.Shape} section: the Modeler changes rectangular concrete sections only.");
            Set(section.Lines, "MATERIAL", E2kText.Quote(material)); Set(section.Lines, "D", N(depth)); Set(section.Lines, "B", N(width));
            if (was != name)
            {
                section.Name = name;
                foreach (var x in m.LineAssigns.Where(x => x.Section == was)) x.Section = name;
                RenameIn(m, "CONCRETE SECTIONS", was!, name);
            }
            return Done($"changed {name}");
        });
    }

    private static JsonNode DefineShellSection(JsonElement a, ICsiLink e)
    {
        var (name, was, kind) = (Need(a, "name"), Opt(a, "was"), Need(a, "kind"));
        var thickness = NeedNum(a, "thickness");
        if (thickness <= 0) throw new ToolInputException("The thickness is above 0.");
        return Offline(e).Edit(m =>
        {
            FreeName(m.ShellSections.Select(x => x.Name), name, was, "slab or wall section");
            var material = Material(m, a);
            var section = Existing(m.ShellSections, was, x => x.Name, "slab or wall section");
            if (section is null)
            {
                if (kind == "wall") m.AddWallSection(name, material, thickness); else m.AddSlabSection(name, material, thickness);
                return Done($"added {name}");
            }
            Set(section.Lines, "MATERIAL", E2kText.Quote(material));
            Set(section.Lines, section.IsWall ? "WALLTHICKNESS" : section.Get("DECKSLABDEPTH") is not null ? "DECKSLABDEPTH" : "SLABTHICKNESS", N(thickness));
            if (was != name)
            {
                section.Name = name;
                foreach (var x in m.AreaAssigns.Where(x => x.Section == was)) x.Section = name;
            }
            return Done($"changed {name}");
        });
    }

    // ---- loads ----

    private static void RenameCase(StructuralModel m, string was, string name)
    {
        foreach (var c in m.Combos)
            foreach (var line in c.Lines)
                if (line.Count >= 2 && line[0] is "LOADCASE" or "LOADCOMBO" && E2kText.Unquote(line[1]) == was) line[1] = E2kText.Quote(name);
    }

    private static void Retag(List<string> tokens, string was, string name)
    {
        for (var i = 0; i + 1 < tokens.Count; i += 2)
            if (tokens[i] == "LC" && E2kText.Unquote(tokens[i + 1]) == was) tokens[i + 1] = E2kText.Quote(name);
    }

    private static JsonNode DefineLoadPattern(JsonElement a, ICsiLink e)
    {
        var (name, was) = (Need(a, "name"), Opt(a, "was"));
        var type = ModelEdit.LoadPatternTypes.FirstOrDefault(t => t.Equals(Need(a, "type"), StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolInputException($"Load pattern types: {string.Join(", ", ModelEdit.LoadPatternTypes)}.");
        var selfWeight = Num(a, "self_weight") ?? 0;
        return Offline(e).Edit(m =>
        {
            FreeName(m.LoadPatterns.Select(x => x.Name).Concat(m.Combos.Select(c => c.Name)), name, was, "load pattern or combination");
            var pattern = Existing(m.LoadPatterns, was, x => x.Name, "load pattern");
            if (pattern is null) { m.AddLoadPattern(name, type, selfWeight); return Done($"added {name}"); }
            pattern.Type = type; pattern.SelfWeight = selfWeight;
            if (was != name)
            {
                pattern.Name = name;
                foreach (var l in m.LineLoads) Retag(l.Tokens, was!, name);
                foreach (var l in m.AreaLoads) Retag(l.Tokens, was!, name);
                foreach (var l in m.PointLoads) Retag(l.Tokens, was!, name);
                if (m.LoadCases.Find(c => c.Name == was) is { } loadCase)
                {
                    loadCase.Name = name;
                    foreach (var line in loadCase.Lines)
                        for (var i = 0; i + 1 < line.Count; i += 2)
                            if (line[i] == "LOADPAT" && E2kText.Unquote(line[i + 1]) == was) line[i + 1] = E2kText.Quote(name);
                    RenameCase(m, was!, name);
                }
            }
            return Done($"changed {name}");
        });
    }

    private static readonly string[] ComboTypes = ["Linear Add", "Envelope", "Absolute Add", "SRSS", "Range Add"];

    private static JsonNode DefineCombination(JsonElement a, ICsiLink e)
    {
        var (name, was) = (Need(a, "name"), Opt(a, "was"));
        var type = ComboTypes.FirstOrDefault(t => t.Equals(Opt(a, "type") ?? "Linear Add", StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolInputException($"Combination types: {string.Join(", ", ComboTypes)}.");
        var cases = a.GetProperty("cases").EnumerateArray().Select(c => (Case: Need(c, "case"), Factor: NeedNum(c, "factor"))).ToList();
        if (cases.Count == 0) throw new ToolInputException("A combination needs one load case or combination at least.");
        return Offline(e).Edit(m =>
        {
            FreeName(m.Combos.Select(c => c.Name).Concat(m.LoadCases.Select(c => c.Name)), name, was, "load case or combination");
            var lines = new List<List<string>> { new() { "TYPE", E2kText.Quote(type) } };
            foreach (var (loadCase, factor) in cases)
            {
                if (loadCase == name || loadCase == was) throw new ToolInputException("A combination can't include itself.");
                var isCase = m.LoadCases.Exists(c => c.Name == loadCase);
                if (!isCase && !m.Combos.Exists(c => c.Name == loadCase)) throw new ToolInputException($"There is no load case or combination named '{loadCase}'.");
                lines.Add([isCase ? "LOADCASE" : "LOADCOMBO", E2kText.Quote(loadCase), "SF", N(factor)]);
            }
            var combo = Existing(m.Combos, was, c => c.Name, "combination");
            if (combo is null) { m.Combos.Add(new LoadCombo { Name = name, Lines = lines }); return Done($"added {name}"); }
            // What ETABS keeps on a combination beyond its type and cases (design use, ...) stays.
            combo.Lines = lines.Concat(combo.Lines.Skip(1).Where(l => l.Count > 0 && l[0] is not ("LOADCASE" or "LOADCOMBO" or "TYPE"))).ToList();
            if (was != name) { combo.Name = name; RenameCase(m, was!, name); }
            return Done($"changed {name}");
        });
    }

    private static JsonNode DeleteDefinition(JsonElement a, ICsiLink e)
    {
        var (kind, name) = (Need(a, "kind"), Need(a, "name"));
        return Offline(e).Edit<JsonNode>(m =>
        {
            void InUse(int count, string by) { if (count > 0) throw new ToolInputException($"'{name}' is used by {count} {by}, so it was not deleted."); }
            switch (kind)
            {
                case "material":
                    InUse(m.FrameSections.Count(s => s.Material == name) + m.ShellSections.Count(s => s.Material == name), "sections");
                    if (m.Materials.RemoveAll(x => x.Name == name) == 0) throw new ToolInputException($"There is no material named '{name}'.");
                    break;
                case "frame_section":
                    InUse(m.LineAssigns.Count(x => x.Section == name), "members");
                    if (m.FrameSections.RemoveAll(x => x.Name == name) == 0) throw new ToolInputException($"There is no frame section named '{name}'.");
                    if (m.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS") is { } raw) raw.Lines.RemoveAll(l => l.Contains("CONCRETESECTION \"" + name + "\"", StringComparison.Ordinal) || l.Contains("CONCRETESECTION  \"" + name + "\"", StringComparison.Ordinal));
                    break;
                case "shell_section":
                    InUse(m.AreaAssigns.Count(x => x.Section == name), "floors and walls");
                    if (m.ShellSections.RemoveAll(x => x.Name == name) == 0) throw new ToolInputException($"There is no slab or wall section named '{name}'.");
                    break;
                case "load_pattern":
                    if (!m.LoadPatterns.Exists(p => p.Name == name)) throw new ToolInputException($"There is no load pattern named '{name}'.");
                    if (m.LoadPatterns.Count == 1) throw new ToolInputException("A model keeps one load pattern at least.");
                    InUse(m.Combos.Count(c => c.Lines.Any(l => l.Count >= 2 && l[0] == "LOADCASE" && E2kText.Unquote(l[1]) == name)), "combinations (take it out of them first)");
                    InUse(m.LoadCases.Count(c => c.Name != name && c.Lines.Any(l => l.Count >= 2 && l[0] == "LOADPAT" && E2kText.Unquote(l[1]) == name)), "other load cases (take it out of them first)");
                    InUse(CsiProMax.Lateral.MassSource.Of(m).Loads.Count(l => l.Pattern == name), "the mass source (take it out first)");
                    InUse(AnalysisDefs.PDelta(m).Loads.Count(l => l.Pattern == name), "the P-Delta option (take it out first)");
                    m.LoadPatterns.RemoveAll(p => p.Name == name);
                    m.LoadCases.RemoveAll(c => c.Name == name);
                    m.LineLoads.RemoveAll(l => l.Pattern == name); m.AreaLoads.RemoveAll(l => l.Pattern == name); m.PointLoads.RemoveAll(l => l.Pattern == name);
                    break;
                case "combination":
                    InUse(m.Combos.Count(c => c.Lines.Any(l => l.Count >= 2 && l[0] == "LOADCOMBO" && E2kText.Unquote(l[1]) == name)), "other combinations");
                    if (m.Combos.RemoveAll(c => c.Name == name) == 0) throw new ToolInputException($"There is no combination named '{name}'.");
                    break;
                case "load_case":
                    InUse(m.Combos.Count(c => c.Lines.Any(l => l.Count >= 2 && l[0] == "LOADCASE" && E2kText.Unquote(l[1]) == name)), "combinations (take it out of them first)");
                    InUse(m.LoadCases.Count(c => c.Lines.Any(l => (E2kText.Value(l, "MODALCASE") == name || E2kText.Value(l, "INITCOND") == name) && c.Name != name)), "other load cases (as their modal case or initial conditions)");
                    if (m.LoadCases.RemoveAll(c => c.Name == name) == 0) throw new ToolInputException($"There is no load case named '{name}'.");
                    break;
                case "function":
                    InUse(AnalysisDefs.UsersOfFunction(m, name).Count(), "load cases");
                    if (!AnalysisDefs.DeleteFunction(m, name)) throw new ToolInputException($"There is no function named '{name}'.");
                    break;
                default:
                    throw new ToolInputException("kind is material, frame_section, shell_section, load_pattern, load_case, function or combination.");
            }
            return Done($"deleted {kind.Replace('_', ' ')} {name}");
        });
    }
}
