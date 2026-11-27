namespace CsiProMax.Lateral;

/// <summary>How a code parameter is entered on the form.</summary>
public enum ParamKind { Number, Choice, Text, Story }

/// <summary>One input of a code, the way the CSI programs' Modify Lateral Load form asks for it. Default "" means
/// optional: left blank, the code's own value is used (Fa from the site class, the period from the height).</summary>
public sealed record CodeParam(string Key, string Label, string Default, ParamKind Kind = ParamKind.Number, string[]? Choices = null, string Help = "");

/// <summary>A seismic or wind code the Modeler computes story forces for. <see cref="Family"/> is the method behind it
/// (several editions share one), <see cref="Programs"/> the CSI programs that list it.</summary>
public sealed record LateralCode(string Name, string Kind, string Family, IReadOnlyList<CodeParam> Params, string About, string Programs = "ETABS, SAP2000");

/// <summary>
/// Every seismic and wind method the Modeler gives: the codes the CSI programs list in their Define > Load Patterns
/// auto lateral menu, grouped by the method each follows, plus user coefficients, user loads, a user spectrum and a user
/// pressure profile, through which any other code's numbers can be given. Each computes forces at the stories.
/// </summary>
public static class LateralCodes
{
    public const string Seismic = "Seismic";
    public const string Wind = "Wind";

    private static readonly string[] Dirs = ["X", "X+Ecc", "X-Ecc", "Y", "Y+Ecc", "Y-Ecc"];
    private static readonly string[] Systems = ["Concrete moment frame", "Steel moment frame", "Steel braced frame (eccentric)", "All other"];

    private static IEnumerable<CodeParam> Range() =>
    [
        new("TopStory", "Top story", "", ParamKind.Story, Help: "Blank: the top story."),
        new("BottomStory", "Bottom story", "", ParamKind.Story, Help: "Blank: the base. Forces go on the stories above it."),
    ];

    /// <param name="ecc">The code's accidental eccentricity: 0.05 in most codes, 0.10 in NBCC (4.1.8.11).</param>
    private static IEnumerable<CodeParam> Direction(string ecc = "0.05") =>
    [
        new("Dir", "Direction", "X", ParamKind.Choice, Dirs, "X+Ecc and X-Ecc move the force by the eccentricity across the building."),
        new("Ecc", "Eccentricity ratio", ecc, Help: "Of the plan size across the force."),
    ];

    private static IEnumerable<CodeParam> Period(string ct, string x) =>
    [
        new("Period", "Period", "Approximate", ParamKind.Choice, ["Approximate", "User"], "Approximate: Ct h^x. The Modeler has no analysis, so a program-calculated period is given as User."),
        new("System", "Structural system (for Ct)", "Concrete moment frame", ParamKind.Choice, Systems),
        new("Ct", "Ct (m)", "", Help: $"Blank: from the system (a concrete moment frame: {ct})."),
        new("x", "x", "", Help: $"Blank: from the system (a concrete moment frame: {x})."),
        new("UserT", "User period T (s)", ""),
    ];

    private static List<CodeParam> P(params IEnumerable<CodeParam>[] parts) => parts.SelectMany(p => p).ToList();

    private static IEnumerable<CodeParam> Asce(bool cu) =>
    [
        new("R", "Response modification R", "8"), new("Omega", "Overstrength Ω0", "3"), new("Cd", "Deflection amplification Cd", "5.5"),
        new("I", "Importance factor Ie", "1"), new("Ss", "Ss (g)", "1.5"), new("S1", "S1 (g)", "0.6"), new("TL", "Long period TL (s)", "8"),
        new("SiteClass", "Site class", "D", ParamKind.Choice, ["A", "B", "C", "D", "E"]),
        new("Fa", "Fa", "", Help: "Blank: from the site class table of the code's edition."), new("Fv", "Fv", "", Help: "Blank: from the site class table of the code's edition."),
        new("SDS", "SDS (g)", "", Help: "Blank: 2/3 Fa Ss."), new("SD1", "SD1 (g)", "", Help: "Blank: 2/3 Fv S1."),
        .. cu ? new[] { new CodeParam("CuCap", "Cap T at Cu Ta", "Yes", ParamKind.Choice, ["Yes", "No"]) } : [],
    ];

    /// <summary>ASCE 7-22 (11.4): no Fa and Fv tables. SDS and SD1 are the USGS geodatabase's for the site class (with BC,
    /// CD and DE); S1 stays for the minimum Cs of 12.8-6.</summary>
    private static IEnumerable<CodeParam> Asce722() =>
    [
        new("R", "Response modification R", "8"), new("Omega", "Overstrength Ω0", "3"), new("Cd", "Deflection amplification Cd", "5.5"),
        new("I", "Importance factor Ie", "1"), new("S1", "S1 (g)", "0.6"), new("TL", "Long period TL (s)", "8"),
        new("SiteClass", "Site class", "D", ParamKind.Choice, ["A", "B", "BC", "C", "CD", "D", "DE", "E"]),
        new("SDS", "SDS (g)", "", Help: "Needed: from the USGS Seismic Design Geodatabase for the site class (ASCE 7-22 has no Fa and Fv tables)."),
        new("SD1", "SD1 (g)", "", Help: "Needed: from the USGS Seismic Design Geodatabase for the site class."),
        new("CuCap", "Cap T at Cu Ta", "Yes", ParamKind.Choice, ["Yes", "No"]),
    ];

    private static IEnumerable<CodeParam> Ubc() =>
    [
        new("Z", "Seismic zone factor Z", "0.4", ParamKind.Choice, ["0.075", "0.15", "0.2", "0.3", "0.4"]),
        new("SoilType", "Soil profile", "SD", ParamKind.Choice, ["SA", "SB", "SC", "SD", "SE"]),
        new("Ca", "Ca", "", Help: "Blank: from Z and the soil."), new("Cv", "Cv", "", Help: "Blank: from Z and the soil."),
        new("Na", "Near source Na", "1"), new("Nv", "Near source Nv", "1"),
        new("R", "R", "8.5"), new("I", "Importance factor I", "1"),
    ];

    private static IEnumerable<CodeParam> Ec8(bool ecp) =>
    [
        new("ag", "Design ground acceleration agR (g)", ecp ? "0.15" : "0.25"),
        new("GammaI", "Importance factor γI", "1"),
        new("Ground", "Ground type", "C", ParamKind.Choice, ecp ? ["A", "B", "C", "D"] : ["A", "B", "C", "D", "E"]),
        new("SpectrumType", "Spectrum type", "1", ParamKind.Choice, ["1", "2"]),
        new("q", "Behaviour factor q", ecp ? "5" : "3.9"),
        new("Beta", "Lower bound β", "0.2"),
        new("Lambda", "Correction λ", "", Help: "Blank: 0.85 when T ≤ 2 TC and the building has more than two stories, else 1."),
        new("S", "Soil factor S", "", Help: "Blank: from the ground type."), new("TB", "TB (s)", ""), new("TC", "TC (s)", ""), new("TD", "TD (s)", ""),
    ];

    private static IEnumerable<CodeParam> Nbcc() =>
    [
        new("Sa02", "S(0.2) (g)", "0.6"), new("Sa05", "S(0.5) (g)", "0.4"), new("Sa10", "S(1.0) (g)", "0.2"), new("Sa20", "S(2.0) (g)", "0.1"),
        new("Sa50", "S(5.0) (g)", "0.03"), new("Sa100", "S(10.0) (g)", "0.01"),
        new("Mv", "Higher mode factor Mv", "1"), new("IE", "Importance factor IE", "1"), new("Rd", "Ductility Rd", "4"), new("Ro", "Overstrength Ro", "1.7"),
    ];

    private static IEnumerable<CodeParam> Is1893() =>
    [
        new("Z", "Zone factor Z", "0.24", ParamKind.Choice, ["0.10", "0.16", "0.24", "0.36"]),
        new("Soil", "Soil type", "II", ParamKind.Choice, ["I", "II", "III"]),
        new("I", "Importance factor I", "1"), new("R", "Response reduction R", "5"),
    ];

    private static IEnumerable<CodeParam> UserSpectrum() =>
    [
        new("Spectrum", "Spectrum points: T Sa ...", "0 0.4 0.2 1 0.6 1 2 0.3 4 0.15", ParamKind.Text, Help: "Periods (s) and Sa (g) in pairs, as in a code spectrum."),
        new("R", "Reduction R (or q)", "1"), new("I", "Importance factor", "1"), new("K", "Height exponent k", "", Help: "Blank: 1 up to 0.5 s, 2 from 2.5 s."),
        new("Min", "Minimum Cs", "0"),
    ];

    private static IEnumerable<CodeParam> UserLoads(bool wind) =>
    [
        new("Loads", wind ? "Story forces: story Fx Fy Mz; ..." : "Story forces: story Fx Fy Mz; ...", "", ParamKind.Text,
            Help: "One story per item, in the model's units, e.g. \"Story1 100 0 0; Story2 200 0 0\". Applied at each story's mass center."),
        .. wind ? [] : new[] { new CodeParam("Ecc", "Eccentricity ratio", "0") },
    ];

    private static IEnumerable<CodeParam> WindCommon(string speed, string unit) =>
    [
        new("Angle", "Wind direction (degrees from X)", "0"),
        new("Speed", "Basic wind speed", speed), new("SpeedUnit", "Speed unit", unit, ParamKind.Choice, ["m/s", "km/h", "mph"]),
        new("Cpw", "Windward coefficient", "0.8"), new("Cpl", "Leeward coefficient", "0.5"),
        new("Width", "Exposure width", "", Help: "Blank: the plan size across the wind, from the model."),
    ];

    private static readonly string[] AsceExposure = ["B", "C", "D"];

    public static IReadOnlyList<LateralCode> All { get; } = Build();

    private static List<LateralCode> Build()
    {
        var list = new List<LateralCode>
        {
            new("User Coefficient", Seismic, "user-coefficient", P(Direction(), Range(), [new("C", "Base shear coefficient C", "0.1"), new("K", "Height exponent K", "1")]),
                "V = C W, spread up the height by w h^K."),
            new("User Loads", Seismic, "user-loads", P(UserLoads(false), Range()), "Story forces you give, at each story's mass center."),
            new("User Spectrum", Seismic, "user-spectrum", P(Direction(), Range(), Period("0.0466", "0.9"), UserSpectrum()),
                "Any code: V = Sa(T) I / R W from the spectrum points you give, spread by w h^k."),
        };
        list.Add(new("ASCE 7-22", Seismic, "asce7", P(Direction(), Range(), Period("0.0466", "0.9"), Asce722()),
            "Equivalent lateral force (ASCE 7-22 12.8): Cs = SDS / (R/Ie), capped by SD1/T and SD1 TL/T², at least 0.044 SDS Ie and 0.01; k from T. SDS and SD1 from the USGS geodatabase."));
        // Each takes Fa and Fv from its own ASCE 7 edition (SiteCoefficients): 7-05 and 7-10 for IBC 2006 to 2015, 7-16 after.
        foreach (var (code, cu) in new[] { ("ASCE 7-16", true), ("ASCE 7-10", true), ("ASCE 7-05", true), ("IBC 2021", true), ("IBC 2018", true),
                     ("IBC 2015", true), ("IBC 2012", true), ("IBC 2009", true), ("IBC 2006", true), ("SBC 301-2018", true), ("KBC 2016", true) })
            list.Add(new(code, Seismic, "asce7", P(Direction(), Range(), Period("0.0466", "0.9"), Asce(cu)),
                "Equivalent lateral force (ASCE 7 12.8): Cs = SDS / (R/Ie), capped by SD1/T and SD1 TL/T², at least 0.044 SDS Ie and 0.01; k from T."));
        // NSCP 2015 (section 208) is the UBC 97 static force procedure: Z, Ca, Cv, Na, Nv.
        foreach (var code in new[] { "UBC 97", "NSCP 2015", "UBC 94", "BOCA 96", "NBC 97" })
            list.Add(new(code, Seismic, "ubc97", P(Direction(), Range(), Period("0.0731", "0.75"), Ubc()),
                "UBC 97 1630.2: V = Cv I W / (R T), at most 2.5 Ca I W / R, at least 0.11 Ca I W (and 0.8 Z Nv I W / R in zone 4), with Ft at the top."));
        foreach (var code in new[] { "Eurocode 8 2004", "Italian NTC 2018", "Italian NTC 2008" })
            list.Add(new(code, Seismic, "ec8", P(Direction(), Range(), Period("0.075", "0.75"), Ec8(false)),
                "Lateral force method (EN 1998-1 4.3.3.2): Fb = Sd(T1) λ W, design spectrum 3.2.2.5, spread by z m."));
        list.Add(new("ECP 201-2012", Seismic, "ec8", P(Direction(), Range(), Period("0.075", "0.75"), Ec8(true)),
            "Egyptian code ECP 201: the Eurocode 8 lateral force method with the Egyptian zone values; check S, TB, TC and TD against its tables.", "Modeler"));
        foreach (var code in new[] { "NBCC 2020", "NBCC 2015", "NBCC 2010", "NBCC 2005" })
            list.Add(new(code, Seismic, "nbcc", P(Direction("0.10"), Range(), Period("0.075", "0.75"), Nbcc()),
                "NBCC 4.1.8.11: V = S(Ta) Mv IE W / (Rd Ro), at least S(4.0) or S(2.0), for Rd ≥ 1.5 at most 2/3 S(0.2) (2015 and 2020: or S(0.5) if larger), with Ft at the top; accidental eccentricity 0.10 Dn."));
        foreach (var code in new[] { "IS 1893:2016", "IS 1893:2002" })
            list.Add(new(code, Seismic, "is1893", P(Direction(), Range(), Period("0.075", "0.75"), Is1893()),
                "IS 1893 7.2: Ah = Z/2 · I/R · Sa/g, spread by W h²."));

        list.Add(new("User Loads", Wind, "user-loads", P(UserLoads(true), Range()), "Story wind forces you give."));
        list.Add(new("User Pressure", Wind, "user-pressure", P(WindCommon("0", "m/s").Where(p => p.Key is "Angle" or "Width"), Range(),
            [new("Profile", "Design pressure by height: z p ...", "0 1 10 1.2 30 1.5", ParamKind.Text, Help: "Heights (model length) and net pressure (force per area), in pairs; any code's pressures."),
             new("Factor", "Factor", "1")]),
            "Any code: the net pressure you give at each height, times the story's exposed area."));
        foreach (var code in new[] { "ASCE 7-22", "ASCE 7-16", "ASCE 7-10", "ASCE 7-05", "IBC 2021", "IBC 2018", "IBC 2015", "IBC 2012", "SBC 301-2018" })
            list.Add(new(code, Wind, "asce7-wind", P(WindCommon(code.EndsWith("05") ? "90" : "115", "mph"), Range(),
                [new("Exposure", "Exposure", "C", ParamKind.Choice, AsceExposure), new("Kzt", "Topographic Kzt", "1"), new("Kd", "Directionality Kd", "0.85"),
                 new("Ke", "Ground elevation Ke", "1"), new("G", "Gust factor G", "0.85"), new("Iw", "Importance (ASCE 7-05)", "1"),
                 // Named as SAP2000's Set call names them, so a live program takes the same case.
                 new("ASCECase", "Wind load case (Fig. 27.3-8)", "5", ParamKind.Choice, ["1", "2", "3", "4", "5"],
                     "5: all four cases, as CSI makes them (the Modeler's pattern holds case 1 and lists the others' shear and torsion). 1 to 4: that case only, one pattern each."),
                 new("ASCEe1", "Eccentricity e1 (of the width)", "0.15", Help: "Cases 2 and 4: ±0.15 of the width across the wind; give -0.15 for the other side."),
                 new("ASCEe2", "Eccentricity e2 (of the 90° width)", "0.15", Help: "Case 4: ±0.15 of the width across the 90° wind.")]),
                "MWFRS directional procedure (ASCE 7 27.3): qz = 0.613 Kz Kzt Kd Ke V², p = q G Cp, windward at qz, leeward at qh."));
        foreach (var code in new[] { "Eurocode 1 2005", "ECP 201-2012" })
            list.Add(new(code, Wind, "ec1-wind", P(WindCommon("28", "m/s"), Range(),
                [new("Terrain", "Terrain category", "II", ParamKind.Choice, ["0", "I", "II", "III", "IV"]), new("Co", "Orography co", "1"),
                 new("CsCd", "Structural factor cs cd", "1"), new("Rho", "Air density (kg/m³)", "1.25")]),
                code.StartsWith("ECP") ? "Egyptian wind loading through the EN 1991-1-4 profile: qp(z) = [1 + 7 Iv] ½ ρ vm²; check the factors against the Egyptian code." : "EN 1991-1-4: qp(z) = [1 + 7 Iv(z)] ½ ρ vm(z)², vm = cr co vb, F = cs cd cf qp A.",
                code.StartsWith("ECP") ? "Modeler" : "ETABS, SAP2000"));
        foreach (var code in new[] { "IS 875:2015", "IS 875:1987" })
            list.Add(new(code, Wind, "is875-wind", P(WindCommon("44", "m/s"), Range(),
                [new("Category", "Terrain category", "2", ParamKind.Choice, ["1", "2", "3", "4"]), new("k1", "Risk k1", "1"), new("k3", "Topography k3", "1"), new("k4", "Importance k4", "1")]),
                "IS 875 Part 3: Vz = Vb k1 k2 k3 k4, pz = 0.6 Vz², with k2 from the terrain category and height."));
        return list;
    }

    /// <summary>The group box of the programs' Modify Lateral Load Pattern form a code input sits in (ETABS 23.2, SAP2000
    /// 26.3: Direction and Eccentricity, Time Period, Story Range, Factors, Seismic Coefficients; for wind Exposure and
    /// Pressure Coefficients, Exposure Height, Wind Coefficients, Wind Case), so the Engine's form is laid out as theirs.</summary>
    public static string Group(LateralCode code, CodeParam p)
    {
        var wind = code.Kind == Wind;
        return p.Key switch
        {
            "Dir" or "Ecc" => wind ? "Exposure and Pressure Coefficients" : "Direction and Eccentricity",
            "Period" or "System" or "Ct" or "x" or "UserT" or "CuCap" => "Time Period",
            "TopStory" or "BottomStory" => wind ? "Exposure Height" : "Story Range",
            "Angle" or "Width" or "Cpw" or "Cpl" => "Exposure and Pressure Coefficients",
            "ASCECase" or "ASCEe1" or "ASCEe2" => "Wind Case",
            "Loads" => wind ? "User Wind Loads" : "User Loads",
            "Spectrum" => "Spectrum",
            "Profile" or "Factor" => "Pressure",
            "R" or "Omega" or "Cd" or "I" or "IE" or "GammaI" or "q" or "Beta" or "Lambda" or "Mv" or "Rd" or "Ro" or "K" or "Min" or "C" => "Factors",
            _ => wind ? "Wind Coefficients" : "Seismic Coefficients",
        };
    }

    /// <summary>The codes of one kind (Seismic or Wind).</summary>
    public static IEnumerable<LateralCode> Of(string kind) => All.Where(c => c.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static LateralCode? Find(string kind, string name) =>
        Of(kind).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Of(kind).FirstOrDefault(c => Norm(c.Name) == Norm(name));
}
