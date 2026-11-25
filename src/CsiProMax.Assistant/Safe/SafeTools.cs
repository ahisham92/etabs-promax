using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;

namespace CsiProMax.Assistant.Safe;

/// <summary>
/// The tools only SAFE has: slab, drop, mat and footing sections, soil under a mat, point springs, point supports
/// and column loads, design strips and SAFE's editable tables; and the slab checks, read from SAFE's own tables after
/// its own analysis and design: punching shear, deflection, strip forces, soil pressure and slab design. Nothing here
/// analyses or designs. The shared tools (get_model_context, add_slabs, set_loads, add_combination, read_table and the
/// rest) work in SAFE as they do in ETABS.
/// </summary>
public static class SafeTools
{
    public static IReadOnlyList<Tool> All { get; } =
    [
        // ---- modelling ----
        new("add_slab_section",
            "Defines a SAFE slab property: a slab, a drop panel, a mat (raft), a footing, or a ribbed or waffle slab, with its concrete and thickness. Thick plate is on for mats and footings unless 'thick_plate' says otherwise. Use set_section or add_slabs to put it on areas.",
            """
            {"type":"object","required":["name","material","thickness"],"properties":{
              "name":{"type":"string"},
              "type":{"type":"string","enum":["slab","drop","mat","footing","ribbed","waffle"],"description":"Default slab."},
              "material":{"type":"string","description":"A concrete material of the model."},
              "thickness":{"type":"number"},
              "thick_plate":{"type":"boolean"}}}
            """, Ask.Changes, AddSlabSection, a => $"Define the {a.Text("type", "slab")} {a.Text("name")}, {a.Number("thickness", 0)} thick"),

        new("add_soil_support",
            "Puts soil under slabs (a mat or footings): defines a soil property from the modulus of subgrade reaction (force per length cubed, in the model's units: e.g. kN/m3 when the model is in kN and m) and gives it to the areas. Compression only by default, as soil does not pull. Give 'areas' by name, or 'section' to take every area with that slab property, or neither for every slab area.",
            """
            {"type":"object","required":["subgrade"],"properties":{
              "name":{"type":"string","description":"The soil property's name. Default SOIL1."},
              "subgrade":{"type":"number","description":"Modulus of subgrade reaction, force per length cubed."},
              "compression_only":{"type":"boolean","description":"Default true."},
              "areas":{"type":"array","items":{"type":"string"}},
              "section":{"type":"string"}}}
            """, Ask.Changes, AddSoil, a => $"Put soil {a.Text("name", "SOIL1")} (k = {a.Number("subgrade", 0)}) under {(a.Texts("areas").Count > 0 ? a.Texts("areas").Count + " areas" : a.Text("section").Length > 0 ? "the " + a.Text("section") + " areas" : "every slab area")}"),

        new("add_point_springs",
            "Defines a point spring (a pile, or a column modelled as a spring) and gives it to points. Stiffnesses are in the model's units; kz is vertical. Give 'points' by name or 'at' as plan points [x, y] (a point is added there on the slab level when none is near).",
            """
            {"type":"object","required":["name","kz"],"properties":{
              "name":{"type":"string"},
              "kz":{"type":"number"},"kx":{"type":"number"},"ky":{"type":"number"},"krx":{"type":"number"},"kry":{"type":"number"},"krz":{"type":"number"},
              "points":{"type":"array","items":{"type":"string"}},
              "at":{"type":"array","items":{"type":"array","items":{"type":"number"}}},
              "z":{"type":"number","description":"Level of new points. Default: the slab level."}}}
            """, Ask.Changes, AddPointSprings, a => $"Give the spring {a.Text("name")} (kz = {a.Number("kz", 0)}) to {a.Texts("points").Count + Count(a, "at")} points"),

        new("add_point_supports",
            "Adds supports under the slab at plan points [x, y] (for example where columns or walls hold a slab modelled without them): pinned (holds up and sideways), fixed (also holds rotation) or vertical (holds up only). A point is added on the slab level when none is near.",
            """
            {"type":"object","required":["at"],"properties":{
              "at":{"type":"array","items":{"type":"array","items":{"type":"number"}}},
              "type":{"type":"string","enum":["pinned","fixed","vertical"],"description":"Default pinned."},
              "z":{"type":"number","description":"Level. Default: the slab level."}}}
            """, Ask.Changes, AddPointSupports, a => $"Add {Count(a, "at")} {a.Text("type", "pinned")} supports"),

        new("add_point_loads",
            "Puts point loads on the slab: column loads on a mat, equipment, a load from above. 'force' is in the gravity direction (positive is down); 'mx' and 'my' are moments about the global X and Y axes. Give 'points' by name or 'at' as plan points [x, y] (a point is added there when none is near).",
            """
            {"type":"object","required":["pattern","force"],"properties":{
              "pattern":{"type":"string"},
              "force":{"type":"number"},"mx":{"type":"number"},"my":{"type":"number"},
              "points":{"type":"array","items":{"type":"string"}},
              "at":{"type":"array","items":{"type":"array","items":{"type":"number"}}},
              "z":{"type":"number"},
              "replace":{"type":"boolean","description":"Replace the point's loads of this pattern. Default false (add)."}}}
            """, Ask.Changes, AddPointLoads, a => $"Put {a.Number("force", 0)} on {a.Texts("points").Count + Count(a, "at")} points in {a.Text("pattern")}"),

        new("list_design_strips",
            "Lists SAFE's design strips: name, layer (A or B), and the strip's points with their widths left and right. Design strips are where SAFE integrates the slab moments and designs the reinforcement.",
            """{"type":"object","properties":{"limit":{"type":"integer","description":"Strips to list (default 200)."}}}""",
            Ask.Never, ListStrips),

        new("edit_safe_table",
            "SAFE's own editable tables (Edit > Interactive Database Editing): the way to add design strips, change punching shear overrides, design preferences, strip widths and anything no other tool covers. action 'fields' gives a table's field names and units; 'read' its rows; 'add' adds rows (each row an object of field to value, every other field left empty); 'update' changes the fields given in 'set' on the rows whose field 'match' equals its value. SAFE checks the edit and its import log comes back. Find the table name with list_tables.",
            """
            {"type":"object","required":["table","action"],"properties":{
              "table":{"type":"string","description":"Table name or key from list_tables."},
              "action":{"type":"string","enum":["fields","read","add","update"]},
              "rows":{"type":"array","items":{"type":"object","additionalProperties":{"type":"string"}},"description":"For add."},
              "match":{"type":"object","additionalProperties":{"type":"string"},"description":"For update: field to the value a row must have."},
              "set":{"type":"object","additionalProperties":{"type":"string"},"description":"For update: field to its new value."},
              "limit":{"type":"integer","description":"For read: rows to return (default 100)."}}}
            """, Ask.Changes, EditTable, a => a.Text("action") is "fields" or "read" ? $"Read SAFE's table {a.Text("table")}" : $"Edit SAFE's table {a.Text("table")} ({a.Text("action")})"),

        // ---- results and checks ----
        new("safe_results_overview",
            "After SAFE's analysis and design: which result and design tables have data (punching shear, strip forces, soil pressure, displacements, slab and beam design, reactions), the total support reaction per load case, and the largest values of each check at a glance. Start result work in SAFE here.",
            """{"type":"object","properties":{"cases":{"type":"array","items":{"type":"string"}},"combinations":{"type":"array","items":{"type":"string"}}}}""",
            Ask.Never, Overview),

        new("check_punching",
            "Punching shear check from SAFE's punching shear design table: every column or point with its location (interior, edge, corner), demand over capacity ratio and governing combination, the largest ratio, and every point above 'limit' (default 1.0). SAFE's design must have run, with the design code set in SAFE.",
            """{"type":"object","properties":{"limit":{"type":"number"},"list":{"type":"integer","description":"How many points above the limit to list (default 40)."}}}""",
            Ask.Never, Punching),

        new("check_deflection",
            "Slab deflection from SAFE's joint displacements: the largest downward deflection per load case or combination and where it is. With 'span' and 'ratio' (for example span 6 and ratio 240 for span/240) or with 'limit' (an allowed deflection), each case gets pass or fail. For long-term deflection, name the nonlinear (cracked, long-term) load case in 'cases'; the engineer chooses which case and limit the code asks for.",
            """
            {"type":"object","properties":{
              "span":{"type":"number"},"ratio":{"type":"number","description":"Span over this, e.g. 240 or 480."},
              "limit":{"type":"number","description":"Allowed deflection in the model's length unit."},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}}}}
            """, Ask.Never, Deflection),

        new("strip_forces",
            "Design strip forces from SAFE's strip forces table: per strip, the largest positive and negative moment and the largest shear, with the station and load case. Give 'strips' to keep some, and 'cases' or 'combinations'.",
            """
            {"type":"object","properties":{
              "strips":{"type":"array","items":{"type":"string"}},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}},
              "list":{"type":"integer","description":"How many strips to list (default 60)."}}}
            """, Ask.Never, StripForces),

        new("check_soil_pressure",
            "Soil pressure under a mat or footing from SAFE's soil pressure table: the largest bearing pressure per load case or combination and where, against 'allowable' (the allowable bearing pressure, in force per area of the model) when given, and the points that lift off (tension or zero pressure under compression-only soil). Use the service combinations for bearing.",
            """
            {"type":"object","properties":{
              "allowable":{"type":"number"},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}}}}
            """, Ask.Never, SoilPressure),

        new("slab_design",
            "Slab and beam design from SAFE's design tables (strip based or finite element based, for the code set in SAFE): for each table, the rows, how many have a failing or warning status, the largest required reinforcement, and the rows that fail with their strip or beam, span and combination. SAFE's design must have run.",
            """{"type":"object","properties":{"table":{"type":"string","description":"One design table only."},"list":{"type":"integer","description":"How many failing rows to list per table (default 40)."}}}""",
            Ask.Never, SlabDesign),

        new("safe_check_report",
            "Runs every SAFE check in one go (punching shear, deflection, strip forces, soil pressure, slab design) and gives one list of findings with pass, fail or not available for each. Pass the engineer's limits. Put it in a document with make_document if the engineer wants a file.",
            """
            {"type":"object","properties":{
              "punching_limit":{"type":"number"},
              "span":{"type":"number"},"ratio":{"type":"number"},"deflection_limit":{"type":"number"},
              "allowable":{"type":"number","description":"Allowable bearing pressure."},
              "cases":{"type":"array","items":{"type":"string"}},
              "combinations":{"type":"array","items":{"type":"string"}}}}
            """, Ask.Never, (a, e) => Report(a, e)),
    ];

    private static int Count(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static void Ok(object result, string what)
    {
        if (Convert.ToInt32(result, CultureInfo.InvariantCulture) != 0) throw new EtabsException($"SAFE refused to {what}.");
    }

    // ---- modelling ----

    private static JsonNode AddSlabSection(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        var material = a.Need("material");
        var thickness = a.Number("thickness");
        if (thickness <= 0) throw new ToolInputException("'thickness' must be more than 0.");
        if (!e.Materials().Contains(material, StringComparer.OrdinalIgnoreCase))
            throw new ToolInputException($"There is no material '{material}'. Materials: {string.Join(", ", e.Materials())}.");
        var type = a.Text("type", "slab").ToLowerInvariant();
        var slabType = type switch { "slab" => 0, "drop" => 1, "ribbed" => 3, "waffle" => 4, "mat" => 5, "footing" => 6, _ => throw new ToolInputException("'type' is slab, drop, mat, footing, ribbed or waffle.") };
        var thick = a.Flag("thick_plate", type is "mat" or "footing");
        e.Raw("define a slab property", m => { Ok(m.PropArea.SetSlab(name, slabType, thick ? 2 : 1, material, thickness, -1, "", ""), $"define the slab property {name}"); return null; });
        return new JsonObject { ["defined"] = name, ["type"] = type, ["material"] = material, ["thickness"] = thickness, ["thick_plate"] = thick };
    }

    private static List<string> SlabAreas(JsonElement a, ICsiLink e)
    {
        var named = a.Texts("areas");
        var areas = e.Areas();
        if (named.Count > 0)
        {
            var have = areas.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            var missing = named.Where(n => !have.Contains(n)).ToList();
            if (missing.Count > 0) throw new ToolInputException($"There is no area {string.Join(", ", missing.Take(10))}.");
            return named;
        }
        var section = a.Text("section");
        var picked = areas.Where(r => r.Kind == "floor" && (section.Length == 0 || string.Equals(r.Section, section, StringComparison.OrdinalIgnoreCase))).Select(r => r.Name).ToList();
        if (picked.Count == 0) throw new ToolInputException(section.Length > 0 ? $"No slab area has the property '{section}'." : "The model has no slab areas.");
        return picked;
    }

    private static JsonNode AddSoil(JsonElement a, ICsiLink e)
    {
        var name = a.Text("name", "SOIL1");
        var k = a.Number("subgrade");
        if (k <= 0) throw new ToolInputException("'subgrade' must be more than 0.");
        var compressionOnly = a.Flag("compression_only", true);
        var areas = SlabAreas(a, e);
        e.Raw("define the soil", m =>
        {
            // U3 is the vertical stiffness per area; nonlinear option 1 is compression only. Spring option 1: user specified.
            Ok(m.PropAreaSpring.SetAreaSpringProp(name, 0.0, 0.0, k, compressionOnly ? 1 : 0, 1, "", 0.0, 0.0, -1, "", ""), $"define the soil property {name}");
            foreach (var area in areas) Ok(m.AreaObj.SetSpringAssignment(area, name, 0), $"put the soil under area {area}");
            return null;
        });
        return new JsonObject { ["soil"] = name, ["subgrade"] = k, ["compression_only"] = compressionOnly, ["areas"] = areas.Count, ["first"] = new JsonArray(areas.Take(10).Select(n => (JsonNode)n).ToArray()) };
    }

    /// <summary>The slab level: the elevation most slab corners sit at (0 when there are none).</summary>
    private static double SlabLevel(ICsiLink e) =>
        e.Areas().Where(r => r.Kind == "floor").SelectMany(r => r.Corners).GroupBy(c => Math.Round(c.Z, 4)).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    /// <summary>The points named, and a point at each plan point given (an existing one within a small distance, or a new one).</summary>
    private static List<string> PointsFor(JsonElement a, ICsiLink e)
    {
        var names = a.Texts("points");
        if (names.Count > 0)
        {
            var have = e.Points().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var missing = names.Where(n => !have.Contains(n)).ToList();
            if (missing.Count > 0) throw new ToolInputException($"There is no point {string.Join(", ", missing.Take(10))}.");
        }
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Array)
        {
            var z = a.Number("z", SlabLevel(e));
            var existing = e.Points();
            foreach (var p in at.EnumerateArray().Select(p => p.Point()))
            {
                var near = existing.FirstOrDefault(q => Math.Abs(q.X - p.X) < 1e-3 && Math.Abs(q.Y - p.Y) < 1e-3 && Math.Abs(q.Z - z) < 1e-3);
                if (near is not null) { names.Add(near.Name); continue; }
                var added = (string)e.Raw("add a point", m => { string n = ""; Ok(m.PointObj.AddCartesian(p.X, p.Y, z, ref n, "", "Global", false, 0), $"add a point at {F(p.X)}, {F(p.Y)}"); return n; })!;
                names.Add(added);
            }
        }
        if (names.Count == 0) throw new ToolInputException("Give 'points' by name or 'at' as plan points [x, y].");
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static JsonNode AddPointSprings(JsonElement a, ICsiLink e)
    {
        var name = a.Need("name");
        double[] k = [a.Number("kx", 0), a.Number("ky", 0), a.Number("kz"), a.Number("krx", 0), a.Number("kry", 0), a.Number("krz", 0)];
        if (k[2] <= 0) throw new ToolInputException("'kz' must be more than 0.");
        var points = PointsFor(a, e);
        e.Raw("define a point spring", m =>
        {
            double[] values = k.ToArray();
            Ok(m.PropPointSpring.SetPointSpringProp(name, 1, ref values, "Global", "", "", 0.0, -1, "", ""), $"define the point spring {name}");
            foreach (var p in points) Ok(m.PointObj.SetSpringAssignment(p, name, 0), $"give the spring to point {p}");
            return null;
        });
        return new JsonObject { ["spring"] = name, ["points"] = new JsonArray(points.Select(p => (JsonNode)p).ToArray()) };
    }

    private static JsonNode AddPointSupports(JsonElement a, ICsiLink e)
    {
        var type = a.Text("type", "pinned").ToLowerInvariant();
        bool[] restraint = type switch
        {
            "pinned" => [true, true, true, false, false, false],
            "fixed" => [true, true, true, true, true, true],
            "vertical" => [false, false, true, false, false, false],
            _ => throw new ToolInputException("'type' is pinned, fixed or vertical."),
        };
        var points = PointsFor(a, e);
        foreach (var p in points) e.SetRestraint(p, restraint);
        return new JsonObject { ["type"] = type, ["points"] = new JsonArray(points.Select(p => (JsonNode)p).ToArray()) };
    }

    private static JsonNode AddPointLoads(JsonElement a, ICsiLink e)
    {
        var pattern = a.Need("pattern");
        if (!e.LoadPatterns().Any(p => string.Equals(p.Name, pattern, StringComparison.OrdinalIgnoreCase)))
            throw new ToolInputException($"There is no load pattern '{pattern}'. Add it with add_load_pattern first.");
        var force = a.Number("force");
        var replace = a.Flag("replace");
        var points = PointsFor(a, e);
        e.Raw("put point loads", m =>
        {
            foreach (var p in points)
            {
                // Global Z is up, so a load downwards is a negative F3.
                double[] value = [0, 0, -force, a.Number("mx", 0), a.Number("my", 0), 0];
                Ok(m.PointObj.SetLoadForce(p, pattern, ref value, replace, "Global", 0), $"put the load on point {p}");
            }
            return null;
        });
        return new JsonObject { ["pattern"] = pattern, ["force"] = force, ["points"] = new JsonArray(points.Select(p => (JsonNode)p).ToArray()) };
    }

    private static JsonNode ListStrips(JsonElement a, ICsiLink e)
    {
        var limit = a.Whole("limit", 200);
        return e.Raw("list the design strips", m =>
        {
            int n = 0; string[] names = [];
            Ok(m.DesignConcreteSlab.DesignStrip.GetNameList(ref n, ref names), "list the design strips");
            var strips = new JsonArray();
            foreach (var name in names.Take(Math.Min(n, limit)))
            {
                int type = 0; string[] pts = []; double[] x = [], y = [], z = [], wbl = [], wbr = [], wal = [], war = []; bool[] auto = [];
                var row = new JsonObject { ["name"] = name };
                try
                {
                    Ok(m.DesignConcreteSlab.DesignStrip.GetDesignStrip_1(name, ref type, ref pts, ref x, ref y, ref z, ref wbl, ref wbr, ref wal, ref war, ref auto), $"read strip {name}");
                    row["design_type"] = type;
                    row["points"] = new JsonArray(Enumerable.Range(0, pts.Length).Select(i => (JsonNode)new JsonArray(F(x[i]), F(y[i]), F(i < wal.Length ? wal[i] : 0), F(i < war.Length ? war[i] : 0))).ToArray());
                }
                catch (Exception ex) when (ex is EtabsException or MissingMethodException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { row["note"] = "Its points could not be read."; }
                strips.Add(row);
            }
            return new JsonObject { ["count"] = n, ["strips"] = strips, ["point_columns"] = "x, y, width left, width right" };
        }) as JsonNode ?? new JsonObject();
    }

    private static TableName FindTable(ICsiLink e, string name)
    {
        var known = e.Tables();
        return known.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase))
            ?? known.FirstOrDefault(t => ResultTools.Norm(t.Name) == ResultTools.Norm(name))
            ?? throw new ToolInputException($"There is no table '{name}'. Find its name with list_tables.");
    }

    private static Dictionary<string, string> Strings(JsonElement o)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in o.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return d;
    }

    private static JsonNode EditTable(JsonElement a, ICsiLink e)
    {
        var table = FindTable(e, a.Need("table"));
        var action = a.Need("action").ToLowerInvariant();
        if (action == "fields")
            return e.Raw("read a table's fields", m =>
            {
                int version = 0, n = 0; string[] keys = [], names = [], about = [], units = []; bool[] importable = [];
                Ok(m.DatabaseTables.GetAllFieldsInTable(table.Key, ref version, ref n, ref keys, ref names, ref about, ref units, ref importable), $"read the fields of {table.Name}");
                return new JsonObject
                {
                    ["table"] = table.Name, ["key"] = table.Key,
                    ["fields"] = new JsonArray(Enumerable.Range(0, n).Select(i => (JsonNode)new JsonObject { ["key"] = keys[i], ["name"] = names[i], ["units"] = i < units.Length ? units[i] : "", ["about"] = i < about.Length ? about[i] : "", ["editable"] = i < importable.Length && importable[i] }).ToArray()),
                };
            }) as JsonNode ?? new JsonObject();

        var (fields, rows) = ((string[], List<string[]>))e.Raw("read a table for editing", m =>
        {
            int version = 0, records = 0; string[] f = [], data = [];
            Ok(m.DatabaseTables.GetTableForEditingArray(table.Key, "", ref version, ref f, ref records, ref data), $"open {table.Name} for editing (is it an editable table?)");
            var list = new List<string[]>();
            for (var r = 0; r < records && f.Length > 0; r++) list.Add(data.Skip(r * f.Length).Take(f.Length).Select(v => v ?? "").ToArray());
            return (f, list);
        })!;

        if (action == "read")
        {
            var limit = a.Whole("limit", 100);
            return new JsonObject
            {
                ["table"] = table.Name, ["fields"] = new JsonArray(fields.Select(f => (JsonNode)f).ToArray()), ["rows_total"] = rows.Count,
                ["rows"] = new JsonArray(rows.Take(limit).Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)v).ToArray())).ToArray()),
            };
        }

        int Field(string name)
        {
            var i = Array.FindIndex(fields, f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = Array.FindIndex(fields, f => ResultTools.Norm(f) == ResultTools.Norm(name));
            return i >= 0 ? i : throw new ToolInputException($"{table.Name} has no field '{name}'. Its fields: {string.Join(", ", fields)}.");
        }

        var changed = 0;
        if (action == "add")
        {
            foreach (var item in a.Items("rows"))
            {
                var row = Enumerable.Repeat("", fields.Length).ToArray();
                foreach (var (k, v) in Strings(item)) row[Field(k)] = v;
                rows.Add(row);
                changed++;
            }
        }
        else if (action == "update")
        {
            var match = Strings(a.TryGetProperty("match", out var mm) ? mm : default).Select(p => (Index: Field(p.Key), p.Value)).ToList();
            var set = Strings(a.TryGetProperty("set", out var ss) ? ss : default).Select(p => (Index: Field(p.Key), p.Value)).ToList();
            if (match.Count == 0 || set.Count == 0) throw new ToolInputException("'update' needs 'match' and 'set'.");
            foreach (var row in rows.Where(r => match.All(c => string.Equals(r[c.Index], c.Value, StringComparison.OrdinalIgnoreCase))))
            {
                foreach (var (i, v) in set) row[i] = v;
                changed++;
            }
            if (changed == 0) throw new ToolInputException($"No row of {table.Name} matches. Read it with action 'read' first.");
        }
        else throw new ToolInputException("'action' is fields, read, add or update.");

        var log = (JsonObject)e.Raw("apply the table edit", m =>
        {
            int version = 0; string[] f = fields.ToArray(); string[] data = rows.SelectMany(r => r).ToArray();
            Ok(m.DatabaseTables.SetTableForEditingArray(table.Key, ref version, ref f, rows.Count, ref data), $"take the edit of {table.Name}");
            int fatal = 0, errors = 0, warnings = 0, infos = 0; string text = "";
            m.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref infos, ref text);
            return new JsonObject { ["fatal"] = fatal, ["errors"] = errors, ["warnings"] = warnings, ["log"] = text.Length > 3000 ? text[..3000] + " ..." : text };
        })!;
        log["table"] = table.Name;
        log["rows_changed"] = changed;
        log["note"] = (int)log["fatal"]! > 0 || (int)log["errors"]! > 0
            ? "SAFE did not take all of the edit: read the log, fix the rows and try again."
            : "SAFE took the edit. Read it back with action 'read'.";
        return log;
    }

    // ---- reading SAFE's tables ----

    /// <summary>The tables with data whose name holds every one of the words (letters and digits only, any case).</summary>
    internal static List<TableName> Like(IReadOnlyList<TableName> known, params string[] words) =>
        known.Where(t => !t.Empty && words.All(w => ResultTools.Norm(t.Name).Contains(ResultTools.Norm(w), StringComparison.Ordinal))).ToList();

    /// <summary>The first field whose name holds one of the fragments; -1 when none.</summary>
    internal static int ColLike(IReadOnlyList<string> fields, params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            var want = ResultTools.Norm(fragment);
            for (var i = 0; i < fields.Count; i++)
                if (ResultTools.Norm(fields[i]).Contains(want, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static TableData? Read(ICsiLink e, TableName t, JsonElement a)
    {
        var cases = a.Texts("cases");
        var combos = a.Texts("combinations");
        if (cases.Count == 0 && combos.Count == 0) { cases = [.. e.LoadCases()]; combos = [.. e.Combos()]; }
        try { var data = e.ReadTable(t.Key, cases, combos); return data.Rows.Count > 0 ? data : null; }
        catch (EtabsException) { return null; }
    }

    /// <summary>The first of the tables that reads with data, and its name.</summary>
    private static (TableData Data, string Name)? First(ICsiLink e, IEnumerable<TableName> tables, JsonElement a)
    {
        foreach (var t in tables)
            if (Read(e, t, a) is { } data) return (data, t.Name);
        return null;
    }

    private static JsonObject Missing(string what, string looked) => new()
    {
        ["status"] = "not available",
        ["note"] = $"SAFE has no {looked} table with data, so {what} could not be checked. Run the analysis (and the design, for design checks) in SAFE first.",
    };

    private static double? Num(string text) => ResultTools.Num(text);

    private static string Cell(string[] row, int i) => i >= 0 && i < row.Length ? row[i] : "";

    private static string NameField(IReadOnlyList<string> fields, params string[] first)
    {
        var i = ResultTools.Col(fields, first);
        return i >= 0 ? fields[i] : "";
    }

    // ---- punching ----

    internal static JsonNode Punching(JsonElement a, ICsiLink e)
    {
        var limit = a.Number("limit", 1.0);
        var list = a.Whole("list", 40);
        var found = First(e, Like(e.Tables(), "punch"), a);
        if (found is not { } f) return Missing("punching shear", "punching shear design");
        var (data, name) = f;
        var ratio = ResultTools.Col(data.Fields, "Ratio", "ShearRatio", "StressRatio", "DCRatio", "PunchRatio");
        if (ratio < 0) ratio = ColLike(data.Fields, "ratio");
        if (ratio < 0) return new JsonObject { ["status"] = "not available", ["table"] = name, ["note"] = $"'{name}' has no ratio field. Its fields: {string.Join(", ", data.Fields)}." };
        var point = ResultTools.Col(data.Fields, "Point", "PointLabel", "Label", "UniqueName", "Column", "ColumnLabel");
        var location = ResultTools.Col(data.Fields, "Location", "Loc", "Position");
        var combo = ColLike(data.Fields, "combo");
        var status = ResultTools.Col(data.Fields, "Status");
        var rows = data.Rows.Select(r => (Row: r, Ratio: Num(Cell(r, ratio)))).ToList();
        var valued = rows.Where(r => r.Ratio is not null).OrderByDescending(r => r.Ratio).ToList();
        var notCalculated = rows.Where(r => r.Ratio is null || status >= 0 && !Cell(r.Row, status).Equals("OK", StringComparison.OrdinalIgnoreCase) && Cell(r.Row, status).Length > 0 && r.Ratio <= limit).ToList();
        var over = valued.Where(r => r.Ratio > limit).ToList();
        JsonNode Item((string[] Row, double? Ratio) r) => new JsonObject
        {
            ["point"] = Cell(r.Row, point), ["location"] = Cell(r.Row, location), ["ratio"] = r.Ratio is { } v ? Math.Round(v, 3) : null,
            ["combination"] = Cell(r.Row, combo), ["design_status"] = Cell(r.Row, status),
        };
        return new JsonObject
        {
            ["status"] = over.Count > 0 ? "fail" : valued.Count > 0 ? "pass" : "not available",
            ["table"] = name, ["limit"] = limit, ["limit_is_default"] = !a.TryGetProperty("limit", out _),
            ["points"] = rows.Count,
            ["largest"] = valued.Count > 0 ? Item(valued[0]) : null,
            ["above_limit"] = over.Count,
            ["above"] = new JsonArray(over.Take(list).Select(Item).ToArray()),
            ["not_checked_or_warning"] = new JsonArray(notCalculated.Take(20).Select(Item).ToArray()),
            ["note"] = "Ratios are SAFE's own punching shear design results for the code set in SAFE. Points SAFE could not check (no ratio, or a status other than OK) are listed apart.",
        };
    }

    // ---- deflection ----

    internal static JsonNode Deflection(JsonElement a, ICsiLink e)
    {
        var table = Like(e.Tables(), "joint", "displacement").Where(t => !ResultTools.Norm(t.Name).Contains("diaphragm", StringComparison.Ordinal)).ToList();
        var found = First(e, table, a);
        if (found is not { } f) return Missing("deflection", "joint displacements");
        var (data, name) = f;
        var uz = ResultTools.Col(data.Fields, "Uz", "U3");
        var caseField = ResultTools.Col(data.Fields, "OutputCase", "Output Case", "LoadCase", "Load Case/Combo", "Combo");
        var point = ResultTools.Col(data.Fields, "UniqueName", "Label", "Point", "Joint");
        if (uz < 0 || caseField < 0) return new JsonObject { ["status"] = "not available", ["table"] = name, ["note"] = $"'{name}' has no Uz or case field. Its fields: {string.Join(", ", data.Fields)}." };
        double? allowed = a.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble()
            : a.TryGetProperty("span", out var s) && s.ValueKind == JsonValueKind.Number && a.TryGetProperty("ratio", out var q) && q.ValueKind == JsonValueKind.Number && q.GetDouble() > 0 ? s.GetDouble() / q.GetDouble() : null;
        var perCase = data.Rows.Where(r => Num(Cell(r, uz)) is not null).GroupBy(r => Cell(r, caseField))
            .Select(g => g.OrderBy(r => Num(Cell(r, uz))).First())
            .Select(r => (Case: Cell(r, caseField), Point: Cell(r, point), Down: -Num(Cell(r, uz))!.Value)).OrderByDescending(c => c.Down).ToList();
        var fails = allowed is { } lim ? perCase.Count(c => c.Down > lim) : 0;
        return new JsonObject
        {
            ["status"] = perCase.Count == 0 ? "not available" : allowed is null ? "reported" : fails > 0 ? "fail" : "pass",
            ["table"] = name, ["allowed"] = allowed is { } al ? Math.Round(al, 6) : null,
            ["cases"] = new JsonArray(perCase.Take(40).Select(c => (JsonNode)new JsonObject
            {
                ["case"] = c.Case, ["largest_down"] = Math.Round(c.Down, 6), ["point"] = c.Point,
                ["verdict"] = allowed is { } lim2 ? c.Down > lim2 ? "fail" : "pass" : null,
            }).ToArray()),
            ["note"] = "Deflections are SAFE's joint displacements (downward positive here), measured from the supports' level, not relative to the supports. "
                + (allowed is null ? "No limit was given, so there is no verdict: give span and ratio (span/ratio) or limit. " : "")
                + "Long-term deflection needs SAFE's cracked long-term load case: name it in 'cases'.",
        };
    }

    // ---- strip forces ----

    internal static JsonNode StripForces(JsonElement a, ICsiLink e)
    {
        var found = First(e, Like(e.Tables(), "strip", "force"), a);
        if (found is not { } f) return Missing("strip forces", "strip forces");
        var (data, name) = f;
        var strip = ResultTools.Col(data.Fields, "Strip", "StripName", "DesignStrip");
        var station = ResultTools.Col(data.Fields, "Station", "Distance");
        var caseField = ResultTools.Col(data.Fields, "OutputCase", "Output Case", "LoadCase", "Combo");
        var moments = data.Fields.Select((x, i) => (x, i)).Where(p => ResultTools.Norm(p.x) is "m2" or "m3" or "moment" or "m").Select(p => p.i).ToList();
        if (moments.Count == 0) moments = data.Fields.Select((x, i) => (x, i)).Where(p => ResultTools.Norm(p.x).StartsWith("m", StringComparison.Ordinal) && ResultTools.Norm(p.x).Length <= 3).Select(p => p.i).ToList();
        var shears = data.Fields.Select((x, i) => (x, i)).Where(p => ResultTools.Norm(p.x) is "v2" or "v3" or "shear" or "v").Select(p => p.i).ToList();
        if (strip < 0 || moments.Count == 0) return new JsonObject { ["status"] = "not available", ["table"] = name, ["note"] = $"'{name}' has no strip or moment field. Its fields: {string.Join(", ", data.Fields)}." };
        var keep = a.Texts("strips").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = a.Whole("list", 60);
        var strips = new JsonArray();
        foreach (var g in data.Rows.Where(r => keep.Count == 0 || keep.Contains(Cell(r, strip))).GroupBy(r => Cell(r, strip)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(list))
        {
            var o = new JsonObject { ["strip"] = g.Key };
            foreach (var mi in moments)
            {
                var vals = g.Select(r => (Row: r, V: Num(Cell(r, mi)))).Where(x => x.V is not null).ToList();
                if (vals.Count == 0) continue;
                var max = vals.MaxBy(x => x.V)!; var min = vals.MinBy(x => x.V)!;
                o[data.Fields[mi] + "_max"] = new JsonObject { ["value"] = Math.Round(max.V!.Value, 4), ["station"] = Cell(max.Row, station), ["case"] = Cell(max.Row, caseField) };
                o[data.Fields[mi] + "_min"] = new JsonObject { ["value"] = Math.Round(min.V!.Value, 4), ["station"] = Cell(min.Row, station), ["case"] = Cell(min.Row, caseField) };
            }
            foreach (var vi in shears)
            {
                var vals = g.Select(r => (Row: r, V: Num(Cell(r, vi)))).Where(x => x.V is not null).ToList();
                if (vals.Count == 0) continue;
                var big = vals.MaxBy(x => Math.Abs(x.V!.Value))!;
                o[data.Fields[vi] + "_largest"] = new JsonObject { ["value"] = Math.Round(big.V!.Value, 4), ["station"] = Cell(big.Row, station), ["case"] = Cell(big.Row, caseField) };
            }
            strips.Add(o);
        }
        return new JsonObject
        {
            ["status"] = strips.Count > 0 ? "reported" : "not available",
            ["table"] = name, ["strips"] = strips,
            ["note"] = "Moments and shears are SAFE's integrated strip forces per strip width, in the model's units, with the station along the strip and the load case.",
        };
    }

    // ---- soil pressure ----

    internal static JsonNode SoilPressure(JsonElement a, ICsiLink e)
    {
        var found = First(e, Like(e.Tables(), "soil", "pressure"), a);
        if (found is not { } f) return Missing("soil pressure", "soil pressure");
        var (data, name) = f;
        var pressure = ResultTools.Col(data.Fields, "Pressure", "SoilPressure", "Soil Pressure");
        if (pressure < 0) pressure = ColLike(data.Fields, "pressure");
        var caseField = ResultTools.Col(data.Fields, "OutputCase", "Output Case", "LoadCase", "Combo");
        var point = ResultTools.Col(data.Fields, "UniqueName", "Point", "Label", "Joint");
        var x = ResultTools.Col(data.Fields, "GlobalX", "X");
        var y = ResultTools.Col(data.Fields, "GlobalY", "Y");
        if (pressure < 0 || caseField < 0) return new JsonObject { ["status"] = "not available", ["table"] = name, ["note"] = $"'{name}' has no pressure or case field. Its fields: {string.Join(", ", data.Fields)}." };
        double? allowable = a.TryGetProperty("allowable", out var al) && al.ValueKind == JsonValueKind.Number ? al.GetDouble() : null;
        // SAFE gives bearing (compression) as a negative pressure.
        var cases = new JsonArray();
        var fails = 0;
        foreach (var g in data.Rows.Where(r => Num(Cell(r, pressure)) is not null).GroupBy(r => Cell(r, caseField)))
        {
            var worst = g.MinBy(r => Num(Cell(r, pressure)))!;
            var bearing = -Num(Cell(worst, pressure))!.Value;
            var lifting = g.Count(r => Num(Cell(r, pressure)) >= 0);
            var fail = allowable is { } lim && bearing > lim;
            if (fail) fails++;
            cases.Add(new JsonObject
            {
                ["case"] = g.Key, ["largest_bearing"] = Math.Round(bearing, 4), ["point"] = Cell(worst, point),
                ["at"] = x >= 0 && y >= 0 ? $"{Cell(worst, x)}, {Cell(worst, y)}" : null,
                ["points_lifting_off"] = lifting, ["points"] = g.Count(),
                ["verdict"] = allowable is null ? null : fail ? "fail" : "pass",
            });
        }
        return new JsonObject
        {
            ["status"] = cases.Count == 0 ? "not available" : allowable is null ? "reported" : fails > 0 ? "fail" : "pass",
            ["table"] = name, ["allowable"] = allowable, ["cases"] = cases,
            ["note"] = "Bearing pressure is SAFE's soil pressure, compression shown positive here. Points lifting off have zero or tension pressure; with compression-only soil, many of them mean the mat loses contact there. "
                + (allowable is null ? "No allowable pressure was given, so there is no verdict." : "Check bearing with the service combinations."),
        };
    }

    // ---- slab design ----

    internal static bool IsDesignTable(string name)
    {
        var n = ResultTools.Norm(name);
        if (n.Contains("punch", StringComparison.Ordinal) || n.Contains("preference", StringComparison.Ordinal) || n.Contains("overwrite", StringComparison.Ordinal)) return false;
        return n.Contains("design", StringComparison.Ordinal) && (n.Contains("slab", StringComparison.Ordinal) || n.Contains("strip", StringComparison.Ordinal) || n.Contains("beam", StringComparison.Ordinal));
    }

    internal static JsonNode SlabDesign(JsonElement a, ICsiLink e)
    {
        var only = a.Text("table");
        var list = a.Whole("list", 40);
        var tables = e.Tables().Where(t => !t.Empty && (only.Length > 0 ? string.Equals(t.Name, only, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Key, only, StringComparison.OrdinalIgnoreCase) : IsDesignTable(t.Name))).ToList();
        var results = new JsonArray();
        var failing = 0;
        foreach (var t in tables)
        {
            if (Read(e, t, a) is not { } data) continue;
            var status = ResultTools.Col(data.Fields, "Status", "DesignStatus", "Warnings", "ErrMsg");
            var id = ResultTools.Col(data.Fields, "Strip", "StripName", "Beam", "Label", "UniqueName", "Area");
            var span = ResultTools.Col(data.Fields, "Span", "SpanID", "Station", "Location");
            var combo = ColLike(data.Fields, "combo");
            var steel = data.Fields.Select((x, i) => (x, i)).Where(p => { var n = ResultTools.Norm(p.x); return n.StartsWith("as", StringComparison.Ordinal) || n.Contains("rebar", StringComparison.Ordinal) || n.Contains("area", StringComparison.Ordinal) && n.Length <= 12; }).Select(p => p.i).ToList();
            var bad = status < 0 ? [] : data.Rows.Where(r => Cell(r, status) is { Length: > 0 } s && !s.Equals("OK", StringComparison.OrdinalIgnoreCase) && !s.Equals("No Message", StringComparison.OrdinalIgnoreCase) && !s.Equals("None", StringComparison.OrdinalIgnoreCase)).ToList();
            failing += bad.Count;
            var o = new JsonObject
            {
                ["table"] = t.Name, ["rows"] = data.Rows.Count, ["not_ok"] = bad.Count,
                ["rows_not_ok"] = new JsonArray(bad.Take(list).Select(r => (JsonNode)new JsonObject { ["id"] = Cell(r, id), ["span"] = Cell(r, span), ["combination"] = Cell(r, combo), ["status"] = Cell(r, status) }).ToArray()),
            };
            var most = new JsonObject();
            foreach (var si in steel)
            {
                var vals = data.Rows.Select(r => (Row: r, V: Num(Cell(r, si)))).Where(v => v.V is not null).ToList();
                if (vals.Count == 0) continue;
                var big = vals.MaxBy(v => v.V)!;
                most[data.Fields[si]] = new JsonObject { ["value"] = Math.Round(big.V!.Value, 6), ["at"] = Cell(big.Row, id), ["span"] = Cell(big.Row, span) };
            }
            if (most.Count > 0) o["largest_reinforcement"] = most;
            if (status < 0) o["note"] = "This table has no status field: read it with read_table for the details.";
            results.Add(o);
        }
        if (results.Count == 0) return Missing("slab design", "slab or beam design");
        return new JsonObject
        {
            ["status"] = failing > 0 ? "fail" : "pass", ["tables"] = results,
            ["note"] = "Statuses and reinforcement are SAFE's own design results for the code set in SAFE. 'not_ok' counts rows whose status is not OK (failures and warnings).",
        };
    }

    // ---- overview and report ----

    private static JsonNode Overview(JsonElement a, ICsiLink e)
    {
        var known = e.Tables();
        var groups = new (string Name, Func<TableName, bool> Is)[]
        {
            ("punching shear", t => ResultTools.Norm(t.Name).Contains("punch", StringComparison.Ordinal)),
            ("strip forces", t => ResultTools.Norm(t.Name).Contains("strip", StringComparison.Ordinal) && ResultTools.Norm(t.Name).Contains("force", StringComparison.Ordinal)),
            ("soil pressure", t => ResultTools.Norm(t.Name).Contains("soilpressure", StringComparison.Ordinal)),
            ("displacements", t => ResultTools.Norm(t.Name).Contains("displacement", StringComparison.Ordinal)),
            ("reactions", t => ResultTools.Norm(t.Name).Contains("reaction", StringComparison.Ordinal)),
            ("slab and beam design", t => IsDesignTable(t.Name)),
        };
        var tables = new JsonObject();
        foreach (var (name, @is) in groups)
            tables[name] = new JsonArray(known.Where(t => !t.Empty && @is(t)).Select(t => (JsonNode)t.Name).ToArray());
        var result = new JsonObject { ["analysis_run"] = e.Info().Locked, ["tables_with_data"] = tables };
        if (First(e, Like(known, "base", "reaction"), a) is { } br)
        {
            var (data, name) = br;
            var c = ResultTools.Col(data.Fields, "OutputCase", "Output Case", "LoadCase");
            var fz = ResultTools.Col(data.Fields, "FZ", "Fz", "F3");
            if (c >= 0 && fz >= 0)
                result["base_reactions"] = new JsonObject { ["table"] = name, ["fz"] = new JsonArray(data.Rows.Take(40).Select(r => (JsonNode)new JsonObject { ["case"] = Cell(r, c), ["fz"] = Num(Cell(r, fz)) }).ToArray()) };
        }
        var quick = new JsonObject();
        if (Punching(a, e) is JsonObject p && p["largest"] is JsonObject pl) quick["largest_punching_ratio"] = pl["ratio"]?.DeepClone();
        if (SoilPressure(a, e) is JsonObject s && s["cases"] is JsonArray sc && sc.Count > 0) quick["largest_bearing"] = sc.Max(x => x?["largest_bearing"]?.GetValue<double>() ?? 0);
        if (Deflection(a, e) is JsonObject d && d["cases"] is JsonArray dc && dc.Count > 0) quick["largest_deflection"] = dc[0]?["largest_down"]?.DeepClone();
        result["at_a_glance"] = quick;
        if (!e.Info().Locked) result["note"] = "SAFE's analysis has not run (the model is not locked), so there are no results yet. run_analysis runs it after the engineer agrees.";
        return result;
    }

    /// <summary>Every SAFE check in one go: what the review pack carries for a SAFE model.</summary>
    public static JsonNode Report(JsonElement a, ICsiLink e)
    {
        var info = e.Info();
        var parts = new (string Name, Func<JsonNode> Run)[]
        {
            ("punching_shear", () => Punching(ResultArgs(a, ("limit", "punching_limit")), e)),
            ("deflection", () => Deflection(ResultArgs(a, ("limit", "deflection_limit"), ("span", "span"), ("ratio", "ratio")), e)),
            ("soil_pressure", () => SoilPressure(ResultArgs(a, ("allowable", "allowable")), e)),
            ("strip_forces", () => StripForces(ResultArgs(a), e)),
            ("slab_design", () => SlabDesign(ResultArgs(a), e)),
        };
        var checks = new JsonObject();
        var summary = new JsonArray();
        foreach (var (name, run) in parts)
        {
            JsonNode node;
            try { node = run(); }
            catch (Exception ex) when (ex is EtabsException or ToolInputException) { node = new JsonObject { ["status"] = "not available", ["note"] = ex.Message }; }
            checks[name] = node;
            summary.Add($"{name.Replace('_', ' ')}: {node["status"]?.GetValue<string>() ?? "reported"}");
        }
        return new JsonObject
        {
            ["program"] = "SAFE", ["model"] = info.File, ["units"] = $"{info.Force}, {info.Length}", ["analysis_run"] = info.Locked,
            ["summary"] = summary, ["checks"] = checks,
            ["note"] = "Every value is read from SAFE's own result and design tables. Limits that were not given are the defaults named in each check, or there is no verdict; confirm them against the project's code.",
        };
    }

    /// <summary>The cases and combinations of the arguments, with the named limits passed on under the names each check reads.</summary>
    private static JsonElement ResultArgs(JsonElement a, params (string To, string From)[] renames)
    {
        var o = new JsonObject();
        if (a.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "cases", "combinations" })
                if (a.TryGetProperty(key, out var v)) o[key] = JsonNode.Parse(v.GetRawText());
            foreach (var (to, from) in renames)
                if (a.TryGetProperty(from, out var v) && v.ValueKind == JsonValueKind.Number) o[to] = v.GetDouble();
        }
        return JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
    }
}
