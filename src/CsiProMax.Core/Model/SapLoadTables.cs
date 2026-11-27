using CsiProMax.CsiTables;
using CsiProMax.S2k;

namespace CsiProMax.Model;

/// <summary>
/// SAP2000's own load tables (Define > Load Patterns with every Auto Lateral code, Load Cases of every type with all
/// their sub-forms, Load Combinations), by SAP2000 26.3's table and field names, kept as rows in
/// <see cref="StructuralModel.DefinitionTables"/> so every option of SAP2000's forms is held (Ahmed 2026-11-27: no option
/// lost, nothing greyed). The first field of each table is the row's key (LoadPat, Case, ComboName, ...).
/// The .s2k reader keeps every row of these tables; the writer lays them over the rows it makes from the model:
/// in a table it makes, the model's own fields (<see cref="ModelOwned"/>) stay as the model has them and the rest
/// come from the kept rows; a table it does not make goes in from the kept rows as they are. Rows of a pattern, case
/// or combination the model no longer has are dropped (a case type the model cannot hold, such as Multi-step
/// Static or Steady State, lives only here and is kept).
/// </summary>
public static class SapLoadTables
{
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Auto Combination Option Data 01 - General"] = ["DesignType", "AutoGen"],
        ["Auto Combination Option Data 02 - User Data"] = ["DesignType", "LSType", "LCName"],
        ["Auto Combination Option Parameters for AASHTO STD 2002"] = ["DesignType", "BetaE", "BetaD", "LIFactor"],
        ["Auto Combination Option Parameters for AASHTO LRFD 2007"] = ["DesignType", "GammaEQ", "GammaSE", "DD", "EH", "EV", "GammaTG1", "GammaTG2", "GammaTG3"],
        ["Auto Combination Option Parameters for Chinese JTG"] = ["DesignType", "IMOption", "Frequency", "IMFactUser", "IMFactor", "Curved"],
        ["Auto Seastate"] = ["LoadPat", "ParamOpt", "CoordSys", "AdjustGravLat", "AdjustGravLatFactor", "AdjustGravVert", "AdjustGravVertFactor", "IgnorePhase", "CenterX", "CenterY", "CenterZ", "UX", "PeriodUX", "PhaseUX", "UY", "PeriodUY", "PhaseUY", "UZ", "PeriodUZ", "PhaseUZ", "RX", "PeriodRX", "PhaseRX", "RY", "PeriodRY", "PhaseRY", "RZ", "PeriodRZ", "PhaseRZ", "AccelUX", "AccelUY", "AccelUZ", "AccelRX", "AccelRY", "AccelRZ", "VelRX", "VelRY", "VelRZ"],
        ["Auto Seismic - AS 1170 2007"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "kt", "UserT", "UserZ", "MaxZ", "MinZ", "SiteClass", "ProbFact", "HazFact", "StPerFact", "StDucFact", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - BOCA96"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Aa", "Av", "S", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - Chinese 2010"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "UserT", "UserZ", "MaxZ", "MinZ", "AlphaMax", "SI", "DampRatio", "Tg", "PTDF", "EnhanceFact", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - Dominican Republic R-001"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "ProgramCalc", "UserT", "UserZ", "MaxZ", "MinZ", "Ss", "S1", "Fa", "Fv", "UseCoeff", "R", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - Eurocode8 2004"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "Country", "Ag", "SpecType", "GroundType", "SoilFact", "Tb", "Tc", "Td", "LBFact", "BehaveFact", "CorrFact", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - IBC2003"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "SeisGroup", "SiteClass", "Ss", "S1", "Fa", "Fv", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - IBC2006"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "I", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - IBC2009"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "I", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - IBC2012"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "I", "AccOption", "Latitude", "Longitude", "ZipCode", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - ASCE 7-16"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "I", "Ss", "S1", "TL", "SiteClass", "Fa", "Fv", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - ASCE 7-22"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "MethodType", "RSFunc", "Smax", "Tmax", "Sds", "Sd1", "TL", "SiteClass", "S1", "R", "Omega", "Cd", "I", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - IS1893:2002"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "ZCode", "ZUser", "SoilType", "I", "R", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - IS 1893:2016"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "ZCode", "ZUser", "SoilType", "I", "R", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - ITALIAN NTC 2018"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "UserT", "UserZ", "MaxZ", "MinZ", "AccOption", "Latitude", "Longitude", "Island", "agOverg", "F0", "Tcs", "LimitState", "UsageClass", "NominalLife", "SpectrumType", "SoilType", "Topography", "hoverH", "Tb", "Tc", "Td", "Damping", "BehaviorFactor", "CorrecFactor", "d", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - ITALIAN NTC 2008"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "AccOption", "Latitude", "Longitude", "Island", "agOverg", "F0", "Tcs", "LimitState", "UsageClass", "NominalLife", "SpectrumType", "SoilType", "Topography", "hoverH", "Tb", "Tc", "Td", "Damping", "BehaviorFactor", "CorrecFactor", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - TCVN 9386-2012"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "Ag", "GroundType", "SoilFact", "Tb", "Tc", "Td", "LBFact", "BehaveFact", "CorrFact", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - KBC 2009"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "I", "Ss", "S1", "SiteClass", "Fa", "Fv", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - KBC 2016"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "I", "SoilDepth", "Ss", "S1", "SiteClass", "Fa", "Fv", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - KDS 41 17 00-2019"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Omega", "Cd", "I", "SeismicZone", "SWave", "S", "SiteClass", "Fa", "Fv", "TL", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - NBCC 2020"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "StructType", "PeriodCalc", "UserT", "UserZ", "MaxZ", "MinZ", "Sa02", "Sa05", "Sa1", "Sa2", "Sa5", "Sa10", "Ie", "Mv", "Rd", "Ro", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear", "FtUsed"],
        ["Auto Seismic - NBCC 2015"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "StructType", "PeriodCalc", "UserT", "UserZ", "MaxZ", "MinZ", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "Sa5", "Sa10", "SiteClass", "F02", "F05", "F1", "F2", "F5", "F10", "Ie", "Mv", "Rd", "Ro", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear", "FtUsed"],
        ["Auto Seismic - NBCC 2010"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "StructType", "PeriodCalc", "UserT", "UserZ", "MaxZ", "MinZ", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "Sa4", "SiteClass", "Fa", "Fv", "Ie", "Mv", "Rd", "Ro", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear", "FtUsed"],
        ["Auto Seismic - NBCC 2005"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "StructType", "PeriodCalc", "UserT", "UserZ", "MaxZ", "MinZ", "PGA", "Sa02", "Sa05", "Sa1", "Sa2", "SiteClass", "Fa", "Fv", "Ie", "Mv", "Rd", "Ro", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear", "FtUsed"],
        ["Auto Seismic - NBCC95"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "StructType", "PeriodCalc", "Ds", "UserT", "UserZ", "MaxZ", "MinZ", "R", "Za", "Zv", "VType", "V", "I", "F", "VeOverWUsed", "WeightUsed", "BaseShear", "FtUsed"],
        ["Auto Seismic - NEHRP97"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "R", "SeisGroup", "SiteClass", "Ss", "S1", "Fa", "Fv", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - NZS 1170 2004"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "UserT", "UserZ", "MaxZ", "MinZ", "SiteClass", "Z", "R", "D", "StPerFact", "StDucFact", "ConsTSite", "TSite", "ConsSStory", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - TSC 2007"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "ZCode", "ZUser", "SeismicZone", "SiteClass", "A0", "I", "R", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - TSC-2018"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "CtAndX", "UserT", "UserZ", "MaxZ", "MinZ", "R", "D", "I", "Ss", "S1", "TL", "SiteClass", "Fs", "F1", "SDS", "SD1", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear"],
        ["Auto Seismic - UBC94"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "Rw", "Z", "S", "I", "TUsed", "CoeffUsed", "WeightUsed", "BaseShear", "FtUsed"],
        ["Auto Seismic - UBC97"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "PeriodCalc", "Ct", "UserT", "UserZ", "MaxZ", "MinZ", "R", "SoilType", "Z", "Ca", "Cv", "SourceType", "SourceDist", "Na", "Nv", "I", "TUsed", "WeightUsed", "BaseShear", "FtUsed"],
        ["Auto Seismic - UBC97 Isolated"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "UserZ", "MaxZ", "MinZ", "Ri", "Bd", "Kmax", "Kmin", "SoilType", "Z", "Cvd", "SourceType", "SourceDist", "Nv", "WeightUsed", "BaseShear"],
        ["Auto Seismic - User Coefficient"] = ["LoadPat", "Dir", "PercentEcc", "EccOverride", "UserZ", "MaxZ", "MinZ", "C", "K", "WeightUsed", "BaseShear"],
        ["Auto Seismic - User Loads"] = ["LoadPat", "Diaphragm", "DiaphragmZ", "AppPoint", "AddEcc", "FX", "FY", "MZ", "X", "Y", "BaseShear"],
        ["Auto Seismic Eccentricity Overrides"] = ["LoadPat", "AutoLdType", "Diaphragm", "Eccen"],
        ["Auto Wave 1 - General"] = ["LoadPat", "WaveChar", "Current", "MarineGrow", "DICoeff", "WaveWind", "BuoyLoad", "LoadDisc", "InitCrestX", "InitCrestY", "NumPos", "DirAngle", "VertDatum", "MLFromDatum", "HTFromDatum", "WWtDensity"],
        ["Auto Wave 3 - Wave Characteristics - General"] = ["WaveChar", "WaveType", "KinFactor", "SWaterDepth", "WaveHeight", "WavePeriod", "WaveTheory", "TheoryOrd"],
        ["Auto Wave 4 - Wave Characteristics - User Horizontal Coordinates"] = ["WaveChar", "HLoc", "SFromDatum"],
        ["Auto Wave 5 - Wave Characteristics - User Vertical Coordinates"] = ["WaveChar", "VFromDatum"],
        ["Auto Wave 6 - Wave Characteristics - User Wave Data"] = ["WaveChar", "HLoc", "VFromDatum", "HWaveVel", "HWaveAcc", "VWaveVel", "VWaveAcc", "Pressure"],
        ["Auto Wave 7 - Current Profile"] = ["Current", "VFromDatum", "HCurrVel", "DirAngle", "BlockFact", "StretchOpt"],
        ["Auto Wave 8 - Marine Growth"] = ["MarineGrow", "VFromDatum", "Thickness"],
        ["Auto Wave 9 - Drag And Inertia Coefficients"] = ["DICoeff", "VFromDatum", "DragCoeff", "InerCoeff"],
        ["Auto Wave 10 - Wave Wind Loads"] = ["WaveWind", "DirAngle", "MeanSpeed", "AvgTimePer", "AirMassDen", "TypShapeCo"],
        ["Auto Wind - AASHTO 2020"] = ["LoadPat", "LimitState", "Superstructure", "Substructure", "Vertical", "GrndElev", "SuperZProg", "SuperZ", "SubZProg", "SubZ", "WindSpeed", "ExpCat", "G", "CdSuper", "CdSub"],
        ["Auto Wind - AASHTO 2018"] = ["LoadPat", "LimitState", "Superstructure", "Substructure", "Vertical", "GrndElev", "SuperZProg", "SuperZ", "SubZProg", "SubZ", "WindSpeed", "ExpCat", "G", "CdSuper", "CdSub"],
        ["Auto Wind - API 4F"] = ["LoadPat", "ExposeFrom", "Angle", "UserZ", "MaxZ", "MinZ", "WindVel", "SSLMult", "ShieldFact"],
        ["Auto Wind - API 4F 2013"] = ["LoadPat", "ExposeFrom", "Angle", "UserZ", "MaxZ", "MinZ", "WindVel", "SSLMult", "ShieldFact"],
        ["Auto Wind - ASCE7-88"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "GustFactor", "ExpWidth"],
        ["Auto Wind - ASCE7-95"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "Kzt", "GustFactor", "ExpWidth"],
        ["Auto Wind - ASCE7-02"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "ASCECase", "E1", "E2", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "Kzt", "GustFactor", "Kd", "SolidRatio", "ExpWidth"],
        ["Auto Wind - ASCE7-05"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "ASCECase", "E1", "E2", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "Kzt", "GustFactor", "Kd", "SolidRatio", "ExpWidth"],
        ["Auto Wind - ASCE7-10"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "ASCECase", "E1", "E2", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "Kzt", "GustFactor", "Kd", "SolidRatio", "ExpWidth"],
        ["Auto Wind - ASCE 7-16"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "ASCECase", "E1", "E2", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "Kzt", "GustFactor", "Kd", "SolidRatio", "ExpWidth"],
        ["Auto Wind - ASCE 7-22"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCpAlongWind", "LeewardCpAlongWind", "WindwardCpAcrossWind", "LeewardCpAcrossWind", "ASCECase", "E1", "E2", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "Ke", "Kzt", "GustFactor", "Kd", "SolidRatio", "ExpWidth"],
        ["Auto Wind - AS-NZS 1170 2 2002"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "Ka", "Kc", "Kl", "Kp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "TerrainCat", "CycloneReg", "Md", "Ms", "Mt", "Cdyn", "ExpWidth"],
        ["Auto Wind - AS-NZS 1170 2 2011"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "Ka", "Kc", "Kl", "Kp", "ASCECase", "UserZ", "MaxZ", "MinZ", "WindSpeed", "TerrainCat", "Md", "Ms", "Mt", "Cdyn", "ExpWidth"],
        ["Auto Wind - BOCA96"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "GhType", "Gh", "ExpWidth"],
        ["Auto Wind - BS6399-95"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "Ve", "Ca", "Cr", "ExpWidth"],
        ["Auto Wind - Chinese 2010"] = ["LoadPat", "ExposeFrom", "Angle", "BldgWidth", "ShapeCoeff", "ComboType", "IsTower", "UserZ", "MaxZ", "MinZ", "WindSpeed", "GrndRough", "AcrossWind", "IsCircular", "CornerRatio", "LatPeriod", "TorsionWind", "TorsPeriod", "BldgDepth", "PhiZSource", "T1Source", "UserT1", "DampRatio", "ExpWidth"],
        ["Auto Wind - CSA S6-14"] = ["LoadPat", "Superstructure", "Substructure", "Vertical", "GrndElev", "SuperZProg", "SuperZ", "q", "Cg", "CeProg", "Ce", "ChStruct", "ChLiveLoad", "Cv"],
        ["Auto Wind - Eurocode1 2005"] = ["LoadPat", "StructType", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "Superstructure", "Substructure", "Vertical", "GrndElev", "SuperZProg", "SuperZ", "SubZProg", "SubZ", "WindSpeed", "TerrainCat", "OroFact", "TurbFact", "StructFact", "AirDensity", "ExpWidth", "Cfx", "CfzUp"],
        ["Auto Wind - IRC 6-2017"] = ["LoadPat", "Superstructure", "Substructure", "Vertical", "GrndElev", "SuperZProg", "SuperZ", "SubZProg", "SubZ", "vb", "Terrain", "G", "CDSuper", "CDSub", "CL"],
        ["Auto Wind - IS875-1987"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "TerrainCat", "StructClass", "k1", "k3", "ExpWidth"],
        ["Auto Wind - IS 875-2015"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "TerrainCat", "ImportanceFac", "k1", "k3", "ExpWidth"],
        ["Auto Wind - ITALIAN NTC 2018"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "ExposureCat", "TopoFact", "DynCoeff", "ExpWidth"],
        ["Auto Wind - ITALIAN NTC 2008"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "ExposureCat", "TopoFact", "DynCoeff", "ExpWidth"],
        ["Auto Wind - KBC 2016"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "ExposureCat", "GustFactor", "Topographic", "Importance", "ExpWidth"],
        ["Auto Wind - Mexican"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindVel", "ExpWidth"],
        ["Auto Wind - NBCC 2020"] = ["LoadPat", "ExposeFrom", "Angle", "Terrain", "Cew", "Cel", "WindwardCp", "LeewardCp", "ASCECase", "E1", "E2", "UserZ", "MaxZ", "MinZ", "VelPressure", "Cg", "Ct", "Iw", "ExpWidth"],
        ["Auto Wind - NBCC 2015"] = ["LoadPat", "ExposeFrom", "Angle", "Terrain", "Cew", "Cel", "WindwardCp", "LeewardCp", "ASCECase", "E1", "E2", "UserZ", "MaxZ", "MinZ", "VelPressure", "Cg", "Ct", "Iw", "ExpWidth"],
        ["Auto Wind - NBCC 2010"] = ["LoadPat", "ExposeFrom", "Angle", "Terrain", "Cew", "Cel", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "VelPressure", "Cg", "Iw", "ExpWidth"],
        ["Auto Wind - NBCC 2005"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "VelPressure", "Cg", "Iw", "ExpWidth"],
        ["Auto Wind - NBCC95"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "VelPressure", "Cg", "ExpWidth"],
        ["Auto Wind - SP 20.13330.2016"] = ["LoadPat", "LoadType", "ExposeFrom", "Angle", "Cp", "UserZ", "MaxZ", "MinZ", "UserDynamic", "StructType", "LogDecr", "Width", "Depth", "Zeq", "WindDist", "WindPres", "Terrain", "LimitFreq", "ModalCase", "FirstMode"],
        ["Auto Wind - TS 498-97"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCp", "LeewardCp", "UserZ", "MaxZ", "MinZ", "WindSpeed", "CoeffDyn", "ExpWidth"],
        ["Auto Wind - UBC94"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCq", "LeewardCq", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "ExpWidth"],
        ["Auto Wind - UBC97"] = ["LoadPat", "ExposeFrom", "Angle", "WindwardCq", "LeewardCq", "UserZ", "MaxZ", "MinZ", "WindSpeed", "Exposure", "I", "ExpWidth"],
        ["Auto Wind - User"] = ["LoadPat", "Diaphragm", "DiaphragmZ", "FX", "FY", "MZ", "X", "Y"],
        ["Auto Wind - Live Load"] = ["LoadPat", "RefLoadPat", "Height", "LLHeight"],
        ["Auto Wind Exposure For Horizontal Diaphragms"] = ["LoadPat", "Diaphragm", "DiaphragmZ", "TribWidth", "TribDepth", "TribHeight", "X", "Y"],
        ["Auto Wind Angles"] = ["LoadPat", "SymTran", "SymLong", "UserDef", "Angle", "TranCoeff", "LongCoeff"],
        ["Case - Buckling 1 - General"] = ["Case", "NumBuckMode", "EigenTol"],
        ["Case - Buckling 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "LoadSF", "TransAccSF", "RotAccSF"],
        ["Case - Direct History 1 - General"] = ["Case", "OutSteps", "StepSize", "VDampType", "MDampType", "MConstDamp", "MaxModFreq"],
        ["Case - Direct History 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Function", "LoadSF", "TransAccSF", "RotAccSF", "TimeFactor", "ArrivalTime", "CoordSys", "Angle"],
        ["Case - Direct History 3 - Viscous Proportional Damping"] = ["Case", "SpecifyType", "MassCoeff", "StiffCoeff", "Period1", "Frequency1", "Ratio1", "Damping1", "Period2", "Frequency2", "Ratio2", "Damping2", "Mode4Ratio"],
        ["Case - Direct History 4 - Integration Parameters"] = ["Case", "IntMethod", "Gamma", "Beta", "Theta", "Alpha", "AlphaM"],
        ["Case - Direct History 5 - Nonlinear Parameters"] = ["Case", "GeoNonLin", "SolScheme", "DTMax", "DTMin", "MaxIter", "MaxIterCS", "MaxIterNR", "ItConvTol", "UseEvStep", "EvLumpTol", "LSPerIter", "LSTol", "LSStepFact", "MaxEvPerStp", "UseIter", "UseLineSrch", "StpAnalysis", "MinEvStepSz", "MaxNulEvStp", "UseCorrStep"],
        ["Case - Direct History 6 - Modal Proportional Damping"] = ["Case", "SpecifyType", "MassCoeff", "StiffCoeff", "Period1", "Frequency1", "Ratio1", "Damping1", "Period2", "Frequency2", "Ratio2", "Damping2", "Mode4Ratio"],
        ["Case - Direct History 7 - Modal Interpolated Damping"] = ["Case", "Period", "Frequency", "Damping"],
        ["Case - Direct History 8 - Modal Damping Overrides"] = ["Case", "Mode", "Damping"],
        ["Case - Direct History 9 - Collapse - Objects to Remove"] = ["Case", "IsCollapse", "ObjectType", "ObjectName", "RemoveTime", "Duration"],
        ["Case - Frequency History 1 - General"] = ["Case", "HistoryType", "OutSteps", "StepSize", "DampingType"],
        ["Case - Frequency History 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Function", "LoadSF", "TransAccSF", "RotAccSF", "TimeFactor", "ArrivalTime", "CoordSys", "Angle"],
        ["Case - Frequency History 3 - Constant Damping"] = ["Case", "MassCoeff", "StiffCoeff"],
        ["Case - Frequency History 4 - Interpolated Damping"] = ["Case", "FreqUnits", "Frequency", "MassCoeff", "StiffCoeff"],
        ["Case - Frequency History 5 - Integration Parameters"] = ["Case", "SubSteps", "PadFact", "TimeStepAdj"],
        ["Case - Frequency History 6 - SSI Control Parameters"] = ["Case", "SoilProfile", "TopSoilElev", "X", "Y", "ZLayer", "RadiusType", "UserRadius", "NumLayersHS"],
        ["Case - Frequency History 7 - SSI Wave Field Parameters"] = ["Case", "WaveType", "AngleProp", "IncludeR", "IncludeSV", "IncludeP", "IncludeSH", "IncludeL", "IncAngleSV", "IncAngleP", "IncAngleSH"],
        ["Case - Frequency History 8 - SSI Wave Composition Ratios"] = ["Case", "Frequency", "RWaveRatio", "SVWaveRatio", "PWaveRatio", "SHWaveRatio", "LWaveRatio"],
        ["Case - Frequency History 9 - SSI Response Interpolation"] = ["Case", "IsFreqReInt", "IsMaxFreq", "MaxFreq", "ModalCase", "IsUnifInc", "FirstFreq", "LastFreq", "NumFreqInc", "IsModalFreq", "IsDeviation", "Deviation", "IsSpecFreq", "SpecFreq"],
        ["Case - Modal 1 - General"] = ["Case", "ModeType", "MaxNumModes", "MinNumModes", "EigenShift", "EigenCutoff", "EigenTol", "AutoShift"],
        ["Case - Modal 2 - Load Assignments - Eigen"] = ["Case", "LoadType", "LoadName", "TargetPar", "StatCorrect"],
        ["Case - Modal 3 - Load Assignments - Ritz"] = ["Case", "LoadType", "LoadName", "MaxCycles", "TargetPar"],
        ["Case - Modal History 1 - General"] = ["Case", "HistoryType", "OutSteps", "StepSize", "DampingType", "ConstDamp"],
        ["Case - Modal History 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Function", "LoadSF", "TransAccSF", "RotAccSF", "TimeFactor", "ArrivalTime", "CoordSys", "Angle"],
        ["Case - Modal History 3 - Interpolated Damping"] = ["Case", "Period", "Frequency", "Damping"],
        ["Case - Modal History 4 - Proportional Damping"] = ["Case", "SpecifyType", "MassCoeff", "StiffCoeff", "Period1", "Frequency1", "Ratio1", "Damping1", "Period2", "Frequency2", "Ratio2", "Damping2", "Mode4Ratio"],
        ["Case - Modal History 5 - Damping Overrides"] = ["Case", "Mode", "Damping"],
        ["Case - Modal History 6 - Nonlinear Parameters"] = ["Case", "PeriodStat", "DTMax", "DTMin", "FConvTol", "EConvTol", "ForceItMax", "ForceItMin", "ConvFactor"],
        ["Case - Moving Load 1 - Lane Assignments"] = ["Case", "AssignNum", "VehClass", "ScaleFactor", "MinLoaded", "MaxLoaded", "NumLanes"],
        ["Case - Moving Load 2 - Lanes Loaded"] = ["Case", "AssignNum", "Lane"],
        ["Case - Moving Load 3 - MultiLane Factors"] = ["Case", "NumberLanes", "ScaleFactor"],
        ["Case - Multistep Static 1 - Load Assignments"] = ["Case", "LoadType", "LoadName", "LoadSF", "TransAccSF", "RotAccSF", "StepRange", "FirstStep", "LastStep", "StartStep", "Extrapolate"],
        ["Case - Power Spectral Density 1 - General"] = ["Case", "SolType", "FirstFreq", "LastFreq", "NumFreqInc", "DampingType", "AddFreqs"],
        ["Case - Power Spectral Density 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Function", "LoadSF", "TransAccSF", "RotAccSF", "PhaseAngle", "CoordSys", "Angle"],
        ["Case - Power Spectral Density 3 - Added Frequencies General"] = ["Case", "AddMFreq", "AddMDev", "AddSpFreq"],
        ["Case - Power Spectral Density 4 - Added Frequency Deviations"] = ["Case", "Deviation"],
        ["Case - Power Spectral Density 5 - Added Specified Frequencies"] = ["Case", "Frequency"],
        ["Case - Power Spectral Density 6 - Constant Damping"] = ["Case", "MassCoeff", "StiffCoeff"],
        ["Case - Power Spectral Density 7 - Interpolated Damping"] = ["Case", "FreqUnits", "Frequency", "MassCoeff", "StiffCoeff"],
        ["Case - Response Spectrum 1 - General"] = ["Case", "ModalCombo", "GMCf1", "GMCf2", "PerRigid", "td", "DirCombo", "ABSSF", "MotionType", "DampingType", "ConstDamp", "EccenRatio", "NumOverride"],
        ["Case - Response Spectrum 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "CoordSys", "Function", "Angle", "LoadSF", "TransAccSF", "RotAccSF"],
        ["Case - Response Spectrum 3 - Interpolated Damping"] = ["Case", "Period", "Frequency", "Damping"],
        ["Case - Response Spectrum 4 - Proportional Damping"] = ["Case", "SpecifyType", "MassCoeff", "StiffCoeff", "Period1", "Frequency1", "Ratio1", "Damping1", "Period2", "Frequency2", "Ratio2", "Damping2", "Mode4Ratio"],
        ["Case - Response Spectrum 5 - Damping Overrides"] = ["Case", "Mode", "Damping"],
        ["Case - Response Spectrum 6 - Eccentricity Overrides"] = ["Case", "Diaphragm", "Eccen"],
        ["Case - Static 1 - Load Assignments"] = ["Case", "LoadType", "LoadName", "LoadSF", "TransAccSF", "RotAccSF"],
        ["Case - Static 2 - Nonlinear Load Application"] = ["Case", "LoadApp", "DisplType", "TargetDispl", "TargetRot", "MonitorDOF", "MonitorJt", "GenDispl"],
        ["Case - Static 4 - Nonlinear Parameters"] = ["Case", "Unloading", "GeoNonLin", "ResultsSave", "MinNumState", "MaxNumState", "PosIncOnly", "SolScheme", "MaxTotal", "MaxNull", "MaxIter", "MaxEvPerStp", "UseIter", "MaxIterCS", "MaxIterNR", "ItConvTol", "UseEvStep", "EvLumpTol", "LSPerIter", "LSTol", "LSStepFact", "StpAnalysis", "MinEvStepSz", "MaxNulEvStp", "UseCorrStep", "StageSave", "StageMinIns", "StageMinTD", "UseLineSrch", "TimeDepMat", "GeomIsFinal", "TFTol", "TFMaxIter", "TFAccelFact", "TFNoStop"],
        ["Case - Static 5 - Nonlinear Stage Definitions"] = ["Case", "Stage", "Duration", "Output", "OutputName", "Comment"],
        ["Case - Static 6 - Nonlinear Stage Data"] = ["Case", "Stage", "Operation", "ObjType", "ObjName", "Age", "LoadType", "LoadName", "LoadSF", "TransAccSF", "ItemType", "ItemName"],
        ["Case - Static 7 - Additional Controlled Displacements"] = ["Case", "Joint", "DOF", "GenDispl"],
        ["Case - Steady State 1 - General"] = ["Case", "SolType", "FirstFreq", "LastFreq", "NumFreqInc", "DampingType", "AddFreqs"],
        ["Case - Steady State 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Function", "LoadSF", "TransAccSF", "RotAccSF", "PhaseAngle", "CoordSys", "Angle"],
        ["Case - Steady State 3 - Added Frequencies General"] = ["Case", "AddMFreq", "AddMDev", "AddSpFreq"],
        ["Case - Steady State 4 - Added Frequency Deviations"] = ["Case", "Deviation"],
        ["Case - Steady State 5 - Added Specified Frequencies"] = ["Case", "Frequency"],
        ["Case - Steady State 6 - Constant Damping"] = ["Case", "MassCoeff", "StiffCoeff"],
        ["Case - Steady State 7 - Interpolated Damping"] = ["Case", "FreqUnits", "Frequency", "MassCoeff", "StiffCoeff"],
        ["Combination Definitions"] = ["ComboName", "ComboType", "AutoDesign", "CaseType", "CaseName", "ModeNumber", "ScaleFactor", "SteelDesign", "ConcDesign", "AlumDesign", "ColdDesign", "GUID", "Notes"],
        ["Load Case Definitions"] = ["Case", "Type", "MLFactVert", "MLFactBrake", "MLFactCentr", "InitialCond", "ModalCase", "BaseCase", "MassSource", "IncludeSSI", "DesTypeOpt", "DesignType", "DesActOpt", "DesignAct", "AutoType", "RunCase", "CaseStatus", "GUID", "Notes"],
        ["Load Pattern Definitions"] = ["LoadPat", "DesignType", "SelfWtMult", "AutoLoad", "NotBasePat", "NotRatio", "NotDir", "GUID", "Notes"],
    };

    /// <summary>In the tables the .s2k writer makes from the model, the fields the model itself decides.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ModelOwned = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Load Pattern Definitions"] = ["LoadPat", "SelfWtMult"],
        ["Load Case Definitions"] = ["Case", "Type", "InitialCond", "ModalCase", "RunCase"],
        ["Case - Static 1 - Load Assignments"] = ["Case", "LoadType", "LoadName", "LoadSF"],
        ["Case - Static 4 - Nonlinear Parameters"] = ["Case", "GeoNonLin"],
        ["Case - Modal 1 - General"] = ["Case", "ModeType", "MaxNumModes", "MinNumModes"],
        ["Case - Response Spectrum 1 - General"] = ["Case", "ModalCombo", "DirCombo", "ConstDamp", "EccenRatio"],
        ["Case - Response Spectrum 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Angle", "TransAccSF", "Function"],
        ["Case - Modal History 1 - General"] = ["Case", "OutSteps", "StepSize", "ConstDamp"],
        ["Case - Modal History 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Function", "LoadSF", "TransAccSF", "ArrivalTime", "Angle"],
        ["Case - Direct History 1 - General"] = ["Case", "OutSteps", "StepSize"],
        ["Case - Direct History 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "Function", "LoadSF", "TransAccSF", "ArrivalTime", "Angle"],
        ["Case - Buckling 1 - General"] = ["Case", "NumBuckMode"],
        ["Case - Buckling 2 - Load Assignments"] = ["Case", "LoadType", "LoadName", "LoadSF"],
        ["Case - Moving Load 1 - Lane Assignments"] = ["Case", "AssignNum", "VehClass", "ScaleFactor", "MinLoaded", "MaxLoaded", "NumLanes"],
        ["Case - Moving Load 2 - Lanes Loaded"] = ["Case", "AssignNum", "Lane"],
        ["Combination Definitions"] = ["ComboName", "ComboType", "CaseType", "CaseName", "ScaleFactor"],
    };

    /// <summary>The SAP2000 case types the model holds as its own load cases (others live only in these rows).</summary>
    public static readonly string[] ModelCaseTypes = ["LinStatic", "NonStatic", "LinModal", "LinRespSpec", "LinModHist", "NonModHist", "LinDirHist", "NonDirHist", "LinBuckling", "LinMoving"];

    public static bool Has(string table) => Fields.ContainsKey(table);

    public static string? Full(string table) => Fields.Keys.FirstOrDefault(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));

    /// <summary>A pattern, case or combination renamed ("pattern", "case", "combo"): its rows and the rows naming it follow.</summary>
    public static void Rename(StructuralModel m, string kind, string was, string name)
    {
        if (was == name || m.DefinitionTables is null) return;
        foreach (var (table, rows) in m.DefinitionTables.Where(x => Has(x.Key)))
        {
            var key = Fields[Full(table)!][0];
            foreach (var r in rows)
            {
                void Swap(string f) { if (r.TryGetValue(f, out var v) && v == was) r[f] = name; }
                if (kind == "pattern")
                {
                    if (key == "LoadPat") Swap("LoadPat");
                    if (r.GetValueOrDefault("LoadType") is { } lt && lt.StartsWith("Load", StringComparison.OrdinalIgnoreCase)) Swap("LoadName");
                    if (table.Equals("Load Pattern Definitions", StringComparison.OrdinalIgnoreCase)) Swap("NotBasePat");
                }
                else if (kind == "case")
                {
                    if (key == "Case") Swap("Case");
                    Swap("InitialCond"); Swap("ModalCase"); Swap("BaseCase");
                    if (key == "ComboName") Swap("CaseName");
                }
                else if (kind == "combo" && key == "ComboName") { Swap("ComboName"); Swap("CaseName"); }
            }
        }
    }

    /// <summary>The .s2k reader: every row of these tables, all its fields, into the model's definition tables.</summary>
    public static void Read(StructuralModel m, Dictionary<string, List<Dictionary<string, string>>> t, ISet<string> known)
    {
        foreach (var name in t.Keys.Where(Has).ToList())
        {
            known.Add(name);
            var full = Full(name)!;
            var fields = Fields[full];
            var rows = t[name].Select(r => r.Where(kv => kv.Value.Length > 0)
                    .ToDictionary(kv => fields.FirstOrDefault(f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) ?? kv.Key, kv => kv.Value, StringComparer.Ordinal))
                .Where(r => r.Count > 0).ToList();
            if (rows.Count == 0) continue;
            m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
            m.DefinitionTables[full] = rows;
        }
    }

    /// <summary>The rows the model still has a place for: those of a pattern, case or combination it has, and of a case
    /// type it cannot hold.</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> Live(StructuralModel m)
    {
        var all = (m.DefinitionTables ?? []).Where(x => Has(x.Key)).ToDictionary(x => Full(x.Key)!, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var patterns = m.LoadPatterns.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var cases = m.LoadCases.Select(c => c.Name).Concat(m.Extras?.MovingCases.Select(c => c.Name) ?? []).ToHashSet(StringComparer.Ordinal);
        var combos = m.Combos.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        // A case is kept by its row in Load Case Definitions: one the model holds only while the model has it.
        var keepCase = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in all.GetValueOrDefault("Load Case Definitions") ?? [])
            if (r.GetValueOrDefault("Case") is { } c && (cases.Contains(c) || !ModelCaseTypes.Contains(r.GetValueOrDefault("Type") ?? "", StringComparer.OrdinalIgnoreCase))) keepCase.Add(c);
        foreach (var c in cases) keepCase.Add(c);
        var live = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, rows) in all)
        {
            var key = Fields[table][0];
            Func<string, bool> ok = key switch
            {
                "LoadPat" => patterns.Contains,
                "Case" => keepCase.Contains,
                "ComboName" => combos.Contains,
                _ => _ => true,
            };
            var list = rows.Where(r => ok(r.GetValueOrDefault(key) ?? "")).ToList();
            if (list.Count > 0) live[table] = list;
        }
        return live;
    }

    /// <summary>The .s2k writer: lays the kept rows over the tables it made from the model, and adds the tables it did not make.</summary>
    public static void MergeInto(StructuralModel m, List<string> tables)
    {
        var live = Live(m);
        if (live.Count == 0) return;
        for (var i = 0; i < tables.Count; i++)
        {
            var parsed = CsiTableText.Parse(tables[i]);
            if (parsed.Count != 1) continue;
            var (name, made) = parsed.First();
            var full = Full(name);
            if (full is null || !live.Remove(full, out var kept)) continue;
            var owned = ModelOwned.GetValueOrDefault(full) ?? [Fields[full][0]];
            var key = Fields[full][0];
            var out_ = new List<Dictionary<string, string>>();
            var byKey = kept.GroupBy(r => r.GetValueOrDefault(key) ?? "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in made)
            {
                var k = row.GetValueOrDefault(key) ?? "";
                var n = seen.GetValueOrDefault(k);
                seen[k] = n + 1;
                var merged = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase);
                if (byKey.TryGetValue(k, out var over) && n < over.Count)
                    foreach (var (f, v) in over[n])
                    {
                        if (owned.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                        // A pattern's type: SAP2000's own word (Hydrostatic, Vehicle Live, ...) while it means the model's type.
                        if (f.Equals("DesignType", StringComparison.OrdinalIgnoreCase) && full.Equals("Load Pattern Definitions", StringComparison.OrdinalIgnoreCase)
                            && merged.TryGetValue(f, out var now) && S2kReader.PatternType(v) != S2kReader.PatternType(now)) continue;
                        merged[f] = v;
                    }
                out_.Add(merged);
            }
            // Rows of what the model does not hold (a case of a type it has no place for) go in after the model's.
            foreach (var (k, over) in byKey)
                if (!seen.ContainsKey(k)) out_.AddRange(over.Select(r => new Dictionary<string, string>(r, StringComparer.OrdinalIgnoreCase)));
            tables[i] = Table(name, full, out_);
        }
        foreach (var (full, rows) in live.OrderBy(x => x.Key, StringComparer.Ordinal))
            tables.Add(Table(full.ToUpperInvariant(), full, rows));
    }

    private static string Table(string name, string full, List<Dictionary<string, string>> rows)
    {
        var order = Fields[full];
        return CsiTableText.Table(name, rows.Select(r => (IReadOnlyList<(string, string)>)order.Where(r.ContainsKey).Select(f => (f, r[f]))
            .Concat(r.Where(kv => !order.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Select(kv => (kv.Key, kv.Value))).ToList()));
    }
}
