using CsiProMax.E2k;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Model;

/// <summary>
/// The frame section shapes of ETABS 23.2's Section Shape list (pictured on Ahmed's PC 2026-11-27) beyond the 13 the
/// .e2k writer has a line for (<see cref="PropertyDefs.Shapes"/>): each kept with every field of its own "Frame Section
/// Property Definitions - (table)" table in the section's <see cref="Definition.Tables"/>, so the Engine's forms hold every
/// option and nothing is lost. ETABS gets the section from that table (EditableTables, ModelerTables); until the field
/// test brings ETABS's own .e2k line for each of them, the .e2k carries a stand-in of the same name, material and size
/// that the table then puts right.
/// </summary>
public static class FrameTableShapes
{
    private static readonly string[] Mods = ["AMod", "A2Mod", "A3Mod", "JMod", "I2Mod", "I3Mod", "MMod", "WMod"];

    /// <summary>Shape → (the ETABS table's name after "Frame Section Property Definitions - ", or null where ETABS has no
    /// table; its fields besides Name, Material, GUID and the modifiers; the material kind it takes: steel, concrete, any or none).</summary>
    public static readonly IReadOnlyDictionary<string, (string? Table, string[] Fields, string Material)> All =
        new Dictionary<string, (string?, string[], string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Filled Steel Tube"] = ("Filled Steel Tube", ["FromFile", "FileName", "SectInFile", "t3", "t2", "tf", "tw", "CornerRad", "FillMat", "Color", "Notes"], "steel"),
            ["Filled Steel Pipe"] = ("Filled Steel Pipe", ["FromFile", "FileName", "SectInFile", "t3", "tw", "FillMat", "Color", "Notes"], "steel"),
            ["BU I Cover Plate"] = ("Steel BU I Cover Plate", ["ISection", "IsTopPlate", "TPMaterial", "TPWidth", "TPThickness", "IsBotPlate", "BPMaterial", "BPWidth", "BPThickness", "Color", "Notes"], "none"),
            ["Standard Joist Section"] = ("Steel Joist", ["DesignType", "Depth", "UnitWeight", "AnalysisI33", "MomentCap", "ShearCap", "MinSpanLen", "MaxSpanLen", "SpanLength", "TLCapacity", "LLDeflCap", "MomInertia", "Color", "Notes"], "steel"),
            ["Custom Steel Joist"] = (null, ["TopChord", "BottomChord", "WebMembers", "TrussType", "Depth", "PanelWidth", "Color", "Notes"], "none"),
            ["Steel Plate"] = ("Steel Plate", ["t3", "t2", "Color", "Notes"], "steel"),
            ["Steel Castellated"] = ("Steel Castellated", ["TopRootISection", "BottomRootISection", "HoleSpacing", "HoleWidth", "HoleHeight", "Color", "Notes"], "none"),
            ["Steel Cellular"] = ("Steel Cellular", ["TopRootISection", "BottomRootISection", "HoleSpacing", "HoleDiameter", "Color", "Notes"], "none"),
            ["Concrete Trapezoid"] = ("Concrete Trapezoid", ["FromFile", "FileName", "SectInFile", "t3", "t2", "t2b", "RigidZone", "ColumnDropPanel", "ColumnDropPanelAxis2", "ColumnDropPanelAxis3", "ColumnDropPanelSlabProperty", "NotSizeType", "NotAutoFact", "NotUserSize", "DesignType", "Color", "Notes"], "concrete"),
            ["Concrete Encasement Rectangle"] = ("Conc Encasement Rectangle", ["FromFile", "FileName", "SectInFile", "t3", "t2", "EmbedISect", "EncaseMat", "NotSizeType", "NotAutoFact", "NotUserSize", "DesignType", "Color", "Notes"], "any"),
            ["Concrete Encasement Circle"] = ("Concrete Encasement Circle", ["FromFile", "FileName", "SectInFile", "t3", "EmbedISect", "EncaseMat", "NotSizeType", "NotAutoFact", "NotUserSize", "DesignType", "Color", "Notes"], "any"),
            ["Precast I"] = ("Concrete Precast I", ["B1", "B2", "B3", "B4", "D1", "D2", "D3", "D4", "D5", "D6", "SketchType", "RigidZone", "NotSizeType", "NotAutoFact", "NotUserSize", "Color", "Notes"], "concrete"),
            ["Concrete Cross"] = ("Concrete Cross", ["t3", "t2", "tf", "tw", "RigidZone", "NotSizeType", "NotAutoFact", "NotUserSize", "DesignType", "Color", "Notes"], "concrete"),
            ["Concrete Box"] = ("Concrete Box", ["t3", "t2", "tf", "tw", "RigidZone", "NotSizeType", "NotAutoFact", "NotUserSize", "DesignType", "Color", "Notes"], "concrete"),
            ["Concrete Pipe"] = ("Concrete Pipe", ["t3", "tw", "RigidZone", "NotSizeType", "NotAutoFact", "NotUserSize", "DesignType", "Color", "Notes"], "concrete"),
            ["Cold Formed C"] = ("Cold Formed C", ["t3", "t2", "tw", "Radius", "LipDepth", "MirrorAbt2", "Color", "Notes"], "steel"),
            ["Cold Formed Z"] = ("Cold Formed Z", ["t3", "t2", "tw", "Radius", "LipDepth", "AngleOfLip", "MirrorAbt2", "Color", "Notes"], "steel"),
            ["Cold Formed Hat"] = ("Cold Formed Hat", ["t3", "t2", "tw", "Radius", "LipDepth", "MirrorAbt2", "Color", "Notes"], "steel"),
            ["Buckling Restrained Brace"] = ("Buckling Restrained Brace", ["Weight", "t3", "t2", "AreaYC", "StiffES", "LengthYC", "LengthES", "UserInElast", "FDType", "Symmetric", "InitStiff", "FinalStiff",
                "TensFy", "TensFu0", "CompFy", "CompFu0", "TensDu", "TensDx", "CompDu", "CompDx", "TensIO", "TensLS", "TensCP", "CompIO", "CompLS", "CompCP",
                "TensHF", "TensMaxDef", "TensAccDef", "TensProDef", "CompHF", "CompMaxDef", "CompAccDef", "CompProDef", "Color", "Notes"], "steel"),
            ["General"] = ("General", ["t3", "t2", "Area", "As2", "As3", "I33", "I22", "I23", "S33Pos", "S33Neg", "S22Pos", "S22Neg", "R33", "R22", "Z33", "Z22", "J",
                "CGOffset3", "CGOffset2", "PNAOffset3", "PNAOffset2", "SCOffset3", "SCOffset2", "Color", "Notes"], "any"),
            ["Nonprismatic"] = ("Nonprismatic", ["StartSect", "EndSect", "LengthType", "Length", "EI33Var", "EI22Var", "Color", "Notes"], "none"),
            ["Auto Select"] = ("Auto Select List", ["DesignType", "FromFile", "FileName", "StartSect", "IncludedSec", "Notes"], "none"),
            ["SD Section"] = ("Section Designer", ["DesignType", "IsDesigned", "NotSizeType", "NotAutoFact", "NotUserSize", "Color", "Notes"], "any"),
            ["Composite Shear Wall"] = (null, ["Usage", "ConcMat", "SteelMat", "Lw", "tsc", "tpw", "tpf", "dtie", "TieSpacing", "EAMod", "EIMod", "Color", "Notes"], "none"),
        };

    /// <summary>The fields whose rows a section has one of for each part: a nonprismatic section's segments, an auto
    /// select list's sections.</summary>
    private static readonly Dictionary<string, string[]> PerRow = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nonprismatic"] = ["StartSect", "EndSect", "LengthType", "Length", "EI33Var", "EI22Var"],
        ["Auto Select"] = ["IncludedSec"],
    };

    public static bool Is(string shape) => All.ContainsKey(shape);

    /// <summary>The ETABS table a section of the shape goes in (the 13 .e2k shapes' tables are named by the shape), or null.</summary>
    public static string? Table(string shape) =>
        All.TryGetValue(shape, out var t) ? t.Table is null ? null : "Frame Section Property Definitions - " + t.Table : "Frame Section Property Definitions - " + shape;

    /// <summary>The kind of each field's value, for a units change, where it is not the table field's usual one.</summary>
    public static ModelUnits.Dim? FieldDim(string field, IReadOnlyDictionary<string, string> row)
    {
        switch (field)
        {
            case "dis" or "SingleAngW" or "SingleChanW" or "FilletRad" or "CornerRad" or "LipDepth" or "NotUserSize" or "B1" or "B2" or "B3" or "B4" or "D1" or "D2" or "D3" or "D4" or "D5" or "D6"
                or "Lw" or "tsc" or "tpw" or "tpf" or "dtie" or "TieSpacing" or "TPWidth" or "TPThickness" or "BPWidth" or "BPThickness" or "HoleSpacing" or "HoleWidth" or "HoleHeight" or "HoleDiameter" or "MinSpanLen" or "MaxSpanLen" or "SpanLength"
                or "LengthYC" or "LengthES" or "PanelWidth" or "WallThick" or "PlateThick" or "R33" or "R22" or "CGOffset3" or "CGOffset2" or "PNAOffset3" or "PNAOffset2" or "SCOffset3" or "SCOffset2":
                return ModelUnits.Dim.Len;
            case "AreaYC": return ModelUnits.Dim.Area;
            case "AnalysisI33" or "MomInertia": return ModelUnits.Dim.Inertia;
            case "S33Pos" or "S33Neg" or "S22Pos" or "S22Neg": return ModelUnits.Dim.Vol;
            case "ShearCap": return ModelUnits.Dim.Force;
            case "MomentCap": return ModelUnits.Dim.Moment;
            case "TLCapacity" or "LLDeflCap" or "StiffES": return ModelUnits.Dim.PerLen;
            // A joist's unit weight is a force per length; a segment's length is a length only when it is absolute.
            case "UnitWeight" when row.ContainsKey("AnalysisI33") || row.ContainsKey("MomentCap"): return ModelUnits.Dim.PerLen;
            case "Length" when row.ContainsKey("LengthType"): return string.Equals(row["LengthType"], "Absolute", StringComparison.OrdinalIgnoreCase) ? ModelUnits.Dim.Len : ModelUnits.Dim.None;
            default: return null;
        }
    }

    /// <summary>
    /// Adds or changes a section of one of these shapes: its material (none for the shapes made of other sections), its
    /// fields by the table's names, and for a nonprismatic section or an auto select list one row per segment or section.
    /// </summary>
    public static FrameSection Set(StructuralModel m, string name, string? was, string shape, string? material,
        IReadOnlyDictionary<string, string> fields, IReadOnlyList<IReadOnlyDictionary<string, string>>? rows = null)
    {
        var def = All.FirstOrDefault(x => x.Key.Equals(shape, StringComparison.OrdinalIgnoreCase));
        if (def.Key is null) throw new ArgumentException($"There is no section shape '{shape}'.");
        shape = def.Key;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the frame section a name.");
        if (name.Contains('"')) throw new ArgumentException("A name cannot have a double quote in it.");
        var kind = def.Value.Material;
        material = string.IsNullOrWhiteSpace(material) ? null : material.Trim();
        if (kind == "none") material = null;
        else
        {
            var mat = material is null ? null : m.Materials.Find(x => x.Name == material);
            if (mat is null) throw new ArgumentException(material is null ? $"A {shape} section needs a material." : $"There is no material '{material}'.");
            if (kind == "steel" && mat.IsConcrete) throw new ArgumentException($"{shape} is a steel shape; {material} is concrete.");
            if (kind == "concrete" && !mat.IsConcrete) throw new ArgumentException($"{shape} is a concrete shape; {material} is {mat.Type}.");
        }
        var old = m.FrameSections.Find(x => x.Name == (was ?? name));
        if (was is not null && old is null) throw new ArgumentException($"There is no frame section '{was}'.");
        if (was is not null && was != name && m.FrameSections.Exists(x => x.Name == name)) throw new ArgumentException($"A frame section is already named '{name}'.");
        var row = Row(shape, def.Value.Fields, fields);
        var list = new List<Dictionary<string, string>>();
        var per = PerRow.TryGetValue(shape, out var p) ? p : [];
        if (rows is { Count: > 0 })
            foreach (var r in rows)
            {
                var one = new Dictionary<string, string>(row, StringComparer.Ordinal);
                foreach (var (k, v) in Row(shape, per, r)) one[k] = v;
                list.Add(one);
            }
        else list.Add(row);
        foreach (var f in per.Where(f => f is "StartSect" or "EndSect" or "IncludedSec"))
            foreach (var r in list)
                if (r.TryGetValue(f, out var s) && !m.FrameSections.Exists(x => x.Name == s) && !(f == "StartSect" && shape == "Auto Select"))
                    throw new ArgumentException($"There is no frame section '{s}'.");
        foreach (var f in new[] { "ISection", "TopRootISection", "BottomRootISection", "EmbedISect", "TopChord", "BottomChord", "WebMembers" })
            if (row.TryGetValue(f, out var s) && !m.FrameSections.Exists(x => x.Name == s && x.Name != (was ?? name))) throw new ArgumentException($"There is no frame section '{s}'.");
        foreach (var f in new[] { "FillMat", "EncaseMat", "TPMaterial", "BPMaterial", "SteelMat", "ConcMat" })
            if (row.TryGetValue(f, out var s) && !m.Materials.Exists(x => x.Name == s)) throw new ArgumentException($"There is no material '{s}'.");

        var line = new List<string>();
        if (material is not null) line.AddRange(["MATERIAL", Quote(material)]);
        line.AddRange(["SHAPE", Quote(shape)]);
        if (shape == "Auto Select") line.AddRange(["AUTOSELECTDESIGNTYPE", Quote(row.GetValueOrDefault("DesignType") ?? "Steel")]);
        var sec = old ?? new FrameSection();
        if (old is null) m.FrameSections.Add(sec);
        var mods = old?.Lines.Where(l => l.Count > 0 && PropertyDefs.FrameModifiers.Contains(l[0])).ToList() ?? [];
        sec.Name = name;
        sec.Lines = [line, .. mods];
        var bars = sec.Tables?.GetValueOrDefault(DefineTables.ColumnReinforcing);
        var sdWas = shape == "SD Section" ? sec.Tables?.GetValueOrDefault(SdSourceTable) : null;
        sec.Tables = new(StringComparer.OrdinalIgnoreCase) { [shape] = list };
        if (bars is not null) sec.Tables[DefineTables.ColumnReinforcing] = bars;
        if (sdWas is not null) sec.Tables[SdSourceTable] = sdWas;
        if (was is not null && was != name)
        {
            foreach (var a in m.LineAssigns.Where(x => x.Section == was)) a.Section = name;
            foreach (var r in m.Passthrough) for (var i = 0; i < r.Lines.Count; i++) r.Lines[i] = r.Lines[i].Replace(Quote(was) + " ", Quote(name) + " ");
            foreach (var s in m.FrameSections.Where(x => x.Tables is not null))
                foreach (var rs in s.Tables!.Values) foreach (var r in rs) foreach (var k in r.Keys.ToList()) if (r[k] == was && k is not ("Notes" or "Color")) r[k] = name;
        }
        return sec;
    }

    private static Dictionary<string, string> Row(string shape, string[] fields, IReadOnlyDictionary<string, string> given)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in given)
        {
            var f = fields.FirstOrDefault(x => x.Equals(k, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"A {shape} section has no field '{k}'. It has: {string.Join(", ", fields)}.");
            if (v.Trim().Length > 0) row[f] = v.Trim();
        }
        return row;
    }

    /// <summary>A section's rows of its table as ETABS takes them: Name, Material and the modifiers added.</summary>
    public static List<Dictionary<string, string>> Rows(FrameSection s)
    {
        var rows = s.Tables is not null && s.Tables.TryGetValue(s.Shape, out var r) ? r : [new Dictionary<string, string>()];
        var mods = PropertyDefs.Modifiers(s.Lines, PropertyDefs.FrameModifiers);
        return rows.Select(x =>
        {
            var o = new Dictionary<string, string>(x, StringComparer.Ordinal) { ["Name"] = s.Name };
            if (s.Material.Length > 0) o["Material"] = s.Material;
            for (var i = 0; i < Mods.Length; i++) if (mods.TryGetValue(PropertyDefs.FrameModifiers[i], out var v)) o[Mods[i]] = Number(v);
            return o;
        }).ToList();
    }

    /// <summary>
    /// The .e2k lines for a section of one of these shapes: an auto select list as ETABS writes it; any other shape as a
    /// stand-in of the same name the .e2k can carry (a concrete rectangle or a steel I of its depth and width, its own
    /// material or the first that fits), with its modifiers; the table then gives ETABS the section as it is.
    /// </summary>
    public static IEnumerable<List<string>> E2kLines(FrameSection s, StructuralModel m)
    {
        if (s.Shape == "Auto Select") { foreach (var l in s.Lines) yield return l; yield break; }
        // A section turned into a Section Designer section keeps the line it had: ETABS gets the section it was.
        if (SdSource(s) is { } was)
        {
            yield return was;
            foreach (var l in s.Lines.Where(l => l.Count > 0 && PropertyDefs.FrameModifiers.Contains(l[0]))) yield return l;
            yield break;
        }
        var row = s.Tables is not null && s.Tables.TryGetValue(s.Shape, out var rs) && rs.Count > 0 ? rs[0] : new Dictionary<string, string>();
        double N(params string[] keys) { foreach (var k in keys) if (row.TryGetValue(k, out var t) && TryNumber(t, out var v) && v > 0) return v; return 0; }
        var mat = m.Materials.Find(x => x.Name == s.Material)
            ?? m.Materials.Find(x => x.Name == (row.GetValueOrDefault("EncaseMat") ?? row.GetValueOrDefault("TPMaterial") ?? row.GetValueOrDefault("SteelMat") ?? ""))
            ?? FirstOf(m, row)
            ?? m.Materials.Find(x => x.Type.Equals("Steel", StringComparison.OrdinalIgnoreCase))
            ?? m.Materials.FirstOrDefault();
        if (mat is null) yield break;
        var size = Size(m, s) ?? (0.0, 0.0);
        var d = N("t3", "Depth", "D1", "tsc") is var d0 && d0 > 0 ? d0 : size.Item1 > 0 ? size.Item1 : Default(m);
        var b = N("t2", "B1", "Lw") is var b0 && b0 > 0 ? b0 : size.Item2 > 0 ? size.Item2 : d / 2;
        var line = mat.IsConcrete || s.Shape is "Concrete Encasement Rectangle" or "Concrete Encasement Circle"
            ? new List<string> { "MATERIAL", Quote(mat.IsConcrete ? mat.Name : (m.Materials.Find(x => x.IsConcrete) ?? mat).Name), "SHAPE", Quote("Concrete Rectangular"), "D", Number(d), "B", Number(b) }
            : new List<string> { "MATERIAL", Quote(mat.Name), "SHAPE", Quote("Steel I/Wide Flange"), "D", Number(d), "B", Number(b), "TF", Number(Math.Min(d, b) / 16), "TW", Number(Math.Min(d, b) / 24) };
        yield return line;
        foreach (var l in s.Lines.Where(l => l.Count > 0 && PropertyDefs.FrameModifiers.Contains(l[0]))) yield return l;
    }

    /// <summary>Where a section made a Section Designer section by Convert/Copy to SD Section keeps the .e2k line it had.</summary>
    public const string SdSourceTable = "SD Source";

    public static List<string>? SdSource(FrameSection s) =>
        s.Tables?.GetValueOrDefault(SdSourceTable) is { Count: > 0 } r && r[0].TryGetValue("Line", out var l) ? Tokenize(l) : null;

    /// <summary>
    /// Frame Properties' Convert to SD Section (the section itself, its members keep it) and Copy to SD Section (a new
    /// section named SD + its name): a Section Designer section of the same material, holding the section's shape.
    /// </summary>
    public static FrameSection ToSd(StructuralModel m, string name, bool convert)
    {
        var s = m.FrameSections.Find(x => x.Name == name) ?? throw new ArgumentException($"There is no frame section '{name}'.");
        if (s.Shape is "SD Section" or "Auto Select" or "Nonprismatic") throw new ArgumentException($"{name} is a {s.Shape} section; it cannot be made a Section Designer section.");
        var line = s.Lines.FirstOrDefault(l => l.Contains("SHAPE")) ?? throw new ArgumentException($"{name} has no shape.");
        var source = Is(s.Shape) ? E2kLines(s, m).First() : line;
        var mat = m.Materials.Find(x => x.Name == s.Material);
        var target = convert ? name : "SD" + name;
        if (!convert && m.FrameSections.Exists(x => x.Name == target)) throw new ArgumentException($"A frame section is already named '{target}'.");
        var sd = Set(m, target, convert ? name : null, "SD Section", s.Material.Length > 0 ? s.Material : (mat ?? m.Materials.First()).Name,
            new Dictionary<string, string> { ["DesignType"] = mat is { IsConcrete: true } ? "Concrete Column" : "General Steel Section", ["Color"] = PropertyDefs.FrameExtras(s).GetValueOrDefault("Color") ?? "" });
        sd.Tables![SdSourceTable] = [new() { ["Line"] = string.Join(" ", source) }];
        return sd;
    }

    // The material of the section a cover plate, root I, nonprismatic segment or joist chord is made of.
    private static Material? FirstOf(StructuralModel m, IReadOnlyDictionary<string, string> row)
    {
        foreach (var f in new[] { "ISection", "TopRootISection", "StartSect", "TopChord", "EmbedISect" })
            if (row.TryGetValue(f, out var n) && m.FrameSections.Find(x => x.Name == n) is { } sec && m.Materials.Find(x => x.Name == sec.Material) is { } mm) return mm;
        return null;
    }

    // The depth and width of the section a section is made of, for its stand-in.
    private static (double, double)? Size(StructuralModel m, FrameSection s)
    {
        var row = s.Tables is not null && s.Tables.TryGetValue(s.Shape, out var rs) && rs.Count > 0 ? rs[0] : null;
        if (row is null) return null;
        foreach (var f in new[] { "ISection", "TopRootISection", "StartSect", "TopChord" })
            if (row.TryGetValue(f, out var n) && m.FrameSections.Find(x => x.Name == n) is { } sec && sec.Number("D") is { } d) return (d, sec.Number("B") ?? d / 2);
        return null;
    }

    // A depth of half a metre in the model's length units.
    private static double Default(StructuralModel m) => 0.5 / (ModelUnits.LengthM(m.Units.Length) ?? 1);
}
