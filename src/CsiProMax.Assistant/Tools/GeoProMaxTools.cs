using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// The link with GeoProMax (settlement of the ground under a raft), in SAFE and in ETABS. GeoProMax works out the
/// spring each part of the raft needs from the soil pressures of the last analysis and writes them to a springs file
/// (.gpmsprings.json); here that file becomes soil properties on the raft's areas, and the next soil pressures are
/// written as a file GeoProMax opens. Nothing here analyses: the program does, on its own license.
/// </summary>
public static class GeoProMaxTools
{
    public static IReadOnlyList<Tool> All { get; } =
    [
        new("apply_geopromax_springs",
            "Puts the springs GeoProMax worked out under the raft: reads a GeoProMax springs file (.gpmsprings.json, from GeoProMax's SAFE link), defines one soil property per zone (GPM-K1, GPM-K2 ... as modulus of subgrade reaction, converted to the model's units) and gives each raft area the zone of the ground under it. Give 'areas' by name, or 'section' for every area with that slab property, or neither for the lowest slab areas that lie on the file's cells. When one area lies over more than one zone it gets the zone nearest its average and is reported under 'mixed': divide the raft into panels about the file's cell size first, or in SAFE use as 'points' to put point springs at the cell centres instead. A model with results is locked: 'unlock' true unlocks it, which discards the results, so export the soil pressures first. Run the analysis again afterwards and repeat until GeoProMax says the springs have converged.",
            """
            {"type":"object","required":["file"],"properties":{
              "file":{"type":"string","description":"Full path of the .gpmsprings.json file."},
              "as":{"type":"string","enum":["areas","points"],"description":"areas (default): soil properties on the slab areas. points (SAFE only): a point spring at every cell centre."},
              "areas":{"type":"array","items":{"type":"string"}},
              "section":{"type":"string"},
              "compression_only":{"type":"boolean","description":"For areas. Default true."},
              "offset":{"type":"array","items":{"type":"number"},"description":"[dx, dy] added to the file's coordinates, in the model's length unit, when the model has moved since the pressures were exported."},
              "unlock":{"type":"boolean","description":"Unlock a locked model first (its results are discarded). Default false."}}}
            """, Ask.Changes, Apply, a => $"Put GeoProMax's springs from {Path.GetFileName(a.Text("file"))} under the raft{(a.Flag("unlock") ? " (unlocks the model: its results are discarded)" : "")}"),

        new("export_soil_pressures",
            "Writes the soil pressures under the raft as a file GeoProMax's SAFE link opens (.csv, in kN and m whatever the model's units): each point with its X and Y, the load case or combination and the pressure. The analysis must have run. Give 'cases' or 'combinations' to keep some (use the service combination the settlement is checked for). Default folder: the model's own. A file of the same name is never overwritten.",
            """
            {"type":"object","properties":{
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}},
              "name":{"type":"string","description":"File name without extension. Default: the model's name and 'soil pressures'."},
              "folder":{"type":"string"}}}
            """, Ask.Never, Export),
    ];

    private static readonly (string Name, double PerKn)[] Forces = [("lb", 224.80894), ("kip", 0.22480894), ("N", 1000), ("kN", 1), ("kgf", 101.97162), ("tonf", 0.10197162)];
    private static readonly (string Name, double PerM)[] Lengths = [("in", 39.370079), ("ft", 3.2808399), ("micron", 1e6), ("mm", 1000), ("cm", 100), ("m", 1)];

    /// <summary>How many of the model's force units are in a kN, and of its length units in a metre.</summary>
    internal static (double Force, double Length) Units(ModelInfo info)
    {
        var f = Forces.FirstOrDefault(u => string.Equals(u.Name, info.Force, StringComparison.OrdinalIgnoreCase));
        var l = Lengths.FirstOrDefault(u => string.Equals(u.Name, info.Length, StringComparison.OrdinalIgnoreCase));
        if (f.Name is null || l.Name is null) throw new EtabsException($"The model's units ('{info.Force}', '{info.Length}') could not be read, so nothing was converted. Set the units in the program and try again.");
        return (f.PerKn, l.PerM);
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static double Sig(double v) => v == 0 ? 0 : Math.Round(v, Math.Clamp(5 - (int)Math.Floor(Math.Log10(Math.Abs(v))), 0, 12));

    private static void Ok(object result, string what, ICsiLink e)
    {
        if (Convert.ToInt32(result, CultureInfo.InvariantCulture) != 0) throw new EtabsException($"{e.Program.Name} refused to {what}.");
    }

    // ---- the springs file ----

    internal sealed record Zone(string Name, double Modulus);
    internal sealed record Cell(double X, double Y, double? Modulus, string Zone);
    internal sealed record Springs(string Project, int Round, bool Converged, double CellSize, List<Zone> Zones, List<Cell> Cells);

    /// <summary>A GeoProMax springs file: lengths in m and moduli in kN/m3, as GeoProMax writes them.</summary>
    internal static Springs ReadSprings(string path)
    {
        if (!File.Exists(path)) throw new ToolInputException($"There is no file '{path}'. Give the full path of the .gpmsprings.json file GeoProMax saved.");
        JsonElement root;
        try { root = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone(); }
        catch (JsonException) { throw new ToolInputException($"'{Path.GetFileName(path)}' is not a GeoProMax springs file (it is not JSON). In GeoProMax's SAFE link, use 'Download for CSI ProMax'."); }
        if (root.ValueKind != JsonValueKind.Object || root.Text("format") != "geopromax-springs")
            throw new ToolInputException($"'{Path.GetFileName(path)}' is not a GeoProMax springs file. In GeoProMax's SAFE link, use 'Download for CSI ProMax'.");
        if (root.Whole("version", 1) != 1) throw new ToolInputException($"This springs file is version {root.Whole("version", 0)}, newer than this CSI ProMax reads. Update CSI ProMax.");
        if (root.TryGetProperty("units", out var u) && u.ValueKind == JsonValueKind.Object && (u.Text("length", "m") != "m" || u.Text("force", "kN") != "kN"))
            throw new ToolInputException("The springs file is not in kN and m.");
        var size = root.Number("cell_size", 0);
        var zones = root.Items("zones").Select(z => new Zone(z.Need("name"), z.Number("modulus"))).Where(z => z.Modulus > 0).ToList();
        var cells = root.Items("cells").Select(c => new Cell(c.Number("x"), c.Number("y"),
            c.TryGetProperty("modulus", out var k) && k.ValueKind == JsonValueKind.Number && k.GetDouble() > 0 ? k.GetDouble() : null, c.Text("zone"))).ToList();
        if (size <= 0 || zones.Count == 0 || cells.Count == 0) throw new ToolInputException("The springs file has no cells or no zones: work out the springs in GeoProMax first.");
        return new Springs(root.Text("project"), root.Whole("round", 0), root.Flag("converged"), size, zones, cells);
    }

    private static bool Inside(IReadOnlyList<(double X, double Y, double Z)> poly, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if (poly[i].Y > y != poly[j].Y > y && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    private static Zone Nearest(List<Zone> zones, double modulus) => zones.MinBy(z => Math.Abs(Math.Log(z.Modulus / modulus)))!;

    private static JsonNode Apply(JsonElement a, ICsiLink e)
    {
        if (e.Program.Key is not ("safe" or "etabs") || e is OfflineEtabs)
            throw new ToolInputException("apply_geopromax_springs works on a model open in SAFE or ETABS, not in the Modeler."); // ETABS itself
        var springs = ReadSprings(a.Need("file").Trim().Trim('"'));
        var mode = a.Text("as", "areas").ToLowerInvariant();
        if (mode is not ("areas" or "points")) throw new ToolInputException("'as' is areas or points.");
        if (mode == "points" && e.Program.Key != "safe")
            throw new ToolInputException("Point springs at the cell centres are for SAFE, where the slab mesh takes in every point. In ETABS, divide the raft into panels and use areas."); // ETABS itself
        var info = e.Info();
        var (force, length) = Units(info);
        var modulus = force / (length * length * length);   // model units per kN/m3
        double dx = 0, dy = 0;
        if (a.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Array) (dx, dy) = off.Point();
        var size = springs.CellSize * length;
        var cells = springs.Cells.Select(c => (X: c.X * length + dx, Y: c.Y * length + dy, c.Modulus, c.Zone)).ToList();
        var zones = springs.Zones;
        var unit = $"{info.Force}/{info.Length}3";

        // ---- which areas, and the zone under each ----
        var all = e.Areas();
        var named = a.Texts("areas");
        var section = a.Text("section");
        List<AreaRow> pool;
        if (named.Count > 0)
        {
            var missing = named.Where(n => all.All(r => r.Name != n)).ToList();
            if (missing.Count > 0) throw new ToolInputException($"There is no area {string.Join(", ", missing.Take(10))}.");
            pool = all.Where(r => named.Contains(r.Name)).ToList();
        }
        else
        {
            pool = all.Where(r => r.Kind == "floor" && (section.Length == 0 || string.Equals(r.Section, section, StringComparison.OrdinalIgnoreCase))).ToList();
            if (section.Length == 0 && pool.Count > 0)
            {
                // The raft is the lowest slab: in ETABS the floors above have slabs too.
                var low = pool.Min(r => r.Corners.Min(c => c.Z));
                pool = pool.Where(r => r.Corners.All(c => Math.Abs(c.Z - low) <= 1e-3 * Math.Max(1, length))).ToList();
            }
        }
        if (pool.Count == 0) throw new ToolInputException(section.Length > 0 ? $"No slab area has the property '{section}'." : "The model has no slab areas.");

        var assign = new List<(string Area, Zone Zone)>();
        var mixed = new List<string>();
        var outside = new List<string>();
        foreach (var area in pool)
        {
            var cx = area.Corners.Average(c => c.X);
            var cy = area.Corners.Average(c => c.Y);
            var under = cells.Where(c => c.Modulus is not null && Inside(area.Corners, c.X, c.Y)).ToList();
            if (under.Count == 0)
            {
                var near = cells.Where(c => c.Modulus is not null).OrderBy(c => (c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)).FirstOrDefault();
                if (near.Modulus is null || Math.Abs(near.X - cx) > size || Math.Abs(near.Y - cy) > size) { outside.Add(area.Name); continue; }
                under.Add(near);
            }
            assign.Add((area.Name, Nearest(zones, under.Average(c => c.Modulus!.Value))));
            if (under.Select(c => c.Zone).Distinct().Count() > 1) mixed.Add(area.Name);
        }
        if (assign.Count == 0)
            throw new ToolInputException($"None of the {pool.Count} slab areas lies on the file's cells (they span x {F(cells.Min(c => c.X))} to {F(cells.Max(c => c.X))}, y {F(cells.Min(c => c.Y))} to {F(cells.Max(c => c.Y))} in {info.Length}). Check the units GeoProMax read the pressures in, or give 'offset'.");

        var raft = pool.Where(r => assign.Any(x => x.Area == r.Name)).ToList();
        var inRaft = cells.Where(c => c.Modulus is not null && raft.Any(r => Inside(r.Corners, c.X, c.Y))).ToList();
        if (mode == "points" && inRaft.Count == 0) throw new ToolInputException("No cell of the file lies inside the raft's areas.");

        // ---- a locked model ----
        var unlocked = false;
        if (info.Locked)
        {
            if (!a.Flag("unlock"))
                throw new EtabsException($"The model is locked because it has results, and {e.Program.Name} changes no springs on a locked model. Export the soil pressures first if GeoProMax still needs them (export_soil_pressures), then call again with 'unlock' true: the results are discarded and the analysis has to run again.");
            e.Raw("unlock the model", m => { Ok(m.SetModelIsLocked(false), "unlock the model", e); return null; });
            unlocked = true;
        }

        var answer = new JsonObject
        {
            ["file"] = Path.GetFileName(a.Need("file").Trim().Trim('"')), ["project"] = springs.Project, ["round"] = springs.Round, ["converged"] = springs.Converged,
            ["as"] = mode, ["unit"] = mode == "areas" ? unit : $"{info.Force}/{info.Length}", ["unlocked"] = unlocked,
        };

        if (mode == "areas")
        {
            var compressionOnly = a.Flag("compression_only", true);
            var used = assign.Select(x => x.Zone).Distinct().OrderBy(z => z.Name, StringComparer.Ordinal).ToList();
            e.Raw("put GeoProMax's springs under the raft", m =>
            {
                // U3 is the vertical stiffness per area; nonlinear option 1 is compression only. Spring option 1: user specified.
                foreach (var z in used)
                    Ok(m.PropAreaSpring.SetAreaSpringProp(z.Name, 0.0, 0.0, Sig(z.Modulus * modulus), compressionOnly ? 1 : 0, 1, "", 0.0, 0.0, -1, "", ""), $"define the soil property {z.Name}", e);
                foreach (var (area, z) in assign) Ok(m.AreaObj.SetSpringAssignment(area, z.Name, 0), $"put the soil {z.Name} under area {area}", e);
                return null;
            });
            answer["compression_only"] = compressionOnly;
            answer["zones"] = new JsonArray(used.Select(z => (JsonNode)new JsonObject { ["soil"] = z.Name, ["subgrade"] = Sig(z.Modulus * modulus), ["areas"] = assign.Count(x => x.Zone == z) }).ToArray());
            answer["areas"] = assign.Count;
            answer["mixed"] = new JsonObject { ["count"] = mixed.Count, ["first"] = new JsonArray(mixed.Take(10).Select(n => (JsonNode)n).ToArray()) };
        }
        else
        {
            var z0 = raft[0].Corners[0].Z;
            var existing = e.Points();
            var tol = 1e-3 * length;
            var byZone = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var removed = true;
            e.Raw("put GeoProMax's point springs under the raft", m =>
            {
                foreach (var c in inRaft)
                {
                    var zone = zones.FirstOrDefault(z => z.Name == c.Zone) ?? Nearest(zones, c.Modulus!.Value);
                    var near = existing.FirstOrDefault(q => Math.Abs(q.X - c.X) < tol && Math.Abs(q.Y - c.Y) < tol && Math.Abs(q.Z - z0) < tol);
                    var name = near?.Name ?? "";
                    if (near is null) Ok(m.PointObj.AddCartesian(c.X, c.Y, z0, ref name, "", "Global", false, 0), $"add a point at {F(c.X)}, {F(c.Y)}", e);
                    if (!byZone.TryGetValue(zone.Name, out var list)) byZone[zone.Name] = list = [];
                    list.Add(name);
                }
                foreach (var (zoneName, points) in byZone)
                {
                    // A cell's spring: its modulus times its area. Vertical only.
                    double[] k = [0, 0, Sig(zones.First(z => z.Name == zoneName).Modulus * modulus * size * size), 0, 0, 0];
                    Ok(m.PropPointSpring.SetPointSpringProp(zoneName + "-PT", 1, ref k, "Global", "", "", 0.0, -1, "", ""), $"define the point spring {zoneName}-PT", e);
                    foreach (var p in points) Ok(m.PointObj.SetSpringAssignment(p, zoneName + "-PT", 0), $"give the spring to point {p}", e);
                }
                // The soil the areas had would hold the raft a second time.
                try { foreach (var r in raft) m.AreaObj.DeleteSpring(r.Name, 0); }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { removed = false; }
                return null;
            });
            answer["zones"] = new JsonArray(byZone.OrderBy(z => z.Key, StringComparer.Ordinal).Select(z => (JsonNode)new JsonObject
            {
                ["spring"] = z.Key + "-PT", ["kz"] = Sig(zones.First(x => x.Name == z.Key).Modulus * modulus * size * size), ["points"] = z.Value.Count,
            }).ToArray());
            answer["points"] = byZone.Sum(z => z.Value.Count);
            answer["area_soil_removed"] = removed;
        }
        answer["outside"] = new JsonObject { ["count"] = outside.Count, ["first"] = new JsonArray(outside.Take(10).Select(n => (JsonNode)n).ToArray()) };
        var notes = new List<string>();
        if (mixed.Count > 0 && mode == "areas") notes.Add($"{mixed.Count} areas lie over more than one zone and took the zone nearest their average: divide the raft into panels of about {F(size)} {info.Length} for the springs to follow the ground{(e.Program.Key == "safe" ? ", or use as 'points'" : "")}.");
        if (outside.Count > 0) notes.Add($"{outside.Count} slab areas are not on the file's cells and were left as they were.");
        if (mode == "points") notes.Add("Point springs are linear (they also pull) and every cell counts as a whole cell, so the edge is a little stiff. " + (answer["area_soil_removed"]!.GetValue<bool>() ? "The soil property the raft's areas had was taken off." : "Take the soil property off the raft's areas by hand, or the raft stands on both."));
        notes.Add(springs.Converged
            ? "GeoProMax marked these springs as converged: run the analysis once more and design with it."
            : "Run the analysis again, export the soil pressures (export_soil_pressures) and give them to GeoProMax for the next round, until it says the springs have converged.");
        answer["note"] = string.Join(" ", notes);
        return answer;
    }

    // ---- soil pressures out ----

    private static string Csv(string text) => text.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    private static JsonNode Export(JsonElement a, ICsiLink e)
    {
        var cases = a.Texts("cases");
        var combos = a.Texts("combinations");
        if (cases.Count == 0 && combos.Count == 0) { cases = [.. e.LoadCases()]; combos = [.. e.Combos()]; }
        TableData? data = null;
        var table = "";
        foreach (var t in Safe.SafeTools.Like(e.Tables(), "soil", "pressure"))
        {
            try { data = e.ReadTable(t.Key, cases, combos); } catch (EtabsException) { data = null; }
            if (data is { Rows.Count: > 0 }) { table = t.Name; break; }
            data = null;
        }
        if (data is null)
            return new JsonObject { ["status"] = "not available", ["note"] = $"{e.Program.Name} has no soil pressure table with data for these cases. Put soil under the raft (add_soil_support), run the analysis, then try again." };
        var pressure = ResultTools.Col(data.Fields, "Pressure", "SoilPressure", "Soil Pressure");
        if (pressure < 0) pressure = Safe.SafeTools.ColLike(data.Fields, "pressure");
        var caseField = ResultTools.Col(data.Fields, "OutputCase", "Output Case", "LoadCase", "Combo");
        var step = ResultTools.Col(data.Fields, "StepType", "Step Type");
        var point = ResultTools.Col(data.Fields, "UniqueName", "Point", "Label", "Joint");
        var x = ResultTools.Col(data.Fields, "GlobalX", "X");
        var y = ResultTools.Col(data.Fields, "GlobalY", "Y");
        if (pressure < 0) return new JsonObject { ["status"] = "not available", ["table"] = table, ["note"] = $"'{table}' has no pressure field. Its fields: {string.Join(", ", data.Fields)}." };
        Dictionary<string, PointRow>? points = null;
        if (x < 0 || y < 0)
        {
            if (point < 0) return new JsonObject { ["status"] = "not available", ["table"] = table, ["note"] = $"'{table}' has neither coordinates nor point names. Its fields: {string.Join(", ", data.Fields)}." };
            points = e.Points().GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        var info = e.Info();
        var (force, length) = Units(info);
        var toKpa = length * length / force;   // kN/m2 per model pressure unit
        var text = new StringBuilder("Point,GlobalX,GlobalY,OutputCase,StepType,Pressure\r\n,m,m,,,kN/m2\r\n");
        var written = 0;
        var skipped = 0;
        var seen = new List<string>();
        string Cell(string[] row, int i) => i >= 0 && i < row.Length ? row[i] : "";
        foreach (var row in data.Rows)
        {
            if (ResultTools.Num(Cell(row, pressure)) is not { } p) continue;
            double? px = x >= 0 ? ResultTools.Num(Cell(row, x)) : null, py = y >= 0 ? ResultTools.Num(Cell(row, y)) : null;
            if (points is not null && points.TryGetValue(Cell(row, point), out var at)) (px, py) = (at.X, at.Y);
            if (px is null || py is null) { skipped++; continue; }
            var kase = Cell(row, caseField);
            if (kase.Length > 0 && !seen.Contains(kase)) seen.Add(kase);
            text.Append(Csv(Cell(row, point))).Append(',').Append(F(px.Value / length)).Append(',').Append(F(py.Value / length)).Append(',')
                .Append(Csv(kase)).Append(',').Append(Csv(Cell(row, step))).Append(',').Append(F(Sig(p * toKpa))).Append("\r\n");
            written++;
        }
        if (written == 0) return new JsonObject { ["status"] = "not available", ["table"] = table, ["note"] = $"'{table}' has no row with a pressure and a place." };

        var name = a.Text("name");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ToolInputException("'name' is a file name without a folder. Give the folder in 'folder'.");
        var folder = a.Text("folder").Trim().Trim('"');
        if (folder.Length == 0)
        {
            try { folder = Path.GetDirectoryName(info.File) ?? ""; } catch (ArgumentException) { folder = ""; }
            if (folder.Length == 0 || !Directory.Exists(folder)) folder = OfflineEtabs.DefaultFolder;
        }
        Directory.CreateDirectory(folder);
        var model = info.File.Length > 0 ? Path.GetFileNameWithoutExtension(info.File.Replace('\\', '/')) : "Model";
        var stem = name.Length > 0 ? name : model + " soil pressures";
        stem = string.Concat(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, stem + ".csv");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}).csv");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return new JsonObject
        {
            ["status"] = "written", ["file"] = path, ["table"] = table, ["rows"] = written, ["skipped"] = skipped,
            ["cases"] = new JsonArray(seen.Select(c => (JsonNode)c).ToArray()), ["units"] = "kN, m",
            ["next"] = "In GeoProMax, open the SAFE link, load this file and pick the load case: it works out the springs and saves a springs file for apply_geopromax_springs."
                + (seen.Count > 1 ? " The file holds several cases: pick the service combination in GeoProMax, or export again with one case." : ""),
        };
    }
}
