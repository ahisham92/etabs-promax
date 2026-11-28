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
              "program":{"type":"string","enum":["etabs","sap2000","safe"],"description":"The CSI program the model is for (default etabs): the Modeler then offers what that program has, and saving writes its file (.e2k, .s2k or .f2k) beside the Modeler's own file (.cpme, .cpms or .cpmf)."},
              "template":{"type":"string","enum":["empty","concrete_frame","blank","grid_only","beam","steel_deck","staggered_truss","flat_slab","flat_slab_perimeter_beams","waffle_slab","two_way_or_ribbed_slab"],"description":"empty or concrete_frame; or one of the program's New Model Quick Templates (blank ... two_way_or_ribbed_slab), built from x_grids, y_grids, story_data and 'quick'."},
              "x_grids":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"at":{"type":"number"},"visible":{"type":"boolean"},"bubble":{"type":"string"}}},"description":"Quick templates: the grid lines spaced along X (a line at x = at), in m."},
              "y_grids":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"at":{"type":"number"},"visible":{"type":"boolean"},"bubble":{"type":"string"}}}},
              "story_data":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"height":{"type":"number"}}},"description":"Quick templates: the stories from the lowest up, heights in m."},
              "quick":{"type":"object","description":"Quick templates: the template form's values in m and kN/m2: left_edge right_edge top_edge bottom_edge, slab_thickness column_size stiff_at_columns, drop_panels drop_size drop_thickness, beam_x_depth beam_x_width beam_y_depth beam_y_width, ribbed rib_depth rib_width_top rib_width_bottom rib_spacing rib_direction (X/Y), secondary_beams secondary_direction secondary_max_spacing secondary_number moment_frame (none/perimeter/intersecting), vierendeel_width panels_before panels_after first_bay_truss_at_top, dead_pattern dead_load live_pattern live_load, restraints (none/pinned/fixed), diaphragm (rigid/semi-rigid/none)."},
              "x_spans":{"type":"array","items":{"type":"number"},"description":"Bay sizes along X, in m."},
              "y_spans":{"type":"array","items":{"type":"number"}},
              "stories":{"type":"integer"},"story_height":{"type":"number"},
              "sdl":{"type":"number","description":"Superimposed dead load on the slabs, kN/m2 (default 2)."},
              "live":{"type":"number","description":"Live load on the slabs, kN/m2 (default 3)."}}}
            """, Ask.Always, NewModel, a => $"Start a new model '{a.Text("title")}' (replaces the one open in the Modeler)"),

        new("set_model_program",
            "Changes the CSI program the Modeler's model is for (etabs, sap2000 or safe): the Modeler then offers what that program has, and saving writes its file beside the Modeler's own file, which takes that program's extension (.cpme, .cpms or .cpmf). Nothing in the model is removed: what the new program has no place for stays in the Modeler's file and is said in the answer.",
            """{"type":"object","required":["program"],"properties":{"program":{"type":"string","enum":["etabs","sap2000","safe"]}}}""",
            Ask.Changes, SetProgram, a => $"Make the model for {StructuralModel.TargetName(a.Text("program"))}"),

        new("set_display_units",
            "Shows the Modeler's model in other units, as the CSI programs' units control at the bottom right does: every form, table, load and result then reads in these units, and the model is still saved in its own units. 'force' one of lb, kip, N, kN, kgf, tonf; 'length' one of in, ft, mm, cm, m; 'temperature' C or F. 'own' true: back to the model's own units.",
            """{"type":"object","properties":{"force":{"type":"string"},"length":{"type":"string"},"temperature":{"type":"string","enum":["C","F"]},"own":{"type":"boolean"}}}""",
            Ask.Never, SetDisplayUnits),

        new("open_model_file",
            "Opens a model file in the Modeler: an ETABS text file (.e2k, .$et), a SAP2000 text file (.s2k, .$2k), a SAFE text file (.f2k, .$fk) or a Modeler file (.cpme for ETABS, .cpms for SAP2000, .cpmf for SAFE, or an old .cpm). A .s2k or .f2k the Modeler saved opens exactly as it was saved; one SAP2000 or SAFE wrote is read from its tables (levels from the Z grid lines, members on the level of their top end), and what has no place in the Modeler yet comes back in not_read. Everything in an .e2k is kept, also what the Modeler does not show, and is written back on save. The model in the Modeler now is replaced. With 'for_program', a file made for another program is not opened: the answer says so (needs_choice), and the call is made again with 'convert' true (the model is made for for_program) or false (it stays for the file's program).",
            """{"type":"object","required":["path"],"properties":{"path":{"type":"string","description":"The file's full path."},"for_program":{"type":"string","enum":["etabs","sap2000","safe"],"description":"The program the Modeler's window works in. Leave it out to keep the file's own program."},"convert":{"type":"boolean","description":"With for_program: true makes a file of another program a model for for_program, false keeps the file's program."}}}""",
            Ask.Always, Open, a => $"Open {a.Text("path")} (replaces the model open in the Modeler)"),

        new("save_model_as",
            "Saves the Modeler's model under a name: an ETABS text file (.e2k) that licensed ETABS imports with File > Import > ETABS .e2k text file, and the Modeler's own file beside it with the extension of the model's program (.cpme, .cpms or .cpmf). Default folder: Documents\\CSI ProMax\\Models. The model is checked first and the findings come back with the answer.",
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
        var openings = SlabDefs.Openings(m).Count;
        var nullLines = SlabDefs.NullLines(m).Count;
        return new JsonObject
        {
            ["title"] = m.Title, ["program"] = StructuralModel.TargetName(m.Target), ["file"] = o.Path, ["stories"] = m.Stories.Count - 1, ["grids"] = m.Grids.Count,
            ["frames"] = m.LineAssigns.Count - nullLines, ["null_lines"] = nullLines, ["areas"] = m.AreaAssigns.Count - openings, ["openings"] = openings,
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
            var template = a.Text("template", "empty");
            if (QuickTemplates.Kinds.Contains(template)) model = QuickTemplates.Build(Quick(a, title, template));
            else if (template == "concrete_frame")
            {
                var xs = a.Items("x_spans").Select(v => v.GetDouble()).ToArray();
                var ys = a.Items("y_spans").Select(v => v.GetDouble()).ToArray();
                model = Templates.ConcreteFrame(title, xs, ys, a.Whole("stories", 1), a.Number("story_height", 3.2), a.Number("sdl", 2.0), a.Number("live", 3.0));
            }
            else model = ModelEdit.New(title);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException) { throw new ToolInputException(ex.Message); }
        model.Target = Target(a.Text("program", "etabs"));
        o.Replace(model, "");
        return Summary(o);
    }

    internal static QuickTemplate Quick(JsonElement a, string title, string kind)
    {
        // Edit > Add to Model from Template gives no grids or stories: it builds over the model's own.
        static List<QuickGrid> Grids(JsonElement a, string key) => !a.TryGetProperty(key, out _) ? [] : a.Items(key).Select(g => new QuickGrid(
            g.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "", g.GetProperty("at").GetDouble(),
            !g.TryGetProperty("visible", out var v) || v.ValueKind != JsonValueKind.False,
            g.TryGetProperty("bubble", out var b) ? b.GetString() : null)).ToList();
        var t = new QuickTemplate
        {
            Kind = kind, Title = title, XGrids = Grids(a, "x_grids"), YGrids = Grids(a, "y_grids"),
            Stories = !a.TryGetProperty("story_data", out _) ? [] : a.Items("story_data").Select(s => (s.GetProperty("name").GetString() ?? "", s.GetProperty("height").GetDouble())).ToList(),
        };
        if (!a.TryGetProperty("quick", out var q) || q.ValueKind != JsonValueKind.Object) return t;
        double N(string k, double d) => q.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        bool B(string k, bool d) => q.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
        string S(string k, string d) => q.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
        t.LeftEdge = N("left_edge", 0); t.RightEdge = N("right_edge", 0); t.TopEdge = N("top_edge", 0); t.BottomEdge = N("bottom_edge", 0);
        t.SlabThickness = N("slab_thickness", t.SlabThickness); t.ColumnSize = N("column_size", t.ColumnSize); t.StiffAtColumns = B("stiff_at_columns", false);
        t.DropPanels = B("drop_panels", false); t.DropSize = N("drop_size", t.DropSize); t.DropThickness = N("drop_thickness", t.DropThickness);
        t.BeamXDepth = N("beam_x_depth", t.BeamXDepth); t.BeamXWidth = N("beam_x_width", t.BeamXWidth); t.BeamYDepth = N("beam_y_depth", t.BeamYDepth); t.BeamYWidth = N("beam_y_width", t.BeamYWidth);
        t.Ribbed = B("ribbed", true); t.RibDepth = N("rib_depth", t.RibDepth); t.RibWidthTop = N("rib_width_top", t.RibWidthTop); t.RibWidthBottom = N("rib_width_bottom", t.RibWidthBottom);
        t.RibSpacing = N("rib_spacing", t.RibSpacing); t.RibDirection = S("rib_direction", "X");
        t.SecondaryBeams = B("secondary_beams", true); t.SecondaryDirection = S("secondary_direction", "X");
        t.SecondaryMaxSpacing = q.TryGetProperty("secondary_max_spacing", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() : null;
        t.SecondaryNumber = q.TryGetProperty("secondary_number", out var sn) && sn.ValueKind == JsonValueKind.Number ? sn.GetInt32() : null;
        if (t.SecondaryMaxSpacing is null && t.SecondaryNumber is null) t.SecondaryMaxSpacing = 3;
        t.MomentFrame = S("moment_frame", "intersecting");
        t.VierendeelWidth = N("vierendeel_width", t.VierendeelWidth); t.PanelsBefore = (int)N("panels_before", 3); t.PanelsAfter = (int)N("panels_after", 3);
        t.FirstBayTrussAtTop = B("first_bay_truss_at_top", true);
        t.DeadPattern = S("dead_pattern", "Dead"); t.DeadLoad = N("dead_load", 0); t.LivePattern = S("live_pattern", "Live"); t.LiveLoad = N("live_load", 0);
        t.Restraints = S("restraints", "pinned"); t.Diaphragm = S("diaphragm", "rigid");
        return t;
    }

    private static string Target(string program) => StructuralModel.Targets.Contains(program.ToLowerInvariant())
        ? program.ToLowerInvariant() : throw new ToolInputException($"'program' is one of {string.Join(", ", StructuralModel.Targets)}, not '{program}'.");

    private static JsonNode SetDisplayUnits(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        if (a.Flag("own", false)) o.ShowIn(null);
        else
        {
            var force = a.Text("force"); var length = a.Text("length");
            if (force.Length == 0 || length.Length == 0) throw new ToolInputException("Give 'force' and 'length' (or 'own': true).");
            if (ModelUnits.ForceKn(force) is null) throw new ToolInputException($"'force' is one of lb, kip, N, kN, kgf, tonf, not '{force}'.");
            if (ModelUnits.LengthM(length) is null) throw new ToolInputException($"'length' is one of in, ft, mm, cm, m, not '{length}'.");
            var t = a.Text("temperature");
            o.ShowIn(new CsiProMax.Model.Units { Force = force, Length = length, Temperature = t.Length > 0 ? t.ToUpperInvariant() : o.Model.Units.Temperature });
        }
        var own = o.SavedUnits ?? o.Model.Units;
        return new JsonObject
        {
            ["shown_in"] = $"{o.Model.Units.Force}, {o.Model.Units.Length}, {o.Model.Units.Temperature}",
            ["saved_in"] = $"{own.Force}, {own.Length}, {own.Temperature}",
        };
    }

    private static JsonNode SetProgram(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var target = Target(a.Need("program"));
        var was = o.Model.Target;
        o.Edit(m => m.Target = target);
        o.ChooseProgram = false;
        var kept = new List<string>();
        var x = o.Model.Extras;
        if (target != "sap2000" && x is not null && (x.Constraints.Count + x.Lanes.Count + x.Vehicles.Count + x.MovingCases.Count) > 0) kept.Add("joint constraints and moving loads (SAP2000 only)");
        if (target != "safe" && x is { Strips.Count: > 0 }) kept.Add("design strips (SAFE only)");
        return new JsonObject
        {
            ["program"] = StructuralModel.TargetName(target), ["was"] = StructuralModel.TargetName(was),
            ["kept_but_not_used"] = new JsonArray(kept.Select(k => (JsonNode)k).ToArray()),
            ["saves"] = target switch { "sap2000" => ".cpms, .e2k and .s2k", "safe" => ".cpmf, .e2k and .f2k", _ => ".cpme and .e2k" },
        };
    }

    private static JsonNode Open(JsonElement a, ICsiLink e)
    {
        var o = Offline(e);
        var path = a.Need("path").Trim().Trim('"');
        if (!File.Exists(path)) throw new ToolInputException($"There is no file at {path}.");
        OfflineEtabs.Opened opened;
        // ETABS's own model file: read through a hidden ETABS, with what its .e2k does not carry kept (ProgramModelOpen).
        if (ProgramModelOpen.Takes(path))
        {
            try { var r = ProgramModelOpen.Open(path); opened = new OfflineEtabs.Opened(r.Model, false, r.Notes); }
            catch (Exception ex) when (ex is EtabsException or FieldCheckException or IOException or UnauthorizedAccessException) { throw new ToolInputException($"The model could not be opened: {ex.Message}"); }
        }
        else
        try { opened = OfflineEtabs.Open(path); }
        catch (Exception ex) when (ex is E2k.E2kFormatException or JsonException or IOException or InvalidDataException) { throw new ToolInputException($"The file could not be read: {ex.Message}"); }
        // The window works in one program: a file made for another is converted only when the person says so, and
        // with no window asking (the chat, the CLI, the tests) the file keeps its own program.
        var mine = a.Text("for_program").ToLowerInvariant();
        var theirs = opened.Model.Target is { Length: > 0 } t ? t : "etabs";
        if (mine.Length > 0 && mine != theirs)
        {
            if (!a.TryGetProperty("convert", out var c) || c.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new JsonObject
                {
                    ["opened"] = false, ["needs_choice"] = true, ["path"] = path,
                    ["file_program"] = StructuralModel.TargetName(theirs), ["file_program_key"] = theirs,
                    ["window_program"] = StructuralModel.TargetName(Target(mine)), ["window_program_key"] = mine,
                };
        }
        o.Replace(opened.Model, OfflineEtabs.SavesTo(path, opened));
        // Converted: the model is now for the window's program, which its file does not say yet, so it is unsaved.
        if (mine.Length > 0 && mine != theirs && a.Flag("convert", false)) o.Edit(m => m.Target = Target(mine));
        var result = Summary(o).AsObject();
        if (o.Model.Target != theirs) result["converted_from"] = StructuralModel.TargetName(theirs);
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
            ["file"] = o.ProgramFile.Length > 0 ? o.ProgramFile : saved, ["e2k"] = saved, ["modeler_file"] = ModelFile.PathFor(saved, o.Model.Target),
            ["program"] = StructuralModel.TargetName(program),
            ["errors"] = findings.Count(f => f.Severity == Severity.Error), ["warnings"] = findings.Count(f => f.Severity == Severity.Warning),
            ["findings"] = new JsonArray(findings.Take(30).Select(f => (JsonNode)$"{f.Severity}: {f.Message}").ToArray()),
            ["next"] = program switch
            {
                "sap2000" => o.ProgramFile.Length > 0 ? "In licensed SAP2000: File > Import > SAP2000 .s2k Text File, then run the analysis there. The Modeler does not analyse." : "The .s2k could not be written; the .cpms and .e2k hold the model.",
                "safe" => o.ProgramFile.Length > 0 ? "In licensed SAFE: File > Import > SAFE .F2K file, then run the analysis and design there. The Modeler does not analyse." : "No floor here has slabs SAFE can take, so no .f2k was written; the .cpmf and .e2k hold the model.",
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
        o.Edit(m => m.AddConcrete(name, fc, modulus, a.Number("unit_weight", m.ConcreteUnitWeight())));
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
