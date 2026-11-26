using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.E2k;
using CsiProMax.Model;

/// <summary>Post-tensioning, design strips and slab design input (SlabDefs), checked against ETABS 20's and SAFE 20/23's
/// own exports of their "ACI 318-19 PT-SL Ex001" and "RC-PN Ex001" examples (samples/*-v20.e2k, *-v20.f2k, *-v23.f2k).</summary>
static class SlabTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Near(double expected, double actual, string what, double tol = 1e-6) { if (Math.Abs(expected - actual) > tol) throw new Exception($"{what}: expected {expected}, got {actual}"); }

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    static JsonObject Run(ICsiLink link, string tool, object args) =>
        (JsonObject)ExtraTools.All.Concat(PropertyTools.All).Concat(ModelerTools.All).Single(x => x.Name == tool).Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static void Refused(OfflineEtabs link, string tool, object args, string says)
    {
        var before = ModelFile.ToJson(link.Model);
        try { Run(link, tool, args); }
        catch (ToolInputException ex)
        {
            Check(ex.Message.Contains(says, StringComparison.OrdinalIgnoreCase), $"{tool}: '{says}' expected in: {ex.Message}");
            Check(ModelFile.ToJson(link.Model) == before, $"{tool}: a refusal changed the model");
            return;
        }
        throw new Exception($"{tool} took {JsonSerializer.Serialize(args)}");
    }

    /// <summary>The lines of a section of an e2k text, trimmed, blank ones left out.</summary>
    static List<string> SectionOf(string text, string heading)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var at = Array.FindIndex(lines, l => l.Trim() == "$ " + heading);
        if (at < 0) return [];
        return lines.Skip(at + 1).TakeWhile(l => !l.StartsWith('$')).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    static List<string> Headings(string text) => text.Replace("\r", "").Split('\n').Where(l => l.StartsWith("$ ") && !l.StartsWith("$ File")).Select(l => l[2..].Trim()).ToList();

    /// <summary>A two-bay concrete frame with a tendon material and section and the two prestress patterns.</summary>
    static OfflineEtabs PtFrame()
    {
        var link = new OfflineEtabs(Templates.ConcreteFrame("PT", [8, 8], [6], 2, 3.2));
        Run(link, "define_material_data", new { name = "A416Gr270", type = "Tendon", values = new { E = 1.965e8, FY = 1675000, FU = 1862000 } });
        Run(link, "define_tendon_section", new { name = "T15", material = "A416Gr270", strand_area = 0.00014 });
        link.Edit(m => { m.AddLoadPattern("PT-TRANSFER", "Prestress-Transfer"); m.AddLoadPattern("PT-FINAL", "Prestress-Final"); return 0; });
        return link;
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("slabs: ETABS 20's own PT example reads into its tendon, profile, strands, jacking, losses and strip, as ETABS shows them", () =>
        {
            var m = E2kReader.Read(Sample("etabs-PT-SL-v20.e2k"));
            var t = SlabDefs.Tendons(m).Single();
            Check(t.Name == "2" && t.Story == "Story1", "tendon 2 on Story1");
            Check(t.Plan.Count == 2 && t.Plan[0][0] == 0 && t.Plan[0][1] == 18 && t.Plan[1][0] == 384, "its plan, from the horizontal layout");
            Near(48, t.Datum, "the datum (GLOBALZ)"); Near(384, t.Length, "length");
            var sp = t.Spans.Single();
            Check(sp.Type == "Reverse Parabola" && sp.Label == "Right Cantilever", "span type and label as read");
            Near(43, sp.ZL, "ZL"); Near(39, sp.ZC, "ZC"); Near(43, sp.ZR, "ZR"); Near(0.1, sp.A, "A"); Near(0.5, sp.C, "C");
            Check(t.Section == "Tendon1" && t.Strands == 2 && !t.Bonded, "section, strands, unbonded");
            Check(t.Jack is { TransferPattern: "PT-TRANSFER", FinalPattern: "PT-FINAL", End: "J" } && t.Jack.Stress == 216000, "jacking from the J end");
            Check(t.Loss is { Method: "Fixed", Stressing: 27000, LongTerm: 13500 }, "fixed losses");
            Check(SlabDefs.TendonSectionList(m).Single() is { Name: "Tendon1", Material: "A416Gr270", StrandArea: 0.153 }, "the tendon section");
            var s = SlabDefs.Strips(m).Single();
            Check(s is { Name: "CSA1", Story: "Story1", Layer: "A", WidthLeft: 18, WidthRight: 18, DesignType: "Column", Design: true, IgnorePT: false, RebarMaterial: "A615Gr60" }, "strip CSA1 with its overwrites");
            Check(s.Plan.Count == 2 && s.Plan[1][0] == 384 && s.Plan[1][1] == 18, "strip points from the plan points");
            Check(m.LoadCases.Exists(c => c.Name == "PT-FINAL-HP" && AnalysisDefs.CaseType(c) == "Hyperstatic"), "the hyperstatic case");
        });

        yield return ("slabs: a reverse parabola passes through ZL, ZC and ZR, and is smooth where its parts meet", () =>
        {
            var sp = new SlabDefs.TendonSpan(10, 0.1, 0.1, 0.5, 0.2, 0.05, 0.2);
            Near(0.2, SlabDefs.SpanZ(sp, 0), "left end"); Near(0.05, SlabDefs.SpanZ(sp, 5), "low point"); Near(0.2, SlabDefs.SpanZ(sp, 10), "right end");
            foreach (var x in new[] { 1.0, 9.0 })
            {
                var h = 1e-5;
                var left = (SlabDefs.SpanZ(sp, x) - SlabDefs.SpanZ(sp, x - h)) / h;
                var right = (SlabDefs.SpanZ(sp, x + h) - SlabDefs.SpanZ(sp, x)) / h;
                Near(left, right, $"same slope either side of the inflection at {x}", 1e-3);
            }
            Check(SlabDefs.SpanZ(sp, 0.5) < 0.2 && SlabDefs.SpanZ(sp, 0.5) > SlabDefs.SpanZ(sp, 2), "it falls from the end to the low point");
            var simple = sp with { A = 0, B = 0 };
            Near(0.05 + 0.15 * 0.25, SlabDefs.SpanZ(simple, 2.5), "A = B = 0 is a simple parabola");
        });

        yield return ("slabs: ETABS's PT and punching examples go out again word for word through read and write", () =>
        {
            foreach (var file in new[] { "etabs-PT-SL-v20.e2k", "etabs-RC-PN-v20.e2k" })
            {
                var text = Sample(file);
                var back = E2kWriter.Write(E2kReader.Read(text));
                foreach (var h in new[] { SlabDefs.TendonSections, SlabDefs.TendonConnect, SlabDefs.TendonLayout, SlabDefs.TendonProfile, SlabDefs.StripGeometry, SlabDefs.TendonAssigns,
                             SlabDefs.TendonLoads, SlabDefs.SlabPreferences, SlabDefs.StripOverwrites, SlabDefs.FemOverwrites, SlabDefs.PunchingOverwrites })
                    Check(SectionOf(text, h).SequenceEqual(SectionOf(back, h)), $"{file}: section {h} kept word for word");
                var order = Headings(back);
                var src = Headings(text).Where(order.Contains).ToList();
                Check(src.SequenceEqual(order.Where(src.Contains)), $"{file}: the sections keep ETABS's order");
            }
        });

        yield return ("slabs: the Modeler draws a two-span tendon with jacking from both ends and percentage losses; the .e2k has it as ETABS writes it, and reads back the same", () =>
        {
            var link = PtFrame();
            var r = Run(link, "draw_tendon", new
            {
                name = "T1", story = "Story2", points = new[] { new[] { 0.0, 3.0 }, new[] { 16.0, 3.0 } },
                spans = new object[] { new { length = 8, depth_left = 0.1, depth_low = 0.16, depth_right = 0.04 }, new { length = 0, depth_left = 0.04, depth_low = 0.16, depth_right = 0.1 } },
                section = "T15", strands = 4,
                jacking = new { transfer_pattern = "PT-TRANSFER", final_pattern = "PT-FINAL", stress = 1400000, end = "Both" },
                losses = new { method = "Percent", stressing = 5, long_term = 10 },
            });
            Check((int)r["spans"]! == 2 && (double)r["length"]! == 16, "two spans over 16");
            var m = link.Model;
            var t = SlabDefs.Tendons(m).Single();
            var top = m.Elevation(m.StoryIndex("Story2"));
            Near(top - 0.1, t.Spans[0].ZL, "ZL is the slab top less the depth"); Near(top - 0.04, t.Spans[1].ZL, "continuous over the support"); Near(8, t.Spans[1].Length, "the second span takes the rest");
            Check(t.Loss is { Method: "Fixed" } && Math.Abs(t.Loss.Stressing - 70000) < 1e-6 && Math.Abs(t.Loss.LongTerm - 140000) < 1e-6, "percentages become the same fixed losses");
            Check(t.Jack!.End == "Both", "both ends");
            var text = E2kWriter.Write(m);
            var hl = SectionOf(text, SlabDefs.TendonLayout);
            Check(SectionOf(text, SlabDefs.TendonConnect).Single().StartsWith("TENDON  \"T1\"  \"Story2\"  NUMPTS  2"), "connectivity line");
            Check(hl.Count == 4 && hl[1].Contains("CURVETYPE  \"Straight\"") && hl[3].Contains("GLOBALX  16") && hl[3].Contains("GLOBALZ  " + E2kText.Number(top)), "the horizontal layout as ETABS writes it");
            Check(SectionOf(text, SlabDefs.TendonProfile).Count == 2, "a profile line per span");
            Check(SectionOf(text, SlabDefs.TendonAssigns).Single().Contains("NUMSTRANDS  4  BONDED  \"NO\""), "strands and bonding");
            var loads = SectionOf(text, SlabDefs.TendonLoads);
            Check(loads.Count == 2 && loads[0].Contains("JACKLOC  \"Both\"") && loads[1].Contains("LOSSTYPE  \"Fixed\"  LOSSFIXEDST  70000"), "jacking and losses");
            Check(SectionOf(text, SlabDefs.TendonSections).Contains("TENDONSECTION  \"T15\"  MATERIAL  \"A416Gr270\"  STRANDAREA  0.00014"), "the tendon section line");
            var h = Headings(text);
            int I(string x) => h.IndexOf(x);
            Check(I("AREA CONNECTIVITIES") < I(SlabDefs.TendonConnect) && I(SlabDefs.TendonConnect) < I(SlabDefs.TendonLayout) && I(SlabDefs.TendonLayout) < I(SlabDefs.TendonProfile)
                  && I(SlabDefs.TendonProfile) < I("POINT ASSIGNS"), "geometry sections where ETABS puts them");
            Check(I("AREA ASSIGNS") < I(SlabDefs.TendonAssigns) && I(SlabDefs.TendonAssigns) < I("LOAD PATTERNS"), "tendon assigns after area assigns");
            Check(I("SHELL OBJECT LOADS") < I(SlabDefs.TendonLoads) && I(SlabDefs.TendonLoads) < I("LOAD CASES"), "tendon loads after shell loads");
            Check(I("FRAME SECTIONS") < I(SlabDefs.TendonSections) && I(SlabDefs.TendonSections) < I("SLAB PROPERTIES"), "tendon sections before slab properties");
            var again = SlabDefs.Tendons(E2kReader.Read(text)).Single();
            Check(again.Spans.Count == 2 && again.Jack!.End == "Both" && again.Strands == 4 && Math.Abs(again.Spans[0].ZC - t.Spans[0].ZC) < 1e-9, "reads back the same");
            Check(SlabDefs.Tendons(ModelFile.FromJson(ModelFile.ToJson(m))).Single().Spans.Count == 2, "kept in the .cpm");
        });

        yield return ("slabs: draw_tendon says no to what ETABS would not take, and changes nothing", () =>
        {
            var link = PtFrame();
            object T(object? jacking = null, object? losses = null, object? spans = null, string section = "T15", string story = "Story1") => new
            {
                name = "T1", story, points = new[] { new[] { 0.0, 3.0 }, new[] { 16.0, 3.0 } }, section,
                spans = spans ?? new object[] { new { depth_left = 0.1, depth_low = 0.15, depth_right = 0.1 } }, jacking, losses,
            };
            Refused(link, "draw_tendon", T(section: "NOPE"), "no tendon section");
            Refused(link, "draw_tendon", T(story: "Base"), "above the base");
            Refused(link, "draw_tendon", T(spans: new object[] { new { depth_left = 0.1, depth_low = 0.9, depth_right = 0.1 } }), "below the slab");
            Refused(link, "draw_tendon", T(spans: new object[] { new { length = 5, depth_left = 0.1, depth_low = 0.15, depth_right = 0.1 } }), "add up");
            Refused(link, "draw_tendon", T(spans: new object[] { new { length = 8, depth_left = 0.1, depth_low = 0.15, depth_right = 0.05 }, new { length = 8, depth_left = 0.1, depth_low = 0.15, depth_right = 0.1 } }), "continuous");
            Refused(link, "draw_tendon", T(jacking: new { transfer_pattern = "Dead", final_pattern = "PT-FINAL", stress = 1e6, end = "J" }), "Prestress-Transfer");
            Refused(link, "draw_tendon", T(jacking: new { transfer_pattern = "PT-TRANSFER", final_pattern = "PT-FINAL", stress = 1e6, end = "K" }), "I, J or Both");
            Refused(link, "draw_tendon", T(losses: new { method = "Percent", stressing = 5, long_term = 5 }), "jacking");
            Refused(link, "draw_tendon", T(jacking: new { transfer_pattern = "PT-TRANSFER", final_pattern = "PT-FINAL", stress = 1e5, end = "J" }, losses: new { method = "Fixed", stressing = 6e4, long_term = 5e4 }), "jacking stress or more");
            Refused(link, "define_tendon_section", new { name = "X", material = "C30/37", strand_area = 0.0001 }, "Tendon material");
            Run(link, "draw_tendon", T());
            Refused(link, "define_tendon_section", new { name = "T15", delete = true }, "used by tendon");
            Run(link, "draw_tendon", new { name = "T1", delete = true });
            Check(SlabDefs.Tendons(link.Model).Count == 0 && !link.Model.Passthrough.Exists(p => p.Heading == SlabDefs.TendonConnect), "deleting the tendon leaves no empty sections");
        });

        yield return ("slabs: a hyperstatic case is made from a prestress case, as ETABS writes it", () =>
        {
            var link = PtFrame();
            Run(link, "define_hyperstatic_case", new { name = "PT-FINAL-HP", base_case = "PT-FINAL" });
            Check(SectionOf(E2kWriter.Write(link.Model), "LOAD CASES").Contains("LOADCASE  \"PT-FINAL-HP\"  TYPE  \"Hyperstatic\"  HYPERSTATICCASE  \"PT-FINAL\"  HYPERSTATICSUPPORT  \"Springs\"  HYPERSTATICSF  1"), "the case line");
            Refused(link, "define_hyperstatic_case", new { name = "HP2", base_case = "Dead" }, "no prestress");
            Refused(link, "define_hyperstatic_case", new { name = "Live", base_case = "PT-FINAL" }, "already");
        });

        yield return ("slabs: a design strip goes into the .e2k for ETABS with its strip overwrites, and into the .f2k for SAFE; one from an older Modeler file still counts", () =>
        {
            var link = PtFrame();
            Run(link, "define_strip", new { name = "CSA9", story = "Story1", layer = "A", points = new[] { new[] { 0.0, 3.0 }, new[] { 8.0, 3.0 }, new[] { 16.0, 3.5 } }, width_left = 1.5, width_right = 1.5, design_type = "Middle", ignore_pt = true });
            var text = E2kWriter.Write(link.Model);
            var g = SectionOf(text, SlabDefs.StripGeometry);
            Check(g.Count == 3 && g[0].Contains("NUMSEGS  2") && g[0].Contains("WALEFT  1.5") && g[2].Contains("WBLEFT  1.5"), "strip geometry lines");
            Check(SectionOf(text, SlabDefs.StripOverwrites).Single().Contains("LAYER  \"B\"") == false, "layer A");
            Check(SectionOf(text, SlabDefs.StripOverwrites).Single().Contains("DESIGNTYPE  \"Middle\"") && SectionOf(text, SlabDefs.StripOverwrites).Single().Contains("IGNOREPT  \"Yes\""), "overwrites");
            var s = SlabDefs.Strips(E2kReader.Read(text)).Single();
            Check(s is { Name: "CSA9", Story: "Story1", Layer: "A", DesignType: "Middle", IgnorePT: true } && s.Plan.Count == 3, "reads back");
            var f2k = CsiProMax.F2k.F2kWriter.Write(link.Model, new CsiProMax.F2k.F2kOptions(Story: "Story1", Strips: false));
            Check(f2k.Strips == 1 && f2k.Text.Contains("Strip=CSA9"), "in the .f2k for the strip's story");
            Check(CsiProMax.F2k.F2kWriter.Write(link.Model, new CsiProMax.F2k.F2kOptions(Story: "Story2", Strips: false)).Strips == 0, "not on another story");
            var old = new OfflineEtabs(Templates.ConcreteFrame("Old", [6], [5], 1, 3));
            old.Edit(m => ExtrasDefs.SetStrip(m, "OLD1", "B", [[0, 0], [0, 5]], 1, 1));
            Check(SlabDefs.Strips(old.Model).Single().Name == "OLD1", "a strip kept apart by an older Modeler file still counts");
            Run(old, "define_strip", new { name = "OLD1", story = "Story1", layer = "B", points = new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 5.0 } }, width_left = 1, width_right = 1 });
            Check(old.Model.Extras is null && SlabDefs.Strips(old.Model).Single().Story == "Story1", "changing it moves it into the .e2k sections");
        });

        yield return ("slabs: the review pack and the Modeler's view carry the tendons in 3D and the strips, for the Viewer to draw over the slabs", () =>
        {
            var link = PtFrame();
            Run(link, "draw_tendon", new { name = "T1", story = "Story2", points = new[] { new[] { 0.0, 3.0 }, new[] { 16.0, 3.0 } },
                spans = new object[] { new { length = 8, depth_left = 0.1, depth_low = 0.16, depth_right = 0.04 }, new { depth_left = 0.04, depth_low = 0.16, depth_right = 0.1 } }, section = "T15", strands = 4 });
            Run(link, "define_strip", new { name = "CSA1", story = "Story2", layer = "A", points = new[] { new[] { 0.0, 3.0 }, new[] { 16.0, 3.0 } }, width_left = 3, width_right = 3 });
            Run(link, "define_strip", new { name = "CSB2", story = "Story2", layer = "B", points = new[] { new[] { 8.0, 0.0 }, new[] { 8.0, 6.0 } }, width_left = 4, width_right = 4 });
            var built = Pack.Build(link, [], [], [], null, null, withTables: false);
            var model = built.Content["model"]!.AsObject();
            var t = model["tendons"]!.AsArray().Single()!.AsArray();
            Check((string)t[0]! == "T1" && (string)t[1]! == "Story2", "the tendon and its story");
            var zs = t[2]!.AsArray().Select(p => (double)p![2]!).ToList();
            var top = link.Model.Elevation(link.Model.StoryIndex("Story2"));
            Check(Math.Abs(zs.First() - (top - 0.1)) < 1e-4 && Math.Abs(zs.Last() - (top - 0.1)) < 1e-4 && Math.Abs(zs.Min() - (top - 0.16)) < 2e-3, "its 3D profile: ends and low points");
            Check(model["strips"]!.AsArray().Count == 2, "both strips");
            var out_ = Environment.GetEnvironmentVariable("CPM_VIEWER_OUT");
            if (!string.IsNullOrEmpty(out_)) File.WriteAllText(out_, Pack.ViewerHtml(built.Content));
        });
    }
}
