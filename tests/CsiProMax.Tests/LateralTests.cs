using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Lateral;
using CsiProMax.Model;

/// <summary>
/// Seismic and wind loads by code in the Modeler (LateralCodes, LateralLoads, LateralTools). Ahmed: the definition of
/// wind and seismic "should cover all options as csi programs gives them", worked around where the API does not allow it.
/// </summary>
static class LateralTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Near(double expected, double actual, string what, double tol = 1e-6)
    {
        if (Math.Abs(expected - actual) > tol * Math.Max(1, Math.Abs(expected))) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static OfflineEtabs Frame() => new(Templates.ConcreteFrame("Lateral", [6, 5], [4], 3, 3.2));

    static JsonObject Do(ICsiLink link, string tool, object args) =>
        HandModel.Handle(link, JsonDocument.Parse(JsonSerializer.Serialize(new { tool, args })).RootElement);

    static JsonObject Undo(ICsiLink link) => HandModel.Handle(link, JsonDocument.Parse("""{"undo":true}""").RootElement);

    static JsonObject Set(OfflineEtabs link, string name, string code, object values, string kind = "seismic")
    {
        var tool = LateralTools.All.Single(t => t.Name == "set_lateral_load");
        return (JsonObject)tool.Run(JsonDocument.Parse(JsonSerializer.Serialize(new { name, kind, code, values })).RootElement, link);
    }

    static double Sum(StructuralModel m, string pattern, string key) =>
        m.PointLoads.Where(l => l.Pattern == pattern).Sum(l => E2kText.TryNumber(E2kText.Value(l.Tokens, key), out var v) ? v : 0);

    static double Torsion(StructuralModel m, string pattern, string story, double x0, double y0) =>
        m.PointLoads.Where(l => l.Pattern == pattern && l.Story == story).Sum(l =>
        {
            var p = m.Point(l.Object)!;
            E2kText.TryNumber(E2kText.Value(l.Tokens, "FX"), out var fx); E2kText.TryNumber(E2kText.Value(l.Tokens, "FY"), out var fy);
            return (p.X - x0) * fy - (p.Y - y0) * fx;
        });

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("lateral: a user coefficient puts V = C W on the stories, spread by w h, as joint loads", () =>
        {
            var link = Frame();
            var answer = Set(link, "EQX", "User Coefficient", new { Dir = "X", C = "0.1", K = "1" });
            var m = link.Model;
            var def = m.LoadPatterns.Single(p => p.Name == "EQX").Auto!;
            Check(def.Error is null, "computed: " + def.Error);
            var w = def.Results["W"];
            Check(w > 0, "the frame has weight");
            Near(0.1 * w, Sum(m, "EQX", "FX"), "the joint loads add up to V", 1e-4);
            Near(0, Sum(m, "EQX", "FY"), "nothing across");
            var f = def.Forces;
            Check(f.Count == 3, "three stories");
            var wh = f.Select(s => s.Weight * (s.Elevation - m.Elevation(0))).ToArray();
            Near(0.1 * w * wh[2] / wh.Sum(), f[2].Fx, "the top story takes w h / Σ w h", 1e-4);
            Check(m.LoadPatterns.Single(p => p.Name == "EQX").Type == "Seismic", "made as a Seismic pattern");
            Check(answer["story_forces"] is JsonArray { Count: 3 }, "the answer gives the story forces");
            // The .e2k carries them as joint loads ETABS reads.
            var back = E2kReader.Read(E2kWriter.Write(m));
            Near(Sum(m, "EQX", "FX"), Sum(back, "EQX", "FX"), "the forces survive the .e2k", 1e-5);
        });

        yield return ("lateral: ASCE 7-16 gives Cs by 12.8 (SDS/(R/Ie), SD1/(T R/Ie), the minimums) and k from T", () =>
        {
            var link = Frame();
            Set(link, "EQX", "ASCE 7-16", new { Dir = "X", R = "8", I = "1", Ss = "1.5", S1 = "0.6", SiteClass = "D" });
            var r = link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results;
            var sds = 2.0 / 3 * 1.0 * 1.5; var sd1 = 2.0 / 3 * 1.7 * 0.6;
            var t = 0.0466 * Math.Pow(9.6, 0.9);
            var cs = Math.Max(Math.Max(Math.Min(sds / 8, sd1 / (t * 8)), 0.044 * sds), 0.5 * 0.6 / 8);
            Near(sds, r["SDS"], "SDS", 1e-5); Near(sd1, r["SD1"], "SD1", 1e-5); Near(t, r["T"], "Ta = Ct hn^x", 1e-5);
            Near(cs, r["Cs"], "Cs", 1e-5); Near(1, r["k"], "k is 1 under 0.5 s", 1e-9);
            Near(cs * r["W"], r["V"], "V = Cs W", 1e-5);
        });

        yield return ("lateral: X+Ecc moves each story force by 5% of the plan size: same shear, torsion -Fx e Ly", () =>
        {
            var link = Frame();
            Set(link, "EQ0", "User Coefficient", new { Dir = "X", C = "0.1" });
            Set(link, "EQE", "User Coefficient", new { Dir = "X+Ecc", C = "0.1", Ecc = "0.05" });
            var m = link.Model;
            Near(Sum(m, "EQ0", "FX"), Sum(m, "EQE", "FX"), "the same base shear", 1e-5);
            var top = m.LoadPatterns.Single(p => p.Name == "EQE").Auto!.Forces[^1];
            Near(-top.Fx * 0.05 * 4, top.Mz, "the torsion about the mass center", 1e-4);
            // The joint loads' moment about the mass center is that torsion (the frame's mass center is its middle).
            var plain = Torsion(m, "EQ0", top.Story, 5.5, 2);
            var moved = Torsion(m, "EQE", top.Story, 5.5, 2);
            Near(top.Mz, moved - plain, "the joints carry the torsion", 1e-4);
        });

        yield return ("lateral: the forces follow the model: a heavier floor gives a larger V at once", () =>
        {
            var link = Frame();
            Set(link, "EQX", "User Coefficient", new { Dir = "X", C = "0.1" });
            var before = link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results["V"];
            // Count the dead loads as mass, then load a floor: the shear is worked out again.
            link.Edit(m => { new MassSource("MsSrc1", true, true, [("Dead", 1.0)]).WriteTo(m); return 0; });
            var floor = link.Model.AreaAssigns.First(a => link.Model.Area(a.Label)!.Kind == AreaKind.Floor);
            link.Edit(m => { m.AreaLoads.Add(new AreaLoad { Object = floor.Label, Story = floor.Story, Tokens = ["TYPE", "\"UNIFF\"", "DIR", "\"GRAV\"", "LC", "\"Dead\"", "FVAL", "50"] }); return 0; });
            var after = link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results["V"];
            Check(after > before, $"V went from {before} to {after}");
            Near(0.1 * link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results["W"], Sum(link.Model, "EQX", "FX"), "the joint loads follow", 1e-4);
        });

        yield return ("lateral: ASCE 7-16 wind at 90 degrees pushes in Y only, more at the top, windward plus leeward", () =>
        {
            var link = Frame();
            Set(link, "WY", "ASCE 7-16", new { Angle = "90", Speed = "115", SpeedUnit = "mph", Exposure = "C" }, "wind");
            var m = link.Model;
            var def = m.LoadPatterns.Single(p => p.Name == "WY").Auto!;
            Check(def.Error is null, "computed: " + def.Error);
            Near(0, Sum(m, "WY", "FX"), "nothing in X", 1e-9);
            Check(Sum(m, "WY", "FY") > 0, "a push in Y");
            Near(def.Results["V"], Sum(m, "WY", "FY"), "the joint loads add up to the wind", 1e-5);
            Near(11, def.Results["Width"], "the width across the wind is the X size", 1e-9);
            Check(m.LoadPatterns.Single(p => p.Name == "WY").Type == "Wind", "a Wind pattern");
            var f = def.Forces;
            Check(f[0].Fy / 3.2 < f[1].Fy / 3.2 + 1e-9, "the pressure grows with height");
        });

        yield return ("lateral: every code the Modeler lists computes on a frame with its defaults", () =>
        {
            foreach (var code in LateralCodes.All)
            {
                var link = Frame();
                // ASCE 7-22 has no Fa and Fv tables: SDS and SD1 are the USGS geodatabase's, so they are given.
                object values = code.Name == "ASCE 7-22" && code.Kind == LateralCodes.Seismic ? new { SDS = "1.0", SD1 = "0.6" } : new { };
                Set(link, "L", code.Name, values, code.Kind.ToLowerInvariant());
                var def = link.Model.LoadPatterns.Single(p => p.Name == "L").Auto!;
                Check(def.Error is null, $"{code.Kind} {code.Name}: {def.Error}");
                if (code.Family != "user-loads") Check(def.Results.GetValueOrDefault("V") > 0, $"{code.Kind} {code.Name} gives a base force");
            }
            Check(LateralCodes.Of("Seismic").Count() >= 25 && LateralCodes.Of("Wind").Count() >= 12, "the CSI programs' codes are listed");
        });

        yield return ("lateral: Fa and Fv come from the code's own ASCE 7 edition (7-05/7-10 for IBC 2006 to 2015, 7-16 after, none in 7-22)", () =>
        {
            Check(SiteCoefficients.Edition("ASCE 7-10") == SiteCoefficients.Table.Asce710 && SiteCoefficients.Edition("IBC 2015") == SiteCoefficients.Table.Asce710
                && SiteCoefficients.Edition("ASCE 7-05") == SiteCoefficients.Table.Asce710, "7-05, 7-10 and IBC 2015 take the 7-10 tables");
            Check(SiteCoefficients.Edition("ASCE 7-16") == SiteCoefficients.Table.Asce716 && SiteCoefficients.Edition("IBC 2018") == SiteCoefficients.Table.Asce716
                && SiteCoefficients.Edition("SBC 301-2018") == SiteCoefficients.Table.Asce716, "7-16, IBC 2018 and SBC 301-2018 take the 7-16 tables");
            Check(SiteCoefficients.Edition("ASCE 7-22") == SiteCoefficients.Table.None, "7-22 has none");
            // Table 11.4-1: site C at Ss 0.5 is 1.2 in 7-10, 1.3 in 7-16; site B is 1.0 in 7-10, 0.9 in 7-16.
            Near(1.2, SiteCoefficients.Fa("ASCE 7-10", "C", 0.5), "7-10 Fa C at 0.5"); Near(1.3, SiteCoefficients.Fa("ASCE 7-16", "C", 0.5), "7-16 Fa C at 0.5");
            Near(1.0, SiteCoefficients.Fa("IBC 2012", "B", 1.0), "7-10 Fa B"); Near(0.9, SiteCoefficients.Fa("IBC 2021", "B", 1.0), "7-16 Fa B");
            // Table 11.4-2: site D at S1 0.6 takes the last column: 1.5 in 7-10 (S1 >= 0.5), 1.7 in 7-16.
            Near(1.5, SiteCoefficients.Fv("ASCE 7-10", "D", 0.6), "7-10 Fv D"); Near(1.7, SiteCoefficients.Fv("ASCE 7-16", "D", 0.6), "7-16 Fv D");
            Near(3.35, SiteCoefficients.Fv("ASCE 7-05", "E", 0.15), "7-05 Fv E halfway from 3.5 to 3.2");
            static bool Refuses(Action a, string says) { try { a(); return false; } catch (ArgumentException ex) { return ex.Message.Contains(says, StringComparison.Ordinal); } }
            Check(Refuses(() => SiteCoefficients.Fa("ASCE 7-22", "D", 1.0), "USGS"), "7-22 asks for SDS and SD1");
            Check(Refuses(() => SiteCoefficients.Fa("ASCE 7-16", "E", 1.0), "site-specific"), "7-16 site E above Ss 0.75 asks for a site-specific value");
            Check(Refuses(() => SiteCoefficients.Fa("ASCE 7-10", "F", 1.0), "site-specific"), "site F asks for a site-specific value");

            var link = Frame();
            Set(link, "EQX", "ASCE 7-10", new { Dir = "X", Ss = "1.5", S1 = "0.6", SiteClass = "D" });
            var r = link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results;
            Near(1.0, r["Fa"], "ASCE 7-10 Fa"); Near(1.5, r["Fv"], "ASCE 7-10 Fv (1.7 is 7-16's)"); Near(1.0, r["SDS"], "SDS"); Near(0.6, r["SD1"], "SD1");
        });

        yield return ("lateral: ASCE 7-22 takes SDS and SD1 as given and asks for them when blank", () =>
        {
            var link = Frame();
            Set(link, "EQX", "ASCE 7-22", new { Dir = "X" });
            var def = link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!;
            Check(def.Error is { } e && e.Contains("USGS", StringComparison.Ordinal), "blank SDS asks for the USGS values: " + def.Error);
            Set(link, "EQX", "ASCE 7-22", new { Dir = "X", SDS = "1.2", SD1 = "0.7", SiteClass = "CD" });
            var r = link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results;
            Near(1.2, r["SDS"], "SDS as given"); Near(0.7, r["SD1"], "SD1 as given");
            Check(!r.ContainsKey("Fa") && !r.ContainsKey("Fv"), "no Fa or Fv in 7-22");
            Check(LateralCodes.Find(LateralCodes.Seismic, "ASCE 7-22")!.Params.All(p => p.Key != "Ss"), "7-22 has no Ss input");
        });

        yield return ("lateral: NSCP 2015 is the UBC 97 procedure, NBCC's eccentricity is 0.10 and its 2015/2020 cap takes S(0.5) when larger", () =>
        {
            Check(LateralCodes.Find(LateralCodes.Seismic, "NSCP 2015")!.Family == "ubc97", "NSCP 2015 follows UBC 97");
            Check(LateralCodes.Find(LateralCodes.Seismic, "NBCC 2015")!.Params.Single(p => p.Key == "Ecc").Default == "0.10", "NBCC 4.1.8.11 (10): 0.10 Dnx");
            Check(LateralCodes.Find(LateralCodes.Seismic, "ASCE 7-16")!.Params.Single(p => p.Key == "Ecc").Default == "0.05", "ASCE 7 12.8.4.2: 0.05");
            // S(0.5) = 0.55 is above 2/3 S(0.2) = 0.4, and S(Ta) is above both: 2015 caps at 0.55, 2010 at 0.4.
            var values = new { Dir = "X", Sa02 = "0.6", Sa05 = "0.55", Sa10 = "0.3", Sa20 = "0.15", Sa50 = "0.05", Sa100 = "0.02", Mv = "1", IE = "1", Rd = "4", Ro = "1.7" };
            var link = Frame();
            Set(link, "E15", "NBCC 2015", values);
            Set(link, "E10", "NBCC 2010", values);
            var r15 = link.Model.LoadPatterns.Single(p => p.Name == "E15").Auto!.Results;
            var r10 = link.Model.LoadPatterns.Single(p => p.Name == "E10").Auto!.Results;
            Check(r15["S(T)"] > 0.55, "S(Ta) is above the caps: " + r15["S(T)"]);
            Near(0.55 / 6.8, r15["Cs"], "NBCC 2015: the larger of 2/3 S(0.2) and S(0.5)", 1e-6);
            Near(0.4 / 6.8, r10["Cs"], "NBCC 2010: 2/3 S(0.2)", 1e-6);
        });

        yield return ("lateral: IS 1893:2016's static method takes Sa/g = 2.5 below 0.1 s (the ramp is the spectrum's only)", () =>
        {
            var link = Frame();
            Set(link, "EQX", "IS 1893:2016", new { Dir = "X", Period = "User", UserT = "0.05" });
            var r = link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results;
            Near(2.5, r["Sa/g"], "the plateau at T = 0.05 s");
        });

        yield return ("lateral: Eurocode 1 wind takes the reference height ze of figure 7.4 (h > 2b: b at the bottom, h at the top)", () =>
        {
            // The frame is 11 m along X and 4 m across: the wind along X sees b = 4 m, h = 9.6 m > 2b.
            var link = Frame();
            Set(link, "WX", "Eurocode 1 2005", new { Angle = "0", Speed = "28", SpeedUnit = "m/s", Terrain = "II" }, "wind");
            var def = link.Model.LoadPatterns.Single(p => p.Name == "WX").Auto!;
            Check(def.Error is null, "computed: " + def.Error);
            Near(4, def.Results["Width"], "the width across the wind", 1e-9);
            var f = def.Forces;
            // Story 2 (6.4 m, above h - b = 5.6 m) and the roof (9.6 m) both take qp(h); story 2's share of height is twice the roof's.
            Near(2 * f[2].Fx, f[1].Fx, "the upper strip is at qp(h)", 1e-6);
            Check(f[0].Fx < f[1].Fx, "the lower strip is at qp(b), below qp(h)");
        });

        yield return ("lateral: the template's mass is the Dead pattern (self weight and superimposed dead), counted once", () =>
        {
            var ms = MassSource.Of(Templates.ConcreteFrame("T", [6, 5], [4], 3, 3.2));
            Check(!ms.Elements && ms.Loads.SequenceEqual([("Dead", 1.0)]), "the mass source is Dead x 1 without the elements");
            var with = Frame();
            var without = new OfflineEtabs(Templates.ConcreteFrame("Lateral", [6, 5], [4], 3, 3.2, sdl: 0));
            Set(with, "EQX", "User Coefficient", new { Dir = "X", C = "0.1" });
            Set(without, "EQX", "User Coefficient", new { Dir = "X", C = "0.1" });
            var w1 = with.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results["W"];
            var w0 = without.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto!.Results["W"];
            // 2 kN/m² on 11 m x 4 m on three floors, and the self weight once in each.
            Near(2.0 * 11 * 4 * 3, w1 - w0, "W takes the superimposed dead load", 1e-4);
        });

        yield return ("lateral: new concrete sections and materials take their covers, bars and unit weight in the model's units", () =>
        {
            var m = ModelEdit.New("mm");
            m.Units.Length = "MM";
            m.AddRectangularSection("B300x600", ModelEdit.DefaultConcrete, 600, 300, beam: true);
            m.AddRectangularSection("C500x500", ModelEdit.DefaultConcrete, 500, 500, beam: false);
            var lines = m.Passthrough.Find(x => x.Heading == "CONCRETE SECTIONS")!.Lines;
            double Value(string section, string key)
            {
                var t = E2kText.Tokenize(lines.Last(l => l.Contains($"\"{section}\"", StringComparison.Ordinal)));
                return E2kText.TryNumber(E2kText.Value(t, key), out var v) ? v : double.NaN;
            }
            Near(60, Value("B300x600", "COVERTOP"), "a 60 mm beam cover"); Near(40, Value("C500x500", "COVER"), "a 40 mm column cover");
            Near(314.2, Value("C500x500", "LONGBARAREA"), "the bar area in mm²", 1e-6); Near(150, Value("C500x500", "CONFINEBARSPACING"), "the tie spacing in mm");
            Near(2.5e-8, m.ConcreteUnitWeight(), "25 kN/m³ in kN/mm³", 1e-9);
            var si = ModelEdit.New("m");
            Near(25, si.ConcreteUnitWeight(), "25 kN/m³ in kN/m³");
        });

        yield return ("lateral: user loads go on the stories named, with Mz", () =>
        {
            var link = Frame();
            Set(link, "EQU", "User Loads", new { Loads = "Story1 10 0 0; Story3 30 5 2" });
            var f = link.Model.LoadPatterns.Single(p => p.Name == "EQU").Auto!.Forces;
            Near(10, f[0].Fx, "story 1"); Near(30, f[2].Fx, "story 3"); Near(5, f[2].Fy, "story 3 Fy"); Near(2, f[2].Mz, "story 3 Mz", 1e-6);
            Near(40, Sum(link.Model, "EQU", "FX"), "all of it on the joints", 1e-6);
        });

        yield return ("lateral: wrong inputs are refused with what to give, and leave the model as it was", () =>
        {
            var link = Frame();
            var json = ModelFile.ToJson(link.Model);
            foreach (var (code, values, says) in new (string, object, string)[]
            {
                ("ASCE 7-99", new { }, "codes"), ("ASCE 7-16", new { R = "eight" }, "number"), ("ASCE 7-16", new { SiteClass = "Z" }, "one of"),
                ("ASCE 7-16", new { TopStory = "Story9" }, "no story"), ("ASCE 7-16", new { Colour = "red" }, "no input"),
            })
            {
                try { Set(link, "EQ", code, values); throw new Exception($"{code} {JsonSerializer.Serialize(values)} was taken"); }
                catch (ToolInputException ex) { Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), ex.Message); }
            }
            link.AddLoadPattern("SDL", "Super Dead", 0);
            try { Set(link, "SDL", "ASCE 7-16", new { }); throw new Exception("a Super Dead pattern took a seismic code"); }
            catch (ToolInputException ex) { Check(ex.Message.Contains("Super Dead"), ex.Message); }
            Check(!link.Model.LoadPatterns.Any(p => p.Auto is not null), "nothing was kept");
        });

        yield return ("lateral: the code load is kept in the model file, and None takes it off with its forces", () =>
        {
            var link = Frame();
            Set(link, "EQX", "Eurocode 8 2004", new { ag = "0.3", Ground = "B", q = "3.9" });
            var back = ModelFile.FromJson(ModelFile.ToJson(link.Model));
            var def = back.LoadPatterns.Single(p => p.Name == "EQX").Auto!;
            Check(def.Code == "Eurocode 8 2004" && def.Get("ag") == "0.3", "the code and its inputs are in the .cpm");
            Set(link, "EQX", "None", new { });
            Check(link.Model.LoadPatterns.Single(p => p.Name == "EQX").Auto is null && !link.Model.PointLoads.Any(l => l.Pattern == "EQX"), "taken off");
        });

        yield return ("lateral: the Modeler's page sets a code load and undo takes it back", () =>
        {
            var link = Frame();
            var before = ModelFile.ToJson(link.Model);
            var done = Do(link, "set_lateral_load", new { name = "EQY", code = "IS 1893:2016", values = new { Dir = "Y" } });
            Check(done["ok"]?.GetValue<bool>() != false, "done: " + done.ToJsonString());
            Check(Sum(link.Model, "EQY", "FY") > 0, "the forces are on");
            Check(Undo(link)["ok"]?.GetValue<bool>() == true, "undone");
            Check(ModelFile.ToJson(link.Model) == before, "undo gives the model back as it was");
        });

        yield return ("lateral: lateral_codes lists each code's inputs and the stories to choose from", () =>
        {
            var link = Frame();
            var tool = LateralTools.All.Single(t => t.Name == "lateral_codes");
            var list = (JsonObject)tool.Run(JsonDocument.Parse("""{"kind":"wind"}""").RootElement, link);
            Check(list["codes"]!.AsArray().Any(c => c!["code"]!.GetValue<string>() == "Eurocode 1 2005"), "Eurocode 1 is a wind code");
            var one = (JsonObject)tool.Run(JsonDocument.Parse("""{"code":"asce 7-16"}""").RootElement, link);
            var top = one["inputs"]!.AsArray().First(i => i!["key"]!.GetValue<string>() == "TopStory")!;
            Check(top["choices"]!.AsArray().Count == 4, "the stories are the choices");
        });
    }
}
