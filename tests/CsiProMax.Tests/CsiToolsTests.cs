using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

/// <summary>
/// The tools every program has through the same API (CsiTools), the call-by-name layer under them (ApiCall), the
/// ASCE 7-16 spectrum and the unit factors: on a stand-in API that keeps every call it is given.
/// </summary>
static class CsiToolsTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }
    static void Near(double expected, double actual, string what, double tol = 1e-6)
    {
        if (Math.Abs(expected - actual) > tol) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static JsonNode Run(CsiProgram program, string tool, string json, ICsiLink e) =>
        ProgramTools.Chat(program).First(t => t.Name == tool).Run(Args(json), e);

    static (SafeMemory Link, FakeApi Api, MemoryEtabs Inner) Model(CsiProgram program, bool withModel = true)
    {
        var inner = new MemoryEtabs();
        if (withModel)
        {
            foreach (var (x, y) in new[] { (0.0, 0.0), (6.0, 0.0), (6.0, 5.0), (0.0, 5.0) })
                for (var s = 1; s < inner.Stories().Count; s++) inner.AddFrame(x, y, inner.Stories()[s - 1].Elevation, x, y, inner.Stories()[s].Elevation, "C500x500");
            for (var s = 1; s < inner.Stories().Count; s++)
            {
                var z = inner.Stories()[s].Elevation;
                inner.AddArea([(0, 0, z), (6, 0, z), (6, 5, z), (0, 5, z)], "S200");
            }
        }
        var api = new FakeApi(inner);
        return (new SafeMemory(inner, program: program) { Api = api }, api, inner);
    }

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("API by name: arguments by name with the rest at their defaults, ref values back by name, enums by name or number", () =>
        {
            var api = new FakeApi(new MemoryEtabs());
            var call = ApiCall.Invoke(api, "LoadPatterns.AutoSeismic.SetUserCoefficient", new Dictionary<string, object?> { ["name"] = "EQX", ["C"] = 0.1, ["dirflag"] = 4L });
            Equal(0, call["return"]!.GetValue<int>(), "the return code");
            Equal("SetUserCoefficient EQX dir 4 ecc 0 top  C 0.1 K 0", api.Log.Last(), "named arguments, case ignored, the rest at 0 or empty");
            var names = ApiCall.Invoke(api, "PointObj.GetNameList");
            Equal(2, names["values"]!["NumberNames"]!.GetValue<int>(), "a ref count by its name");
            Equal("P1,P2", string.Join(",", names["values"]!["MyName"]!.AsArray().Select(n => n!.GetValue<string>())), "a ref list by its name");
            var e1 = ApiCall.Invoke(api, "SetPresentUnits", new Dictionary<string, object?> { ["Units"] = "kN_mm_C" });
            Equal("units kN_mm_C", api.Log.Last(), "an enum by its name");
            ApiCall.Invoke(api, "SetPresentUnits", new Dictionary<string, object?> { ["Units"] = 6 });
            Equal("units kN_m_C", api.Log.Last(), "an enum by its number");
            var json = ApiCall.Invoke(api, "GetPresentUnits");
            Equal("kN_m_C", json["return"]!.GetValue<string>(), "an enum given back by its name");
            Check(ApiCall.Worked(e1), "SetPresentUnits worked");
            var pos = ApiCall.Invoke(api, "FrameObj.GetSection", null, [JsonDocument.Parse("\"F1\"").RootElement]);
            Equal("C500x500", pos["values"]!["PropName"]!.GetValue<string>(), "arguments in order, from JSON");
        });

        yield return ("API by name: a wrong path or method says what is there, and the overload with the most names given is taken", () =>
        {
            var api = new FakeApi(new MemoryEtabs());
            try { ApiCall.Invoke(api, "NoSuchObj.Get"); throw new Exception("no error"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("PointObj"), "lists the parts: " + ex.Message); }
            try { ApiCall.Invoke(api, "PointObj.SetNothing"); throw new Exception("no error"); }
            catch (MissingMethodException ex) { Check(ex.Message.Contains("PointObj has no method SetNothing"), ex.Message); }
            ApiCall.Invoke(api, "PointObj.SetRestraint", new Dictionary<string, object?> { ["Name"] = "P1", ["Value"] = new[] { true, true, true, false, false, false }, ["ItemType"] = 0 });
            Equal("restraint P1 True,True,True,False,False,False", api.Log.Last(), "a list of flags");
            Check(ApiCall.Reads("FrameObj.GetSection") && ApiCall.Reads("Count") && !ApiCall.Reads("FrameObj.SetSection") && !ApiCall.Reads("FrameObj.Delete"), "reads are Get and Count");
            var index = ApiCall.Index(typeof(FakeApi));
            Check(index.Any(m => m.Path == "LoadPatterns.AutoSeismic.SetASCE716") && index.Any(m => m.Path == "Results.Setup.SetCaseSelectedForOutput"), "the index walks every level: " + index.Count);
            var described = ApiCall.Describe(index.First(m => m.Path == "PointObj.SetRestraint").Method, "PointObj.SetRestraint");
            Check(described["parameters"]!.AsArray().Any(p => p!["name"]!.GetValue<string>() == "Value" && p["direction"]!.GetValue<string>() == "ref"), "a ref parameter is said so: " + described.ToJsonString());
        });

        yield return ("ASCE 7-16 spectrum: Fa and Fv from the tables with interpolation, SDS, SD1 and Sa(T)", () =>
        {
            Near(1.1, Spectrum.Fa("D", 1.0), "Fa D at Ss 1.0");
            Near(1.32, Spectrum.Fa("D", 0.6), "Fa D at Ss 0.6, between 1.4 and 1.2");
            Near(1.9, Spectrum.Fv("D", 0.4), "Fv D at S1 0.4");
            Near(1.2, Spectrum.Fa("C", 2.0), "above the table: the last column");
            try { Spectrum.Fa("E", 1.0); throw new Exception("no error"); } catch (ToolInputException ex) { Check(ex.Message.Contains("site-specific"), ex.Message); }
            var (sds, sd1) = Spectrum.Design(1.0, 0.4, 1.1, 1.9);
            Near(2.0 / 3 * 1.1, sds, "SDS");
            Near(2.0 / 3 * 0.76, sd1, "SD1");
            var ts = sd1 / sds;
            Near(0.4 * sds, Spectrum.Sa(0, sds, sd1, 8), "Sa at 0 is 0.4 SDS");
            Near(sds, Spectrum.Sa(ts, sds, sd1, 8), "the plateau");
            Near(sd1 / 2, Spectrum.Sa(2, sds, sd1, 8), "SD1 / T");
            Near(sd1 * 8 / 100, Spectrum.Sa(10, sds, sd1, 8), "beyond TL: SD1 TL / T2");
            var (t, v) = Spectrum.Points(sds, sd1, 8);
            Check(t.Length == v.Length && t.Length > 50 && t[0] == 0 && t.Contains(Math.Round(ts, 15)) || t.Any(x => Math.Abs(x - ts) < 1e-9), "points from 0, through Ts");
            Check(t.Zip(t.Skip(1)).All(p => p.Second > p.First), "periods go up");
        });

        yield return ("units: SI values in the model's units", () =>
        {
            var kn = new ModelInfo("", "", "kN", "m", "C", false);
            var nmm = new ModelInfo("", "", "N", "mm", "C", false);
            var kip = new ModelInfo("", "", "kip", "in", "F", false);
            Near(30000, Units.FromMpa(kn, 30), "30 MPa in kN/m2");
            Near(30, Units.FromMpa(nmm, 30), "30 MPa in N/mm2");
            Near(4.351, Units.FromMpa(kip, 30), "30 MPa in ksi", 1e-3);
            Near(25e-6 * 1e-3 * 1000, Units.FromKnPerM3(nmm, 25), "25 kN/m3 in N/mm3", 1e-12);
            Near(9806.65, Units.Gravity(nmm), "g in mm/s2");
            Equal(6, Units.Code("kN", "m"), "kN_m_C");
            Equal(12, Units.Code("tonf", "m"), "Ton_m_C");
        });

        yield return ("CSI tools: each program is given them, its own tools of the same name win, and the ones that ask always ask", () =>
        {
            var etabs = ProgramTools.Chat(CsiProgram.Etabs).Select(t => t.Name).ToHashSet();
            var sap = ProgramTools.Chat(CsiProgram.Sap2000).Select(t => t.Name).ToHashSet();
            var safe = ProgramTools.Chat(CsiProgram.Safe).Select(t => t.Name).ToHashSet();
            foreach (var name in new[] { "search_api", "describe_api", "call_api", "list_programs", "set_units", "list_properties", "list_load_definitions", "edit_table", "analysis_status", "unlock_model", "design_codes", "assign_group", "what_changed", "save_checkpoint", "list_checkpoints", "restore_checkpoint", "refresh_view", "generate_building" })
                Check(etabs.Contains(name) && sap.Contains(name) && safe.Contains(name), $"{name} in all three programs");
            foreach (var name in new[] { "set_mass_source", "set_modal_case", "add_seismic_load", "add_wind_load", "add_response_spectrum", "scale_response_spectrum", "assign_diaphragm" })
                Check(etabs.Contains(name) && sap.Contains(name) && !safe.Contains(name), $"{name} in ETABS and SAP2000");
            foreach (var name in new[] { "assign_pier_spandrel", "set_stories" }) Check(etabs.Contains(name) && !sap.Contains(name) && !safe.Contains(name), $"{name} in ETABS only");
            foreach (var name in CsiTools.SharedWithEtabs) Check(etabs.Contains(name), $"SAP2000's {name} in ETABS");
            foreach (var name in CsiTools.SharedWithSafe) Check(safe.Contains(name), $"SAP2000's {name} in SAFE");
            Check(ProgramTools.Chat(CsiProgram.Sap2000).First(t => t.Name == "add_material") == Sap2000Tool("add_material"), "SAP2000 keeps its own add_material");
            Check(ProgramTools.Chat(CsiProgram.Sap2000).First(t => t.Name == "add_area_section") == Sap2000Tool("add_area_section"), "SAP2000 keeps its own add_area_section (shells), ETABS has slabs and walls");
            var described = ProgramTools.Chat(CsiProgram.Etabs).First(t => t.Name == "joint_displacements").Description;
            Check(described.Contains("ETABS") && !described.Contains("SAP2000"), "SAP2000's tool in ETABS's words: " + described); // ETABS itself
            var always = ProgramTools.Every.Where(t => t.Ask == Ask.Always).Select(t => t.Name).ToHashSet();
            foreach (var name in new[] { "unlock_model", "save_checkpoint", "restore_checkpoint", "generate_building" }) Check(always.Contains(name), $"{name} always asks");
            foreach (var tool in CsiTools.Every) JsonDocument.Parse(tool.Schema);
            Equal(CsiTools.Every.Count(), CsiTools.Every.Select(t => t.Name).Distinct().Count(), "each name once");
        });

        yield return ("CSI tools: search, describe and call the API; a call that changes the model is refused", () =>
        {
            var (link, api, _) = Model(CsiProgram.Etabs);
            var found = Run(CsiProgram.Etabs, "search_api", """{"query":"pointobj restraint"}""", link);
            Check(found["methods"]!.AsArray().Any(m => m!["path"]!.GetValue<string>() == "PointObj.SetRestraint"), found.ToJsonString());
            var described = Run(CsiProgram.Etabs, "describe_api", """{"path":"LoadPatterns.AutoSeismic.SetUserCoefficient"}""", link);
            Equal(8, described["overloads"]![0]!["parameters"]!.AsArray().Count, "its parameters");
            Throws(() => Run(CsiProgram.Etabs, "describe_api", """{"path":"SetUserCoefficientX"}""", link), "a method that is not there");
            var one = Run(CsiProgram.Etabs, "call_api", """{"path":"FrameObj.GetSection","args":{"Name":"1"}}""", link);
            Equal("C500x500", one["values"]!["PropName"]!.GetValue<string>(), "a read");
            var two = Run(CsiProgram.Etabs, "call_api", """{"calls":[{"path":"PointObj.GetNameList"},{"path":"GetPresentUnits"}]}""", link);
            Equal(2, two["results"]!.AsArray().Count, "two reads in one go");
            try { Run(CsiProgram.Etabs, "call_api", """{"path":"FrameObj.SetSection","args":{"Name":"1"}}""", link); throw new Exception("went through"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("only reads"), ex.Message); }
            Check(!api.Log.Any(l => l.StartsWith("SetSection", StringComparison.Ordinal)), "nothing was changed");
        });

        yield return ("CSI tools: units, mass source, modal case, design codes and the analysis status go to the API and are read back", () =>
        {
            var (link, api, _) = Model(CsiProgram.Sap2000);
            var units = Run(CsiProgram.Sap2000, "set_units", """{"force":"kN","length":"mm"}""", link);
            Equal("units kN_mm_C", api.Log.Last(), "the unit set by force and length");
            Equal("kN_mm_C", units["units"]!.GetValue<string>(), "said back");
            Throws(() => Run(CsiProgram.Sap2000, "set_units", """{"units":"furlong_C"}""", link), "a set that is not one");
            var ms = Run(CsiProgram.Sap2000, "set_mass_source", """{"loads":[{"pattern":"Dead","factor":1},{"pattern":"Live","factor":0.25}]}""", link);
            Equal("mass CPM-MassSource elements True masses True loads True default True Dead*1,Live*0.25", api.Log.First(l => l.StartsWith("mass ", StringComparison.Ordinal)), "the mass source as given");
            Throws(() => Run(CsiProgram.Sap2000, "set_mass_source", """{"loads":[{"pattern":"Nope","factor":1}]}""", link), "a pattern that is not there");
            // ETABS 23 has no SourceMass: its default mass source is set on PropMaterial (0.4.17 field test).
            var (etabs, etabsApi, _) = Model(CsiProgram.Etabs);
            var ems = Run(CsiProgram.Etabs, "set_mass_source", """{"loads":[{"pattern":"Dead","factor":1},{"pattern":"Live","factor":0.25}]}""", etabs);
            Check(etabsApi.Mass.Loads && etabsApi.Mass.Patterns.SequenceEqual(["Dead", "Live"]) && etabsApi.Mass.Factors.SequenceEqual([1.0, 0.25]), "ETABS: PropMaterial.SetMassSource_1 with the loads");
            Equal("Dead,Live", string.Join(",", ems["from_loads"]!.AsArray().Select(x => x!.GetValue<string>())), "read back: " + ems.ToJsonString());
            var modal = Run(CsiProgram.Sap2000, "set_modal_case", """{"name":"MODAL","max_modes":9}""", link);
            Equal(9, modal["max_modes"]!.GetValue<int>(), "read back: " + modal.ToJsonString());
            var codes = Run(CsiProgram.Sap2000, "design_codes", """{"set":{"concrete":"ACI 318-19"}}""", link);
            Equal("ACI 318-19", codes["codes"]!["concrete"]!.GetValue<string>(), "set and read back");
            Equal("AISC 360-16", codes["codes"]!["steel"]!.GetValue<string>(), "the steel code");
            Equal("not in this program", codes["codes"]!["slab"]!.GetValue<string>(), "a design the program does not have");
            var status = Run(CsiProgram.Sap2000, "analysis_status", "{}", link);
            Equal(1, status["finished"]!.GetValue<int>(), "one case finished: " + status.ToJsonString());
            Equal("not run", status["cases"]![1]!["status"]!.GetValue<string>(), "the other not run");
        });

        yield return ("CSI tools: seismic and wind loads by the program's own numbers, the spectrum as points scaled by g Ie / R", () =>
        {
            var (link, api, inner) = Model(CsiProgram.Etabs);
            var eq = Run(CsiProgram.Etabs, "add_seismic_load", """{"name":"EQY","direction":"y","method":"user","coefficient":0.12,"k":1.5}""", link);
            Check(inner.LoadPatterns().Any(p => p.Name == "EQY" && p.Type == "Seismic"), "the pattern is made, as Seismic");
            // ETABS 23 has no SetUserCoefficient: its table takes it (0.4.17 field test).
            var uc = api.Tables["Load Pattern Definitions - Auto Seismic - User Coefficient"];
            string F(string[] row, string field) => row[Array.IndexOf(uc.Fields, field)];
            var ucRow = uc.Rows.Single(r => F(r, "Name") == "EQY");
            Equal("No|Yes|0.12|1.5|Base", string.Join("|", F(ucRow, "XDir"), F(ucRow, "YDir"), F(ucRow, "C"), F(ucRow, "K"), F(ucRow, "BotStory")), "ETABS: the user coefficient row, in Y, over the stories");
            Check(F(ucRow, "TopStory").Length > 0 && eq["code"]!.GetValue<string>() == "User Coefficient", "read back by its code: " + eq.ToJsonString());
            var (sap, sapApi, _) = Model(CsiProgram.Sap2000);
            Run(CsiProgram.Sap2000, "add_seismic_load", """{"name":"EQY","direction":"y","method":"user","coefficient":0.12}""", sap);
            Check(sapApi.Log.Last(l => l.StartsWith("SetUserCoefficient", StringComparison.Ordinal)).Contains("dir 2"), "SAP2000: Y is 2");
            Run(CsiProgram.Etabs, "add_seismic_load", """{"name":"EQX","direction":"x","r":8,"omega":3,"cd":5.5,"ss":1.0,"s1":0.4,"site_class":"D"}""", link);
            Check(api.Log.Last(l => l.StartsWith("ASCE716", StringComparison.Ordinal)).StartsWith("ASCE716_1 EQX dirs 100000 R 8 Ss 1 S1 0.4 site 4 Fa 1.1 Fv 1.9 TL 8 Z ", StringComparison.Ordinal),
                "ETABS: ASCE 7-16 by SetASCE716_1, X of six directions, Fa and Fv from the tables: " + api.Log.Last(l => l.StartsWith("ASCE716", StringComparison.Ordinal)));
            Run(CsiProgram.Sap2000, "add_seismic_load", """{"name":"EQX","direction":"x","r":8,"ss":1.0,"s1":0.4,"site_class":"D"}""", sap);
            Check(sapApi.Log.Last(l => l.StartsWith("ASCE716", StringComparison.Ordinal)).StartsWith("ASCE716_1 EQX dirs 10 ", StringComparison.Ordinal), "SAP2000: nDir is X and Y");
            Throws(() => Run(CsiProgram.Etabs, "add_seismic_load", """{"name":"Dead","direction":"x","method":"user","coefficient":0.1}""", link), "a pattern of another type");
            Run(CsiProgram.Etabs, "add_wind_load", """{"name":"WX","speed":110,"exposure":"D"}""", link);
            var wind = api.Tables["Load Pattern Definitions - Auto Wind - ASCE 7-16"];
            var wx = wind.Rows.Single(r => r[0] == "WX");
            Check(inner.LoadPatterns().Any(p => p.Name == "WX" && p.Type == "Wind") && wx[Array.IndexOf(wind.Fields, "WindSpeed")] == "110" && wx[Array.IndexOf(wind.Fields, "ExpType")] == "D",
                "ETABS 23's cAutoWind has no methods: the wind goes through its table: " + string.Join(",", wx));
            var rs = Run(CsiProgram.Etabs, "add_response_spectrum", """{"ss":1.0,"s1":0.4,"site_class":"D","r":8,"importance":1.25}""", link);
            Near(9.80665 * 1.25 / 8, rs["scale_factor"]!.GetValue<double>(), "g Ie / R in m/s2");
            Equal("Modal", rs["modal_case"]!.GetValue<string>(), "on the model's modal case");
            Check(api.Functions["CPM-ASCE716"].Length == rs["points"]!.GetValue<int>(), "the function's points");
            Near(0.4 * 2.0 / 3 * 1.1, api.Functions["CPM-ASCE716"][0], "Sa at T = 0");
            Equal("U1,U2", string.Join(",", api.RsLoads["CPM-RS"].Loads), "both directions");
        });

        yield return ("CSI tools: the response spectrum is scaled up to the ELF base shear, and left when it reaches it", () =>
        {
            var (link, api, inner) = Model(CsiProgram.Etabs);
            api.RsLoads["RSX"] = (["U1"], ["F"], [1.2]);
            api.Shear["RSX"] = [800, -900];
            api.Shear["EQX"] = [1000];
            Throws(() => Run(CsiProgram.Etabs, "scale_response_spectrum", """{"rs_case":"RSX","elf_case":"EQX","direction":"x"}""", link), "no analysis");
            inner.Locked = true;
            var look = Run(CsiProgram.Etabs, "scale_response_spectrum", """{"rs_case":"RSX","elf_case":"EQX","direction":"x"}""", link);
            Check(look["passes"]!.GetValue<bool>() == false && Math.Abs(look["scale_needed"]!.GetValue<double>() - 1000.0 / 900) < 1e-4 && look["applied"]!.GetValue<bool>() == false, look.ToJsonString());
            Near(1.2, api.RsLoads["RSX"].Sf[0], "only compared");
            var done = Run(CsiProgram.Etabs, "scale_response_spectrum", """{"rs_case":"RSX","elf_case":"EQX","direction":"x","apply":true}""", link);
            Near(1.2 * 1000 / 900, api.RsLoads["RSX"].Sf[0], "scaled", 1e-9);
            Check(done["applied"]!.GetValue<bool>() && !inner.Locked, "applied, and the model was unlocked for it");
            inner.Locked = true;
            api.Shear["RSX"] = [1100];
            var fine = Run(CsiProgram.Etabs, "scale_response_spectrum", """{"rs_case":"RSX","elf_case":"EQX","direction":"x","apply":true}""", link);
            Check(fine["passes"]!.GetValue<bool>() && !fine["applied"]!.GetValue<bool>() && inner.Locked, "nothing to scale: " + fine.ToJsonString());
        });

        yield return ("CSI tools: diaphragms on a story's floors (ETABS) and on a level's joints (SAP2000); groups; piers", () =>
        {
            var (link, api, inner) = Model(CsiProgram.Etabs);
            var d = Run(CsiProgram.Etabs, "assign_diaphragm", """{"name":"D1","story":"Story2"}""", link);
            Equal(1, d["done"]!.GetValue<int>(), "the one floor on Story2: " + d.ToJsonString());
            Equal(1, api.AreaDiaphragm.Count, "set through the API");
            var all = Run(CsiProgram.Etabs, "assign_diaphragm", """{"name":"D1","all_floors":true}""", link);
            Equal(3, all["done"]!.GetValue<int>(), "every floor");
            Throws(() => Run(CsiProgram.Etabs, "assign_diaphragm", """{"story":"Nope"}""", link), "a story that is not there");
            var (sap, sapApi, _) = Model(CsiProgram.Sap2000);
            var s = Run(CsiProgram.Sap2000, "assign_diaphragm", """{"name":"D","all_floors":true}""", sap);
            Equal(3, s["diaphragms"]!.AsArray().Count, "one per level above the base: " + s.ToJsonString());
            Equal(12, s["joints_done"]!.GetValue<int>(), "four joints a level");
            Check(sapApi.Log.Any(l => l == "constraint D-1 axis 3"), "a diaphragm constraint about Z");
            var frames = inner.Frames().Take(2).Select(f => f.Name).ToList();
            var g = Run(CsiProgram.Etabs, "assign_group", $$"""{"group":"G1","frames":["{{frames[0]}}","{{frames[1]}}"],"points":["no such"]}""", link);
            Equal(2, g["done"]!.GetValue<int>(), "two in the group");
            Equal(1, g["failed"]!.AsArray().Count, "the one that is not there is said");
            Equal(2, g["in_group_now"]!.GetValue<int>(), "read back");
            var area = inner.Areas()[0].Name;
            var p = Run(CsiProgram.Etabs, "assign_pier_spandrel", $$"""{"kind":"pier","label":"P1","areas":["{{area}}"]}""", link);
            Equal(1, p["done"]!.GetValue<int>(), "the pier read back: " + p.ToJsonString());
            Check(api.Log.Contains("pier label P1"), "the label made");
        });

        yield return ("pack: SAP2000, which has no stories, gets the levels its joints stand on, for the Viewer's plan views", () =>
        {
            var inner = new MemoryEtabs();
            inner.AddFrame(0, 0, 0, 0, 0, 3.2, "C500x500");
            inner.AddFrame(0, 0, 3.2, 0, 0, 6.4, "C500x500");
            var sap = new CsiProMax.Assistant.Sap2000.SapLink(new SafeMemory(inner, program: CsiProgram.Sap2000));
            Equal(0, sap.Stories().Count, "SAP2000 has no stories");
            var levels = Pack.Levels(sap, [new PointRow("1", 0, 0, 0), new PointRow("2", 0, 0, 3.2), new PointRow("3", 5, 0, 3.2000001), new PointRow("4", 0, 0, 6.4)]);
            Equal("Base@0|Z 3.2@3.2|Z 6.4@6.4", string.Join("|", levels.Select(l => $"{l.Name}@{l.Elevation.ToString(System.Globalization.CultureInfo.InvariantCulture)}")), "one level a height");
            Equal(inner.Stories().Count, Pack.Levels(new SafeMemory(inner, program: CsiProgram.Etabs), []).Count, "ETABS keeps its own stories");
        });

        yield return ("CSI tools: nothing imports SAP2000's load patterns table (0.4.34: set_lateral_load emptied it); a code load goes into the code's own table", () =>
        {
            const string uc = "Load Pattern Definitions - Auto Seismic - User Coefficient";
            var (link, api, inner) = Model(CsiProgram.Sap2000);
            api.PatternName = "LoadPat";
            inner.ResultTables["Load Pattern Definitions"] = new(["LoadPat", "Type", "SelfWtMult"], [["Dead", "Dead", "1"]]);
            inner.ResultTables[uc] = new(["LoadPat"], []);
            api.Tables[uc] = (["LoadPat", "IsAuto", "XDir", "YDir", "EccRatio", "C", "K"], []);
            var before = string.Join(";", api.Table.Select(r => string.Join("|", r)));
            // The guard is in EditableTables.Put itself, so no tool that writes through it can reach the wipe.
            string said = "";
            try { EditableTables.Put(link, "Load Pattern Definitions", "LoadPat", [EditableTables.Row(("LoadPat", "Dead"), ("AutoLoad", "None"))], "test it"); }
            catch (EtabsException ex) { said = ex.Message; }
            Check(said.Contains("does not take its 'Load Pattern Definitions' table back", StringComparison.Ordinal), "Put refuses SAP2000's patterns' table: " + said);
            // set_lateral_load gives the code by SAP2000's own Set call, then writes the inputs into the code's own table.
            const string ibc = "Load Pattern Definitions - Auto Seismic - IBC2006";
            inner.ResultTables[ibc] = new(["LoadPat"], []);
            api.Tables[ibc] = (["LoadPat", "R", "Ss"], []);
            var set = Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"IBC2006"}""", link);
            Check(set["read_back"] is JsonObject { Count: > 0 } && set["set_by"]?.ToString() == "LoadPatterns.AutoSeismic.SetIBC2006", "the code is set by its call and its row read back: " + set.ToJsonString());
            Check(api.Log.Any(l => l == "IBC2006 CPM-AUTO-S R 8 site 4"), "the call had plain starting values: " + string.Join(" | ", api.Log));
            set = Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"IBC2006","values":{"R":"6"}}""", link);
            Equal("6", set["read_back"]?["R"]?.ToString(), "an input given goes into the row the call made: " + set.ToJsonString());
            // A code SAP2000's API has no call for, on a pattern without it: the tool says how to choose it by hand, and nothing is written.
            const string kds = "Load Pattern Definitions - Auto Seismic - KDS 41 17 00-2019";
            inner.ResultTables[kds] = new(["LoadPat"], []);
            api.Tables[kds] = (["LoadPat", "Dir", "R"], []);
            // A code SAP2000's API has no call for: the pattern's row and the code's row in one import, every pattern kept.
            var patternsBefore = string.Join(",", api.Table.Select(r => r[0]));
            // A model with no file of its own is refused: there would be nothing to open again if a pattern were lost.
            api.File.Current = "";
            string unsaved = "";
            try { Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"KDS 41 17 00-2019","values":{"R":"5"}}""", link); }
            catch (Exception ex) { unsaved = ex.Message; }
            Check(unsaved.Contains("Save As", StringComparison.Ordinal) && api.PatternTableWrites == 0, "an unsaved model is refused before any import: " + unsaved);
            api.File.Current = "C:/models/scratch.sdb";
            var kdsSet = Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"KDS 41 17 00-2019","values":{"R":"5"}}""", link);
            Check(kdsSet["set_by"]?.ToString().Contains("in one import", StringComparison.Ordinal) == true && kdsSet["read_back"]?["R"]?.ToString() == "5", "KDS by the two tables at once: " + kdsSet.ToJsonString());
            Equal("KDS 41 17 00-2019", api.SeismicCodes["CPM-AUTO-S"], "the pattern has the code");
            Check(kdsSet["saved_before_edit"]?.ToString().Contains("scratch.sdb", StringComparison.Ordinal) == true, "the engineer is told the model was saved first: " + kdsSet.ToJsonString());
            Check(api.Tables[kds].Rows.Count == 1 && api.Tables[kds].Rows[0].Contains("X"), "the code's row, with its direction X: " + string.Join("|", api.Tables[kds].Rows.SelectMany(r => r)));
            Check(api.Table.Select(r => r[0]).Take(patternsBefore.Split(',').Length).SequenceEqual(patternsBefore.Split(',')), "every pattern is kept");
            // SAP2000's two names of one code: the AutoLoad field's and GetAutoSeismicCode's, and its table's (0.4.44 field test).
            Check(EditableTables.SameCode("USER COEFF", "User Coefficient") && EditableTables.SameCode("ASCE7-16", "ASCE 7-16") && EditableTables.SameCode("NBCC2020", "NBCC 2020")
                && !EditableTables.SameCode("IBC2006", "IBC2009") && !EditableTables.SameCode("", "IBC2006") && EditableTables.SameCode("", "", empty: true), "SAP2000's code names are not matched to its tables' names");
            Check(SapLateral.Setters("Seismic", "ASCE 7-22").Count == 0 && SapLateral.Setters("Seismic", "KDS 41 17 00-2019").Count == 0 && SapLateral.Setters("Wind", "AASHTO 2020").Count == 0, "codes with no Set call in SAP2000 26");
            Check(SapLateral.Setters("Seismic", "UBC97 Isolated").SequenceEqual(["SetUBC97Iso"]) && SapLateral.Setters("Wind", "Eurocode1 2005")[0] == "SetEurocode12005_1", "codes are found by SAP2000's table name");
            var none = Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"None"}""", link);
            Check(api.Log.Contains("seismic none CPM-AUTO-S") && none["set_by"]?.ToString() == "LoadPatterns.AutoSeismic.SetNone", "the code is taken off by SetNone: " + none.ToJsonString());
            Equal(1, api.PatternTableWrites, "SAP2000's patterns' table was written once, with the code's table (KDS)");
            // Another pattern with a code given by the API (0.4.46 field test: CPM-WX's ASCE 7-16, with no row in its table):
            // it goes in with a blank AutoLoad, which SAP2000 26 takes as "keep the code" (0.4.49: naming its code
            // without a row cleared it, and every other pattern was reported lost).
            const string other = "CPM-HELD";
            Run(CsiProgram.Sap2000, "set_lateral_load", $$$"""{"name":"{{{other}}}","kind":"seismic","code":"IBC2006","values":{"R":"7"}}""", link);
            api.Tables["Load Pattern Definitions - Auto Seismic - IBC2006"].Rows.Clear();
            if (!api.Table.Any(r => r[0] == other)) api.Table.Add([other, "Quake", "0"]);
            api.ListCodes = true;
            var parkedSet = Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"KDS 41 17 00-2019","values":{"R":"5"}}""", link);
            api.ListCodes = false;
            Equal("KDS 41 17 00-2019", api.SeismicCodes["CPM-AUTO-S"], "the pattern has the code, with another pattern's API code in the model");
            Equal("IBC2006", api.SeismicCodes.GetValueOrDefault(other, ""), $"{other} has its IBC2006 back: {parkedSet.ToJsonString()} | {string.Join(" | ", api.Log.TakeLast(6))}");
            Equal(1, api.Log.Count(l => l.StartsWith("IBC2006 " + other, StringComparison.Ordinal)), "kept by the import itself, with no second Set call: " + string.Join(" | ", api.Log.TakeLast(6)));
            // 0.4.51: the model was locked by the analysis before the import, and a table staged by an earlier call is kept
            // through File > Open: the import is made all the same, on an unlocked model with nothing else staged.
            int sv = 0; string[] sf = ["LoadPat", "DesignType", "SelfWtMult", "AutoLoad"], sd = ["Dead", "Dead", "1", ""];
            api.DatabaseTables.SetTableForEditingArray("Load Pattern Definitions", ref sv, ref sf, 1, ref sd);
            inner.Locked = true;
            var kept = string.Join(",", api.Table.Select(r => r[0]));
            var lockedSet = Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"KDS 41 17 00-2019","values":{"R":"4"}}""", link);
            Check(!inner.Locked && lockedSet["unlocked"]?.ToString().Contains("results are deleted", StringComparison.Ordinal) == true && lockedSet["read_back"]?["R"]?.ToString() == "4", "a locked model is unlocked for the import, and the answer says so: " + lockedSet.ToJsonString());
            Equal(kept, string.Join(",", api.Table.Select(r => r[0])), "the stale one-row patterns table was not applied: every pattern is kept");
            // SAP2000 losing patterns in that import: the model is opened again from the file saved just before.
            api.WipeOnApply = 1;
            string wiped = "";
            try { Run(CsiProgram.Sap2000, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"KDS 41 17 00-2019"}""", link); }
            catch (Exception ex) { wiped = ex.Message; }
            Check(wiped.Contains("opened again as it was saved just before (scratch.sdb)", StringComparison.Ordinal) && api.Log.Contains("open C:/models/scratch.sdb"), "a lost pattern opens the saved model again: " + wiped);
            // Every direct import call in the program is one of the guarded ones.
            var root = Path.Combine(SourceText.Root(), "src");
            var direct = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("DatabaseTables.SetTableForEditingArray(", StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f)).OrderBy(f => f).ToList();
            Equal("CsiTools.cs,EditableTables.cs,SafeTools.cs", string.Join(",", direct), "the files that import a table directly (each new one needs SAP2000's guard)");
        });
        yield return ("CSI tools: ETABS 23 gets a code load's row in the code's table even with no inputs given (0.4.34: none was read back)", () =>
        {
            const string uc = "Load Pattern Definitions - Auto Seismic - User Coefficient";
            var (link, api, inner) = Model(CsiProgram.Etabs);
            inner.ResultTables["Load Pattern Definitions"] = new(["Name", "Type", "SelfWtMult"], [["Dead", "Dead", "1"]]);
            inner.ResultTables[uc] = new(["Name"], []);
            var set = Run(CsiProgram.Etabs, "set_lateral_load", """{"name":"CPM-AUTO-S","kind":"seismic","code":"User Coefficient"}""", link);
            Check(set["read_back"] is JsonObject { Count: > 0 } && api.Tables[uc].Rows.Any(r => r[0] == "CPM-AUTO-S"), "the row is in the code's table: " + set.ToJsonString());
        });
        yield return ("CSI tools: SAP2000's load patterns table: only a number for SelfWtMult, through the API; anything else refused up front", () =>
        {
            var (link, api, inner) = Model(CsiProgram.Sap2000);
            inner.ResultTables["Load Pattern Definitions"] = new(["LoadPat", "Type", "SelfWtMult"], [["Dead", "Dead", "1"]]);
            api.PatternName = "LoadPat";
            api.Table.Add(["Wind", "Wind", "0"]);
            string Said(string json)
            {
                try { Run(CsiProgram.Sap2000, "edit_table", json, link); return ""; }
                catch (Exception ex) when (ex is ToolInputException or EtabsException) { return ex.Message; }
            }
            var before = string.Join(";", api.Table.Select(r => string.Join("|", r)));
            // 1) An add, a delete or a set of another field is refused before any save or import.
            var add = Said("""{"table":"Load Pattern Definitions","action":"add","rows":[{"LoadPat":"New","Type":"Dead","SelfWtMult":"0"}]}""");
            Check(add.Contains("add_load_pattern", StringComparison.Ordinal), "an add is refused with the tool to use: " + add);
            var delete = Said("""{"table":"Load Pattern Definitions","action":"delete","match":{"LoadPat":"Live"}}""");
            Check(delete.Contains("does not take", StringComparison.Ordinal) && delete.Contains("delete", StringComparison.Ordinal), "a delete is refused: " + delete);
            var type = Said("""{"table":"Load Pattern Definitions","action":"update","match":{"LoadPat":"Live"},"set":{"Type":"Dead"}}""");
            Check(type.Contains("only SelfWtMult", StringComparison.Ordinal), "a set of another field is refused: " + type);
            // 2) A self weight that is not a number is said to be one.
            var word = Said("""{"table":"Load Pattern Definitions","action":"update","match":{"LoadPat":"Live"},"set":{"SelfWtMult":"half"}}""");
            Check(word.Contains("must be a number", StringComparison.Ordinal), "a word for SelfWtMult: " + word);
            Equal(before, string.Join(";", api.Table.Select(r => string.Join("|", r))), "nothing refused changed the table");
            Check(!api.Log.Any(l => l.StartsWith("self weight", StringComparison.Ordinal)), "nothing refused reached the API");
            // 3) When one of several is refused, the error names the ones already changed.
            api.RefuseSelfWeight.Add("Wind");
            var part = Said("""{"table":"Load Pattern Definitions","action":"update","match":{"SelfWtMult":"0"},"set":{"SelfWtMult":"0.5"}}""");
            Check(part.Contains("Wind", StringComparison.Ordinal) && part.Contains("Already changed: Live", StringComparison.Ordinal), "the refusal names the patterns already changed: " + part);
            api.RefuseSelfWeight.Clear();
            var ok = Run(CsiProgram.Sap2000, "edit_table", """{"table":"Load Pattern Definitions","action":"update","match":{"LoadPat":"Live"},"set":{"SelfWtMult":"0.25"}}""", link);
            Check(ok["applied"]!.GetValue<bool>() && api.Table.First(r => r[0] == "Live")[2] == "0.25", "a number goes through the API: " + ok.ToJsonString());
        });
        yield return ("CSI tools: table edits update and delete rows; what changed since a starting point; checkpoints", () =>
        {
            var (link, api, inner) = Model(CsiProgram.Safe);
            inner.ResultTables["Load Pattern Definitions"] = new(["Name", "Type", "SelfWtMult"], [["Dead", "Dead", "1"]]);
            var changed = Run(CsiProgram.Safe, "edit_table", """{"table":"Load Pattern Definitions","action":"update","match":{"Name":"Live"},"set":{"SelfWtMult":"0.5"}}""", link);
            Check(changed["applied"]!.GetValue<bool>() && changed["rows_changed"]!.GetValue<int>() == 1, changed.ToJsonString());
            Equal("Dead|Dead|1;Live|Live|0.5", string.Join(";", api.Table.Select(r => string.Join("|", r))), "the row changed");
            Run(CsiProgram.Safe, "edit_table", """{"table":"Load Pattern Definitions","action":"delete","match":{"Name":"Live"}}""", link);
            Equal(1, api.Table.Count, "the row deleted");
            Throws(() => Run(CsiProgram.Safe, "edit_table", """{"table":"Load Pattern Definitions","action":"delete","match":{"Name":"Nope"}}""", link), "no row matches");
            // SAP2000 26 gives the patterns' table for editing with no rows (0.4.17, 0.4.20): the rows come from the display read.
            api.EditingEmpty = true;
            var shown = Run(CsiProgram.Safe, "edit_table", """{"table":"Load Pattern Definitions","action":"read"}""", link);
            Equal(1, shown["rows_total"]!.GetValue<int>(), "read for display: " + shown.ToJsonString());
            Check(shown["read_note"]!.GetValue<string>().Contains("display", StringComparison.Ordinal), "the reads tried are said");
            api.EditingEmpty = false;
            // A refused apply that empties the table gets the rows back and says so (0.4.22: SAP2000 lost every pattern).
            api.WipeOnApply = 1;
            var count = api.Table.Count;
            string said = "";
            try { Run(CsiProgram.Safe, "edit_table", """{"table":"Load Pattern Definitions","action":"update","match":{"Name":"Dead"},"set":{"SelfWtMult":"0.9"}}""", link); }
            catch (EtabsException ex) { said = ex.Message; }
            Check(said.Contains("were put back", StringComparison.Ordinal) && said.Contains("could not be imported", StringComparison.Ordinal), "a refused apply says the rows were put back, with its log: " + said);
            Equal(count, api.Table.Count, "the rows are back after a refused apply");
            Equal("1", api.Table[0][2], "the rows are back as they were");

            var root = Path.Combine(Path.GetTempPath(), "cpm-csi-" + Guid.NewGuid().ToString("N"));
            var before = ResultTools.SnapshotRoot;
            ResultTools.SnapshotRoot = root;
            try
            {
                var first = Run(CsiProgram.Safe, "what_changed", "{}", link);
                Equal("set", first["baseline"]!.GetValue<string>(), "the first call sets the starting point");
                inner.AddLoadPattern("BYHAND", "Other", 0);
                var then = Run(CsiProgram.Safe, "what_changed", "{}", link);
                Check(then["definitions"]!["load_patterns"]!["added"]!.ToJsonString().Contains("BYHAND"), then.ToJsonString());
            }
            finally { ResultTools.SnapshotRoot = before; if (Directory.Exists(root)) Directory.Delete(root, true); }

            var folder = Path.Combine(Path.GetTempPath(), "cpm-cp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "tower.FDB");
                File.WriteAllText(file, "v1");
                var cp = new SafeMemory(inner, program: CsiProgram.Safe) { Api = api };
                api.File.Current = file;
                var saved = ProgramTools.Chat(CsiProgram.Safe).First(t => t.Name == "save_checkpoint").Run(Args("""{"name":"before core"}"""), new FileModel(cp, () => api.File.Current));
                Check(File.Exists(Path.Combine(folder, "tower checkpoints", "before-core.FDB")), "the copy: " + saved.ToJsonString());
                Equal(file, api.File.Current, "the model is under its own name again");
                File.WriteAllText(file, "v2");
                var listed = ProgramTools.Chat(CsiProgram.Safe).First(t => t.Name == "list_checkpoints").Run(Args("{}"), new FileModel(cp, () => api.File.Current));
                Equal(1, listed["count"]!.GetValue<int>(), "listed");
                ProgramTools.Chat(CsiProgram.Safe).First(t => t.Name == "restore_checkpoint").Run(Args("""{"name":"before core"}"""), new FileModel(cp, () => api.File.Current));
                Equal("v1", File.ReadAllText(file), "the model is the checkpoint again");
                Throws(() => ProgramTools.Chat(CsiProgram.Safe).First(t => t.Name == "restore_checkpoint").Run(Args("""{"name":"nope"}"""), new FileModel(cp, () => api.File.Current)), "a checkpoint that is not there");
            }
            finally { Directory.Delete(folder, true); }
        });

        yield return ("ETABS 23 (Ahmed's PC, 2026-11-26): a model that will not open over a saved model opens from an empty one (restore_checkpoint, open_model)", () =>
        {
            var (link, api, _) = Model(CsiProgram.Etabs);
            var folder = Path.Combine(Path.GetTempPath(), "cpm-miopen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "tower.EDB");
                File.WriteAllText(file, "v1");
                api.File.Current = file;
                api.File.RefuseOverSavedModel = true;
                var fm = new FileModel(link, () => api.File.Current);
                Run(CsiProgram.Etabs, "save_checkpoint", """{"name":"cp1"}""", fm);
                File.WriteAllText(file, "v2");
                var cp = Path.Combine(folder, "tower checkpoints", "cp1.EDB");
                Run(CsiProgram.Etabs, "restore_checkpoint", """{"name":"cp1"}""", fm);
                Check(api.Log.Contains("refused " + cp) && api.Log.IndexOf("open " + cp) > api.Log.FindLastIndex(l => l.StartsWith("init ")) - 1, "opened after a new model: " + string.Join(" | ", api.Log.TakeLast(5)));
                Equal("v1", File.ReadAllText(file), "the model is the checkpoint again");
                var other = Path.Combine(folder, "other.EDB");
                File.WriteAllText(other, "x");
                Run(CsiProgram.Etabs, "open_model", System.Text.Json.JsonSerializer.Serialize(new { path = other }), fm);
                Equal(other, api.File.Current, "open_model opened it from an empty model");
                api.File.RefuseOverSavedModel = false;
            }
            finally { Directory.Delete(folder, true); }
        });

        yield return ("CSI tools: a building in one call, on the program's stories, with sections, supports, diaphragms and loads", () =>
        {
            var (link, api, inner) = Model(CsiProgram.Etabs, withModel: false);
            var answer = Run(CsiProgram.Etabs, "generate_building", """{"spans_x":[6,6],"spans_y":[5],"new_model":false}""", link);
            // The demo has three stories: 6 grid points by 3 stories of columns, 7 beams and 2 slabs a story.
            Equal(18, answer["columns"]!.GetValue<int>(), "columns: " + answer.ToJsonString());
            Equal(21, answer["beams"]!.GetValue<int>(), "beams");
            Equal(6, answer["slabs"]!.GetValue<int>(), "slabs");
            Equal(6, answer["supports"]!.GetValue<int>(), "fixed bases");
            Equal(39, inner.Frames().Count, "in the model");
            Check(inner.Frames().All(f => f.Section is "CPM-C500x500" or "CPM-B300x600"), "the sections made for it: " + string.Join(", ", inner.Frames().Select(f => f.Section).Distinct()));
            Check(api.Log.Any(l => l.StartsWith("material CPM-C30 2", StringComparison.Ordinal)), "the concrete: " + string.Join(" | ", api.Log));
            Near(4700 * Math.Sqrt(30) * 1000, api.Moduli["CPM-C30"], "E in kN/m2", 1e-6);
            Equal(6, api.AreaDiaphragm.Count, "every slab on the diaphragm");
            Near(1.5, inner.Loads.Where(l => l.Key.Contains("SDL")).Select(l => l.Value).DefaultIfEmpty(0).First(), "SDL on the slabs");
            Check(!api.Log.Any(l => l.StartsWith("new", StringComparison.Ordinal)), "the open model kept");

            var (fresh, freshApi, _) = Model(CsiProgram.Etabs, withModel: false);
            Run(CsiProgram.Etabs, "generate_building", """{"spans_x":[6,6],"spans_y":[5],"stories":3,"story_height":3.2}""", fresh);
            Check(freshApi.Log.Contains("new grid 3 3.2 3.2 3x2 6x5"), "a new model with its stories and grids: " + string.Join(" | ", freshApi.Log));

            var (safe, safeApi, safeInner) = Model(CsiProgram.Safe, withModel: false);
            var slab = Run(CsiProgram.Safe, "generate_building", """{"spans_x":[6,6],"spans_y":[5],"stories":4}""", safe);
            Equal(0, slab["columns"]!.GetValue<int>(), "SAFE: no columns: " + slab.ToJsonString());
            Equal(2, slab["slabs"]!.GetValue<int>(), "one floor");
            Equal(6, slab["supports"]!.GetValue<int>(), "point supports at the columns");
            Check(safeApi.Log.Contains("new blank"), "a blank SAFE model");
            Check(safeInner.Points().Where(p => safeInner.Restraint(p.Name).Take(3).All(r => r)).Count() == 6, "pinned under the columns");
        });
    }

    static void Throws(Action act, string what)
    {
        try { act(); }
        catch (Exception ex) when (ex is ToolInputException or EtabsException) { return; }
        throw new Exception(what + ": it went through");
    }

    static Tool Sap2000Tool(string name) => CsiProMax.Assistant.Sap2000.SapTools.All.First(t => t.Name == name);
}

/// <summary>A link whose file name is the stand-in API's current file (for the checkpoints).</summary>
sealed class FileModel(SafeMemory inner, Func<string> file) : ICsiLink
{
    public CsiProgram Program => inner.Program;
    public ModelInfo Info() => inner.Info() with { File = file() };
    public IReadOnlyList<StoryRow> Stories() => inner.Stories();
    public IReadOnlyList<FrameRow> Frames() => inner.Frames();
    public IReadOnlyList<AreaRow> Areas() => inner.Areas();
    public IReadOnlyList<PointRow> Points() => inner.Points();
    public bool[] Restraint(string point) => inner.Restraint(point);
    public IReadOnlyList<(string Kind, string Name)> Selected() => inner.Selected();
    public void ClearSelection() => inner.ClearSelection();
    public void Select(string kind, string name) => inner.Select(kind, name);
    public IReadOnlyList<string> FrameSections() => inner.FrameSections();
    public IReadOnlyList<string> AreaSections() => inner.AreaSections();
    public IReadOnlyList<string> Materials() => inner.Materials();
    public IReadOnlyList<LoadPatternRow> LoadPatterns() => inner.LoadPatterns();
    public IReadOnlyList<string> LoadCases() => inner.LoadCases();
    public IReadOnlyList<string> Combos() => inner.Combos();
    public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section) => inner.AddFrame(x1, y1, z1, x2, y2, z2, section);
    public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => inner.AddArea(corners, section);
    public void SetFrameSection(string name, string section) => inner.SetFrameSection(name, section);
    public void SetAreaSection(string name, string section) => inner.SetAreaSection(name, section);
    public void Delete(string kind, string name) => inner.Delete(kind, name);
    public void SetRestraint(string point, bool[] restraint) => inner.SetRestraint(point, restraint);
    public void AddLoadPattern(string name, string type, double selfWeight) => inner.AddLoadPattern(name, type, selfWeight);
    public void SetFrameLoad(string frame, string pattern, double value, bool replace) => inner.SetFrameLoad(frame, pattern, value, replace);
    public void SetAreaLoad(string area, string pattern, double value, bool replace) => inner.SetAreaLoad(area, pattern, value, replace);
    public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) => inner.AddCombo(name, cases);
    public IReadOnlyList<TableName> Tables() => inner.Tables();
    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => inner.ReadTable(key, cases, combos);
    public object? Raw(string what, Func<dynamic, object?> work) => inner.Raw(what, work);
    public void RunAnalysis() => inner.RunAnalysis();
    public void Save() => inner.Save();
    public void Refresh() => inner.Refresh();
}

public enum eFakeUnits { lb_in_F = 1, kN_mm_C = 5, kN_m_C = 6 }

/// <summary>
/// A stand-in for a CSI program's API (cSapModel and the objects under it), shaped like the real one: the same
/// names, ref values, return codes. It keeps every call in <see cref="Log"/> and the state the tools read back.
/// </summary>
public sealed class FakeApi
{
    public List<string> Log { get; } = [];
    public Dictionary<string, double[]> Functions { get; } = [];
    public Dictionary<string, (string[] Loads, string[] Funcs, double[] Sf)> RsLoads { get; } = [];
    public Dictionary<string, double[]> Shear { get; } = [];
    public Dictionary<string, string> AreaDiaphragm { get; } = [];
    public Dictionary<string, double> Moduli { get; } = [];
    public List<string[]> Table { get; } = [["Dead", "Dead", "1"], ["Live", "Live", "0"]];
    public string Units { get; set; } = "kN_m_C";
    private readonly MemoryEtabs _m;
    public FakeApi(MemoryEtabs m)
    {
        _m = m;
        PointObj = new(this); FrameObj = new(this); AreaObj = new(this); LoadPatterns = new(this); LoadCases = new(this); Func = new(this);
        SourceMass = new(this); Diaphragm = new(this); ConstraintDef = new(this); Results = new(this); PropMaterial = new(this); PropFrame = new(this);
        PropArea = new(this); File = new(this); GroupDef = new(this); Analyze = new(this); DesignSteel = new("AISC 360-16"); DesignConcrete = new("ACI 318-14");
        DatabaseTables = new(this); PierLabel = new(this);
    }

    public PointApi PointObj { get; }
    public FrameApi FrameObj { get; }
    public AreaApi AreaObj { get; }
    public PatternsApi LoadPatterns { get; }
    public CasesApi LoadCases { get; }
    public FuncApi Func { get; }
    public MassApi SourceMass { get; }
    public DiaphragmApi Diaphragm { get; }
    public ConstraintApi ConstraintDef { get; }
    public ResultsApi Results { get; }
    public MaterialApi PropMaterial { get; }
    public FrameSectionApi PropFrame { get; }
    public AreaSectionApi PropArea { get; }
    public FileApi File { get; }
    public GroupApi GroupDef { get; }
    public AnalyzeApi Analyze { get; }
    public DesignApi DesignSteel { get; }
    public DesignApi DesignConcrete { get; }
    public TablesApi DatabaseTables { get; }
    public LabelApi PierLabel { get; }

    public int SetPresentUnits(eFakeUnits Units) { this.Units = Units.ToString(); Log.Add("units " + Units); return 0; }
    public eFakeUnits GetPresentUnits() => Enum.Parse<eFakeUnits>(Units);
    public string GetModelFilename(bool IncludePath = true) => File.Current;
    public int SetModelIsLocked(bool LockIt) { _m.Locked = LockIt; Log.Add("locked " + LockIt); return 0; }
    public bool GetModelIsLocked() => _m.Locked;
    public int InitializeNewModel(eFakeUnits Units = eFakeUnits.kN_m_C) { Log.Add("init " + Units); File.Empty = true; return 0; }

    public sealed class PointApi(FakeApi o)
    {
        public int GetNameList(ref int NumberNames, ref string[] MyName) { NumberNames = 2; MyName = ["P1", "P2"]; return 0; }
        public int SetRestraint(string Name, ref bool[] Value, int ItemType = 0) { o.Log.Add($"restraint {Name} {string.Join(",", Value)}"); return 0; }
        public int SetConstraint(string Name, string ConstraintName, int ItemType = 0, bool Replace = true) { o.Log.Add($"joint {Name} in {ConstraintName}"); return 0; }
        public int SetGroupAssign(string Name, string GroupName, bool Remove = false, int ItemType = 0) => 1;
        public int Count() => 2;
    }

    public sealed class FrameApi(FakeApi o)
    {
        public int GetSection(string Name, ref string PropName, ref string SAuto) { PropName = "C500x500"; return 0; }
        public int SetSection(string Name, string PropName, int ItemType = 0) { o.Log.Add("SetSection"); return 0; }
        public int SetGroupAssign(string Name, string GroupName, bool Remove = false, int ItemType = 0)
        {
            if (!o._m.Frames().Any(f => f.Name == Name)) return 1;
            o.Group.Add(Name); return 0;
        }
        public int Count() => o._m.Frames().Count;
    }

    public List<string> Group { get; } = [];
    public Dictionary<string, string> Piers { get; } = [];

    public sealed class AreaApi(FakeApi o)
    {
        public int SetDiaphragm(string Name, string DiaphragmName) { o.AreaDiaphragm[Name] = DiaphragmName; return 0; }
        public int GetDiaphragm(string Name, ref string DiaphragmName) { DiaphragmName = o.AreaDiaphragm.GetValueOrDefault(Name, ""); return 0; }
        public int SetGroupAssign(string Name, string GroupName, bool Remove = false, int ItemType = 0) { o.Group.Add(Name); return 0; }
        public int SetPier(string Name, string PierName, int ItemType = 0) { o.Piers[Name] = PierName; return 0; }
        public int GetPier(string Name, ref string PierName) { PierName = o.Piers.GetValueOrDefault(Name, ""); return 0; }
    }

    public sealed class LabelApi(FakeApi o)
    {
        public int SetPier(string Name) { o.Log.Add("pier label " + Name); return 0; }
    }

    public sealed class GroupApi(FakeApi o)
    {
        public int SetGroup(string Name, int Color = -1, bool SpecifiedForSelection = true) { o.Log.Add("group " + Name); return 0; }
        public int GetAssignments(string Name, ref int NumberItems, ref int[] ObjectType, ref string[] ObjectName) { ObjectName = o.Group.ToArray(); NumberItems = ObjectName.Length; ObjectType = new int[NumberItems]; return 0; }
    }

    public Dictionary<string, string> SeismicCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public (bool Elements, bool Added, bool Loads, string[] Patterns, double[] Factors) Mass;
    public bool EditingEmpty;
    /// <summary>The next applies that refuse and leave the patterns' table empty, as SAP2000 26 did (0.4.22 field test).</summary>
    public int WipeOnApply;
    public bool ListCodes;
    /// <summary>The patterns' table's name field: "Name" in ETABS and SAFE, "LoadPat" in SAP2000.</summary>
    public string PatternName = "Name";
    /// <summary>Patterns whose SetSelfWTMultiplier is refused.</summary>
    public HashSet<string> RefuseSelfWeight { get; } = [];
    /// <summary>How many times the patterns' table was given rows to import.</summary>
    public int PatternTableWrites;

    public sealed class PatternsApi(FakeApi o)
    {
        public SeismicApi AutoSeismic { get; } = new(o);
        public WindApi AutoWind { get; } = new(o);
        public int GetAutoSeismicCode(string Name, ref string CodeName) { CodeName = o.SeismicCodes.GetValueOrDefault(Name, ""); return 0; }
        public int GetNameList(ref int NumberNames, ref string[] MyName) { MyName = o.Table.Select(r => r[0]).ToArray(); NumberNames = MyName.Length; return 0; }
        public int GetSelfWTMultiplier(string Name, ref double Value)
        {
            var row = o.Table.FirstOrDefault(r => r[0] == Name);
            if (row is null) return 1;
            Value = double.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture); return 0;
        }
        public int SetSelfWTMultiplier(string Name, double Value)
        {
            var row = o.Table.FirstOrDefault(r => r[0] == Name);
            if (row is null || o.RefuseSelfWeight.Contains(Name)) return 1;
            row[2] = Value.ToString(System.Globalization.CultureInfo.InvariantCulture); o.Log.Add($"self weight {Name} {row[2]}"); return 0;
        }
    }

    public sealed class SeismicApi(FakeApi o)
    {
        // ETABS's shape: over the stories.
        public int SetUserCoefficient(string Name, int DirFlag, double Eccen, bool OverStories, string StoryTop, string StoryBottom, double C, double K)
        {
            o.Log.Add(FormattableString.Invariant($"SetUserCoefficient {Name} dir {DirFlag} ecc {Eccen} top {StoryTop} C {C} K {K}")); return 0;
        }
        public int GetUserCoefficient(string Name, ref int DirFlag, ref double C) { C = 0.1; return 0; }
        // SAP2000 26: the code goes on by its Set call, which makes the pattern's row in the code's table; SetNone takes it off.
        public int SetIBC2006(string Name, int DirFlag, double Eccen, int PeriodFlag, int CtType, double UserT, bool UserZ, double TopZ, double BottomZ, double R, double Omega, double Cd, double I, int IBC2006Option, double Latitude, double Longitude, string ZipCode, double Ss, double S1, double Tl, int SiteClass, double Fa, double Fv)
        {
            if (R <= 0 || Ss <= 0 || SiteClass <= 0) return 1;
            o.SeismicCodes[Name] = "IBC2006";
            const string key = "Load Pattern Definitions - Auto Seismic - IBC2006";
            if (o.Tables.TryGetValue(key, out var t)) t.Rows.RemoveAll(r => r[0] == Name);
            if (o.Tables.TryGetValue(key, out t)) t.Rows.Add(t.Fields.Select(f => f == "LoadPat" ? Name : f == "R" ? R.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1").ToArray());
            o.Log.Add(FormattableString.Invariant($"IBC2006 {Name} R {R} site {SiteClass}")); _ibcR[Name] = R; return 0;
        }
        private readonly Dictionary<string, double> _ibcR = new();
        // SAP2000 26's Get call answers the values the pattern has (0.4.44 field test: its answer went into the reply twice).
        public int GetIBC2006(string Name, ref double R, ref int SiteClass) { if (!_ibcR.TryGetValue(Name, out R)) return 1; SiteClass = 4; return 0; }
        public int SetNone(string Name) { o.SeismicCodes.Remove(Name); o.Log.Add("seismic none " + Name); return 0; }
        public int SetASCE716(string Name, int DirFlag, double Eccen, int PeriodFlag, int CtType, double UserT, bool UserZ, double TopZ, double BottomZ, double R, double Omega, double Cd, double I, double Ss, double S1, double TL, int SiteClass, double Fa, double Fv)
        {
            o.Log.Add(FormattableString.Invariant($"ASCE716 {Name} dir {DirFlag} R {R} Ss {Ss} S1 {S1} site {SiteClass} Fa {Math.Round(Fa, 6)} Fv {Math.Round(Fv, 6)} TL {TL}")); return 0;
        }
        public int GetASCE716(string Name, ref double R) { R = 8; return 0; }
        // ETABS 23 and SAP2000 26: the directions as nDir (ETABS 6 long, 0-based: X, Y, X+e, Y+e, X-e, Y-e; SAP2000 X, Y).
        public int SetASCE716_1(string Name, ref bool[] nDir, double Eccen, int PeriodFlag, int CtType, double UserT, bool UserZ, double TopZ, double BottomZ, double R, double Omega, double Cd, double I, double Ss, double S1, double TL, int SiteClass, double Fa, double Fv)
        {
            if (nDir is null || !nDir.Any(d => d) || R <= 0) return 1;
            o.SeismicCodes[Name] = "ASCE 7-16";
            o.Log.Add(FormattableString.Invariant($"ASCE716_1 {Name} dirs {string.Join("", nDir.Select(d => d ? '1' : '0'))} R {R} Ss {Ss} S1 {S1} site {SiteClass} Fa {Math.Round(Fa, 6)} Fv {Math.Round(Fv, 6)} TL {TL} Z {TopZ}..{BottomZ}")); return 0;
        }
    }

    public sealed class WindApi(FakeApi o)
    {
        public int SetASCE716(string Name, int ExposureFrom, double DirAngle, double Cpw, double Cpl, bool UserZ, double TopZ, double BottomZ, double WindSpeed, int ExposureType, double Kzt, double GustFactor, double Kd, double SolidGrossRatio, bool UserExposure, double Ke)
        {
            o.Log.Add(FormattableString.Invariant($"wind {Name} speed {WindSpeed} exposure {ExposureType} angle {DirAngle}")); return 0;
        }
        public int GetASCE716(string Name, ref double WindSpeed) { WindSpeed = 110; return 0; }
    }

    public sealed class CasesApi(FakeApi o)
    {
        public RsApi ResponseSpectrum { get; } = new(o);
        public ModalApi ModalEigen { get; } = new(o);
        public int GetTypeOAPI(string Name, ref int CaseType, ref int SubType) { CaseType = Name.Contains("Modal") ? 3 : 1; return 0; }
    }

    public sealed class ModalApi(FakeApi o)
    {
        private int _max = 12;
        public int SetCase(string Name) { o.Log.Add("modal " + Name); return 0; }
        public int SetNumberModes(string Name, int MaxModes, int MinModes) { _max = MaxModes; return 0; }
        public int GetNumberModes(string Name, ref int MaxModes, ref int MinModes) { MaxModes = _max; MinModes = 1; return 0; }
    }

    public sealed class RsApi(FakeApi o)
    {
        public int SetCase(string Name) { o.RsLoads[Name] = ([], [], []); return 0; }
        public int SetModalCase(string Name, string ModalCase) => 0;
        public int SetLoads(string Name, int NumberLoads, ref string[] LoadName, ref string[] Func, ref double[] SF, ref string[] CSys, ref double[] Ang)
        {
            o.RsLoads[Name] = (LoadName, Func, SF); return 0;
        }
        public int GetLoads(string Name, ref int NumberLoads, ref string[] LoadName, ref string[] Func, ref double[] SF, ref string[] CSys, ref double[] Ang)
        {
            var (l, f, s) = o.RsLoads[Name];
            NumberLoads = l.Length; LoadName = l; Func = f; SF = s; CSys = l.Select(_ => "Global").ToArray(); Ang = new double[l.Length]; return 0;
        }
        public int SetDampConstant(string Name, double Damp) => 0;
    }

    public sealed class FuncApi(FakeApi o)
    {
        public RsFuncApi FuncRS { get; } = new(o);
        public int GetNameList(ref int NumberNames, ref string[] MyName) { MyName = o.Functions.Keys.ToArray(); NumberNames = MyName.Length; return 0; }
    }

    public sealed class RsFuncApi(FakeApi o)
    {
        public int SetUser(string Name, int NumberItems, ref double[] Period, ref double[] Value, double DampRatio) { o.Functions[Name] = Value; return 0; }
    }

    public sealed class MassApi(FakeApi o)
    {
        public int SetMassSource(string Name, bool MassFromElements, bool MassFromMasses, bool MassFromLoads, bool IsDefault, int NumberLoads, ref string[] LoadPat, ref double[] SF)
        {
            var sf = SF;
            o.Log.Add(FormattableString.Invariant($"mass {Name} elements {MassFromElements} masses {MassFromMasses} loads {MassFromLoads} default {IsDefault} {string.Join(",", LoadPat.Select((p, i) => $"{p}*{sf[i]}"))}"));
            return 0;
        }
    }

    public sealed class DiaphragmApi(FakeApi o) { public int SetDiaphragm(string Name, bool SemiRigid) { o.Log.Add("diaphragm " + Name); return 0; } }

    public sealed class ConstraintApi(FakeApi o) { public int SetDiaphragm(string Name, int Axis = 4, string CSys = "Global") { o.Log.Add($"constraint {Name} axis {Axis}"); return 0; } }

    public sealed class ResultsApi(FakeApi o)
    {
        public SetupApi Setup { get; } = new();
        public int BaseReact(ref int NumberResults, ref string[] LoadCase, ref string[] StepType, ref double[] StepNum, ref double[] FX, ref double[] FY, ref double[] FZ, ref double[] MX, ref double[] ParamMy, ref double[] MZ, ref double gx, ref double gy, ref double gz)
        {
            FX = o.Shear.GetValueOrDefault(Setup.Selected, []); FY = new double[FX.Length]; NumberResults = FX.Length; return 0;
        }
    }

    public sealed class SetupApi
    {
        public string Selected = "";
        public int DeselectAllCasesAndCombosForOutput() { Selected = ""; return 0; }
        public int SetCaseSelectedForOutput(string Name, bool Selected = true) { this.Selected = Name; return 0; }
    }

    public sealed class MaterialApi(FakeApi o)
    {
        public int SetMaterial(string Name, int MatType, int Color = -1, string Notes = "", string GUID = "") { o.Log.Add($"material {Name} {MatType}"); return 0; }
        public int SetMPIsotropic(string Name, double E, double U, double A, double Temp = 0) { o.Moduli[Name] = E; return 0; }
        public int SetWeightAndMass(string Name, int MyOption, double Value, double Temp = 0) => 0;
        public int SetOConcrete_1(string Name, double Fc, bool IsLightweight, double FcsFactor, int SSType, int SSHysType, double StrainAtFc, double StrainUltimate, double FinalSlope, double FrictionAngle = 0, double DilatationalAngle = 0, double Temp = 0) => 0;
        public int SetMassSource_1(ref bool IncludeElements, ref bool IncludeAddedMass, ref bool IncludeLoads, int NumberLoads, ref string[] LoadPat, ref double[] sf)
        {
            o.Mass = (IncludeElements, IncludeAddedMass, IncludeLoads, LoadPat, sf); return 0;
        }
        public int GetMassSource_1(ref bool IncludeElements, ref bool IncludeAddedMass, ref bool IncludeLoads, ref int NumberLoads, ref string[] LoadPat, ref double[] sf)
        {
            (IncludeElements, IncludeAddedMass, IncludeLoads, LoadPat, sf) = o.Mass; LoadPat ??= []; sf ??= []; NumberLoads = LoadPat.Length; return 0;
        }
        public int SetOSteel_1(string Name, double Fy, double Fu, double EFy, double EFu, int SSType, int SSHysType, double StrainAtHardening, double StrainAtMaxStress, double StrainAtRupture, double FinalSlope, double Temp = 0) => 0;
    }

    public sealed class FrameSectionApi(FakeApi o)
    {
        public int SetRectangle(string Name, string MatProp, double T3, double T2, int Color = -1, string Notes = "", string GUID = "") { o._m.FrameSectionNames.Add(Name); return 0; }
        public int SetISection(string Name, string MatProp, double T3, double T2, double Tf, double Tw, double T2b, double Tfb, int Color = -1, string Notes = "", string GUID = "") { o._m.FrameSectionNames.Add(Name); return 0; }
    }

    public sealed class AreaSectionApi(FakeApi o)
    {
        public int SetSlab(string Name, int SlabType, int ShellType, string MatProp, double Thickness, int color = -1, string notes = "", string GUID = "") { o._m.AreaSectionNames.Add(Name); return 0; }
    }

    public sealed class FileApi(FakeApi o)
    {
        public string Current = "";
        public int NewGridOnly(int NumberStorys, double TypicalStoryHeight, double BottomStoryHeight, int NumberLinesX, int NumberLinesY, double SpacingX, double SpacingY)
        {
            o.Log.Add(FormattableString.Invariant($"new grid {NumberStorys} {TypicalStoryHeight} {BottomStoryHeight} {NumberLinesX}x{NumberLinesY} {SpacingX}x{SpacingY}")); return 0;
        }
        public int NewBlank() { o.Log.Add("new blank"); return 0; }
        public int Save(string FileName = "")
        {
            if (Current.Length > 0 && FileName != Current) System.IO.File.Copy(Current, FileName, true);
            Current = FileName; return 0;
        }
        /// <summary>As ETABS 23 on Ahmed's PC (2026-11-26): no to a file while another saved model is open ("Error in performing miOpen").</summary>
        public bool RefuseOverSavedModel;
        public bool Empty;
        public int OpenFile(string FileName)
        {
            if (RefuseOverSavedModel && !Empty && Current.Length > 0) { o.Log.Add("refused " + FileName); return 1; }
            Current = FileName; Empty = false; o.Log.Add("open " + FileName); return 0;
        }
    }

    public sealed class AnalyzeApi(FakeApi o)
    {
        public int GetCaseStatus(ref int NumberItems, ref string[] CaseName, ref int[] Status) { CaseName = ["Dead", "Live"]; Status = [4, 1]; NumberItems = 2; return 0; }
    }

    public sealed class DesignApi(string code)
    {
        private string _code = code;
        public int GetCode(ref string CodeName) { CodeName = _code; return 0; }
        public int SetCode(string CodeName) { _code = CodeName; return 0; }
    }

    /// <summary>The other editable tables, by key: their fields and rows (the patterns' table is <see cref="Table"/>).</summary>
    public Dictionary<string, (string[] Fields, List<string[]> Rows)> Tables { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Load Pattern Definitions - Auto Seismic - User Coefficient"] = (["Name", "IsAuto", "XDir", "XDirPlusE", "XDirMinusE", "YDir", "YDirPlusE", "YDirMinusE", "EccRatio", "TopStory", "BotStory", "OverStory", "OverDiaph", "OverEcc", "C", "K"], []),
        ["Load Pattern Definitions - Auto Wind - ASCE 7-16"] = (["Name", "IsAuto", "Exposure", "TopStory", "BotStory", "Parapet", "ParapetHt", "UserCp", "UserCpw", "UserCpl", "ASCECase", "e1", "e2", "WindSpeed", "ExpType", "GrdElevFact", "kzt", "GustFact", "Kd", "SolidGross", "Set", "WidthType", "Angle", "Story", "Diaphragm", "Width", "Depth", "X", "Y"], []),
        ["Functions - Response Spectrum - User Defined"] = (["Name", "Period", "Value", "DampRatio", "GUID"], []),
    };
    private readonly Dictionary<string, (string[] Fields, List<string[]> Rows)> _pending = new(StringComparer.OrdinalIgnoreCase);

    public sealed class TablesApi(FakeApi o)
    {
        private const string Patterns = "Load Pattern Definitions";
        public int GetTableForEditingArray(string TableKey, string GroupName, ref int TableVersion, ref string[] FieldsKeysIncluded, ref int NumberRecords, ref string[] TableData)
        {
            // SAP2000 26 lists each pattern with its type and its code (the probe on Ahmed's PC, 2026-11-26).
            if (TableKey == Patterns && o.ListCodes) { FieldsKeysIncluded = ["LoadPat", "DesignType", "SelfWtMult", "AutoLoad"]; NumberRecords = o.Table.Count; TableData = o.Table.SelectMany(r => new[] { r[0], r[1], r[2], o.SeismicCodes.GetValueOrDefault(r[0], "") }).ToArray(); return 0; }
            if (TableKey == Patterns) { FieldsKeysIncluded = [o.PatternName, "Type", "SelfWtMult"]; NumberRecords = o.EditingEmpty ? 0 : o.Table.Count; TableData = o.EditingEmpty ? [] : o.Table.SelectMany(r => r).ToArray(); return 0; }
            if (!o.Tables.TryGetValue(TableKey, out var t)) return 1;
            FieldsKeysIncluded = t.Fields; NumberRecords = t.Rows.Count; TableData = t.Rows.SelectMany(r => r).ToArray(); return 0;
        }
        public int GetAllFieldsInTable(string TableKey, ref int TableVersion, ref int NumberFields, ref string[] FieldKey, ref string[] FieldName, ref string[] Description, ref string[] UnitsString, ref bool[] IsImportable)
        {
            FieldKey = TableKey == Patterns ? [o.PatternName, "IsAuto", "Type", "SelfWtMult", "AutoLoad", "GUID"] : o.Tables.TryGetValue(TableKey, out var t) ? t.Fields : [];
            NumberFields = FieldKey.Length; FieldName = FieldKey; Description = FieldKey.Select(f => "about " + f).ToArray(); UnitsString = new string[NumberFields]; IsImportable = FieldKey.Select(_ => true).ToArray(); return 0;
        }
        public int SetTableForEditingArray(string TableKey, ref int TableVersion, ref string[] FieldsKeysIncluded, int NumberRecords, ref string[] TableData)
        {
            // SAP2000 26 (experiments G and E on Ahmed's PC, 2026-11-26): a locked model refuses every table, and a table
            // already staged and not applied (kept through File > Open) refuses new rows until CancelTableEditing.
            if (o._m.Locked || o._pending.ContainsKey(TableKey)) return 1;
            if (TableKey == Patterns) o.PatternTableWrites++;
            var rows = new List<string[]>();
            for (var r = 0; r < NumberRecords; r++) rows.Add(TableData.Skip(r * FieldsKeysIncluded.Length).Take(FieldsKeysIncluded.Length).ToArray());
            o._pending[TableKey] = (FieldsKeysIncluded, rows);
            return 0;
        }
        public int GetTableForDisplayArray(string TableKey, ref string[] FieldKeyList, string GroupName, ref int TableVersion, ref string[] FieldsKeysIncluded, ref int NumberRecords, ref string[] TableData)
        {
            if (TableKey != Patterns) return 1;
            FieldsKeysIncluded = [o.PatternName, "Type", "SelfWtMult", "AutoLoad"]; NumberRecords = o.Table.Count; TableData = o.Table.SelectMany(r => r.Append("None")).ToArray(); return 0;
        }
        public int CancelTableEditing() { o._pending.Clear(); return 0; }
        public int ApplyEditedTables(bool FillImportLog, ref int NumFatalErrors, ref int NumErrorMsgs, ref int NumWarnMsgs, ref int NumInfoMsgs, ref string ImportLog)
        {
            if (o._m.Locked) { ImportLog = ""; return 1; }
            if (o.WipeOnApply > 0)
            {
                o.WipeOnApply--;
                if (o._pending.ContainsKey(Patterns)) o.Table.Clear();
                o._pending.Clear();
                NumErrorMsgs = 1; ImportLog = "Error: the patterns could not be imported"; return 1;
            }
            foreach (var (key, (fields, rows)) in o._pending)
            {
                if (key == Patterns && o.PatternName == "LoadPat" && fields.Contains("AutoLoad"))
                {
                    // SAP2000 26 with the code's table in the same import (the probe on Ahmed's PC, 2026-11-26): the
                    // pattern takes the code, and the other patterns are kept.
                    foreach (var r in rows)
                    {
                        var at = o.Table.FindIndex(t => t[0] == r[0]);
                        var self = r[Array.IndexOf(fields, "SelfWtMult")];
                        if (at < 0) o.Table.Add([r[0], r[Array.IndexOf(fields, "DesignType") is >= 0 and var dt ? dt : Array.IndexOf(fields, "Type")], self]); else o.Table[at][2] = self;
                        // SAP2000 26 (the pattern-import experiment on Ahmed's PC, 2026-11-26): a blank AutoLoad keeps the
                        // pattern's code; a code with no row for the pattern in its table (in the import or the model)
                        // clears it, as for a code set through the API.
                        var code = r[Array.IndexOf(fields, "AutoLoad")];
                        if (code.Length == 0) continue;
                        if (code.Equals("None", StringComparison.OrdinalIgnoreCase)) { o.SeismicCodes.Remove(r[0]); continue; }
                        bool Has((string[] Fields, List<string[]> Rows) t) => Array.IndexOf(t.Fields, "LoadPat") is >= 0 and var lp && t.Rows.Any(x => x[lp] == r[0]);
                        bool Of(string k) => k.Contains("Auto ", StringComparison.Ordinal) && EditableTables.Norm(k).EndsWith(EditableTables.Norm(code), StringComparison.Ordinal);
                        if (o._pending.Any(kv => Of(kv.Key) && Has(kv.Value)) || o.Tables.Any(kv => Of(kv.Key) && Has(kv.Value))) o.SeismicCodes[r[0]] = code; else o.SeismicCodes.Remove(r[0]);
                    }
                    continue;
                }
                if (key == Patterns)
                {
                    // Only the fields the patterns' table is read with are kept, as before.
                    o.Table.Clear();
                    foreach (var r in rows) o.Table.Add(new[] { o.PatternName, "Type", "SelfWtMult" }.Select(f => Array.IndexOf(fields, f) is var i and >= 0 ? r[i] : "").ToArray());
                    continue;
                }
                if (o.PatternName == "LoadPat" && key.Contains("Auto Seismic - ", StringComparison.Ordinal))
                {
                    // SAP2000 26 (0.4.40 field test): a code's table keeps no row for a pattern that does not have that code.
                    var code = key[(key.IndexOf("Auto Seismic - ", StringComparison.Ordinal) + 15)..];
                    var at = Math.Max(0, Array.IndexOf(fields, "LoadPat"));
                    var kept = rows.Where(r => o.SeismicCodes.GetValueOrDefault(r[at], "") == code).ToList();
                    var old = o.Tables.TryGetValue(key, out var had) ? had.Rows.Where(r => !kept.Any(k => k[at] == r[at])).ToList() : [];
                    o.Tables[key] = (fields, old.Concat(kept).ToList());
                    continue;
                }
                o.Tables[key] = (fields, rows);
                int name = Math.Max(0, Array.IndexOf(fields, "Name"));
                if (key.Contains("User Coefficient")) foreach (var r in rows) o.SeismicCodes[r[name]] = "User Coefficient";
                if (key.StartsWith("Functions")) foreach (var g in rows.GroupBy(r => r[name]))
                        o.Functions[g.Key] = g.Select(r => double.Parse(r[Array.IndexOf(fields, "Value")], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            }
            o._pending.Clear();
            ImportLog = "ok"; return 0;
        }
    }
}

/// <summary>The field steps of the shared tools, on a stand-in API: they run every shared tool of each program.</summary>
static class CsiFieldStepsTests
{
    public static IEnumerable<(string, Action)> All()
    {
        yield return ("CSI tools' field steps on a stand-in API: every shared tool is run", () =>
        {
            foreach (var program in CsiProgram.All)
            {
                var inner = new MemoryEtabs();
                inner.AddFrame(0, 0, 0, 0, 0, 3.2, "C500x500");
                inner.AddArea([(0, 0, 3.2), (6, 0, 3.2), (6, 5, 3.2)], "S200");
                var link = new SafeMemory(inner, program: program) { Api = new FakeApi(inner) };
                var folder = Path.Combine(Path.GetTempPath(), "cpm-cf-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var before = ResultTools.SnapshotRoot;
                ResultTools.SnapshotRoot = folder;
                try
                {
                    var ctx = new CsiProMax.Assistant.FieldContext(program, folder, link);
                    CsiProMax.Assistant.CsiFieldSteps.Run(ctx);
                    if (Environment.GetEnvironmentVariable("CPM_FIELD_DUMP") == "1")
                        foreach (var s in ctx.Steps) Console.WriteLine($"      {program.Name} {(s.Skipped ? "skip" : s.Ok ? "ok  " : "FAIL")} {s.Name}: {(s.Detail.Length > 200 ? s.Detail[..200] : s.Detail)}");
                    var missing = ctx.NotRun;
                    if (missing.Count > 0) throw new Exception($"{program.Name}: not run: {string.Join(", ", missing)}");
                }
                finally { ResultTools.SnapshotRoot = before; Directory.Delete(folder, true); }
            }
        });
    }
}
