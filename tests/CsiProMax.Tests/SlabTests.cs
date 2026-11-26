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

        yield return ("slabs, ETABS 23 (main 3cdc64d run, 2026-11-26): a tendon goes out with its support points and its profile from the datum, or ETABS 23 drops the whole file; ETABS 20 keeps its own form", () =>
        {
            var link = PtFrame();
            Run(link, "draw_tendon", new
            {
                name = "T1", story = "Story2", points = new[] { new[] { 0.0, 3.0 }, new[] { 16.0, 3.0 } },
                spans = new object[] { new { length = 8, depth_left = 0.1, depth_low = 0.16, depth_right = 0.04 }, new { length = 0, depth_left = 0.04, depth_low = 0.16, depth_right = 0.1 } },
                section = "T15", strands = 4,
            });
            var m = link.Model;
            m.ProgramVersion = "23.2.0";
            var top = m.Elevation(m.StoryIndex("Story2"));
            var text = E2kWriter.Write(m);
            var sp = SectionOf(text, SlabDefs.TendonSupports);
            Check(sp.Count == 4 && sp[0] == "TENDONSP  \"T1\"  \"Story2\"  NUMSUPPORTS  3", "three supports: " + string.Join(" | ", sp));
            Check(sp[1].Contains("SUPPNUM  1  SUPPORTTYPE  \"None\"  RD  0  GLOBALX  0  GLOBALY  3  GLOBALZ  " + E2kText.Number(Math.Round(top - 0.1, 9))), sp[1]);
            Check(sp[2].Contains("RD  0.5  GLOBALX  8") && sp[3].Contains("RD  1  GLOBALX  16"), "the middle and far supports");
            var vp = SectionOf(text, SlabDefs.TendonProfile);
            Check(vp[0].Contains("ZL  -0.1  ZC  -0.16  ZR  -0.04"), "heights from the datum, as ETABS 23 writes them: " + vp[0]);
            var h = Headings(text);
            Check(h.IndexOf(SlabDefs.TendonLayout) < h.IndexOf(SlabDefs.TendonSupports) && h.IndexOf(SlabDefs.TendonSupports) < h.IndexOf(SlabDefs.TendonProfile), "support points between the layout and the profile");
            var back = E2kReader.Read(text);
            var t = SlabDefs.Tendons(back).Single();
            Near(top - 0.1, t.Spans[0].ZL, "read back as heights"); Near(top - 0.16, t.Spans[1].ZC, "the second span too");
            Check(SectionOf(E2kWriter.Write(back), SlabDefs.TendonSupports).Count == 4, "written once again, not twice");
            m.ProgramVersion = "20.0.0";
            var old = E2kWriter.Write(m);
            Check(SectionOf(old, SlabDefs.TendonSupports).Count == 0 && SectionOf(old, SlabDefs.TendonProfile)[0].Contains("ZL  " + E2kText.Number(Math.Round(top - 0.1, 9))), "ETABS 20: no support points, the heights themselves");
            // ETABS 23.2's own lines (its export of PT-SL Ex001, Ahmed's PC): ZL -5 from the datum at 48 is 43.
            var own = E2kReader.Read("$ PROGRAM INFORMATION\n  PROGRAM  \"ETABS\"  VERSION \"23.2.0\"\n$ STORIES - IN SEQUENCE FROM TOP\n  STORY \"Story1\"  HEIGHT 48\n  STORY \"Base\"  ELEV 0\n"
                + "$ POINT COORDINATES\n  POINT \"7\"  0 18\n  POINT \"8\"  384 18\n$ TENDON CONNECTIVITIES\n  TENDON \"2\"  \"Story1\"  NUMPTS 2  \"7\"  \"8\"    LAYER  \"OTHER\"    DATUMOFFSET -0    MINCRADIUS 36\n"
                + "$ TENDON HORIZONTAL LAYOUT\n  TENDONHL  \"2\"  \"Story1\"  NUMSEGS  1\n  TENDONHL  \"2\"  \"Story1\"  SEGMENTNUM 1  CURVETYPE \"Straight\"  NUMCURVEPTS 2\n"
                + "  TENDONHL  \"2\"  \"Story1\"  SEGMENTNUM 1  POINT 1  GLOBALX 0  GLOBALY 18  GLOBALZ 48\n  TENDONHL  \"2\"  \"Story1\"  SEGMENTNUM 1  POINT 2  GLOBALX 384  GLOBALY 18  GLOBALZ 48\n"
                + "$ TENDON SUPPORT POINTS\n  TENDONSP  \"2\"  \"Story1\"  NUMSUPPORTS 2\n  TENDONSP  \"2\"  \"Story1\"  SUPPNUM 1  SUPPORTTYPE \"None\"  RD 0  GLOBALX 0  GLOBALY 18  GLOBALZ 43\n"
                + "  TENDONSP  \"2\"  \"Story1\"  SUPPNUM 2  SUPPORTTYPE \"None\"  RD 1  GLOBALX 384  GLOBALY 18  GLOBALZ 43\n"
                + "$ TENDON VERTICAL PROFILE\n  TENDONVP  \"2\"  \"Story1\"  SPANNUM 1  SPANLABEL \"Right Cantilever\"  SPANTYPE \"Reverse Parabola\"  L 384  A 0.1  B 0.1  C 0.5  ZL -5  ZC -9  ZR -5\n$ END OF MODEL FILE\n");
            var e = SlabDefs.Tendons(own).Single();
            Near(43, e.Spans[0].ZL, "ETABS 23's ZL -5 is at 43"); Near(39, e.Spans[0].ZC, "ZC -9 is at 39");
            var again = E2kWriter.Write(own);
            Check(SectionOf(again, SlabDefs.TendonSupports).Any(l => l.Contains("SUPPNUM  2") && l.Contains("RD  1  GLOBALX  384  GLOBALY  18  GLOBALZ  43")), "its support points written again: " + string.Join(" | ", SectionOf(again, SlabDefs.TendonSupports)));
            Check(SectionOf(again, SlabDefs.TendonProfile).Single().Contains("ZL  -5  ZC  -9  ZR  -5"), "and its profile as it was");
        });

        yield return ("slabs: ETABS 23.2's own export (PT-SL Ex001 edited on Ahmed's PC: both-end jacking, detailed and percent losses, a parabola) reads, and its support points and profile go out again as it wrote them", () =>
        {
            var text = Sample("etabs23-PT-SL-edited.e2k");
            var m = E2kReader.Read(text);
            var ts = SlabDefs.Tendons(m);
            Check(ts.Count == 2 && ts.All(t => Math.Abs(t.Spans.Single().ZL - 43) < 1e-9 && Math.Abs(t.Spans.Single().ZC - 39) < 1e-9), "both tendons from 43 down to 39");
            Check(ts.All(t => t.Jack?.End == "Both"), "jacked from both ends");
            var back = E2kWriter.Write(m);
            string Flat(string l) => string.Join(" ", E2kText.Tokenize(l));
            foreach (var h in new[] { SlabDefs.TendonSupports, SlabDefs.TendonProfile, SlabDefs.TendonLayout, SlabDefs.TendonLoads })
                Check(SectionOf(text, h).Select(Flat).SequenceEqual(SectionOf(back, h).Select(Flat)), $"{h} as ETABS 23 wrote it: " + string.Join(" | ", SectionOf(back, h)));
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
            Check(f2k.Strips == 1 && f2k.Text.Contains("Name=CSA9"), "in the .f2k for the strip's story");
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

        yield return ("slabs: the .f2k is in SAFE 20's own tables and fields, each one as SAFE 20.1 or 23.1 writes it, or as the programs' table keys name it", () =>
        {
            var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in new[] { "safe-PT-SL-v20.f2k", "safe-RC-PN-v20.f2k", "safe-PT-SL-v23.f2k", "safe-RC-PN-v23.f2k" })
                foreach (var (table, rows) in CsiProMax.CsiTables.CsiTableText.Parse(Sample(file)))
                {
                    if (!seen.TryGetValue(table, out var fields)) seen[table] = fields = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in rows) fields.UnionWith(row.Keys);
                }
            // Tables the two examples do not have: their fields as "Table and Field Keys.xml" of ETABS 23.2 names them (SAFE 20+ shares them).
            var keys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["BEAM OBJECT CONNECTIVITY"] = ["Unique Name", "UniquePtI", "UniquePtJ", "Length"],
                ["WALL OBJECT CONNECTIVITY"] = ["UniqueName", "UniquePt1", "UniquePt2", "UniquePt3", "UniquePt4"],
                ["FRAME SECTION PROPERTY DEFINITIONS - CONCRETE CIRCLE"] = ["Name", "Material", "From File?", "Diameter", "Design Type"],
                ["FRAME LOADS ASSIGNMENTS - DISTRIBUTED"] = ["UniqueName", "Load Pattern", "Load Type", "Direction", "Distance Type", "Relative Distance A", "Relative Distance B", "Force A", "Force B"],
                ["JOINT LOADS ASSIGNMENTS - FORCE"] = ["UniqueName", "Load Pattern", "FX", "FY", "FZ", "MX", "MY", "MZ"],
                ["SPRING PROPERTY DEFINITIONS - AREA SPRINGS"] = ["Name", "Stiffness Option", "Stiffnes U1", "Stiffnes U2", "Stiffnes U3", "Nonlinear Option for U3"],
                ["AREA ASSIGNMENTS - AREA SPRINGS"] = ["UniqueName", "Spring Property"],
            };
            foreach (var (k, v) in keys) seen.TryAdd(k, [.. v]);
            // The fields of the material tables SAFE has, as ETABS 23.2's keys name them (the forms' design, nonlinear and rupture data).
            foreach (var (k, v) in CsiProMax.Model.ProgramTables.MaterialTables)
                if (seen.TryGetValue("MATERIAL PROPERTIES - " + k.ToUpperInvariant(), out var have)) have.UnionWith(v);
            seen["FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE"].UnionWith(["File Name", "Section in File"]);
            seen["TENDON LOAD ASSIGNMENTS - LOSS OPTIONS"].UnionWith(["Stressing Loss", "Long Term Loss"]);
            seen["STRIP OBJECT CONNECTIVITY"].UnionWith(["Segment End Point", "End Width Left", "End Width Right"]);

            // Everything the writer can write: a frame with a wall, a circle column, soil, line and point loads, strips, a two-span PT tendon.
            var link = PtFrame();
            var m = link.Model;
            Run(link, "draw_tendon", new { name = "T1", story = "Story2", points = new[] { new[] { 0.0, 3.0 }, new[] { 8.0, 3.0 }, new[] { 16.0, 3.5 } },
                spans = new object[] { new { length = 8, depth_left = 0.1, depth_low = 0.16, depth_right = 0.04 }, new { depth_left = 0.04, depth_low = 0.16, depth_right = 0.1 } }, section = "T15", strands = 4,
                jacking = new { transfer_pattern = "PT-TRANSFER", final_pattern = "PT-FINAL", stress = 1300000, end = "Both" }, losses = new { method = "Fixed", stressing = 100000, long_term = 150000 } });
            Run(link, "define_strip", new { name = "CSA1", story = "Story2", layer = "A", points = new[] { new[] { 0.0, 3.0 }, new[] { 8.0, 3.0 }, new[] { 16.0, 3.0 } }, width_left = 3, width_right = 3, design_type = "Middle" });
            Run(link, "define_hyperstatic_case", new { name = "PTF-HP", base_case = "PT-FINAL" });
            Run(link, "define_spring", new { kind = "area", name = "SOIL-K", values = new { U3 = 20000 } });
            var floor = m.AreaAssigns.First(x => m.Area(x.Label)!.Kind == AreaKind.Floor && x.Story == "Story2");
            Run(link, "assign_shell_data", new { objects = new[] { floor.Label + "@Story2" }, spring = "SOIL-K" });
            var beam = link.Frames().First(f => f.Kind == "beam" && f.Story == "Story2").Name;
            link.SetFrameLoad(beam, "Dead", 5, false);
            m.PointLoads.Add(new PointLoad { Object = m.PointAssigns.FirstOrDefault()?.Point ?? m.Points[0].Name, Story = "Story2", Tokens = ["TYPE", "\"FORCE\"", "LC", "\"Live\"", "FZ", "-10"] });
            var r = CsiProMax.F2k.F2kWriter.Write(m, new CsiProMax.F2k.F2kOptions(Story: "Story2"));
            var t = CsiProMax.CsiTables.CsiTableText.Parse(r.Text);
            foreach (var (table, rows) in t)
            {
                Check(seen.TryGetValue(table, out var fields), $"SAFE 20 has no table '{table}'");
                var unknown = rows.SelectMany(x => x.Keys).Distinct().Where(f => !fields!.Contains(f)).ToList();
                Check(unknown.Count == 0, $"'{table}': SAFE 20 has no field {string.Join(", ", unknown)}");
            }
            foreach (var table in new[] { "TENDON OBJECT CONNECTIVITY", "TENDON ASSIGNMENTS - PROPERTIES", "TENDON LOAD ASSIGNMENTS - JACKING STRESS", "TENDON LOAD ASSIGNMENTS - LOSS OPTIONS",
                "TENDON SECTION PROPERTIES", "LOAD CASE DEFINITIONS - HYPERSTATIC", "STRIP OBJECT CONNECTIVITY", "CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED", "SPRING PROPERTY DEFINITIONS - AREA SPRINGS",
                "FRAME LOADS ASSIGNMENTS - DISTRIBUTED", "JOINT LOADS ASSIGNMENTS - FORCE", "MATERIAL PROPERTIES - TENDON DATA" })
                Check(t.ContainsKey(table), $"the file has '{table}'");
            Check(r.Tendons == 1, "the tendon is counted");
            var tendon = t["TENDON OBJECT CONNECTIVITY"];
            Check(tendon[0]["Number Points"] == "3" && tendon[0]["Number Vertical Spans"] == "2" && tendon.Any(x => x.GetValueOrDefault("Span Number") == "2"), "three points and two spans");
            Check(t["TENDON LOAD ASSIGNMENTS - JACKING STRESS"].Single()["Jacking Location"] == "Both Ends", "jacked from both ends");
            Check(t["STRIP OBJECT CONNECTIVITY"].Count(x => x["Name"] == "CSA1") == 2 && t["CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED"].Single(x => x["Strip Name"] == "CSA1")["Strip Design Type"] == "Middle Strip", "the strip of two segments and its overwrites");
            Check(!r.Text.Contains("OBJECT GEOMETRY", StringComparison.Ordinal) && !r.Text.Contains("16.0.0", StringComparison.Ordinal), "nothing of SAFE 16's format is left");
        });

        yield return ("slabs: ETABS 20's PT example written as a .f2k gives the tendon, its profile, jacking, losses, strip and hyperstatic case that SAFE 20 itself exports for it", () =>
        {
            var m = E2kReader.Read(Sample("etabs-PT-SL-v20.e2k"));
            var r = CsiProMax.F2k.F2kWriter.Write(m, new CsiProMax.F2k.F2kOptions(Story: "Story1", Strips: false));
            var ours = CsiProMax.CsiTables.CsiTableText.Parse(r.Text);
            var safe = CsiProMax.CsiTables.CsiTableText.Parse(Sample("safe-PT-SL-v20.f2k"));
            void Same(string table, string field, Func<Dictionary<string, string>, bool>? which = null)
            {
                var a = ours[table].First(x => which?.Invoke(x) ?? true);
                var b = safe[table].First(x => which?.Invoke(x) ?? true);
                var (va, vb) = (a.GetValueOrDefault(field), b.GetValueOrDefault(field));
                if (double.TryParse(va, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var na) && double.TryParse(vb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nb))
                    Near(nb, na, $"{table} {field}", 1e-6);
                else Check(va == vb, $"{table} {field}: SAFE writes '{vb}', ours '{va}'");
            }
            foreach (var f in new[] { "Number Points", "Horizontal Curve Type", "Global X", "Global Y", "Number Vertical Spans", "Datum Offset", "Span ID", "Span Profile", "L", "A", "B", "C", "ZL", "ZC", "ZR" })
                Same("TENDON OBJECT CONNECTIVITY", f);
            Same("TENDON OBJECT CONNECTIVITY", "Global X", x => x.GetValueOrDefault("Curve Point Number") == "2");
            foreach (var f in new[] { "Tendon Property", "Number Strands", "Bonding Options" }) Same("TENDON ASSIGNMENTS - PROPERTIES", f);
            foreach (var f in new[] { "Transfer Load Pattern", "Final Load Pattern", "Jacking Stress", "Jacking Location" }) Same("TENDON LOAD ASSIGNMENTS - JACKING STRESS", f);
            foreach (var f in new[] { "Loss Method", "Stressing Loss Fixed", "Long Term Loss Fixed" }) Same("TENDON LOAD ASSIGNMENTS - LOSS OPTIONS", f);
            foreach (var f in new[] { "Material", "StrandArea" }) Same("TENDON SECTION PROPERTIES", f);
            foreach (var f in new[] { "Base Case", "Support Type", "Relative SF" }) Same("LOAD CASE DEFINITIONS - HYPERSTATIC", f);
            foreach (var f in new[] { "Number Segments", "Start Width Left", "Start Width Right", "End Width Left", "End Width Right", "Auto Widen", "Layer" }) Same("STRIP OBJECT CONNECTIVITY", f);
            foreach (var f in new[] { "Strip Layer", "Strip Design Type", "Design?", "Ignote PT?", "Rebar Material", "Cover Specification Type", "LLRF" }) Same("CONCRETE SLAB DESIGN OVERWRITES - STRIP BASED", f);
            foreach (var f in new[] { "Type", "Self Weight Multiplier" }) Same("LOAD PATTERN DEFINITIONS", f, x => x["Name"] == "PT-FINAL");
            Same("PROGRAM CONTROL", "ConcSlbCode");
            Same("GRID DEFINITIONS - GENERAL", "Model Datum");
            Check(ours["LOAD COMBINATION DEFINITIONS"].Count(x => x["Name"] == "Ultimate") == 3, "the combination and its three cases");
            Check(ours["CONCRETE SLAB DESIGN LOAD COMBINATION DATA"].Select(x => x["Combo Type"] + ":" + x["Combo Name"]).OrderBy(x => x).SequenceEqual(
                safe["CONCRETE SLAB DESIGN LOAD COMBINATION DATA"].Select(x => x["Combo Type"] + ":" + x["Combo Name"]).OrderBy(x => x)), "the slab design combinations with their kinds");
            var points = ours["POINT OBJECT CONNECTIVITY"].ToDictionary(x => x["UniqueName"]);
            var strip = ours["STRIP OBJECT CONNECTIVITY"].Single();
            Check(points[strip["Strip Start Point"]]["Y"] == "18" && points[strip["Segment End Point"]]["X"] == "384", "the strip runs between its point objects");

            // and SAFE 20 and 21, which cannot open the file through their API, rebuild it from the same tables
            var rows = CsiProMax.Assistant.Safe.F2kRebuild.StripPoints(ours);
            Check(rows.Count == 2 && rows[1]["GlobalX"] == "384" && rows[0]["WBLeft"] == "18", "the strip's points for SAFE's strip table");
        });
    }
}
