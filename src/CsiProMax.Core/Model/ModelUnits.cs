using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsiProMax.Model;

/// <summary>
/// A model's numbers in another set of units (Ahmed 2026-11-27: a units change shows every form, table and result in the
/// new units, as ETABS, SAP2000 and SAFE do, and the model is saved in its own units). Each value is scaled by its
/// kind: a length, a force, a stress, a unit weight, a load per length or per area, an area, a moment of inertia, a
/// temperature change. A number whose kind is not known here is left as it is, so converting there and back always
/// gives the model that was there (to 15 significant digits for the converted ones).
/// </summary>
public static partial class ModelUnits
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A kind of value: the powers of force, length and temperature in it ("F/L2" is force 1, length -2).</summary>
    public readonly record struct Dim(int F, int L, int T = 0)
    {
        public static readonly Dim None = new(0, 0), Len = new(0, 1), Area = new(0, 2), Vol = new(0, 3), Inertia = new(0, 4), Force = new(1, 0),
            PerLen = new(1, -1), Stress = new(1, -2), UnitWeight = new(1, -3), Moment = new(1, 1), MassPerVol = new(1, -4), MassPerLen = new(1, -2),
            MassPerArea = new(1, -3), Mass = new(1, -1), Temp = new(0, 0, 1), PerTemp = new(0, 0, -1), PerLength = new(0, -1);
        public bool IsNone => F == 0 && L == 0 && T == 0;
    }

    /// <summary>A force unit in kN.</summary>
    public static double? ForceKn(string force) => force.Trim().ToLowerInvariant() switch
    {
        "kn" => 1, "n" => 0.001, "kip" => 4.4482216152605, "lb" => 0.0044482216152605, "kgf" => 0.00980665, "tonf" or "ton" => 9.80665, _ => null,
    };

    /// <summary>A length unit in m.</summary>
    public static double? LengthM(string length) => length.Trim().ToLowerInvariant() switch
    {
        "m" => 1, "mm" => 0.001, "cm" => 0.01, "in" => 0.0254, "ft" => 0.3048, "micron" => 1e-6, _ => null,
    };

    /// <summary>A temperature change unit in degrees C.</summary>
    public static double TempC(string temperature) => temperature.Trim().StartsWith('F') || temperature.Trim().StartsWith('f') ? 5.0 / 9.0 : 1;

    /// <summary>Whether CSI ProMax knows both units of a set.</summary>
    public static bool Known(Units u) => ForceKn(u.Force) is not null && LengthM(u.Length) is not null;

    /// <summary>What one of a kind in units <paramref name="from"/> is in units <paramref name="to"/>.</summary>
    public static double Factor(Dim d, Units from, Units to) =>
        Math.Pow(ForceKn(from.Force)!.Value / ForceKn(to.Force)!.Value, d.F) *
        Math.Pow(LengthM(from.Length)!.Value / LengthM(to.Length)!.Value, d.L) *
        Math.Pow(TempC(from.Temperature) / TempC(to.Temperature), d.T);

    /// <summary>The same units (case aside).</summary>
    public static bool Same(Units a, Units b) =>
        a.Force.Equals(b.Force, StringComparison.OrdinalIgnoreCase) && a.Length.Equals(b.Length, StringComparison.OrdinalIgnoreCase) &&
        TempC(a.Temperature) == TempC(b.Temperature);

    /// <summary>A value rounded to 15 significant digits, so converting there and back gives the number typed.</summary>
    public static double Round(double v)
    {
        if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
        return double.Parse(v.ToString("G15", Inv), Inv);
    }

    // A converted number as text: rounded to 15 digits when that leaves a short number (a value coming back to its own
    // units, 0.006349999999999999 m is 0.00635), else every digit, so that going on to other units and back loses
    // nothing (0.00635 m is 0.020833333333333332 ft, and 0.0208333333333333 would come back as 0.00634999999999999).
    private static string Text(double v)
    {
        if (v == 0) return "0";
        var short15 = v.ToString("G15", Inv);
        var digits = short15.Split('E')[0].Count(char.IsDigit) - short15.Split('E')[0].TakeWhile(c => c is '0' or '.' or '-').Count(char.IsDigit);
        return digits <= 12 ? short15 : v.ToString("R", Inv);
    }

    // The .e2k keywords by kind, as ETABS writes them (materials, sections, slabs, decks, tendons, rebar, design
    // preferences, analysis options, assignments).
    private static readonly Dictionary<string, Dim> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // lengths
        ["D"] = Dim.Len, ["B"] = Dim.Len, ["TF"] = Dim.Len, ["TW"] = Dim.Len, ["TFB"] = Dim.Len, ["TWB"] = Dim.Len, ["T3"] = Dim.Len, ["T2"] = Dim.Len,
        ["DIS"] = Dim.Len, ["BBOTTOM"] = Dim.Len, ["TFBOTTOM"] = Dim.Len, ["RADIUS"] = Dim.Len, ["FILLETRADIUS"] = Dim.Len, ["LIPDEPTH"] = Dim.Len,
        ["R33"] = Dim.Len, ["R22"] = Dim.Len, ["COVER"] = Dim.Len, ["COVERTOP"] = Dim.Len, ["COVERBOTTOM"] = Dim.Len, ["COVERBOT"] = Dim.Len,
        ["CONFINEBARSPACING"] = Dim.Len, ["SLABTHICKNESS"] = Dim.Len, ["WALLTHICKNESS"] = Dim.Len, ["DECKSLABDEPTH"] = Dim.Len, ["DECKRIBDEPTH"] = Dim.Len,
        ["DECKRIBWIDTHTOP"] = Dim.Len, ["DECKRIBWIDTHBOTTOM"] = Dim.Len, ["DECKRIBSPACING"] = Dim.Len, ["DECKSHEARTHICKNESS"] = Dim.Len,
        ["SLABRIBWIDTHTOP"] = Dim.Len, ["SLABRIBWIDTHBOTTOM"] = Dim.Len, ["SLABRIBSPACING"] = Dim.Len, ["SLABRIBSPACINGDIR1"] = Dim.Len, ["SLABRIBSPACINGDIR2"] = Dim.Len, ["OVERALLDEPTH"] = Dim.Len,
        ["WALLLENGTH"] = Dim.Len, ["TOTALTHICK"] = Dim.Len, ["PLATETHICK"] = Dim.Len, ["FLANGEPLATETHICK"] = Dim.Len, ["TIEBARDIAMETER"] = Dim.Len, ["TIEBARSPACING"] = Dim.Len,
        ["SHEARSTUDDIAM"] = Dim.Len, ["SHEARSTUDHEIGHT"] = Dim.Len, ["MEMBRANETHICKNESS"] = Dim.Len, ["BENDINGTHICKNESS"] = Dim.Len, ["DIA"] = Dim.Len,
        ["ANCHORSLIP"] = Dim.Len, ["LENGTHOFFI"] = Dim.Len, ["LENGTHOFFJ"] = Dim.Len, ["MAXSTASPC"] = Dim.Len, ["FLOORMESHMAXSIZE"] = Dim.Len,
        ["WALLMESHMAXSIZE"] = Dim.Len, ["MERGETOL"] = Dim.Len, ["MINLONGSPACING"] = Dim.Len, ["MAXLONGSPACING"] = Dim.Len, ["MINTRANSSPACING"] = Dim.Len,
        ["MINDEPTHFORCAMBER"] = Dim.Len, ["MINWEBTHICKFORCAMBER"] = Dim.Len, ["MINSPANFORCAMBER"] = Dim.Len, ["CAMBERIGNORE"] = Dim.Len,
        ["CAMBERABSMAX"] = Dim.Len, ["CAMBERINTERVAL"] = Dim.Len, ["PTCGSTOP"] = Dim.Len, ["PTCGSBOTEXT"] = Dim.Len, ["PTCGSBOTINT"] = Dim.Len,
        ["EFFDEPTH"] = Dim.Len, ["REINFSPCG"] = Dim.Len, ["REINFDIA"] = Dim.Len, ["WALEFT"] = Dim.Len, ["WARIGHT"] = Dim.Len, ["WBLEFT"] = Dim.Len,
        ["WBRIGHT"] = Dim.Len, ["DIST"] = Dim.Len, ["DSTART"] = Dim.Len, ["DEND"] = Dim.Len, ["ABSDIST"] = Dim.Len, ["OFFSETX"] = Dim.Len,
        ["OFFSETY"] = Dim.Len, ["OFFSETZ"] = Dim.Len, ["DEPTH"] = Dim.Len, ["WIDTH"] = Dim.Len, ["THICKNESS"] = Dim.Len,
        // areas and section properties
        ["AREA"] = Dim.Area, ["AS2"] = Dim.Area, ["AS3"] = Dim.Area, ["LONGBARAREA"] = Dim.Area, ["CONFINEBARAREA"] = Dim.Area, ["STRANDAREA"] = Dim.Area,
        ["ATI"] = Dim.Area, ["ABI"] = Dim.Area, ["ATJ"] = Dim.Area, ["ABJ"] = Dim.Area, ["I33"] = Dim.Inertia, ["I22"] = Dim.Inertia, ["I23"] = Dim.Inertia,
        ["J"] = Dim.Inertia, ["S33"] = Dim.Vol, ["S22"] = Dim.Vol, ["Z33"] = Dim.Vol, ["Z22"] = Dim.Vol,
        // stresses and moduli
        ["E"] = Dim.Stress, ["G"] = Dim.Stress, ["E1"] = Dim.Stress, ["E2"] = Dim.Stress, ["E3"] = Dim.Stress, ["G12"] = Dim.Stress, ["G13"] = Dim.Stress,
        ["G23"] = Dim.Stress, ["FC"] = Dim.Stress, ["FY"] = Dim.Stress, ["FU"] = Dim.Stress, ["FYE"] = Dim.Stress, ["FUE"] = Dim.Stress,
        ["SHEARSTUDFU"] = Dim.Stress, ["JACKSTRESS"] = Dim.Stress, ["REINFFY"] = Dim.Stress, ["LOSSFIXEDST"] = Dim.Stress, ["LOSSFIXEDLT"] = Dim.Stress,
        ["ELASTICSHORT"] = Dim.Stress, ["CREEPSTRESS"] = Dim.Stress, ["SHRINKSTRESS"] = Dim.Stress, ["STEELRELAX"] = Dim.Stress,
        // weights, masses, temperature
        ["WEIGHTPERVOLUME"] = Dim.UnitWeight, ["MASSPERVOLUME"] = Dim.MassPerVol, ["DECKUNITWEIGHT"] = Dim.Stress, ["WOBBLECOEFF"] = Dim.PerLength,
        ["A"] = Dim.PerTemp, ["A1"] = Dim.PerTemp, ["A2"] = Dim.PerTemp, ["A3"] = Dim.PerTemp,
    };

    // The keywords whose kind depends on the line: a frame load's FVAL is per length, an area load's per area.
    private static readonly HashSet<string> NotInDefinitions = new(StringComparer.OrdinalIgnoreCase) { "DIST", "DSTART", "DEND", "ABSDIST", "DEPTH", "WIDTH", "THICKNESS" };

    // The program's table fields by kind (definition, assignment and kept tables), by their names in ETABS 23, SAP2000
    // 26 and SAFE. Only names that mean one kind of value in every table are here.
    private static readonly Dictionary<string, Dim> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Depth"] = Dim.Len, ["Width"] = Dim.Len, ["Thickness"] = Dim.Len, ["t1"] = Dim.Len, ["t2"] = Dim.Len, ["t3"] = Dim.Len, ["tf"] = Dim.Len, ["tw"] = Dim.Len,
        ["tfb"] = Dim.Len, ["twb"] = Dim.Len, ["t2b"] = Dim.Len, ["Ordinate"] = Dim.Len, ["Elevation"] = Dim.Len, ["Height"] = Dim.Len, ["BSElev"] = Dim.Len,
        ["XOrdinate"] = Dim.Len, ["YOrdinate"] = Dim.Len, ["ZOrdinate"] = Dim.Len, ["GlobalX"] = Dim.Len, ["GlobalY"] = Dim.Len, ["GlobalZ"] = Dim.Len,
        ["LocX"] = Dim.Len, ["LocY"] = Dim.Len, ["LocZ"] = Dim.Len, ["Offset"] = Dim.Len, ["OffsetI"] = Dim.Len, ["OffsetJ"] = Dim.Len,
        ["Cover"] = Dim.Len, ["CoverTop"] = Dim.Len, ["CoverBot"] = Dim.Len, ["CoverBottom"] = Dim.Len, ["ClearCover"] = Dim.Len, ["BarSpacing"] = Dim.Len,
        ["TieSpacing"] = Dim.Len, ["SlabDepth"] = Dim.Len, ["RibDepth"] = Dim.Len, ["RibWidthTop"] = Dim.Len, ["RibWidthBot"] = Dim.Len,
        ["RibSpacing"] = Dim.Len, ["RibSpacing1"] = Dim.Len, ["RibSpacing2"] = Dim.Len, ["DeckShrThk"] = Dim.Len, ["StudDiam"] = Dim.Len,
        ["StudHeight"] = Dim.Len, ["Radius"] = Dim.Len, ["Diameter"] = Dim.Len, ["BarDiam"] = Dim.Len, ["Length"] = Dim.Len, ["AbsDist"] = Dim.Len,
        ["DefLength"] = Dim.Len, ["MaxMeshSize"] = Dim.Len, ["MaxSize"] = Dim.Len, ["Tolerance"] = Dim.Len, ["Spacing"] = Dim.Len, ["Width1"] = Dim.Len,
        ["Width2"] = Dim.Len, ["WidthLeft"] = Dim.Len, ["WidthRight"] = Dim.Len, ["Offset1"] = Dim.Len, ["Offset2"] = Dim.Len, ["Offset3"] = Dim.Len,
        ["EndOffI"] = Dim.Len, ["EndOffJ"] = Dim.Len, ["Area"] = Dim.Area, ["AS2"] = Dim.Area, ["AS3"] = Dim.Area, ["BarArea"] = Dim.Area,
        ["DefArea"] = Dim.Area, ["I33"] = Dim.Inertia, ["I22"] = Dim.Inertia, ["I23"] = Dim.Inertia, ["J"] = Dim.Inertia, ["TorsConst"] = Dim.Inertia,
        ["S33"] = Dim.Vol, ["S22"] = Dim.Vol, ["Z33"] = Dim.Vol, ["Z22"] = Dim.Vol, ["Fy"] = Dim.Stress, ["Fu"] = Dim.Stress, ["Fye"] = Dim.Stress,
        ["Fue"] = Dim.Stress, ["Fc"] = Dim.Stress, ["Fce"] = Dim.Stress, ["E"] = Dim.Stress, ["E1"] = Dim.Stress, ["G12"] = Dim.Stress, ["StudFu"] = Dim.Stress,
        ["UnitWeight"] = Dim.UnitWeight, ["UnitWt"] = Dim.UnitWeight, ["WeightPerVolume"] = Dim.UnitWeight, ["UnitMass"] = Dim.MassPerVol,
        ["DeckUnitWt"] = Dim.Stress, ["FOverL"] = Dim.PerLen, ["UnifLoad"] = Dim.Stress, ["Force"] = Dim.Force, ["Moment"] = Dim.Moment,
        ["Weight"] = Dim.Force, ["Mass"] = Dim.Mass, ["A1"] = Dim.PerTemp,
    };

    /// <summary>The kind of a program table field, or none.</summary>
    public static Dim FieldDim(string field) => Fields.TryGetValue(field, out var d) ? d : Dim.None;

    /// <summary>The kind of an .e2k keyword in a definition, or none.</summary>
    public static Dim KeywordDim(string key) => Keywords.TryGetValue(key, out var d) ? d : Dim.None;

    /// <summary>A copy of the model with its numbers in units <paramref name="to"/>. The model itself is not changed.</summary>
    public static StructuralModel Convert(StructuralModel model, Units to)
    {
        var m = ModelFile.FromJson(ModelFile.ToJson(model));
        if (!Known(model.Units) || !Known(to)) throw new ArgumentException($"CSI ProMax does not know the units {model.Units.Force}, {model.Units.Length} or {to.Force}, {to.Length}.");
        var from = model.Units;
        double K(Dim d) => Factor(d, from, to);
        double S(double v, Dim d) => d.IsNone ? v : double.Parse(Text(v * K(d)), Inv);

        foreach (var s in m.Stories)
        {
            s.Height = S(s.Height, Dim.Len); s.Elevation = S(s.Elevation, Dim.Len);
            Tokens(s.Extra, k => k.Equals("SPLICEHEIGHT", StringComparison.OrdinalIgnoreCase) ? Dim.Len : Dim.None, K);
        }
        // A cylindrical system's T lines are at angles, which no units change touches.
        foreach (var g in m.Grids) g.Coordinate = g.Direction.Equals("T", StringComparison.OrdinalIgnoreCase) ? g.Coordinate : S(g.Coordinate, Dim.Len);
        Tokens(m.GridSystemExtra, k => k.Equals("BUBBLESIZE", StringComparison.OrdinalIgnoreCase) ? Dim.Len : Dim.None, K);
        foreach (var system in m.MoreGridSystems)
        {
            Tokens(system.Extra, k => k.Equals("BUBBLESIZE", StringComparison.OrdinalIgnoreCase) ? Dim.Len : Dim.None, K);
            // ORIGIN takes two lengths (x and y of the system's origin).
            var at = system.Extra.FindIndex(t => t.Equals("ORIGIN", StringComparison.OrdinalIgnoreCase));
            for (var i = at + 1; at >= 0 && i <= at + 2 && i < system.Extra.Count; i++)
                if (double.TryParse(system.Extra[i], NumberStyles.Float, Inv, out var v)) system.Extra[i] = Text(v * K(Dim.Len));
        }
        foreach (var p in m.Points) { p.X = S(p.X, Dim.Len); p.Y = S(p.Y, Dim.Len); p.Below = S(p.Below, Dim.Len); }
        foreach (var d in m.Materials.Cast<Definition>().Concat(m.FrameSections).Concat(m.ShellSections))
        {
            foreach (var line in d.Lines) Tokens(line, k => NotInDefinitions.Contains(k) ? Dim.None : KeywordDim(k), K);
            if (d is FrameSection fs && FrameTableShapes.SdSource(fs) is { } sd)
            {
                Tokens(sd, k => NotInDefinitions.Contains(k) ? Dim.None : KeywordDim(k), K);
                fs.Tables![FrameTableShapes.SdSourceTable][0]["Line"] = string.Join(" ", sd);
            }
            Tables(d.Tables, K);
        }
        foreach (var a in m.PointAssigns.Cast<Assign>().Concat(m.LineAssigns).Concat(m.AreaAssigns))
        {
            foreach (var line in a.Extra) Tokens(line, k => k.Equals("LENGTHOFFI", StringComparison.OrdinalIgnoreCase) || k.Equals("LENGTHOFFJ", StringComparison.OrdinalIgnoreCase) || k.Equals("MAXSTASPC", StringComparison.OrdinalIgnoreCase) ? Dim.Len : Dim.None, K);
            Tables(a.Tables, K);
        }
        foreach (var l in m.PointLoads) Tokens(l.Tokens, k => PointLoadDim(l.Tokens, k), K);
        foreach (var l in m.LineLoads) Tokens(l.Tokens, k => LineLoadDim(l.Tokens, k), K);
        foreach (var l in m.AreaLoads) Tokens(l.Tokens, k => AreaLoadDim(l.Tokens, k), K);
        foreach (var c in m.LoadCases)
            foreach (var line in c.Lines)
                // A response spectrum or time history case scales an acceleration: its SF is a length per second squared.
                if (line.Any(t => E2k.E2kText.Unquote(t).Equals("ACC", StringComparison.OrdinalIgnoreCase)))
                    Tokens(line, k => k.Equals("SF", StringComparison.OrdinalIgnoreCase) ? Dim.Len : Dim.None, K);
        foreach (var p in m.LoadPatterns)
            if (p.Auto is { } auto)
                auto.Forces = auto.Forces.Select(f => new Lateral.StoryForce(f.Story, S(f.Elevation, Dim.Len), S(f.Weight, Dim.Force), S(f.Fx, Dim.Force), S(f.Fy, Dim.Force), S(f.Mz, Dim.Moment))).ToList();
        foreach (var r in m.Passthrough) r.Lines = r.Lines.Select(l => RawLine(l, r.Heading, K)).ToList();
        foreach (var (heading, lines) in m.UnparsedLines.ToList()) m.UnparsedLines[heading] = lines.Select(l => RawLine(l, heading, K)).ToList();
        m.Controls = m.Controls.Select(l => RawLine(l, "CONTROLS", K)).ToList();
        Tables(m.DefinitionTables, K);
        Tables(m.Kept, K);
        if (m.Carried is { } carried) Tables(carried.Rows, K);
        if (m.Extras is { } x)
        {
            foreach (var c in x.Constraints) c.Tolerance = S(c.Tolerance, Dim.Len);
            foreach (var lane in x.Lanes) { lane.Offset = S(lane.Offset, Dim.Len); lane.Width = S(lane.Width, Dim.Len); }
            foreach (var v in x.Vehicles)
            {
                v.Uniform = S(v.Uniform, Dim.PerLen);
                foreach (var axle in v.Axles) { axle.Load = S(axle.Load, Dim.Force); axle.Spacing = S(axle.Spacing, Dim.Len); }
            }
            foreach (var s in x.Strips)
            {
                s.WidthLeft = S(s.WidthLeft, Dim.Len); s.WidthRight = S(s.WidthRight, Dim.Len);
                s.Points = s.Points.Select(p => p.Select(v => S(v, Dim.Len)).ToArray()).ToList();
            }
        }
        m.Units = new Units { Force = to.Force, Length = to.Length, Temperature = to.Temperature };
        return m;
    }

    private static Dim PointLoadDim(List<string> tokens, string key)
    {
        var type = (E2k.E2kText.Value(tokens, "TYPE") ?? "FORCE").ToUpperInvariant();
        return key.ToUpperInvariant() switch
        {
            "FX" or "FY" or "FZ" => Dim.Force,
            "MX" or "MY" or "MZ" => Dim.Moment,
            "UX" or "UY" or "UZ" when type.StartsWith("DISP", StringComparison.Ordinal) => Dim.Len,
            _ => Dim.None,
        };
    }

    private static Dim LineLoadDim(List<string> tokens, string key)
    {
        var type = (E2k.E2kText.Value(tokens, "TYPE") ?? "").ToUpperInvariant();
        return key.ToUpperInvariant() switch
        {
            "FVAL" or "FSTART" or "FEND" => type switch
            {
                "UNIFF" or "TRAPF" => Dim.PerLen, "UNIFM" or "TRAPM" => Dim.Force, "POINTF" => Dim.Force, "POINTM" => Dim.Moment, "TEMP" or "TEMPERATURE" => Dim.Temp, _ => Dim.None,
            },
            "T" when type.StartsWith("TEMP", StringComparison.Ordinal) => Dim.Temp,
            "DIST" or "DSTART" or "DEND" or "ABSDIST" => Dim.Len,
            _ => Dim.None,
        };
    }

    private static Dim AreaLoadDim(List<string> tokens, string key)
    {
        var type = (E2k.E2kText.Value(tokens, "TYPE") ?? "").ToUpperInvariant();
        return key.ToUpperInvariant() switch
        {
            "FVAL" => type switch { "UNIFF" or "UNIFLOADSET" => Dim.Stress, "TEMP" or "TEMPERATURE" => Dim.Temp, _ => type.StartsWith("UNIF", StringComparison.Ordinal) ? Dim.Stress : Dim.None },
            "T" when type.StartsWith("TEMP", StringComparison.Ordinal) => Dim.Temp,
            _ => Dim.None,
        };
    }

    // Each keyword's number after it, in place.
    private static void Tokens(List<string> tokens, Func<string, Dim> dimOf, Func<Dim, double> k)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (E2k.E2kText.IsQuoted(tokens[i])) continue;
            var d = dimOf(tokens[i]);
            if (d.IsNone || E2k.E2kText.IsQuoted(tokens[i + 1]) || !double.TryParse(tokens[i + 1], NumberStyles.Float, Inv, out var v)) continue;
            tokens[i + 1] = Text(v * k(d));
            i++;
        }
    }

    // A line of a section kept word for word: the numbers after the keywords this file knows, in place.
    private static string RawLine(string line, string heading, Func<Dim, double> k)
    {
        // The rebar sizes' names are numbers too ("REBARDEFINITION "12" AREA ..."): only a keyword's own number changes.
        return KeyNumber().Replace(line, mt =>
        {
            // One-letter keywords (D, B, E, A, J) mean a section's or material's value only on its own line, read above.
            var d = mt.Groups[2].Value.Length < 2 ? Dim.None : KeywordDim(mt.Groups[2].Value);
            if (d.IsNone || !double.TryParse(mt.Groups[4].Value, NumberStyles.Float, Inv, out var v)) return mt.Value;
            return mt.Groups[1].Value + mt.Groups[2].Value + mt.Groups[3].Value + Text(v * k(d));
        });
    }

    [GeneratedRegex(@"(^|\s)([A-Z][A-Z0-9%]*)(\s+)(-?(?:\d+\.?\d*|\.\d+)(?:[Ee][+-]?\d+)?)(?=\s|$)")]
    private static partial Regex KeyNumber();

    private static void Tables(Dictionary<string, List<Dictionary<string, string>>>? tables, Func<Dim, double> k)
    {
        if (tables is null) return;
        foreach (var rows in tables.Values)
            foreach (var row in rows)
                foreach (var key in row.Keys.ToList())
                {
                    var d = FrameTableShapes.FieldDim(key, row) ?? FieldDim(key);
                    if (d.IsNone || !double.TryParse(row[key], NumberStyles.Float, Inv, out var v)) continue;
                    row[key] = Text(v * k(d));
                }
    }
}
