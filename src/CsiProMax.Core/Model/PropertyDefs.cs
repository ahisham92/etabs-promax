using CsiProMax.E2k;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Model;

/// <summary>One frame section shape the CSI programs offer, with the dimensions its form asks for (e2k keywords).</summary>
public sealed record ShapeDef(string Name, string[] Dims, bool Concrete, string About);

/// <summary>A spring property (Define > Spring Properties): point (UX..RZ), line (U1, U2, U3, R1) or area (U1, U2, U3).</summary>
public sealed record SpringDef(string Kind, string Name, Dictionary<string, double> Values);

/// <summary>
/// The Define and Assign items for the model's properties, as ETABS writes them in its .e2k: materials of every type,
/// frame sections of every shape, slab and wall types, property modifiers, member releases, local axes, insertion
/// points, end offsets and springs. Plain code on the model; the tools check their input and call these.
/// </summary>
public static class PropertyDefs
{
    public static readonly string[] MaterialTypes = ["Concrete", "Steel", "Rebar", "Tendon", "Aluminum", "ColdFormed", "Masonry", "Other"];

    /// <summary>The e2k keyword of each material value the forms ask for.</summary>
    public static readonly (string Key, string Label)[] MaterialValues =
    [
        ("WEIGHTPERVOLUME", "Weight per unit volume"), ("E", "Modulus of elasticity"), ("U", "Poisson's ratio"), ("A", "Coefficient of thermal expansion"),
        ("FC", "Specified concrete compressive strength f'c"), ("FY", "Minimum yield stress Fy"), ("FU", "Minimum tensile stress Fu"),
        ("FYE", "Expected yield stress Fye"), ("FUE", "Expected tensile stress Fue"),
    ];

    /// <summary>The Nonlinear Material Data values the .e2k carries, by its keywords (strains and the acceptance criteria).</summary>
    public static readonly string[] MaterialNonlinearValues =
        ["STRAINATFC", "STRAINATULTIMATE", "STRAINATHARDENING", "STRAINATMAXSTRESS", "STRAINATRUPTURE", "FINALSLOPE", "ENERGYDEGFACT",
         "IOTENSION", "LSTENSION", "CPTENSION", "IOCOMPRESSION", "LSCOMPRESSION", "CPCOMPRESSION"];

    /// <summary>The material's choices the .e2k carries as text, with the values the program offers ([] when any text goes).</summary>
    public static readonly Dictionary<string, string[]> MaterialTexts = new(StringComparer.Ordinal)
    {
        ["HYSTYPE"] = ["Elastic", "Kinematic", "Takeda", "Pivot", "Concrete", "BRB Hardening", "Degrading", "Isotropic"],
        ["SSTYPE"] = ["Simple", "Mander", "Park", "User", "250ksi", "270ksi"],
        ["TIMEDEPCONCCODE"] = [],
    };

    /// <summary>The keywords of the nonlinear line for a material type, in ETABS's order; [] where ETABS writes none.</summary>
    public static string[] NonlinearKeys(string type) => type switch
    {
        "Concrete" => ["HYSTYPE", "ENERGYDEGFACT", "SSTYPE", "STRAINATFC", "STRAINATULTIMATE", "FINALSLOPE", .. Acceptance],
        "Steel" => ["HYSTYPE", "SSTYPE", "STRAINATHARDENING", "STRAINATMAXSTRESS", "STRAINATRUPTURE", "FINALSLOPE", .. Acceptance],
        "Rebar" => ["HYSTYPE", "SSTYPE", "STRAINATHARDENING", "STRAINATULTIMATE", "FINALSLOPE", .. Acceptance],
        "Tendon" => ["HYSTYPE", "SSTYPE", "FINALSLOPE", .. Acceptance],
        _ => [],
    };

    private static readonly string[] Acceptance = ["IOTENSION", "LSTENSION", "CPTENSION", "IOCOMPRESSION", "LSCOMPRESSION", "CPCOMPRESSION"];

    /// <summary>ETABS's own values for a new material of the type (its .e2k files, 2026-11-26).</summary>
    public static Dictionary<string, string> NonlinearDefaults(string type) => type switch
    {
        "Concrete" => new() { ["HYSTYPE"] = "Concrete", ["ENERGYDEGFACT"] = "0", ["SSTYPE"] = "Mander", ["STRAINATFC"] = "0.002", ["STRAINATULTIMATE"] = "0.005", ["FINALSLOPE"] = "-0.1",
            ["IOTENSION"] = "0.01", ["LSTENSION"] = "0.02", ["CPTENSION"] = "0.05", ["IOCOMPRESSION"] = "0.003", ["LSCOMPRESSION"] = "0.006", ["CPCOMPRESSION"] = "0.015" },
        "Tendon" => new() { ["HYSTYPE"] = "Kinematic", ["SSTYPE"] = "270ksi", ["FINALSLOPE"] = "-0.1",
            ["IOTENSION"] = "0.015", ["LSTENSION"] = "0.023", ["CPTENSION"] = "0.03", ["IOCOMPRESSION"] = "0.015", ["LSCOMPRESSION"] = "0.023", ["CPCOMPRESSION"] = "0.03" },
        _ => new() { ["HYSTYPE"] = "Kinematic", ["SSTYPE"] = "Simple", ["STRAINATHARDENING"] = type == "Rebar" ? "0.01" : "0.015", ["STRAINATMAXSTRESS"] = "0.11", ["STRAINATRUPTURE"] = "0.17",
            ["STRAINATULTIMATE"] = "0.09", ["FINALSLOPE"] = "-0.1",
            ["IOTENSION"] = "0.01", ["LSTENSION"] = "0.02", ["CPTENSION"] = "0.05", ["IOCOMPRESSION"] = "0.005", ["LSCOMPRESSION"] = "0.01", ["CPCOMPRESSION"] = "0.02" },
    };

    public static readonly ShapeDef[] Shapes =
    [
        new("Concrete Rectangular", ["D", "B"], true, "depth D, width B"),
        new("Concrete Circle", ["D"], true, "diameter D"),
        new("Concrete Tee", ["D", "B", "TF", "TW"], true, "depth D, flange width B, flange thickness TF, web thickness TW"),
        new("Concrete L", ["D", "B", "TF", "TW"], true, "depth D, flange width B, flange thickness TF, web thickness TW"),
        new("Steel I/Wide Flange", ["D", "B", "TF", "TW", "BBOTTOM", "TFBOTTOM"], false, "depth D, top flange B and TF, web TW, bottom flange BBOTTOM and TFBOTTOM (the top's when not given)"),
        new("Steel Channel", ["D", "B", "TF", "TW"], false, "depth D, flange width B, flange TF, web TW"),
        new("Steel Tee", ["D", "B", "TF", "TW"], false, "depth D, flange width B, flange TF, web TW"),
        new("Steel Angle", ["D", "B", "TF", "TW"], false, "vertical leg D, horizontal leg B, horizontal leg thickness TF, vertical leg thickness TW"),
        new("Steel Double Angle", ["D", "B", "TF", "TW", "DIS"], false, "as an angle, with the back to back distance DIS"),
        new("Steel Double Channel", ["D", "B", "TF", "TW", "DIS"], false, "as a channel, with the back to back distance DIS"),
        new("Steel Tube", ["D", "B", "TF", "TW"], false, "depth D, width B, flange TF, web TW"),
        new("Steel Pipe", ["D", "TW"], false, "outside diameter D, wall thickness TW"),
        new("Steel Rod", ["D"], false, "diameter D"),
    ];

    public static readonly string[] FrameModifiers = ["AMOD", "A2MOD", "A3MOD", "JMOD", "I2MOD", "I3MOD", "MMOD", "WMOD"];
    public static readonly string[] ShellModifiers = ["F11MOD", "F22MOD", "F12MOD", "M11MOD", "M22MOD", "M12MOD", "V13MOD", "V23MOD", "MMOD", "WMOD"];

    public static readonly string[] SlabTypes = ["Slab", "Drop", "Stiff", "Ribbed", "Waffle", "Mat", "Footing"];
    public static readonly string[] ModelingTypes = ["ShellThin", "ShellThick", "Membrane", "Layered"];

    /// <summary>The member end releases a frame can have.</summary>
    public static readonly string[] Releases = ["P", "V2", "V3", "T", "M2", "M3"];

    // ---- materials ----

    public static Material SetMaterial(StructuralModel m, string name, string? was, string type, string? grade, IReadOnlyDictionary<string, double> values,
        IReadOnlyDictionary<string, string>? texts = null, IReadOnlyDictionary<string, List<Dictionary<string, string>>>? tables = null)
    {
        type = MaterialTypes.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There is no material type '{type}'. Types: {string.Join(", ", MaterialTypes)}.");
        Name(name, "material");
        var old = m.Materials.Find(x => x.Name == (was ?? name));
        if (was is not null && old is null) throw new ArgumentException($"There is no material '{was}'.");
        if (was is not null && was != name && m.Materials.Exists(x => x.Name == name)) throw new ArgumentException($"A material is already named '{name}'.");
        foreach (var (k, v) in values)
        {
            var nonlinear = MaterialNonlinearValues.Contains(k);
            if (!nonlinear && !MaterialValues.Any(x => x.Key == k)) throw new ArgumentException($"A material has no value '{k}'. Values: {string.Join(", ", MaterialValues.Select(x => x.Key).Concat(MaterialNonlinearValues))}.");
            if (double.IsNaN(v) || double.IsInfinity(v) || (v < 0 && k != "A" && k != "FINALSLOPE")) throw new ArgumentException($"{k} must be a number, 0 or more.");
        }
        texts ??= new Dictionary<string, string>();
        foreach (var (k, v) in texts)
        {
            if (!MaterialTexts.ContainsKey(k)) throw new ArgumentException($"A material has no '{k}'. It has: {string.Join(", ", MaterialTexts.Keys)}.");
            if (v.Length > 0 && MaterialTexts[k].Length > 0 && !MaterialTexts[k].Contains(v)) throw new ArgumentException($"{k} is one of {string.Join(", ", MaterialTexts[k])}, not '{v}'.");
        }
        double Get(string k, double fallback) => values.TryGetValue(k, out var v) ? v : old?.Number(k) ?? fallback;
        var uniaxial = type is "Rebar" or "Tendon";
        var e = Get("E", 0);
        if (e <= 0) throw new ArgumentException("Give the modulus of elasticity E, above 0.");
        var u = Get("U", type == "Concrete" ? 0.2 : 0.3);
        if (!uniaxial && u >= 0.5) throw new ArgumentException("Poisson's ratio U must be below 0.5.");
        var lines = new List<List<string>>
        {
            new() { "TYPE", Quote(type), "GRADE", Quote(grade ?? old?.Get("GRADE") ?? type), "WEIGHTPERVOLUME", Number(Get("WEIGHTPERVOLUME", 0)) },
            uniaxial ? new() { "SYMTYPE", Quote("Uniaxial"), "E", Number(e), "A", Number(Get("A", 1.17e-5)) }
                     : new() { "SYMTYPE", Quote("Isotropic"), "E", Number(e), "U", Number(u), "A", Number(Get("A", type == "Concrete" ? 9.9e-6 : 1.17e-5)) },
        };
        if (type == "Concrete")
        {
            var fc = Get("FC", 0);
            if (fc <= 0) throw new ArgumentException("A concrete needs f'c (FC), above 0.");
            lines.Add(["FC", Number(fc)]);
        }
        else if (type is "Steel" or "Rebar" or "Tendon" or "ColdFormed" or "Aluminum")
        {
            var fy = Get("FY", 0); var fu = Get("FU", 0);
            if (fy <= 0 || fu < fy) throw new ArgumentException($"A {type.ToLowerInvariant()} needs Fy above 0 and Fu at least Fy.");
            lines.Add(["FY", Number(fy), "FU", Number(fu), "FYE", Number(Get("FYE", fy * 1.1)), "FUE", Number(Get("FUE", fu * 1.1))]);
        }
        // Time dependence (Define > Material > Time Dependent Properties): ETABS's .e2k names the CEB-FIP 90 code; the
        // other codes and every code's values go in its tables (Concrete Data's TimeType and the code's own table).
        var code = texts.TryGetValue("TIMEDEPCONCCODE", out var tc) ? tc : old?.Get("TIMEDEPCONCCODE");
        if (type == "Concrete" && !string.IsNullOrEmpty(code)) lines.Add(["TIMEDEPCONCCODE", Quote(code)]);
        // Nonlinear Material Data: the hysteresis, the stress-strain curve and its strains, and the acceptance criteria,
        // in the keywords ETABS writes them with (its own .e2k files, 2026-11-26). Written when the material had them or
        // the form gives them; ETABS fills in its defaults otherwise.
        if (NonlinearKeys(type) is { Length: > 0 } keys)
        {
            var oldLine = old?.Lines.FirstOrDefault(l => l.Count > 0 && l[0] == "HYSTYPE");
            var given = values.Keys.Any(MaterialNonlinearValues.Contains) || texts.ContainsKey("HYSTYPE") || texts.ContainsKey("SSTYPE");
            if (oldLine is not null || given)
            {
                var defaults = NonlinearDefaults(type);
                string Val(string k)
                {
                    if (k is "HYSTYPE" or "SSTYPE") return Quote(texts.TryGetValue(k, out var t) && t.Length > 0 ? t : old?.Get(k) ?? defaults[k]);
                    if (values.TryGetValue(k, out var v)) return Number(v);
                    var at = oldLine?.IndexOf(k) ?? -1;
                    return at >= 0 && at + 1 < oldLine!.Count ? oldLine[at + 1] : defaults[k];
                }
                lines.Add([.. keys.SelectMany(k => new[] { k, Val(k) })]);
            }
        }
        else if (old?.Lines.FirstOrDefault(l => l.Count > 0 && l[0] == "HYSTYPE") is { } keep) lines.Add(keep);
        var mat = old ?? new Material();
        if (old is null) m.Materials.Add(mat);
        mat.Name = name;
        mat.Lines = lines;
        if (tables is not null) ProgramTables.SetMaterialTables(mat, tables);
        if (was is not null && was != name)
        {
            foreach (var d in m.FrameSections.Cast<Definition>().Concat(m.ShellSections))
                foreach (var l in d.Lines)
                    for (var i = 0; i + 1 < l.Count; i++) if (l[i] == "MATERIAL" && Unquote(l[i + 1]) == was) l[i + 1] = Quote(name);
        }
        return mat;
    }

    // ---- frame sections of every shape ----

    public static ShapeDef? Shape(string name) => Shapes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static FrameSection SetShape(StructuralModel m, string name, string? was, string shape, string material, IReadOnlyDictionary<string, double> dims)
    {
        var def = Shape(shape) ?? throw new ArgumentException($"There is no section shape '{shape}'. Shapes: {string.Join(", ", Shapes.Select(s => s.Name))}.");
        Name(name, "frame section");
        var mat = m.Materials.Find(x => x.Name == material) ?? throw new ArgumentException($"There is no material '{material}'.");
        if (def.Concrete != mat.IsConcrete && !(def.Name == "Steel Rod" || def.Name == "Steel Pipe" && mat.Type == "Aluminum"))
            throw new ArgumentException($"{def.Name} is a {(def.Concrete ? "concrete" : "steel")} shape; {material} is {mat.Type}.");
        var old = m.FrameSections.Find(x => x.Name == (was ?? name));
        if (was is not null && old is null) throw new ArgumentException($"There is no frame section '{was}'.");
        if (was is not null && was != name && m.FrameSections.Exists(x => x.Name == name)) throw new ArgumentException($"A frame section is already named '{name}'.");
        foreach (var k in dims.Keys) if (!def.Dims.Contains(k)) throw new ArgumentException($"{def.Name} has no dimension '{k}'. It takes {string.Join(", ", def.Dims)} ({def.About}).");
        var needed = def.Dims.Where(k => k is not ("BBOTTOM" or "TFBOTTOM")).ToList();
        foreach (var k in needed) if (!dims.TryGetValue(k, out var v) || !(v > 0)) throw new ArgumentException($"{def.Name} needs {k} above 0 ({def.About}).");
        double D(string k) => dims.TryGetValue(k, out var v) ? v : 0;
        if (needed.Contains("TF") && 2 * D("TF") >= D("D") && def.Name is not ("Steel Angle" or "Steel Double Angle" or "Steel Tee" or "Concrete Tee" or "Concrete L"))
            throw new ArgumentException("The flanges are thicker than the depth.");
        if (needed.Contains("TW") && def.Name == "Steel Pipe" && 2 * D("TW") >= D("D")) throw new ArgumentException("The pipe wall is thicker than the radius.");
        if (needed.Contains("TW") && def.Name is not "Steel Pipe" && D("TW") >= D("B") && def.Name is not ("Steel Angle" or "Steel Double Angle"))
            throw new ArgumentException("The web is wider than the section.");
        var line = new List<string> { "MATERIAL", Quote(material), "SHAPE", Quote(def.Name) };
        foreach (var k in def.Dims) if (dims.TryGetValue(k, out var v)) line.AddRange([k, Number(v)]);
        var sec = old ?? new FrameSection();
        if (old is null) m.FrameSections.Add(sec);
        var mods = old?.Lines.Where(l => l.Count > 0 && FrameModifiers.Contains(l[0])).ToList() ?? [];
        sec.Name = name;
        sec.Lines = [line, .. mods];
        if (was is not null && was != name)
        {
            foreach (var a in m.LineAssigns.Where(x => x.Section == was)) a.Section = name;
            foreach (var r in m.Passthrough) for (var i = 0; i < r.Lines.Count; i++) r.Lines[i] = r.Lines[i].Replace(Quote(was) + " ", Quote(name) + " ");
        }
        return sec;
    }

    /// <summary>
    /// What ETABS 23.2's Frame Section Property Data form holds beside the shape's size (its display colour, notes,
    /// notional size, fillet or corner radius, mirroring and rigid zone): the fields of its "Frame Section Property
    /// Definitions - (shape)" table the .e2k has no keyword for, kept in the section's <see cref="Definition.Tables"/>
    /// under its shape and put into ETABS through that table (SAP2000 takes the colour, notes and fillet radius in its
    /// "Frame Section Properties 01 - General").
    /// </summary>
    public static string[] FrameExtraFields(string shape)
    {
        var concrete = new[] { "NotSizeType", "NotAutoFact", "NotUserSize", "RigidZone" };
        string[] own = shape switch
        {
            "Concrete Rectangular" or "Concrete Circle" => concrete,
            "Concrete Tee" => [.. concrete, "MirrorAbt3"],
            "Concrete L" => [.. concrete, "MirrorAbt2", "MirrorAbt3"],
            "Steel I/Wide Flange" or "Steel Double Channel" => ["FilletRad"],
            "Steel Channel" => ["FilletRad", "MirrorAbt2"],
            "Steel Tee" or "Steel Double Angle" => ["FilletRad", "MirrorAbt3"],
            "Steel Angle" => ["FilletRad", "MirrorAbt2", "MirrorAbt3"],
            "Steel Tube" => ["CornerRad"],
            _ => [],
        };
        return [.. own, "Color", "Notes"];
    }

    /// <summary>Sets a frame section's other fields (<see cref="FrameExtraFields"/>); empty values are taken off.</summary>
    public static void SetFrameExtras(FrameSection s, IReadOnlyDictionary<string, string> extras) => SetExtras(s, s.Shape, FrameExtraFields(s.Shape), extras, $"A {s.Shape} section");

    /// <summary>A frame section's other fields, or an empty row.</summary>
    public static IReadOnlyDictionary<string, string> FrameExtras(FrameSection s) => Extras(s, s.Shape);

    /// <summary>
    /// What ETABS 23.2's Slab and Wall Property Data forms hold beside the .e2k's line (notional size, colour, notes; a
    /// wall's automatic rigid zone), by its "Slab Property Definitions" and "Wall Property Definitions - Specified"
    /// fields, and SAP2000's Shell Section Data material angle and bending thickness (its "Area Section Properties").
    /// </summary>
    public static string[] ShellExtraFields(bool wall) => wall
        ? ["RigidZone", "NotSizeType", "NotAutoFact", "NotUserSize", "Color", "Notes", "MatAngle", "BendThick"]
        : ["NotSizeType", "NotAutoFact", "NotUserSize", "Color", "Notes", "MatAngle", "BendThick"];

    /// <summary>The fields of <see cref="ShellExtraFields"/> that are SAP2000's only.</summary>
    public static readonly string[] SapOnlyShellFields = ["MatAngle", "BendThick"];

    /// <summary>ETABS 23.2's table for a slab or a wall section's row.</summary>
    public static string ShellTable(ShellSection s) => s.IsDeck ? DeckTable : s.IsWall ? "Wall Property Definitions - Specified" : "Slab Property Definitions";

    public static void SetShellExtras(ShellSection s, IReadOnlyDictionary<string, string> extras) => s.IsDeck
        ? SetExtras(s, DeckTable, DeckExtraFields, extras, "A deck section")
        : SetExtras(s, ShellTable(s), ShellExtraFields(s.IsWall), extras, s.IsWall ? "A wall section" : "A slab section");

    // ---- deck sections (ETABS 23.2: Define > Section Properties > Deck Sections, its Deck Property Data form) ----

    public const string DeckTable = "Deck Property Definitions";
    public static readonly string[] DeckTypes = ["Filled", "Unfilled", "Solid Slab"];
    /// <summary>Deck Property Data's fields the .e2k line has no keyword for.</summary>
    public static readonly string[] DeckExtraFields = ["Color", "Notes"];

    /// <summary>A deck's geometry and studs as the Deck Property Data form has them (lengths in the model's units, the stud
    /// strength in its force over length squared, the deck unit weight in force over length squared).</summary>
    public sealed record Deck(string Type, string SlabMaterial, string DeckMaterial, double SlabDepth, double RibDepth, double RibWidthTop, double RibWidthBottom,
        double RibSpacing, double ShearThickness, double UnitWeight, double StudDiameter, double StudHeight, double StudFu);

    /// <summary>Adds or changes a deck section, written as ETABS 23 writes it in $ DECK PROPERTIES (SHELLPROP ... PROPTYPE
    /// "Deck" DECKTYPE ...): a filled deck has the concrete, the deck and the studs, an unfilled deck the deck only, a solid
    /// slab the concrete and the studs.</summary>
    public static ShellSection SetDeck(StructuralModel m, string name, string? was, Deck d)
    {
        Name(name, "deck section");
        var type = DeckTypes.FirstOrDefault(t => t.Replace(" ", "").Equals(d.Type.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"The deck type is one of {string.Join(", ", DeckTypes)}.");
        bool concrete = type != "Unfilled", deck = type != "Solid Slab";
        if (concrete && !m.Materials.Exists(x => x.Name == d.SlabMaterial)) throw new ArgumentException($"There is no material '{d.SlabMaterial}' for the slab.");
        if (deck && !m.Materials.Exists(x => x.Name == d.DeckMaterial)) throw new ArgumentException($"There is no material '{d.DeckMaterial}' for the deck.");
        if (concrete && !(d.SlabDepth > 0)) throw new ArgumentException("The slab depth (tc) is above 0.");
        if (deck && (!(d.RibDepth > 0) || !(d.RibWidthTop > 0) || !(d.RibWidthBottom > 0) || !(d.RibSpacing > d.RibWidthTop) || !(d.ShearThickness > 0)))
            throw new ArgumentException("The deck needs a rib depth, rib widths and a shear thickness above 0, and a rib spacing above the top rib width.");
        if (new[] { d.UnitWeight, d.StudDiameter, d.StudHeight, d.StudFu }.Any(x => x < 0 || double.IsNaN(x))) throw new ArgumentException("The unit weight and the stud values are 0 or more.");
        var old = m.ShellSections.Find(x => x.Name == (was ?? name));
        if (was is not null && old is null) throw new ArgumentException($"There is no deck section '{was}'.");
        if (old is not null && !old.IsDeck) throw new ArgumentException($"'{old.Name}' is a slab or wall section, not a deck.");
        if (was is not null && was != name && m.ShellSections.Exists(x => x.Name == name)) throw new ArgumentException($"A section is already named '{name}'.");
        if (old is null && m.ShellSections.Exists(x => x.Name == name)) throw new ArgumentException($"A section is already named '{name}'.");
        var line = new List<string> { "PROPTYPE", Quote("Deck"), "DECKTYPE", Quote(type == "Solid Slab" ? "SolidSlab" : type) };
        if (concrete) line.AddRange(["CONCMATERIAL", Quote(d.SlabMaterial)]);
        if (deck) line.AddRange(["DECKMATERIAL", Quote(d.DeckMaterial)]);
        if (concrete) line.AddRange(["DECKSLABDEPTH", Number(d.SlabDepth)]);
        if (deck) line.AddRange(["DECKRIBDEPTH", Number(d.RibDepth), "DECKRIBWIDTHTOP", Number(d.RibWidthTop), "DECKRIBWIDTHBOTTOM", Number(d.RibWidthBottom), "DECKRIBSPACING", Number(d.RibSpacing),
            "DECKSHEARTHICKNESS", Number(d.ShearThickness), "DECKUNITWEIGHT", Number(d.UnitWeight)]);
        if (concrete) line.AddRange(["SHEARSTUDDIAM", Number(d.StudDiameter), "SHEARSTUDHEIGHT", Number(d.StudHeight), "SHEARSTUDFU", Number(d.StudFu)]);
        var sec = old ?? new ShellSection();
        if (old is null) m.ShellSections.Add(sec);
        var mods = old?.Lines.Where(l => l.Count > 0 && ShellModifiers.Contains(l[0])).ToList() ?? [];
        sec.Name = name;
        sec.Lines = [line, .. mods];
        if (was is not null && was != name) foreach (var a in m.AreaAssigns.Where(x => x.Section == was)) a.Section = name;
        return sec;
    }

    public static IReadOnlyDictionary<string, string> ShellExtras(ShellSection s) => Extras(s, ShellTable(s));

    private static void SetExtras(Definition d, string table, string[] fields, IReadOnlyDictionary<string, string> extras, string what)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in extras)
        {
            var f = fields.FirstOrDefault(x => x.Equals(k, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{what} has no field '{k}'. It has: {string.Join(", ", fields)}.");
            if (v.Trim().Length > 0) row[f] = v.Trim();
        }
        d.Tables = row.Count > 0 ? new(StringComparer.OrdinalIgnoreCase) { [table] = [row] } : null;
    }

    private static IReadOnlyDictionary<string, string> Extras(Definition d, string table) =>
        d.Tables is not null && d.Tables.TryGetValue(table, out var rows) && rows.Count > 0 ? rows[0] : new Dictionary<string, string>();

    // ---- slab and wall types ----

    /// <summary>A ribbed slab's ribs, as ETABS 23.2 writes them: the overall depth (slab and ribs), the rib width at its top and
    /// bottom, the rib spacing, and the local axis (1 or 2) the ribs run along.</summary>
    public sealed record Ribs(double OverallDepth, double WidthTop, double WidthBottom, double Spacing, int ParallelTo = 1);

    public static ShellSection SetShellType(StructuralModel m, string name, string? was, bool wall, string slabType, string modeling, double thickness, string material, bool oneWay, Ribs? ribs = null)
    {
        Name(name, "slab or wall section");
        if (!m.Materials.Exists(x => x.Name == material)) throw new ArgumentException($"There is no material '{material}'.");
        if (!(thickness > 0)) throw new ArgumentException("The thickness must be above 0.");
        var type = SlabTypes.FirstOrDefault(t => t.Equals(slabType, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"The slab type is one of {string.Join(", ", SlabTypes)}.");
        var model = ModelingTypes.FirstOrDefault(t => t.Equals(modeling, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"The modeling type is one of {string.Join(", ", ModelingTypes)}.");
        var old = m.ShellSections.Find(x => x.Name == (was ?? name));
        if (was is not null && old is null) throw new ArgumentException($"There is no slab or wall section '{was}'.");
        if (was is not null && was != name && m.ShellSections.Exists(x => x.Name == name)) throw new ArgumentException($"A section is already named '{name}'.");
        var line = wall
            ? new List<string> { "PROPTYPE", Quote("Wall"), "MATERIAL", Quote(material), "MODELINGTYPE", Quote(model), "WALLTHICKNESS", Number(thickness) }
            : new List<string> { "PROPTYPE", Quote("Slab"), "MATERIAL", Quote(material), "MODELINGTYPE", Quote(model) };
        if (!wall)
        {
            if (oneWay) line.AddRange(["ONEWAYLOADDIST", Quote("Yes")]);
            if (type == "Ribbed")
            {
                if (ribs is null) throw new ArgumentException("A ribbed slab needs its ribs: overall_depth, rib_width_top, rib_width_bottom, rib_spacing (and rib_direction 1 or 2).");
                if (!(ribs.OverallDepth > thickness)) throw new ArgumentException("The overall depth of a ribbed slab is more than its slab thickness.");
                if (!(ribs.WidthTop > 0) || !(ribs.WidthBottom > 0) || ribs.WidthBottom > ribs.WidthTop + 1e-12) throw new ArgumentException("The rib widths are more than 0, the bottom no wider than the top.");
                if (!(ribs.Spacing > ribs.WidthTop)) throw new ArgumentException("The rib spacing is more than the rib width.");
                if (ribs.ParallelTo is not (1 or 2)) throw new ArgumentException("The ribs run along local axis 1 or 2.");
                line.AddRange(["SLABTYPE", Quote(type), "OVERALLDEPTH", Number(ribs.OverallDepth), "SLABTHICKNESS", Number(thickness), "SLABRIBWIDTHTOP", Number(ribs.WidthTop),
                    "SLABRIBWIDTHBOTTOM", Number(ribs.WidthBottom), "SLABRIBSPACING", Number(ribs.Spacing), "SLABRIBPARALLELTOAXIS", ribs.ParallelTo.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
            else line.AddRange(["SLABTYPE", Quote(type), "SLABTHICKNESS", Number(thickness)]);
        }
        var sec = old ?? new ShellSection();
        if (old is null) m.ShellSections.Add(sec);
        var mods = old?.Lines.Where(l => l.Count > 0 && ShellModifiers.Contains(l[0])).ToList() ?? [];
        sec.Name = name;
        sec.Lines = [line, .. mods];
        if (was is not null && was != name) foreach (var a in m.AreaAssigns.Where(x => x.Section == was)) a.Section = name;
        return sec;
    }

    // ---- property modifiers ----

    /// <summary>The modifiers on a section or an assignment, every key with 1 where none is set.</summary>
    public static Dictionary<string, double> Modifiers(IEnumerable<List<string>> lines, string[] keys)
    {
        var o = keys.ToDictionary(k => k, _ => 1.0);
        foreach (var l in lines)
            for (var i = 0; i + 1 < l.Count; i++)
                if (keys.Contains(l[i]) && TryNumber(l[i + 1], out var v)) o[l[i]] = v;
        return o;
    }

    /// <summary>Sets the modifiers (all of them, 1 for those not given) on their own line; all 1 takes the line away.</summary>
    public static void CheckModifiers(string[] keys, IReadOnlyDictionary<string, double> values)
    {
        foreach (var (k, v) in values)
        {
            if (!keys.Contains(k)) throw new ArgumentException($"There is no modifier '{k}'. Modifiers: {string.Join(", ", keys)}.");
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) throw new ArgumentException($"{k} must be 0 or more.");
        }
    }

    public static void SetModifiers(List<List<string>> lines, string[] keys, IReadOnlyDictionary<string, double> values)
    {
        CheckModifiers(keys, values);
        var now = Modifiers(lines, keys);
        foreach (var (k, v) in values) now[k] = v;
        Remove(lines, keys);
        if (now.Values.Any(v => Math.Abs(v - 1) > 1e-12)) lines.Add([.. keys.SelectMany(k => new[] { k, Number(now[k]) })]);
    }

    // ---- assignments on a member, joint or shell ----

    /// <summary>Takes these keywords and their values off every line, and drops lines left empty.</summary>
    public static void Remove(List<List<string>> lines, params string[] keys)
    {
        foreach (var l in lines)
            for (var i = 0; i + 1 < l.Count;)
                if (keys.Contains(l[i])) l.RemoveRange(i, 2); else i += 2;
        lines.RemoveAll(l => l.Count == 0);
    }

    public static string? Value(List<List<string>> lines, string key)
    {
        foreach (var l in lines)
            for (var i = 0; i + 1 < l.Count; i += 2) if (l[i] == key) return Unquote(l[i + 1]);
        return null;
    }

    public static void Put(List<List<string>> lines, string key, string? value)
    {
        Remove(lines, key);
        if (value is not null) lines.Add([key, value]);
    }

    /// <summary>Releases at each end, e.g. ["M2","M3"] and ["M3"], as ETABS writes them ("M2I M3I M3J"). Empty takes them off.</summary>
    public static void SetReleases(LineAssign a, IReadOnlyCollection<string> i, IReadOnlyCollection<string> j)
    {
        CheckReleases(i, j);
        var text = string.Join(" ", Releases.Where(i.Contains).Select(r => r + "I").Concat(Releases.Where(j.Contains).Select(r => r + "J")));
        Put(a.Extra, "RELEASE", text.Length == 0 ? null : Quote(text));
    }

    public static void CheckReleases(IReadOnlyCollection<string> i, IReadOnlyCollection<string> j)
    {
        foreach (var r in i.Concat(j)) if (!Releases.Contains(r)) throw new ArgumentException($"There is no release '{r}'. Releases: {string.Join(", ", Releases)}.");
        foreach (var r in Releases) if (i.Contains(r) && j.Contains(r) && r is "P" or "T") throw new ArgumentException($"{r} released at both ends leaves the member unstable.");
    }

    public static (List<string> I, List<string> J) ReleasesOf(LineAssign a)
    {
        var words = (Value(a.Extra, "RELEASE") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (words.Where(w => w.EndsWith('I')).Select(w => w[..^1]).ToList(), words.Where(w => w.EndsWith('J')).Select(w => w[..^1]).ToList());
    }

    // ---- springs ----

    public static readonly Dictionary<string, (string Heading, string Word, string[] Keys)> SpringKinds = new()
    {
        ["point"] = ("POINT SPRING PROPERTIES", "POINTSPRING", ["UX", "UY", "UZ", "RX", "RY", "RZ"]),
        ["line"] = ("LINE SPRING PROPERTIES", "LINESPRING", ["U1", "U2", "U3", "R1"]),
        ["area"] = ("AREA SPRING PROPERTIES", "AREASPRING", ["U1", "U2", "U3"]),
    };

    public static List<SpringDef> Springs(StructuralModel m)
    {
        var list = new List<SpringDef>();
        foreach (var (kind, (heading, word, keys)) in SpringKinds)
            foreach (var line in m.Passthrough.Where(r => r.Heading == heading).SelectMany(r => r.Lines))
            {
                var t = Tokenize(line);
                if (t.Count < 2 || t[0] != word) continue;
                var name = Unquote(t[1]);
                var s = list.Find(x => x.Kind == kind && x.Name == name);
                if (s is null) list.Add(s = new SpringDef(kind, name, keys.ToDictionary(k => k, _ => 0.0)));
                for (var i = 2; i + 1 < t.Count; i += 2) if (keys.Contains(t[i]) && TryNumber(t[i + 1], out var v)) s.Values[t[i]] = v;
            }
        return list;
    }

    public static void SetSpring(StructuralModel m, string kind, string name, IReadOnlyDictionary<string, double> values)
    {
        if (!SpringKinds.TryGetValue(kind, out var k)) throw new ArgumentException("The spring kind is point, line or area.");
        Name(name, "spring property");
        foreach (var (key, v) in values)
        {
            if (!k.Keys.Contains(key)) throw new ArgumentException($"A {kind} spring has {string.Join(", ", k.Keys)}, not '{key}'.");
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) throw new ArgumentException($"{key} must be a stiffness, 0 or more.");
        }
        if (!values.Values.Any(v => v > 0)) throw new ArgumentException("Give at least one stiffness above 0.");
        var raw = m.Passthrough.Find(r => r.Heading == k.Heading);
        if (raw is null)
        {
            // ETABS writes the spring properties after the wall properties, ahead of the points.
            raw = new RawSection { Heading = k.Heading, After = m.Passthrough.LastOrDefault(r => r.Heading.EndsWith("SPRING PROPERTIES", StringComparison.Ordinal))?.Heading ?? "WALL PROPERTIES" };
            m.Passthrough.Add(raw);
        }
        raw.Lines.RemoveAll(l => Tokenize(l) is { Count: >= 2 } t && t[0] == k.Word && Unquote(t[1]) == name);
        raw.Lines.Add("  " + Line([k.Word, Quote(name), "STIFFNESSOPTION", Quote("USERDEFINED")], [.. k.Keys.SelectMany(x => new[] { x, Number(values.TryGetValue(x, out var v) ? v : 0) })]));
    }

    public static IEnumerable<string> SpringUsers(StructuralModel m, string kind, string name) => kind switch
    {
        "point" => m.PointAssigns.Where(a => Value(a.Extra, "SPRINGPROP") == name).Select(a => a.Point + "@" + a.Story),
        "line" => m.LineAssigns.Where(a => Value(a.Extra, "SPRINGPROP") == name).Select(a => a.Label + "@" + a.Story),
        _ => m.AreaAssigns.Where(a => Value(a.Extra, "SPRINGPROP") == name).Select(a => a.Label + "@" + a.Story),
    };

    public static void DeleteSpring(StructuralModel m, string kind, string name)
    {
        if (!SpringKinds.TryGetValue(kind, out var k)) throw new ArgumentException("The spring kind is point, line or area.");
        if (!Springs(m).Exists(s => s.Kind == kind && s.Name == name)) throw new ArgumentException($"There is no {kind} spring '{name}'.");
        var users = SpringUsers(m, kind, name).Take(5).ToList();
        if (users.Count > 0) throw new ArgumentException($"{name} is assigned to {string.Join(", ", users)}: take it off those first.");
        foreach (var r in m.Passthrough.Where(r => r.Heading == k.Heading))
            r.Lines.RemoveAll(l => Tokenize(l) is { Count: >= 2 } t && t[0] == k.Word && Unquote(t[1]) == name);
        m.Passthrough.RemoveAll(r => r.Heading == k.Heading && r.Lines.Count == 0);
    }

    // ---- link properties ----

    public const string LinkHeading = "LINK PROPERTIES";
    public static readonly string[] LinkTypes = ["LINEAR", "DAMPER", "GAP", "HOOK", "PLASTIC (WEN)", "RUBBER ISOLATOR", "FRICTION ISOLATOR", "MULTILINEAR ELASTIC", "MULTILINEAR PLASTIC"];
    public static readonly string[] LinkDofs = ["U1", "U2", "U3", "R1", "R2", "R3"];

    /// <summary>A link property: its type, and per active direction its effective stiffness and damping (the linear
    /// analysis uses these; a nonlinear type's own curve goes in by Edit > Model Text).</summary>
    public sealed record LinkDef(string Name, string Type, Dictionary<string, (double Stiff, double Damp)> Dofs);

    public static List<LinkDef> Links(StructuralModel m)
    {
        var list = new List<LinkDef>();
        foreach (var line in m.Passthrough.Where(r => r.Heading == LinkHeading).SelectMany(r => r.Lines))
        {
            var t = Tokenize(line);
            if (t.Count < 3 || t[0] != "LINKPROP") continue;
            var name = Unquote(t[1]);
            var at = list.FindIndex(x => x.Name == name);
            if (at < 0) { list.Add(new LinkDef(name, "LINEAR", [])); at = list.Count - 1; }
            var l = list[at];
            for (var i = 2; i + 1 < t.Count; i += 2)
            {
                var k = t[i]; var v = Unquote(t[i + 1]);
                if (k == "TYPE") list[at] = l = l with { Type = v };
                else if (k == "DOF" && LinkDofs.Contains(v) && !l.Dofs.ContainsKey(v)) l.Dofs[v] = (0, 0);
                else if (k.Length == 7 && k.EndsWith("STIFF", StringComparison.Ordinal) && LinkDofs.Contains(k[..2]) && TryNumber(v, out var st)) l.Dofs[k[..2]] = (st, l.Dofs.GetValueOrDefault(k[..2]).Damp);
                else if (k.Length == 6 && k.EndsWith("DAMP", StringComparison.Ordinal) && LinkDofs.Contains(k[..2]) && TryNumber(v, out var dm)) l.Dofs[k[..2]] = (l.Dofs.GetValueOrDefault(k[..2]).Stiff, dm);
            }
        }
        return list;
    }

    public static void SetLink(StructuralModel m, string name, string type, IReadOnlyDictionary<string, (double Stiff, double Damp)> dofs)
    {
        Name(name, "link property");
        type = LinkTypes.FirstOrDefault(x => x.Equals(type, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"The link type is one of {string.Join(", ", LinkTypes)}.");
        if (dofs.Count == 0) throw new ArgumentException("Give at least one direction (U1 to R3) with its stiffness.");
        foreach (var (k, (st, dm)) in dofs)
        {
            if (!LinkDofs.Contains(k)) throw new ArgumentException($"A link's directions are {string.Join(", ", LinkDofs)}, not '{k}'.");
            if (!double.IsFinite(st) || !double.IsFinite(dm) || st < 0 || dm < 0) throw new ArgumentException($"{k}: stiffness and damping are 0 or more.");
        }
        var raw = m.Passthrough.Find(r => r.Heading == LinkHeading);
        if (raw is null) m.Passthrough.Add(raw = new RawSection { Heading = LinkHeading, After = "WALL PROPERTIES" });
        var at = raw.Lines.FindIndex(l => Tokenize(l) is { Count: >= 2 } t && t[0] == "LINKPROP" && Unquote(t[1]) == name);
        raw.Lines.RemoveAll(l => Tokenize(l) is { Count: >= 2 } t && t[0] == "LINKPROP" && Unquote(t[1]) == name);
        var lines = new List<string> { "  " + Line(["LINKPROP", Quote(name), "TYPE", Quote(type)]) };
        foreach (var k in LinkDofs.Where(dofs.ContainsKey))
        {
            lines.Add("  " + Line(["LINKPROP", Quote(name), "DOF", Quote(k)]));
            lines.Add("  " + Line(["LINKPROP", Quote(name), k + "STIFF", Number(dofs[k].Stiff)]));
            if (dofs[k].Damp > 0) lines.Add("  " + Line(["LINKPROP", Quote(name), k + "DAMP", Number(dofs[k].Damp)]));
        }
        raw.Lines.InsertRange(at < 0 ? raw.Lines.Count : Math.Min(at, raw.Lines.Count), lines);
    }

    public static void DeleteLink(StructuralModel m, string name)
    {
        if (!Links(m).Exists(x => x.Name == name)) throw new ArgumentException($"There is no link property '{name}'.");
        foreach (var r in m.Passthrough.Where(r => r.Heading == LinkHeading))
            r.Lines.RemoveAll(l => Tokenize(l) is { Count: >= 2 } t && t[0] == "LINKPROP" && Unquote(t[1]) == name);
        m.Passthrough.RemoveAll(r => r.Heading == LinkHeading && r.Lines.Count == 0);
    }

    // ---- frame hinges ----

    public const string HingeHeading = "FRAME HINGE ASSIGNMENTS";

    /// <summary>The degrees of freedom of an auto hinge, as Assign > Frame > Hinges offers them (the program picks the
    /// hinge from the code's tables for the member's section and kind).</summary>
    public static readonly string[] HingeDofs = ["M3", "M2", "P", "V2", "V3", "T", "P-M2-M3", "P-M3", "M3-V2"];

    /// <summary>One hinge on a frame: where along it (0 to 1 of its length) and its degrees of freedom.</summary>
    public sealed record HingeDef(double At, string Dof);

    /// <summary>Every hinge, by frame. The program cannot assign hinges by its API, so they go in by the file.</summary>
    public static List<(string Label, string Story, HingeDef Hinge)> Hinges(StructuralModel m)
    {
        var list = new List<(string, string, HingeDef)>();
        foreach (var line in m.Passthrough.Where(r => r.Heading == HingeHeading).SelectMany(r => r.Lines))
        {
            var t = Tokenize(line);
            if (t.Count < 7 || t[0] != "HINGEASSIGN") continue;
            string? dof = null; double? at = null;
            // The Modeler's form (AUTO "M3" RELDIST 0.05) and ETABS 23's own (DOF "PM2M3" ... RDISTANCE 0.05).
            for (var i = 3; i + 1 < t.Count; i++)
            {
                if (t[i] is "AUTO" or "DOF") { var d0 = Unquote(t[i + 1]); dof = HingeDofs.FirstOrDefault(h => h.Replace("-", "") == d0.Replace("-", "")) ?? d0; }
                else if (t[i] is "RELDIST" or "RDISTANCE" && TryNumber(t[i + 1], out var d)) at = d;
            }
            if (dof is not null && at is { } a) list.Add((Unquote(t[1]), Unquote(t[2]), new HingeDef(a, dof)));
        }
        return list;
    }

    public static List<HingeDef> HingesOf(StructuralModel m, string label, string story) =>
        Hinges(m).Where(h => h.Label == label && h.Story == story).Select(h => h.Hinge).ToList();

    public static void CheckHinges(IReadOnlyList<HingeDef> hinges)
    {
        foreach (var h in hinges)
        {
            if (!double.IsFinite(h.At) || h.At < 0 || h.At > 1) throw new ArgumentException("A hinge's relative distance is from 0 to 1 of the member's length.");
            if (!HingeDofs.Contains(h.Dof)) throw new ArgumentException($"A hinge is one of {string.Join(", ", HingeDofs)}, not '{h.Dof}'.");
        }
        if (hinges.GroupBy(h => (Math.Round(h.At, 6), h.Dof)).Any(g => g.Count() > 1)) throw new ArgumentException("Two of the hinges are the same, at the same place.");
    }

    /// <summary>Replaces the hinges of one frame (none takes them all off).</summary>
    public static void SetHinges(StructuralModel m, string label, string story, IReadOnlyList<HingeDef> hinges)
    {
        CheckHinges(hinges);
        bool Mine(string l) => Tokenize(l) is { Count: >= 3 } t && t[0] == "HINGEASSIGN" && Unquote(t[1]) == label && Unquote(t[2]) == story;
        var raw = m.Passthrough.Find(r => r.Heading == HingeHeading);
        if (raw is null)
        {
            if (hinges.Count == 0) return;
            m.Passthrough.Add(raw = new RawSection { Heading = HingeHeading, After = "LINE ASSIGNS" });
        }
        var at = raw.Lines.FindIndex(Mine);
        raw.Lines.RemoveAll(Mine);
        var lines = hinges.OrderBy(h => h.At).Select(h => "  " + Line(["HINGEASSIGN", Quote(label), Quote(story), "AUTO", Quote(h.Dof), "RELDIST", Number(h.At)])).ToList();
        raw.Lines.InsertRange(at < 0 ? raw.Lines.Count : Math.Min(at, raw.Lines.Count), lines);
        m.Passthrough.RemoveAll(r => r.Heading == HingeHeading && r.Lines.Count == 0);
    }

    private static void Name(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"Give the {what} a name.");
        if (name.Contains('"')) throw new ArgumentException("A name cannot have a double quote in it.");
    }
}
