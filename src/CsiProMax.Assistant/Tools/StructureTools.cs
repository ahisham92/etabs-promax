using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Model;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's Define and Assign items for how the structure is grouped: diaphragms (rigid or semi-rigid, on floors
/// or joints), pier and spandrel labels for shear wall design, groups, and section cuts by group. Modeler only;
/// written into the .e2k as ETABS writes them, and groups into the .s2k.
/// </summary>
public static class StructureTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This is the Modeler's own Define or Assign item (no CSI program behind it). In a live program use assign_diaphragm, assign_group or assign_pier_spandrel.");

    public const string PierHeading = "PIER/SPANDREL NAMES", GroupHeading = "GROUPS", CutHeading = "SECTION CUTS";
    public static readonly string[] Colors = ["Red", "Green", "Blue", "Yellow", "Cyan", "Magenta", "Orange", "Gray"];

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("define_diaphragm",
            "Adds, changes or deletes a diaphragm (Define > Diaphragms): rigid or semi-rigid. A diaphragm in use cannot be deleted.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"semi_rigid":{"type":"boolean"},"delete":{"type":"boolean"}}}""",
            Ask.Changes, DefineDiaphragm, a => a.Flag("delete") ? $"Delete the diaphragm {a.Text("name")}" : $"Define the diaphragm {a.Text("name")}"),
        new("assign_diaphragm_data",
            "Gives floors (label@story) or joints (point@story) a diaphragm (Assign > Shell > Diaphragms, Assign > Joint > Diaphragms); \"\" takes it off.",
            """{"type":"object","required":["objects","diaphragm"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"joints":{"type":"boolean","description":"The objects are joints. Default false: floors."},"diaphragm":{"type":"string"}}}""",
            Ask.Changes, AssignDiaphragm, a => $"Assign the diaphragm {a.Text("diaphragm")}"),
        new("define_pier_spandrel",
            "Adds or deletes a pier or spandrel label (Define > Pier Labels, Spandrel Labels), for shear wall design. A label in use cannot be deleted.",
            """{"type":"object","required":["kind","name"],"properties":{"kind":{"type":"string","enum":["pier","spandrel"]},"name":{"type":"string"},"multistory":{"type":"boolean","description":"A spandrel: Multi-Story Spandrel."},"was":{"type":"string","description":"Change Name: the label's name now."},"delete":{"type":"boolean"}}}""",
            Ask.Changes, DefineLabel, a => $"{(a.Flag("delete") ? "Delete" : "Define")} the {a.Text("kind")} {a.Text("name")}"),
        new("assign_pier_spandrel_data",
            "Labels walls and frames (label@story) as a pier and/or spandrel (Assign > Shell > Pier Label, Spandrel Label); \"\" takes a label off. The label is made when it is not there.",
            """{"type":"object","required":["objects"],"properties":{"objects":{"type":"array","items":{"type":"string"}},"pier":{"type":"string"},"spandrel":{"type":"string"}}}""",
            Ask.Changes, AssignLabel, a => "Assign pier and spandrel labels"),
        new("define_group",
            "Adds, changes or deletes a group (Define > Groups), with its color. Deleting a group takes its objects out of it, and refuses while a section cut uses it.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"color":{"type":"string"},"delete":{"type":"boolean"},"uses":{"type":"object","additionalProperties":{"type":"boolean"},"description":"What the group is used for, by the program's field names: Steel, Concrete, Composite (design groups); SAP2000 also Selection, SectionCut, Aluminum, ColdFormed, Stage, Bridge, AutoSeismic, AutoWind, MassWeight."}}}""",
            Ask.Changes, DefineGroup, a => $"{(a.Flag("delete") ? "Delete" : "Define")} the group {a.Text("name")}"),
        new("assign_group_data",
            "Puts frames, slabs and walls (label@story) and joints (point@story, with joints) in a group, or takes them out with remove (Assign > Assign Objects to Group). The group is made when it is not there.",
            """{"type":"object","required":["group","objects"],"properties":{"group":{"type":"string"},"objects":{"type":"array","items":{"type":"string"}},"joints":{"type":"boolean"},"remove":{"type":"boolean"}}}""",
            Ask.Changes, AssignGroup, a => $"{(a.Flag("remove") ? "Take objects out of" : "Put objects in")} the group {a.Text("group")}"),
        new("define_section_cut",
            "Adds or deletes a section cut (Define > Section Cuts) from a group: the forces across the group's objects are reported after the analysis.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"group":{"type":"string"},"delete":{"type":"boolean"},"result_type":{"type":"string","enum":["Analysis","Design"],"description":"Analysis (F1, F2, F3, M1, M2, M3) or Design (P, V2, V3, T, M2, M3)."},"user_location":{"type":"boolean","description":"Section cut force location user defined at x, y, z; default false."},"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"rot_z":{"type":"number","description":"Rotation of the cut's local axes about Z, Y, X (degrees)."},"rot_y":{"type":"number"},"rot_x":{"type":"number"}}}""",
            Ask.Changes, DefineCut, a => $"{(a.Flag("delete") ? "Delete" : "Define")} the section cut {a.Text("name")}"),
        new("structure_definitions",
            "What the diaphragm, pier, spandrel, group and section cut forms show: each with where it is used.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => Definitions(Offline(e).Model)),
    ];

    private static T Core<T>(OfflineEtabs link, Func<StructuralModel, T> work)
    {
        try { return link.Edit(work); }
        catch (EtabsException ex) { throw new ToolInputException(ex.Message); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
    }

    private static void Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ToolInputException("Give it a name.");
        if (name.Contains('"')) throw new ToolInputException("A name cannot have a double quote in it.");
    }

    private static List<(string Label, string Story)> Objects(JsonElement a)
    {
        var list = new List<(string, string)>();
        foreach (var o in a.Texts("objects"))
        {
            var at = o.LastIndexOf('@');
            if (at <= 0) throw new ToolInputException($"'{o}' is not label@story.");
            list.Add((o[..at], o[(at + 1)..]));
        }
        if (list.Count == 0) throw new ToolInputException("Name the objects, as label@story.");
        return list;
    }

    // ---- raw sections the Modeler keeps word for word ----

    private static RawSection Section(StructuralModel m, string heading, string after)
    {
        var raw = m.Passthrough.Find(r => r.Heading == heading);
        if (raw is null) m.Passthrough.Add(raw = new RawSection { Heading = heading, After = after });
        return raw;
    }

    private static IEnumerable<List<string>> Lines(StructuralModel m, string heading) =>
        m.Passthrough.Where(r => r.Heading == heading).SelectMany(r => r.Lines).Select(Tokenize).Where(t => t.Count >= 2);

    private static void RemoveLines(StructuralModel m, string heading, Func<List<string>, bool> which)
    {
        foreach (var r in m.Passthrough.Where(r => r.Heading == heading)) r.Lines.RemoveAll(l => Tokenize(l) is { Count: >= 2 } t && which(t));
        m.Passthrough.RemoveAll(r => r.Heading == heading && r.Lines.Count == 0);
    }

    // ---- diaphragms ----

    private static JsonNode DefineDiaphragm(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var name = a.Need("name"); Name(name);
        var m0 = link.Model;
        if (a.Flag("delete"))
        {
            if (!m0.Diaphragms.Exists(d => d.Name == name)) throw new ToolInputException($"There is no diaphragm '{name}'.");
            var users = m0.AreaAssigns.Where(x => x.Diaphragm == name).Select(x => x.Label + "@" + x.Story).Concat(m0.PointAssigns.Where(x => x.Diaphragm == name).Select(x => x.Point + "@" + x.Story)).Take(5).ToList();
            if (users.Count > 0) throw new ToolInputException($"{name} is on {string.Join(", ", users)}: take it off those first.");
            Core(link, m => m.Diaphragms.RemoveAll(d => d.Name == name));
            return new JsonObject { ["deleted"] = name };
        }
        var type = a.Flag("semi_rigid") ? "SEMIRIGID" : "RIGID";
        Core(link, m =>
        {
            var d = m.Diaphragms.Find(x => x.Name == name);
            if (d is null) m.Diaphragms.Add(d = new Diaphragm { Name = name });
            d.Type = type;
            return 0;
        });
        return new JsonObject { ["diaphragm"] = name, ["type"] = type };
    }

    private static JsonNode AssignDiaphragm(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var m0 = link.Model;
        var d = a.Text("diaphragm");
        if (d.Length > 0 && !m0.Diaphragms.Exists(x => x.Name == d)) throw new ToolInputException($"There is no diaphragm '{d}'. Define it with define_diaphragm first.");
        var joints = a.Flag("joints");
        var objects = Objects(a);
        foreach (var (l, s) in objects)
        {
            var ok = joints ? m0.Point(l) is not null && m0.StoryIndex(s) >= 0 : m0.AreaAssigns.Exists(x => x.Label == l && x.Story == s && m0.Area(l)?.Kind == AreaKind.Floor);
            if (!ok) throw new ToolInputException($"There is no {(joints ? "joint" : "floor")} {l}@{s}.");
        }
        return Core(link, m =>
        {
            foreach (var (l, s) in objects)
                if (joints)
                {
                    var p = m.PointAssigns.Find(x => x.Point == l && x.Story == s);
                    if (p is null) m.PointAssigns.Add(p = new PointAssign { Point = l, Story = s });
                    p.Diaphragm = d.Length == 0 ? null : d;
                }
                else m.AreaAssigns.First(x => x.Label == l && x.Story == s).Diaphragm = d.Length == 0 ? null : d;
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        });
    }

    // ---- pier and spandrel labels ----

    public static List<(string Kind, string Name)> Labels(StructuralModel m) =>
        Lines(m, PierHeading).Where(t => t[0] is "PIERNAME" or "SPANDRELNAME").Select(t => (t[0] == "PIERNAME" ? "pier" : "spandrel", Unquote(t[1]))).Distinct().ToList();

    private static IEnumerable<string> LabelUsers(StructuralModel m, string kind, string name)
    {
        var key = kind == "pier" ? "PIER" : "SPANDREL";
        return m.AreaAssigns.Where(x => PropertyDefs.Value(x.Extra, key) == name).Select(x => x.Label + "@" + x.Story)
            .Concat(m.LineAssigns.Where(x => PropertyDefs.Value(x.Extra, key) == name).Select(x => x.Label + "@" + x.Story));
    }

    private static void AddLabel(StructuralModel m, string kind, string name)
    {
        if (Labels(m).Contains((kind, name))) return;
        Section(m, PierHeading, "WALL PROPERTIES").Lines.Add("  " + Line([kind == "pier" ? "PIERNAME" : "SPANDRELNAME", Quote(name)]));
    }

    private static JsonNode DefineLabel(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var kind = a.Need("kind").ToLowerInvariant();
        if (kind is not ("pier" or "spandrel")) throw new ToolInputException("kind is pier or spandrel.");
        var name = a.Need("name"); Name(name);
        if (a.Flag("delete"))
        {
            if (!Labels(link.Model).Contains((kind, name))) throw new ToolInputException($"There is no {kind} '{name}'.");
            var users = LabelUsers(link.Model, kind, name).Take(5).ToList();
            if (users.Count > 0) throw new ToolInputException($"{name} labels {string.Join(", ", users)}: take it off those first.");
            Core(link, m => { RemoveLines(m, PierHeading, t => t[0] == (kind == "pier" ? "PIERNAME" : "SPANDRELNAME") && Unquote(t[1]) == name); if (kind == "spandrel") StructureExtras.Remove(m, StructureExtras.SpandrelTable, name); return 0; });
            return new JsonObject { ["deleted"] = name };
        }
        var was = a.Text("was").Trim();
        if (was.Length > 0 && was != name)
        {
            if (!Labels(link.Model).Contains((kind, was))) throw new ToolInputException($"There is no {kind} '{was}'.");
            if (Labels(link.Model).Contains((kind, name))) throw new ToolInputException($"There is a {kind} '{name}' already.");
        }
        Core(link, m =>
        {
            if (was.Length > 0 && was != name)
            {
                // Change Name: the label and every wall or frame it labels.
                var key = kind == "pier" ? "PIERNAME" : "SPANDRELNAME";
                foreach (var r in m.Passthrough.Where(r => r.Heading == PierHeading))
                    r.Lines = r.Lines.Select(l => Tokenize(l) is { Count: >= 2 } t && t[0] == key && Unquote(t[1]) == was ? "  " + Line([key, Quote(name), .. t.Skip(2)]) : l).ToList();
                var word = kind == "pier" ? "PIER" : "SPANDREL";
                foreach (var extra in m.AreaAssigns.Select(x => x.Extra).Concat(m.LineAssigns.Select(x => x.Extra)))
                    if (PropertyDefs.Value(extra, word) == was) PropertyDefs.Put(extra, word, Quote(name));
                if (kind == "spandrel" && StructureExtras.Get(m, StructureExtras.SpandrelTable, was) is { Count: > 0 } old)
                {
                    StructureExtras.Remove(m, StructureExtras.SpandrelTable, was);
                    StructureExtras.Set(m, StructureExtras.SpandrelTable, name, old);
                }
            }
            else AddLabel(m, kind, name);
            if (kind == "spandrel" && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("multistory", out _))
            {
                if (a.Flag("multistory")) StructureExtras.Set(m, StructureExtras.SpandrelTable, name, new Dictionary<string, string> { ["MultiStory"] = "Yes" });
                else StructureExtras.Remove(m, StructureExtras.SpandrelTable, name);
            }
            return 0;
        });
        return new JsonObject { [kind] = name };
    }

    private static bool Has(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String;

    private static JsonNode AssignLabel(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var m0 = link.Model;
        var objects = Objects(a);
        string? pier = Has(a, "pier") ? a.Text("pier") : null, spandrel = Has(a, "spandrel") ? a.Text("spandrel") : null;
        if (pier is null && spandrel is null) throw new ToolInputException("Give pier, spandrel or both (\"\" takes one off).");
        if (pier is { Length: > 0 }) Name(pier);
        if (spandrel is { Length: > 0 }) Name(spandrel);
        foreach (var (l, s) in objects)
            if (!m0.AreaAssigns.Exists(x => x.Label == l && x.Story == s) && !m0.LineAssigns.Exists(x => x.Label == l && x.Story == s))
                throw new ToolInputException($"There is no wall or frame {l}@{s}.");
        return Core(link, m =>
        {
            if (pier is { Length: > 0 }) AddLabel(m, "pier", pier);
            if (spandrel is { Length: > 0 }) AddLabel(m, "spandrel", spandrel);
            foreach (var (l, s) in objects)
            {
                var extra = m.AreaAssigns.Find(x => x.Label == l && x.Story == s)?.Extra ?? m.LineAssigns.First(x => x.Label == l && x.Story == s).Extra;
                if (pier is not null) PropertyDefs.Put(extra, "PIER", pier.Length == 0 ? null : Quote(pier));
                if (spandrel is not null) PropertyDefs.Put(extra, "SPANDREL", spandrel.Length == 0 ? null : Quote(spandrel));
            }
            return (JsonNode)new JsonObject { ["assigned"] = objects.Count };
        });
    }

    // ---- groups ----

    public static List<Group> Groups(StructuralModel m) => GroupDefs.Groups(m);

    private static JsonNode DefineGroup(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var name = a.Need("name"); Name(name);
        if (a.Flag("delete"))
        {
            if (!Groups(link.Model).Exists(g => g.Name == name)) throw new ToolInputException($"There is no group '{name}'.");
            var cuts = Cuts(link.Model).Where(c => c.Group == name).Select(c => c.Name).ToList();
            if (cuts.Count > 0) throw new ToolInputException($"The section cut {string.Join(", ", cuts)} uses {name}: delete it first.");
            Core(link, m => { RemoveLines(m, GroupHeading, t => t[0] == "GROUP" && Unquote(t[1]) == name); StructureExtras.Remove(m, StructureExtras.GroupTable, name); return 0; });
            return new JsonObject { ["deleted"] = name };
        }
        var color = Colors.FirstOrDefault(c => c.Equals(a.Text("color", "Red"), StringComparison.OrdinalIgnoreCase)) ?? throw new ToolInputException($"The color is one of {string.Join(", ", Colors)}.");
        var uses = new Dictionary<string, string>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("uses", out var u) && u.ValueKind == JsonValueKind.Object)
            foreach (var p in u.EnumerateObject())
            {
                var key = StructureExtras.Fields[StructureExtras.GroupTable].FirstOrDefault(f => f.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolInputException($"'{p.Name}' is not a group use. The uses: {string.Join(", ", StructureExtras.Fields[StructureExtras.GroupTable])}.");
                uses[key] = p.Value.ValueKind == JsonValueKind.True ? "Yes" : "No";
            }
        Core(link, m => { SetGroup(m, name, color); if (uses.Count > 0) StructureExtras.Set(m, StructureExtras.GroupTable, name, uses); return 0; });
        return new JsonObject { ["group"] = name, ["color"] = color };
    }

    private static void SetGroup(StructuralModel m, string name, string? color)
    {
        var raw = Section(m, GroupHeading, "AREA ASSIGNS");
        var at = raw.Lines.FindIndex(l => Tokenize(l) is { Count: >= 4 } t && t[0] == "GROUP" && Unquote(t[1]) == name && t[2] == "COLOR");
        if (at >= 0) { if (color is not null) raw.Lines[at] = "  " + Line(["GROUP", Quote(name), "COLOR", Quote(color)]); return; }
        raw.Lines.Insert(raw.Lines.FindLastIndex(l => Tokenize(l) is { Count: >= 4 } t && t[2] == "COLOR") + 1, "  " + Line(["GROUP", Quote(name), "COLOR", Quote(color ?? "Red")]));
    }

    private static JsonNode AssignGroup(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var m0 = link.Model;
        var group = a.Need("group"); Name(group);
        var joints = a.Flag("joints");
        var objects = Objects(a).Select(o =>
        {
            if (joints) return m0.Point(o.Label) is not null && m0.StoryIndex(o.Story) >= 0 ? ("POINT", o.Label, o.Story) : throw new ToolInputException($"There is no joint {o.Label}@{o.Story}.");
            if (m0.LineAssigns.Exists(x => x.Label == o.Label && x.Story == o.Story)) return ("LINE", o.Label, o.Story);
            if (m0.AreaAssigns.Exists(x => x.Label == o.Label && x.Story == o.Story)) return ("AREA", o.Label, o.Story);
            throw new ToolInputException($"There is no frame, slab or wall {o.Label}@{o.Story}.");
        }).ToList();
        var remove = a.Flag("remove");
        if (remove && !Groups(m0).Exists(g => g.Name == group)) throw new ToolInputException($"There is no group '{group}'.");
        return Core(link, m =>
        {
            SetGroup(m, group, null);
            var raw = m.Passthrough.First(r => r.Heading == GroupHeading);
            foreach (var (type, label, story) in objects)
            {
                raw.Lines.RemoveAll(l => Tokenize(l) is { Count: >= 5 } t && t[0] == "GROUP" && Unquote(t[1]) == group && t[2] == type && Unquote(t[3]) == label && Unquote(t[4]) == story);
                if (!remove) raw.Lines.Add("  " + Line(["GROUP", Quote(group), type, Quote(label), Quote(story)]));
            }
            return (JsonNode)new JsonObject { ["group"] = group, [remove ? "removed" : "added"] = objects.Count, ["members"] = Groups(m).First(g => g.Name == group).Members.Count };
        });
    }

    // ---- section cuts ----

    public static List<(string Name, string Group)> Cuts(StructuralModel m) =>
        Lines(m, CutHeading).Where(t => t[0] == "SECTIONCUT").Select(t => (Unquote(t[1]), Unquote(Value(t, "GROUP") ?? "\"\""))).ToList();

    private static JsonNode DefineCut(JsonElement a, ICsiLink e)
    {
        var link = Offline(e);
        var name = a.Need("name"); Name(name);
        if (a.Flag("delete"))
        {
            if (!Cuts(link.Model).Exists(c => c.Name == name)) throw new ToolInputException($"There is no section cut '{name}'.");
            Core(link, m => { RemoveLines(m, CutHeading, t => t[0] == "SECTIONCUT" && Unquote(t[1]) == name); StructureExtras.Remove(m, StructureExtras.CutTable, name); return 0; });
            return new JsonObject { ["deleted"] = name };
        }
        var group = a.Need("group");
        var g = Groups(link.Model).Find(x => x.Name == group) ?? throw new ToolInputException($"There is no group '{group}'. Make it with define_group and put the objects to cut in it.");
        if (g.Members.Count == 0) throw new ToolInputException($"The group {group} is empty: put the objects to cut in it first.");
        Core(link, m =>
        {
            RemoveLines(m, CutHeading, t => t[0] == "SECTIONCUT" && Unquote(t[1]) == name);
            Section(m, CutHeading, GroupHeading).Lines.Add("  " + Line(["SECTIONCUT", Quote(name), "GROUP", Quote(group)]));
            // Section Cut Data's other fields: result type, force location, local axes.
            var user = a.Flag("user_location");
            string N(string k) => Number(a.Number(k, 0));
            StructureExtras.Remove(m, StructureExtras.CutTable, name);
            StructureExtras.Set(m, StructureExtras.CutTable, name, new Dictionary<string, string>
            {
                ["ResultType"] = a.Text("result_type", "Analysis").StartsWith("Design", StringComparison.OrdinalIgnoreCase) ? "Design" : "Analysis",
                ["ResultLoc"] = user ? "User" : "Default", ["LocX"] = user ? N("x") : "0", ["LocY"] = user ? N("y") : "0", ["LocZ"] = user ? N("z") : "0",
                ["RotAboutZ"] = N("rot_z"), ["RotAboutY"] = N("rot_y"), ["RotAboutX"] = N("rot_x"),
            });
            return 0;
        });
        return new JsonObject { ["section_cut"] = name, ["group"] = group };
    }

    private static JsonNode Definitions(StructuralModel m) => new JsonObject
    {
        ["diaphragms"] = new JsonArray(m.Diaphragms.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name, ["semi_rigid"] = d.Type.Contains("SEMI", StringComparison.OrdinalIgnoreCase),
            ["floors"] = m.AreaAssigns.Count(x => x.Diaphragm == d.Name), ["joints"] = m.PointAssigns.Count(x => x.Diaphragm == d.Name),
        }).ToArray()),
        ["piers"] = new JsonArray(Labels(m).Where(l => l.Kind == "pier").Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["used_by"] = LabelUsers(m, "pier", l.Name).Count() }).ToArray()),
        ["spandrels"] = new JsonArray(Labels(m).Where(l => l.Kind == "spandrel").Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["used_by"] = LabelUsers(m, "spandrel", l.Name).Count(), ["multistory"] = StructureExtras.Yes(StructureExtras.Get(m, StructureExtras.SpandrelTable, l.Name), "MultiStory", false) }).ToArray()),
        ["groups"] = new JsonArray(Groups(m).Select(g => (JsonNode)new JsonObject
        {
            ["name"] = g.Name, ["color"] = g.Color, ["members"] = g.Members.Count,
            ["uses"] = new JsonObject(StructureExtras.Get(m, StructureExtras.GroupTable, g.Name).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)(kv.Value == "Yes")))),
        }).ToArray()),
        ["section_cuts"] = new JsonArray(Cuts(m).Select(c => (JsonNode)new JsonObject
        {
            ["name"] = c.Name, ["group"] = c.Group,
            ["options"] = new JsonObject(StructureExtras.Get(m, StructureExtras.CutTable, c.Name).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        }).ToArray()),
        ["colors"] = new JsonArray(Colors.Select(c => (JsonNode)c).ToArray()),
    };
}
