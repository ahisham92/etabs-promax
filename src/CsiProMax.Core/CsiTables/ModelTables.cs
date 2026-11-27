using System.Globalization;
using CsiProMax.E2k;
using CsiProMax.Model;

namespace CsiProMax.CsiTables;

/// <summary>One table of the model, the way SAP2000's and ETABS's interactive database shows it: its name, its field
/// names, the unit of each field ("" for none) and one row of text per record.
/// <see cref="FirstRow"/> is the number the first data row has where it came from (Excel's row number), so a message
/// names the row the user sees.</summary>
public sealed record ModelTable(string Name, List<string> Fields, List<string> Units, List<List<string>> Rows, int FirstRow = 1)
{
    public int Field(string name) => Fields.FindIndex(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The model as interactive database tables (stories, grids, materials, sections, load patterns, combinations, objects,
/// assignments and loads), to export to Excel and import back the way SAP2000 and ETABS do: each table in the file
/// replaces the same table in the model, and tables not in the file are left as they are. Values in the file's own
/// units are converted to the model's. Pure, so the tests run it.
/// </summary>
public static class ModelTables
{
    public const string Base = "Tower and Base Story Definitions";
    public const string Stories = "Story Definitions";
    public const string Grids = "Grid Definitions - Grid Lines";
    public const string Concrete = "Material Properties - Concrete Data";
    public const string Rectangular = "Frame Section Property Definitions - Concrete Rectangular";
    public const string Slabs = "Slab Property Definitions";
    public const string Walls = "Wall Property Definitions";
    public const string Patterns = "Load Pattern Definitions";
    public const string Combos = "Load Combination Definitions";
    public const string Points = "Point Object Connectivity";
    public const string Frames = "Frame Object Connectivity";
    public const string Areas = "Area Object Connectivity";
    public const string FrameSections = "Frame Assignments - Section Properties";
    public const string AreaSections = "Area Assignments - Section Properties";
    public const string Restraints = "Joint Assignments - Restraints";
    public const string FrameLoads = "Frame Loads - Distributed";
    public const string AreaLoads = "Area Loads - Uniform";
    public const string JointLoads = "Joint Loads - Force";

    /// <summary>Every table, in the order they are exported and imported (definitions before what uses them).</summary>
    public static readonly string[] Names =
        [Base, Stories, Grids, Concrete, Rectangular, Slabs, Walls, Patterns, Combos, Points, Frames, Areas, FrameSections, AreaSections, Restraints, FrameLoads, AreaLoads, JointLoads];

    private static readonly string[] Dofs = ["UX", "UY", "UZ", "RX", "RY", "RZ"];

    // ---- units ----

    private static string ForceText(string unit) => unit.ToUpperInvariant() switch
    {
        "KN" => "kN", "N" => "N", "KGF" => "kgf", "TONF" => "tonf", "KIP" => "kip", "LB" => "lb", _ => unit,
    };

    /// <summary>The unit text of a field kind in the model's units: L, F, F/L, F/L2, F/L3, F-L, or "".</summary>
    public static string UnitText(StructuralModel m, string kind)
    {
        var f = ForceText(m.Units.Force);
        var l = m.Units.Length.ToLowerInvariant();
        return kind switch
        {
            "L" => l, "F" => f, "F/L" => f + "/" + l, "F/L2" => f + "/" + l + "²", "F/L3" => f + "/" + l + "³", "F-L" => f + "-" + l, "L2" => l + "²", _ => "",
        };
    }

    private static readonly Dictionary<string, double> Factors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["n"] = 1, ["kn"] = 1e3, ["mn"] = 1e6, ["kgf"] = 9.80665, ["kg"] = 9.80665, ["tonf"] = 9806.65, ["ton"] = 9806.65, ["t"] = 9806.65,
        ["kip"] = 4448.2216152605, ["kips"] = 4448.2216152605, ["lb"] = 4.4482216152605, ["lbf"] = 4.4482216152605,
        ["m"] = 1, ["mm"] = 1e-3, ["cm"] = 1e-2, ["ft"] = 0.3048, ["in"] = 0.0254,
    };

    /// <summary>The size of a unit text ("kN/m²", "kN-m", "mm", "N/mm2", "kip/ft^3") in newtons and metres; null when
    /// it is not one these tables use.</summary>
    public static double? UnitFactor(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var parts = text.Split('/');
        if (parts.Length > 2) return null;
        double? Side(string side, int sign)
        {
            var value = 1.0;
            foreach (var raw in side.Split(new[] { '-', '*', '·', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Replace("^", "", StringComparison.Ordinal);
                var power = 1;
                if (t.EndsWith('²') || t.EndsWith('2')) { power = 2; t = t[..^1]; }
                else if (t.EndsWith('³') || t.EndsWith('3')) { power = 3; t = t[..^1]; }
                if (!Factors.TryGetValue(t, out var f)) return null;
                value *= Math.Pow(f, power * sign);
            }
            return value;
        }
        var top = Side(parts[0], 1);
        if (top is null) return null;
        if (parts.Length == 1) return top;
        var bottom = Side(parts[1], -1);
        return bottom is null ? null : top * bottom;
    }

    // ---- export ----

    private sealed record Spec(string Name, (string Field, string Kind)[] Fields);

    private static readonly Spec[] Specs =
    [
        new(Base, [("BSName", ""), ("BSElev", "L")]),
        new(Stories, [("Story", ""), ("Height", "L"), ("Elevation", "L")]),
        new(Grids, [("Name", ""), ("LineType", ""), ("ID", ""), ("Ordinate", "L"), ("BubbleLoc", ""), ("Visible", "")]),
        new(Concrete, [("Material", ""), ("Fc", "F/L2"), ("E", "F/L2"), ("U", ""), ("UnitWeight", "F/L3")]),
        new(Rectangular, [("Name", ""), ("Material", ""), ("Depth", "L"), ("Width", "L"), ("DesignType", "")]),
        new(Slabs, [("Name", ""), ("Material", ""), ("Thickness", "L")]),
        new(Walls, [("Name", ""), ("Material", ""), ("Thickness", "L")]),
        new(Patterns, [("Name", ""), ("Type", ""), ("SelfWtMult", "")]),
        new(Combos, [("Name", ""), ("Type", ""), ("LoadName", ""), ("SF", "")]),
        new(Points, [("Label", ""), ("X", "L"), ("Y", "L"), ("Below", "L")]),
        new(Frames, [("Label", ""), ("Type", ""), ("PointI", ""), ("PointJ", ""), ("IStoriesBelow", "")]),
        new(Areas, [("Label", ""), ("Type", ""), ("NumPoints", ""), ("Points", ""), ("StoriesBelow", "")]),
        new(FrameSections, [("Story", ""), ("Label", ""), ("Section", "")]),
        new(AreaSections, [("Story", ""), ("Label", ""), ("Section", ""), ("Diaphragm", "")]),
        new(Restraints, [("Story", ""), ("Label", ""), ("UX", ""), ("UY", ""), ("UZ", ""), ("RX", ""), ("RY", ""), ("RZ", "")]),
        new(FrameLoads, [("Story", ""), ("Label", ""), ("LoadPattern", ""), ("Dir", ""), ("FOverL", "F/L")]),
        new(AreaLoads, [("Story", ""), ("Label", ""), ("LoadPattern", ""), ("Dir", ""), ("UnifLoad", "F/L2")]),
        new(JointLoads, [("Story", ""), ("Label", ""), ("LoadPattern", ""), ("FX", "F"), ("FY", "F"), ("FZ", "F"), ("MX", "F-L"), ("MY", "F-L"), ("MZ", "F-L")]),
    ];

    private static string N(double v) => CsiTableText.Number(v);
    private static string N(double? v) => v is { } d ? N(d) : "";

    private static bool IsBeamSection(StructuralModel m, string name)
    {
        var concrete = m.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS")?.Lines ?? [];
        return concrete.Any(l => ConcreteLineOf(l, name) && l.Contains("\"Beam\"", StringComparison.Ordinal));
    }

    private static bool ConcreteLineOf(string line, string section)
    {
        var tokens = E2kText.Tokenize(line);
        return tokens.Count > 1 && tokens[0] == "CONCRETESECTION" && E2kText.Unquote(tokens[1]) == section;
    }

    private static bool IsRectangular(FrameSection s) => s.Shape.Equals("Concrete Rectangular", StringComparison.OrdinalIgnoreCase);
    private static string PropType(ShellSection s) => s.Get("PROPTYPE") is { } t ? E2kText.Unquote(t) : "";

    private static string Uniform(List<string> tokens, out string dir, out double value)
    {
        dir = E2kText.Unquote(E2kText.Value(tokens, "DIR") ?? "GRAV");
        value = E2kText.TryNumber(E2kText.Value(tokens, "FVAL"), out var v) ? v : double.NaN;
        return E2kText.Unquote(E2kText.Value(tokens, "TYPE") ?? "");
    }

    private static bool IsUniform(ObjectLoad load) =>
        Uniform(load.Tokens, out _, out var v).Equals("UNIFF", StringComparison.OrdinalIgnoreCase) && !double.IsNaN(v);

    private static bool IsForce(PointLoad load) =>
        E2kText.Unquote(E2kText.Value(load.Tokens, "TYPE") ?? "FORCE").Equals("FORCE", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rows each table has in the model now (for a Choose Tables list).</summary>
    public static Dictionary<string, int> Counts(StructuralModel m) => Export(m).ToDictionary(t => t.Name, t => t.Rows.Count);

    /// <summary>The model's tables; <paramref name="only"/> picks some by name (all when null or empty).</summary>
    public static List<ModelTable> Export(StructuralModel m, IEnumerable<string>? only = null)
    {
        var wanted = only?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tables = new List<ModelTable>();
        foreach (var spec in Specs)
        {
            if (wanted is { Count: > 0 } && !wanted.Contains(spec.Name)) continue;
            tables.Add(new ModelTable(spec.Name, spec.Fields.Select(f => f.Field).ToList(), spec.Fields.Select(f => UnitText(m, f.Kind)).ToList(), Rows(m, spec.Name)));
        }
        // The rows of the programs' own tables the Modeler keeps (a material's other data, assignments and definitions
        // the .e2k has no keyword for), so nothing is lost out to Excel and back.
        tables.AddRange(ExtraTables.Export(m, wanted));
        return tables;
    }

    private static List<List<string>> Rows(StructuralModel m, string table)
    {
        var rows = new List<List<string>>();
        switch (table)
        {
            case Base:
                rows.Add([m.Stories[0].Name, N(m.Stories[0].Elevation)]);
                break;
            case Stories:
                for (var i = m.Stories.Count - 1; i >= 1; i--) rows.Add([m.Stories[i].Name, N(m.Stories[i].Height), N(m.Elevation(i))]);
                break;
            case Grids:
                foreach (var g in m.Grids)
                    rows.Add([m.GridSystem, g.Direction == "Y" ? "Y (Cartesian)" : "X (Cartesian)", g.Label, N(g.Coordinate),
                        E2kText.Unquote(E2kText.Value(g.Extra, "BUBBLELOC") ?? (g.Direction == "Y" ? "\"Start\"" : "\"End\"")), E2kText.Unquote(E2kText.Value(g.Extra, "VISIBLE") ?? "\"Yes\"")]);
                break;
            case Concrete:
                foreach (var x in m.Materials.Where(x => x.IsConcrete))
                    rows.Add([x.Name, N(x.Number("FC")), N(x.Number("E")), N(x.Number("U")), N(x.Number("WEIGHTPERVOLUME"))]);
                break;
            case Rectangular:
                foreach (var s in m.FrameSections.Where(IsRectangular))
                    rows.Add([s.Name, E2kText.Unquote(s.Material), N(s.Number("D")), N(s.Number("B")), IsBeamSection(m, s.Name) ? "Beam" : "Column"]);
                break;
            case Slabs or Walls:
                foreach (var s in m.ShellSections.Where(s => PropType(s).Equals(table == Slabs ? "Slab" : "Wall", StringComparison.OrdinalIgnoreCase)))
                    rows.Add([s.Name, E2kText.Unquote(s.Material), N(s.Thickness)]);
                break;
            case Patterns:
                foreach (var p in m.LoadPatterns) rows.Add([p.Name, p.Type, N(p.SelfWeight)]);
                break;
            case Combos:
                foreach (var c in m.Combos)
                {
                    var type = E2kText.Unquote(c.Lines.Select(l => E2kText.Value(l, "TYPE")).FirstOrDefault(v => v is not null) ?? "\"Linear Add\"");
                    var first = true;
                    foreach (var l in c.Lines.Where(l => l.Count >= 2 && l[0] is "LOADCASE" or "LOADCOMBO"))
                    {
                        rows.Add([c.Name, first ? type : "", E2kText.Unquote(l[1]), N(E2kText.TryNumber(E2kText.Value(l, "SF"), out var f) ? f : 1)]);
                        first = false;
                    }
                    if (first) rows.Add([c.Name, type, "", ""]);
                }
                break;
            case Points:
                foreach (var p in m.Points) rows.Add([p.Name, N(p.X), N(p.Y), N(p.Below)]);
                break;
            case Frames:
                foreach (var l in m.Lines) rows.Add([l.Label, l.Kind.ToString(), l.PointI, l.PointJ, l.StoriesBelowI.ToString(CultureInfo.InvariantCulture)]);
                break;
            case Areas:
                foreach (var a in m.Areas)
                    rows.Add([a.Label, a.Kind.ToString(), a.PointNames.Count.ToString(CultureInfo.InvariantCulture), string.Join(" ", a.PointNames),
                        a.StoriesBelow.All(b => b == 0) ? "" : string.Join(" ", a.StoriesBelow)]);
                break;
            case FrameSections:
                foreach (var a in m.LineAssigns) rows.Add([a.Story, a.Label, a.Section ?? ""]);
                break;
            case AreaSections:
                foreach (var a in m.AreaAssigns) rows.Add([a.Story, a.Label, a.Section ?? "", a.Diaphragm ?? ""]);
                break;
            case Restraints:
                foreach (var a in m.PointAssigns.Where(a => a.Restraint is { Length: > 0 }))
                {
                    var set = a.Restraint!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToUpperInvariant()).ToHashSet();
                    rows.Add([a.Story, a.Point, .. Dofs.Select(d => set.Contains(d) ? "Yes" : "No")]);
                }
                break;
            case FrameLoads:
                foreach (var l in m.LineLoads.Where(IsUniform)) { Uniform(l.Tokens, out var dir, out var v); rows.Add([l.Story, l.Object, E2kText.Unquote(l.Pattern), dir, N(v)]); }
                break;
            case AreaLoads:
                foreach (var l in m.AreaLoads.Where(IsUniform)) { Uniform(l.Tokens, out var dir, out var v); rows.Add([l.Story, l.Object, E2kText.Unquote(l.Pattern), dir, N(v)]); }
                break;
            case JointLoads:
                foreach (var l in m.PointLoads.Where(IsForce))
                    rows.Add([l.Story, l.Object, E2kText.Unquote(l.Pattern), .. new[] { "FX", "FY", "FZ", "MX", "MY", "MZ" }.Select(k => N(E2kText.TryNumber(E2kText.Value(l.Tokens, k) is { } t ? E2kText.Unquote(t) : null, out var v) ? v : 0))]);
                break;
        }
        return rows;
    }

    // ---- import ----

    /// <summary>What an import did: one line per table read, and notes on what was left out or removed with it.</summary>
    public sealed record ImportResult(List<string> Read, List<string> Notes);

    /// <summary>A table being read: each row's cells by field name, numbers converted to the model's units.</summary>
    private sealed class Reader
    {
        private readonly ModelTable _table;
        private readonly Dictionary<string, (int Column, double Scale)> _fields = new(StringComparer.OrdinalIgnoreCase);
        public string Name => _table.Name;

        public Reader(StructuralModel m, ModelTable table, Spec spec)
        {
            _table = table;
            foreach (var (field, kind) in spec.Fields)
            {
                var column = table.Field(field);
                if (column < 0) continue;
                var scale = 1.0;
                var unit = column < table.Units.Count ? table.Units[column].Trim().Trim('[', ']', '(', ')') : "";
                if (kind.Length > 0 && unit.Length > 0)
                {
                    var file = UnitFactor(unit) ?? throw new ArgumentException($"{table.Name}: the unit '{unit}' of {field} is not one the Modeler knows (use units like kN, m, mm, kN/m², N/mm²).");
                    var model = UnitFactor(UnitText(m, kind))!.Value;
                    scale = file / model;
                }
                _fields[field] = (column, scale);
            }
        }

        public bool Has(string field) => _fields.ContainsKey(field);

        public IEnumerable<(int Row, Func<string, string> Text, Func<string, double?> Number)> Rows()
        {
            for (var r = 0; r < _table.Rows.Count; r++)
            {
                var row = _table.Rows[r];
                if (row.All(c => string.IsNullOrWhiteSpace(c))) continue;
                var number = _table.FirstRow + r;
                string Text(string field) => _fields.TryGetValue(field, out var f) && f.Column < row.Count ? (row[f.Column] ?? "").Trim() : "";
                double? Number(string field)
                {
                    var t = Text(field);
                    if (t.Length == 0) return null;
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        throw new ArgumentException($"{_table.Name}, row {number}: {field} '{t}' is not a number.");
                    return v * _fields[field].Scale;
                }
                yield return (number, Text, Number);
            }
        }

        public string Need(int row, Func<string, string> text, string field) =>
            text(field) is { Length: > 0 } t ? t : throw new ArgumentException($"{_table.Name}, row {row}: {field} is empty.");

        public double NeedNumber(int row, Func<string, double?> number, string field) =>
            number(field) ?? throw new ArgumentException($"{_table.Name}, row {row}: {field} is empty.");
    }

    /// <summary>
    /// Reads the tables into the model: each table replaces what the model has of it, in <see cref="Names"/> order.
    /// Throws ArgumentException (naming the table, row and field) when a table can't be read; the model may then be
    /// half changed, so import into a copy and keep it only when this returns.
    /// </summary>
    public static ImportResult Import(StructuralModel m, IReadOnlyList<ModelTable> tables)
    {
        var read = new List<string>();
        var notes = new List<string>();
        var byName = new Dictionary<string, ModelTable>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string Kind, string Name, string Table)>();
        foreach (var t in tables)
        {
            var known = Names.FirstOrDefault(n => n.Equals(t.Name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ExtraTables.Known(t.Name);
            if (known is null) { notes.Add($"The table '{t.Name}' is not one the Modeler reads, so it was left out."); continue; }
            if (!byName.TryAdd(known, t)) notes.Add($"'{known}' is in the file twice; the first was read.");
        }
        foreach (var spec in Specs)
        {
            if (!byName.TryGetValue(spec.Name, out var table)) continue;
            var r = new Reader(m, table, spec);
            var missing = spec.Fields.Select(f => f.Field).Where(f => !r.Has(f) && !Optional(spec.Name, f)).ToList();
            if (missing.Count > 0) throw new ArgumentException($"{spec.Name}: the field{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing)} {(missing.Count > 1 ? "are" : "is")} missing (row 2 holds the field names).");
            var count = Apply(m, r, notes, pending);
            read.Add($"{spec.Name}: {count} row{(count == 1 ? "" : "s")}");
        }
        if (read.Count > 0) { Tidy(m, notes); Drop(m, pending, notes); }
        // After the objects and materials, which the rows name.
        foreach (var (name, table) in byName.Where(x => !Names.Contains(x.Key)))
        {
            var count = ExtraTables.Import(m, table, notes);
            read.Add($"{name}: {count} row{(count == 1 ? "" : "s")}");
        }
        return new ImportResult(read, notes.Distinct().ToList());
    }

    private static bool Optional(string table, string field) => (table, field) switch
    {
        (Stories, "Elevation") or (Grids, "Name") or (Grids, "BubbleLoc") or (Grids, "Visible") or (Concrete, "U") or (Concrete, "UnitWeight") => true,
        (Rectangular, "DesignType") or (Patterns, "SelfWtMult") or (Points, "Below") or (Frames, "IStoriesBelow") or (Areas, "NumPoints") or (Areas, "StoriesBelow") => true,
        (AreaSections, "Diaphragm") or (FrameLoads, "Dir") or (AreaLoads, "Dir") => true,
        (JointLoads, "FX" or "FY" or "FZ" or "MX" or "MY" or "MZ") => true,
        _ => false,
    };

    private static void Set(List<List<string>> lines, string key, string value)
    {
        foreach (var line in lines)
            for (var i = 0; i + 1 < line.Count; i++)
                if (line[i] == key) { line[i + 1] = value; return; }
        if (lines.Count == 0) lines.Add([]);
        lines[^1].AddRange([key, value]);
    }

    private static T? Find<T>(List<T> list, Func<T, string> name, string wanted) where T : class => list.Find(x => name(x) == wanted);

    private static void Unique(string table, IEnumerable<(int Row, string Key)> keys, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, key) in keys)
            if (!seen.Add(key)) throw new ArgumentException($"{table}, row {row}: {what} '{key}' is in the table twice.");
    }

    private static string StoryOf(StructuralModel m, string table, int row, string story)
    {
        var i = m.StoryIndex(story);
        if (i < 0) throw new ArgumentException($"{table}, row {row}: there is no story '{story}'.");
        return m.Stories[i].Name;
    }

    private static int Apply(StructuralModel m, Reader r, List<string> notes, List<(string Kind, string Name, string Table)> pending)
    {
        var rows = r.Rows().ToList();
        switch (r.Name)
        {
            case Base:
            {
                if (rows.Count == 0) return 0;
                var (row, text, number) = rows[0];
                var name = r.Need(row, text, "BSName");
                if (m.Stories.Skip(1).Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{r.Name}: '{name}' is also a story's name.");
                Rename(m, m.Stories[0].Name, name);
                m.Stories[0].Name = name;
                m.Stories[0].Elevation = r.NeedNumber(row, number, "BSElev");
                return 1;
            }
            case Stories:
            {
                var list = rows.Select(x => (x.Row, Name: r.Need(x.Row, x.Text, "Story"), Height: r.NeedNumber(x.Row, x.Number, "Height"))).ToList();
                if (list.Count == 0) throw new ArgumentException($"{r.Name}: a model needs one story above the base at least.");
                Unique(r.Name, list.Select(x => (x.Row, x.Name.ToUpperInvariant())), "the story");
                if (list.FirstOrDefault(x => x.Name.Equals(m.Stories[0].Name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } clash && clash.Row > 0)
                    throw new ArgumentException($"{r.Name}, row {clash.Row}: '{clash.Name}' is the base level's name.");
                if (list.FirstOrDefault(x => x.Height <= 0) is { Row: > 0 } flat) throw new ArgumentException($"{r.Name}, row {flat.Row}: the height is above 0.");
                // Top first, as the programs list them; the model keeps them bottom first.
                var kept = new List<Story> { m.Stories[0] };
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var old = m.Stories.Skip(1).FirstOrDefault(s => s.Name.Equals(list[i].Name, StringComparison.OrdinalIgnoreCase));
                    var story = old ?? new Story { Name = list[i].Name };
                    story.Name = list[i].Name; story.Height = list[i].Height;
                    kept.Add(story);
                }
                foreach (var gone in m.Stories.Skip(1).Where(s => !kept.Contains(s)))
                    notes.Add($"Story {gone.Name} is not in {r.Name}, so it was removed with what was on it.");
                m.Stories = kept;
                return list.Count;
            }
            case Grids:
            {
                var list = rows.Select(x =>
                {
                    var type = r.Need(x.Row, x.Text, "LineType").ToUpperInvariant();
                    var dir = type.StartsWith('X') ? "X" : type.StartsWith('Y') ? "Y" : throw new ArgumentException($"{r.Name}, row {x.Row}: LineType is X (Cartesian) or Y (Cartesian).");
                    return (x.Row, Dir: dir, Id: r.Need(x.Row, x.Text, "ID"), At: r.NeedNumber(x.Row, x.Number, "Ordinate"), Bubble: x.Text("BubbleLoc"), Visible: x.Text("Visible"));
                }).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Id)), "the grid line");
                var grids = new List<GridLine>();
                foreach (var g in list)
                {
                    var line = m.Grids.Find(x => x.Label == g.Id) ?? new GridLine
                    {
                        Label = g.Id, Extra = ["VISIBLE", "\"Yes\"", "BUBBLELOC", g.Dir == "X" ? "\"End\"" : "\"Start\""],
                    };
                    line.Direction = g.Dir; line.Coordinate = g.At;
                    void Put(string key, string value)
                    {
                        if (value.Length == 0) return;
                        var at = line.Extra.IndexOf(key);
                        if (at >= 0 && at + 1 < line.Extra.Count) line.Extra[at + 1] = E2kText.Quote(value); else line.Extra.AddRange([key, E2kText.Quote(value)]);
                    }
                    Put("BUBBLELOC", g.Bubble); Put("VISIBLE", g.Visible);
                    grids.Add(line);
                }
                m.Grids = grids;
                return list.Count;
            }
            case Concrete:
            {
                var list = rows.Select(x => (x.Row, Name: r.Need(x.Row, x.Text, "Material"), Fc: r.NeedNumber(x.Row, x.Number, "Fc"), E: r.NeedNumber(x.Row, x.Number, "E"), U: x.Number("U"), W: x.Number("UnitWeight"))).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Name)), "the material");
                foreach (var x in list)
                {
                    if (x.Fc <= 0 || x.E <= 0) throw new ArgumentException($"{r.Name}, row {x.Row}: Fc and E are above 0.");
                    var material = Find(m.Materials, y => y.Name, x.Name);
                    if (material is not null && !material.IsConcrete) throw new ArgumentException($"{r.Name}, row {x.Row}: '{x.Name}' is a {material.Type} material, not concrete.");
                    if (material is null) { m.AddConcrete(x.Name, x.Fc, x.E, x.W ?? m.ConcreteUnitWeight()); material = m.Materials[^1]; }
                    Set(material.Lines, "FC", E2kText.Number(x.Fc)); Set(material.Lines, "E", E2kText.Number(x.E));
                    if (x.W is { } w) Set(material.Lines, "WEIGHTPERVOLUME", E2kText.Number(w));
                    if (x.U is { } u) Set(material.Lines, "U", E2kText.Number(u));
                }
                var names = list.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var gone in m.Materials.Where(x => x.IsConcrete && !names.Contains(x.Name))) pending.Add(("material", gone.Name, r.Name));
                return list.Count;
            }
            case Rectangular:
            {
                var list = rows.Select(x => (x.Row, Name: r.Need(x.Row, x.Text, "Name"), Material: r.Need(x.Row, x.Text, "Material"), D: r.NeedNumber(x.Row, x.Number, "Depth"), B: r.NeedNumber(x.Row, x.Number, "Width"), Type: x.Text("DesignType"))).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Name)), "the section");
                foreach (var x in list)
                {
                    if (x.D <= 0 || x.B <= 0) throw new ArgumentException($"{r.Name}, row {x.Row}: Depth and Width are above 0.");
                    if (!m.Materials.Exists(y => y.Name == x.Material)) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no material '{x.Material}'.");
                    var beam = x.Type.Length > 0 ? x.Type.Equals("Beam", StringComparison.OrdinalIgnoreCase)
                        : x.Type.Length == 0 && Find(m.FrameSections, s => s.Name, x.Name) is { } known && IsBeamSection(m, known.Name);
                    if (x.Type.Length > 0 && !beam && !x.Type.Equals("Column", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{r.Name}, row {x.Row}: DesignType is Column or Beam.");
                    var section = Find(m.FrameSections, s => s.Name, x.Name);
                    if (section is not null && !IsRectangular(section)) throw new ArgumentException($"{r.Name}, row {x.Row}: '{x.Name}' is a {section.Shape} section, not a rectangular concrete one.");
                    if (section is not null && IsBeamSection(m, x.Name) != beam)
                    {
                        // The kind lives in the reinforcement data: make it again in place.
                        var at = m.FrameSections.IndexOf(section);
                        m.FrameSections.Remove(section);
                        m.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS")?.Lines.RemoveAll(l => ConcreteLineOf(l, x.Name));
                        m.AddRectangularSection(x.Name, x.Material, x.D, x.B, beam);
                        var made = m.FrameSections[^1];
                        m.FrameSections.RemoveAt(m.FrameSections.Count - 1);
                        m.FrameSections.Insert(at, made);
                        continue;
                    }
                    if (section is null) { m.AddRectangularSection(x.Name, x.Material, x.D, x.B, beam); continue; }
                    Set(section.Lines, "MATERIAL", E2kText.Quote(x.Material)); Set(section.Lines, "D", E2kText.Number(x.D)); Set(section.Lines, "B", E2kText.Number(x.B));
                }
                var names = list.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var gone in m.FrameSections.Where(s => IsRectangular(s) && !names.Contains(s.Name))) pending.Add(("frame section", gone.Name, r.Name));
                return list.Count;
            }
            case Slabs or Walls:
            {
                var wall = r.Name == Walls;
                var list = rows.Select(x => (x.Row, Name: r.Need(x.Row, x.Text, "Name"), Material: r.Need(x.Row, x.Text, "Material"), T: r.NeedNumber(x.Row, x.Number, "Thickness"))).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Name)), "the section");
                foreach (var x in list)
                {
                    if (x.T <= 0) throw new ArgumentException($"{r.Name}, row {x.Row}: Thickness is above 0.");
                    if (!m.Materials.Exists(y => y.Name == x.Material)) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no material '{x.Material}'.");
                    var section = Find(m.ShellSections, s => s.Name, x.Name);
                    if (section is not null && !PropType(section).Equals(wall ? "Wall" : "Slab", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"{r.Name}, row {x.Row}: '{x.Name}' is a {PropType(section)} section; it is listed in {(wall ? Slabs : Walls)} instead.");
                    if (section is null) { if (wall) m.AddWallSection(x.Name, x.Material, x.T); else m.AddSlabSection(x.Name, x.Material, x.T); continue; }
                    Set(section.Lines, "MATERIAL", E2kText.Quote(x.Material)); Set(section.Lines, wall ? "WALLTHICKNESS" : "SLABTHICKNESS", E2kText.Number(x.T));
                }
                var names = list.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var gone in m.ShellSections.Where(s => PropType(s).Equals(wall ? "Wall" : "Slab", StringComparison.OrdinalIgnoreCase) && !names.Contains(s.Name))) pending.Add(("section", gone.Name, r.Name));
                return list.Count;
            }
            case Patterns:
            {
                var list = rows.Select(x => (x.Row, Name: r.Need(x.Row, x.Text, "Name"), Type: r.Need(x.Row, x.Text, "Type"), Self: x.Number("SelfWtMult") ?? 0)).ToList();
                if (list.Count == 0) throw new ArgumentException($"{r.Name}: a model keeps one load pattern at least.");
                Unique(r.Name, list.Select(x => (x.Row, x.Name)), "the load pattern");
                foreach (var x in list)
                {
                    var type = ModelEdit.LoadPatternTypes.FirstOrDefault(t => t.Equals(x.Type, StringComparison.OrdinalIgnoreCase))
                               ?? throw new ArgumentException($"{r.Name}, row {x.Row}: '{x.Type}' is not a load pattern type ({string.Join(", ", ModelEdit.LoadPatternTypes)}).");
                    var pattern = m.LoadPatterns.Find(p => p.Name == x.Name);
                    if (pattern is null) m.AddLoadPattern(x.Name, type, x.Self);
                    else { pattern.Type = type; pattern.SelfWeight = x.Self; }
                }
                var names = list.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                var order = list.Select(x => x.Name).ToList();
                foreach (var gone in m.LoadPatterns.Where(p => !names.Contains(p.Name)).ToList())
                {
                    m.LoadPatterns.Remove(gone);
                    var loads = m.LineLoads.RemoveAll(l => E2kText.Unquote(l.Pattern) == gone.Name) + m.AreaLoads.RemoveAll(l => E2kText.Unquote(l.Pattern) == gone.Name) + m.PointLoads.RemoveAll(l => E2kText.Unquote(l.Pattern) == gone.Name);
                    var single = m.LoadCases.Where(c => c.Name == gone.Name).ToList();
                    foreach (var c in single) m.LoadCases.Remove(c);
                    foreach (var c in m.LoadCases) c.Lines.RemoveAll(l => l.Count >= 2 && l[0] == "LOADPAT" && E2kText.Unquote(l[1]) == gone.Name);
                    foreach (var c in m.Combos) c.Lines.RemoveAll(l => l.Count >= 2 && l[0] == "LOADCASE" && E2kText.Unquote(l[1]) == gone.Name);
                    notes.Add($"The load pattern {gone.Name} is not in {r.Name}, so it was removed with its load case{(loads > 0 ? $" and {loads} loads" : "")}.");
                }
                m.LoadPatterns = m.LoadPatterns.OrderBy(p => order.IndexOf(p.Name)).ToList();
                return list.Count;
            }
            case Combos:
            {
                var groups = new List<(int Row, string Name, string Type, List<(int Row, string Case, double Factor)> Cases)>();
                foreach (var (row, text, number) in rows)
                {
                    var name = text("Name");
                    if (name.Length == 0)
                    {
                        if (groups.Count == 0) throw new ArgumentException($"{r.Name}, row {row}: Name is empty.");
                        name = groups[^1].Name;
                    }
                    var index = groups.FindIndex(g => g.Name == name);
                    if (index >= 0 && index != groups.Count - 1) throw new ArgumentException($"{r.Name}, row {row}: the rows of '{name}' are not together.");
                    if (index < 0) { groups.Add((row, name, text("Type"), [])); index = groups.Count - 1; }
                    else if (text("Type") is { Length: > 0 } t && groups[index].Type.Length == 0) groups[index] = groups[index] with { Type = t };
                    if (text("LoadName") is { Length: > 0 } c) groups[index].Cases.Add((row, c, number("SF") ?? 1));
                }
                string[] types = ["Linear Add", "Envelope", "Absolute Add", "SRSS", "Range Add"];
                var comboNames = groups.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
                var combos = new List<LoadCombo>();
                foreach (var g in groups)
                {
                    var type = g.Type.Length == 0 ? "Linear Add" : types.FirstOrDefault(t => t.Equals(g.Type, StringComparison.OrdinalIgnoreCase))
                               ?? throw new ArgumentException($"{r.Name}, row {g.Row}: '{g.Type}' is not a combination type ({string.Join(", ", types)}).");
                    if (g.Cases.Count == 0) throw new ArgumentException($"{r.Name}, row {g.Row}: '{g.Name}' has no LoadName.");
                    var combo = m.Combos.Find(c => c.Name == g.Name) ?? new LoadCombo { Name = g.Name };
                    var rest = combo.Lines.Where(l => l.Count > 0 && l[0] is not ("LOADCASE" or "LOADCOMBO") && E2kText.Value(l, "TYPE") is null).ToList();
                    combo.Lines = [["TYPE", E2kText.Quote(type)]];
                    foreach (var (row, c, f) in g.Cases)
                    {
                        if (c == g.Name) throw new ArgumentException($"{r.Name}, row {row}: '{g.Name}' can't include itself.");
                        var isCombo = comboNames.Contains(c);
                        if (!isCombo && !m.LoadCases.Exists(x => x.Name == c)) throw new ArgumentException($"{r.Name}, row {row}: there is no load case or combination '{c}'.");
                        combo.Lines.Add([isCombo ? "LOADCOMBO" : "LOADCASE", E2kText.Quote(c), "SF", E2kText.Number(f)]);
                    }
                    combo.Lines.AddRange(rest);
                    combos.Add(combo);
                }
                foreach (var gone in m.Combos.Where(c => !comboNames.Contains(c.Name))) notes.Add($"The combination {gone.Name} is not in {r.Name}, so it was removed.");
                m.Combos = combos;
                return groups.Count;
            }
            case Points:
            {
                var list = rows.Select(x => (x.Row, Name: r.Need(x.Row, x.Text, "Label"), X: r.NeedNumber(x.Row, x.Number, "X"), Y: r.NeedNumber(x.Row, x.Number, "Y"), Below: x.Number("Below") ?? 0)).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Name)), "the point");
                var points = new List<PlanPoint>();
                foreach (var x in list)
                {
                    var p = m.Point(x.Name) ?? new PlanPoint { Name = x.Name };
                    p.X = x.X; p.Y = x.Y; p.Below = x.Below;
                    points.Add(p);
                }
                m.Points = points;
                return list.Count;
            }
            case Frames:
            {
                var list = rows.Select(x =>
                {
                    var kind = Enum.TryParse<LineKind>(r.Need(x.Row, x.Text, "Type"), true, out var k) ? k : throw new ArgumentException($"{r.Name}, row {x.Row}: Type is Column, Beam or Brace.");
                    var below = x.Text("IStoriesBelow") is { Length: > 0 } b ? int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new ArgumentException($"{r.Name}, row {x.Row}: IStoriesBelow is a whole number.") : kind == LineKind.Beam ? 0 : 1;
                    return (x.Row, Label: r.Need(x.Row, x.Text, "Label"), Kind: kind, I: r.Need(x.Row, x.Text, "PointI"), J: r.Need(x.Row, x.Text, "PointJ"), Below: below);
                }).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Label)), "the frame");
                foreach (var x in list)
                {
                    foreach (var p in new[] { x.I, x.J }) if (m.Point(p) is null) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no point '{p}'.");
                    if (x.Kind != LineKind.Column && x.I == x.J) throw new ArgumentException($"{r.Name}, row {x.Row}: a {x.Kind.ToString().ToLowerInvariant()} needs two different points.");
                }
                m.Lines = list.Select(x => new LineObject { Label = x.Label, Kind = x.Kind, PointI = x.I, PointJ = x.J, StoriesBelowI = x.Below }).ToList();
                return list.Count;
            }
            case Areas:
            {
                var list = rows.Select(x =>
                {
                    var kind = Enum.TryParse<AreaKind>(r.Need(x.Row, x.Text, "Type"), true, out var k) ? k : throw new ArgumentException($"{r.Name}, row {x.Row}: Type is Floor, Wall or Ramp.");
                    var names = r.Need(x.Row, x.Text, "Points").Split(new[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (names.Count < 3) throw new ArgumentException($"{r.Name}, row {x.Row}: an area needs 3 points at least.");
                    if (x.Text("NumPoints") is { Length: > 0 } c && c != names.Count.ToString(CultureInfo.InvariantCulture)) throw new ArgumentException($"{r.Name}, row {x.Row}: NumPoints is {c} but Points lists {names.Count}.");
                    var below = x.Text("StoriesBelow") is { Length: > 0 } b
                        ? b.Split(new[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new ArgumentException($"{r.Name}, row {x.Row}: StoriesBelow lists whole numbers.")).ToList()
                        : names.Select(_ => 0).ToList();
                    if (below.Count != names.Count) throw new ArgumentException($"{r.Name}, row {x.Row}: StoriesBelow needs one number per point ({names.Count}).");
                    return (x.Row, Label: r.Need(x.Row, x.Text, "Label"), Kind: kind, Names: names, Below: below);
                }).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Label)), "the area");
                foreach (var x in list)
                    foreach (var p in x.Names) if (m.Point(p) is null) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no point '{p}'.");
                m.Areas = list.Select(x => new AreaObject { Label = x.Label, Kind = x.Kind, PointNames = x.Names, StoriesBelow = x.Below }).ToList();
                return list.Count;
            }
            case FrameSections:
            {
                var list = rows.Select(x => (x.Row, Story: StoryOf(m, r.Name, x.Row, r.Need(x.Row, x.Text, "Story")), Label: r.Need(x.Row, x.Text, "Label"), Section: x.Text("Section"))).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Label + "@" + x.Story)), "the frame");
                var assigns = new List<LineAssign>();
                foreach (var x in list)
                {
                    var line = m.Line(x.Label) ?? throw new ArgumentException($"{r.Name}, row {x.Row}: there is no frame '{x.Label}' in {Frames}.");
                    if (x.Section.Length > 0 && !m.FrameSections.Exists(s => s.Name == x.Section)) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no frame section '{x.Section}'.");
                    if (m.StoryIndex(x.Story) < line.StoriesBelowI) throw new ArgumentException($"{r.Name}, row {x.Row}: {x.Label} hangs {line.StoriesBelowI} story below {x.Story}, under the base.");
                    var a = m.LineAssigns.Find(y => y.Label == x.Label && y.Story == x.Story) ?? new LineAssign { Label = x.Label, Story = x.Story, Extra = [ModelEdit.LineAssignDefaults(line.Kind)] };
                    a.Section = x.Section.Length > 0 ? x.Section : null;
                    assigns.Add(a);
                }
                var gone = m.LineAssigns.Count - m.LineAssigns.Count(assigns.Contains);
                if (gone > 0) notes.Add($"{gone} frame{(gone == 1 ? " is" : "s are")} not in {r.Name}, so {(gone == 1 ? "it was" : "they were")} removed with {(gone == 1 ? "its" : "their")} loads.");
                m.LineAssigns = assigns;
                return list.Count;
            }
            case AreaSections:
            {
                var list = rows.Select(x => (x.Row, Story: StoryOf(m, r.Name, x.Row, r.Need(x.Row, x.Text, "Story")), Label: r.Need(x.Row, x.Text, "Label"), Section: x.Text("Section"), Diaphragm: x.Text("Diaphragm"))).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Label + "@" + x.Story)), "the area");
                var assigns = new List<AreaAssign>();
                foreach (var x in list)
                {
                    var area = m.Area(x.Label) ?? throw new ArgumentException($"{r.Name}, row {x.Row}: there is no area '{x.Label}' in {Areas}.");
                    if (x.Section.Length > 0 && !m.ShellSections.Exists(s => s.Name == x.Section)) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no slab or wall section '{x.Section}'.");
                    if (m.StoryIndex(x.Story) < area.StoriesBelow.DefaultIfEmpty(0).Max()) throw new ArgumentException($"{r.Name}, row {x.Row}: {x.Label} reaches below the base from {x.Story}.");
                    var a = m.AreaAssigns.Find(y => y.Label == x.Label && y.Story == x.Story) ?? new AreaAssign { Label = x.Label, Story = x.Story, Extra = [ModelEdit.AreaAssignDefaults(area.Kind)] };
                    a.Section = x.Section.Length > 0 ? x.Section : null;
                    if (r.Has("Diaphragm"))
                    {
                        a.Diaphragm = x.Diaphragm.Length > 0 ? x.Diaphragm : null;
                        if (a.Diaphragm is { } d && m.Diaphragms.TrueForAll(y => y.Name != d)) m.Diaphragms.Add(new Diaphragm { Name = d });
                    }
                    assigns.Add(a);
                }
                var gone = m.AreaAssigns.Count - m.AreaAssigns.Count(assigns.Contains);
                if (gone > 0) notes.Add($"{gone} area{(gone == 1 ? " is" : "s are")} not in {r.Name}, so {(gone == 1 ? "it was" : "they were")} removed with {(gone == 1 ? "its" : "their")} loads.");
                m.AreaAssigns = assigns;
                return list.Count;
            }
            case Restraints:
            {
                var list = rows.Select(x => (x.Row, Story: StoryOf(m, r.Name, x.Row, r.Need(x.Row, x.Text, "Story")), Label: r.Need(x.Row, x.Text, "Label"),
                    Dofs: Dofs.Where(d => Yes(r.Name, x.Row, d, x.Text(d))).ToList())).ToList();
                Unique(r.Name, list.Select(x => (x.Row, x.Label + "@" + x.Story)), "the joint");
                foreach (var a in m.PointAssigns) a.Restraint = null;
                foreach (var x in list)
                {
                    if (m.Point(x.Label) is null) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no point '{x.Label}'.");
                    var a = m.PointAssigns.Find(y => y.Point == x.Label && y.Story == x.Story);
                    if (a is null) m.PointAssigns.Add(a = new PointAssign { Point = x.Label, Story = x.Story });
                    a.Restraint = x.Dofs.Count > 0 ? string.Join(" ", x.Dofs) : null;
                }
                return list.Count;
            }
            case FrameLoads or AreaLoads:
            {
                var frame = r.Name == FrameLoads;
                var value = frame ? "FOverL" : "UnifLoad";
                var list = rows.Select(x => (x.Row, Story: StoryOf(m, r.Name, x.Row, r.Need(x.Row, x.Text, "Story")), Label: r.Need(x.Row, x.Text, "Label"),
                    Pattern: r.Need(x.Row, x.Text, "LoadPattern"), Dir: x.Text("Dir") is { Length: > 0 } d ? d : "GRAV", Value: r.NeedNumber(x.Row, x.Number, value))).ToList();
                string[] dirs = ["GRAV", "X", "Y", "Z", "1", "2", "3", "XPROJ", "YPROJ", "GRAVPROJ"];
                foreach (var x in list)
                {
                    if (!m.LoadPatterns.Exists(p => p.Name == x.Pattern)) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no load pattern '{x.Pattern}'.");
                    var exists = frame ? m.LineAssigns.Exists(a => a.Label == x.Label && a.Story == x.Story) : m.AreaAssigns.Exists(a => a.Label == x.Label && a.Story == x.Story);
                    if (!dirs.Contains(x.Dir.ToUpperInvariant())) throw new ArgumentException($"{r.Name}, row {x.Row}: Dir is one of {string.Join(", ", dirs)}.");
                    // A load on an object removed in the same workbook goes with it, as in SAP2000.
                    if (!exists) notes.Add($"{r.Name}, row {x.Row}: there is no {(frame ? "frame" : "area")} {x.Label} on {x.Story}, so this load was left out.");
                }
                list.RemoveAll(x => frame ? !m.LineAssigns.Exists(a => a.Label == x.Label && a.Story == x.Story) : !m.AreaAssigns.Exists(a => a.Label == x.Label && a.Story == x.Story));
                List<string> Tokens((int Row, string Story, string Label, string Pattern, string Dir, double Value) x) =>
                    ["TYPE", "\"UNIFF\"", "DIR", E2kText.Quote(x.Dir.ToUpperInvariant()), "LC", E2kText.Quote(x.Pattern), "FVAL", E2kText.Number(x.Value)];
                if (frame)
                {
                    m.LineLoads.RemoveAll(IsUniform);
                    m.LineLoads.AddRange(list.Select(x => new LineLoad { Object = x.Label, Story = x.Story, Tokens = Tokens(x) }));
                }
                else
                {
                    m.AreaLoads.RemoveAll(IsUniform);
                    m.AreaLoads.AddRange(list.Select(x => new AreaLoad { Object = x.Label, Story = x.Story, Tokens = Tokens(x) }));
                }
                return list.Count;
            }
            case JointLoads:
            {
                string[] keys = ["FX", "FY", "FZ", "MX", "MY", "MZ"];
                var list = rows.Select(x => (x.Row, Story: StoryOf(m, r.Name, x.Row, r.Need(x.Row, x.Text, "Story")), Label: r.Need(x.Row, x.Text, "Label"),
                    Pattern: r.Need(x.Row, x.Text, "LoadPattern"), Values: keys.Select(k => x.Number(k) ?? 0).ToArray())).ToList();
                foreach (var x in list)
                {
                    if (!m.LoadPatterns.Exists(p => p.Name == x.Pattern)) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no load pattern '{x.Pattern}'.");
                    if (m.Point(x.Label) is null) throw new ArgumentException($"{r.Name}, row {x.Row}: there is no point '{x.Label}'.");
                }
                m.PointLoads.RemoveAll(IsForce);
                foreach (var x in list)
                {
                    var tokens = new List<string> { "TYPE", "\"FORCE\"", "LC", E2kText.Quote(x.Pattern) };
                    for (var i = 0; i < keys.Length; i++) if (x.Values[i] != 0) tokens.AddRange([keys[i], E2kText.Number(x.Values[i])]);
                    m.PointLoads.Add(new PointLoad { Object = x.Label, Story = x.Story, Tokens = tokens });
                }
                return list.Count;
            }
        }
        return 0;
    }

    /// <summary>Definitions a table left out go once every table is read, so an assignment table in the same file can
    /// move members off them first; one still in use stays, with a note.</summary>
    private static void Drop(StructuralModel m, List<(string Kind, string Name, string Table)> pending, List<string> notes)
    {
        // Sections first: a material is free only once the sections that used it are gone.
        foreach (var (kind, name, table) in pending.OrderBy(p => p.Kind == "material" ? 1 : 0))
        {
            int users;
            switch (kind)
            {
                case "frame section":
                    users = m.LineAssigns.Count(a => a.Section == name);
                    if (users == 0)
                    {
                        m.FrameSections.RemoveAll(s => s.Name == name);
                        m.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS")?.Lines.RemoveAll(l => ConcreteLineOf(l, name));
                    }
                    break;
                case "section":
                    users = m.AreaAssigns.Count(a => a.Section == name);
                    if (users == 0) m.ShellSections.RemoveAll(s => s.Name == name);
                    break;
                default:
                    users = m.FrameSections.Count(s => E2kText.Unquote(s.Material) == name) + m.ShellSections.Count(s => E2kText.Unquote(s.Material) == name);
                    if (users == 0) m.Materials.RemoveAll(x => x.Name == name);
                    break;
            }
            if (users > 0) notes.Add($"The {kind} {name} is not in {table} but {users} {(kind == "material" ? "section" : "object")}{(users == 1 ? " uses" : "s use")} it, so it was kept.");
        }
    }

    private static bool Yes(string table, int row, string field, string text) => text.ToUpperInvariant() switch
    {
        "YES" or "Y" or "TRUE" or "1" => true,
        "NO" or "N" or "FALSE" or "0" or "" => false,
        _ => throw new ArgumentException($"{table}, row {row}: {field} is Yes or No."),
    };

    private static void Rename(StructuralModel m, string was, string name)
    {
        if (was == name) return;
        foreach (var a in m.PointAssigns.Where(a => a.Story == was)) a.Story = name;
        foreach (var a in m.LineAssigns.Where(a => a.Story == was)) a.Story = name;
        foreach (var a in m.AreaAssigns.Where(a => a.Story == was)) a.Story = name;
        foreach (var l in m.PointLoads.Where(l => l.Story == was)) l.Story = name;
        foreach (var l in m.LineLoads.Where(l => l.Story == was)) l.Story = name;
        foreach (var l in m.AreaLoads.Where(l => l.Story == was)) l.Story = name;
    }

    /// <summary>After an import: what points at something no longer there goes, with a note.</summary>
    private static void Tidy(StructuralModel m, List<string> notes)
    {
        var stories = m.Stories.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var lines = m.Lines.ToDictionary(l => l.Label, StringComparer.Ordinal);
        var areas = m.Areas.ToDictionary(a => a.Label, StringComparer.Ordinal);
        var points = m.Points.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        bool Fits(string story, int below) => m.StoryIndex(story) is var i && i >= 0 && i >= below;

        var n = m.LineAssigns.RemoveAll(a => !lines.TryGetValue(a.Label, out var l) || !Fits(a.Story, l.StoriesBelowI));
        if (n > 0) notes.Add($"{n} frame assignment{(n == 1 ? "" : "s")} pointed at a frame or story no longer there and {(n == 1 ? "was" : "were")} removed.");
        n = m.AreaAssigns.RemoveAll(a => !areas.TryGetValue(a.Label, out var x) || !Fits(a.Story, x.StoriesBelow.DefaultIfEmpty(0).Max()));
        if (n > 0) notes.Add($"{n} area assignment{(n == 1 ? "" : "s")} pointed at an area or story no longer there and {(n == 1 ? "was" : "were")} removed.");
        n = m.PointAssigns.RemoveAll(a => !points.Contains(a.Point) || !stories.Contains(a.Story));
        if (n > 0) notes.Add($"{n} joint assignment{(n == 1 ? "" : "s")} pointed at a point or story no longer there and {(n == 1 ? "was" : "were")} removed.");

        var lineAt = m.LineAssigns.Select(a => (a.Label, a.Story)).ToHashSet();
        var areaAt = m.AreaAssigns.Select(a => (a.Label, a.Story)).ToHashSet();
        var patterns = m.LoadPatterns.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        n = m.LineLoads.RemoveAll(l => !lineAt.Contains((l.Object, l.Story)) || !patterns.Contains(E2kText.Unquote(l.Pattern)))
            + m.AreaLoads.RemoveAll(l => !areaAt.Contains((l.Object, l.Story)) || !patterns.Contains(E2kText.Unquote(l.Pattern)))
            + m.PointLoads.RemoveAll(l => !points.Contains(l.Object) || !stories.Contains(l.Story) || !patterns.Contains(E2kText.Unquote(l.Pattern)));
        if (n > 0) notes.Add($"{n} load{(n == 1 ? "" : "s")} pointed at an object, story or load pattern no longer there and {(n == 1 ? "was" : "were")} removed.");

        // Sections that the members used and that the import took away: keep the members, without a section.
        var frameSections = m.FrameSections.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var shellSections = m.ShellSections.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var a in m.LineAssigns.Where(a => a.Section is { } s && !frameSections.Contains(s))) { notes.Add($"Frame {a.Label} on {a.Story} uses '{a.Section}', which is not defined."); }
        foreach (var a in m.AreaAssigns.Where(a => a.Section is { } s && !shellSections.Contains(s))) { notes.Add($"Area {a.Label} on {a.Story} uses '{a.Section}', which is not defined."); }

        // Combinations that name a case or combination no longer there lose that line.
        var cases = m.LoadCases.Select(c => c.Name).Concat(m.Combos.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var c in m.Combos)
            if (c.Lines.RemoveAll(l => l.Count >= 2 && l[0] is "LOADCASE" or "LOADCOMBO" && !cases.Contains(E2kText.Unquote(l[1]))) > 0)
                notes.Add($"The combination {c.Name} named a load case no longer there; that line was removed.");
    }
}
