using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Tools every CSI program has through the same API: the whole API (search, describe, call), the installed programs,
/// units, the properties and load definitions in full, table editing, the analysis's case status, unlocking, the
/// design codes, groups, what changed by hand, checkpoints of the model file, a view refresh and a building in one
/// call. Then the lateral tools ETABS and SAP2000 share (mass source, modal case, seismic and wind loads, response
/// spectrum, its scaling, diaphragms), ETABS's own (piers and spandrels, stories, slab and wall sections), and the
/// SAP2000 tools that work the same in the other programs (joints, frames by coordinates, materials, sections,
/// joint and member loads, releases, results through the API).
///
/// Filling these gaps was prompted by the tool list of etabs-mcp by Moha2005269 (Apache-2.0,
/// https://mcpservers.org/servers/moha2005269/etabs-mcp). Nothing of its code is used: these are written for CSI
/// ProMax's link and its three programs.
/// </summary>
public static class CsiTools
{
    /// <summary>The shared tools this program is given (the program's own tools of the same name take their place).</summary>
    public static IReadOnlyList<Tool> For(CsiProgram program) => program.Key switch
    {
        "sap2000" => SapList.Value,
        "safe" => SafeList.Value,
        _ => EtabsList.Value,
    };

    /// <summary>Every shared tool of every program, by name once.</summary>
    public static IEnumerable<Tool> Every => CsiProgram.All.SelectMany(For).DistinctBy(t => t.Name);

    /// <summary>SAP2000's tools that work the same in ETABS (its API has the same calls).</summary>
    internal static readonly string[] SharedWithEtabs =
        ["add_joints", "add_frames", "add_material", "add_frame_section", "set_joint_loads", "set_member_loads", "set_releases", "delete_joints", "joint_displacements", "frame_forces", "base_reactions", "modal_results"];

    /// <summary>The same for SAFE: no modal results (SAFE has no modal case in the scratch model).</summary>
    internal static readonly string[] SharedWithSafe =
        ["add_joints", "add_frames", "add_material", "add_frame_section", "set_joint_loads", "set_member_loads", "set_releases", "delete_joints", "joint_displacements", "frame_forces", "base_reactions"];

    private static readonly Lazy<IReadOnlyList<Tool>> EtabsList = new(() => [.. Common, .. Lateral, .. EtabsOwn, .. FromSap(CsiProgram.Etabs, SharedWithEtabs)]);
    private static readonly Lazy<IReadOnlyList<Tool>> SapList = new(() => [.. Common, .. Lateral]);
    private static readonly Lazy<IReadOnlyList<Tool>> SafeList = new(() => [.. Common, .. FromSap(CsiProgram.Safe, SharedWithSafe)]);

    /// <summary>A SAP2000 tool given to another program: its words name that program.</summary>
    private static IEnumerable<Tool> FromSap(CsiProgram program, string[] names) =>
        names.Select(n => Sap2000.SapTools.All.First(t => t.Name == n)).Select(t => t with
        {
            Description = t.Description.Replace("SAP2000", program.Name, StringComparison.Ordinal),
            Run = (a, e) =>
            {
                try { return Reword(t.Run(a, e), e.Program); }
                catch (EtabsException ex) { throw new EtabsException(ex.Message.Replace("SAP2000", e.Program.Name, StringComparison.Ordinal)); }
                catch (ToolInputException ex) { throw new ToolInputException(ex.Message.Replace("SAP2000", e.Program.Name, StringComparison.Ordinal)); }
            },
        });

    private static JsonNode Reword(JsonNode answer, CsiProgram program) =>
        program.Key == CsiProgram.Sap2000.Key ? answer : JsonNode.Parse(answer.ToJsonString().Replace("SAP2000", program.Name, StringComparison.Ordinal))!;

    // ======================================================================================================
    // Tools every program has
    // ======================================================================================================

    private static IReadOnlyList<Tool> Common =>
    [
        new("search_api",
            "Searches the whole API of the program the model is open in (every method reached from the model, about 1,300 of them) by words in the method's path, e.g. 'AutoSeismic', 'SetRestraint', 'Pier'. Gives each match's path and parameters. Use it for anything no other tool covers, then describe_api and call_api (reads) or the code tool (changes).",
            """{"type":"object","required":["query"],"properties":{"query":{"type":"string","description":"Words that must all be in the path, e.g. 'frame release' or 'GetNameList'."},"limit":{"type":"integer","description":"Default 40."}}}""",
            Ask.Never, SearchApi),

        new("describe_api",
            "One method of the program's API in full: each parameter's name, type, whether it is given or given back (ref), its default, and the values of an enum. Give the path as search_api gives it, e.g. FrameObj.SetReleases.",
            """{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}""",
            Ask.Never, DescribeApi),

        new("call_api",
            "Calls methods of the program's API that only read (Get..., Count..., GetNameList) and gives back the return code and every value given back by name. Arguments by parameter name ('args': {\"Name\":\"C1\"}), or in order ('values'). Several calls in one go with 'calls'. A method that changes the model is refused here: write it with the code tool, which the engineer sees first.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"e.g. FrameObj.GetSection"},
              "args":{"type":"object","description":"Parameter name to value. Left out: 0, false, \"\" or an empty list."},
              "values":{"type":"array","description":"The arguments in order, instead of args."},
              "calls":{"type":"array","items":{"type":"object","properties":{"path":{"type":"string"},"args":{"type":"object"},"values":{"type":"array"}}},"description":"Several calls, each like the above."}}}
            """, Ask.Never, CallApi),

        new("list_programs",
            "The CSI programs on this PC: ETABS, SAP2000 and SAFE, each with whether it is installed, its version, its API file and how many of its windows are open, and which one this window works in.", // ETABS itself
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => ListPrograms(e)),

        new("set_units",
            "Sets the units the program shows and the tools read and write in: kN_m_C, kN_mm_C, N_mm_C, kip_in_F, kip_ft_F, lb_in_F, kgf_m_C, Ton_m_C and the others of the program's list, or give force and length.",
            """{"type":"object","properties":{"units":{"type":"string","description":"e.g. kN_m_C"},"force":{"type":"string","enum":["lb","kip","N","kN","kgf","tonf"]},"length":{"type":"string","enum":["in","ft","mm","cm","m"]}}}""",
            Ask.Changes, SetUnits, a => $"Set the units to {(a.Text("units").Length > 0 ? a.Text("units") : a.Text("force") + ", " + a.Text("length"))}"),

        new("list_properties",
            "The materials, frame sections and area sections in full: each material's type, modulus, Poisson's ratio, unit weight and strength (fc or fy, fu); each frame section's shape, material and sizes; each area section's kind, material and thickness. Filter by kind or names.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["materials","frame_sections","area_sections","all"]},"names":{"type":"array","items":{"type":"string"}},"limit":{"type":"integer","description":"Per kind. Default 60."}}}""",
            Ask.Never, ListProperties),

        new("list_load_definitions",
            "The load definitions in full: every load pattern with its type and self weight multiplier, every load case with its type (static, modal, response spectrum, ...), every combination with its type and its cases and factors, and the mass source.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => LoadDefinitions(e)),

        new("edit_table",
            "The program's own editable tables (interactive database editing): the way to change anything the other tools do not, in bulk. action 'fields' gives a table's fields and units; 'read' its rows; 'add' adds rows (each row an object of field to value); 'update' changes the fields in 'set' on the rows whose fields equal 'match'; 'delete' removes the rows that match. The program checks the edit and its import log comes back. Find tables with list_tables.",
            """
            {"type":"object","required":["table","action"],"properties":{
              "table":{"type":"string"},"action":{"type":"string","enum":["fields","read","add","update","delete"]},
              "rows":{"type":"array","items":{"type":"object","additionalProperties":{"type":"string"}}},
              "match":{"type":"object","additionalProperties":{"type":"string"}},
              "set":{"type":"object","additionalProperties":{"type":"string"}},
              "limit":{"type":"integer"}}}
            """, Ask.Changes, EditTable, a => a.Text("action") is "fields" or "read" ? $"Read the table {a.Text("table")}" : $"Edit the table {a.Text("table")} ({a.Text("action")})"),

        new("analysis_status",
            "Which load cases are set to run and whether each has run (finished, not run, could not start, not finished), and whether the model is locked by its results.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => AnalysisStatus(e)),

        new("unlock_model",
            "Unlocks the analysed model so it can be changed. This deletes the analysis and design results (they come back when the analysis runs again). The engineer is always asked first.",
            """{"type":"object","properties":{}}""", Ask.Always, (_, e) => Unlock(e), _ => "Unlock the model (its results are deleted)"),

        new("design_codes",
            "The design code of each design the program has (steel frame, concrete frame, composite beam, shear wall, concrete slab), and sets one when 'set' gives it, by the code's name as the program lists it (e.g. \"ACI 318-19\", \"AISC 360-16\", \"Eurocode 2-2004\").",
            """{"type":"object","properties":{"set":{"type":"object","description":"Kind to code name, e.g. {\"concrete\":\"ACI 318-19\"}. Kinds: steel, concrete, composite, shear_wall, slab.","additionalProperties":{"type":"string"}}}}""",
            Ask.Changes, DesignCodes, a => a.TryGetProperty("set", out _) ? "Set the design codes" : "Read the design codes"),

        new("assign_group",
            "Puts frames, areas and points in a group (made when it is not there), or takes them out with remove. Groups select, design and report together.",
            """
            {"type":"object","required":["group"],"properties":{
              "group":{"type":"string"},"frames":{"type":"array","items":{"type":"string"}},"areas":{"type":"array","items":{"type":"string"}},
              "points":{"type":"array","items":{"type":"string"}},"remove":{"type":"boolean"}}}
            """, Ask.Changes, AssignGroup, a => $"{(a.Flag("remove") ? "Take objects out of" : "Put objects in")} group {a.Text("group")}"),

        new("what_changed",
            "What changed in the model since a starting point, also by hand in the program: objects added, removed, moved or given another section, and definitions added or removed. The first call (or reset) sets the starting point.",
            """{"type":"object","properties":{"reset":{"type":"boolean","description":"Start again from the model as it is now."}}}""",
            Ask.Never, WhatChanged),

        new("save_checkpoint",
            "Saves a copy of the model file as a checkpoint (in a folder beside the model), then saves the model under its own name again, so the work can go back to it with restore_checkpoint. The program has no undo for the API: take one before a big change. The engineer is always asked first (the model is saved).",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""",
            Ask.Always, SaveCheckpoint, a => $"Save the model and a checkpoint '{a.Text("name")}'"),

        new("list_checkpoints",
            "The checkpoints of this model, newest first, with when each was saved.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => ListCheckpoints(e)),

        new("restore_checkpoint",
            "Goes back to a checkpoint: opens the checkpoint's copy and saves it under the model's own name, so the model is as it was then. Everything since is lost. The engineer is always asked first.",
            """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""",
            Ask.Always, RestoreCheckpoint, a => $"Go back to checkpoint '{a.Text("name")}' (later changes are lost)"),

        new("refresh_view",
            "Redraws the program's views, after changes made through the API.",
            """{"type":"object","properties":{}}""", Ask.Never, (_, e) => { e.Refresh(); return new JsonObject { ["refreshed"] = true }; }),

        new("generate_building",
            "Builds a whole structure in one call on a new model: a grid of spans in X and Y, a number of stories, columns at every grid point, beams on every grid line (not for a flat slab), slabs in every bay, fixed supports, a rigid diaphragm per floor (ETABS, SAP2000), its concrete (or steel) material and sections, and superimposed dead and live loads on the slabs. In SAFE it is one floor: the slab, with point supports at the columns. The open model is replaced, so the engineer is always asked first.", // ETABS itself
            """
            {"type":"object","required":["spans_x","spans_y"],"properties":{
              "spans_x":{"type":"array","items":{"type":"number"},"description":"Bay widths in X, model length units."},
              "spans_y":{"type":"array","items":{"type":"number"}},
              "stories":{"type":"integer","description":"Default 3. SAFE: one."},
              "story_height":{"type":"number","description":"Default 3.2 m in model units."},
              "bottom_story_height":{"type":"number","description":"Default the story height."},
              "system":{"type":"string","enum":["concrete_frame","steel_frame","flat_slab"],"description":"Default concrete_frame."},
              "column":{"type":"array","items":{"type":"number"},"description":"[depth, width] (concrete) in model length units. Default 0.5 x 0.5 m."},
              "beam":{"type":"array","items":{"type":"number"},"description":"[depth, width]. Default 0.6 x 0.3 m."},
              "slab_thickness":{"type":"number","description":"Default 0.2 m."},
              "fc_mpa":{"type":"number","description":"Concrete strength in MPa. Default 30."},
              "fy_mpa":{"type":"number","description":"Steel yield in MPa (steel_frame). Default 345."},
              "steel_sizes":{"type":"object","description":"steel_frame: I sections as [depth, flange width, flange thickness, web thickness] in model length units.","properties":{"column":{"type":"array","items":{"type":"number"}},"beam":{"type":"array","items":{"type":"number"}}}},
              "sdl":{"type":"number","description":"Superimposed dead load on the slabs, force per area in model units. Default 1.5 kN/m2."},
              "live":{"type":"number","description":"Default 2.5 kN/m2."},
              "new_model":{"type":"boolean","description":"Default true. false builds into the open model, on its own stories or levels."}}}
            """, Ask.Always, GenerateBuilding, a => $"Replace the model with a new {Count(a, "spans_x")} x {Count(a, "spans_y")} bay building"),
    ];

    // ======================================================================================================
    // Lateral loads and dynamics: ETABS and SAP2000
    // ======================================================================================================

    private static IReadOnlyList<Tool> Lateral =>
    [
        new("set_mass_source",
            "Sets the mass source the modal and seismic analysis take their mass from: the elements' own mass, added masses, and load patterns with factors (e.g. Dead 1.0, SDL 1.0, Live 0.25). Made the default mass source.",
            """
            {"type":"object","properties":{
              "name":{"type":"string","description":"Default CPM-MassSource."},
              "from_elements":{"type":"boolean","description":"Default true."},"from_masses":{"type":"boolean","description":"Default true."},
              "loads":{"type":"array","items":{"type":"object","properties":{"pattern":{"type":"string"},"factor":{"type":"number"}}}}}}
            """, Ask.Changes, SetMassSource, a => "Set the mass source"),

        new("set_modal_case",
            "Sets a modal case (eigen or Ritz) with its number of modes: made when it is not there.",
            """{"type":"object","properties":{"name":{"type":"string","description":"Default Modal."},"type":{"type":"string","enum":["eigen","ritz"]},"max_modes":{"type":"integer","description":"Default 12."},"min_modes":{"type":"integer","description":"Default 1."}}}""",
            Ask.Changes, SetModalCase, a => $"Set the modal case {(a.Text("name").Length > 0 ? a.Text("name") : "Modal")}"),

        new("add_seismic_load",
            "Adds an equivalent lateral force (ELF) seismic load pattern the program computes itself: ASCE 7-16 (R, Omega, Cd, importance, Ss, S1, TL, site class, period) or a user coefficient (C, K), in X or Y with an accidental eccentricity.",
            """
            {"type":"object","required":["name","direction"],"properties":{
              "name":{"type":"string"},"direction":{"type":"string","enum":["x","y"]},
              "method":{"type":"string","enum":["asce7-16","user"],"description":"Default asce7-16."},
              "eccentricity":{"type":"number","description":"Ratio. Default 0.05."},
              "r":{"type":"number"},"omega":{"type":"number"},"cd":{"type":"number"},"importance":{"type":"number","description":"Default 1."},
              "ss":{"type":"number"},"s1":{"type":"number"},"tl":{"type":"number","description":"Default 8 s."},
              "site_class":{"type":"string","enum":["A","B","C","D","E"]},"fa":{"type":"number"},"fv":{"type":"number"},
              "period":{"type":"string","enum":["approximate","program","user"],"description":"Default program (calculated, capped by Cu Ta)."},
              "ct_type":{"type":"integer","description":"Approximate period: 0 all other, 1 steel MRF, 2 concrete MRF, 3 EBF. Default 2."},
              "user_period":{"type":"number"},
              "coefficient":{"type":"number","description":"user: C (base shear over weight)."},"k":{"type":"number","description":"user: height exponent. Default 1."},
              "dir_flag":{"type":"integer","description":"Only to override the program's own direction number."}}}
            """, Ask.Changes, AddSeismicLoad, a => $"Add the seismic load {a.Text("name")} in {a.Text("direction").ToUpperInvariant()}"),

        new("add_wind_load",
            "Adds an ASCE 7-16 wind load pattern the program computes itself: basic wind speed, exposure B, C or D, direction angle, windward and leeward coefficients, gust factor, Kzt and Kd.",
            """
            {"type":"object","required":["name","speed"],"properties":{
              "name":{"type":"string"},"speed":{"type":"number","description":"Basic wind speed in the unit the program asks for (mph in ASCE 7)."},
              "angle":{"type":"number","description":"Direction in degrees. Default 0 (X)."},
              "exposure":{"type":"string","enum":["B","C","D"],"description":"Default C."},
              "cpw":{"type":"number","description":"Default 0.8."},"cpl":{"type":"number","description":"Default 0.5."},
              "gust":{"type":"number","description":"Default 0.85."},"kzt":{"type":"number","description":"Default 1."},"kd":{"type":"number","description":"Default 0.85."}}}
            """, Ask.Changes, AddWindLoad, a => $"Add the wind load {a.Text("name")}"),

        new("add_response_spectrum",
            "Adds an ASCE 7-16 response spectrum: the function (Sa from Ss, S1, TL and the site class, as points) and a response spectrum case in X, Y or both, scaled by g times Ie over R, on a modal case, with CQC and the damping given.",
            """
            {"type":"object","required":["ss","s1","site_class","r"],"properties":{
              "function":{"type":"string","description":"Default CPM-ASCE716."},"case":{"type":"string","description":"Default CPM-RS."},
              "direction":{"type":"string","enum":["x","y","both"],"description":"Default both."},
              "ss":{"type":"number"},"s1":{"type":"number"},"tl":{"type":"number","description":"Default 8 s."},
              "site_class":{"type":"string","enum":["A","B","C","D","E"]},"fa":{"type":"number"},"fv":{"type":"number"},
              "r":{"type":"number"},"importance":{"type":"number","description":"Default 1."},
              "damping":{"type":"number","description":"Default 0.05."},"modal_case":{"type":"string","description":"Default the first modal case."}}}
            """, Ask.Changes, AddResponseSpectrum, a => $"Add the response spectrum {(a.Text("case").Length > 0 ? a.Text("case") : "CPM-RS")}"),

        new("scale_response_spectrum",
            "Compares the response spectrum base shear with the equivalent lateral force base shear in a direction (ASCE 7-16 12.9.1.4: at least 100% of the ELF), from the program's analysis, and with apply scales the response spectrum case up by the ratio. Needs a finished analysis of both cases. Scaling changes the model, so the results go and the analysis must run again.",
            """
            {"type":"object","required":["rs_case","elf_case","direction"],"properties":{
              "rs_case":{"type":"string"},"elf_case":{"type":"string"},"direction":{"type":"string","enum":["x","y"]},
              "target":{"type":"number","description":"Share of the ELF shear the RS must reach. Default 1.0."},
              "apply":{"type":"boolean","description":"Scale the case. Default false: only compare."}}}
            """, Ask.Changes, ScaleResponseSpectrum, a => a.Flag("apply") ? $"Scale {a.Text("rs_case")} to the ELF base shear" : $"Compare {a.Text("rs_case")} with {a.Text("elf_case")}"),

        new("assign_diaphragm",
            "Makes a rigid (or semi-rigid) diaphragm and gives it to the floors: in ETABS the floor areas of a story (or every floor); in SAP2000 the joints of a level z, as a diaphragm constraint.", // ETABS itself
            """
            {"type":"object","properties":{
              "name":{"type":"string","description":"Default D1."},"semi_rigid":{"type":"boolean"},
              "story":{"type":"string","description":"With stories: the story whose floors take it."},
              "all_floors":{"type":"boolean","description":"Every floor, or in SAP2000 every level above the lowest, each its own diaphragm."},
              "areas":{"type":"array","items":{"type":"string"}},
              "z":{"type":"number","description":"SAP2000: the level whose joints take it."}}}
            """, Ask.Changes, AssignDiaphragm, a => $"Give floors the diaphragm {(a.Text("name").Length > 0 ? a.Text("name") : "D1")}"),
    ];

    // ======================================================================================================
    // ETABS's own
    // ======================================================================================================

    private static IReadOnlyList<Tool> EtabsOwn =>
    [
        new("assign_pier_spandrel",
            "Labels walls (areas) and frames as a shear wall pier or spandrel (the label is made when it is not there), so the shear wall design and its results have them.",
            """
            {"type":"object","required":["kind","label"],"properties":{
              "kind":{"type":"string","enum":["pier","spandrel"]},"label":{"type":"string"},
              "areas":{"type":"array","items":{"type":"string"}},"frames":{"type":"array","items":{"type":"string"}}}}
            """, Ask.Changes, AssignPierSpandrel, a => $"Label {Count(a, "areas") + Count(a, "frames")} objects {a.Text("kind")} {a.Text("label")}"),

        new("set_stories",
            "Sets the model's stories from the base up: each story's name and height above the one below, and the base elevation. The program may refuse once objects stand on the stories: then add or change stories in its Edit > Edit Stories and Grids.",
            """
            {"type":"object","required":["stories"],"properties":{
              "base_elevation":{"type":"number","description":"Default 0."},
              "stories":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"height":{"type":"number"}}},"description":"From the lowest up."}}}
            """, Ask.Changes, SetStories, a => $"Set {Count(a, "stories")} stories"),

        new("add_area_section",
            "Adds a slab or wall section: its kind, material and thickness in the model's length unit (a thin shell).",
            """
            {"type":"object","required":["name","kind","material","thickness"],"properties":{
              "name":{"type":"string"},"kind":{"type":"string","enum":["slab","wall"]},"material":{"type":"string"},"thickness":{"type":"number"}}}
            """, Ask.Changes, AddAreaSection, a => $"Add the {a.Text("kind")} section {a.Text("name")}"),
    ];

    // ======================================================================================================
    // Helpers
    // ======================================================================================================

    private static int Count(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    internal static Dictionary<string, object?> D(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>One API call through the link, by path and named arguments; throws when the program says no.</summary>
    internal static JsonObject Call(ICsiLink e, string what, string path, Dictionary<string, object?>? args = null) =>
        (JsonObject)e.Raw(what, m => ApiCall.Must((object)m, path, args, what))!;

    /// <summary>The same, and null when this program or version has no such call or refuses it.</summary>
    internal static JsonObject? Try(ICsiLink e, string what, string path, Dictionary<string, object?>? args = null)
    {
        try { return Call(e, what, path, args); }
        catch (Exception ex) when (ex is MissingMethodException or ToolInputException or EtabsException or MissingMemberException) { return null; }
    }

    internal static List<string> Names(JsonObject? call, string name) =>
        call is null ? [] : ApiCall.Value(call, name) is JsonArray a ? a.Select(n => n?.ToString() ?? "").ToList() : [];

    private static JsonArray Arr(IEnumerable<string> items) => new(items.Select(s => (JsonNode)s).ToArray());

    private static double R(double v, int digits = 6) => double.IsFinite(v) ? Math.Round(v, digits) : 0;

    private static bool IsEtabs(ICsiLink e) => e.Program.Key == CsiProgram.Etabs.Key;
    private static bool IsSap(ICsiLink e) => e.Program.Key == CsiProgram.Sap2000.Key;

    private static Dictionary<string, object?> Args(JsonElement a, string name)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject()) result[p.Name] = p.Value.Clone();
        return result;
    }

    // ---- the whole API ----

    private static JsonNode SearchApi(JsonElement a, ICsiLink e)
    {
        var words = a.Need("query").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var limit = Math.Clamp(a.Whole("limit", 40), 1, 200);
        var index = ApiCall.Index(ApiCall.ModelType(e));
        var found = index.Where(m => words.All(w => m.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Path.Count(c => c == '.')).ThenBy(m => m.Path.Length).ToList();
        return new JsonObject
        {
            ["program"] = e.Program.Name, ["methods_in_api"] = index.Count, ["found"] = found.Count,
            ["methods"] = new JsonArray(found.Take(limit).Select(m => (JsonNode)new JsonObject { ["path"] = m.Path, ["signature"] = m.Signature, ["reads_only"] = ApiCall.Reads(m.Method.Name) }).ToArray()),
            ["note"] = found.Count == 0 ? "Nothing matched: try fewer or shorter words (Frame, Release, Seismic)." : found.Count > limit ? $"{found.Count - limit} more: narrow the words." : null,
        };
    }

    private static JsonNode DescribeApi(JsonElement a, ICsiLink e)
    {
        var path = a.Need("path").Trim();
        var index = ApiCall.Index(ApiCall.ModelType(e));
        var matches = index.Where(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            var close = index.Where(m => m.Path.EndsWith("." + path.Split('.').Last(), StringComparison.OrdinalIgnoreCase) || m.Path.Equals(path.Split('.').Last(), StringComparison.OrdinalIgnoreCase)).Take(10).Select(m => m.Path).ToList();
            throw new ToolInputException($"The API has no {path}." + (close.Count > 0 ? " Did you mean: " + string.Join(", ", close) + "?" : " Find it with search_api."));
        }
        return new JsonObject { ["program"] = e.Program.Name, ["overloads"] = new JsonArray(matches.Select(m => (JsonNode)ApiCall.Describe(m.Method, m.Path)).ToArray()) };
    }

    private static JsonNode CallApi(JsonElement a, ICsiLink e)
    {
        var calls = new List<JsonElement>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("calls", out var many) && many.ValueKind == JsonValueKind.Array) calls.AddRange(many.EnumerateArray());
        else calls.Add(a);
        if (calls.Count == 0) throw new ToolInputException("Give 'path', or 'calls'.");
        if (calls.Count > 50) throw new ToolInputException("At most 50 calls in one go.");
        var results = new JsonArray();
        foreach (var c in calls)
        {
            var path = c.Need("path");
            if (!ApiCall.Reads(path))
                throw new ToolInputException($"{path} changes the model, and call_api only reads. Write the change with the code tool (the engineer sees it first), or use the tool made for it.");
            var named = Args(c, "args");
            List<object?>? positional = null;
            if (c.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array) positional = vals.EnumerateArray().Select(v => (object?)v.Clone()).ToList();
            try
            {
                results.Add(e.Raw("call " + path, m => ApiCall.Invoke((object)m, path, named.Count > 0 ? named : null, positional)) as JsonNode);
            }
            catch (MissingMethodException ex) { results.Add(new JsonObject { ["method"] = path, ["error"] = ex.Message }); }
        }
        return calls.Count == 1 && !a.TryGetProperty("calls", out _) ? results[0]!.DeepClone() : new JsonObject { ["calls"] = results.Count, ["results"] = results };
    }

    // ---- this PC ----

    private static JsonNode ListPrograms(ICsiLink e)
    {
        var list = new JsonArray();
        foreach (var program in CsiProgram.All)
        {
            var api = "";
            try { api = EtabsApi.FindApiFile(program); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            var installed = api.Length > 0 && File.Exists(api);
            var version = "";
            if (installed)
                try
                {
                    var exe = Path.Combine(Path.GetDirectoryName(api)!, program.ProcessName + ".exe");
                    version = FileVersionInfo.GetVersionInfo(File.Exists(exe) ? exe : api).FileVersion ?? "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            var open = 0;
            try { open = Process.GetProcessesByName(program.ProcessName).Length; } catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { }
            list.Add(new JsonObject
            {
                ["program"] = program.Name, ["installed"] = installed, ["version"] = version, ["api_file"] = installed ? api : "",
                ["windows_open"] = open, ["this_window"] = program.Key == e.Program.Key,
            });
        }
        return new JsonObject { ["programs"] = list, ["this_window_works_in"] = e.Program.Name };
    }

    // ---- units ----

    private static JsonNode SetUnits(JsonElement a, ICsiLink e)
    {
        var name = a.Text("units");
        int code;
        if (name.Length > 0)
        {
            var i = Array.FindIndex(ComEtabs.UnitNames, u => u.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new ToolInputException($"'{name}' is not one of the program's unit sets: {string.Join(", ", ComEtabs.UnitNames)}.");
            code = i + 1;
        }
        else code = Units.Code(a.Need("force"), a.Need("length"));
        var before = e.Info();
        Call(e, "set the units", "SetPresentUnits", D(("Units", code)));
        var after = e.Info();
        return new JsonObject
        {
            ["before"] = $"{before.Force}, {before.Length}, {before.Temperature}", ["now"] = $"{after.Force}, {after.Length}, {after.Temperature}",
            ["units"] = ComEtabs.UnitNames[code - 1],
        };
    }

    // ---- properties and load definitions ----

    private static JsonNode ListProperties(JsonElement a, ICsiLink e)
    {
        var kind = a.Text("kind", "all").ToLowerInvariant();
        var names = a.Texts("names").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var limit = Math.Clamp(a.Whole("limit", 60), 1, 500);
        bool Keep(string n) => names.Count == 0 || names.Contains(n);
        var result = new JsonObject { ["units"] = $"{e.Info().Force}, {e.Info().Length}" };
        if (kind is "all" or "materials")
        {
            var list = new JsonArray();
            foreach (var name in e.Materials().Where(Keep).Take(limit))
            {
                var o = new JsonObject { ["name"] = name };
                var args = D(("Name", name));
                if (Try(e, "read a material", "PropMaterial.GetMaterial", args) is { } mat) o["type"] = ApiCall.Value(mat, "MatType")?.DeepClone();
                if (Try(e, "read a material's stiffness", "PropMaterial.GetMPIsotropic", args) is { } iso) { o["e"] = R(ApiCall.Number(iso, "E")); o["poisson"] = R(ApiCall.Number(iso, "U")); }
                if (Try(e, "read a material's weight", "PropMaterial.GetWeightAndMass", args) is { } w) o["unit_weight"] = R(ApiCall.Number(w, "W"));
                var type = o["type"]?.ToString() ?? "";
                if (type.Contains("Concrete", StringComparison.OrdinalIgnoreCase) && Try(e, "read the concrete's strength", "PropMaterial.GetOConcrete_1", args) is { } c) o["fc"] = R(ApiCall.Number(c, "Fc"));
                else if (type.Contains("Steel", StringComparison.OrdinalIgnoreCase) && Try(e, "read the steel's strength", "PropMaterial.GetOSteel_1", args) is { } s) { o["fy"] = R(ApiCall.Number(s, "Fy")); o["fu"] = R(ApiCall.Number(s, "Fu")); }
                else if (type.Contains("Rebar", StringComparison.OrdinalIgnoreCase) && Try(e, "read the rebar's strength", "PropMaterial.GetORebar_1", args) is { } r) { o["fy"] = R(ApiCall.Number(r, "Fy")); o["fu"] = R(ApiCall.Number(r, "Fu")); }
                list.Add(o);
            }
            result["materials"] = list;
        }
        if (kind is "all" or "frame_sections")
        {
            var list = new JsonArray();
            foreach (var name in e.FrameSections().Where(Keep).Take(limit))
            {
                var o = new JsonObject { ["name"] = name };
                var shape = Try(e, "read a section's shape", "PropFrame.GetTypeOAPI", D(("Name", name))) is { } t ? ApiCall.Value(t, "PropType")?.ToString() ?? "" : "";
                o["shape"] = shape;
                var getter = ShapeGetter(shape);
                if (getter is not null && Try(e, "read a section's sizes", "PropFrame." + getter, D(("Name", name))) is { } sizes && sizes["values"] is JsonObject v)
                    foreach (var (k, value) in v)
                        if (k is not ("Color" or "Notes" or "GUID" or "FileName") && value is not null) o[Snake(k)] = value.DeepClone();
                list.Add(o);
            }
            result["frame_sections"] = list;
        }
        if (kind is "all" or "area_sections")
        {
            var list = new JsonArray();
            foreach (var name in e.AreaSections().Where(Keep).Take(limit))
            {
                var o = new JsonObject { ["name"] = name };
                foreach (var (getter, label) in new[] { ("GetSlab", "slab"), ("GetWall", "wall"), ("GetShell_1", "shell"), ("GetDeck", "deck") })
                    if (Try(e, "read an area section", "PropArea." + getter, D(("Name", name))) is { } got && got["values"] is JsonObject v)
                    {
                        o["kind"] = label;
                        foreach (var (k, value) in v)
                            if (k is not ("Color" or "Notes" or "GUID") && value is not null) o[Snake(k)] = value.DeepClone();
                        break;
                    }
                list.Add(o);
            }
            result["area_sections"] = list;
        }
        return result;
    }

    internal static string? ShapeGetter(string shape)
    {
        var s = shape.Replace("_", "").ToLowerInvariant();
        if (s.Contains("rect")) return "GetRectangle";
        if (s.Contains("circ")) return "GetCircle";
        if (s.Contains("pipe")) return "GetPipe";
        if (s.Contains("box") || s.Contains("tube")) return "GetTube";
        if (s.Contains("dblchannel") || s.Contains("dbchannel")) return "GetDblChannel";
        if (s.Contains("channel")) return "GetChannel";
        if (s.Contains("dblangle")) return "GetDblAngle";
        if (s.Contains("angle")) return "GetAngle";
        if (s is "i" or "isection" || s.StartsWith("i", StringComparison.Ordinal) && s.Length <= 2) return "GetISection";
        if (s is "t" or "tee") return "GetTee";
        return null;
    }

    private static string Snake(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && !char.IsUpper(name[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    private static JsonNode LoadDefinitions(ICsiLink e)
    {
        var patterns = new JsonArray(e.LoadPatterns().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["self_weight"] = p.SelfWeight }).ToArray());
        var cases = new JsonArray();
        foreach (var name in e.LoadCases())
        {
            var o = new JsonObject { ["name"] = name };
            if (Try(e, "read a load case's type", "LoadCases.GetTypeOAPI", D(("Name", name))) is { } t)
            {
                o["type"] = ApiCall.Value(t, "CaseType")?.DeepClone();
                if (ApiCall.Value(t, "SubType") is { } sub) o["sub_type"] = sub.DeepClone();
            }
            cases.Add(o);
        }
        var combos = new JsonArray();
        foreach (var name in e.Combos())
        {
            var o = new JsonObject { ["name"] = name };
            if (Try(e, "read a combination's type", "RespCombo.GetTypeOAPI", D(("Name", name))) is { } t)
                o["type"] = ApiCall.Value(t, "ComboType") is { } ct ? ComboType(ct.ToString()) : null;
            if (Try(e, "read a combination's cases", "RespCombo.GetCaseList", D(("Name", name))) is { } list)
            {
                var items = Names(list, "CName");
                var factors = ApiCall.Value(list, "SF") as JsonArray;
                o["cases"] = new JsonArray(items.Select((c, i) => (JsonNode)new JsonObject { ["case"] = c, ["factor"] = factors is not null && i < factors.Count ? factors[i]?.DeepClone() : null }).ToArray());
            }
            combos.Add(o);
        }
        var result = new JsonObject { ["load_patterns"] = patterns, ["load_cases"] = cases, ["combinations"] = combos };
        if (Try(e, "list the mass sources", "SourceMass.GetNameList") is { } ms) result["mass_sources"] = Arr(Names(ms, "MyName").Concat(Names(ms, "Name")).Concat(Names(ms, "MassSourceName")).Distinct());
        return result;
    }

    private static string ComboType(string value) => value switch
    {
        "0" => "linear add", "1" => "envelope", "2" => "absolute add", "3" => "SRSS", "4" => "range add", _ => value,
    };

    // ---- tables ----

    private static TableName FindTable(ICsiLink e, string name)
    {
        var tables = e.Tables();
        return tables.FirstOrDefault(t => t.Key.Equals(name, StringComparison.OrdinalIgnoreCase) || t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? tables.FirstOrDefault(t => ResultTools.Norm(t.Name) == ResultTools.Norm(name))
            ?? throw new ToolInputException($"{e.Program.Name} has no table '{name}'. Find its name with list_tables.");
    }

    private static IEnumerable<(string Key, string Value)> Strings(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in o.EnumerateObject()) yield return (p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
    }

    private static JsonNode EditTable(JsonElement a, ICsiLink e)
    {
        var table = FindTable(e, a.Need("table"));
        var action = a.Need("action").ToLowerInvariant();
        var program = e.Program.Name;
        if (action == "fields")
            return (JsonNode)e.Raw("read a table's fields", m =>
            {
                int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                Ok(program, m.DatabaseTables.GetAllFieldsInTable(table.Key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable), $"read the fields of {table.Name}");
                keys ??= []; names ??= []; about ??= []; units ??= []; importable ??= [];
                return new JsonObject
                {
                    ["table"] = table.Name, ["key"] = table.Key,
                    ["fields"] = new JsonArray(Enumerable.Range(0, Math.Min(n, keys.Length)).Select(i => (JsonNode)new JsonObject { ["key"] = keys[i], ["name"] = i < names.Length ? names[i] : keys[i], ["units"] = i < units.Length ? units[i] : "", ["about"] = i < about.Length ? about[i] : "", ["editable"] = i < importable.Length && importable[i] }).ToArray()),
                };
            })!;

        string? readNote = null;
        var (fields, rows) = ((string[], List<string[]>))e.Raw("read a table for editing", m =>
        {
            int version = 0, records = 0; string[] f = [], data = [];
            CsiProMax.Assistant.Etabs.EditableTables.ShowEveryPattern(m);
            int got = Convert.ToInt32(m.DatabaseTables.GetTableForEditingArray(table.Key, "", ref version, ref f, ref records, ref data));
            Ok(program, got, $"open {table.Name} for editing (is it an editable table?)");
            f ??= []; data ??= [];
            var list = new List<string[]>();
            for (var r = 0; r < records && f.Length > 0; r++) list.Add(data.Skip(r * f.Length).Take(f.Length).Select(v => v ?? "").ToArray());
            if (list.Count == 0 && f.Length > 0)
            {
                // SAP2000 26 gives "Load Pattern Definitions" for editing with no rows while the model has its patterns
                // (0.4.17 and 0.4.20 field tests): the rows are read for display (all fields, by group "" then "All"),
                // then as the editing CSV text, and mapped onto the editing fields by name.
                var tried = new List<string> { $"editing array: answer {got}, 0 rows" };
                foreach (var group in new[] { "", "All" })
                {
                    if (list.Count > 0) break;
                    try
                    {
                        string[] ask = [], shown = [], rowsShown = []; int v2 = 0, n2 = 0;
                        int answer = Convert.ToInt32(m.DatabaseTables.GetTableForDisplayArray(table.Key, ref ask, group, ref v2, ref shown, ref n2, ref rowsShown));
                        tried.Add($"display, group '{group}': answer {answer}, {n2} rows");
                        if (answer == 0 && shown is { Length: > 0 } && rowsShown is not null)
                            for (var r = 0; r < n2; r++)
                            {
                                var source = rowsShown.Skip(r * shown.Length).Take(shown.Length).ToArray();
                                list.Add(f.Select(k => Array.FindIndex(shown, x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 && i < source.Length ? source[i] ?? "" : "").ToArray());
                            }
                    }
                    catch (Exception ex) { tried.Add($"display, group '{group}': {ex.Message}"); }
                }
                if (list.Count == 0)
                {
                    try
                    {
                        int v3 = 0; string csv = "";
                        int answer = Convert.ToInt32(m.DatabaseTables.GetTableForEditingCSVString(table.Key, "", ref v3, ref csv));
                        var lines = (csv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        tried.Add($"editing CSV: answer {answer}, {Math.Max(0, lines.Length - 1)} lines after the header");
                        if (answer == 0 && lines.Length > 1)
                        {
                            var head = lines[0].Split(',').Select(x => x.Trim().Trim('"')).ToArray();
                            foreach (var line in lines.Skip(1))
                            {
                                var cells = line.Split(',').Select(x => x.Trim().Trim('"')).ToArray();
                                list.Add(f.Select(k => Array.FindIndex(head, x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 && i < cells.Length ? cells[i] : "").ToArray());
                            }
                        }
                    }
                    catch (Exception ex) { tried.Add($"editing CSV: {ex.Message}"); }
                }
                readNote = string.Join("; ", tried);
            }
            return (f, list);
        })!;

        if (action == "read")
        {
            var limit = a.Whole("limit", 100);
            return new JsonObject
            {
                ["table"] = table.Name, ["fields"] = Arr(fields), ["rows_total"] = rows.Count, ["read_note"] = readNote,
                ["rows"] = new JsonArray(rows.Take(limit).Select(r => (JsonNode)Arr(r)).ToArray()),
            };
        }

        int Field(string name)
        {
            var i = Array.FindIndex(fields, f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = Array.FindIndex(fields, f => ResultTools.Norm(f) == ResultTools.Norm(name));
            return i >= 0 ? i : throw new ToolInputException($"{table.Name} has no field '{name}'. Its fields: {string.Join(", ", fields)}.");
        }

        List<(int Index, string Value)> Pairs(string name) =>
            Strings(a.TryGetProperty(name, out var p) ? p : default).Select(x => (Field(x.Key), x.Value)).ToList();

        var changed = 0;
        switch (action)
        {
            case "add":
                foreach (var item in a.Items("rows"))
                {
                    var row = Enumerable.Repeat("", fields.Length).ToArray();
                    foreach (var (k, v) in Strings(item)) row[Field(k)] = v;
                    rows.Add(row);
                    changed++;
                }
                break;
            case "update":
            {
                var match = Pairs("match");
                var set = Pairs("set");
                if (match.Count == 0 || set.Count == 0) throw new ToolInputException("'update' needs 'match' and 'set'.");
                foreach (var row in rows.Where(r => match.All(c => string.Equals(r[c.Index], c.Value, StringComparison.OrdinalIgnoreCase))))
                {
                    foreach (var (i, v) in set) row[i] = v;
                    changed++;
                }
                if (changed == 0) throw new ToolInputException($"No row of {table.Name} matches. Read it with action 'read' first.");
                break;
            }
            case "delete":
            {
                var match = Pairs("match");
                if (match.Count == 0) throw new ToolInputException("'delete' needs 'match'.");
                changed = rows.RemoveAll(r => match.All(c => string.Equals(r[c.Index], c.Value, StringComparison.OrdinalIgnoreCase)));
                if (changed == 0) throw new ToolInputException($"No row of {table.Name} matches. Read it with action 'read' first.");
                break;
            }
            default: throw new ToolInputException("'action' is fields, read, add, update or delete.");
        }

        var log = (JsonObject)e.Raw("apply the table edit", m =>
        {
            int version = 0; string[] f = fields.ToArray(); string[] data = rows.SelectMany(r => r).ToArray();
            Ok(program, m.DatabaseTables.SetTableForEditingArray(table.Key, ref version, ref f, rows.Count, ref data), $"take the edit of {table.Name}");
            int fatal = 0, errors = 0, warnings = 0, info = 0; string text = "";
            Ok(program, m.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref info, ref text), $"apply the edit of {table.Name}");
            return new JsonObject { ["fatal_errors"] = fatal, ["errors"] = errors, ["warnings"] = warnings, ["import_log"] = text ?? "" };
        })!;
        e.Refresh();
        var bad = log["fatal_errors"]!.GetValue<int>() + log["errors"]!.GetValue<int>();
        log["table"] = table.Name;
        log["rows_changed"] = changed;
        log["applied"] = bad == 0;
        if (bad > 0) log["note"] = $"{program} did not take the edit as it was: read its import log.";
        return log;
    }

    private static void Ok(string program, object result, string what)
    {
        if (Convert.ToInt32(result, CultureInfo.InvariantCulture) != 0) throw new EtabsException($"{program} refused to {what}.");
    }

    // ---- analysis ----

    private static JsonNode AnalysisStatus(ICsiLink e)
    {
        var info = e.Info();
        var result = new JsonObject { ["locked"] = info.Locked };
        if (Try(e, "read the analysis status", "Analyze.GetCaseStatus") is { } status)
        {
            var names = Names(status, "CaseName");
            var states = ApiCall.Value(status, "Status") as JsonArray;
            string State(int i) => (states is not null && i < states.Count ? states[i]?.ToString() : "") switch
            {
                "1" => "not run", "2" => "could not start", "3" => "not finished", "4" => "finished", var s => s ?? "",
            };
            result["cases"] = new JsonArray(names.Select((n, i) => (JsonNode)new JsonObject { ["case"] = n, ["status"] = State(i) }).ToArray());
            result["finished"] = names.Where((_, i) => State(i) == "finished").Count();
        }
        if (Try(e, "read which cases run", "Analyze.GetRunCaseFlag") is { } flags)
        {
            var names = Names(flags, "CaseName");
            var run = ApiCall.Value(flags, "Run") as JsonArray;
            result["set_to_run"] = Arr(names.Where((_, i) => run is not null && i < run.Count && run[i] is JsonValue rv && rv.TryGetValue<bool>(out var flag) && flag));
        }
        return result;
    }

    private static JsonNode Unlock(ICsiLink e)
    {
        var was = e.Info().Locked;
        if (was) Call(e, "unlock the model", "SetModelIsLocked", D(("LockIt", false)));
        var now = e.Info().Locked;
        if (now) throw new EtabsException($"{e.Program.Name} still says the model is locked.");
        return new JsonObject { ["was_locked"] = was, ["locked"] = now, ["note"] = was ? "The results were deleted: run the analysis again for new ones." : "The model was not locked." };
    }

    private static readonly (string Kind, string Api)[] DesignKinds =
        [("steel", "DesignSteel"), ("concrete", "DesignConcrete"), ("composite", "DesignCompositeBeam"), ("shear_wall", "DesignShearWall"), ("slab", "DesignConcreteSlab")];

    private static JsonNode DesignCodes(JsonElement a, ICsiLink e)
    {
        var set = Args(a, "set");
        var result = new JsonObject();
        var changes = new JsonArray();
        foreach (var (key, value) in set)
        {
            var api = DesignKinds.FirstOrDefault(k => k.Kind.Equals(key, StringComparison.OrdinalIgnoreCase)).Api
                ?? throw new ToolInputException($"'{key}' is not a design kind: {string.Join(", ", DesignKinds.Select(k => k.Kind))}.");
            var code = value is JsonElement je ? je.ToString() : value?.ToString() ?? "";
            Call(e, $"set the {key} design code to {code}", api + ".SetCode", D(("CodeName", code)));
            changes.Add($"{key}: {code}");
        }
        foreach (var (kind, api) in DesignKinds)
            result[kind] = Try(e, $"read the {kind} design code", api + ".GetCode") is { } got ? ApiCall.Value(got, "CodeName")?.DeepClone() : "not in this program";
        var answer = new JsonObject { ["codes"] = result };
        if (changes.Count > 0) answer["set"] = changes;
        return answer;
    }

    // ---- groups, piers, spandrels ----

    private static JsonNode AssignGroup(JsonElement a, ICsiLink e)
    {
        var group = a.Need("group");
        var remove = a.Flag("remove");
        if (!remove) Call(e, $"make the group {group}", "GroupDef.SetGroup", D(("Name", group)));
        var done = 0;
        var failed = new JsonArray();
        foreach (var (kind, api) in new[] { ("frames", "FrameObj"), ("areas", "AreaObj"), ("points", "PointObj") })
            foreach (var name in a.Texts(kind))
            {
                if (Try(e, $"put {name} in {group}", api + ".SetGroupAssign", D(("Name", name), ("GroupName", group), ("Remove", remove), ("ItemType", 0))) is not null) done++;
                else failed.Add($"{kind[..^1]} {name}");
            }
        if (done + failed.Count == 0) throw new ToolInputException("Give frames, areas or points.");
        var result = new JsonObject { ["group"] = group, ["done"] = done, ["failed"] = failed };
        if (Try(e, "read the group", "GroupDef.GetAssignments", D(("Name", group))) is { } back) result["in_group_now"] = Names(back, "ObjectName").Count;
        return result;
    }

    private static JsonNode AssignPierSpandrel(JsonElement a, ICsiLink e)
    {
        var kind = a.Need("kind").ToLowerInvariant();
        var label = a.Need("label");
        if (kind is not ("pier" or "spandrel")) throw new ToolInputException("kind is pier or spandrel.");
        var (labels, make, set, get, field) = kind == "pier"
            ? ("PierLabel", "SetPier", "SetPier", "GetPier", "PierName")
            : ("SpandrelLabel", "SetSpandrel", "SetSpandrel", "GetSpandrel", "SpandrelName");
        // Made when it is not there; a label that is already there is kept.
        Try(e, $"make the {kind} label {label}", $"{labels}.{make}", D(("Name", label), ("IsMultiStory", false)));
        var done = new JsonArray();
        var failed = new JsonArray();
        foreach (var (key, api) in new[] { ("areas", "AreaObj"), ("frames", "FrameObj") })
            foreach (var name in a.Texts(key))
            {
                var ok = Try(e, $"label {name}", $"{api}.{set}", D(("Name", name), (field, label), ("ItemType", 0))) is not null;
                var back = Try(e, $"read {name}'s label", $"{api}.{get}", D(("Name", name)));
                if (ok && back is not null && ApiCall.Value(back, field)?.ToString() == label) done.Add(name);
                else failed.Add(name);
            }
        if (done.Count + failed.Count == 0) throw new ToolInputException("Give areas or frames.");
        return new JsonObject { ["kind"] = kind, ["label"] = label, ["done"] = done.Count, ["labelled"] = done, ["failed"] = failed };
    }

    // ---- what changed ----

    internal static string BaselineFile(ICsiLink e)
    {
        var file = e.Info().File;
        var model = file.Length > 0 ? Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/').Last()) : "unsaved";
        var clean = new string(model.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return Path.Combine(ResultTools.SnapshotRoot, "what-changed", (clean.Length > 0 ? clean : "unsaved") + ".json");
    }

    private static JsonNode WhatChanged(JsonElement a, ICsiLink e)
    {
        var file = BaselineFile(e);
        if (a.Flag("reset") || !File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, ResultTools.Capture(e).ToJsonString());
            return new JsonObject { ["baseline"] = "set", ["note"] = "The starting point is the model as it is now. Ask again after changes (made here or by hand) to see them." };
        }
        var from = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var result = ResultTools.Difference(from, ResultTools.Capture(e));
        result["since"] = from["taken"]?.DeepClone();
        return result;
    }

    // ---- checkpoints ----

    private static (string Model, string Folder) CheckpointFolder(ICsiLink e)
    {
        var file = e.Info().File;
        if (file.Length == 0 || !Path.IsPathRooted(file)) throw new ToolInputException("The model has not been saved yet: save it first, then take a checkpoint.");
        var folder = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + " checkpoints");
        return (file, folder);
    }

    private static string CheckpointName(string name)
    {
        var clean = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? throw new ToolInputException("Give the checkpoint a name with letters or digits.") : clean.Length > 50 ? clean[..50] : clean;
    }

    private static JsonNode SaveCheckpoint(JsonElement a, ICsiLink e)
    {
        var (model, folder) = CheckpointFolder(e);
        var name = CheckpointName(a.Need("name"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, name + Path.GetExtension(model));
        Call(e, "save the checkpoint's copy", "File.Save", D(("FileName", copy)));
        // Back under its own name, so the work goes on in the model and not in the copy.
        Call(e, "save the model under its own name again", "File.Save", D(("FileName", model)));
        var now = e.Info().File;
        return new JsonObject
        {
            ["checkpoint"] = name, ["file"] = copy, ["saved"] = File.Exists(copy) || !OperatingSystem.IsWindows(), ["model"] = now,
            ["note"] = string.Equals(Path.GetFullPath(now), Path.GetFullPath(model), StringComparison.OrdinalIgnoreCase) ? null : $"{e.Program.Name} now has {now} open: open {model} again.",
        };
    }

    private static JsonNode ListCheckpoints(ICsiLink e)
    {
        var (model, folder) = CheckpointFolder(e);
        var files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("*" + Path.GetExtension(model)).OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];
        return new JsonObject
        {
            ["folder"] = folder, ["count"] = files.Count,
            ["checkpoints"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(f.Name), ["saved"] = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) }).ToArray()),
        };
    }

    private static JsonNode RestoreCheckpoint(JsonElement a, ICsiLink e)
    {
        var (model, folder) = CheckpointFolder(e);
        var name = CheckpointName(a.Need("name"));
        var copy = Path.Combine(folder, name + Path.GetExtension(model));
        if (!File.Exists(copy))
        {
            var have = ListCheckpoints(e)["checkpoints"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
            throw new ToolInputException($"There is no checkpoint '{name}'." + (have.Count > 0 ? " The checkpoints: " + string.Join(", ", have) + "." : " Take one with save_checkpoint first."));
        }
        Call(e, "open the checkpoint", "File.OpenFile", D(("FileName", copy)));
        Call(e, "save it under the model's own name", "File.Save", D(("FileName", model)));
        e.Refresh();
        return new JsonObject { ["restored"] = name, ["model"] = e.Info().File, ["frames"] = e.Frames().Count, ["areas"] = e.Areas().Count };
    }

    // ---- ETABS's stories and area sections ----

    private static JsonNode SetStories(JsonElement a, ICsiLink e)
    {
        var items = a.Items("stories").ToList();
        if (items.Count == 0) throw new ToolInputException("Give at least one story.");
        var names = items.Select((s, i) => s.Text("name") is { Length: > 0 } n ? n : $"Story{i + 1}").ToArray();
        var heights = items.Select(s => s.Number("height")).ToArray();
        if (heights.Any(h => h <= 0)) throw new ToolInputException("Every story needs a height above zero.");
        var n = items.Count;
        var args = D(("BaseElevation", a.Number("base_elevation", 0)), ("NumberStories", n), ("StoryNames", names), ("StoryHeights", heights),
            ("IsMasterStory", Enumerable.Repeat(true, n).ToArray()), ("SimilarToStory", Enumerable.Repeat("", n).ToArray()),
            ("SpliceAbove", new bool[n]), ("SpliceHeight", new double[n]), ("Color", new int[n]));
        e.Raw("set the stories", m => ApiCall.First((object)m, ["Story.SetStories_2", "Story.SetStories"], args, "set the stories"));
        e.Refresh();
        var back = e.Stories();
        return new JsonObject
        {
            ["stories"] = new JsonArray(back.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["elevation"] = R(s.Elevation, 4), ["height"] = R(s.Height, 4) }).ToArray()),
        };
    }

    private static JsonNode AddAreaSection(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        var kind = a.Need("kind").ToLowerInvariant();
        var material = a.Need("material");
        var thickness = a.Number("thickness");
        if (thickness <= 0) throw new ToolInputException("The thickness must be above zero.");
        if (!e.Materials().Contains(material, StringComparer.OrdinalIgnoreCase)) throw new ToolInputException($"There is no material '{material}'. The materials: {string.Join(", ", e.Materials())}.");
        if (kind == "slab") Call(e, $"add the slab section {name}", "PropArea.SetSlab", D(("Name", name), ("SlabType", 0), ("ShellType", 1), ("MatProp", material), ("Thickness", thickness), ("color", -1)));
        else if (kind == "wall") Call(e, $"add the wall section {name}", "PropArea.SetWall", D(("Name", name), ("WallPropType", 1), ("ShellType", 1), ("MatProp", material), ("Thickness", thickness), ("color", -1)));
        else throw new ToolInputException("kind is slab or wall.");
        if (!e.AreaSections().Contains(name, StringComparer.OrdinalIgnoreCase)) throw new EtabsException($"{e.Program.Name} answered, and has no area section {name} after it.");
        return new JsonObject { ["added"] = name, ["kind"] = kind, ["thickness"] = thickness, ["material"] = material };
    }

    // ======================================================================================================
    // Lateral
    // ======================================================================================================

    private static JsonNode SetMassSource(JsonElement a, ICsiLink e)
    {
        var name = a.Text("name", "CPM-MassSource");
        var loads = new List<(string Pattern, double Factor)>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("loads", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var item in l.EnumerateArray()) loads.Add((item.Need("pattern"), item.Number("factor", 1)));
        var patterns = e.LoadPatterns().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = loads.Where(x => !patterns.Contains(x.Pattern)).Select(x => x.Pattern).ToList();
        if (missing.Count > 0) throw new ToolInputException($"No load pattern named {string.Join(", ", missing)}. The patterns: {string.Join(", ", patterns)}.");
        if (IsEtabs(e))
        {
            // ETABS 23 has no SourceMass: its one (default) mass source is set on PropMaterial.
            var (elements, added, pats) = EtabsLateral.MassSource(e, a.Flag("from_elements", true), a.Flag("from_masses", true), loads);
            return new JsonObject
            {
                ["mass_source"] = "the model's default", ["loads"] = new JsonArray(loads.Select(x => (JsonNode)$"{x.Pattern} x {x.Factor.ToString(CultureInfo.InvariantCulture)}").ToArray()),
                ["from_elements"] = elements, ["from_masses"] = added, ["from_loads"] = Arr(pats), ["is_default"] = true,
                ["note"] = $"{e.Program.Name}'s API sets only the default mass source, so the name {name} is not used.",
            };
        }
        Call(e, "set the mass source", "SourceMass.SetMassSource", D(("Name", name), ("MassFromElements", a.Flag("from_elements", true)), ("MassFromMasses", a.Flag("from_masses", true)),
            ("MassFromLoads", loads.Count > 0), ("IsDefault", true), ("NumberLoads", loads.Count), ("LoadPat", loads.Select(x => x.Pattern).ToArray()), ("SF", loads.Select(x => x.Factor).ToArray())));
        var result = new JsonObject { ["mass_source"] = name, ["loads"] = new JsonArray(loads.Select(x => (JsonNode)$"{x.Pattern} x {x.Factor.ToString(CultureInfo.InvariantCulture)}").ToArray()) };
        if (Try(e, "read the mass source", "SourceMass.GetMassSource", D(("Name", name))) is { } back)
        {
            result["from_elements"] = ApiCall.Value(back, "MassFromElements")?.DeepClone();
            result["from_loads"] = Arr(Names(back, "LoadPat"));
            result["is_default"] = ApiCall.Value(back, "IsDefault")?.DeepClone();
        }
        return result;
    }

    private static JsonNode SetModalCase(JsonElement a, ICsiLink e)
    {
        var name = a.Text("name", "Modal");
        var api = a.Text("type", "eigen").ToLowerInvariant() == "ritz" ? "ModalRitz" : "ModalEigen";
        var max = Math.Max(1, a.Whole("max_modes", 12));
        var min = Math.Clamp(a.Whole("min_modes", 1), 1, max);
        Call(e, $"set the modal case {name}", $"LoadCases.{api}.SetCase", D(("Name", name)));
        Call(e, $"set the modes of {name}", $"LoadCases.{api}.SetNumberModes", D(("Name", name), ("MaxModes", max), ("MinModes", min)));
        var back = Try(e, $"read the modes of {name}", $"LoadCases.{api}.GetNumberModes", D(("Name", name)));
        return new JsonObject
        {
            ["case"] = name, ["type"] = api == "ModalRitz" ? "ritz" : "eigen",
            ["max_modes"] = back is null ? max : (int)ApiCall.Number(back, "MaxModes"), ["min_modes"] = back is null ? min : (int)ApiCall.Number(back, "MinModes"),
        };
    }

    /// <summary>The program's number for a direction: SAP2000 1 X, 2 Y; ETABS 1 X, 4 Y (2, 3, 5, 6 with the eccentricity).</summary>
    internal static int DirFlag(ICsiLink e, string direction) => direction.ToLowerInvariant() switch
    {
        "x" => 1,
        "y" => IsSap(e) ? 2 : 4,
        _ => throw new ToolInputException("direction is x or y."),
    };

    private static int SiteNumber(string site) => site.ToUpperInvariant() switch
    {
        "A" => 1, "B" => 2, "C" => 3, "D" => 4, "E" => 5, "F" => 6, _ => throw new ToolInputException("site_class is A, B, C, D or E."),
    };

    private static (string Top, string Bottom) StoryRange(ICsiLink e)
    {
        var stories = e.Stories();
        return stories.Count >= 2 ? (stories[^1].Name, stories[0].Name) : ("", "");
    }

    private static (double Top, double Bottom) ZRange(ICsiLink e)
    {
        var points = e.Points();
        return points.Count > 0 ? (points.Max(p => p.Z), points.Min(p => p.Z)) : (0, 0);
    }

    private static void EnsurePattern(ICsiLink e, string name, string type)
    {
        var have = e.LoadPatterns().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (have is null) e.AddLoadPattern(name, type, 0);
        else if (!have.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) throw new ToolInputException($"The load pattern {name} is there as {have.Type}: give another name for the {type.ToLowerInvariant()} load.");
    }

    private static JsonNode AddSeismicLoad(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        var direction = a.Need("direction");
        var dir = a.Whole("dir_flag", DirFlag(e, direction));
        var method = a.Text("method", "asce7-16").ToLowerInvariant();
        EnsurePattern(e, name, "Seismic");
        var (top, bottom) = StoryRange(e);
        var (topZ, bottomZ) = ZRange(e);
        var common = D(("Name", name), ("DirFlag", dir), ("Eccen", a.Number("eccentricity", 0.05)),
            ("OverStories", top.Length > 0), ("StoryTop", top), ("StoryBottom", bottom), ("UserZ", false), ("TopZ", topZ), ("BottomZ", bottomZ));
        JsonObject? back;
        if (method == "user")
        {
            common["C"] = a.Number("coefficient");
            common["K"] = a.Number("k", 1);
            if (IsEtabs(e))
            {
                var said = EtabsLateral.UserCoefficient(e, name, direction, a.Number("eccentricity", 0.05), a.Number("coefficient"), a.Number("k", 1));
                var made = new JsonObject { ["pattern"] = name, ["method"] = method, ["direction"] = direction.ToLowerInvariant(), ["lateral"] = said, ["code"] = EtabsLateral.SeismicCode(e, name) };
                if (EditableTables.Read(e, "Load Pattern Definitions - Auto Seismic - User Coefficient", "Name", name) is { } row)
                    made["read_back"] = new JsonObject(row.Where(kv => kv.Key is not ("Name" or "GUID")).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
                made["note"] = $"{e.Program.Name} computes the loads when the analysis runs. Check them in its Load Patterns > Modify Lateral Load.";
                return made;
            }
            Call(e, $"set {name} to a user coefficient", "LoadPatterns.AutoSeismic.SetUserCoefficient", common);
            back = Try(e, $"read {name}", "LoadPatterns.AutoSeismic.GetUserCoefficient", D(("Name", name)));
        }
        else if (method == "asce7-16")
        {
            var site = a.Need("site_class");
            var ss = a.Number("ss");
            var s1 = a.Number("s1");
            var period = a.Text("period", "program").ToLowerInvariant();
            common["PeriodFlag"] = period switch { "approximate" => 1, "user" => 3, _ => 2 };
            common["CtType"] = a.Whole("ct_type", 2);
            common["UserT"] = a.Number("user_period", 0);
            common["R"] = a.Number("r");
            common["Omega"] = a.Number("omega", 3);
            common["Cd"] = a.Number("cd", a.Number("r") * 0.9);
            common["I"] = a.Number("importance", 1);
            common["Ss"] = ss;
            common["S1"] = s1;
            common["TL"] = a.Number("tl", 8);
            common["SiteClass"] = SiteNumber(site);
            common["Fa"] = a.Number("fa", Spectrum.Fa(site, ss));
            common["Fv"] = a.Number("fv", Spectrum.Fv(site, s1));
            if (IsEtabs(e))
            {
                var took = EtabsLateral.Asce716(e, name, direction, a.Number("eccentricity", 0.05), (int)common["PeriodFlag"]!, (int)common["CtType"]!, (double)common["UserT"]!,
                    (double)common["R"]!, (double)common["Omega"]!, (double)common["Cd"]!, (double)common["I"]!, ss, s1, (double)common["TL"]!, (int)common["SiteClass"]!, (double)common["Fa"]!, (double)common["Fv"]!);
                var made = new JsonObject { ["pattern"] = name, ["method"] = method, ["direction"] = direction.ToLowerInvariant(), ["set_by"] = took, ["code"] = EtabsLateral.SeismicCode(e, name) };
                // Read back by ETABS's own getter, else from its ASCE 7-16 table.
                if ((Try(e, $"read {name}", "LoadPatterns.AutoSeismic.GetASCE716_1", D(("Name", name))) ?? Try(e, $"read {name}", "LoadPatterns.AutoSeismic.GetASCE716", D(("Name", name))))?["values"] is JsonObject got)
                    made["read_back"] = got.DeepClone();
                else if (EditableTables.Read(e, "Load Pattern Definitions - Auto Seismic - ASCE 7-16", "Name", name) is { } row)
                    made["read_back"] = new JsonObject(row.Where(kv => kv.Key is not ("Name" or "GUID")).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
                made["note"] = $"{e.Program.Name} computes the loads when the analysis runs. Check them in its Load Patterns > Modify Lateral Load.";
                return made;
            }
            // SAP2000 26's SetASCE716(_1) takes nDir, the directions X and Y, not a DirFlag (its help; 0.4.17 field test).
            var x = direction.Equals("x", StringComparison.OrdinalIgnoreCase);
            (string Path, bool[] Dirs)[] tries =
            [
                ("LoadPatterns.AutoSeismic.SetASCE716_1", [x, !x]), ("LoadPatterns.AutoSeismic.SetASCE716_1", [false, x, !x]),
                ("LoadPatterns.AutoSeismic.SetASCE716", [false, x, !x]), ("LoadPatterns.AutoSeismic.SetASCE716", [x, !x]),
            ];
            var set = false;
            for (var i = 0; i < tries.Length && !set; i++)
            {
                common["nDir"] = tries[i].Dirs;
                if (i < tries.Length - 1) set = Try(e, $"set {name} to ASCE 7-16", tries[i].Path, common) is not null;
                else { Call(e, $"set {name} to ASCE 7-16", tries[i].Path, common); set = true; }
            }
            back = Try(e, $"read {name}", "LoadPatterns.AutoSeismic.GetASCE716_1", D(("Name", name))) ?? Try(e, $"read {name}", "LoadPatterns.AutoSeismic.GetASCE716", D(("Name", name)));
        }
        else throw new ToolInputException("method is asce7-16 or user.");
        var result = new JsonObject { ["pattern"] = name, ["method"] = method, ["direction"] = direction.ToLowerInvariant(), ["dir_flag"] = dir };
        if (back?["values"] is JsonObject values) result["read_back"] = values.DeepClone();
        result["note"] = $"{e.Program.Name} computes the loads when the analysis runs. Check them in its Load Patterns > Modify Lateral Load.";
        return result;
    }

    private static JsonNode AddWindLoad(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        EnsurePattern(e, name, "Wind");
        var (top, bottom) = StoryRange(e);
        var (topZ, bottomZ) = ZRange(e);
        var exposure = a.Text("exposure", "C").ToUpperInvariant() switch { "B" => 1, "C" => 2, "D" => 3, _ => throw new ToolInputException("exposure is B, C or D.") };
        var args = D(("Name", name), ("ExposureFrom", 1), ("DirAngle", a.Number("angle", 0)), ("Cpw", a.Number("cpw", 0.8)), ("Cpl", a.Number("cpl", 0.5)),
            ("UserZ", false), ("TopZ", topZ), ("BottomZ", bottomZ), ("OverStories", top.Length > 0), ("StoryTop", top), ("StoryBottom", bottom),
            ("WindSpeed", a.Number("speed")), ("ExposureType", exposure), ("Kzt", a.Number("kzt", 1)), ("GustFactor", a.Number("gust", 0.85)),
            ("Kd", a.Number("kd", 0.85)), ("SolidGrossRatio", 0.2), ("UserExposure", false), ("Ke", 1.0),
            // SAP2000 26's help: ASCECase 1 to 5 (5 is every case of ASCE 7-16 Fig. 27.3-8), e1 and e2 of that figure; ASCE 7-10 also takes the importance.
            ("ASCECase", a.Whole("asce_case", 1)), ("ASCEe1", 0.15), ("ASCEe2", 0.15), ("ImportanceFactor", a.Number("importance", 1)));
        if (IsEtabs(e))
        {
            // ETABS 23's cAutoWind has no methods: its table takes the wind.
            EtabsLateral.Wind716(e, name, a.Number("angle", 0), a.Number("speed"), a.Text("exposure", "C").ToUpperInvariant(), a.Number("kzt", 1), a.Number("gust", 0.85), a.Number("kd", 0.85));
            return new JsonObject { ["pattern"] = name, ["code"] = "ASCE 7-16", ["set_by"] = "the table Auto Wind - ASCE 7-16",
                ["note"] = $"{e.Program.Name} computes the wind loads when the analysis runs, from the extents of the diaphragms." };
        }
        JsonObject used;
        try { used = (JsonObject)e.Raw($"set {name} to ASCE 7-16 wind", m => ApiCall.First((object)m, ["LoadPatterns.AutoWind.SetASCE716", "LoadPatterns.AutoWind.SetASCE710"], args, $"set {name} to ASCE 7 wind"))!; }
        catch (EtabsException)
        {
            // Exposure 1 needs rigid diaphragms; 2 takes it from the area objects.
            args["ExposureFrom"] = 2;
            used = (JsonObject)e.Raw($"set {name} to ASCE 7-16 wind", m => ApiCall.First((object)m, ["LoadPatterns.AutoWind.SetASCE716", "LoadPatterns.AutoWind.SetASCE710"], args, $"set {name} to ASCE 7 wind, exposure from the areas"))!;
        }
        var getter = used["method"]!.ToString().Replace(".Set", ".Get", StringComparison.Ordinal);
        var back = Try(e, $"read {name}", "LoadPatterns.AutoWind." + getter.Split('.').Last(), D(("Name", name)));
        var result = new JsonObject { ["pattern"] = name, ["code"] = used["method"]!.ToString().EndsWith("716", StringComparison.Ordinal) ? "ASCE 7-16" : "ASCE 7-10" };
        if (back?["values"] is JsonObject values) result["read_back"] = values.DeepClone();
        result["note"] = $"{e.Program.Name} computes the wind loads when the analysis runs, from the extents of the diaphragms or of the model.";
        return result;
    }

    private static JsonNode AddResponseSpectrum(JsonElement a, ICsiLink e)
    {
        var info = e.Info();
        var function = a.Text("function", "CPM-ASCE716");
        var name = a.Text("case", "CPM-RS");
        var site = a.Need("site_class");
        var ss = a.Number("ss");
        var s1 = a.Number("s1");
        var tl = a.Number("tl", 8);
        var fa = a.Number("fa", Spectrum.Fa(site, ss));
        var fv = a.Number("fv", Spectrum.Fv(site, s1));
        var (sds, sd1) = Spectrum.Design(ss, s1, fa, fv);
        var damping = a.Number("damping", 0.05);
        var (periods, values) = Spectrum.Points(sds, sd1, tl);
        if (IsEtabs(e)) EtabsLateral.UserSpectrum(e, function, periods, values, damping); // ETABS 23's FuncRS has only NTC 2008/2018
        else Call(e, $"add the spectrum {function}", "Func.FuncRS.SetUser", D(("Name", function), ("NumberItems", periods.Length), ("Period", periods), ("Value", values), ("DampRatio", damping)));

        var modal = a.Text("modal_case");
        if (modal.Length == 0)
        {
            modal = e.LoadCases().FirstOrDefault(c => Try(e, "read a load case's type", "LoadCases.GetTypeOAPI", D(("Name", c))) is { } t && (ApiCall.Value(t, "CaseType")?.ToString() ?? "").Contains("Modal", StringComparison.OrdinalIgnoreCase))
                ?? e.LoadCases().FirstOrDefault(c => c.Contains("Modal", StringComparison.OrdinalIgnoreCase)) ?? "Modal";
        }
        var scale = Units.Gravity(info) * a.Number("importance", 1) / a.Number("r");
        var dirs = a.Text("direction", "both").ToLowerInvariant() switch { "x" => new[] { "U1" }, "y" => new[] { "U2" }, "both" => new[] { "U1", "U2" }, _ => throw new ToolInputException("direction is x, y or both.") };
        Call(e, $"add the response spectrum case {name}", "LoadCases.ResponseSpectrum.SetCase", D(("Name", name)));
        Try(e, $"put {name} on the modal case {modal}", "LoadCases.ResponseSpectrum.SetModalCase", D(("Name", name), ("ModalCase", modal)));
        Call(e, $"give {name} its loads", "LoadCases.ResponseSpectrum.SetLoads", D(("Name", name), ("NumberLoads", dirs.Length), ("LoadName", dirs),
            ("Func", Enumerable.Repeat(function, dirs.Length).ToArray()), ("SF", Enumerable.Repeat(scale, dirs.Length).ToArray()),
            ("CSys", Enumerable.Repeat("Global", dirs.Length).ToArray()), ("Ang", new double[dirs.Length])));
        Try(e, $"set the damping of {name}", "LoadCases.ResponseSpectrum.SetDampConstant", D(("Name", name), ("Damp", damping)));
        var back = Try(e, $"read {name}'s loads", "LoadCases.ResponseSpectrum.GetLoads", D(("Name", name)));
        return new JsonObject
        {
            ["function"] = function, ["case"] = name, ["modal_case"] = modal,
            ["fa"] = R(fa, 4), ["fv"] = R(fv, 4), ["sds"] = R(sds, 4), ["sd1"] = R(sd1, 4), ["points"] = periods.Length,
            ["scale_factor"] = R(scale, 6), ["directions"] = Arr(dirs),
            ["read_back"] = back is null ? null : new JsonObject { ["loads"] = Arr(Names(back, "LoadName")), ["functions"] = Arr(Names(back, "Func")), ["scale_factors"] = ApiCall.Value(back, "SF")?.DeepClone() },
        };
    }

    /// <summary>The largest base shear of a case in X or Y, read through the API's results (every program has BaseReact).</summary>
    internal static double BaseShear(ICsiLink e, string loadCase, string direction)
    {
        var call = (JsonObject)e.Raw($"read the base shear of {loadCase}", m =>
        {
            object model = m;
            ApiCall.Invoke(model, "Results.Setup.DeselectAllCasesAndCombosForOutput");
            ApiCall.Must(model, "Results.Setup.SetCaseSelectedForOutput", D(("Name", loadCase), ("Selected", true)), $"choose {loadCase} for output");
            return ApiCall.Must(model, "Results.BaseReact", null, $"read the base reactions of {loadCase}");
        })!;
        var values = ApiCall.Value(call, direction.Equals("x", StringComparison.OrdinalIgnoreCase) ? "FX" : "FY") as JsonArray;
        if (values is null || values.Count == 0) throw new EtabsException($"{e.Program.Name} gave no base reaction for {loadCase}: has the analysis run it?");
        return values.Max(v => Math.Abs(ApiCall.Number(v) is var x && double.IsFinite(x) ? x : 0));
    }

    private static JsonNode ScaleResponseSpectrum(JsonElement a, ICsiLink e)
    {
        var rs = a.Need("rs_case");
        var elf = a.Need("elf_case");
        var direction = a.Need("direction").ToLowerInvariant();
        if (direction is not ("x" or "y")) throw new ToolInputException("direction is x or y.");
        var target = a.Number("target", 1.0);
        if (!e.Info().Locked) throw new ToolInputException("The model has no results: run the analysis first (both cases must have run).");
        var vrs = BaseShear(e, rs, direction);
        var velf = BaseShear(e, elf, direction);
        if (vrs <= 0) throw new EtabsException($"{rs} has no base shear in {direction.ToUpperInvariant()}: is it loaded in that direction?");
        var ratio = target * velf / vrs;
        var result = new JsonObject
        {
            ["rs_base_shear"] = R(vrs, 3), ["elf_base_shear"] = R(velf, 3), ["target"] = target, ["rs_over_elf"] = R(vrs / Math.Max(velf, 1e-12), 4),
            ["passes"] = ratio <= 1.0 + 1e-9, ["scale_needed"] = R(Math.Max(ratio, 1), 4),
        };
        if (!a.Flag("apply") || ratio <= 1.0 + 1e-9)
        {
            result["applied"] = false;
            if (a.Flag("apply")) result["note"] = "The response spectrum already reaches the target: nothing to scale.";
            return result;
        }
        var load = direction == "x" ? "U1" : "U2";
        var back = Call(e, $"read {rs}'s loads", "LoadCases.ResponseSpectrum.GetLoads", D(("Name", rs)));
        var loads = Names(back, "LoadName");
        var funcs = Names(back, "Func");
        var sf = (ApiCall.Value(back, "SF") as JsonArray)?.Select(v => ApiCall.Number(v) is var x && double.IsFinite(x) ? x : 0).ToArray() ?? [];
        var csys = Names(back, "CSys");
        var ang = (ApiCall.Value(back, "Ang") as JsonArray)?.Select(v => ApiCall.Number(v) is var x && double.IsFinite(x) ? x : 0).ToArray() ?? new double[loads.Count];
        var scaled = 0;
        for (var i = 0; i < loads.Count && i < sf.Length; i++)
            if (loads[i].Equals(load, StringComparison.OrdinalIgnoreCase)) { sf[i] *= ratio; scaled++; }
        if (scaled == 0) throw new EtabsException($"{rs} has no {load} load to scale.");
        if (e.Info().Locked) Call(e, "unlock the model to scale the case", "SetModelIsLocked", D(("LockIt", false)));
        Call(e, $"scale {rs}", "LoadCases.ResponseSpectrum.SetLoads", D(("Name", rs), ("NumberLoads", loads.Count), ("LoadName", loads.ToArray()), ("Func", funcs.ToArray()),
            ("SF", sf), ("CSys", csys.Count == loads.Count ? csys.ToArray() : Enumerable.Repeat("Global", loads.Count).ToArray()), ("Ang", ang)));
        result["applied"] = true;
        result["scale_factors_now"] = new JsonArray(sf.Select(v => (JsonNode)R(v, 6)).ToArray());
        result["note"] = "The case is scaled and the results were deleted: run the analysis again.";
        return result;
    }

    private static JsonNode AssignDiaphragm(JsonElement a, ICsiLink e)
    {
        var name = a.Text("name", "D1");
        var semi = a.Flag("semi_rigid");
        if (IsSap(e)) return SapDiaphragm(a, e, name);
        Call(e, $"make the diaphragm {name}", "Diaphragm.SetDiaphragm", D(("Name", name), ("SemiRigid", semi)));
        var areas = a.Texts("areas");
        if (areas.Count == 0)
        {
            var floors = e.Areas().Where(x => x.Kind == "floor").ToList();
            if (!a.Flag("all_floors"))
            {
                var story = a.Text("story");
                if (story.Length == 0) throw new ToolInputException("Give story, areas or all_floors.");
                var level = e.Stories().FirstOrDefault(s => s.Name.Equals(story, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolInputException($"No story named {story}.");
                floors = floors.Where(f => f.Corners.All(c => Math.Abs(c.Z - level.Elevation) < 1e-3)).ToList();
            }
            areas = floors.Select(f => f.Name).ToList();
        }
        if (areas.Count == 0) throw new ToolInputException("There are no floors there to take the diaphragm.");
        var done = new List<string>();
        var failed = new JsonArray();
        foreach (var area in areas)
        {
            var set = Try(e, $"give {area} the diaphragm", "AreaObj.SetDiaphragm", D(("Name", area), ("DiaphragmName", name)));
            var back = Try(e, $"read {area}'s diaphragm", "AreaObj.GetDiaphragm", D(("Name", area)));
            if (set is not null && (back is null || ApiCall.Value(back, "DiaphragmName")?.ToString() == name)) done.Add(area);
            else failed.Add(area);
        }
        e.Refresh();
        return new JsonObject { ["diaphragm"] = name, ["semi_rigid"] = semi, ["done"] = done.Count, ["areas"] = Arr(done.Take(200)), ["failed"] = failed };
    }

    private static JsonNode SapDiaphragm(JsonElement a, ICsiLink e, string name)
    {
        var points = e.Points();
        if (points.Count == 0) throw new ToolInputException("The model has no joints.");
        var low = points.Min(p => p.Z);
        List<double> levels;
        if (a.Flag("all_floors")) levels = points.Select(p => Math.Round(p.Z, 4)).Distinct().Where(z => z > low + 1e-4).OrderBy(z => z).ToList();
        else if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("z", out var zv) && zv.ValueKind == JsonValueKind.Number) levels = [zv.GetDouble()];
        else throw new ToolInputException("In SAP2000 give z (the level), or all_floors.");
        var made = new JsonArray();
        var joints = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            var constraint = levels.Count == 1 ? name : $"{name}-{i + 1}";
            Call(e, $"make the diaphragm {constraint}", "ConstraintDef.SetDiaphragm", D(("Name", constraint), ("Axis", 3), ("CSys", "Global")));
            var at = points.Where(p => Math.Abs(p.Z - levels[i]) < 1e-3).ToList();
            foreach (var p in at)
                if (Try(e, $"give joint {p.Name} the diaphragm", "PointObj.SetConstraint", D(("Name", p.Name), ("ConstraintName", constraint), ("ItemType", 0), ("Replace", true))) is not null) joints++;
            made.Add(new JsonObject { ["diaphragm"] = constraint, ["z"] = R(levels[i], 4), ["joints"] = at.Count });
        }
        return new JsonObject { ["diaphragms"] = made, ["joints_done"] = joints };
    }

    // ======================================================================================================
    // A building in one call
    // ======================================================================================================

    private static JsonNode GenerateBuilding(JsonElement a, ICsiLink e)
    {
        var info = e.Info();
        var unit = Units.LengthToM(info.Length);
        double Len(double metres) => metres / unit;
        var spansX = a.Items("spans_x").Select(v => v.GetDouble()).ToList();
        var spansY = a.Items("spans_y").Select(v => v.GetDouble()).ToList();
        if (spansX.Count == 0 || spansY.Count == 0 || spansX.Concat(spansY).Any(s => s <= 0)) throw new ToolInputException("Give one span or more in X and in Y, each above zero.");
        if (spansX.Count * spansY.Count > 400) throw new ToolInputException("At most 400 bays.");
        var safe = e.Program.Key == CsiProgram.Safe.Key;
        var stories = safe ? 1 : Math.Clamp(a.Whole("stories", 3), 1, 100);
        var height = a.Number("story_height", Len(3.2));
        var bottom = a.Number("bottom_story_height", height);
        var system = a.Text("system", "concrete_frame").ToLowerInvariant();
        if (system is not ("concrete_frame" or "steel_frame" or "flat_slab")) throw new ToolInputException("system is concrete_frame, steel_frame or flat_slab.");
        var newModel = a.Flag("new_model", true);
        var notes = new JsonArray();
        var xs = new List<double> { 0 };
        foreach (var s in spansX) xs.Add(xs[^1] + s);
        var ys = new List<double> { 0 };
        foreach (var s in spansY) ys.Add(ys[^1] + s);

        if (newModel)
        {
            var code = Units.Code(info.Force, info.Length);
            Call(e, "start a new model", "InitializeNewModel", D(("Units", code)));
            if (IsEtabs(e))
            {
                var uniform = spansX.All(s => Math.Abs(s - spansX[0]) < 1e-9) && spansY.All(s => Math.Abs(s - spansY[0]) < 1e-9);
                Call(e, "make the stories and grids", "File.NewGridOnly", D(("NumberStorys", stories), ("TypicalStoryHeight", height), ("BottomStoryHeight", bottom),
                    ("NumberLinesX", xs.Count), ("NumberLinesY", ys.Count), ("SpacingX", spansX[0]), ("SpacingY", spansY[0])));
                if (!uniform) notes.Add($"The grid lines are evenly spaced ({spansX[0]} by {spansY[0]}); the members follow the spans given. Move the grid lines in Edit > Edit Stories and Grids.");
            }
            else e.Raw("start a blank model", m => ApiCall.First((object)m, ["File.NewBlank", "File.NewGridOnly"], D(("NumberStorys", 1), ("TypicalStoryHeight", height), ("BottomStoryHeight", height), ("NumberLinesX", xs.Count), ("NumberLinesY", ys.Count), ("SpacingX", spansX[0]), ("SpacingY", spansY[0])), "start a blank model"));
        }

        // Levels: the program's stories (ETABS), or our own elevations.
        List<double> levels;
        if (IsEtabs(e))
        {
            var have = e.Stories();
            if (have.Count < 2) throw new EtabsException($"{e.Program.Name} has no stories to build on.");
            levels = have.Select(s => s.Elevation).ToList();
        }
        else
        {
            levels = [0];
            for (var i = 0; i < stories; i++) levels.Add(levels[^1] + (i == 0 ? bottom : height));
        }

        // The material and the sections.
        var steel = system == "steel_frame";
        var fc = a.Number("fc_mpa", 30);
        var concrete = $"CPM-C{fc.ToString("0", CultureInfo.InvariantCulture)}";
        Run(e, "add_material", new JsonObject
        {
            ["name"] = concrete, ["type"] = "concrete", ["elastic_modulus"] = Units.FromMpa(info, 4700 * Math.Sqrt(fc)), ["fc"] = Units.FromMpa(info, fc),
            ["unit_weight"] = Units.FromKnPerM3(info, 25), ["poisson"] = 0.2, ["thermal"] = 1e-5,
        }, notes);
        string column, beam, steelMat = "";
        if (steel)
        {
            var fy = a.Number("fy_mpa", 345);
            steelMat = $"CPM-S{fy.ToString("0", CultureInfo.InvariantCulture)}";
            Run(e, "add_material", new JsonObject
            {
                ["name"] = steelMat, ["type"] = "steel", ["elastic_modulus"] = Units.FromMpa(info, 200000), ["fy"] = Units.FromMpa(info, fy), ["fu"] = Units.FromMpa(info, fy * 1.3),
                ["unit_weight"] = Units.FromKnPerM3(info, 77), ["poisson"] = 0.3, ["thermal"] = 1.17e-5,
            }, notes);
            var sizes = a.TryGetProperty("steel_sizes", out var ss) ? ss : default;
            double[] I(string key, double[] metres) => sizes.ValueKind == JsonValueKind.Object && sizes.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 4
                ? v.EnumerateArray().Take(4).Select(x => x.GetDouble()).ToArray() : metres.Select(Len).ToArray();
            var c = I("column", [0.35, 0.35, 0.02, 0.012]);
            var b = I("beam", [0.45, 0.19, 0.015, 0.009]);
            column = "CPM-COL-I"; beam = "CPM-BEAM-I";
            Run(e, "add_frame_section", new JsonObject { ["name"] = column, ["shape"] = "i_section", ["material"] = steelMat, ["depth"] = c[0], ["width"] = c[1], ["flange_thickness"] = c[2], ["web_thickness"] = c[3] }, notes);
            Run(e, "add_frame_section", new JsonObject { ["name"] = beam, ["shape"] = "i_section", ["material"] = steelMat, ["depth"] = b[0], ["width"] = b[1], ["flange_thickness"] = b[2], ["web_thickness"] = b[3] }, notes);
        }
        else
        {
            var c = Pair(a, "column", Len(0.5), Len(0.5));
            var b = Pair(a, "beam", Len(0.6), Len(0.3));
            column = $"CPM-C{Tag(c.D, unit)}x{Tag(c.B, unit)}"; beam = $"CPM-B{Tag(b.B, unit)}x{Tag(b.D, unit)}";
            Run(e, "add_frame_section", new JsonObject { ["name"] = column, ["shape"] = "rectangle", ["material"] = concrete, ["depth"] = c.D, ["width"] = c.B }, notes);
            Run(e, "add_frame_section", new JsonObject { ["name"] = beam, ["shape"] = "rectangle", ["material"] = concrete, ["depth"] = b.D, ["width"] = b.B }, notes);
        }
        var thickness = a.Number("slab_thickness", Len(0.2));
        var slab = $"CPM-S{Tag(thickness, unit)}";
        if (IsSap(e)) Run(e, "add_area_section", new JsonObject { ["name"] = slab, ["material"] = concrete, ["thickness"] = thickness, ["type"] = "shell_thin" }, notes, Sap2000.SapTools.All);
        else e.Raw($"add the slab section {slab}", m => ApiCall.Must((object)m, "PropArea.SetSlab", D(("Name", slab), ("SlabType", 0), ("ShellType", 1), ("MatProp", concrete), ("Thickness", thickness), ("color", -1)), $"add the slab section {slab}"));

        // The members, story by story.
        int columns = 0, beams = 0, slabs = 0;
        var slabNames = new List<string>();
        var flat = system == "flat_slab";
        for (var s = 1; s < levels.Count; s++)
        {
            var (z0, z1) = (levels[s - 1], levels[s]);
            if (!safe)
            {
                foreach (var x in xs) foreach (var y in ys) { e.AddFrame(x, y, z0, x, y, z1, column); columns++; }
                if (!flat)
                {
                    foreach (var y in ys) for (var i = 0; i + 1 < xs.Count; i++) { e.AddFrame(xs[i], y, z1, xs[i + 1], y, z1, beam); beams++; }
                    foreach (var x in xs) for (var j = 0; j + 1 < ys.Count; j++) { e.AddFrame(x, ys[j], z1, x, ys[j + 1], z1, beam); beams++; }
                }
            }
            var at = safe ? z0 : z1;
            for (var i = 0; i + 1 < xs.Count; i++)
                for (var j = 0; j + 1 < ys.Count; j++)
                {
                    slabNames.Add(e.AddArea([(xs[i], ys[j], at), (xs[i + 1], ys[j], at), (xs[i + 1], ys[j + 1], at), (xs[i], ys[j + 1], at)], slab));
                    slabs++;
                }
        }

        // Supports: fixed at the column bases; in SAFE the slab sits on point supports at the columns.
        var supports = 0;
        var baseZ = levels[0];
        foreach (var p in e.Points().Where(p => Math.Abs(p.Z - baseZ) < 1e-6 && xs.Any(x => Math.Abs(x - p.X) < 1e-6) && ys.Any(y => Math.Abs(y - p.Y) < 1e-6)))
        {
            e.SetRestraint(p.Name, safe ? [true, true, true, false, false, false] : [true, true, true, true, true, true]);
            supports++;
        }

        // A rigid diaphragm per floor.
        if (!safe)
            try
            {
                if (IsSap(e)) SapDiaphragm(JsonSerializer.SerializeToElement(new { all_floors = true }), e, "D");
                else AssignDiaphragm(JsonSerializer.SerializeToElement(new { name = "D1", all_floors = true }), e);
                notes.Add("Rigid diaphragms on every floor.");
            }
            catch (Exception ex) when (ex is EtabsException or ToolInputException) { notes.Add("The diaphragms were not made: " + ex.Message); }

        // Superimposed dead and live loads on the slabs.
        var sdl = a.Number("sdl", Units.FromKpa(info, 1.5));
        var live = a.Number("live", Units.FromKpa(info, 2.5));
        var sdlName = e.LoadPatterns().FirstOrDefault(p => p.Type == "Super Dead")?.Name ?? "SDL";
        if (!e.LoadPatterns().Any(p => p.Name.Equals(sdlName, StringComparison.OrdinalIgnoreCase))) e.AddLoadPattern(sdlName, "Super Dead", 0);
        var liveName = e.LoadPatterns().FirstOrDefault(p => p.Type is "Live" or "Reducible Live")?.Name ?? "LIVE";
        if (!e.LoadPatterns().Any(p => p.Name.Equals(liveName, StringComparison.OrdinalIgnoreCase))) e.AddLoadPattern(liveName, "Live", 0);
        foreach (var area in slabNames)
        {
            if (sdl > 0) e.SetAreaLoad(area, sdlName, sdl, true);
            if (live > 0) e.SetAreaLoad(area, liveName, live, true);
        }
        e.Refresh();
        return new JsonObject
        {
            ["new_model"] = newModel, ["system"] = system, ["bays"] = $"{spansX.Count} x {spansY.Count}", ["stories"] = levels.Count - 1,
            ["columns"] = columns, ["beams"] = beams, ["slabs"] = slabs, ["supports"] = supports,
            ["sections"] = new JsonObject { ["column"] = safe ? null : column, ["beam"] = safe || flat ? null : beam, ["slab"] = slab },
            ["materials"] = Arr(steel ? [concrete, steelMat] : [concrete]),
            ["loads"] = new JsonObject { [sdlName] = R(sdl, 6), [liveName] = R(live, 6) },
            ["notes"] = notes,
        };
    }

    private static (double D, double B) Pair(JsonElement a, string name, double d, double b) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 2 ? (v[0].GetDouble(), v[1].GetDouble()) : (d, b);

    /// <summary>A size in mm for a section's name.</summary>
    private static string Tag(double size, double unit) => Math.Round(size * unit * 1000).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Runs one of the tools by name (SAP2000's material and section tools, which work in every program).</summary>
    private static void Run(ICsiLink e, string tool, JsonObject args, JsonArray notes, IReadOnlyList<Tool>? from = null)
    {
        var found = (from ?? Sap2000.SapTools.All).First(t => t.Name == tool);
        try { found.Run(JsonSerializer.SerializeToElement(args), e); }
        catch (EtabsException ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)) { notes.Add($"{args["name"]} was already there and was kept."); }
        catch (EtabsException ex) { throw new EtabsException(ex.Message.Replace("SAP2000", e.Program.Name, StringComparison.Ordinal)); }
    }
}
