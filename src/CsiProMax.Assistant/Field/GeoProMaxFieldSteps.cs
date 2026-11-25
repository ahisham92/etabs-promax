using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant;

/// <summary>
/// The link with GeoProMax, tried on the scratch model of SAFE or ETABS: the soil pressures are written as GeoProMax
/// reads them, and a springs file made here for the scratch model's own lowest slabs is put under them and read back.
/// </summary>
public static class GeoProMaxFieldSteps
{
    public static void Run(FieldContext ctx)
    {
        var link = ctx.Link;
        var program = link.Program.Name;
        var folder = JsonSerializer.Serialize(ctx.Folder);
        ctx.Tool("export_soil_pressures", $$"""{"folder":{{folder}},"name":"{{ModelerFieldSteps.ModelName}} {{program}} soil pressures"}""", a =>
        {
            var status = a["status"]?.GetValue<string>();
            FieldContext.Expect(status is "written" or "not available", "written or not available was expected: " + a.ToJsonString());
            if (status != "written") return;
            var lines = File.ReadAllLines(a["file"]!.GetValue<string>());
            FieldContext.Expect(lines.Length > 2 && lines[0].StartsWith("Point,GlobalX,GlobalY", StringComparison.Ordinal), "the file has no rows under its headings");
        });

        if (link is OfflineEtabs)
        {
            // The Modeler has no program behind it: the tool must say so and change nothing.
            ctx.Refused("apply_geopromax_springs", $$"""{"file":{{JsonSerializer.Serialize(Path.Combine(ctx.Folder, "none.gpmsprings.json"))}}}""", "SAFE or ETABS");
            return;
        }

        var file = Path.Combine(ctx.Folder, $"{ModelerFieldSteps.ModelName} {program}.gpmsprings.json");
        var areas = new List<string>();
        var made = ctx.Step("a GeoProMax springs file for the scratch model's lowest slabs", () =>
        {
            var (_, length) = GeoProMaxTools.Units(link.Info());
            var floors = link.Areas().Where(a => a.Kind == "floor").ToList();
            FieldContext.Expect(floors.Count > 0, "the scratch model has no slab areas");
            var low = floors.Min(a => a.Corners.Min(c => c.Z));
            var lowest = floors.Where(a => a.Corners.All(c => Math.Abs(c.Z - low) <= 1e-3 * Math.Max(1, length))).ToList();
            areas.AddRange(lowest.Select(a => a.Name));
            var cells = new JsonArray(lowest.Select(a => (JsonNode)new JsonObject
            {
                ["x"] = a.Corners.Average(c => c.X) / length, ["y"] = a.Corners.Average(c => c.Y) / length,
                ["pressure"] = 100, ["settlement"] = 0.005, ["modulus"] = 20000, ["zone"] = "GPM-K1",
            }).ToArray());
            var springs = new JsonObject
            {
                ["format"] = "geopromax-springs", ["version"] = 1, ["project"] = "CSI ProMax field test", ["round"] = 1, ["converged"] = false,
                ["units"] = new JsonObject { ["length"] = "m", ["force"] = "kN", ["pressure"] = "kN/m2", ["modulus"] = "kN/m3" }, ["cell_size"] = 1,
                ["zones"] = new JsonArray(new JsonObject { ["name"] = "GPM-K1", ["modulus"] = 20000, ["from"] = 20000, ["to"] = 20000, ["cells"] = lowest.Count }),
                ["cells"] = cells,
            };
            File.WriteAllText(file, springs.ToJsonString());
            return new { file = Path.GetFileName(file), areas = lowest.Count };
        }) is not null;
        if (!made) { ctx.Fail("tool apply_geopromax_springs", "the springs file could not be made"); return; }

        var locked = false;
        try { locked = link.Info().Locked; } catch (EtabsException) { }
        if (locked) ctx.Refused("apply_geopromax_springs", $$"""{"file":{{JsonSerializer.Serialize(file)}}}""", "unlock", "tool apply_geopromax_springs asks before it unlocks an analysed model");
        ctx.Tool("apply_geopromax_springs", $$"""{"file":{{JsonSerializer.Serialize(file)}},"unlock":true}""", a =>
            FieldContext.Expect(a["areas"]!.GetValue<int>() == areas.Count && a["outside"]!["count"]!.GetValue<int>() == 0, $"{areas.Count} areas were to get soil: " + a.ToJsonString()));
        ctx.Step($"GeoProMax's soil is under the slabs (read back through {program}'s API)", () =>
        {
            try
            {
                var names = (string[])link.Raw("list the soil properties", m => { int n = 0; string[] list = []; m.PropAreaSpring.GetNameList(ref n, ref list); return list; })!;
                FieldContext.Expect(names.Contains("GPM-K1"), $"{program}'s soil properties: " + string.Join(", ", names));
                var under = (string)link.Raw("read an area's soil", m => { string prop = ""; m.AreaObj.GetSpringAssignment(areas[0], ref prop); return prop; })!;
                FieldContext.Expect(under == "GPM-K1", $"area {areas[0]} has the soil '{under}'");
                return new { soils = names, area = areas[0], under };
            }
            catch (Exception ex) when (ex is EtabsException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            { throw new FieldSkipException($"the soil could not be read back through {program}'s API ({ex.Message})"); }
        });
        if (link.Program.Key == "safe")
            ctx.Tool("apply_geopromax_springs", $$"""{"file":{{JsonSerializer.Serialize(file)}},"as":"points"}""", a =>
                FieldContext.Expect(a["points"]!.GetValue<int>() >= 1, "point springs at the cell centres were expected: " + a.ToJsonString()), "tool apply_geopromax_springs as point springs");
    }
}
