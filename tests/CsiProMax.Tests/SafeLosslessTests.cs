using CsiProMax.CsiTables;
using CsiProMax.F2k;
using CsiProMax.Model;

/// <summary>
/// A SAFE file opened in CSI ProMax and saved again loses nothing (Ahmed 2026-11-27: "it should be nothing lost whenever
/// we get into etabs or out or sap or safe, it should be exactly the same ... tested for all types of models"). Every
/// table, row and field of SAFE 20, 22 and 23's own files comes back as SAFE wrote it; a change the engineer makes
/// changes only what it touches, and every other field (GUIDs, colours, modifiers, design data) stays SAFE's.
/// </summary>
static class SafeLosslessTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static readonly string[] Files = ["safe-PT-SL-v20.f2k", "safe-PT-SL-v23.f2k", "safe-RC-PN-v20.f2k", "safe-RC-PN-v23.f2k", "safe22-tester-cpm-field-test.f2k"];

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

    /// <summary>The tables that differ in any row or field, and how ("LOST", "NEW" or the first field that differs).</summary>
    static List<string> Differences(Dictionary<string, List<Dictionary<string, string>>> a, Dictionary<string, List<Dictionary<string, string>>> b)
    {
        var d = new List<string>();
        foreach (var (t, rows) in a)
        {
            if (!b.TryGetValue(t, out var w)) { d.Add(t + " LOST"); continue; }
            if (rows.Count != w.Count) { d.Add($"{t} rows {rows.Count} -> {w.Count}"); continue; }
            for (var i = 0; i < rows.Count; i++)
            {
                var bad = rows[i].FirstOrDefault(kv => !w[i].TryGetValue(kv.Key, out var v) || v != kv.Value);
                if (bad.Key is not null || rows[i].Count != w[i].Count) { d.Add($"{t} row {i + 1} {bad.Key}: {bad.Value} -> {(bad.Key is null ? "" : w[i].GetValueOrDefault(bad.Key))}"); break; }
            }
        }
        d.AddRange(b.Keys.Where(k => !a.ContainsKey(k)).Select(k => k + " NEW"));
        return d;
    }

    static List<string> Tables(List<string> differences) => differences.Select(x => x.Split(" LOST")[0].Split(" NEW")[0].Split(" row")[0]).Distinct().ToList();

    static bool Derived(string t) => t.EndsWith("SUMMARY", StringComparison.Ordinal) || t.StartsWith("MATERIAL LIST", StringComparison.Ordinal) || t.StartsWith("TENDON DISCRETIZED", StringComparison.Ordinal) || t == "TENDON - TOTAL ELONGATION";

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("SAFE lossless: SAFE 20, 22 and 23's own .f2k files come back with every table, row and field, also through the .cpm and the Modeler's own .f2k", () =>
        {
            foreach (var file in Files)
            {
                var text = Sample(file);
                var before = CsiTableText.Parse(text);
                var m = F2kReader.Read(text, file).Model;
                var d = Differences(before, CsiTableText.Parse(F2kWriter.Write(m).Text));
                Check(d.Count == 0, file + ": saved again: " + string.Join(" | ", d.Take(6)));
                // The model saved as .cpm and opened again.
                var j = ModelFile.FromJson(ModelFile.ToJson(m));
                d = Differences(before, CsiTableText.Parse(F2kWriter.Write(j).Text));
                Check(d.Count == 0, file + ": through the .cpm: " + string.Join(" | ", d.Take(6)));
                // The Modeler's own .f2k of it (the model carried inside), opened again and saved again.
                var own = EmbeddedModel.Attach(F2kWriter.Write(m).Text, m);
                d = Differences(before, CsiTableText.Parse(F2kWriter.Write(F2kReader.Read(own).Model).Text));
                Check(d.Count == 0, file + ": through the Modeler's .f2k: " + string.Join(" | ", d.Take(6)));
                // SAFE's tables in SAFE's order.
                Check(CsiTableText.Names(F2kWriter.Write(m).Text).SequenceEqual(CsiTableText.Names(text).Distinct()), file + ": the tables keep SAFE's order");
                // And every floor's file the Modeler saves beside the model is the same.
                // (The tester's file is one the Modeler wrote, carrying its model: its busiest floor is the file.)
                var floor = F2kWriter.WriteEveryFloor(m).First(f => !F2kSource.Has(m) || f.Story == F2kSource.Story(m));
                Check(Differences(before, CsiTableText.Parse(floor.Text)).Count == 0, file + ": SAFE's floor among the files of every floor");
            }
        });

        yield return ("SAFE lossless: a field the engineer changes is the only field that changes; every other one stays SAFE's", () =>
        {
            foreach (var file in Files)
            {
                var text = Sample(file);
                var before = CsiTableText.Parse(text);
                var m = F2kReader.Read(text, file).Model;
                var p = m.LoadPatterns[0];
                p.SelfWeight = p.SelfWeight == 0.5 ? 0.25 : 0.5;
                var after = CsiTableText.Parse(F2kWriter.Write(m).Text);
                var d = Differences(before, after);
                Check(Tables(d).Where(t => !Derived(t)).SequenceEqual(["LOAD PATTERN DEFINITIONS"]), file + ": only the load pattern changes: " + string.Join(" | ", d.Take(6)));
                var row = after["LOAD PATTERN DEFINITIONS"].Single(r => r["Name"] == p.Name);
                var was = before["LOAD PATTERN DEFINITIONS"].Single(r => r["Name"] == p.Name);
                Check(CsiTableText.Number(p.SelfWeight) == row["Self Weight Multiplier"], file + ": the new self weight is written");
                foreach (var (k, v) in was.Where(kv => kv.Key != "Self Weight Multiplier"))
                    Check(row.GetValueOrDefault(k) == v, $"{file}: {k} of the changed pattern stays SAFE's ({v}, got {row.GetValueOrDefault(k)})");
            }
        });

        yield return ("SAFE lossless: a load taken away goes, the other loads keep SAFE's rows with their GUIDs", () =>
        {
            foreach (var file in Files)
            {
                var text = Sample(file);
                var before = CsiTableText.Parse(text);
                var m = F2kReader.Read(text, file).Model;
                var gone = m.AreaLoads[0];
                m.AreaLoads.RemoveAt(0);
                var after = CsiTableText.Parse(F2kWriter.Write(m).Text);
                var d = Differences(before, after);
                Check(Tables(d).Where(t => !Derived(t)).SequenceEqual(["AREA LOAD ASSIGNMENTS - UNIFORM"]), file + ": only the slab loads change: " + string.Join(" | ", d.Take(6)));
                var rowsBefore = before["AREA LOAD ASSIGNMENTS - UNIFORM"];
                var rowsAfter = after["AREA LOAD ASSIGNMENTS - UNIFORM"];
                Check(rowsAfter.Count == rowsBefore.Count - 1, file + ": one load fewer");
                Check(!rowsAfter.Exists(r => r["UniqueName"] == gone.Object && r["Load Pattern"] == gone.Pattern), file + ": the load taken away is gone");
                foreach (var r in rowsAfter)
                    Check(rowsBefore.Exists(b => b.Count == r.Count && b.All(kv => r.GetValueOrDefault(kv.Key) == kv.Value)), $"{file}: the load on {r["UniqueName"]} ({r["Load Pattern"]}) is SAFE's row, GUID too");
            }
        });

        yield return ("SAFE lossless: a combination added is new rows; SAFE's combinations, their GUIDs and every other table stay", () =>
        {
            foreach (var file in Files)
            {
                var text = Sample(file);
                var before = CsiTableText.Parse(text);
                var m = F2kReader.Read(text, file).Model;
                m.AddCombo("CPM NEW", (m.LoadPatterns[0].Name, 1.4));
                var after = CsiTableText.Parse(F2kWriter.Write(m).Text);
                var d = Differences(before, after);
                Check(Tables(d).Where(t => !Derived(t)).All(t => t is "LOAD COMBINATION DEFINITIONS" or "CONCRETE SLAB DESIGN LOAD COMBINATION DATA"), file + ": only the combinations change: " + string.Join(" | ", d.Take(6)));
                var combos = after["LOAD COMBINATION DEFINITIONS"];
                Check(combos.Exists(r => r["Name"] == "CPM NEW"), file + ": the new combination is in the file");
                foreach (var r in before["LOAD COMBINATION DEFINITIONS"])
                    Check(combos.Exists(c => c.Count == r.Count && r.All(kv => c.GetValueOrDefault(kv.Key) == kv.Value)), $"{file}: SAFE's row of {r.GetValueOrDefault("Name")} stays as it was");
            }
        });

        yield return ("SAFE lossless: a slab given another section changes that slab's section only; a slab taken away goes with its points, loads and summaries", () =>
        {
            foreach (var file in Files.Where(f => !f.StartsWith("safe22", StringComparison.Ordinal)))
            {
                var text = Sample(file);
                var before = CsiTableText.Parse(text);
                var m = F2kReader.Read(text, file).Model;
                var slab = m.AreaAssigns.First(a => before["AREA ASSIGNMENTS - SECTION PROPERTIES"].Exists(r => r["UniqueName"] == a.Label));
                var other = before["SLAB PROPERTY DEFINITIONS"].Select(r => r["Name"]).First(n => n != slab.Section);
                slab.Section = other;
                var after = CsiTableText.Parse(F2kWriter.Write(m).Text);
                var d = Differences(before, after);
                Check(Tables(d).Where(t => !Derived(t)).SequenceEqual(["AREA ASSIGNMENTS - SECTION PROPERTIES"]), file + ": only the slab's section changes: " + string.Join(" | ", d.Take(6)));
                Check(after["AREA ASSIGNMENTS - SECTION PROPERTIES"].Single(r => r["UniqueName"] == slab.Label)["Section Property"] == other, file + ": the slab has its new section");

                // Taken away: the slab, its assignments and its loads.
                var m2 = F2kReader.Read(text, file).Model;
                var label = m2.AreaAssigns.First(a => before["FLOOR OBJECT CONNECTIVITY"].Exists(r => r["Unique Name"] == a.Label)).Label;
                m2.AreaAssigns.RemoveAll(a => a.Label == label);
                m2.Areas.RemoveAll(a => a.Label == label);
                m2.AreaLoads.RemoveAll(l => l.Object == label);
                var gone = CsiTableText.Parse(F2kWriter.Write(m2).Text);
                Check(!gone["FLOOR OBJECT CONNECTIVITY"].Exists(r => r["Unique Name"] == label), file + ": the slab is gone");
                Check(gone["FLOOR OBJECT CONNECTIVITY"].Count == before["FLOOR OBJECT CONNECTIVITY"].Count - 1, file + ": one slab fewer");
                Check(!gone.TryGetValue("AREA LOAD ASSIGNMENTS - UNIFORM", out var loads) || !loads.Exists(r => r["UniqueName"] == label), file + ": its loads are gone");
                foreach (var r in gone["FLOOR OBJECT CONNECTIVITY"])
                    Check(before["FLOOR OBJECT CONNECTIVITY"].Exists(b => b["Unique Name"] == r["Unique Name"] && b.GetValueOrDefault("GUID") == r.GetValueOrDefault("GUID")), $"{file}: slab {r["Unique Name"]} keeps SAFE's GUID");
                Check(!gone.ContainsKey("AREA ASSIGNMENTS - SUMMARY"), file + ": SAFE's slab summary, out of date now, is left for SAFE to make again");
                foreach (var t in before.Keys.Where(t => t.StartsWith("MATERIAL PROPERTIES", StringComparison.Ordinal) || t.Contains("PREFERENCES") || t.StartsWith("LOAD ", StringComparison.Ordinal)))
                {
                    var dt = Differences(new() { [t] = before[t] }, gone).Where(x => !x.EndsWith(" NEW", StringComparison.Ordinal)).ToList();
                    Check(dt.Count == 0, file + ": " + t + " stays as SAFE wrote it: " + string.Join(" | ", dt));
                }
            }
        });

        yield return ("SAFE lossless: a model that is not SAFE's file is written as before, and a model made new from it forgets the file", () =>
        {
            var m = Templates.ConcreteFrame("T", [6, 6], [5], 2, 3);
            Check(!F2kSource.Has(m), "a new model has no SAFE file");
            var once = F2kWriter.Write(m).Text;
            Check(CsiTableText.Names(once).Count > 0, "it writes its tables");
            var safe = F2kReader.Read(Sample("safe-PT-SL-v20.f2k")).Model;
            Check(F2kSource.Has(safe), "a SAFE file's model keeps the file");
            F2kSource.Forget(safe);
            Check(!F2kSource.Has(safe) && !(safe.Kept ?? []).Keys.Any(k => k.StartsWith("model|SAFE", StringComparison.Ordinal)), "forgotten, nothing of it is left");
        });
    }
}
