using System.Reflection;
using CsiProMax.E2k;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.Model;

/// <summary>One item of a program's form: the table field it is, and the name the program shows for it.</summary>
public sealed record FormField(string Key, string Label);

/// <summary>A design the Design menu has, with the tables of its preferences (one per code) and, for ETABS, where the
/// .e2k keeps its code.</summary>
public sealed record DesignKind(string Kind, string Name, string TablePrefix, string? E2kHeading = null, string? E2kKeyword = null);

/// <summary>
/// The program's own single-row forms the .e2k has no keyword for: a design's View/Revise Preferences (one table per
/// design code) and the Analyze menu's options (active degrees of freedom, SAPFire solver, mesh settings, hinges).
/// Their items are the program's own table fields, with the names the program shows (read from ETABS 23.2 and
/// SAP2000 26.3 on Ahmed's PC, field/table-keys). A value set in a form is a row of that table in
/// <see cref="StructuralModel.DefinitionTables"/>; an item left blank is the program's own default.
/// </summary>
public static class ProgramForms
{
    private static readonly Dictionary<string, Dictionary<string, List<FormField>>> Catalogs = new(StringComparer.Ordinal);

    /// <summary>The program's form tables (SAFE's are ETABS's, as SAFE 23 names its tables as ETABS does).</summary>
    public static IReadOnlyDictionary<string, List<FormField>> Catalog(string target) => Load(target == ProgramOptions.Sap2000 ? "forms-sap2000.txt" : "forms-etabs.txt");

    /// <summary>
    /// The design overwrite tables of each object (Design > ... > View/Revise Overwrites), with the program's items, by
    /// table, without the object's own key fields (ETABS's Story, Label, UniqueName; SAP2000's Frame).
    /// </summary>
    public static IReadOnlyDictionary<string, List<FormField>> OverwriteCatalog(string target) => Load(target == ProgramOptions.Sap2000 ? "overwrites-sap2000.txt" : "overwrites-etabs.txt");

    /// <summary>The overwrite table prefixes of a design (ETABS's concrete frames have one for beams, one for columns).</summary>
    private static string[] OverwritePrefixes(string target, string kind) => (target, kind) switch
    {
        (ProgramOptions.Sap2000, "steel") => ["Overwrites - Steel Design - "],
        (ProgramOptions.Sap2000, "concrete") => ["Overwrites - Concrete Design - "],
        (ProgramOptions.Sap2000, "aluminum") => ["Overwrites - Aluminum Design - "],
        (ProgramOptions.Sap2000, "cold_formed") => ["Overwrites - Cold Formed Design - "],
        (ProgramOptions.Sap2000, _) => [],
        (_, "steel") => ["Steel Frame Design Overwrites - "],
        (_, "concrete") => ["Concrete Beam Overwrites - ", "Concrete Column Overwrites - "],
        (_, "composite") => ["Composite Beam Design Overwrites - "],
        (_, "composite_column") => ["Composite Column Design Overwrites - "],
        (_, "joist") => ["Steel Joist Design Overwrites - "],
        (_, "shear_wall") => ["Shear Wall Pier Design Overwrites - ", "Shear Wall Spandrel Design Overwrites - "],
        _ => [],
    };

    /// <summary>
    /// The overwrite tables of a design for a code, as the program names them (the code written as the table has it:
    /// "IS 11384:2022" is "IS 11384_2022"). ETABS's slab design (finite element based) has one table for every code.
    /// </summary>
    public static List<string> OverwriteTables(string target, string kind, string code)
    {
        var cat = kind == "shear_wall" && target != ProgramOptions.Sap2000 ? WallOverwriteCatalog() : OverwriteCatalog(target);
        if (target != ProgramOptions.Sap2000 && kind == "slab") return cat.ContainsKey(SlabOverwrites) ? [SlabOverwrites] : [];
        static string Norm(string x) => new(x.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var found = new List<string>();
        foreach (var prefix in OverwritePrefixes(target, kind))
            if ((cat.Keys.FirstOrDefault(k => k.Equals(prefix + code, StringComparison.OrdinalIgnoreCase))
                ?? cat.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Norm(k[prefix.Length..]) == Norm(code))) is { } t)
                found.Add(t);
        return found;
    }

    /// <summary>
    /// ETABS's shear wall design overwrites (Design > Shear Wall Design > View/Revise Pier/Spandrel Overwrites): a row per
    /// story of each pier or spandrel label, the label first (definition rows by label, not an object's).
    /// </summary>
    public static IReadOnlyDictionary<string, List<FormField>> WallOverwriteCatalog() => Load("wall-overwrites-etabs.txt");

    public static bool IsWallOverwrite(string table) => WallOverwriteCatalog().ContainsKey(table);

    public const string SlabOverwrites = "Concrete Slab Design Overwrites - Finite Element Based";

    /// <summary>The overwrite tables for the codes the model's designs have now.</summary>
    public static HashSet<string> CurrentOverwriteTables(StructuralModel m) =>
        Designs(m.Target).SelectMany(d => OverwriteTables(m.Target, d.Kind, Code(m, d.Kind) is { Length: > 0 } c ? c : Codes(m.Target, d.Kind).FirstOrDefault() ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a design overwrite table of either program.</summary>
    public static bool IsOverwrite(string table) => OverwriteCatalog(ProgramOptions.Etabs).ContainsKey(table) || OverwriteCatalog(ProgramOptions.Sap2000).ContainsKey(table);

    /// <summary>True for SAP2000's own overwrite tables (keyed by Frame).</summary>
    public static bool IsSapOverwrite(string table) => OverwriteCatalog(ProgramOptions.Sap2000).ContainsKey(table);

    private static IReadOnlyDictionary<string, List<FormField>> Load(string file)
    {
        lock (Catalogs)
        {
            if (Catalogs.TryGetValue(file, out var have)) return have;
            var map = new Dictionary<string, List<FormField>>(StringComparer.OrdinalIgnoreCase);
            using var stream = typeof(ProgramForms).Assembly.GetManifestResourceStream(file) ?? throw new InvalidOperationException("The forms list " + file + " is missing.");
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var at = line.IndexOf(" :: ", StringComparison.Ordinal);
                if (at <= 0) continue;
                map[line[..at]] = line[(at + 4)..].Split('|').Select(f => f.Split('=', 2)).Select(p => new FormField(p[0], p.Length > 1 ? p[1] : p[0])).ToList();
            }
            return Catalogs[file] = map;
        }
    }

    /// <summary>The designs of each program's Design menu, in its order.</summary>
    public static IReadOnlyList<DesignKind> Designs(string target) => target switch
    {
        ProgramOptions.Sap2000 =>
        [
            new("steel", "Steel Frame Design", "Preferences - Steel Design - "),
            new("concrete", "Concrete Frame Design", "Preferences - Concrete Design - "),
            new("aluminum", "Aluminum Frame Design", "Preferences - Aluminum Design - "),
            new("cold_formed", "Cold-Formed Steel Frame Design", "Preferences - Cold Formed Design - "),
            new("shell", "Concrete Shell Design", "Preferences - Concrete Shell Design - "),
        ],
        ProgramOptions.Safe => [new("slab", "Concrete Slab Design", "Concrete Slab Design Preferences - ", SlabDefs.SlabPreferences, "CONCRETESLABPREFERENCE")],
        _ =>
        [
            new("steel", "Steel Frame Design", "Steel Frame Design Preferences - ", "STEEL DESIGN PREFERENCES", "STEELPREFERENCE"),
            new("concrete", "Concrete Frame Design", "Concrete Frame Design Preferences - ", "CONCRETE DESIGN PREFERENCES", "CONCRETEPREFERENCE"),
            new("composite", "Composite Beam Design", "Composite Beam Design Preferences - ", "COMPOSITE DESIGN PREFERENCES", "COMPOSITEPREFERENCE"),
            new("composite_column", "Composite Column Design", "Composite Column Design Preferences - ", "COMPOSITE COLUMN DESIGN PREFERENCES", "COMPOSITECOLUMNPREFERENCE"),
            new("joist", "Steel Joist Design", "Steel Joist Design Preferences - "),
            new("shear_wall", "Shear Wall Design", "Shear Wall Design Preferences - ", "WALL DESIGN PREFERENCES", "WALLPREFERENCE"),
            new("slab", "Concrete Slab Design", "Concrete Slab Design Preferences - ", SlabDefs.SlabPreferences, "CONCRETESLABPREFERENCE"),
        ],
    };

    /// <summary>The Analyze menu's option forms of each program, as (menu item, table).</summary>
    public static IReadOnlyList<(string Item, string Table)> AnalysisForms(string target) => target switch
    {
        ProgramOptions.Sap2000 => [("Set Analysis Options...", "Analysis Options"), ("Set Active Degrees of Freedom...", "Active Degrees of Freedom")],
        ProgramOptions.Safe => [("Automatic Mesh Settings for Floors...", "Analysis Options - Automatic Mesh Settings for Floors"), ("Cracking Analysis Options...", "Analysis Options - Cracking Analysis Options"), ("Advanced SAPFire Options...", "Analysis Options - SAPFire Options")],
        _ =>
        [
            ("Set Active Degrees of Freedom...", "Analysis Options - Active Degrees of Freedom"),
            ("Set Analysis Options: Nonlinear Hinges...", "Analysis Options - Analysis Model for Nonlinear Hinges"),
            ("Set Analysis Options: Design and Response Recovery...", "Analysis Options - Design and Response Recovery Options"),
            ("Automatic Mesh Settings for Floors...", "Analysis Options - Automatic Mesh Settings for Floors"),
            ("Automatic Rectangular Mesh Settings for Walls...", "Analysis Options - Automatic Rectangular Mesh Options for Walls"),
            ("Cracking Analysis Options...", "Analysis Options - Cracking Analysis Options"),
            ("Advanced SAPFire Options...", "Analysis Options - SAPFire Options"),
        ],
    };

    /// <summary>
    /// The Options menu's forms of each program (Ahmed 2026-11-27, every form): ETABS 23.2's Preferences
    /// (Dimensions/Tolerances, Graphics), Colors (Display, Output) and Miscellaneous Options; SAP2000 26.3's
    /// Preferences > Dimensions/Tolerances and Colors. SAFE has ETABS's. Group is the submenu the item is in.
    /// </summary>
    public static IReadOnlyList<(string Group, string Item, string Table)> OptionForms(string target) => target switch
    {
        ProgramOptions.Sap2000 =>
        [
            ("Preferences", "Dimensions/Tolerances...", "Preferences - Dimensional"),
            ("Colors", "Display...", "Options - Colors - Display"),
            ("Colors", "Output...", "Options - Colors - Output"),
        ],
        _ =>
        [
            ("Preferences", "Dimensions/Tolerances...", "Preferences - Tolerance"),
            ("Preferences", "Graphics...", "Preferences - Graphics"),
            ("Colors", "Display...", "Options - Colors - Display"),
            ("Colors", "Output...", "Options - Colors - Output"),
            ("", "Miscellaneous Options...", "Miscellaneous Options"),
        ],
    };

    /// <summary>True for an Options menu form that is the program's own setting (colours, start page...), not the model's:
    /// ETABS keeps those in its settings, so they stay with the model in CSI ProMax and are not put into ETABS's tables.</summary>
    public static bool IsProgramSetting(string table) =>
        table.StartsWith("Options - ", StringComparison.OrdinalIgnoreCase) || table.Equals("Miscellaneous Options", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a table of these forms (one row, no name to key it by).</summary>
    public static bool IsForm(string table) =>
        table.Contains("Design Preferences - ", StringComparison.OrdinalIgnoreCase) || table.StartsWith("Preferences - ", StringComparison.OrdinalIgnoreCase)
        || table.StartsWith("Analysis Options", StringComparison.OrdinalIgnoreCase) || table.Equals("Active Degrees of Freedom", StringComparison.OrdinalIgnoreCase)
        || IsProgramSetting(table);

    /// <summary>True when the program's own file or tables take this form's table (ETABS's tables for ETABS and SAFE).</summary>
    public static bool HasForm(string target, string table) => IsForm(table) && Catalog(target).ContainsKey(table);

    public static DesignKind Design(string target, string kind) =>
        Designs(target).FirstOrDefault(d => d.Kind == kind) ?? throw new ArgumentException($"There is no {kind} design here. The designs: {string.Join(", ", Designs(target).Select(d => d.Kind))}.");

    /// <summary>The codes a design has, as the program names them (its preference tables, one per code).</summary>
    public static List<string> Codes(string target, string kind)
    {
        var d = Design(target, kind);
        return Catalog(target).Keys.Where(t => t.StartsWith(d.TablePrefix, StringComparison.OrdinalIgnoreCase)).Select(t => t[d.TablePrefix.Length..]).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The design codes chosen in the Modeler, by design kind (kept in the .cpm; for ETABS and SAFE also in the file).</summary>
    public const string CodesKept = "model|DESIGN CODES";

    /// <summary>The code of a design: chosen in the Modeler, else what the .e2k says, else "".</summary>
    public static string Code(StructuralModel m, string kind)
    {
        if (m.Kept?.GetValueOrDefault(CodesKept)?.Find(r => r.GetValueOrDefault("Kind") == kind)?.GetValueOrDefault("Code") is { Length: > 0 } kept) return kept;
        var d = Designs(m.Target).FirstOrDefault(x => x.Kind == kind);
        if (d?.E2kHeading is null) return "";
        return (m.Passthrough.Find(x => x.Heading == d.E2kHeading)?.Lines ?? []).Select(Tokenize).Where(t => t.Count > 0 && t[0] == d.E2kKeyword)
            .Select(t => Value(t, "CODE")).FirstOrDefault(c => c is not null) ?? "";
    }

    /// <summary>Sets a design's code, refused unless the program has it.</summary>
    public static void SetCode(StructuralModel m, string kind, string code)
    {
        var d = Design(m.Target, kind);
        var codes = Codes(m.Target, kind);
        var known = codes.FirstOrDefault(c => c.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{d.Name} has no code '{code}'. Its codes: {string.Join(", ", codes)}.");
        m.Kept ??= new(StringComparer.OrdinalIgnoreCase);
        var rows = m.Kept.TryGetValue(CodesKept, out var have) ? have : m.Kept[CodesKept] = [];
        rows.RemoveAll(r => r.GetValueOrDefault("Kind") == kind);
        rows.Add(new Dictionary<string, string> { ["Kind"] = kind, ["Code"] = known });
        if (d.E2kHeading is null) return;
        // The .e2k keeps the code as CODE on the design's preference lines: changed there, or a line added for it.
        var s = m.Passthrough.Find(x => x.Heading == d.E2kHeading);
        if (s is null) { s = new RawSection { Heading = d.E2kHeading, After = E2kSections.IsKnown("LOAD COMBINATIONS") ? "LOAD COMBINATIONS" : "" }; m.Passthrough.Add(s); }
        var at = s.Lines.FindIndex(l => Tokenize(l) is { Count: > 0 } t && t[0] == d.E2kKeyword && t.Contains("CODE"));
        if (at < 0) { s.Lines.Insert(0, Line([d.E2kKeyword!, "CODE", Quote(known)])); return; }
        var tokens = Tokenize(s.Lines[at]);
        var i = tokens.IndexOf("CODE");
        if (i + 1 < tokens.Count) tokens[i + 1] = Quote(known); else tokens.Add(Quote(known));
        s.Lines[at] = "  " + string.Join("  ", tokens);
    }

    /// <summary>A form's items with the values set in the Modeler ("" = the program's default).</summary>
    public static List<(FormField Field, string Value)> Values(StructuralModel m, string table)
    {
        var fields = Fields(m.Target, table);
        var row = m.DefinitionTables?.GetValueOrDefault(table)?.FirstOrDefault() ?? [];
        return fields.Select(f => (f, row.GetValueOrDefault(f.Key) ?? "")).ToList();
    }

    public static List<FormField> Fields(string target, string table) =>
        IsForm(table) && Catalog(target).TryGetValue(table, out var fields) ? fields
            : throw new ArgumentException($"{(target == ProgramOptions.Sap2000 ? "SAP2000" : target == ProgramOptions.Safe ? "SAFE" : "ETABS")} has no form '{table}'.");

    /// <summary>
    /// Sets a form's values: each item by its field or by the name the program shows. A blank value goes back to the
    /// program's default. Checked whole before anything changes.
    /// </summary>
    public static void Set(StructuralModel m, string table, IReadOnlyDictionary<string, string> values)
    {
        var fields = Fields(m.Target, table);
        var full = Catalog(m.Target).Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));
        var row = new Dictionary<string, string>(m.DefinitionTables?.GetValueOrDefault(full)?.FirstOrDefault() ?? [], StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            var f = fields.Find(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? fields.Find(x => x.Label.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"'{full}' has no item '{name}'. Its items: {string.Join(", ", fields.Select(x => x.Label))}.");
            if (value.Contains('"') || value.Contains('\n')) throw new ArgumentException($"The value of {f.Label} cannot hold quotes or line breaks.");
            if (value.Trim().Length == 0) row.Remove(f.Key); else row[f.Key] = value.Trim();
        }
        m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
        if (row.Count == 0) m.DefinitionTables.Remove(full);
        else m.DefinitionTables[full] = [row];
    }
}
