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

    /// <summary>Two e2k lines say the same: the same words, and the same numbers however written (8.3E-06 is 0.0000083).</summary>
    static bool SameLine(string a, string b)
    {
        var x = E2kText.Tokenize(a); var y = E2kText.Tokenize(b);
        return x.Count == y.Count && x.Zip(y).All(p => p.First == p.Second
            || (double.TryParse(p.First, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var u)
                && double.TryParse(p.Second, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && Math.Abs(u - v) <= 1e-12 * Math.Max(1, Math.Abs(u))));
    }

    static List<string> Tokens(string line) => E2kText.Tokenize(line);

    static bool SameLines(List<string> a, List<string> b) => a.Count == b.Count && a.Zip(b).All(p => SameLine(p.First, p.Second));

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
            foreach (var file in new[] { "etabs-PT-SL-v20.e2k", "etabs-RC-PN-v20.e2k", "etabs-PT-SL-v23-edited.e2k" })
            {
                var text = Sample(file);
                var back = E2kWriter.Write(E2kReader.Read(text));
                foreach (var h in new[] { SlabDefs.TendonSections, SlabDefs.TendonConnect, SlabDefs.TendonLayout, SlabDefs.TendonProfile, SlabDefs.StripGeometry, SlabDefs.TendonAssigns,
                             SlabDefs.TendonLoads, SlabDefs.SlabPreferences, SlabDefs.StripOverwrites, SlabDefs.FemOverwrites, SlabDefs.PunchingOverwrites, SlabDefs.PunchingPerimeter,
                             "TENDON SUPPORT POINTS" })
                    // ETABS 23.2 spaces its words one way, ETABS 20 another: the 20 files go out byte for byte, the 23 one word for word.
                    Check(file.Contains("v23") ? SameLines(SectionOf(text, h), SectionOf(back, h)) : SectionOf(text, h).SequenceEqual(SectionOf(back, h)), $"{file}: section {h} kept word for word: {string.Join(" / ", SectionOf(text, h).Except(SectionOf(back, h)))} became {string.Join(" / ", SectionOf(back, h).Except(SectionOf(text, h)))}");
                // Sections the Modeler writes from its own model: the same words and numbers, spaced its own way.
                foreach (var h in new[] { "SLAB PROPERTIES", "AREA CONNECTIVITIES", "AREA ASSIGNS", "LINE CONNECTIVITIES" })
                    Check(SameLines(SectionOf(text, h), SectionOf(back, h)), $"{file}: section {h} kept: {string.Join(" / ", SectionOf(text, h))} became {string.Join(" / ", SectionOf(back, h))}");
                var order = Headings(back);
                var src = Headings(text).Where(order.Contains).ToList();
                Check(src.SequenceEqual(order.Where(src.Contains)), $"{file}: the sections keep ETABS's order");
            }
        });

        yield return ("slabs: ETABS 23.2's export with detailed and percent losses, both-end jacking, a parabola, a user punching perimeter with stud rails, an opening and a ribbed slab reads as ETABS has it", () =>
        {
            var m = E2kReader.Read(Sample("etabs-PT-SL-v23-edited.e2k"));
            var t = SlabDefs.Tendons(m);
            var t1 = t.Single(x => x.Name == "2"); var t2 = t.Single(x => x.Name == "T2");
            Check(t1.Jack is { End: "Both" } && t2.Jack is { End: "Both" }, "jacking from both ends");
            Check(t1.Loss is { Method: "Detailed" } l && Math.Abs(l.Curvature - 0.07) < 1e-9 && Math.Abs(l.Wobble - 8.3e-6) < 1e-12 && Math.Abs(l.AnchorSlip - 0.25) < 1e-9
                  && l.ElasticShortening == 1000 && l.Creep == 7000 && l.Shrinkage == 6000 && l.Relaxation == 3000, "the detailed losses");
            Check(t2.Loss is { Method: "Percent", Stressing: 5, LongTerm: 10 }, "the percent losses");
            Check(t2.Spans.Single().Type == "Parabola" && t1.Spans.Single().Type == "Reverse Parabola", "the span types");
            var p = SlabDefs.PunchingList(m).Single();
            Check(p is { Point: "17", Story: "Story1", Check: "Program Determined", Location: "Interior", Reinforcement: "Stud Rails", Pattern: "Radial", Fy: 60000, Diameter: 0.375, Spacing: 3 }, "the punching overwrite");
            Check(p.Perimeter is { Count: 4 } c && c[0].SequenceEqual(new[] { -12.0, -12, 0 }) && c[2].SequenceEqual(new[] { 12.0, 12, 0 }), "the user perimeter's corners");
            var o = SlabDefs.Openings(m).Single();
            Check(o is { Name: "A1", Story: "Story1" } && o.Plan.Count == 4 && o.Plan[0].SequenceEqual(new[] { 100.0, 20 }) && o.Plan[2].SequenceEqual(new[] { 124.0, 32 }), "the opening");
            var rib = m.ShellSections.Single(x => x.Name == "RIB16");
            Check(rib.Number("OVERALLDEPTH") == 16 && rib.Number("SLABRIBWIDTHTOP") == 6 && rib.Number("SLABRIBWIDTHBOTTOM") == 5 && rib.Number("SLABRIBSPACING") == 36, "the ribbed slab");

            // The Modeler's forms make the same lines from the same input, so what ETABS reads is what it wrote.
            var link = new OfflineEtabs(m);
            Run(link, "draw_tendon", new { name = "T9", story = "Story1", points = new[] { new[] { 0.0, 24 }, new[] { 384.0, 24 } },
                spans = new object[] { new { length = 384, depth_left = 5, depth_low = 9, depth_right = 5, a = 0, b = 0 } }, section = "Tendon1", strands = 2,
                jacking = new { transfer_pattern = "PT-TRANSFER", final_pattern = "PT-FINAL", stress = 216000, end = "Both" },
                losses = new { method = "Detailed", curvature = 0.07, wobble = 8.3e-6, anchor_slip = 0.25, elastic_shortening = 1000, creep = 7000, shrinkage = 6000, relaxation = 3000 } });
            var text = E2kWriter.Write(m);
            var loads = SectionOf(text, SlabDefs.TendonLoads);
            Check(SameLine(loads.Single(x => x.StartsWith("TENDONLOAD  \"T9\"") && x.Contains("Losses")).Replace("\"T9\"", "\"2\""), loads.Single(x => x.StartsWith("TENDONLOAD  \"2\"") && x.Contains("Losses"))),
                "the Modeler's detailed losses line is ETABS 23.2's");
            Run(link, "set_punching_overwrite", new { point = "17", story = "Story1", location = "Interior", perimeter = new[] { new[] { -12.0, -12 }, new[] { 12.0, -12 }, new[] { 12.0, 12 }, new[] { -12.0, 12 } },
                reinforcement = new { type = "Stud Rails", pattern = "Radial", fy = 60000, diameter = 0.375, spacing = 3 } });
            var src = Sample("etabs-PT-SL-v23-edited.e2k");
            var again = E2kWriter.Write(m);
            Check(SectionOf(again, SlabDefs.PunchingOverwrites)is var po && SameLines(po, SectionOf(src, SlabDefs.PunchingOverwrites)), "the Modeler's punching line is ETABS 23.2's");
            Check(SameLines(SectionOf(again, SlabDefs.PunchingPerimeter), SectionOf(src, SlabDefs.PunchingPerimeter)), "the Modeler's perimeter lines are ETABS 23.2's");
            Run(link, "draw_opening", new { name = "A1", story = "Story1", points = new[] { new[] { 100.0, 20 }, new[] { 124.0, 20 }, new[] { 124.0, 32 }, new[] { 100.0, 32 } } });
            again = E2kWriter.Write(m);
            Check(SameLines(SectionOf(again, "AREA CONNECTIVITIES"), SectionOf(src, "AREA CONNECTIVITIES")), "the Modeler's opening is ETABS 23.2's area line");
            Check(SectionOf(again, "AREA ASSIGNS").Count(x => x.Contains("OPENING")) == 1 && SameLine(SectionOf(again, "AREA ASSIGNS").Single(x => x.Contains("OPENING")), SectionOf(src, "AREA ASSIGNS").Single(x => x.Contains("OPENING"))), "and its assignment");
            Run(link, "define_shell_type", new { kind = "slab", name = "RIB16", slab_type = "Ribbed", modeling = "ShellThin", thickness = 4, material = "4000Psi", overall_depth = 16, rib_width_top = 6, rib_width_bottom = 5, rib_spacing = 36, rib_direction = 1 });
            again = E2kWriter.Write(m);
            Check(SameLine(SectionOf(again, "SLAB PROPERTIES").Single(x => x.Contains("RIB16")), SectionOf(src, "SLAB PROPERTIES").Single(x => x.Contains("RIB16"))), "the Modeler's ribbed slab is ETABS 23.2's line");
            Refused(link, "set_punching_overwrite", new { point = "17", story = "Story1", reinforcement = new { type = "Ties" } }, "yield stress");
            Refused(link, "draw_opening", new { name = "A9", story = "Story1", points = new[] { new[] { 0.0, 0 }, new[] { 1.0, 1 } } }, "3 corners");
            Refused(link, "define_shell_type", new { kind = "slab", name = "RIB2", slab_type = "Ribbed", thickness = 4, material = "4000Psi" }, "ribs");
            Refused(link, "draw_tendon", new { name = "T8", story = "Story1", points = new[] { new[] { 0.0, 30 }, new[] { 384.0, 30 } }, spans = new object[] { new { length = 384, depth_left = 5, depth_low = 9, depth_right = 5 } },
                section = "Tendon1", jacking = new { transfer_pattern = "PT-TRANSFER", final_pattern = "PT-FINAL", stress = 216000 }, losses = new { method = "Percent", stressing = 60, long_term = 50 } }, "100");

            // The .f2k: SAFE's own tables for the punching overwrite, its perimeter, the opening, the ribs and the losses.
            m.AreaAssigns.Single(a => a.Label == "F5").Section = "RIB16"; // SAFE gets the slab properties its slabs use
            var f = CsiProMax.CsiTables.CsiTableText.Parse(CsiProMax.F2k.F2kWriter.Write(m, new CsiProMax.F2k.F2kOptions(Story: "Story1")).Text);
            var g = f["CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - GENERAL"].Single();
            Check(g["Location Type"] == "Interior" && g["Perimeter Type"] == "User Perimeter" && g["Reinforcement Type"] == "Stud Rails"
                  && g["Reinforcement Diameter"] == "60000" && g["Reinforcement Fy"] == "0.375", $"the punching row ({string.Join(", ", g.Select(kv => kv.Key + "=" + kv.Value))}), fy and diameter under the names SAFE's keys give them");
            Check(f["CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - PERIMETER"].Count == 4, "the perimeter's 4 corners");
            var op = f["NULL AREA OBJECT CONNECTIVITY"].Single()["Unique Name"];
            Check(op.Length > 0, "the opening");
            Check(f["AREA ASSIGNMENTS - SECTION PROPERTIES"].Any(x => x["UniqueName"] == op && x["Section Property"] == "Opening"), "the opening's assignment");
            var slab = f["SLAB PROPERTY DEFINITIONS"].SingleOrDefault(x => x["Name"] == "RIB16") ?? throw new Exception("no RIB16 in " + string.Join(" / ", f["SLAB PROPERTY DEFINITIONS"].Select(x => x["Name"])));
            Check(slab["Overall Depth"] == "16" && slab["Rib Width at Top"] == "6" && slab["Rib Width at Bottom"] == "5" && slab["Rib Spacing 1"] == "36", "the ribs");
            var loss = f["TENDON LOAD ASSIGNMENTS - LOSS OPTIONS"];
            Check(loss.Any(x => x["Loss Method"] == "Detailed" && x["Curvature Coefficient"] == "0.07") && loss.Any(x => x["Loss Method"] == "Percent" && x["Stressing Loss"] == "5"), "detailed and percent losses: " + string.Join(" / ", loss.Select(x => string.Join(",", x.Values))));
        });

        yield return ("slabs, ETABS 23.2 (the J export, 2026-11-26): a slab line release sits on a null line ETABS writes as LINE ... LINE, has no .e2k keyword of its own, and goes into the .f2k and the program's table; slab rebar too", () =>
        {
            var src = Sample("etabs-PT-SL-v23-edited.e2k");
            var m = E2kReader.Read(src);
            var l1 = SlabDefs.NullLines(m).Single();
            Check(l1 is { Name: "L1", Story: "Story1" } && l1.I.SequenceEqual(new[] { 288.0, 0 }) && l1.J.SequenceEqual(new[] { 288.0, 36 }), "ETABS's null line reads");
            Check(!CsiProMax.Checks.ModelChecks.Run(m).Any(f => f.Object == "L1" || f.Object == "A1"), "check_model takes ETABS's null line and opening as they are");
            var link = new OfflineEtabs(m);
            Run(link, "set_slab_line_release", new { name = "L2", story = "Story1", points = new[] { new[] { 96.0, 0 }, new[] { 96.0, 36 } }, shear_left = true, moment_left = true });
            var text = E2kWriter.Write(m);
            var line = SectionOf(text, "LINE CONNECTIVITIES").Single(x => x.Contains("\"L2\""));
            Check(SameLine(line.Replace("\"L2\"", "\"L1\""), SectionOf(src, "LINE CONNECTIVITIES").Single(x => x.Contains("\"L1\"")).Replace("\"15\"", Tokens(line)[3]).Replace("\"16\"", Tokens(line)[4])), "the null line as ETABS 23.2 writes it: " + line);
            Check(SectionOf(text, "LINE ASSIGNS").Any(x => x.StartsWith("LINEASSIGN  \"L2\"  \"Story1\"") && x.Contains("SECTION  \"NONE\"")), "its assignment, section NONE");
            Check(!text.Contains("RELEASE", StringComparison.OrdinalIgnoreCase) || SectionOf(src, "LINE ASSIGNS").Any(x => x.Contains("RELEASE")), "no line release keyword is made up: ETABS writes none");
            var r = SlabDefs.LineReleases(m).Single();
            Check(r is { Line: "L2", Story: "Story1", ShearLeft: true, MomentLeft: true, ShearRight: false, MomentRight: false }, "the release is kept");
            var back = CsiProMax.Model.ModelFile.FromJson(CsiProMax.Model.ModelFile.ToJson(m));
            Check(SlabDefs.LineReleases(back).Single() == r, "and comes back with the Modeler's own file");
            Refused(link, "set_slab_line_release", new { name = "NOPE" }, "null line");
            Refused(link, "set_slab_line_release", new { name = "L2", shear_left = false }, "at least one");

            Run(link, "define_material_data", new { name = "Gr60", type = "Rebar", values = new { E = 29000, FY = 60, FU = 90 } });
            Refused(link, "draw_slab_rebar", new { name = "SR1", story = "Story1", from = new[] { 24.0, 6 }, to = new[] { 360.0, 6 }, width_left = 6, bar_size = "#99", material = "Gr60", total_bars = 4 }, "bar size");
            Run(link, "draw_slab_rebar", new { name = "SR1", story = "Story1", from = new[] { 24.0, 6 }, to = new[] { 360.0, 6 }, width_left = 6, width_right = 6, vertical_offset = 1.5, layer = "A", bar_size = "#5", material = "Gr60", total_bars = 4 });
            Refused(link, "draw_slab_rebar", new { name = "SR2", story = "Story1", from = new[] { 24.0, 6 }, to = new[] { 360.0, 6 }, width_left = 6, bar_size = "#5", material = "Gr60" }, "not both");
            Check(SlabDefs.SlabRebars(back = CsiProMax.Model.ModelFile.FromJson(CsiProMax.Model.ModelFile.ToJson(m))).Single() is { Name: "SR1", TotalBars: 4, BarSize: "#5", VerticalOffset: 1.5 }, "the rebar comes back with the Modeler's own file");
            Check(E2kWriter.Write(m) == E2kWriter.Write(back), "the .e2k is the same either way");

            // The .f2k: SAFE's tables for the null line, the release and the rebar, in the fields the keys name.
            var f = CsiProMax.CsiTables.CsiTableText.Parse(CsiProMax.F2k.F2kWriter.Write(m, new CsiProMax.F2k.F2kOptions(Story: "Story1")).Text);
            var nl = f["NULL LINE OBJECT CONNECTIVITY"];
            Check(nl.Count == 2, "both null lines");
            var rel = f["AREA ASSIGNMENTS - SLAB LINE RELEASES"].Single();
            Check(nl.Any(x => x["Unique Name"] == rel["UniqueName"]) && rel["Shear Left"] == "Yes" && rel["Moment Left"] == "Yes" && rel["Shear Right"] == "No" && rel["Moment Right"] == "No", "the release on its null line");
            var geo = f["SLAB REBAR OBJECT GEOMETRY"].Single(); var bars = f["SLAB REBAR PROPERTY ASSIGNMENTS"].Single();
            Check(geo["X1"] == "24" && geo["X2"] == "360" && geo["Vertical Offset"] == "1.5" && geo["Width Left"] == "6" && geo["Layer"] == "A", "the rebar's geometry");
            Check(bars["Rebar Size"] == "#5" && bars["Material"] == "Gr60" && bars["Bar Number Type"] == "Total Bars" && bars["Total Bars"] == "4", "and its bars");
            var keys = File.ReadAllLines("/mnt/project-files/field/table-keys/etabs-23.2-field-names.txt") is var lines && lines.Length > 0 ? lines : [];
            foreach (var (table, fields) in new[] { ("Null Line Object Connectivity", new[] { "Unique Name", "UniquePtI", "UniquePtJ" }), ("Area Assignments - Slab Line Releases", new[] { "UniqueName", "Shear Left", "Moment Left", "Shear Right", "Moment Right" }),
                         ("Slab Rebar Object Geometry", new[] { "Name", "X1", "Y1", "X2", "Y2", "Vertical Offset", "Width Left", "Width Right", "Layer" }), ("Slab Rebar Property Assignments", new[] { "Name", "Rebar Size", "Material", "Bar Number Type", "Total Bars", "Max Spacing" }) })
                foreach (var row in f[table.ToUpperInvariant()])
                    Check(row.Keys.All(fields.Contains), $"'{table}' has only the fields the keys name: {string.Join(", ", row.Keys)}");

            // A run in ETABS or SAFE: the release goes into the program's table on the line found where the Modeler drew it, and the rebar into its two tables.
            var (live, api, inner) = CsiToolsTests.Model(CsiProgram.Etabs);
            var z = inner.Stories()[1].Elevation;
            var mm = new StructuralModel();
            mm.Stories.AddRange([new Story { Name = "Base" }, new Story { Name = inner.Stories()[1].Name, Height = z }]);
            var name = inner.AddFrame(3, 0, z, 3, 5, z, "None");
            SlabDefs.SetNullLine(mm, "N1", mm.Stories[1].Name, [3, 0], [3, 5]);
            SlabDefs.SetLineRelease(mm, new SlabDefs.LineRelease("N1", "", false, true, false, true));
            mm.Materials.Add(new Material { Name = "R", Lines = [["TYPE", "\"Rebar\""]] });
            api.Tables[SlabDefs.LineReleaseTable] = (["UniqueName", "ShearL", "MomentL", "ShearR", "MomentR"], []);
            api.Tables[SlabDefs.SlabRebarGeometryTable] = (["Name", "Story", "X1", "Y1", "X2", "Y2", "VertOffset", "WidthLeft", "WidthRight", "Layer"], []);
            api.Tables[SlabDefs.SlabRebarPropertyTable] = (["Name", "RebarSize", "Material", "BarNumType", "TotalBars", "MaxSpacing"], []);
            SlabDefs.SetSlabRebar(mm, new SlabDefs.SlabRebar("B1", mm.Stories[1].Name, [0, 1], [6, 1], 0.5, 0.5, 0.03, "A", "16", "R", 0, 0.2));
            var said = SlabTables.PutInto(live, mm);
            Check(said.Count == 2 && said[0].Contains("went into") && said[1].Contains("went into"), "both go in: " + string.Join(" | ", said));
            Check(api.Tables[SlabDefs.LineReleaseTable].Rows.Single().SequenceEqual(new[] { name, "No", "Yes", "No", "Yes" }), "the release row names the program's line: " + string.Join(",", api.Tables[SlabDefs.LineReleaseTable].Rows.Single()));
            Check(api.Tables[SlabDefs.SlabRebarPropertyTable].Rows.Single()[3] == "Max Spacing", "the rebar by its spacing");
            Check(SlabTables.PutInto(new SafeMemory(inner, program: CsiProgram.Sap2000), mm).Count == 0, "SAP2000 has no slab line releases or slab rebar");
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
            Check(t.Loss is { Method: "Percent" } && Math.Abs(t.Loss.Stressing - 5) < 1e-6 && Math.Abs(t.Loss.LongTerm - 10) < 1e-6, "percentages stay percentages, as ETABS 23.2 keeps them");
            Check(t.Jack!.End == "Both", "both ends");
            var text = E2kWriter.Write(m);
            var hl = SectionOf(text, SlabDefs.TendonLayout);
            Check(SectionOf(text, SlabDefs.TendonConnect).Single().StartsWith("TENDON  \"T1\"  \"Story2\"  NUMPTS  2"), "connectivity line");
            Check(hl.Count == 4 && hl[1].Contains("CURVETYPE  \"Straight\"") && hl[3].Contains("GLOBALX  16") && hl[3].Contains("GLOBALZ  " + E2kText.Number(top)), "the horizontal layout as ETABS writes it");
            Check(SectionOf(text, SlabDefs.TendonProfile).Count == 2, "a profile line per span");
            Check(SectionOf(text, SlabDefs.TendonAssigns).Single().Contains("NUMSTRANDS  4  BONDED  \"NO\""), "strands and bonding");
            var loads = SectionOf(text, SlabDefs.TendonLoads);
            Check(loads.Count == 2 && loads[0].Contains("JACKLOC  \"Both\"") && loads[1].Contains("LOSSTYPE  \"Percent\"  LOSSPERCENTST  5  LOSSPERCENTLT  10"), "jacking and losses");
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
                ["NULL AREA OBJECT CONNECTIVITY"] = ["Unique Name", "UniquePt1", "UniquePt2", "UniquePt3", "UniquePt4"],
                ["CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - PERIMETER"] = ["Unique Name", "Point Number", "X Coord", "Y Coord", "Radius", "Is Null?"],
            };
            foreach (var (k, v) in keys) seen.TryAdd(k, [.. v]);
            // The fields of the material tables SAFE has, as ETABS 23.2's keys name them (the forms' design, nonlinear and rupture data).
            foreach (var (k, v) in CsiProMax.Model.ProgramTables.MaterialTables)
                if (seen.TryGetValue("MATERIAL PROPERTIES - " + k.ToUpperInvariant(), out var have)) have.UnionWith(v);
            seen["FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE"].UnionWith(["File Name", "Section in File"]);
            seen["TENDON LOAD ASSIGNMENTS - LOSS OPTIONS"].UnionWith(["Stressing Loss", "Long Term Loss", "Curvature Coefficient", "Wobble Coefficient", "Anchorage Set Slip", "Shortening Stress", "Creep Stress", "Shrinkage Stress", "Relaxation Stress"]);
            seen["CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - GENERAL"].UnionWith(["Reinforcement Pattern", "Reinforcement Fy", "Reinforcement Diameter", "Reinforcement Spacing"]);
            seen["SLAB PROPERTY DEFINITIONS"].UnionWith(["Overall Depth", "Rib Width at Top", "Rib Width at Bottom", "Rib Spacing 1", "Rib Spacing 2"]);
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
            Run(link, "set_punching_overwrite", new { point = m.Points[0].Name, story = "Story2", location = "Edge", perimeter = new[] { new[] { -0.3, -0.3 }, new[] { 0.3, -0.3 }, new[] { 0.3, 0.3, 0.05 } },
                reinforcement = new { type = "Stud Rails", pattern = "Orthogonal", fy = 420000, diameter = 0.012, spacing = 0.1 } });
            Run(link, "draw_opening", new { name = "OP1", story = "Story2", points = new[] { new[] { 1.0, 1 }, new[] { 2.0, 1 }, new[] { 2.0, 2 }, new[] { 1.0, 2 } } });
            Run(link, "define_shell_type", new { kind = "slab", name = "RIB-X", slab_type = "Ribbed", thickness = 0.1, material = m.Materials.First(x => x.Type.Equals("Concrete", StringComparison.OrdinalIgnoreCase)).Name,
                overall_depth = 0.4, rib_width_top = 0.15, rib_width_bottom = 0.12, rib_spacing = 0.8, rib_direction = 2 });
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
                "FRAME LOADS ASSIGNMENTS - DISTRIBUTED", "JOINT LOADS ASSIGNMENTS - FORCE", "MATERIAL PROPERTIES - TENDON DATA", "NULL AREA OBJECT CONNECTIVITY",
                "CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - GENERAL", "CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - PERIMETER" })
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
