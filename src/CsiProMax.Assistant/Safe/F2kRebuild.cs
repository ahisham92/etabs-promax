using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.CsiTables;

namespace CsiProMax.Assistant.Safe;

/// <summary>
/// Builds the model of a .f2k (the Modeler's SAFE file) in an open SAFE model through the API, for a SAFE that cannot
/// open the file through its API: SAFE 20 and 21 read every file given to File.OpenFile as a packed .fdb, and their
/// cFile has no import (tester's PC, 2026-11-26). The program must already hold a blank model in the file's units.
/// Materials, slab, beam, column and wall properties, the points, beams, columns, walls and slabs, the supports, the
/// load patterns, the surface loads and the combinations go in. Design strips do not: SAFE's API cannot draw them.
/// </summary>
public static class F2kRebuild
{
    /// <summary>The program's unit set (InitializeNewModel's number) for the file's "PROGRAM CONTROL" CurrUnits.</summary>
    public static int UnitsOf(string f2kText)
    {
        var tables = CsiTableText.Parse(f2kText);
        var units = Rows(tables, "PROGRAM CONTROL").FirstOrDefault()?.GetValueOrDefault("CurrUnits") ?? "";
        var parts = units.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        try { return parts.Length >= 2 ? Units.Code(parts[0], parts[1]) : 6; }
        catch (ToolInputException) { return 6; }
    }

    /// <summary>Builds what the file holds; the answer counts it the way save_safe_file does, with what was left out.</summary>
    public static JsonObject Build(ICsiLink e, string f2kText)
    {
        var t = CsiTableText.Parse(f2kText);
        var notes = new JsonArray();

        // ---- materials ----
        var mechanical = Rows(t, "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES").ToDictionary(r => r.GetValueOrDefault("Material") ?? "", StringComparer.OrdinalIgnoreCase);
        var concrete = Rows(t, "MATERIAL PROPERTIES 03 - CONCRETE").ToDictionary(r => r.GetValueOrDefault("Material") ?? "", StringComparer.OrdinalIgnoreCase);
        var rebar = Rows(t, "MATERIAL PROPERTIES 04 - REBAR").ToDictionary(r => r.GetValueOrDefault("Material") ?? "", StringComparer.OrdinalIgnoreCase);
        var have = e.Materials().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(t, "MATERIAL PROPERTIES 01 - GENERAL"))
        {
            var name = row.GetValueOrDefault("Material") ?? "";
            if (name.Length == 0 || have.Contains(name)) continue;
            var type = (row.GetValueOrDefault("Type") ?? "").ToLowerInvariant() switch { "concrete" => "concrete", "rebar" or "tendon" => "rebar", "steel" => "steel", _ => "other" };
            var m = mechanical.GetValueOrDefault(name);
            var args = new JsonObject
            {
                ["name"] = name, ["type"] = type, ["elastic_modulus"] = N(m, "E1", type == "concrete" ? 25e6 : 2e8),
                ["poisson"] = N(m, "U12", type == "concrete" ? 0.2 : 0.3), ["thermal"] = N(m, "A1", 1e-5), ["unit_weight"] = N(m, "UnitWeight", 0),
            };
            if (concrete.TryGetValue(name, out var c)) args["fc"] = N(c, "Fc", 0);
            if (rebar.TryGetValue(name, out var r)) { args["fy"] = N(r, "Fy", 0); args["fu"] = N(r, "Fu", 0); }
            Run(e, Sap2000.SapTools.All, "add_material", args);
        }

        // ---- properties ----
        foreach (var row in Rows(t, "SLAB PROPERTIES 01 - GENERAL"))
        {
            var kind = (row.GetValueOrDefault("Type") ?? "slab").ToLowerInvariant() switch { "drop" => "drop", "mat" => "mat", "footing" => "footing", "ribbed" => "ribbed", "waffle" => "waffle", _ => "slab" };
            Run(e, SafeTools.All, "add_slab_section", new JsonObject
            {
                ["name"] = row.GetValueOrDefault("Slab"), ["type"] = kind, ["material"] = row.GetValueOrDefault("MatProp"),
                ["thickness"] = N(row, "Thickness", 0.2), ["thick_plate"] = Yes(row, "ThickPlate"),
            });
        }
        foreach (var row in Rows(t, "BEAM PROPERTIES 01 - GENERAL"))
            Run(e, Sap2000.SapTools.All, "add_frame_section", new JsonObject
            {
                ["name"] = row.GetValueOrDefault("Beam"), ["shape"] = "rectangle", ["material"] = row.GetValueOrDefault("MatProp"),
                ["depth"] = N(row, "Depth", 0.6), ["width"] = N(row, "WidthTop", N(row, "WidthBot", 0.3)),
            });
        foreach (var row in Rows(t, "COLUMN PROPERTIES 01 - GENERAL"))
        {
            var circle = string.Equals(row.GetValueOrDefault("Type"), "Circular", StringComparison.OrdinalIgnoreCase);
            Run(e, Sap2000.SapTools.All, "add_frame_section", circle
                ? new JsonObject { ["name"] = row.GetValueOrDefault("Column"), ["shape"] = "circle", ["material"] = row.GetValueOrDefault("MatProp"), ["diameter"] = N(row, "Diameter", N(row, "SecDim2", 0.5)) }
                : new JsonObject { ["name"] = row.GetValueOrDefault("Column"), ["shape"] = "rectangle", ["material"] = row.GetValueOrDefault("MatProp"), ["depth"] = N(row, "SecDim3", 0.5), ["width"] = N(row, "SecDim2", 0.5) });
        }
        var walls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(t, "WALL PROPERTIES"))
        {
            var name = row.GetValueOrDefault("Wall") ?? "";
            var material = row.GetValueOrDefault("MatProp") ?? "";
            var thickness = N(row, "Thickness", 0.2);
            try
            {
                e.Raw("define a wall property", m => { Ok(m.PropArea.SetWall(name, 1, 1, material, thickness, -1, "", ""), $"define the wall property {name}"); return null; });
                walls[name] = name;
            }
            catch (Exception ex) when (ex is EtabsException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or MissingMethodException)
            {
                // A wall still stands on a slab property of its thickness: what it carries is the same.
                var stand = name + "-SLAB";
                Run(e, SafeTools.All, "add_slab_section", new JsonObject { ["name"] = stand, ["type"] = "slab", ["material"] = material, ["thickness"] = thickness });
                walls[name] = stand;
                notes.Add($"The wall property {name} could not be defined through the API ({ex.Message}); its walls carry the slab property {stand}.");
            }
        }

        // ---- objects ----
        var points = Rows(t, "OBJECT GEOMETRY - POINT COORDINATES").ToDictionary(r => r.GetValueOrDefault("Point") ?? "",
            r => (X: N(r, "GlobalX", 0), Y: N(r, "GlobalY", 0), Z: N(r, "GlobalZ", 0)), StringComparer.OrdinalIgnoreCase);
        (double X, double Y, double Z) P(Dictionary<string, string> row, string key) =>
            points.TryGetValue(row.GetValueOrDefault(key) ?? "", out var p) ? p : throw new EtabsException($"The .f2k names point {row.GetValueOrDefault(key)}, which it does not list.");
        var beamSection = Assignments(t, "BEAM PROPERTY ASSIGNMENTS", "Line", "BeamProp");
        var columnSection = Assignments(t, "COLUMN PROPERTY ASSIGNMENTS", "Line", "ColProp");
        var wallSection = Assignments(t, "WALL PROPERTY ASSIGNMENTS", "Line", "WallProp");
        var slabSection = Assignments(t, "SLAB PROPERTY ASSIGNMENTS", "Area", "SlabProp");
        var firstFrame = e.FrameSections().FirstOrDefault() ?? "";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int beams = 0, columns = 0, wallCount = 0, slabs = 0;
        foreach (var row in Rows(t, "OBJECT GEOMETRY - LINES 01 - GENERAL"))
        {
            var name = row.GetValueOrDefault("Line") ?? "";
            var (i, j) = (P(row, "PointI"), P(row, "PointJ"));
            switch ((row.GetValueOrDefault("LineType") ?? "").ToLowerInvariant())
            {
                case "beam":
                    Rename(e, "FrameObj", e.AddFrame(i.X, i.Y, i.Z, j.X, j.Y, j.Z, beamSection.GetValueOrDefault(name, firstFrame)), name, names);
                    beams++;
                    break;
                case "column":
                    Rename(e, "FrameObj", e.AddFrame(i.X, i.Y, i.Z, j.X, j.Y, j.Z, columnSection.GetValueOrDefault(name, firstFrame)), name, names);
                    columns++;
                    break;
                case "wall":
                    var height = N(row, "Height", -3);
                    var property = walls.GetValueOrDefault(wallSection.GetValueOrDefault(name, "")) ?? walls.Values.FirstOrDefault() ?? e.AreaSections().FirstOrDefault() ?? "";
                    Rename(e, "AreaObj", e.AddArea([i, j, (j.X, j.Y, j.Z + height), (i.X, i.Y, i.Z + height)], property), name, names);
                    wallCount++;
                    break;
                default:
                    notes.Add($"The line {name} ({row.GetValueOrDefault("LineType")}) was left out: only beams, columns and walls are built.");
                    break;
            }
        }
        var firstSlab = e.AreaSections().FirstOrDefault() ?? "";
        foreach (var row in Rows(t, "OBJECT GEOMETRY - AREAS 01 - GENERAL"))
        {
            var name = row.GetValueOrDefault("Area") ?? "";
            var count = (int)N(row, "NumPoints", 0);
            var corners = Enumerable.Range(1, count).Select(k => P(row, "Point" + k.ToString(CultureInfo.InvariantCulture))).ToList();
            if (corners.Count < 3) { notes.Add($"The area {name} has fewer than 3 points and was left out."); continue; }
            Rename(e, "AreaObj", e.AddArea(corners, slabSection.GetValueOrDefault(name, firstSlab)), name, names);
            slabs++;
        }

        // ---- supports, by where the file's restrained points are ----
        var supports = 0;
        var built = e.Points();
        foreach (var row in Rows(t, "RESTRAINT ASSIGNMENTS"))
        {
            if (!points.TryGetValue(row.GetValueOrDefault("Point") ?? "", out var at)) continue;
            var point = built.FirstOrDefault(p => Math.Abs(p.X - at.X) < 1e-4 && Math.Abs(p.Y - at.Y) < 1e-4 && Math.Abs(p.Z - at.Z) < 1e-4);
            if (point is null) { notes.Add($"No point of the model is at the support {row.GetValueOrDefault("Point")} ({at.X}, {at.Y}, {at.Z})."); continue; }
            e.SetRestraint(point.Name, new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Select(d => Yes(row, d)).ToArray());
            supports++;
        }

        // ---- loads ----
        var patterns = e.LoadPatterns().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(t, "LOAD PATTERNS"))
        {
            var name = row.GetValueOrDefault("LoadPat") ?? "";
            if (name.Length == 0 || patterns.Contains(name)) continue;
            e.AddLoadPattern(name, PatternType(row.GetValueOrDefault("Type") ?? ""), N(row, "SelfWtMult", 0));
            patterns.Add(name);
        }
        var loads = 0;
        foreach (var row in Rows(t, "LOAD ASSIGNMENTS - SURFACE LOADS"))
        {
            var area = row.GetValueOrDefault("Area") ?? "";
            var named = names.GetValueOrDefault(area);
            if (named is null) { notes.Add($"The surface load on {area} was left out: the area was not built."); continue; }
            e.SetAreaLoad(named, row.GetValueOrDefault("LoadPat") ?? "", N(row, "UnifLoad", 0), false);
            loads++;
        }
        var cases = e.LoadCases().Concat(e.Combos()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combos = 0;
        foreach (var combo in Rows(t, "LOAD COMBINATIONS").GroupBy(r => r.GetValueOrDefault("Combo") ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0))
        {
            var items = combo.Select(r => (Case: r.GetValueOrDefault("Load") ?? "", Factor: N(r, "SF", 1))).ToList();
            var missing = items.Where(x => !cases.Contains(x.Case)).Select(x => x.Case).ToList();
            if (missing.Count > 0) notes.Add($"The combination {combo.Key} leaves out {string.Join(", ", missing)}: SAFE has no such case.");
            var kept = items.Where(x => cases.Contains(x.Case)).ToList();
            if (kept.Count == 0) continue;
            e.AddCombo(combo.Key, kept);
            cases.Add(combo.Key);
            combos++;
        }

        var strips = Rows(t, "OBJECT GEOMETRY - DESIGN STRIPS").Select(r => r.GetValueOrDefault("Strip")).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (strips > 0) notes.Add($"The {strips} design strips of the file were left out: SAFE's API cannot draw strips (in SAFE: File > Import > SAFE .F2K file brings them).");
        e.Refresh();
        return new JsonObject
        {
            ["slabs"] = slabs, ["beams"] = beams, ["columns"] = columns, ["walls"] = wallCount, ["supports"] = supports,
            ["loads"] = loads, ["combinations"] = combos, ["strips"] = 0, ["strips_left_out"] = strips, ["notes"] = notes,
        };
    }

    private static IEnumerable<Dictionary<string, string>> Rows(Dictionary<string, List<Dictionary<string, string>>> tables, string name) =>
        tables.TryGetValue(name, out var rows) ? rows : [];

    private static Dictionary<string, string> Assignments(Dictionary<string, List<Dictionary<string, string>>> tables, string table, string key, string value) =>
        Rows(tables, table).Where(r => r.ContainsKey(key) && r.ContainsKey(value)).GroupBy(r => r[key], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[value], StringComparer.OrdinalIgnoreCase);

    private static double N(Dictionary<string, string>? row, string key, double fallback) =>
        row is not null && row.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool Yes(Dictionary<string, string> row, string key) => string.Equals(row.GetValueOrDefault(key), "Yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>A .f2k pattern type ("SUPER DEAD") as the link names it ("Super Dead"); one it does not know is Other.</summary>
    public static string PatternType(string f2k)
    {
        var known = EtabsWords.PatternTypes.FirstOrDefault(p => string.Equals(p.Name, f2k.Trim(), StringComparison.OrdinalIgnoreCase)).Name;
        return known ?? "Other";
    }

    private static void Run(ICsiLink e, IReadOnlyList<Tool> from, string tool, JsonObject args)
    {
        try { from.First(x => x.Name == tool).Run(JsonSerializer.SerializeToElement(args), e); }
        catch (EtabsException ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)) { /* kept as it is */ }
    }

    /// <summary>
    /// Gives the object the file's name (FrameObj.ChangeName / AreaObj.ChangeName), so the loads and the checks find it;
    /// the program's own name stays when it says no. <paramref name="names"/> keeps which name each one ended up with.
    /// </summary>
    private static void Rename(ICsiLink e, string kind, string made, string wanted, Dictionary<string, string> names)
    {
        names[wanted] = made;
        if (wanted.Length == 0 || string.Equals(made, wanted, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var answer = e.Raw($"name {made} {wanted}", m => kind == "FrameObj" ? m.FrameObj.ChangeName(made, wanted) : m.AreaObj.ChangeName(made, wanted));
            if (Convert.ToInt32(answer, CultureInfo.InvariantCulture) == 0) names[wanted] = wanted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* the program's name stands */ }
    }

    private static void Ok(object? answer, string what)
    {
        if (Convert.ToInt32(answer, CultureInfo.InvariantCulture) != 0) throw new EtabsException($"SAFE refused to {what}.");
    }
}
