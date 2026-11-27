using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>
/// The Modeler's analysis definitions (CaseTools, AnalysisDefs): functions, every load case type, the mass source,
/// P-Delta and the object loads beyond uniform gravity. Ahmed: "every option every spring all cases dynamic and static
/// and time history and everything", and temperature "well covered".
/// </summary>
static class AnalysisTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Analysis", [6, 5], [4], 3, 3.2));

    static JsonObject Run(ICsiLink link, string tool, object args)
    {
        var t = CaseTools.All.Concat(HandTools.All).Concat(LateralTools.All).Single(x => x.Name == tool);
        return (JsonObject)t.Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);
    }

    static void Refused(ICsiLink link, string tool, object args, string says)
    {
        try { Run(link, tool, args); }
        catch (ToolInputException ex) { Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), $"{tool}: '{says}' expected in: {ex.Message}"); return; }
        throw new Exception($"{tool} took {JsonSerializer.Serialize(args)}");
    }

    static StructuralModel ViaE2k(StructuralModel m) => E2kReader.Read(E2kWriter.Write(m));

    static string Beam(StructuralModel m) { var a = m.LineAssigns.First(x => m.Line(x.Label)!.Kind == LineKind.Beam); return a.Label + "@" + a.Story; }
    static string Floor(StructuralModel m) { var a = m.AreaAssigns.First(x => m.Area(x.Label)!.Kind == AreaKind.Floor); return a.Label + "@" + a.Story; }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("analysis: a code spectrum, a sine history and points become functions the .e2k and .s2k carry", () =>
        {
            var link = Frame();
            Run(link, "define_function", new { name = "RS-ASCE", kind = "spectrum", from = "code", code = "ASCE 7-16", values = new { Ss = "1.5", S1 = "0.6", SiteClass = "D" } });
            Run(link, "define_function", new { name = "SINE", kind = "history", from = "sine", period = 0.5, cycles = 4, amplitude = 0.3 });
            Run(link, "define_function", new { name = "PTS", kind = "spectrum", points = new[] { new[] { 0.0, 0.4 }, new[] { 0.5, 1.0 }, new[] { 2.0, 0.25 } }, damping = 0.03 });
            var back = AnalysisDefs.ListFunctions(ViaE2k(link.Model)).ToDictionary(f => f.Name);
            var asce = back["RS-ASCE"];
            Check(asce.Kind == "spectrum" && asce.Points.Count > 50, "the ASCE spectrum has its points");
            Check(Math.Abs(asce.Points.First(p => Math.Abs(p.X - 0.3) < 1e-9).Y - 1.0) < 1e-6, "Sa is SDS = 1.0 on the plateau");
            Check(back["SINE"].Kind == "history" && back["SINE"].Points.Count == 65 && Math.Abs(back["SINE"].Points.Max(p => p.Y) - 0.3) < 1e-6, "the sine wave, 4 cycles of 16 steps");
            Check(Math.Abs(back["PTS"].Damping - 0.03) < 1e-12 && back["PTS"].Points.Count == 3, "the points and the damping");
            var s2k = S2kWriter.Write(link.Model, "a.s2k").Text;
            Check(s2k.Contains("TABLE:  \"FUNCTION - RESPONSE SPECTRUM - USER\"") && s2k.Contains("TABLE:  \"FUNCTION - TIME HISTORY - USER\""), "the .s2k has the function tables");
            Refused(link, "define_function", new { name = "BAD", kind = "spectrum", points = new[] { new[] { 1.0, 1.0 }, new[] { 0.5, 1.0 } } }, "must go up");
            Refused(link, "define_function", new { name = "BAD", kind = "history", from = "code", code = "ASCE 7-16" }, "spectrum");
        });

        yield return ("analysis: the function forms' types (Ramp, Sawtooth, Triangular, User Periodic, From File) make their points, the form's type and fields are kept for Modify/Show, and a preview changes nothing (Ahmed 2026-11-27, every field)", () =>
        {
            var link = Frame();
            var p = Run(link, "define_function", new { name = "R", kind = "history", from = "ramp", ramp_time = 0.5, amplitude = 2, max_time = 3, preview = true });
            Check(p["points"]!.AsArray().Count == 3 && AnalysisDefs.ListFunctions(link.Model).Count == 0, "a preview gives the points and defines nothing");
            Run(link, "define_function", new { name = "R", kind = "history", from = "ramp", ramp_time = 0.5, amplitude = 2, max_time = 3 });
            Run(link, "define_function", new { name = "SAW", kind = "history", from = "sawtooth", period = 1, ramp_time = 0.1, cycles = 2, amplitude = 1 });
            Run(link, "define_function", new { name = "TRI", kind = "history", from = "triangular", period = 2, cycles = 3, amplitude = 0.5 });
            Run(link, "define_function", new { name = "PER", kind = "history", from = "periodic", cycles = 3, points = new[] { new[] { 0.0, 0.0 }, new[] { 0.5, 1.0 }, new[] { 1.0, 0.0 } } });
            Run(link, "define_function", new { name = "FILE", kind = "history", from = "file", points = new[] { new[] { 0.0, 0.0 }, new[] { 0.01, 0.2 } }, file = new { name = "elcentro.txt", header_lines = 2, values = "equal", interval = 0.01 } });
            var f = AnalysisDefs.ListFunctions(ViaE2k(link.Model)).ToDictionary(x => x.Name);
            Check(f["R"].Points.SequenceEqual(new[] { (0.0, 0.0), (0.5, 2.0), (3.0, 2.0) }), "the ramp: up at the ramp time, held to the maximum time");
            Check(f["SAW"].Points.Count == 11 && f["SAW"].Points[^1] == (2.0, 0.0) && f["SAW"].Points.Max(x => x.Y) == 1, "the sawtooth, 2 cycles of 5 points after 0");
            Check(f["TRI"].Points.Count == 10 && f["TRI"].Points[1] == (0.5, 0.5) && f["TRI"].Points[2] == (1.5, -0.5), "the triangle, 3 cycles");
            Check(f["PER"].Points.Count == 7 && f["PER"].Points[^1] == (3.0, 0.0), "one cycle repeated 3 times");
            var defs = Run(link, "analysis_definitions", new { })["functions"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>());
            Check(defs["SAW"]!["source"]!["Type"]!.GetValue<string>() == "sawtooth" && defs["SAW"]!["source"]!["ramp_time"]!.GetValue<string>() == "0.1", "the sawtooth form's fields come back");
            Check(defs["FILE"]!["source"]!["f:name"]!.GetValue<string>() == "elcentro.txt" && defs["FILE"]!["source"]!["f:header_lines"]!.GetValue<string>() == "2", "the From File fields come back");
            Check(defs["PER"]!["source"]!["cycle"]!.GetValue<string>().Contains("0.5"), "the one cycle comes back");
            Check(!E2kWriter.Write(link.Model).Contains("elcentro"), "the form's fields stay in the .cpm: the programs get a user function");
            Run(link, "define_function", new { name = "SAW2", was = "SAW", kind = "history", from = "sawtooth", period = 1, ramp_time = 0.1, cycles = 2, amplitude = 1 });
            Check(AnalysisDefs.FunctionSource(link.Model, "SAW") is null && AnalysisDefs.FunctionSource(link.Model, "SAW2") is not null, "a rename moves the fields");
            Refused(link, "define_function", new { name = "BAD", kind = "history", from = "sawtooth", period = 1, ramp_time = 0.5 }, "quarter");
            Refused(link, "define_function", new { name = "BAD", kind = "history", from = "ramp", ramp_time = 2, max_time = 1 }, "maximum time");
            Refused(link, "define_function", new { name = "BAD", kind = "history", from = "periodic", points = new[] { new[] { 0.5, 0.0 }, new[] { 1.0, 1.0 } } }, "time 0");
            Refused(link, "define_function", new { name = "BAD", kind = "history", from = "wavy" }, "no function type");
            Run(link, "delete_definition", new { kind = "function", name = "FILE" });
            Check(AnalysisDefs.FunctionSource(link.Model, "FILE") is null, "a deleted function loses its fields");
        });

        yield return ("analysis: every load case type is written, read back from the .e2k, and goes to the .s2k", () =>
        {
            var link = Frame();
            Run(link, "define_function", new { name = "RS1", kind = "spectrum", from = "code", code = "Eurocode 8 2004", values = new { ag = "0.25" } });
            Run(link, "define_function", new { name = "TH1", kind = "history", from = "ramp", period = 1, cycles = 2 });
            if (!link.Model.LoadCases.Exists(c => c.Name == "Modal")) Run(link, "define_load_case", new { name = "Modal", type = "Modal - Eigen", max_modes = 12 });
            Run(link, "define_load_case", new { name = "NLS", type = "Nonlinear Static", geometric = "P-Delta", loads = new object[] { new { pattern = "Dead", factor = 1.2 }, new { pattern = "Live", factor = 0.5 } } });
            Run(link, "define_load_case", new { name = "RITZ", type = "Modal - Ritz", max_modes = 9, loads = new object[] { new { accel = "U1", factor = 1 }, new { accel = "U2", factor = 1 } } });
            Run(link, "define_load_case", new { name = "RSX", type = "Response Spectrum", modal_case = "Modal", modal_combo = "CQC", dir_combo = "SRSS", damping = 0.05, eccentricity = 0.05, loads = new object[] { new { accel = "U1", factor = 9.80665, function = "RS1" } } });
            Run(link, "define_load_case", new { name = "THX", type = "Linear Modal History", modal_case = "Modal", steps = 200, step_size = 0.01, loads = new object[] { new { accel = "U1", factor = 9.80665, function = "TH1" } } });
            Run(link, "define_load_case", new { name = "THD", type = "Nonlinear Direct History", initial_case = "NLS", steps = 50, step_size = 0.02, geometric = "P-Delta", loads = new object[] { new { pattern = "Live", factor = 1, function = "TH1", arrival = 0.5 } } });
            Run(link, "define_load_case", new { name = "FNA", type = "Nonlinear Modal History (FNA)", modal_case = "RITZ", loads = new object[] { new { accel = "U2", factor = 9.80665, function = "TH1", angle = 30 } } });
            Run(link, "define_load_case", new { name = "LDH", type = "Linear Direct History", loads = new object[] { new { accel = "U3", factor = 1, function = "TH1" } } });
            Run(link, "define_load_case", new { name = "BUCK", type = "Buckling", max_modes = 6, loads = new object[] { new { pattern = "Dead", factor = 1 } } });
            var back = ViaE2k(link.Model);
            foreach (var name in new[] { "NLS", "RITZ", "RSX", "THX", "THD", "FNA", "LDH", "BUCK" })
            {
                var a = AnalysisDefs.ReadCase(link.Model.LoadCases.First(c => c.Name == name));
                var b = AnalysisDefs.ReadCase(back.LoadCases.First(c => c.Name == name));
                Check(JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b), $"{name} reads back the same: {JsonSerializer.Serialize(a)} / {JsonSerializer.Serialize(b)}");
            }
            var rs = AnalysisDefs.ReadCase(back.LoadCases.First(c => c.Name == "RSX"));
            Check(rs.Type == "Response Spectrum" && rs.ModalCombo == "CQC" && rs.Eccentricity == 0.05 && rs.Loads.Single().Function == "RS1", "the RS case's options");
            var thd = AnalysisDefs.ReadCase(back.LoadCases.First(c => c.Name == "THD"));
            Check(thd.InitialCase == "NLS" && thd.Steps == 50 && thd.Geometric == "P-Delta" && thd.Loads.Single().Arrival == 0.5, "the history's options");
            var s2k = S2kWriter.Write(link.Model, "a.s2k");
            foreach (var t in new[] { "CASE - RESPONSE SPECTRUM 2 - LOAD ASSIGNMENTS", "CASE - MODAL HISTORY 2 - LOAD ASSIGNMENTS", "CASE - DIRECT HISTORY 1 - GENERAL", "CASE - BUCKLING 1 - GENERAL", "CASE - STATIC 4 - NONLINEAR PARAMETERS" })
                Check(s2k.Text.Contains($"TABLE:  \"{t}\""), "the .s2k has " + t);
            Check(!s2k.Notes.Any(n => n.Contains("Load case", StringComparison.Ordinal)), "no case was left out of the .s2k: " + string.Join("; ", s2k.Notes));
        });

        yield return ("analysis: load cases refuse what the programs refuse, and in-use ones are not deleted", () =>
        {
            var link = Frame();
            Run(link, "define_function", new { name = "TH1", kind = "history", from = "sine" });
            Run(link, "define_function", new { name = "RS1", kind = "spectrum", points = new[] { new[] { 0.0, 1.0 }, new[] { 1.0, 1.0 } } });
            Refused(link, "define_load_case", new { name = "X", type = "Seismic Static" }, "no load case type");
            Refused(link, "define_load_case", new { name = "X", type = "Response Spectrum", loads = new object[] { new { pattern = "Dead", factor = 1, function = "RS1" } } }, "accelerations");
            Refused(link, "define_load_case", new { name = "X", type = "Response Spectrum", loads = new object[] { new { accel = "U1", factor = 1, function = "TH1" } } }, "spectrum function");
            Refused(link, "define_load_case", new { name = "X", type = "Response Spectrum", modal_case = "Nope", loads = new object[] { new { accel = "U1", factor = 1, function = "RS1" } } }, "no modal case");
            Refused(link, "define_load_case", new { name = "X", type = "Linear Static", loads = new object[] { new { pattern = "Nope", factor = 1 } } }, "no load pattern");
            Refused(link, "define_load_case", new { name = "X", type = "Linear Static", loads = new object[] { new { accel = "U1", factor = 1 } } }, "load patterns");
            Refused(link, "define_load_case", new { name = "X", type = "Linear Modal History", steps = 0, loads = new object[] { new { accel = "U1", factor = 1, function = "TH1" } } }, "steps");
            Run(link, "define_load_case", new { name = "RSX", type = "Response Spectrum", loads = new object[] { new { accel = "U1", factor = 1, function = "RS1" } } });
            Refused(link, "delete_definition", new { kind = "function", name = "RS1" }, "used by");
            Refused(link, "delete_definition", new { kind = "load_case", name = "Modal" }, "used by");
            Run(link, "delete_definition", new { kind = "load_case", name = "RSX" });
            Run(link, "delete_definition", new { kind = "function", name = "RS1" });
            Check(!AnalysisDefs.ListFunctions(link.Model).Any(f => f.Name == "RS1") && !link.Model.LoadCases.Any(c => c.Name == "RSX"), "deleted");
        });

        yield return ("analysis: the mass source counts loads, the seismic weight follows, and the .s2k carries it", () =>
        {
            var link = Frame();
            Run(link, "set_lateral_load", new { name = "EQX", code = "User Coefficient", values = new { C = "0.1" } });
            var w0 = link.Model.LoadPatterns.First(p => p.Name == "EQX").Auto!.Results["W"];
            Run(link, "define_mass_source", new { elements = true, loads = new object[] { new { pattern = "Live", factor = 0.25 } } });
            var w1 = link.Model.LoadPatterns.First(p => p.Name == "EQX").Auto!.Results["W"];
            Check(w1 > w0, $"a quarter of the live load adds weight: {w0} then {w1}");
            var ms = CsiProMax.Lateral.MassSource.Of(ViaE2k(link.Model));
            Check(ms.Elements && ms.Loads.Single() == ("Live", 0.25), "the mass source reads back from the .e2k");
            Check(S2kWriter.Write(link.Model, "a.s2k").Text.Contains("LoadPat=Live") , "the .s2k mass source names Live");
            Refused(link, "delete_definition", new { kind = "load_pattern", name = "Live" }, "mass source");
            Refused(link, "define_mass_source", new { loads = new object[] { new { pattern = "Nope", factor = 1 } } }, "no load pattern");
        });

        yield return ("analysis: Mass Source Data in full (name, mass options, centroid moved by a ratio) reads back (Ahmed 2026-11-27, every field)", () =>
        {
            var link = Frame();
            Run(link, "define_mass_source", new { name = "MsSrc2", elements = true, added_mass = false, lateral = true, vertical = true, lump = false, move = true, move_x = 0.05, move_y = 0.1, loads = new object[] { new { pattern = "Live", factor = 0.25 } } });
            var e2k = E2kWriter.Write(link.Model);
            Check(e2k.Contains("INCLUDEVERTICALMASS  \"Yes\"") && e2k.Contains("LUMPATSTORIES  \"No\"") && e2k.Contains("INCLUDEMOVE  \"Yes\"") && e2k.Contains("\"MsSrc2\""), "the .e2k has the options and the name");
            var ms = CsiProMax.Lateral.MassSource.Of(link.Model);
            Check(ms.Name == "MsSrc2" && !ms.AddedMass && ms.Vertical && !ms.Lump && ms.Move && ms.MoveX == 0.05 && ms.MoveY == 0.1, "read back: " + ms);
            var back = CsiProMax.Lateral.MassSource.Of(ViaE2k(link.Model));
            Check(back.Vertical && !back.Lump && back.Move, "the options read back from the .e2k alone");
            Check(link.Model.DefinitionTables?["Mass Source Definition"].Single(r => r["LoadPattern"] == "Live")["RatioY"] == "0.1", "the ratios are kept as the program's table row");
            Run(link, "define_mass_source", new { move = false, loads = new object[] { new { pattern = "Live", factor = 0.25 } } });
            Check(link.Model.DefinitionTables is null || !link.Model.DefinitionTables.ContainsKey("Mass Source Definition"), "no move, no table row");
            Check(CsiProMax.Lateral.MassSource.Of(link.Model).Vertical, "an option not given stays as it was");
        });

        yield return ("analysis: P-Delta none, non-iterative and iterative with its loads read back", () =>
        {
            var link = Frame();
            Run(link, "define_pdelta", new { method = "iterative", iterations = 8, tolerance = 0.0005, loads = new object[] { new { pattern = "Dead", factor = 1.2 }, new { pattern = "Live", factor = 0.5 } } });
            var pd = AnalysisDefs.PDelta(ViaE2k(link.Model));
            Check(pd.Method == "iterative" && pd.Iterations == 8 && pd.Loads.Count == 2 && pd.Loads[0] == ("Dead", 1.2), "iterative reads back: " + JsonSerializer.Serialize(pd));
            Run(link, "define_pdelta", new { method = "non-iterative" });
            Check(AnalysisDefs.PDelta(ViaE2k(link.Model)).Method == "non-iterative", "non-iterative");
            Check(link.Model.Passthrough.First(s => s.Heading == AnalysisDefs.Options).Lines.Count(l => l.Contains("PDELTA")) == 1, "one PDELTA line");
            Refused(link, "define_pdelta", new { method = "iterative" }, "load patterns");
        });

        yield return ("analysis: temperature, strain, point, trapezoid and displacement loads on joints, frames and shells", () =>
        {
            var link = Frame();
            var m = link.Model;
            link.AddLoadPattern("TEMP", "Temperature", 0);
            var beam = Beam(m); var floor = Floor(m);
            var joint = m.LineAssigns.First(x => m.Line(x.Label)!.Kind == LineKind.Column) is var col ? m.Line(col.Label)!.PointJ + "@" + col.Story : "";
            Run(link, "assign_object_loads", new { kind = "frame_temperature", pattern = "TEMP", objects = new[] { beam }, values = new { temperature = 25, gradient = 5 } });
            Run(link, "assign_object_loads", new { kind = "shell_temperature", pattern = "TEMP", objects = new[] { floor }, values = new { temperature = -15 } });
            Run(link, "assign_object_loads", new { kind = "frame_strain", pattern = "TEMP", objects = new[] { beam }, values = new { strain = 0.0002 } });
            Run(link, "assign_object_loads", new { kind = "frame_point", pattern = "Live", objects = new[] { beam }, values = new { value = 40, distance = 0.25 } });
            Run(link, "assign_object_loads", new { kind = "frame_trapezoid", pattern = "Live", objects = new[] { beam }, values = new { start = 2, end = 6, from = 0, to = 1 } });
            Run(link, "assign_object_loads", new { kind = "frame_uniform", pattern = "Live", objects = new[] { beam }, values = new { value = 3 }, dir = "X" });
            Run(link, "assign_object_loads", new { kind = "joint_displacement", pattern = "Dead", objects = new[] { joint }, values = new { uz = -0.01 } });
            Run(link, "assign_object_loads", new { kind = "joint_force", pattern = "Live", objects = new[] { joint }, values = new { fx = 5, mz = 2 } });
            var back = ViaE2k(m);
            Check(back.LineLoads.Count(l => E2kText.Value(l.Tokens, "TYPE") == "TEMP" && E2kText.Value(l.Tokens, "TEMP") == "25") == 1, "the frame temperature reads back");
            Check(back.AreaLoads.Any(l => E2kText.Value(l.Tokens, "TYPE") == "TEMP" && E2kText.Value(l.Tokens, "TEMP") == "-15"), "the shell temperature reads back");
            Check(back.PointLoads.Any(l => E2kText.Value(l.Tokens, "TYPE") == "GROUNDDISP" && E2kText.Value(l.Tokens, "UZ") == "-0.01"), "the ground displacement reads back");
            var s2k = S2kWriter.Write(m, "a.s2k");
            foreach (var t in new[] { "FRAME LOADS - TEMPERATURE", "AREA LOADS - TEMPERATURE", "FRAME LOADS - STRAIN", "FRAME LOADS - POINT", "JOINT LOADS - GROUND DISPLACEMENT" })
                Check(s2k.Text.Contains($"TABLE:  \"{t}\""), "the .s2k has " + t);
            Check(!s2k.Notes.Any(n => n.Contains("left out", StringComparison.Ordinal)), "nothing was left out of the .s2k: " + string.Join("; ", s2k.Notes));
            // replace: the same kind again takes the place of the first.
            Run(link, "assign_object_loads", new { kind = "frame_temperature", pattern = "TEMP", objects = new[] { beam }, values = new { temperature = 30 } });
            Check(m.LineLoads.Count(l => l.Pattern == "TEMP" && E2kText.Value(l.Tokens, "TYPE") == "TEMP") == 1, "replaced, not added");
            Refused(link, "assign_object_loads", new { kind = "frame_point", pattern = "Live", objects = new[] { beam }, values = new { value = 1, distance = 1.5 } }, "relative distance");
            Refused(link, "assign_object_loads", new { kind = "frame_point", pattern = "Live", objects = new[] { "NOPE@Story1" }, values = new { value = 1 } }, "no frame");
            Refused(link, "assign_object_loads", new { kind = "frame_heat", pattern = "Live", objects = new[] { beam }, values = new { value = 1 } }, "no load kind");
            Refused(link, "assign_object_loads", new { kind = "frame_uniform", pattern = "Live", objects = new[] { beam }, values = new { value = 1 }, dir = "W" }, "dir is one of");
        });

        yield return ("analysis: the Modeler's page defines a case and undo takes it back", () =>
        {
            var link = Frame();
            var before = ModelFile.ToJson(link.Model);
            var done = HandModel.Handle(link, JsonDocument.Parse(JsonSerializer.Serialize(new { tool = "define_load_case", args = new { name = "NLS", type = "Nonlinear Static", loads = new object[] { new { pattern = "Dead", factor = 1 } } } })).RootElement);
            Check(done["ok"]?.GetValue<bool>() == true, "done: " + done.ToJsonString());
            HandModel.Handle(link, JsonDocument.Parse("""{"undo":true}""").RootElement);
            Check(ModelFile.ToJson(link.Model) == before, "undo gives the model back");
            var defs = HandModel.Handle(link, JsonDocument.Parse("""{"tool":"analysis_definitions","args":{}}""").RootElement);
            Check(defs["result"]?["case_types"] is JsonArray { Count: 10 }, "the forms get the case types");
        });
    }
}
