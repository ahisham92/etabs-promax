using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Checks;
using CsiProMax.Model;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The Modeler's own tools, on top of the modelling tools shared with ETABS: start, open and save a model file, and
/// define what ETABS's dialogs define (stories, grids, materials, sections). The Modeler writes ETABS text files; it
/// has no analysis and no design, and never stands in for ETABS.
/// </summary>
public static class ModelerTools
{
    private static OfflineEtabs Offline(ICsiLink e) => e as OfflineEtabs
        ?? throw new ToolInputException("This tool works in the Modeler (no ETABS). With ETABS open, make the same change in ETABS or with run_etabs_code.");

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("new_model",
            "Starts a new, empty model in the Modeler with ETABS 23's own defaults (kN, m; its materials and the Dead and Live patterns). The model in the Modeler now is replaced: save it first if it is wanted. With 'template' concrete_frame it builds a regular concrete frame at once from x_spans, y_spans, stories and story_height (columns on every grid crossing, beams on every grid line, a slab per bay, fixed supports, SDL and live load).",
            """
            {"type":"object","required":["title"],"properties":{
              "title":{"type":"string"},
              "program":{"type":"string","enum":["etabs","sap2000","safe"],"description":"The CSI program the model is for (default etabs): the Modeler then offers what that program has, and saving writes its file (.e2k, .s2k or .f2k) beside the .cpm."},
              "template":{"type":"string","enum":["empty","concrete_frame"]},
              "x_spans":{"type":"array","items":{"type":"number"},"description":"Bay sizes along X, in m."},
              "y_spans":{"type":"array","items":{"type":"number"}},
              "stories":{"type":"integer"},"story_height":{"type":"number"},
              "sdl":{"type":"number","description":"Superimposed dead load on the slabs, kN/m2 (default 2)."},
              "live":{"type":"number","description":"Live load on the slabs, kN/m2 (default 3)."}}}
            """, Ask.Always, NewModel, a => $"Start a new model '{a.Text("title")}' (replaces the one open in the Modeler)"),

        new("set_model_program",
            "Changes the CSI program the Modeler's model is for (etabs, sap2000 or safe): the Modeler then offers what that program has, and saving writes its file beside the .cpm. Nothing in the model is removed: what the new program has no place for stays in the .cpm and is said in the answer.",
            """{"type":"object","required":["program"],"properties":{"program":{"type":"string","enum":["etabs","sap2000","safe"]}}}""",
            Ask.Changes, SetProgram, a => $"Make the model for {StructuralModel.TargetName(a.Text("program"))}"),

        new("open_model_file",
            "Opens a model file in the Modeler: an ETABS text file (.e2k, .$et), a SAP2000 text file (.s2k, .$2k), a SAFE text file (.f2k, .$fk) or a Modeler file (.cpm). A .s2k or .f2k the Modeler saved opens exactly as it was saved; one SAP2000 or SAFE wrote is read from its tables (levels from the Z grid lines, members on the level of their top end), and what has no place in the Modeler yet comes back in not_read. Everything in an .e2k is kept, also what the Modeler does not show, and is written back on save. The model in the Modeler now is replaced.",
            """{"type":"object","required":["path"],"properties":{"path":{"type":"string","description":"The file's full path."}}}""",
            Ask.Always, Open, a => $"Open {a.Text("path")} (replaces the model open in the Modeler)"),

        new("save_model_as",
            "Saves the Modeler's model under a name: an ETABS text file (.e2k) that licensed ETABS imports with File > Import > ETABS .e2k text file, and the Modeler's own .cpm beside it. Default folder: Documents\\CSI ProMax\\Models. The model is checked first and the findings come back with the answer.",
            """{"type":"object","properties":{"name":{"type":"string","description":"File name without extension. Default: the model's title."},"folder":{"type":"string"}}}""",
            Ask.Always, SaveAs, a => $"Save the model as {(a.Text("name").Length > 0 ? a.Text("name") : "its title")}.e2k"),

        new("add_stories",
            "Adds stories on top of the model, in order from the lowest: each with a name and a height.",
            """{"type":"object","required":["stories"],"properties":{"stories":{"type":"array","items":{"type":"object","required":["name","height"],"properties":{"name":{"type":"string"},"height":{"type":"number"}}}}}}""",
            Ask.Changes, AddStories, a => $"Add {a.Items("stories").Count()} stories"),

        new("add_grids",
            "Adds grid lines: each with a label, the direction it is spaced along (X: a line at x = coordinate; Y: a line at y = coordinate) and the coordinate.",
            """{"type":"object","required":["grids"],"properties":{"grids":{"type":"array","items":{"type":"object","required":["label","direction","coordinate"],"properties":{"label":{"type":"string"},"direction":{"type":"string","enum":["X","Y"]},"coordinate":{"type":"number"}}}}}}""",
            Ask.Changes, AddGrids, a => $"Add {a.Items("grids").Count()} grid lines"),

        new("add_concrete",
            "Adds a concrete material: name, cylinder strength fc and modulus of elasticity in the model's units (kN/m2 in kN, m), and unit weight (default 25 kN/m3).",
            """{"type":"object","required":["name","fc","elastic_modulus"],"properties":{"name":{"type":"string"},"fc":{"type":"number"},"elastic_modulus":{"type":"number"},"unit_weight":{"type":"number"}}}""",
            Ask.Changes, AddConcrete, a => $"Add the concrete {a.Text("name")}"),

        new("add_section",
            "Adds a section: a rectangular concrete column or beam (depth and width), a slab or a wall (thickness), in the model's length unit. 'material' is a material name from get_model_context (default: ETABS's own concrete).",
            """
            {"type":"object","required":["name","kind"],"properties":{
              "name":{"type":"string"},"kind":{"type":"string","enum":["column","beam","slab","wall"]},
              "depth":{"type":"number"},"width":{"type":"number"},"thickness":{"type":"number"},"material":{"type":"string"}}}
            """, Ask.Changes, AddSection, a => $"Add the {a.Text("kind")} section {a.Text("name")}"),

        new("check_model",
            "Checks the Modeler's model before it goes to ETABS: members with no section or an unknown one, beams with a free end, columns with nothing under them, stories with nothing on them, loads on objects or patterns that are not there. No analysis.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => Check(Offline(e))),

        new("model_quantities",
            "Quantities of the Modeler's model: count, length, area and concrete volume by kind and section.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => Quantity(Offline(e))),
    ];

    private static JsonNode Summary(OfflineEtabs o)
    {
        var m = o.Model;
        return new JsonObject
        {
            ["title"] = m.Title, ["program"] = StructuralModel.TargetName(m.Target), ["file"] = o.Path, ["stories"] = m.Stories.Count - 1, ["grids"] = m.Grids.Count,
            ["frames"] = m.LineAssigns.Count, ["areas"] = m.AreaAssigns.Count,
            ["frame_sections"] = m.FrameSections.Count, ["slab_and_wall_sections"] = m.ShellSections.Count,
        };
    }

    private static JsonNode NewModel(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var title = a.Need("title");
        StructuralModel model;
        try
        {
            if (a.Text("template", "empty") == "concrete_frame")
            {
                var xs = a.Items("x_spans").Select(v => v.GetDouble()).ToArray();
                var ys = a.Items("y_spans").Select(v => v.GetDouble()).ToArray();
                model = Templates.ConcreteFrame(title, xs, ys, a.Whole("stories", 1), a.Number("story_height", 3.2), a.Number("sdl", 2.0), a.Number("live", 3.0));
            }
            else model = ModelEdit.New(title);
        }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        model.Target = Target(a.Text("program", "etabs"));
        o.Replace(model, "");
        return Summary(o);
    }

    private static string Target(string program) => StructuralModel.Targets.Contains(program.ToLowerInvariant())
        ? program.ToLowerInvariant() : throw new ToolInputException($"'program' is one of {string.Join(", ", StructuralModel.Targets)}, not '{program}'.");

    private static JsonNode SetProgram(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var target = Target(a.Need("program"));
        var was = o.Model.Target;
        o.Edit(m => m.Target = target);
        var kept = new List<string>();
        var x = o.Model.Extras;
        if (target != "sap2000" && x is not null && (x.Constraints.Count + x.Lanes.Count + x.Vehicles.Count + x.MovingCases.Count) > 0) kept.Add("joint constraints and moving loads (SAP2000 only)");
        if (target != "safe" && x is { Strips.Count: > 0 }) kept.Add("design strips (SAFE only)");
        return new JsonObject
        {
            ["program"] = StructuralModel.TargetName(target), ["was"] = StructuralModel.TargetName(was),
            ["kept_but_not_used"] = new JsonArray(kept.Select(k => (JsonNode)k).ToArray()),
            ["saves"] = target switch { "sap2000" => ".cpm, .e2k and .s2k", "safe" => ".cpm, .e2k and .f2k", _ => ".cpm and .e2k" },
        };
    }

    private static JsonNode Open(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var path = a.Need("path").Trim().Trim('"');
        if (!File.Exists(path)) throw new ToolInputException($"There is no file at {path}.");
        OfflineEtabs.Opened opened;
        try { opened = OfflineEtabs.Open(path); }
        catch (Exception ex) when (ex is E2k.E2kFormatException or JsonException or IOException or InvalidDataException) { throw new ToolInputException($"The file could not be read: {ex.Message}"); }
        o.Replace(opened.Model, OfflineEtabs.SavesTo(path, opened));
        var result = Summary(o).AsObject();
        if (o.Path.Length == 0) result["note"] = "Opened as a copy: saving writes a new .e2k, the file you opened is not changed.";
        // What another program's file had that the Modeler has no place for yet: for the chat to say if asked, never a pop-up.
        if (opened.Notes.Count > 0) result["not_read"] = new JsonArray(opened.Notes.Take(40).Select(n => (JsonNode)n).ToArray());
        return result;
    }

    private static JsonNode SaveAs(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var name = a.Text("name");
        var folder = a.Text("folder");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ToolInputException("'name' is a file name without a folder. Give the folder in 'folder'.");
        var path = Path.Combine(folder.Length > 0 ? folder.Trim().Trim('"') : OfflineEtabs.DefaultFolder, (name.Length > 0 ? name : o.Model.Title.Length > 0 ? o.Model.Title : "Untitled") + ".e2k");
        var findings = ModelChecks.Run(o.Model);
        var saved = o.SaveAs(path);
        var program = o.Model.Target;
        return new JsonObject
        {
            ["file"] = o.ProgramFile.Length > 0 ? o.ProgramFile : saved, ["e2k"] = saved, ["modeler_file"] = Path.ChangeExtension(saved, ModelFile.Extension),
            ["program"] = StructuralModel.TargetName(program),
            ["errors"] = findings.Count(f => f.Severity == Severity.Error), ["warnings"] = findings.Count(f => f.Severity == Severity.Warning),
            ["findings"] = new JsonArray(findings.Take(30).Select(f => (JsonNode)$"{f.Severity}: {f.Message}").ToArray()),
            ["next"] = program switch
            {
                "sap2000" => o.ProgramFile.Length > 0 ? "In licensed SAP2000: File > Import > SAP2000 .s2k Text File, then run the analysis there. The Modeler does not analyse." : "The .s2k could not be written; the .cpm and .e2k hold the model.",
                "safe" => o.ProgramFile.Length > 0 ? "In licensed SAFE: File > Import > SAFE .F2K file, then run the analysis and design there. The Modeler does not analyse." : "No floor here has slabs SAFE can take, so no .f2k was written; the .cpm and .e2k hold the model.",
                _ => "In licensed ETABS: File > Import > ETABS .e2k text file, then run the analysis there. The Modeler does not analyse.",
            },
        };
    }

    private static JsonNode AddStories(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var items = a.Items("stories").Select(s => (Name: s.Need("name"), Height: s.Number("height"))).ToList();
        o.Edit(m => { foreach (var (name, height) in items) m.AddStory(name, height); return 0; });
        return new JsonObject { ["added"] = items.Count, ["stories"] = new JsonArray(e.Stories().Select(s => (JsonNode)$"{s.Name} at {Args.Round(s.Elevation)}").ToArray()) };
    }

    private static JsonNode AddGrids(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var items = a.Items("grids").Select(g => (Label: g.Need("label"), Direction: g.Need("direction").ToUpperInvariant(), At: g.Number("coordinate"))).ToList();
        if (items.Any(g => g.Direction is not ("X" or "Y"))) throw new ToolInputException("A grid's direction is X or Y.");
        var have = o.Model.Grids.Select(g => g.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var twice = items.Where(g => !have.Add(g.Label)).Select(g => g.Label).ToList();
        if (twice.Count > 0) throw new ToolInputException($"There is already a grid named {string.Join(", ", twice)}.");
        o.Edit(m => { foreach (var g in items) m.AddGrid(g.Label, g.Direction, g.At); return 0; });
        return new JsonObject { ["added"] = items.Count, ["grids"] = o.Model.Grids.Count };
    }

    private static void Fresh(IEnumerable<string> names, string name, string what)
    {
        if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ToolInputException($"There is already a {what} named '{name}'.");
    }

    private static JsonNode AddConcrete(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var name = a.Need("name");
        Fresh(e.Materials(), name, "material");
        double fc = a.Number("fc"), modulus = a.Number("elastic_modulus");
        if (fc <= 0 || modulus <= 0) throw new ToolInputException("fc and elastic_modulus are above 0, in the model's units.");
        o.Edit(m => m.AddConcrete(name, fc, modulus, a.Number("unit_weight", 25)));
        return new JsonObject { ["added"] = name };
    }

    private static JsonNode AddSection(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var name = a.Need("name");
        var kind = a.Need("kind");
        var material = a.Text("material", ModelEdit.DefaultConcrete);
        material = e.Materials().FirstOrDefault(m => string.Equals(m, material, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolInputException($"There is no material '{material}'. The model has: {string.Join(", ", e.Materials())}.");
        switch (kind)
        {
            case "column" or "beam":
                Fresh(e.FrameSections(), name, "frame section");
                double depth = a.Number("depth"), width = a.Number("width");
                if (depth <= 0 || width <= 0) throw new ToolInputException("depth and width are above 0.");
                o.Edit(m => m.AddRectangularSection(name, material, depth, width, kind == "beam"));
                break;
            case "slab" or "wall":
                Fresh(e.AreaSections(), name, "slab or wall section");
                var thickness = a.Number("thickness");
                if (thickness <= 0) throw new ToolInputException("thickness is above 0.");
                o.Edit(m => kind == "slab" ? m.AddSlabSection(name, material, thickness) : m.AddWallSection(name, material, thickness));
                break;
            default: throw new ToolInputException("kind is column, beam, slab or wall.");
        }
        return new JsonObject { ["added"] = name, ["kind"] = kind, ["material"] = material };
    }

    private static JsonNode Check(OfflineEtabs o)
    {
        var findings = ModelChecks.Run(o.Model);
        return new JsonObject
        {
            ["errors"] = findings.Count(f => f.Severity == Severity.Error), ["warnings"] = findings.Count(f => f.Severity == Severity.Warning),
            ["findings"] = new JsonArray(findings.Take(200).Select(f => (JsonNode)new JsonObject { ["severity"] = f.Severity.ToString().ToLowerInvariant(), ["check"] = f.Check, ["message"] = f.Message, ["object"] = f.Object, ["story"] = f.Story }).ToArray()),
        };
    }

    private static JsonNode Quantity(OfflineEtabs o) => new JsonObject
    {
        ["units"] = $"{o.Info().Force}, {o.Info().Length}",
        ["rows"] = new JsonArray(Quantities.Run(o.Model).Select(q => (JsonNode)new JsonObject
        {
            ["kind"] = q.Kind, ["section"] = q.Section, ["material"] = q.Material, ["count"] = q.Count,
            ["length"] = Args.Round(q.Length), ["area"] = Args.Round(q.Area), ["volume"] = q.Volume is { } v ? Args.Round(v) : null,
        }).ToArray()),
    };
}
