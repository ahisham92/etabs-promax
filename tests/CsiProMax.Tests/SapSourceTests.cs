using System.Text.RegularExpressions;
using CsiProMax.CsiTables;
using CsiProMax.Model;
using CsiProMax.S2k;

/// <summary>A SAP2000 file opened in CSI ProMax and written again keeps every table, row and field (SourceTables).</summary>
static class SapSourceTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    /// <summary>A file as SAP2000 itself writes one: its own joint and frame names ("J7", "305"), fields CSI ProMax has no
    /// place for (S33, S22, GUIDs, notes), a column drawn top to bottom, and a table no form holds.</summary>
    static string AsSap2000Writes(string text)
    {
        var table = "";
        var frames = new Dictionary<string, string>();
        string F(string n) => frames.TryGetValue(n, out var f) ? f : frames[n] = (301 + frames.Count).ToString();
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw;
            if (line.StartsWith("TABLE:")) table = line[6..].Trim().Trim('"');
            else if (line.StartsWith("   "))
            {
                var jointTable = table.StartsWith("JOINT") || table.StartsWith("CONNECTIVITY") || table.StartsWith("CONSTRAINT")
                    || table.StartsWith("GROUPS 2") && line.Contains("ObjectType=Joint");
                if (jointTable) line = Regex.Replace(line, @"\b(Joint\w*|ObjectLabel)=(\d+)\b", "$1=J$2");
                if (table.StartsWith("FRAME") || table == "CONNECTIVITY - FRAME") line = Regex.Replace(line, @"\bFrame=(\S+)", m => "Frame=" + F(m.Groups[1].Value));
                if (table.StartsWith("GROUPS 2") && line.Contains("ObjectType=Frame")) line = Regex.Replace(line, @"\bObjectLabel=(\S+)", m => "ObjectLabel=" + F(m.Groups[1].Value));
                if (table == "FRAME SECTION PROPERTIES 01 - GENERAL") line += "   S33=0.0123456789   S22=0.004567   GUID=1b2c-77   Notes=\"from the steel catalogue\"";
                if (table == "CONNECTIVITY - FRAME") line += "   Length=3.25   GUID=f-" + lines.Count;
                if (table == "JOINT COORDINATES") line += "   GUID=j-" + lines.Count;
            }
            if (line.StartsWith("END TABLE DATA"))
            {
                lines.Add("TABLE:  \"OVERWRITES - CONCRETE DESIGN 1 - ACI 318-19\"");
                lines.Add("   Frame=301   DesignSect=C1   DesignType=Column   RLLF=0.8   XLMajor=0");
                lines.Add("");
            }
            lines.Add(line);
        }
        return string.Join("\r\n", lines);
    }

    /// <summary>Every difference of two files' tables: a table gone or added, a row count, a field's value.</summary>
    static List<string> Differences(string a, string b)
    {
        var ta = CsiTableText.Parse(a); var tb = CsiTableText.Parse(b);
        var out_ = new List<string>();
        foreach (var (name, rows) in ta)
        {
            if (!tb.TryGetValue(name, out var other)) { out_.Add($"{name}: gone"); continue; }
            if (rows.Count != other.Count) { out_.Add($"{name}: {other.Count} rows, not {rows.Count}"); continue; }
            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var (k, v) in rows[i])
                    if (!other[i].TryGetValue(k, out var w) || w != v) out_.Add($"{name} row {i + 1} {k}: '{w}', not '{v}'");
                foreach (var k in other[i].Keys.Where(k => !rows[i].ContainsKey(k))) out_.Add($"{name} row {i + 1}: field {k} added");
            }
        }
        foreach (var name in tb.Keys.Where(k => !ta.ContainsKey(k))) out_.Add($"{name}: added");
        return out_;
    }

    static List<Dictionary<string, string>> Rows(string text, string table) => CsiTableText.Parse(text).GetValueOrDefault(table) ?? [];

    public static IEnumerable<(string Name, Action Body)> All()
    {
        var sap = AsSap2000Writes(Sample("sap2000-test-frame.s2k"));

        yield return ("SAP2000 file: opened and written again, every table, row and field is the same (names, S33/S22, GUIDs, notes, tables with no form)", () =>
        {
            var read = S2kReader.Read(sap, "Real");
            Check(!read.Exact && read.Model.Source is { Program: "sap2000" }, "the file is kept with the model");
            var back = S2kWriter.Write(read.Model).Text;
            var lost = Differences(sap, back);
            Check(lost.Count == 0, $"{lost.Count} difference(s): " + string.Join(" | ", lost.Take(15)));
            // And through the .cpm (saved, opened again) and the file the Modeler writes (opened again, carried model).
            var reopened = ModelFile.FromJson(ModelFile.ToJson(read.Model));
            lost = Differences(sap, S2kWriter.Write(reopened).Text);
            Check(lost.Count == 0, "after the .cpm: " + string.Join(" | ", lost.Take(10)));
            var carried = S2kReader.Read(EmbeddedModel.Attach(back, read.Model), "Again");
            Check(carried.Exact, "the written file opens as the model it carries");
            lost = Differences(sap, S2kWriter.Write(carried.Model).Text);
            Check(lost.Count == 0, "a second round trip: " + string.Join(" | ", lost.Take(10)));
        });

        yield return ("SAP2000 file: CSI ProMax's own .s2k round trip is the same too (every sample table, the derived section values kept)", () =>
        {
            var own = Sample("sap2000-test-frame.s2k");
            var lost = Differences(own, S2kWriter.Write(S2kReader.Read(own, "Own").Model).Text);
            Check(lost.Count == 0, string.Join(" | ", lost.Take(15)));
        });

        yield return ("SAP2000 file: a changed section changes only that frame's row; the file's names and other fields stay", () =>
        {
            var m = S2kReader.Read(sap, "Edit").Model;
            var sections = m.FrameSections.Select(s => s.Name).ToList();
            Check(sections.Count >= 2, "two sections to swap between");
            var assign = m.LineAssigns.First(a => a.Section is { Length: > 0 });
            var was = assign.Section!;
            assign.Section = sections.First(s => s != was);
            var back = S2kWriter.Write(m).Text;
            var lost = Differences(sap, back);
            Check(lost.Count > 0 && lost.All(d => d.StartsWith("FRAME SECTION ASSIGNMENTS row ") && (d.Contains(" AnalSect:") || d.Contains(" SectionType:"))) && lost.Select(d => d.Split(' ')[4]).Distinct().Count() == 1,
                "only the one frame's section (and its shape) changes: " + string.Join(" | ", lost.Take(10)));
        });

        yield return ("SAP2000 file: a frame taken away goes from every table that names it, nothing else does", () =>
        {
            var m = S2kReader.Read(sap, "Delete").Model;
            var gone = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam);
            m.LineAssigns.Remove(gone);
            var back = S2kWriter.Write(m).Text;
            int Count(string t, string text) => Rows(text, t).Count;
            Check(Count("CONNECTIVITY - FRAME", back) == Count("CONNECTIVITY - FRAME", sap) - 1, "one connectivity row less");
            Check(Count("FRAME SECTION ASSIGNMENTS", back) == Count("FRAME SECTION ASSIGNMENTS", sap) - 1, "one section assignment less");
            var lost = Differences(sap, back).Where(d => !d.StartsWith("CONNECTIVITY - FRAME") && !d.StartsWith("FRAME SECTION ASSIGNMENTS") && !d.StartsWith("GROUPS 2")).ToList();
            Check(lost.Count == 0, "nothing else changes: " + string.Join(" | ", lost.Take(10)));
            var names = Rows(back, "CONNECTIVITY - FRAME").Select(r => r["Frame"]).ToHashSet();
            Check(Rows(back, "FRAME SECTION ASSIGNMENTS").All(r => names.Contains(r["Frame"])), "no section assignment for a frame that is gone");
            Check(Rows(back, "CONNECTIVITY - FRAME").All(r => r.ContainsKey("GUID") && r.ContainsKey("Length")), "the frames left keep the fields SAP2000 gave them");
        });

        yield return ("SAP2000 file: a new beam joins the file's own joints, and gets a name no other frame has", () =>
        {
            var m = S2kReader.Read(sap, "Add").Model;
            var beam = m.LineAssigns.First(a => m.Line(a.Label)!.Kind == LineKind.Beam);
            var line = m.Line(beam.Label)!;
            var otherStory = m.Stories[^1].Name == beam.Story ? m.Stories[1].Name : m.Stories[^1].Name;
            if (!m.LineAssigns.Exists(a => a.Label == beam.Label && a.Story == otherStory))
                m.LineAssigns.Add(new LineAssign { Label = beam.Label, Story = otherStory, Section = beam.Section, Extra = [ModelEdit.LineAssignDefaults(LineKind.Beam)] });
            else
            {
                m.Lines.Add(new LineObject { Label = "NEWB", Kind = LineKind.Beam, PointI = line.PointJ, PointJ = line.PointI });
                m.LineAssigns.Add(new LineAssign { Label = "NEWB", Story = beam.Story, Section = beam.Section, Extra = [ModelEdit.LineAssignDefaults(LineKind.Beam)] });
            }
            var back = S2kWriter.Write(m).Text;
            var before = Rows(sap, "CONNECTIVITY - FRAME"); var after = Rows(back, "CONNECTIVITY - FRAME");
            Check(after.Count == before.Count + 1, $"one frame more: {after.Count} vs {before.Count}");
            var joints = Rows(back, "JOINT COORDINATES").Select(r => r["Joint"]).ToHashSet();
            Check(after.All(r => joints.Contains(r["JointI"]) && joints.Contains(r["JointJ"])), "every frame joins joints the file has: " + string.Join(", ", after.Where(r => !joints.Contains(r["JointI"]) || !joints.Contains(r["JointJ"])).Select(r => r["Frame"] + " " + r["JointI"] + "-" + r["JointJ"])));
            Check(after.Select(r => r["Frame"]).Distinct().Count() == after.Count, "no two frames share a name");
            var added = after.Single(r => !before.Exists(b => b["Frame"] == r["Frame"]));
            Check(Rows(back, "FRAME SECTION ASSIGNMENTS").Exists(r => r["Frame"] == added["Frame"]), "the new frame has its section");
            Check(before.All(b => after.Exists(a => a["Frame"] == b["Frame"] && a["JointI"] == b["JointI"] && a["GUID"] == b["GUID"])), "the file's frames are as they were");
        });

        yield return ("SAP2000 .sdb with no .$2k: its database tables are written as its .s2k, and that goes round with nothing lost", () =>
        {
            var tables = CsiTableText.Parse(sap).Where(t => t.Key != "PROGRAM CONTROL").Select(t =>
            {
                var fields = t.Value.SelectMany(r => r.Keys).Distinct().ToList();
                var title = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.Key.ToLowerInvariant());
                return (title, new CsiProMax.Assistant.Etabs.TableData(fields, t.Value.Select(r => fields.Select(f => r.GetValueOrDefault(f) ?? "").ToArray()).ToList()));
            }).ToList();
            var text = CsiProMax.Assistant.Sap2000.SapTablesText.Write(new("C:/real/Tower.sdb", "26.3.0", "KN", "m", "C", false), tables);
            Check(Rows(text, "PROGRAM CONTROL").Single()["CurrUnits"] == "KN, m, C", "the units the tables were read in");
            var lost = Differences(sap, text).Where(d => !d.StartsWith("PROGRAM CONTROL")).ToList();
            Check(lost.Count == 0, "the tables as SAP2000 gave them: " + string.Join(" | ", lost.Take(10)));
            lost = Differences(text, S2kWriter.Write(S2kReader.Read(text, "Tower").Model).Text);
            Check(lost.Count == 0, "round trip: " + string.Join(" | ", lost.Take(10)));
        });

        yield return ("SAP2000 file: a large model (tens of thousands of rows) goes round in seconds, and a change in it stays small", () =>
        {
            var big = Templates.ConcreteFrame("Big", Enumerable.Repeat(6.0, 15).ToArray(), Enumerable.Repeat(5.0, 15).ToArray(), 20, 3.2);
            big.Target = "sap2000";
            var text = AsSap2000Writes(S2kWriter.WriteOwn(big).Text);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var m = S2kReader.Read(text, "Big").Model;
            var back = S2kWriter.Write(m).Text;
            var took = clock.Elapsed.TotalSeconds;
            var lost = Differences(text, back);
            Check(lost.Count == 0, string.Join(" | ", lost.Take(10)));
            Check(took < 60, $"took {took:0.0} s");
            m.LineAssigns.RemoveAt(0);
            lost = Differences(text, S2kWriter.Write(m).Text);
            Check(lost.Count > 0 && lost.All(d => d.StartsWith("CONNECTIVITY - FRAME") || d.StartsWith("FRAME SECTION ASSIGNMENTS") || d.StartsWith("GROUPS 2")), "one frame less, nothing else: " + string.Join(" | ", lost.Where(d => !d.StartsWith("CONNECTIVITY - FRAME") && !d.StartsWith("FRAME SECTION ASSIGNMENTS") && !d.StartsWith("GROUPS 2")).Take(10)));
            Console.WriteLine($"      ({Rows(text, "CONNECTIVITY - FRAME").Count:N0} frames, {text.Length / 1024:N0} KB, read and written in {took:0.0} s)");
        });

        yield return ("SAP2000 file: a kept table's changed row keeps the file's joint names, though the writer uses the same names for other joints", () =>
        {
            // The file's joints are numbered as CSI ProMax's writer numbers others: joint n is "n+1" (the last is "1").
            var count = Rows(sap, "JOINT COORDINATES").Count;
            var shifted = Regex.Replace(sap, @"=J(\d+)\b", m => "=" + (int.Parse(m.Groups[1].Value) % count + 1));
            shifted = shifted.Replace("END TABLE DATA", "TABLE:  \"CONNECTIVITY - CABLE\"\r\n   Cable=C1   JointI=3   JointJ=4   IsCurved=No\r\n\r\nEND TABLE DATA");
            var m = S2kReader.Read(shifted, "Cable").Model;
            var kept = m.Kept!.First(k => k.Key.EndsWith("CONNECTIVITY - CABLE", StringComparison.OrdinalIgnoreCase)).Value;
            kept[0]["IsCurved"] = "Yes";
            kept.Add(new Dictionary<string, string> { ["Cable"] = "C2", ["JointI"] = "5", ["JointJ"] = "6", ["IsCurved"] = "No" });
            var back = S2kWriter.Write(m).Text;
            var cables = Rows(back, "CONNECTIVITY - CABLE");
            Check(cables.Count == 2 && cables[1]["JointI"] == "5" && cables[1]["JointJ"] == "6", "the new cable on the joints it names: " + string.Join(" | ", cables.Select(c => string.Join(" ", c.Select(kv => kv.Key + "=" + kv.Value)))));
            var cable = cables[0];
            Check(cable["JointI"] == "3" && cable["JointJ"] == "4" && cable["IsCurved"] == "Yes", "the cable as edited, on the file's joints: " + string.Join(" ", cable.Select(kv => kv.Key + "=" + kv.Value)));
            var lost = Differences(shifted, back);
            Check(lost.Count == 1 && lost[0] == "CONNECTIVITY - CABLE: 2 rows, not 1", "only the cable table: " + string.Join(" | ", lost.Take(10)));
        });

        yield return ("SAP2000 file: a model with no file behind it is written as before", () =>
        {
            var m = Templates.ConcreteFrame("Plain", [6], [5], 2, 3);
            m.Target = "sap2000";
            Check(m.Source is null && S2kWriter.Write(m).Text == S2kWriter.WriteOwn(m).Text, "no source, the writer's own tables");
        });
    }
}
