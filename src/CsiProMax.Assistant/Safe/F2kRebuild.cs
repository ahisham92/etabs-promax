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
/// load patterns, the surface loads and the combinations go in. Design strips go in
/// apart (<see cref="BuildStrips"/>): SAFE's API reads them and has no call to draw them, so they go through its table.
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
        var mechanical = ByName(t, "MATERIAL PROPERTIES - BASIC MECHANICAL PROPERTIES", "Material");
        var concrete = ByName(t, "MATERIAL PROPERTIES - CONCRETE DATA", "Material");
        var strength = ByName(t, "MATERIAL PROPERTIES - REBAR DATA", "Material");
        foreach (var (k, v) in ByName(t, "MATERIAL PROPERTIES - TENDON DATA", "Material")) strength.TryAdd(k, v);
        foreach (var (k, v) in ByName(t, "MATERIAL PROPERTIES - STEEL DATA", "Material")) strength.TryAdd(k, v);
        var have = e.Materials().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(t, "MATERIAL PROPERTIES - GENERAL"))
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
            if (strength.TryGetValue(name, out var r)) { args["fy"] = N(r, "Fy", 0); args["fu"] = N(r, "Fu", 0); }
            Run(e, Sap2000.SapTools.All, "add_material", args);
        }

        // ---- properties ----
        foreach (var row in Rows(t, "SLAB PROPERTY DEFINITIONS"))
        {
            var kind = (row.GetValueOrDefault("Property Type") ?? "slab").ToLowerInvariant() switch { "drop" => "drop", "mat" => "mat", "footing" => "footing", "ribbed" => "ribbed", "waffle" => "waffle", _ => "slab" };
            Run(e, SafeTools.All, "add_slab_section", new JsonObject
            {
                ["name"] = row.GetValueOrDefault("Name"), ["type"] = kind, ["material"] = row.GetValueOrDefault("Material"),
                ["thickness"] = N(row, "Slab Thickness", 0.2), ["thick_plate"] = string.Equals(row.GetValueOrDefault("Modeling Type"), "Shell-Thick", StringComparison.OrdinalIgnoreCase),
            });
        }
        foreach (var row in Rows(t, "FRAME SECTION PROPERTY DEFINITIONS - CONCRETE RECTANGULAR"))
            Run(e, Sap2000.SapTools.All, "add_frame_section", new JsonObject
            {
                ["name"] = row.GetValueOrDefault("Name"), ["shape"] = "rectangle", ["material"] = row.GetValueOrDefault("Material"),
                ["depth"] = N(row, "Depth", 0.6), ["width"] = N(row, "Width", 0.3),
            });
        foreach (var row in Rows(t, "FRAME SECTION PROPERTY DEFINITIONS - CONCRETE CIRCLE"))
            Run(e, Sap2000.SapTools.All, "add_frame_section", new JsonObject { ["name"] = row.GetValueOrDefault("Name"), ["shape"] = "circle", ["material"] = row.GetValueOrDefault("Material"), ["diameter"] = N(row, "Diameter", 0.5) });
        foreach (var row in Rows(t, "FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE"))
            notes.Add($"The steel section {row.GetValueOrDefault("Name")} was not defined (only concrete rectangles and circles are built through the API); its frames take the first frame section.");
        var walls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(t, "WALL PROPERTY DEFINITIONS - SPECIFIED"))
        {
            var name = row.GetValueOrDefault("Name") ?? "";
            var material = row.GetValueOrDefault("Material") ?? "";
            var thickness = N(row, "Wall Thickness", 0.2);
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
        var points = Rows(t, "POINT OBJECT CONNECTIVITY").ToDictionary(r => r.GetValueOrDefault("UniqueName") ?? "",
            r => (X: N(r, "X", 0), Y: N(r, "Y", 0), Z: N(r, "Z", 0)), StringComparer.OrdinalIgnoreCase);
        (double X, double Y, double Z) P(Dictionary<string, string> row, string key) =>
            points.TryGetValue(row.GetValueOrDefault(key) ?? "", out var p) ? p : throw new EtabsException($"The .f2k names point {row.GetValueOrDefault(key)}, which it does not list.");
        var frameSection = Assignments(t, "FRAME ASSIGNMENTS - SECTION PROPERTIES", "UniqueName", "Section Property");
        var areaSection = Assignments(t, "AREA ASSIGNMENTS - SECTION PROPERTIES", "UniqueName", "Section Property");
        var firstFrame = e.FrameSections().FirstOrDefault() ?? "";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int beams = 0, columns = 0, wallCount = 0, slabs = 0;
        foreach (var (table, isColumn) in new[] { ("BEAM OBJECT CONNECTIVITY", false), ("COLUMN OBJECT CONNECTIVITY", true) })
            foreach (var row in Rows(t, table))
            {
                var name = row.GetValueOrDefault("Unique Name") ?? "";
                var (i, j) = (P(row, "UniquePtI"), P(row, "UniquePtJ"));
                Rename(e, "FrameObj", e.AddFrame(i.X, i.Y, i.Z, j.X, j.Y, j.Z, frameSection.GetValueOrDefault(name, firstFrame)), name, names);
                if (isColumn) columns++; else beams++;
            }
        List<(double X, double Y, double Z)> Corners(Dictionary<string, string> row) =>
            Enumerable.Range(1, 64).Select(k => "UniquePt" + k.ToString(CultureInfo.InvariantCulture)).TakeWhile(row.ContainsKey).Select(k => P(row, k)).ToList();
        foreach (var row in Rows(t, "WALL OBJECT CONNECTIVITY"))
        {
            var name = row.GetValueOrDefault("UniqueName") ?? "";
            var corners = Corners(row);
            if (corners.Count < 3) { notes.Add($"The wall {name} has fewer than 3 points and was left out."); continue; }
            var property = walls.GetValueOrDefault(areaSection.GetValueOrDefault(name, "")) ?? walls.Values.FirstOrDefault() ?? e.AreaSections().FirstOrDefault() ?? "";
            Rename(e, "AreaObj", e.AddArea(corners, property), name, names);
            wallCount++;
        }
        var firstSlab = e.AreaSections().FirstOrDefault() ?? "";
        foreach (var row in Rows(t, "FLOOR OBJECT CONNECTIVITY"))
        {
            var name = row.GetValueOrDefault("Unique Name") ?? "";
            var corners = Corners(row);
            if (corners.Count < 3) { notes.Add($"The area {name} has fewer than 3 points and was left out."); continue; }
            Rename(e, "AreaObj", e.AddArea(corners, areaSection.GetValueOrDefault(name, firstSlab)), name, names);
            slabs++;
        }

        // ---- supports, by where the file's restrained points are ----
        var supports = 0;
        var built = e.Points();
        foreach (var row in Rows(t, "JOINT ASSIGNMENTS - RESTRAINTS"))
        {
            if (!points.TryGetValue(row.GetValueOrDefault("UniqueName") ?? "", out var at)) continue;
            var point = built.FirstOrDefault(p => Math.Abs(p.X - at.X) < 1e-4 && Math.Abs(p.Y - at.Y) < 1e-4 && Math.Abs(p.Z - at.Z) < 1e-4);
            if (point is null) { notes.Add($"No point of the model is at the support {row.GetValueOrDefault("UniqueName")} ({at.X}, {at.Y}, {at.Z})."); continue; }
            e.SetRestraint(point.Name, new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Select(d => Yes(row, d)).ToArray());
            supports++;
        }

        // ---- loads ----
        var patterns = e.LoadPatterns().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(t, "LOAD PATTERN DEFINITIONS"))
        {
            var name = row.GetValueOrDefault("Name") ?? "";
            if (name.Length == 0 || patterns.Contains(name)) continue;
            e.AddLoadPattern(name, PatternType(row.GetValueOrDefault("Type") ?? ""), N(row, "Self Weight Multiplier", 0));
            patterns.Add(name);
        }
        var loads = 0;
        foreach (var row in Rows(t, "AREA LOAD ASSIGNMENTS - UNIFORM"))
        {
            var area = row.GetValueOrDefault("UniqueName") ?? "";
            var named = names.GetValueOrDefault(area);
            if (named is null) { notes.Add($"The surface load on {area} was left out: the area was not built."); continue; }
            e.SetAreaLoad(named, row.GetValueOrDefault("Load Pattern") ?? "", N(row, "Load", 0), false);
            loads++;
        }
        foreach (var row in Rows(t, "FRAME LOADS ASSIGNMENTS - DISTRIBUTED"))
        {
            var frame = row.GetValueOrDefault("UniqueName") ?? "";
            var named = names.GetValueOrDefault(frame);
            if (named is null) { notes.Add($"The line load on {frame} was left out: the beam was not built."); continue; }
            e.SetFrameLoad(named, row.GetValueOrDefault("Load Pattern") ?? "", N(row, "Force A", 0), false);
            loads++;
        }
        var pointLoads = Rows(t, "JOINT LOADS ASSIGNMENTS - FORCE").Count();
        if (pointLoads > 0) notes.Add($"{pointLoads} point load(s) of the file were left out: they go in through SAFE's own import of the file.");
        var cases = e.LoadCases().Concat(e.Combos()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combos = 0;
        foreach (var combo in Rows(t, "LOAD COMBINATION DEFINITIONS").GroupBy(r => r.GetValueOrDefault("Name") ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0))
        {
            var items = combo.Select(r => (Case: r.GetValueOrDefault("Load Name") ?? "", Factor: N(r, "SF", 1))).ToList();
            var missing = items.Where(x => !cases.Contains(x.Case)).Select(x => x.Case).ToList();
            if (missing.Count > 0) notes.Add($"The combination {combo.Key} leaves out {string.Join(", ", missing)}: SAFE has no such case.");
            var kept = items.Where(x => cases.Contains(x.Case)).ToList();
            if (kept.Count == 0) continue;
            e.AddCombo(combo.Key, kept);
            cases.Add(combo.Key);
            combos++;
        }
        var tendons = Rows(t, "TENDON OBJECT CONNECTIVITY").Select(r => r.GetValueOrDefault("Name")).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (tendons > 0) notes.Add($"{tendons} tendon(s) of the file were left out: SAFE's API has no call to draw a tendon; they go in through SAFE's own import of the file.");

        var openings = Rows(t, "NULL AREA OBJECT CONNECTIVITY").Count();
        if (openings > 0) notes.Add($"{openings} floor opening(s) of the file were left out: they go in through SAFE's own import of the file.");
        var punching = Rows(t, "CONCRETE SLAB DESIGN OVERWRITES - PUNCHING SHEAR - GENERAL").Count();
        if (punching > 0) notes.Add($"{punching} punching shear overwrite(s) of the file were left out: they go in through SAFE's own import of the file, or edit_table on SAFE's punching shear overwrite tables.");
        var strips = Rows(t, "STRIP OBJECT CONNECTIVITY").Select(r => r.GetValueOrDefault("Name")).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        e.Refresh();
        return new JsonObject
        {
            ["slabs"] = slabs, ["beams"] = beams, ["columns"] = columns, ["walls"] = wallCount, ["supports"] = supports,
            ["loads"] = loads, ["combinations"] = combos, ["strips_in_file"] = strips, ["tendons_in_file"] = tendons, ["notes"] = notes,
        };
    }

    /// <summary>SAFE's table of design strips, which its API (cDesignStrip: read, rename, delete only) cannot draw.</summary>
    public const string StripTable = "Strip Object Connectivity";

    /// <summary>
    /// The file's design strips, one row per strip point, in the fields SAFE's strip table has (<paramref name="fields"/>,
    /// as SAFE lists them): each value goes to the first field named like it. Throws, naming SAFE's fields, when the
    /// table has no field for the strip's name or for a point's place, so the report shows what to match next time.
    /// </summary>
    public static List<Dictionary<string, string>> StripRows(string f2kText, IReadOnlyList<string> fields)
    {
        string? Field(params string[] names) => FieldOf(fields, names);
        var name = NameField(fields);
        var x = Field("GlobalX", "X", "XCoord", "PointX");
        var y = Field("GlobalY", "Y", "YCoord", "PointY");
        if (name is null || x is null || y is null)
            throw new EtabsException($"SAFE's table '{StripTable}' has no field for {(name is null ? "the strip's name" : "a point's X and Y")}, so the strips cannot be written. Its fields: {string.Join(", ", fields)}.");
        var map = new (string? Field, string From)[]
        {
            (name, "Strip"), (Field("Layer", "StripLayer"), "Layer"), (Field("Point", "PointNumber", "PointNum", "PointOrder", "Order"), "Point"),
            (x, "GlobalX"), (y, "GlobalY"), (Field("GlobalZ", "Z", "ZCoord", "PointZ"), "GlobalZ"),
            (Field("WBLeft", "WidthBeforeLeft", "StartWidthLeft", "WidthLeftStart", "WidthLeft", "LeftWidth"), "WBLeft"),
            (Field("WBRight", "WidthBeforeRight", "StartWidthRight", "WidthRightStart", "WidthRight", "RightWidth"), "WBRight"),
            (Field("WALeft", "WidthAfterLeft", "EndWidthLeft", "WidthLeftEnd"), "WALeft"),
            (Field("WARight", "WidthAfterRight", "EndWidthRight", "WidthRightEnd"), "WARight"),
            (Field("AutoWiden", "AutoWidening"), "AutoWiden"),
        };
        var rows = new List<Dictionary<string, string>>();
        foreach (var row in StripPoints(CsiTableText.Parse(f2kText)))
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (field, from) in map)
                if (field is not null && !r.ContainsKey(field) && row.TryGetValue(from, out var v)) r[field] = from == "GlobalZ" && v.Length == 0 ? "0" : v;
            rows.Add(r);
        }
        return rows;
    }

    /// <summary>
    /// The file's strips one row per strip point (Strip, Layer, Point, GlobalX, GlobalY, GlobalZ and the widths before and
    /// after the point), from SAFE 20's "Strip Object Connectivity", whose rows name point objects.
    /// </summary>
    public static List<Dictionary<string, string>> StripPoints(Dictionary<string, List<Dictionary<string, string>>> t)
    {
        var points = Rows(t, "POINT OBJECT CONNECTIVITY").GroupBy(r => r.GetValueOrDefault("UniqueName") ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = new List<Dictionary<string, string>>();
        foreach (var strip in Rows(t, "STRIP OBJECT CONNECTIVITY").GroupBy(r => r.GetValueOrDefault("Name") ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0))
        {
            var first = strip.First();
            var layer = first.GetValueOrDefault("Layer") ?? "A";
            var places = new List<(string Point, string Left, string Right)> { (first.GetValueOrDefault("Strip Start Point") ?? "", first.GetValueOrDefault("Start Width Left") ?? "", first.GetValueOrDefault("Start Width Right") ?? "") };
            places.AddRange(strip.Select(r => (r.GetValueOrDefault("Segment End Point") ?? "", r.GetValueOrDefault("End Width Left") ?? "", r.GetValueOrDefault("End Width Right") ?? "")));
            var n = 0;
            foreach (var (point, left, right) in places)
            {
                if (!points.TryGetValue(point, out var p)) throw new EtabsException($"The strip {strip.Key} names point {point}, which the file does not list.");
                n++;
                list.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Strip"] = strip.Key, ["Layer"] = layer, ["Point"] = n.ToString(CultureInfo.InvariantCulture),
                    ["GlobalX"] = p.GetValueOrDefault("X") ?? "0", ["GlobalY"] = p.GetValueOrDefault("Y") ?? "0", ["GlobalZ"] = p.GetValueOrDefault("Z") ?? "0",
                    ["WBLeft"] = left, ["WBRight"] = right, ["WALeft"] = left, ["WARight"] = right, ["AutoWiden"] = first.GetValueOrDefault("Auto Widen") ?? "No",
                });
            }
        }
        return list;
    }

    private static Dictionary<string, Dictionary<string, string>> ByName(Dictionary<string, List<Dictionary<string, string>>> tables, string table, string key) =>
        Rows(tables, table).Where(r => r.ContainsKey(key)).GroupBy(r => r[key], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static string? FieldOf(IReadOnlyList<string> fields, params string[] names) =>
        names.Select(n => fields.FirstOrDefault(f => string.Equals(Plain(f), Plain(n), StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f is not null);

    /// <summary>The strip table's field for the strip's name.</summary>
    public static string? NameField(IReadOnlyList<string> fields) => FieldOf(fields, "Name", "Strip", "StripName", "Label");

    /// <summary>A field name without spaces, dashes or the like, to match "WB Left" with "WBLeft".</summary>
    private static string Plain(string field) => new(field.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Puts the file's design strips into SAFE through its strip table (<see cref="EditableTables.Put"/>), and gives how
    /// many SAFE then lists. The table's fields are read from SAFE first: the names differ between versions.
    /// </summary>
    public static int BuildStrips(ICsiLink e, string f2kText)
    {
        var fields = (string[])(e.Raw($"read the fields of {StripTable}", m =>
        {
            int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
            var answer = Convert.ToInt32(m.DatabaseTables.GetAllFieldsInTable(StripTable, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable), CultureInfo.InvariantCulture);
            if (answer != 0 || keys is not { Length: > 0 }) throw new EtabsException($"SAFE gave no fields for its table '{StripTable}' (it answered {answer}).");
            return keys;
        }) ?? Array.Empty<string>());
        var rows = StripRows(f2kText, fields);
        if (rows.Count == 0) return 0;
        EditableTables.Put(e, StripTable, NameField(fields)!, rows, "put in the design strips");
        return Convert.ToInt32(e.Raw("count the design strips", m => { int n = 0; string[] names = []; m.DesignConcreteSlab.DesignStrip.GetNameList(ref n, ref names); return n; }), CultureInfo.InvariantCulture);
    }

    /// <summary>The beams, columns, walls and floors the file holds (what Build would draw).</summary>
    public static int ObjectCount(string f2kText)
    {
        var t = CsiTableText.Parse(f2kText);
        return new[] { "BEAM OBJECT CONNECTIVITY", "COLUMN OBJECT CONNECTIVITY", "WALL OBJECT CONNECTIVITY", "FLOOR OBJECT CONNECTIVITY" }.Sum(x => Rows(t, x).Count());
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
