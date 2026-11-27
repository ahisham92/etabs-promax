using CsiProMax.E2k;

namespace CsiProMax.Model;

/// <summary>
/// The Define items the Modeler had no form for (Ahmed 2026-11-26: every button). What the .e2k has keywords for is kept
/// in its own lines: rebar sizes (REBARDEFINITION) and a concrete section's rebar (CONCRETESECTION, Frame Section >
/// Modify/Show Rebar). The rest (generalized displacements, shell uniform load sets, auto construction sequence, walking
/// vibrations, panel zone and frame release definitions, slab and wall layers, wall auto select lists, every hinge
/// definition) is kept as rows of ETABS 23's own tables by name, in <see cref="StructuralModel.DefinitionTables"/>, and
/// goes into ETABS through its tables when the Modeler's .e2k opens.
/// </summary>
public static class DefineTables
{
    /// <summary>SAP2000's own hinge definition tables (Define > Section Properties > Hinge Properties), by SAP2000 26's
    /// table and field names; the first field, HingeName, is the definition's name. A model for SAP2000 keeps its hinges
    /// in them, and the .s2k writes them back as they are.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SapHinges = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        // SAP2000's frame and area sections of the kinds the Engine has no geometry for (steel joist, cold formed, precast,
        // hybrid, aluminum, plane, asolid, plate ...): SAP2000's own rows, written into the .s2k as they are. The Engine keeps a
        // General section (or a shell section) of the same name, so the members and areas keep their section.
        ["Area Section Properties"] = ["Section", "Material", "MatAngle", "AreaType", "Type", "DrillDOF", "Thickness", "BendThick", "Arc", "CoordSys", "InComp", "Color", "TotalWt", "TotalMass", "F11Mod", "F22Mod", "F12Mod", "M11Mod", "M22Mod", "M12Mod", "V13Mod", "V23Mod", "MMod", "WMod", "GUID", "Notes"],
        ["Frame Section Properties 01 - General"] = ["SectionName", "Material", "FromFile", "FileName", "SectInFile", "Shape", "AutoType", "t3", "t2", "SngAngWid", "SngChnWid", "tf", "tw", "t2b", "tfb", "dis", "FilletRadius", "Radius", "LipDepth", "LipAngle", "Area", "TorsConst", "I33", "I22", "I23", "AS2", "AS3", "S33Top", "S33Bot", "S22Left", "S22Right", "Z33", "Z22", "R33", "R22", "CGOffset3", "CGOffset2", "EccV2", "EccV3", "Cw", "IncludeSCAn", "ConcCol", "ConcBeam", "Color", "TotalWt", "TotalMass", "AMod", "A2Mod", "A3Mod", "JMod", "I2Mod", "I3Mod", "MMod", "WMod", "GUID", "Notes"],
        ["Frame Section Properties 06 - Polygon Data"] = ["SectionName", "Polygon", "Point", "X", "Y", "Opening", "Material", "RefPtX", "RefPtY"],
        ["Frame Section Properties 07 - Built Up I"] = ["SectionName", "ItemType", "ISection", "FyTF", "FyW", "FyBF", "Material", "Width", "Thick"],
        ["Frame Section Properties 08 - PCC I Girder"] = ["SectionName", "B1", "B2", "B3", "B4", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "T1", "T2", "C1"],
        ["Frame Section Properties 09 - PCC U Girder"] = ["SectionName", "B1", "B2", "B3", "B4", "B5", "B6", "D1", "D2", "D3", "D4", "D5", "D6", "D7"],
        ["Frame Section Properties 10 - Steel Joist General"] = ["SectionName", "Material", "Color", "JoistType", "Depth", "UnitWt", "AnalysisI33", "MomCap", "ShearCap", "MinSpan", "MaxSpan"],
        ["Frame Section Properties 11 - Steel Joist Data"] = ["SectionName", "SpanLength", "TLCap", "LLDeflCap", "I33"],
        ["Frame Section Properties 12 - Hybrid Steel U Section"] = ["SectionName", "WebMat", "TFMat", "BFMat", "D1", "B1", "B2", "B3", "B4", "tw", "tf", "tfb"],
        ["Frame Section Properties 17 - PCC Super-T"] = ["SectionName", "OpenFlange", "B1", "B2", "B3", "B4", "B5", "B6", "BL", "BR", "BLBRMax", "BLmax", "BRmax", "D1", "D2", "D3", "D4", "D5", "T1", "T2", "CornerType", "C1", "C2"],
        ["Frame Section Properties 18 - PCC Box Girder"] = ["SectionName", "B", "D", "tf", "tfb", "tw", "twr", "Chamfer"],
        // Link/Support Property Data as SAP2000 keeps it: the general row and each direction's row in its type's table.
        ["Link Property Definitions 01 - General"] = ["Link", "LinkType", "Mass", "Weight", "RotInert1", "RotInert2", "RotInert3", "DefLength", "DefArea", "PDM2I", "PDM2J", "PDM3I", "PDM3J", "StiffLOpt", "StiffDOpt", "StiffDFact", "Color", "GUID", "Notes"],
        ["Link Property Definitions 02 - Linear"] = ["Link", "DOF", "Fixed", "TransKE", "RotKE", "TransRotKE", "TransCE", "RotCE", "TransRotCE", "DJ"],
        ["Link Property Definitions 03 - MultiLinear"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "Point", "Force", "Displ", "Moment", "Rotation"],
        ["Link Property Definitions 04 - Damper"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "RotK", "TransC", "RotC", "CExp", "FLDamper", "FLDLength", "FLDModulus", "FLDArea"],
        ["Link Property Definitions 05 - Gap"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "RotK", "TransOpen", "RotOpen"],
        ["Link Property Definitions 06 - Hook"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "RotK", "TransOpen", "RotOpen"],
        ["Link Property Definitions 07 - Rubber Isolator"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "TransYield", "Ratio"],
        ["Link Property Definitions 08 - Sliding Isolator"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "TransC", "Slow", "Fast", "Rate", "Radius"],
        ["Link Property Definitions 09 - TC Sliding Isolator"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "TransKTens", "GapComp", "GapTens", "TransC", "SlowComp", "SlowTens", "FastComp", "FastTens", "RateComp", "RateTens", "Radius"],
        ["Link Property Definitions 10 - Plastic (Wen)"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "RotK", "TransYield", "RotYield", "Ratio", "YieldExp"],
        ["Link Property Definitions 11 - Multilinear Plastic"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "HysType", "PivotAlpha1", "PivotAlpha2", "PivotBeta1", "PivotBeta2", "PivotEta", "Point", "Force", "Displ", "Moment", "Rotation"],
        ["Link Property Definitions 12 - Triple Pendulum Isolator"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "TransC", "HeightOut", "HeightIn", "SymOut", "KOutTop", "KOutBot", "KInTop", "SlowOutTop", "SlowOutBot", "SlowInTop", "FastOutTop", "FastOutBot", "FastInTop", "RateOutTop", "RateOutBot", "RateInTop", "RadOutTop", "RadOutBot", "RadInTop", "StopOutTop", "StopOutBot", "StopInTop"],
        ["Link Property Definitions 13 - Linear Exponential Damper"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "RotK", "TransC", "RotC", "CExp", "ForceLimit", "MomLimit"],
        ["Link Property Definitions 14 - Bilinear Damper"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "RotK", "TransC", "RotC", "TransCy", "RotCy", "ForceLimit", "MomLimit", "FLDamper", "FLDLength", "FLDModulus", "FLDArea"],
        ["Link Property Definitions 15 - Friction Spring Damper"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "TransK", "RotK", "TransK1", "RotK1", "TransK2", "RotK2", "TransU0", "RotU0", "TransUs", "RotUs", "ActiveDir"],
        ["Link Property Definitions 16 - High Damping Rubber Isolator"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "Area", "EffHeight", "AddStiff", "NumHysTerms", "CoStrain1", "CoStrength1", "CoStrain2", "CoStrength2", "CoStrain3", "CoStrength3", "Resistance", "CoStrainDam", "StiffIter"],
        ["Link Property Definitions 17 - Sumitomo Damper"] = ["Link", "DOF", "Fixed", "NonLinear", "TransKE", "RotKE", "TransCE", "RotCE", "DJ", "ProdModel", "ShearArea", "Thickness", "Temp", "ExcitFreq", "RigidFluctCoeff", "DampFluctCoeff"],
        // Define > Coordinate Systems/Grids: the systems other than GLOBAL and their grid lines (GLOBAL's grids are the model's own).
        ["Coordinate Systems"] = ["Name", "Type", "X", "Y", "Z", "AboutZ", "AboutY", "AboutX"],
        ["Grid Lines"] = ["CoordSys", "AxisDir", "GridID", "XRYZCoord", "TAngle", "LineType", "LineColor", "Visible", "BubbleLoc", "AllVisible", "BubbleSize"],
        ["Hinges Def 01 - Overview"] = ["HingeName", "AssignHinge", "DOFType", "Behavior"],
        ["Hinges Def 02 - Noninteracting - Deform Control - General"] = ["HingeName", "AssignHinge", "DOFType", "Symmetric", "BeyondE", "FDType", "UseYldForce", "UseYldDispl", "FDPosForSF", "FDPosDisSF", "FDNegForSF", "FDNegDisSF", "MRPosMoSF", "MRPosRoSF", "MRNegMoSF", "MRNegRoSF", "SSPosStrsSF", "SSPosStnSF", "SSNegStrsSF", "SSNegStnSF", "MCPosMoSF", "MCPosCuSF", "MCNegMoSF", "MCNegCuSF", "LengthType", "SSAbsLen", "SSRelLen", "HysType", "PivotAlpha1", "PivotAlpha2", "PivotBeta1", "PivotBeta2", "PivotEta"],
        ["Hinges Def 03 - Noninteracting - Deform Control - Force-Deform"] = ["HingeName", "AssignHinge", "DOFType", "FDPoint", "Force", "Displ"],
        ["Hinges Def 04 - Noninteracting - Deform Control - Acceptance"] = ["HingeName", "AssignHinge", "DOFType", "ACPoint", "ACPos", "ACNeg"],
        ["Hinges Def 05 - Noninteracting - Force Control"] = ["HingeName", "AssignHinge", "DOFType", "Symmetric", "DropLoad", "MaxType", "RatioPos", "RatioNeg", "ForcePos", "ForceNeg", "MomentPos", "MomentNeg", "IOPos", "LSPos", "CPPos", "IONeg", "LSNeg", "CPNeg"],
        ["Hinges Def 06 - Interacting - Deform Control - General"] = ["HingeName", "AssignHinge", "DOFType", "FDType", "LengthType", "SSAbsLen", "SSRelLen", "SFType", "UserSFRot", "UserSFCur", "BeyondE", "PMSym", "PMMorMMSym", "NumAxForce", "NumAngle"],
        ["Hinges Def 07 - Interacting - Deform Control - Fs and Angs"] = ["HingeName", "AssignHinge", "DOFType", "AxForce", "Angle"],
        ["Hinges Def 08 - Interacting - Deform Control - Force-Deform"] = ["HingeName", "AssignHinge", "DOFType", "CurveNum", "AxForce", "Angle", "FDPoint", "MomRatio", "RCRatio"],
        ["Hinges Def 09 - Interacting - Deform Control - Acceptance"] = ["HingeName", "AssignHinge", "DOFType", "CurveNum", "AxForce", "Angle", "ACPoint", "AC"],
        ["Hinges Def 10 - Interacting - Force Control"] = ["HingeName", "AssignHinge", "DOFType", "CapFactor", "DropLoad", "IO", "LS", "CP"],
        ["Hinges Def 11 - Interacting - Interaction Surface - General"] = ["HingeName", "AssignHinge", "DOFType", "IntType", "PCurve", "PForM2M3", "SymPM", "SymMMandPMM", "NumCurves", "NumPoints", "ScaleP", "ScaleM2", "ScaleM3"],
        ["Hinges Def 12 - Interacting - Interaction Surface - Data"] = ["HingeName", "AssignHinge", "DOFType", "CurveNum", "PointNum", "P", "M2", "M3"],
        ["Hinges Def 13 - Fiber - General"] = ["HingeName", "HingeType", "AssignHinge", "DefType", "LengthType", "SSAbsLen", "SSRelLen"],
        ["Hinges Def 14 - Fiber - Fiber Data"] = ["HingeName", "AssignHinge", "Fiber", "Area", "Coord3", "Coord2", "Material"],
        ["Hinges Def 15 - Hysteresis Parameters - Concrete"] = ["HingeName", "AssignHinge", "EnrgyDegFac"],
        ["Hinges Def 16 - Hysteresis Parameters - BRB Hardening"] = ["HingeName", "AssignHinge", "IsSymmetric", "TensHardFac", "TensMaxPDef", "TensAccPDef", "TensProAcc", "CompHardFac", "CompMaxPDef", "CompAccPDef", "CompProAcc"],
        ["Hinges Def 17 - Hysteresis Parameters - Degrading"] = ["HingeName", "AssignHinge", "IsSymmetric", "Tensf0", "Tensf1", "Tensf2", "Tensx1", "Tensx2", "TensADWtFac", "Compf0", "Compf1", "Compf2", "Compx1", "Compx2", "CompADWtFac", "StiffWtFac", "LrgSmlWtFac"],
        ["Hinges Def 18a - Conc Parametric PM2M3 - Factors"] = ["HingeName", "AssignHinge", "FDType", "LengthType", "SSAbsLen", "SSRelLen", "UseYldForce", "ForcSFTens", "ForcSFComp", "ForcSFBend2", "ForcSFBend3", "DispSFTens", "DispSFComp", "DispSFBd2MR", "DispSFBd3MR", "DispSFBd2MC", "DispSFBd3MC"],
        ["Hinges Def 18b - Conc Parametric PM2M3 - Yield Surface"] = ["HingeName", "AssignHinge", "AlphaT2", "AlphaC2", "AlphaT3", "AlphaC3", "Beta", "Gamma"],
        ["Hinges Def 18c - Conc Parametric PM2M3 - Force Deformation"] = ["HingeName", "AssignHinge", "CurveShape", "StrngthLoss", "FUOverB", "FDOverBAx", "FDOverBBend", "FBalance", "DUTens", "DUComp", "DUBend2", "DUBend3", "DCBend2", "DCBend3", "DETens", "DEComp", "DEBend2", "DEBend3", "DOverCBend"],
        ["Hinges Def 18d - Conc Parametric PM2M3 - Cyclic Degradation"] = ["HingeName", "AssignHinge", "CycDegType", "BDegFact", "OneBend2", "OneBend3", "Uor1DegFact", "TwoBend2", "TwoBend3", "Cor2DegFact", "ThreeBend2", "ThreeBend3", "Dor3DegFact", "EDegFact"],
        ["Hinges Def 18e - Conc Parametric PM2M3 - Deformation Capacity"] = ["HingeName", "AssignHinge", "DefCapType", "PUpper", "PLower", "VUpper", "VLower", "IOInd", "LSInd", "CPInd", "IOatPUVUDep", "LSatPUVUDep", "CPatPUVUDep", "IOatPUVLDep", "LSatPUVLDep", "CPatPUVLDep", "IOatPLVUDep", "LSatPLVUDep", "CPatPLVUDep", "IOatPLVLDep", "LSatPLVLDep", "CPatPLVLDep", "RatioAxis2"],
        ["Hinges Def 19a - Steel Parametric PM2M3 - Factors"] = ["HingeName", "AssignHinge", "FDType", "LengthType", "SSAbsLen", "SSRelLen", "UseYldForce", "ForcSFTens", "ForcSFComp", "ForcSFBend2", "ForcSFBend3", "UseYldDispl", "DispSFTens", "DispSFComp", "DispSFBd2MR", "DispSFBd3MR", "DispSFBd2MC", "DispSFBd3MC"],
        ["Hinges Def 19b - Steel Parametric PM2M3 - Yield Surface"] = ["HingeName", "AssignHinge", "AlphaT2", "AlphaC2", "AlphaT3", "AlphaC3", "Beta", "Gamma"],
        ["Hinges Def 19c - Steel Parametric PM2M3 - Force Deformation"] = ["HingeName", "AssignHinge", "CurveShape", "StrngthLoss", "PUpper", "PLower", "FUOverB", "FDOverBAxPu", "FDOvBBendPu", "FDOverBAxPl", "FDOvBBendPl", "DUTens", "DUComp", "DUBend2", "DUBend3", "DCPuBend2", "DCPuBend3", "DCPlBend2", "DCPlBend3", "DETens", "DEComp", "DEBend2", "DEBend3", "DOvCBendPu", "DOvCBendPl"],
        ["Hinges Def 19d - Steel Parametric PM2M3 - Cyclic Degradation"] = ["HingeName", "AssignHinge", "CycDegType", "BDegFact", "OneBend2", "OneBend3", "Uor1DegFact", "TwoBend2", "TwoBend3", "Cor2DegFact", "ThreeBend2", "ThreeBend3", "Dor3DegFact", "EDegFact"],
        ["Hinges Def 19e - Steel Parametric PM2M3 - Deformation Capacity"] = ["HingeName", "AssignHinge", "DefCapType", "PUpper", "PLower", "IOInd", "LSInd", "CPInd", "IOatPUDep", "LSatPUDep", "CPatPUDep", "IOatPLDep", "LSatPLDep", "CPatPLDep", "RatioAxis2"],
        // SAP2000's Shell Section Data sub-forms, kept the same way: Concrete Shell Section Design Parameters and Time Dependent Properties.
        ["Area Section Property Design Parameters"] = ["Section", "RebarMat", "RebarOpt", "CoverTop1", "CoverTop2", "CoverBot1", "CoverBot2"],
        ["Area Section Property - Time Dependent"] = ["Section", "TypeSize", "AutoSFSize", "UserValSize"],
        // SAP2000 26.3 Define items the Engine keeps as its own table rows (Define menu read on Ahmed's PC 2026-11-27), every field.
        // Tendon Section Data's SAP2000 fields (modeling option, diameter or area, colour, notes); the Engine's own tendon
        // section (name, material, strand area) is kept as for ETABS and SAFE.
        ["Tendon Section Definitions"] = ["TendonSect", "ModelOpt", "PreType", "Material", "Specify", "Diameter", "Area", "TorsConst", "I", "AS", "Color", "TotalWt", "TotalMass", "AMod", "A2Mod", "A3Mod", "JMod", "I2Mod", "I3Mod", "MMod", "WMod", "GUID", "Notes"],
        ["Cable Section Definitions"] = ["CableSect", "Material", "Specify", "Diameter", "Area", "TorsConst", "I", "AS", "Color", "TotalWt", "TotalMass", "AMod", "A2Mod", "A3Mod", "JMod", "I2Mod", "I3Mod", "MMod", "WMod", "GUID", "Notes"],
        ["Solid Property Definitions"] = ["SolidProp", "Material", "MatAngleA", "MatAngleB", "MatAngleC", "InComp", "Color", "GUID", "Notes", "TotalWt", "TotalMass"],
        ["Link Frequency Dependent Properties 01 - General"] = ["Link", "Color", "GUID", "Notes"],
        ["Link Frequency Dependent Properties 02 - Details"] = ["Link", "DOF", "Frequency", "TransK", "RotK", "TransRotK", "TransC", "RotC", "TransRotC"],
        ["Foundation Property Definitions"] = ["Name", "FndType"],
        ["Foundation Assemblies 1 - Definitions"] = ["Name", "FndProp", "Angle", "IntermedPts", "NCtrlPts", "CtrlPt1", "CtrlPt2"],
        ["Foundation Assemblies 2 - Connection Points"] = ["Name", "PointType", "PointName"],
        ["Joint Pattern Definitions"] = ["Pattern", "AutoBridge", "BridgeObj", "BridgePType", "BridgeLPat"],
        ["Floor Vibration Excitation Sets"] = ["Name", "Type", "Ejoint", "Rjoint", "Function", "ModalCase", "Damping", "LastFreq"],
        ["Frame Property Modifier Definitions"] = ["Name", "AMod", "AS2Mod", "AS3Mod", "JMod", "I22Mod", "I33Mod", "MassMod", "WeightMod"],
        ["Cable Property Modifier Definitions"] = ["Name", "AMod", "MassMod", "WeightMod"],
        ["Pushover Parameters - ATC40"] = ["ParamName", "PlotAxes", "DemandFrom", "Function", "FuncSF", "Ca", "Cv", "AddDamp", "StructType", "BetaZero1", "Kappa1", "BetaZero2", "Kappa2", "PlotCap", "PlotDemand", "PlotLocus", "PlotPeriod", "CapColor", "DemandColor", "LocusColor", "PeriodColor", "Damp1", "Damp2", "Damp3", "Damp4", "Period1", "Period2", "Period3", "Period4"],
        ["Pushover Parameters - FEMA356"] = ["ParamName", "Beff", "DemandFrom", "Function", "FuncSF", "FuncTs", "Ss", "S1", "SiteClass", "UserC2", "C2", "UserC3", "C3", "UserCm", "Cm", "PlotCap", "PlotBiLin", "CapColor", "BiLinColor"],
        ["Pushover Parameters - ASCE 41-13 NSP"] = ["ParamName", "Beff", "DemandFrom", "Function", "FuncSF", "FuncTs", "Ss", "S1", "SiteClass", "Tl", "IncludeSSI", "UserC2", "C2", "UserCm", "Cm", "PlotCap", "PlotBiLin", "CapColor", "BiLinColor"],
        ["Pushover Parameters - FEMA440 Equivalent Linearization"] = ["ParamName", "PlotAxes", "DemandFrom", "Function", "FuncSF", "Ca", "Cv", "IncludeSSI", "AddDamp", "UserBeff", "A", "B", "C", "D", "E", "F", "UserTeff", "G", "H", "I", "J", "K", "L", "PlotCap", "PlotDemand", "PlotLocus", "PlotPeriod", "CapColor", "DemandColor", "LocusColor", "PeriodColor", "DuctRatio1", "DuctRatio2", "DuctRatio3", "DuctRatio4", "Period1", "Period2", "Period3", "Period4"],
        ["Pushover Parameters - Force Displacement"] = ["ParamName", "CapColor"],
        ["Named Sets - Pushover"] = ["PONamedSet", "Case", "PlotType", "ParamName"],
    };

    /// <summary>Each table and its fields with the names the program shows; the first field is the definition's name.</summary>
    public static readonly IReadOnlyDictionary<string, (string Key, string Label)[]> Tables = WithSap(new Dictionary<string, (string, string)[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Deck Property Definitions"] = [("Name", "Name"), ("DeckType", "Deck Type"), ("MaterialSlb", "Slab Material"), ("MaterialDck", "Deck Material"), ("SlabDepth", "Slab Depth"), ("RibDepth", "Rib Depth"), ("RibWidthTop", "Rib Width Top"), ("RibWidthBot", "Rib Width Bottom"), ("RibSpacing", "Rib Spacing"), ("DeckShrThk", "Deck Shear Thickness"), ("DeckUnitWt", "Deck Unit Weight"), ("StudDiam", "Shear Stud Diameter"), ("StudHeight", "Shear Stud Height"), ("StudFu", "Shear Stud Tensile Strength"), ("f11Mod", "f11 Modifier"), ("f22Mod", "f22 Modifier"), ("f12Mod", "f12 Modifier"), ("m11Mod", "m11 Modifier"), ("m22Mod", "m22 Modifier"), ("m12Mod", "m12 Modifier"), ("v13Mod", "v13 Modifier"), ("v23Mod", "v23 Modifier"), ("MMod", "Mass Modifier"), ("WMod", "Weight Modifier"), ("Color", "Color"), ("Notes", "Notes")],
        ["Generalized Displacement Definitions"] = [("Name", "Name"), ("Type", "Type"), ("PointName", "PointName"), ("TransU1SF", "Translational U1 SF"), ("TransU2SF", "Translational U2 SF"), ("TransU3SF", "Translational U3 SF"), ("TransR1SF", "Translational R1 SF"), ("TransR2SF", "Translational R2 SF"), ("TransR3SF", "Translational R3 SF"), ("RotU1SF", "Rotational U1 SF"), ("RotU2SF", "Rotational U2 SF"), ("RotU3SF", "Rotational U3 SF"), ("RotR1SF", "Rotational R1 SF"), ("RotR2SF", "Rotational R2 SF"), ("RotR3SF", "Rotational R3 SF")],
        ["Shell Uniform Load Sets"] = [("Name", "Name"), ("LoadPattern", "Load Pattern"), ("LoadValue", "Load Value")],
        ["Auto Construction Sequence Definition"] = [("Name", "Name"), ("IsActive", "Is Active"), ("GeoNonlin", "Geometric Nonlinearity"), ("NumStories", "Number Stories"), ("Group", "Exclude Group"), ("LoadPattern", "Load Pattern"), ("ScaleFactor", "ScaleFactor"), ("ReplaceDead", "Replace Dead")],
        ["Walking Vibration Definitions"] = [("Name", "Name"), ("Story", "Story"), ("X", "X"), ("Y", "Y"), ("ModalOpt", "Modal Case Option"), ("ModePerStep", "Modes per Step"), ("ModalCase", "Modal Case"), ("DampRatio", "Damping Ratio"), ("Weight", "Weight"), ("LoadFactor", "Load Factor"), ("Frequency", "Frequency"), ("Speed", "Speed"), ("Duration", "Duration"), ("ThreshOpt", "Threshold Option"), ("UserThresh", "User Threshold"), ("Color", "Color"), ("Notes", "Notes")],
        ["Panel Zone Property Definitions"] = [("Name", "Name"), ("Source", "Source"), ("ActiveDir", "Active Direction"), ("OffsetRigid", "Offsets Rigid?"), ("DblPlThick", "Doubler Plate Thickness"), ("AxLoadRatio", "Axial Load Ratio"), ("SpringMajor", "Spring Major Stiffness"), ("SpringMinor", "Spring Minor Stiffness"), ("LinkProp", "Link Property"), ("Connect", "Connectivity"), ("AxisSource", "Local Axis Source"), ("AxialDir", "User Axial Direction"), ("Axis2Angle", "User Axis 2 Angle"), ("AcceptType", "Acceptance Type"), ("UserAccType", "User Acceptance Type"), ("UserIOMomMa", "User IO Moment Major"), ("UserLSMomMa", "User LS Moment Major"), ("UserCPMomMa", "User CP Moment Major"), ("UserIOMomMi", "User IO Moment Minor"), ("UserLSMomMi", "User LS Moment Minor"), ("UserCPMomMi", "User CP Moment Minor"), ("UserIORotMa", "User IO Rotation Major"), ("UserLSRotMa", "User LS Rotation Major"), ("UserCPRotMa", "User CP Rotation Major"), ("UserIORotMi", "User IO Rotation Minor"), ("UserLSRotMi", "User LS Rotation Minor"), ("UserCPRotMi", "User CP Rotation Minor"), ("Notes", "Notes")],
        ["Panel Zone Property Definitions - User Nonlinear Data"] = [("Name", "Name"), ("Direction", "Direction"), ("Type", "Type"), ("LinearStiff", "Linear Stiffness"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("Point", "Point"), ("Force", "Moment"), ("Displ", "Rotation"), ("UseYldForce", "Use Yield Moment?"), ("UseYldDispl", "Use Yield Rotation?"), ("FDPosForSF", "Moment SF"), ("FDPosDispSF", "Rotation SF"), ("BeyondE", "Beyond E"), ("HysType", "Hysteresis Type"), ("PivotAlpha1", "Pivot Alpha1"), ("PivotAlpha2", "Pivot Alpha2"), ("PivotBeta1", "Pivot Beta1"), ("PivotBeta2", "Pivot Beta2"), ("PivotEta", "Pivot Eta"), ("ConcreteFact", "Energy Degradation Factor"), ("BRBTensHF", "BRB Hardening Factor"), ("BRBTensMPD", "BRB Max Plastic Deformation Ratio"), ("BRBTensAPD", "BRB Accumulated Plastic Deformation Ratio"), ("BRBTensPro", "BRB Proportion"), ("DegTensf0", "Degrading f0"), ("DegTensf1", "Degrading f1"), ("DegTensf2", "Degrading f2"), ("DegTensx1", "Degrading x1"), ("DegTensx2", "Degrading x2"), ("DegTensADWF", "Degrading Accumulation Deformation Factor"), ("DegStiff", "Degrading Stiffness Weighting Factor"), ("DegLargeSml", "Degrading Larger-Smaller Weighting Factor")],
        ["Slab Property Definitions - Layers"] = [("Name", "Name"), ("LayerName", "Layer Name"), ("Distance", "Distance"), ("Thickness", "Thickness"), ("ModelType", "Modeling Type"), ("NumIntPts", "Number Integration Points"), ("Material", "Material"), ("MatAng", "Material Angle"), ("Behavior", "Material Behavior"), ("S11", "Material S11"), ("S22", "Material S22"), ("S12", "Material S12")],
        ["Wall Property Definitions - Layers"] = [("Name", "Name"), ("LayerName", "Layer Name"), ("Distance", "Distance"), ("Thickness", "Thickness"), ("ModelType", "Modeling Type"), ("NumIntPts", "Number Integration Points"), ("Material", "Material"), ("MatAng", "Material Angle"), ("Behavior", "Material Behavior"), ("S11", "Material S11"), ("S22", "Material S22"), ("S12", "Material S12")],
        ["Wall Property Definitions - Auto Select List"] = [("Name", "Name"), ("IncludedPrp", "Included Property"), ("StartProp", "Starting Property"), ("Color", "Color"), ("Notes", "Notes")],
        ["Frame Release Definitions"] = [("Name", "Name"), ("PI", "PI"), ("PJ", "PJ"), ("V2I", "V2I"), ("V2J", "V2J"), ("V3I", "V3I"), ("V3J", "V3J"), ("TI", "TI"), ("TJ", "TJ"), ("M2I", "M2I"), ("M2J", "M2J"), ("M3I", "M3I"), ("M3J", "M3J"), ("PISpring", "PI Spring"), ("PJSpring", "PJ Spring"), ("V2ISpring", "V2I Spring"), ("V2JSpring", "V2J Spring"), ("V3ISpring", "V3I Spring"), ("V3JSpring", "V3J Spring"), ("TISpring", "TI Spring"), ("TJSpring", "TJ Spring"), ("M2ISpring", "M2I Spring"), ("M2JSpring", "M2J Spring"), ("M3ISpring", "M3I Spring"), ("M3JSpring", "M3J Spring")],
        ["Hinge Definitions - Axial P"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("FDType", "Force Displacement Type"), ("Point", "Point"), ("Force", "Force"), ("Displ", "Displacement"), ("UseYldForce", "Use Yield Force?"), ("UseYldDispl", "Use Yield Displacement?"), ("FDPosForSF", "Positive Force SF"), ("FDPosDispSF", "Positive Displacement SF"), ("FDNegForSF", "Negative Force SF"), ("FDNegDispSF", "Negative Displacement SF"), ("SSPosForSF", "Positive Stress SF"), ("SSPosDispSF", "Positive Strain SF"), ("SSNegForSF", "Negative Stress SF"), ("SSNegDispSF", "Negative Strain SF"), ("BeyondE", "Beyond E"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP"), ("HysType", "Hysteresis Type"), ("PivotAlpha1", "Pivot Alpha1"), ("PivotAlpha2", "Pivot Alpha2"), ("PivotBeta1", "Pivot Beta1"), ("PivotBeta2", "Pivot Beta2"), ("PivotEta", "Pivot Eta"), ("ConcreteFact", "Energy Degradation Factor"), ("BRBSym", "BRB Hardening Symmetric?"), ("BRBTensHF", "BRB Tension Hardening Factor"), ("BRBTensMPD", "BRB Tension Max Plastic Deformation Ratio"), ("BRBTensAPD", "BRB Tension Accumulated Plastic Deformation Ratio"), ("BRBTensPro", "BRB Tension Proportion"), ("BRBCompHF", "BRB Compression Hardening Factor"), ("BRBCompMPD", "BRB Compression Max Plastic Deformation Ratio"), ("BRBCompAPD", "BRB Compression Accumulated Plastic Deformation Ratio"), ("BRBCompPro", "BRB Compression Proportion"), ("DegSym", "Degrading Symmetric?"), ("DegTensf0", "Degrading Tension f0"), ("DegTensf1", "Degrading Tension f1"), ("DegTensf2", "Degrading Tension f2"), ("DegTensx1", "Degrading Tension x1"), ("DegTensx2", "Degrading Tension x2"), ("DegTensADWF", "Degrading Tension Accumulation Deformation Factor"), ("DegCompf0", "Degrading Compression f0"), ("DegCompf1", "Degrading Compression f1"), ("DegCompf2", "Degrading Compression f2"), ("DegCompx1", "Degrading Compression x1"), ("DegCompx2", "Degrading Compression x2"), ("DegCompADWF", "Degrading Compression Accumulation Deformation Factor"), ("DegStiff", "Degrading Stiffness Weighting Factor"), ("DegLargeSml", "Degrading Larger-Smaller Weighting Factor")],
        ["Hinge Definitions - Fiber PM2M3"] = [("Name", "Name"), ("FiberOpt", "Fiber Definition Option"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("FiberNum", "Fiber Number"), ("Area", "Fiber Area"), ("Coord3", "Fiber Coordinate 3"), ("Coord2", "Fiber Coordinate 2"), ("Material", "Fiber Material"), ("SSCurve", "Material Stress-Strain Curve")],
        ["Hinge Definitions - Fiber PM3"] = [("Name", "Name"), ("FiberNum", "Fiber Number"), ("Area", "Fiber Area"), ("Coord2", "Fiber Coordinate 2"), ("Material", "Fiber Material"), ("SSCurve", "Material Stress-Strain Curve")],
        ["Hinge Definitions - Force Controlled - Axial P"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("MaxType", "Maximum Allowed Force Type"), ("RatioPos", "Positive Proportion"), ("RatioNeg", "Negative Proportion"), ("ForcePos", "Positive Force"), ("ForceNeg", "Negative Force"), ("DropLoad", "Drops Load?"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP")],
        ["Hinge Definitions - Force Controlled - Interacting M2M3"] = [("Name", "Name"), ("CapFactor", "Capacity Factor"), ("DropLoad", "Drops Load?"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("IntSurfType", "Interaction Surface Type"), ("AxialForce", "Axial Force"), ("SymType", "Interaction Symmetry Type"), ("M2SF", "M2 Scale Factor"), ("M3SF", "M3 Scale Factor"), ("Point", "Point"), ("M2OverSF", "M2"), ("M3OverSF", "M3")],
        ["Hinge Definitions - Force Controlled - Interacting PM2"] = [("Name", "Name"), ("CapFactor", "Capacity Factor"), ("DropLoad", "Drops Load?"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("IntSurfType", "Interaction Surface Type"), ("AxLoadDisp", "Axial Load Displacement Type"), ("IsSymmetric", "Is Interaction Symmetric?"), ("NumPoints", "Number Points"), ("PSF", "P Scale Factor"), ("M2SF", "M2 Scale Factor"), ("POvSFFirst", "P First"), ("M2OvSFFirst", "M2 First"), ("POvSFLast", "P Last"), ("M2OvSFLast", "M2 Last"), ("Curve", "Curve"), ("Point", "Point"), ("POverSF", "P"), ("M2OverSF", "M2")],
        ["Hinge Definitions - Force Controlled - Interacting PM2M3"] = [("Name", "Name"), ("CapFactor", "Capacity Factor"), ("DropLoad", "Drops Load?"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("IntSurfType", "Interaction Surface Type"), ("AxLoadDisp", "Axial Load Displacement Type"), ("SymType", "Interaction Symmetry Type"), ("NumCurves", "Number Curves"), ("NumPoints", "Number Points"), ("PSF", "P Scale Factor"), ("M2SF", "M2 Scale Factor"), ("M3SF", "M3 Scale Factor"), ("POvSFFirst", "P First"), ("M2OvSFFirst", "M2 First"), ("M3OvSFFirst", "M3 First"), ("POvSFLast", "P Last"), ("M2OvSFLast", "M2 Last"), ("M3OvSFLast", "M3 Last"), ("Curve", "Curve"), ("Point", "Point"), ("POverSF", "P"), ("M2OverSF", "M2"), ("M3OverSF", "M3")],
        ["Hinge Definitions - Force Controlled - Interacting PM3"] = [("Name", "Name"), ("CapFactor", "Capacity Factor"), ("DropLoad", "Drops Load?"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("IntSurfType", "Interaction Surface Type"), ("AxLoadDisp", "Axial Load Displacement Type"), ("IsSymmetric", "Is Interaction Symmetric?"), ("NumPoints", "Number Points"), ("PSF", "P Scale Factor"), ("M3SF", "M3 Scale Factor"), ("POvSFFirst", "P First"), ("M3OvSFFirst", "M3 First"), ("POvSFLast", "P Last"), ("M3OvSFLast", "M3 Last"), ("Curve", "Curve"), ("Point", "Point"), ("POverSF", "P"), ("M3OverSF", "M3")],
        ["Hinge Definitions - Force Controlled - Moment M2"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("MaxType", "Maximum Allowed Moment Type"), ("RatioPos", "Positive Proportion"), ("RatioNeg", "Negative Proportion"), ("ForcePos", "Positive Moment"), ("ForceNeg", "Negative Moment"), ("DropLoad", "Drops Load?"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP")],
        ["Hinge Definitions - Force Controlled - Moment M3"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("MaxType", "Maximum Allowed Moment Type"), ("RatioPos", "Positive Proportion"), ("RatioNeg", "Negative Proportion"), ("ForcePos", "Positive Moment"), ("ForceNeg", "Negative Moment"), ("DropLoad", "Drops Load?"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP")],
        ["Hinge Definitions - Force Controlled - Shear V2"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("MaxType", "Maximum Allowed Force Type"), ("RatioPos", "Positive Proportion"), ("RatioNeg", "Negative Proportion"), ("ForcePos", "Positive Force"), ("ForceNeg", "Negative Force"), ("DropLoad", "Drops Load?"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP")],
        ["Hinge Definitions - Force Controlled - Shear V3"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("MaxType", "Maximum Allowed Force Type"), ("RatioPos", "Positive Proportion"), ("RatioNeg", "Negative Proportion"), ("ForcePos", "Positive Force"), ("ForceNeg", "Negative Force"), ("DropLoad", "Drops Load?"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP")],
        ["Hinge Definitions - Force Controlled - Torsion T"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("ForcePos", "Positive Moment"), ("ForceNeg", "Negative Moment"), ("DropLoad", "Drops Load?"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP")],
        ["Hinge Definitions - Interacting M2M3"] = [("Name", "Name"), ("FDType", "Moment Rotation Type"), ("MRSymType", "MR Symmetry Type"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("NumAngles", "Number MR Angles"), ("MRAngle", "MR Angle"), ("MRCurveNum", "MR Curve Number"), ("CurveAngle", "MR Curve Angle"), ("MRPoint", "MR Point"), ("Moment", "Moment"), ("Rotation", "Rotation"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("BeyondE", "Beyond E"), ("SFType", "Rotation SF Type"), ("UserSFRot", "User SF for Rotation"), ("UserSFCur", "User SF for Curvature"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IntSurfType", "Interaction Surface Type"), ("AxialForce", "Axial Force"), ("SymType", "Interaction Symmetry Type"), ("M2SF", "M2 Scale Factor"), ("M3SF", "M3 Scale Factor"), ("Point", "Point"), ("M2OverSF", "M2"), ("M3OverSF", "M3")],
        ["Hinge Definitions - Interacting PM2"] = [("Name", "Name"), ("FDType", "Moment Rotation Type"), ("IsMRSym", "Is Moment-Rotation Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("NumForces", "Number MR Forces"), ("NumAngles", "Number MR Angles"), ("MRForce", "MR Axial Force"), ("MRAngle", "MR Angle"), ("MRCurveNum", "MR Curve Number"), ("CurveForce", "MR Curve Force"), ("CurveAngle", "MR Curve Angle"), ("MRPoint", "MR Point"), ("Moment", "Moment"), ("Rotation", "Rotation"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("BeyondE", "Beyond E"), ("SFType", "Rotation SF Type"), ("UserSFRot", "User SF for Rotation"), ("UserSFCur", "User SF for Curvature"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IntSurfType", "Interaction Surface Type"), ("AxLoadDisp", "Axial Load Displacement Type"), ("IsSymmetric", "Is Interaction Symmetric?"), ("NumPoints", "Number Points"), ("PSF", "P Scale Factor"), ("M2SF", "M2 Scale Factor"), ("POvSFFirst", "P First"), ("M2OvSFFirst", "M2 First"), ("POvSFLast", "P Last"), ("M2OvSFLast", "M2 Last"), ("Curve", "Curve"), ("Point", "Point"), ("POverSF", "P"), ("M2OverSF", "M2")],
        ["Hinge Definitions - Interacting PM2M3"] = [("Name", "Name"), ("FDType", "Moment Rotation Type"), ("MRSymType", "MR Symmetry Type"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("NumForces", "Number MR Forces"), ("NumAngles", "Number MR Angles"), ("MRForce", "MR Axial Force"), ("MRAngle", "MR Angle"), ("MRCurveNum", "MR Curve Number"), ("CurveForce", "MR Curve Force"), ("CurveAngle", "MR Curve Angle"), ("MRPoint", "MR Point"), ("Moment", "Moment"), ("Rotation", "Rotation"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("BeyondE", "Beyond E"), ("SFType", "Rotation SF Type"), ("UserSFRot", "User SF for Rotation"), ("UserSFCur", "User SF for Curvature"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IntSurfType", "Interaction Surface Type"), ("AxLoadDisp", "Axial Load Displacement Type"), ("SymType", "Interaction Symmetry Type"), ("NumCurves", "Number Curves"), ("NumPoints", "Number Points"), ("PSF", "P Scale Factor"), ("M2SF", "M2 Scale Factor"), ("M3SF", "M3 Scale Factor"), ("POvSFFirst", "P First"), ("M2OvSFFirst", "M2 First"), ("M3OvSFFirst", "M3 First"), ("POvSFLast", "P Last"), ("M2OvSFLast", "M2 Last"), ("M3OvSFLast", "M3 Last"), ("Curve", "Curve"), ("Point", "Point"), ("POverSF", "P"), ("M2OverSF", "M2"), ("M3OverSF", "M3")],
        ["Hinge Definitions - Interacting PM3"] = [("Name", "Name"), ("FDType", "Moment Rotation Type"), ("IsMRSym", "Is Moment-Rotation Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("NumForces", "Number MR Forces"), ("NumAngles", "Number MR Angles"), ("MRForce", "MR Axial Force"), ("MRAngle", "MR Angle"), ("MRCurveNum", "MR Curve Number"), ("CurveForce", "MR Curve Force"), ("CurveAngle", "MR Curve Angle"), ("MRPoint", "MR Point"), ("Moment", "Moment"), ("Rotation", "Rotation"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("BeyondE", "Beyond E"), ("SFType", "Rotation SF Type"), ("UserSFRot", "User SF for Rotation"), ("UserSFCur", "User SF for Curvature"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IntSurfType", "Interaction Surface Type"), ("AxLoadDisp", "Axial Load Displacement Type"), ("IsSymmetric", "Is Interaction Symmetric?"), ("NumPoints", "Number Points"), ("PSF", "P Scale Factor"), ("M3SF", "M3 Scale Factor"), ("POvSFFirst", "P First"), ("M3OvSFFirst", "M3 First"), ("POvSFLast", "P Last"), ("M3OvSFLast", "M3 Last"), ("Curve", "Curve"), ("Point", "Point"), ("POverSF", "P"), ("M3OverSF", "M3")],
        ["Hinge Definitions - Moment M2"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("FDType", "Moment Rotation Type"), ("Point", "Point"), ("Force", "Moment"), ("Displ", "Rotation"), ("UseYldForce", "Use Yield Moment?"), ("UseYldDispl", "Use Yield Rotation?"), ("FDPosForSF", "Positive Moment SF"), ("FDPosDispSF", "Positive Rotation SF"), ("FDNegForSF", "Negative Moment SF"), ("FDNegDispSF", "Negative Rotation SF"), ("SSPosDispSF", "Positive Curvature SF"), ("SSNegDispSF", "Negative Curvature SF"), ("BeyondE", "Beyond E"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP"), ("HysType", "Hysteresis Type"), ("PivotAlpha1", "Pivot Alpha1"), ("PivotAlpha2", "Pivot Alpha2"), ("PivotBeta1", "Pivot Beta1"), ("PivotBeta2", "Pivot Beta2"), ("PivotEta", "Pivot Eta"), ("ConcreteFact", "Energy Degradation Factor"), ("BRBSym", "BRB Hardening Symmetric?"), ("BRBTensHF", "BRB Tension Hardening Factor"), ("BRBTensMPD", "BRB Tension Max Plastic Deformation Ratio"), ("BRBTensAPD", "BRB Tension Accumulated Plastic Deformation Ratio"), ("BRBTensPro", "BRB Tension Proportion"), ("BRBCompHF", "BRB Compression Hardening Factor"), ("BRBCompMPD", "BRB Compression Max Plastic Deformation Ratio"), ("BRBCompAPD", "BRB Compression Accumulated Plastic Deformation Ratio"), ("BRBCompPro", "BRB Compression Proportion"), ("DegSym", "Degrading Symmetric?"), ("DegTensf0", "Degrading Tension f0"), ("DegTensf1", "Degrading Tension f1"), ("DegTensf2", "Degrading Tension f2"), ("DegTensx1", "Degrading Tension x1"), ("DegTensx2", "Degrading Tension x2"), ("DegTensADWF", "Degrading Tension Accumulation Deformation Factor"), ("DegCompf0", "Degrading Compression f0"), ("DegCompf1", "Degrading Compression f1"), ("DegCompf2", "Degrading Compression f2"), ("DegCompx1", "Degrading Compression x1"), ("DegCompx2", "Degrading Compression x2"), ("DegCompADWF", "Degrading Compression Accumulation Deformation Factor"), ("DegStiff", "Degrading Stiffness Weighting Factor"), ("DegLargeSml", "Degrading Larger-Smaller Weighting Factor")],
        ["Hinge Definitions - Moment M3"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("FDType", "Moment Rotation Type"), ("Point", "Point"), ("Force", "Moment"), ("Displ", "Rotation"), ("UseYldForce", "Use Yield Moment?"), ("UseYldDispl", "Use Yield Rotation?"), ("FDPosForSF", "Positive Moment SF"), ("FDPosDispSF", "Positive Rotation SF"), ("FDNegForSF", "Negative Moment SF"), ("FDNegDispSF", "Negative Rotation SF"), ("SSPosDispSF", "Positive Curvature SF"), ("SSNegDispSF", "Negative Curvature SF"), ("BeyondE", "Beyond E"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP"), ("HysType", "Hysteresis Type"), ("PivotAlpha1", "Pivot Alpha1"), ("PivotAlpha2", "Pivot Alpha2"), ("PivotBeta1", "Pivot Beta1"), ("PivotBeta2", "Pivot Beta2"), ("PivotEta", "Pivot Eta"), ("ConcreteFact", "Energy Degradation Factor"), ("BRBSym", "BRB Hardening Symmetric?"), ("BRBTensHF", "BRB Tension Hardening Factor"), ("BRBTensMPD", "BRB Tension Max Plastic Deformation Ratio"), ("BRBTensAPD", "BRB Tension Accumulated Plastic Deformation Ratio"), ("BRBTensPro", "BRB Tension Proportion"), ("BRBCompHF", "BRB Compression Hardening Factor"), ("BRBCompMPD", "BRB Compression Max Plastic Deformation Ratio"), ("BRBCompAPD", "BRB Compression Accumulated Plastic Deformation Ratio"), ("BRBCompPro", "BRB Compression Proportion"), ("DegSym", "Degrading Symmetric?"), ("DegTensf0", "Degrading Tension f0"), ("DegTensf1", "Degrading Tension f1"), ("DegTensf2", "Degrading Tension f2"), ("DegTensx1", "Degrading Tension x1"), ("DegTensx2", "Degrading Tension x2"), ("DegTensADWF", "Degrading Tension Accumulation Deformation Factor"), ("DegCompf0", "Degrading Compression f0"), ("DegCompf1", "Degrading Compression f1"), ("DegCompf2", "Degrading Compression f2"), ("DegCompx1", "Degrading Compression x1"), ("DegCompx2", "Degrading Compression x2"), ("DegCompADWF", "Degrading Compression Accumulation Deformation Factor"), ("DegStiff", "Degrading Stiffness Weighting Factor"), ("DegLargeSml", "Degrading Larger-Smaller Weighting Factor")],
        ["Hinge Definitions - Parametric - Concrete PM2M3"] = [("Name", "Name"), ("FDType", "Moment Rotation Type"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("UseYldForce", "Use Yield Forces?"), ("FSFTens", "Force SF Tension"), ("FSFComp", "Force SF Compression"), ("FSFBend2", "Force SF Bending 2"), ("FSFBend3", "Force SF Bending 3"), ("MRDSFTens", "MR Def SF Tension"), ("MRDSFComp", "MR Def SF Compression"), ("MRDSFBend2", "MR Def SF Bending 2"), ("MRDSFBend3", "MR Def SF Bending 3"), ("MCDSFTens", "MC Def SF Tension"), ("MCDSFComp", "MC Def SF Compression"), ("MCDSFBend2", "MC Def SF Bending 2"), ("MCDSFBend3", "MC Def SF Bending 3"), ("AlphaT2", "AlphaT2"), ("AlphaC2", "AlphaC2"), ("AlphaT3", "AlphaT3"), ("AlphaC3", "AlphaC3"), ("Beta", "Beta"), ("Gamma", "Gamma"), ("ShapeType", "Shape Type"), ("IsStrLoss", "Is Strength Loss?"), ("FUOverB", "Ratio U Over B"), ("FDOverBAx", "Ratio D Over B Axial"), ("FDOverBBend", "Ratio D Over B Bending"), ("AxFBal", "Axial Force Ratio"), ("DUTens", "Deformation U Tension"), ("DUComp", "Deformation U Compression"), ("DUBend2", "Deformation U Bending 2"), ("DUBend3", "Deformation U Bending 3"), ("DCBend2", "Deformation C Bending 2"), ("DCBend3", "Deformation C Bending 3"), ("DETens", "Deformation E Tension"), ("DEComp", "Deformation E Compression"), ("DEBend2", "Deformation E Bending 2"), ("DEBend3", "Deformation E Bending 3"), ("DOverCBend", "Deformation D Over C"), ("DegOption", "Degradation Option"), ("DegBEnergy", "Degradation Pt B Energy"), ("Deg1Bend2", "Degradation Pt 1 Bending 2"), ("Deg1Bend3", "Degradation Pt 1 Bending 3"), ("DegU1Energy", "Degradation Pt U1 Energy"), ("Deg2Bend2", "Degradation Pt 2 Bending 2"), ("Deg2Bend3", "Degradation Pt 2 Bending 3"), ("DegC2Energy", "Degradation Pt C2 Energy"), ("Deg3Bend2", "Degradation Pt 3 Bending 2"), ("Deg3Bend3", "Degradation Pt 3 Bending 3"), ("DegD3Energy", "Degradation Pt D3 Energy"), ("DegEEnergy", "Degradation Pt E Energy"), ("DefOption", "Deformation Capacity Option"), ("PU", "PU"), ("PL", "PL"), ("VU", "VU"), ("VL", "VL"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("IOAtPUVU", "IO at PU and VU"), ("LSAtPUVU", "LS at PU and VU"), ("CPAtPUVU", "CP at PU and VU"), ("IOAtPUVL", "IO at PU and VL"), ("LSAtPUVL", "LS at PU and VL"), ("CPAtPUVL", "CP at PU and VL"), ("IOAtPLVU", "IO at PL and VU"), ("LSAtPLVU", "LS at PL and VU"), ("CPAtPLVU", "CP at PL and VU"), ("IOAtPLVL", "IO at PL and VL"), ("LSAtPLVL", "LS at PL and VL"), ("CPAtPLVL", "CP at PL and VL"), ("CapRatio", "Capacity Ratio")],
        ["Hinge Definitions - Parametric - Steel PM2M3"] = [("Name", "Name"), ("FDType", "Moment Rotation Type"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("UseYldForce", "Use Yield Forces?"), ("FSFTens", "Force SF Tension"), ("FSFComp", "Force SF Compression"), ("FSFBend2", "Force SF Bending 2"), ("FSFBend3", "Force SF Bending 3"), ("UseYldDef", "Use Yield Deformations?"), ("MRDSFTens", "MR Def SF Tension"), ("MRDSFComp", "MR Def SF Compression"), ("MRDSFBend2", "MR Def SF Bending 2"), ("MRDSFBend3", "MR Def SF Bending 3"), ("MCDSFTens", "MC Def SF Tension"), ("MCDSFComp", "MC Def SF Compression"), ("MCDSFBend2", "MC Def SF Bending 2"), ("MCDSFBend3", "MC Def SF Bending 3"), ("AlphaT2", "AlphaT2"), ("AlphaC2", "AlphaC2"), ("AlphaT3", "AlphaT3"), ("AlphaC3", "AlphaC3"), ("Beta", "Beta"), ("Gamma", "Gamma"), ("ShapeType", "Shape Type"), ("StrLossType", "Strength Loss Type"), ("PUOverSF", "PU Over SF"), ("PLOverSF", "PL Over SF"), ("FUOverB", "Ratio U Over B"), ("FDOverBAxPU", "Ratio D Over B Axial at PU"), ("FDOvBBendPU", "Ratio D Over B Bending at PU"), ("FDOverBAxPL", "Ratio D Over B Axial at PL"), ("FDOvBBendPL", "Ratio D Over B Bending at PL"), ("DUTens", "Deformation U Tension"), ("DUComp", "Deformation U Compression"), ("DUBend2", "Deformation U Bending 2"), ("DUBend3", "Deformation U Bending 3"), ("DCBend2PU", "Deformation C Bending 2 at PU"), ("DCBend3PU", "Deformation C Bending 3 at PU"), ("DCBend2PL", "Deformation C Bending 2 at PL"), ("DCBend3PL", "Deformation C Bending 3 at PL"), ("DETens", "Deformation E Tension"), ("DEComp", "Deformation E Compression"), ("DEBend2", "Deformation E Bending 2"), ("DEBend3", "Deformation E Bending 3"), ("DOvCBendPU", "Deformation D Over C at PU"), ("DOvCBendPL", "Deformation D Over C at PL"), ("DegOption", "Degradation Option"), ("DegBEnergy", "Degradation Pt B Energy"), ("Deg1Bend2", "Degradation Pt 1 Bending 2"), ("Deg1Bend3", "Degradation Pt 1 Bending 3"), ("DegU1Energy", "Degradation Pt U1 Energy"), ("Deg2Bend2", "Degradation Pt 2 Bending 2"), ("Deg2Bend3", "Degradation Pt 2 Bending 3"), ("DegC2Energy", "Degradation Pt C2 Energy"), ("Deg3Bend2", "Degradation Pt 3 Bending 2"), ("Deg3Bend3", "Degradation Pt 3 Bending 3"), ("DegD3Energy", "Degradation Pt D3 Energy"), ("DegEEnergy", "Degradation Pt E Energy"), ("DefOption", "Deformation Capacity Option"), ("PU", "PU"), ("PL", "PL"), ("IO", "IO"), ("LS", "LS"), ("CP", "CP"), ("IOAtPU", "IO at PU"), ("LSAtPU", "LS at PU"), ("CPAtPU", "CP at PU"), ("IOAtPL", "IO at PL"), ("LSAtPL", "LS at PL"), ("CPAtPL", "CP at PL"), ("CapRatio", "Capacity Ratio")],
        ["Hinge Definitions - Shear V2"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("FDType", "Force Displacement Type"), ("Point", "Point"), ("Force", "Force"), ("Displ", "Displacement"), ("UseYldForce", "Use Yield Force?"), ("UseYldDispl", "Use Yield Displacement?"), ("FDPosForSF", "Positive Force SF"), ("FDPosDispSF", "Positive Displacement SF"), ("FDNegForSF", "Negative Force SF"), ("FDNegDispSF", "Negative Displacement SF"), ("SSPosForSF", "Positive Stress SF"), ("SSPosDispSF", "Positive Strain SF"), ("SSNegForSF", "Negative Stress SF"), ("SSNegDispSF", "Negative Strain SF"), ("BeyondE", "Beyond E"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP"), ("HysType", "Hysteresis Type"), ("PivotAlpha1", "Pivot Alpha1"), ("PivotAlpha2", "Pivot Alpha2"), ("PivotBeta1", "Pivot Beta1"), ("PivotBeta2", "Pivot Beta2"), ("PivotEta", "Pivot Eta"), ("ConcreteFact", "Energy Degradation Factor"), ("BRBSym", "BRB Hardening Symmetric?"), ("BRBTensHF", "BRB Tension Hardening Factor"), ("BRBTensMPD", "BRB Tension Max Plastic Deformation Ratio"), ("BRBTensAPD", "BRB Tension Accumulated Plastic Deformation Ratio"), ("BRBTensPro", "BRB Tension Proportion"), ("BRBCompHF", "BRB Compression Hardening Factor"), ("BRBCompMPD", "BRB Compression Max Plastic Deformation Ratio"), ("BRBCompAPD", "BRB Compression Accumulated Plastic Deformation Ratio"), ("BRBCompPro", "BRB Compression Proportion"), ("DegSym", "Degrading Symmetric?"), ("DegTensf0", "Degrading Tension f0"), ("DegTensf1", "Degrading Tension f1"), ("DegTensf2", "Degrading Tension f2"), ("DegTensx1", "Degrading Tension x1"), ("DegTensx2", "Degrading Tension x2"), ("DegTensADWF", "Degrading Tension Accumulation Deformation Factor"), ("DegCompf0", "Degrading Compression f0"), ("DegCompf1", "Degrading Compression f1"), ("DegCompf2", "Degrading Compression f2"), ("DegCompx1", "Degrading Compression x1"), ("DegCompx2", "Degrading Compression x2"), ("DegCompADWF", "Degrading Compression Accumulation Deformation Factor"), ("DegStiff", "Degrading Stiffness Weighting Factor"), ("DegLargeSml", "Degrading Larger-Smaller Weighting Factor")],
        ["Hinge Definitions - Shear V3"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("FDType", "Force Displacement Type"), ("Point", "Point"), ("Force", "Force"), ("Displ", "Displacement"), ("UseYldForce", "Use Yield Force?"), ("UseYldDispl", "Use Yield Displacement?"), ("FDPosForSF", "Positive Force SF"), ("FDPosDispSF", "Positive Displacement SF"), ("FDNegForSF", "Negative Force SF"), ("FDNegDispSF", "Negative Displacement SF"), ("SSPosForSF", "Positive Stress SF"), ("SSPosDispSF", "Positive Strain SF"), ("SSNegForSF", "Negative Stress SF"), ("SSNegDispSF", "Negative Strain SF"), ("BeyondE", "Beyond E"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP"), ("HysType", "Hysteresis Type"), ("PivotAlpha1", "Pivot Alpha1"), ("PivotAlpha2", "Pivot Alpha2"), ("PivotBeta1", "Pivot Beta1"), ("PivotBeta2", "Pivot Beta2"), ("PivotEta", "Pivot Eta"), ("ConcreteFact", "Energy Degradation Factor"), ("BRBSym", "BRB Hardening Symmetric?"), ("BRBTensHF", "BRB Tension Hardening Factor"), ("BRBTensMPD", "BRB Tension Max Plastic Deformation Ratio"), ("BRBTensAPD", "BRB Tension Accumulated Plastic Deformation Ratio"), ("BRBTensPro", "BRB Tension Proportion"), ("BRBCompHF", "BRB Compression Hardening Factor"), ("BRBCompMPD", "BRB Compression Max Plastic Deformation Ratio"), ("BRBCompAPD", "BRB Compression Accumulated Plastic Deformation Ratio"), ("BRBCompPro", "BRB Compression Proportion"), ("DegSym", "Degrading Symmetric?"), ("DegTensf0", "Degrading Tension f0"), ("DegTensf1", "Degrading Tension f1"), ("DegTensf2", "Degrading Tension f2"), ("DegTensx1", "Degrading Tension x1"), ("DegTensx2", "Degrading Tension x2"), ("DegTensADWF", "Degrading Tension Accumulation Deformation Factor"), ("DegCompf0", "Degrading Compression f0"), ("DegCompf1", "Degrading Compression f1"), ("DegCompf2", "Degrading Compression f2"), ("DegCompx1", "Degrading Compression x1"), ("DegCompx2", "Degrading Compression x2"), ("DegCompADWF", "Degrading Compression Accumulation Deformation Factor"), ("DegStiff", "Degrading Stiffness Weighting Factor"), ("DegLargeSml", "Degrading Larger-Smaller Weighting Factor")],
        ["Hinge Definitions - Torsion T"] = [("Name", "Name"), ("IsSymmetric", "Is Symmetric?"), ("AddBC", "Is BC Points?"), ("AddCD", "Is CD Points?"), ("FDType", "Moment Rotation Type"), ("Point", "Point"), ("Force", "Moment"), ("Displ", "Rotation"), ("FDPosForSF", "Positive Moment SF"), ("FDPosDispSF", "Positive Rotation SF"), ("SSPosDispSF", "Positive Curvature SF"), ("BeyondE", "Beyond E"), ("LengthType", "Hinge Length Type"), ("SSAbsLen", "Hinge Absolute Length"), ("SSRelLen", "Hinge Relative Length"), ("IOPos", "Positive IO"), ("LSPos", "Positive LS"), ("CPPos", "Positive CP"), ("IONeg", "Negative IO"), ("LSNeg", "Negative LS"), ("CPNeg", "Negative CP"), ("HysType", "Hysteresis Type"), ("PivotAlpha1", "Pivot Alpha1"), ("PivotAlpha2", "Pivot Alpha2"), ("PivotBeta1", "Pivot Beta1"), ("PivotBeta2", "Pivot Beta2"), ("PivotEta", "Pivot Eta"), ("ConcreteFact", "Energy Degradation Factor"), ("BRBSym", "BRB Hardening Symmetric?"), ("BRBTensHF", "BRB Tension Hardening Factor"), ("BRBTensMPD", "BRB Tension Max Plastic Deformation Ratio"), ("BRBTensAPD", "BRB Tension Accumulated Plastic Deformation Ratio"), ("BRBTensPro", "BRB Tension Proportion"), ("BRBCompHF", "BRB Compression Hardening Factor"), ("BRBCompMPD", "BRB Compression Max Plastic Deformation Ratio"), ("BRBCompAPD", "BRB Compression Accumulated Plastic Deformation Ratio"), ("BRBCompPro", "BRB Compression Proportion"), ("DegSym", "Degrading Symmetric?"), ("DegTensf0", "Degrading Tension f0"), ("DegTensf1", "Degrading Tension f1"), ("DegTensf2", "Degrading Tension f2"), ("DegTensx1", "Degrading Tension x1"), ("DegTensx2", "Degrading Tension x2"), ("DegTensADWF", "Degrading Tension Accumulation Deformation Factor"), ("DegCompf0", "Degrading Compression f0"), ("DegCompf1", "Degrading Compression f1"), ("DegCompf2", "Degrading Compression f2"), ("DegCompx1", "Degrading Compression x1"), ("DegCompx2", "Degrading Compression x2"), ("DegCompADWF", "Degrading Compression Accumulation Deformation Factor"), ("DegStiff", "Degrading Stiffness Weighting Factor"), ("DegLargeSml", "Degrading Larger-Smaller Weighting Factor")],
    });


    private static Dictionary<string, (string, string)[]> WithSap(Dictionary<string, (string, string)[]> etabs)
    {
        foreach (var (t, fields) in SapHinges) etabs[t] = fields.Select(f => (f, f)).ToArray();
        // ETABS's shear wall pier and spandrel overwrites, by label (Ahmed 2026-11-27, every form).
        foreach (var (t, fields) in ProgramForms.WallOverwriteCatalog()) etabs[t] = fields.Select(f => (f.Key, f.Label)).ToArray();
        return etabs;
    }

    /// <summary>True for a table of SAP2000's own (written to the .s2k as it is), not one of ETABS's.</summary>
    public static bool IsSap(string table) => SapHinges.ContainsKey(table) || SapLoadTables.Has(table);

    /// <summary>The coordinate system and grid line tables: the .s2k writes them with GLOBAL's own, not on their own.</summary>
    public static bool IsGridTable(string table) => table.Equals("Coordinate Systems", StringComparison.OrdinalIgnoreCase) || table.Equals("Grid Lines", StringComparison.OrdinalIgnoreCase)
        || table.Equals(SapFrameGeneral, StringComparison.OrdinalIgnoreCase) || table.Equals(SapAreaSections, StringComparison.OrdinalIgnoreCase)
        || table.Equals(SapLinkGeneral, StringComparison.OrdinalIgnoreCase) || table.Equals(SapLinkLinear, StringComparison.OrdinalIgnoreCase);

    public const string SapLinkGeneral = "Link Property Definitions 01 - General", SapLinkLinear = "Link Property Definitions 02 - Linear";

    public const string SapFrameGeneral = "Frame Section Properties 01 - General", SapAreaSections = "Area Section Properties";

    /// <summary>After SAP2000's own row of a frame or area section changes: the Engine's section of that name follows it (a
    /// General frame section with the row's depth, width, area and inertias; a shell section for Plane, Asolid and the
    /// plates), so the members and areas keep their section. No row: the Engine's section goes, unless objects use it.</summary>
    public static void SyncSapSection(StructuralModel m, string table, string name)
    {
        var row = m.DefinitionTables?.GetValueOrDefault(table)?.Find(r => r.GetValueOrDefault(SapHinges[table][0]) == name);
        double D(string k) => row is not null && double.TryParse(row.GetValueOrDefault(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (table.Equals(SapFrameGeneral, StringComparison.OrdinalIgnoreCase))
        {
            if (row is null)
            {
                if (m.LineAssigns.Exists(x => x.Section == name)) throw new ArgumentException($"Frame section {name} is used by members: give them another section first.");
                m.FrameSections.RemoveAll(x => x.Name == name);
                return;
            }
            var material = row.GetValueOrDefault("Material") ?? "";
            if (!m.Materials.Exists(x => x.Name == material)) throw new ArgumentException($"There is no material '{material}'.");
            var sec = m.FrameSections.Find(x => x.Name == name);
            if (sec is null) m.FrameSections.Add(sec = new FrameSection { Name = name });
            var mods = sec.Lines.Where(l => l.Count > 0 && PropertyDefs.FrameModifiers.Contains(l[0])).ToList();
            sec.Lines = [["MATERIAL", E2kText.Quote(material), "SHAPE", E2kText.Quote("General"), "D", E2kText.Number(D("t3") > 0 ? D("t3") : 1), "B", E2kText.Number(D("t2") > 0 ? D("t2") : 1),
                "AREA", E2kText.Number(D("Area")), "AS2", E2kText.Number(D("AS2")), "AS3", E2kText.Number(D("AS3")), "I33", E2kText.Number(D("I33")), "I22", E2kText.Number(D("I22")), "J", E2kText.Number(D("TorsConst"))], .. mods];
        }
        else if (table.Equals(SapAreaSections, StringComparison.OrdinalIgnoreCase))
        {
            if (row is null)
            {
                if (m.AreaAssigns.Exists(x => x.Section == name)) throw new ArgumentException($"Area section {name} is used by areas: give them another section first.");
                m.ShellSections.RemoveAll(x => x.Name == name);
                return;
            }
            var type = row.GetValueOrDefault("Type") ?? "";
            var modelling = type.Equals("Plate-Thick", StringComparison.OrdinalIgnoreCase) ? "ShellThick" : type.StartsWith("Plate", StringComparison.OrdinalIgnoreCase) ? "ShellThin" : "Membrane";
            var t = D("Thickness") > 0 ? D("Thickness") : 1;
            PropertyDefs.SetShellType(m, name, m.ShellSections.Exists(x => x.Name == name) ? name : null, false, "Slab", modelling, t, row.GetValueOrDefault("Material") ?? "", false);
        }
    }

    /// <summary>The table each SAP2000 link type keeps its directions in (Link/Support Property Data, Link/Support Type).</summary>
    public static readonly Dictionary<string, string> SapLinkTypeTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Linear"] = "Link Property Definitions 02 - Linear", ["MultiLinear Elastic"] = "Link Property Definitions 03 - MultiLinear",
        ["MultiLinear Plastic"] = "Link Property Definitions 11 - Multilinear Plastic", ["Damper"] = "Link Property Definitions 04 - Damper",
        ["Damper - Exponential"] = "Link Property Definitions 04 - Damper", ["Damper - Bilinear"] = "Link Property Definitions 14 - Bilinear Damper",
        ["Damper - Friction Spring"] = "Link Property Definitions 15 - Friction Spring Damper", ["Damper - Sumitomo Rubber QR4"] = "Link Property Definitions 17 - Sumitomo Damper",
        ["Gap"] = "Link Property Definitions 05 - Gap", ["Hook"] = "Link Property Definitions 06 - Hook", ["Plastic (Wen)"] = "Link Property Definitions 10 - Plastic (Wen)",
        ["Rubber Isolator"] = "Link Property Definitions 07 - Rubber Isolator", ["Friction Isolator"] = "Link Property Definitions 08 - Sliding Isolator",
        ["T/C Friction Isolator"] = "Link Property Definitions 09 - TC Sliding Isolator", ["Triple Pendulum Isolator"] = "Link Property Definitions 12 - Triple Pendulum Isolator",
        ["High Damping Rubber Isolator"] = "Link Property Definitions 16 - High Damping Rubber Isolator", ["Linear Exponential Damper"] = "Link Property Definitions 13 - Linear Exponential Damper",
    };

    /// <summary>After a SAP2000 link's "01 - General" row changes: its directions stay only in its type's table, and the
    /// Engine's link of that name (the one analysis and assignments use) follows, with each direction's effective
    /// stiffness and damping. No row takes the link out.</summary>
    public static void SyncSapLink(StructuralModel m, string name)
    {
        var row = m.DefinitionTables?.GetValueOrDefault(SapLinkGeneral)?.Find(r => r.GetValueOrDefault("Link") == name);
        var own = row is null ? null : SapLinkTypeTables.GetValueOrDefault(row.GetValueOrDefault("LinkType") ?? "");
        foreach (var t in SapHinges.Keys.Where(k => k.StartsWith("Link Property Definitions ", StringComparison.Ordinal) && k != SapLinkGeneral && k != own).ToList())
            if (HasRow(m, t, name)) Set(m, t, name, []);
        if (row is null)
        {
            if (PropertyDefs.Links(m).Exists(x => x.Name == name)) PropertyDefs.DeleteLink(m, name);
            return;
        }
        static double D(Dictionary<string, string> r, string k) => double.TryParse(r.GetValueOrDefault(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : 0;
        var dofs = new Dictionary<string, (double, double)>();
        var ownRows = own is null ? null : m.DefinitionTables?.GetValueOrDefault(own);
        foreach (var r in (ownRows ?? []).Where(x => x.GetValueOrDefault("Link") == name))
        {
            var dof = (r.GetValueOrDefault("DOF") ?? "").ToUpperInvariant();
            if (!PropertyDefs.LinkDofs.Contains(dof) || dofs.ContainsKey(dof)) continue;
            var rot = dof[0] == 'R';
            dofs[dof] = (D(r, rot ? "RotKE" : "TransKE"), D(r, rot ? "RotCE" : "TransCE"));
        }
        if (dofs.Count == 0) dofs["U1"] = (0, 0);
        PropertyDefs.SetLink(m, name, PropertyDefs.EngineLinkType(row.GetValueOrDefault("LinkType") ?? ""), dofs);
    }

    /// <summary>True when the table keeps a row named so (by its first field).</summary>
    public static bool HasRow(StructuralModel m, string table, string name) =>
        m.DefinitionTables?.GetValueOrDefault(table)?.Exists(r => r.GetValueOrDefault(SapHinges[table][0]) == name) == true;

    /// <summary>Sets the rows of one definition (by its name) in a table; no rows takes the definition out of it.</summary>
    public static void Set(StructuralModel m, string table, string name, IReadOnlyList<Dictionary<string, string>> rows)
    {
        // SAP2000's own load tables (Define > Load Patterns, Load Cases, Load Combinations): SapLoadTables.
        if (SapLoadTables.Full(table) is { } sapLoad) { Set(m, sapLoad, SapLoadTables.Fields[sapLoad].Select(f => (f, f)).ToArray(), name, rows); return; }
        if (!Tables.TryGetValue(table, out var fields)) throw new ArgumentException($"There is no definition table '{table}'. The tables: {string.Join(", ", Tables.Keys)}.");
        Set(m, Tables.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase)), fields, name, rows);
    }

    /// <summary>The name of a definition table as the model keeps it (SAP2000's load tables too), or null.</summary>
    public static string? FullName(string table) => SapLoadTables.Full(table) ?? Tables.Keys.FirstOrDefault(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));

    /// <summary>The key field (the definition's name) of a definition table.</summary>
    public static string KeyField(string table) => SapLoadTables.Full(table) is { } t ? SapLoadTables.Fields[t][0] : Tables[table][0].Key;

    private static void Set(StructuralModel m, string full, (string Key, string Label)[] fields, string name, IReadOnlyList<Dictionary<string, string>> rows)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the definition's name.");
        var key = fields[0].Key;
        foreach (var row in rows)
            foreach (var f in row.Keys)
                if (!fields.Any(x => x.Key.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"'{full}' has no field '{f}'. Its fields: {string.Join(", ", fields.Select(x => x.Key))}.");
        m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
        var list = m.DefinitionTables.TryGetValue(full, out var l) ? l : m.DefinitionTables[full] = [];
        list.RemoveAll(r => r.GetValueOrDefault(key) == name);
        foreach (var row in rows)
        {
            var clean = row.Where(kv => kv.Value is not null && kv.Value.Trim().Length > 0)
                .ToDictionary(kv => fields.First(x => x.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)).Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
            if (clean.Count == 0) continue;
            clean[key] = name;
            list.Add(clean);
        }
        if (list.Count == 0) m.DefinitionTables.Remove(full);
        if (m.DefinitionTables.Count == 0) m.DefinitionTables = null;
    }

    // ---- rebar sizes and a concrete section's rebar, by the .e2k's own keywords ----

    private static RawSection Section(StructuralModel m, string heading, string after)
    {
        var s = m.Passthrough.Find(x => x.Heading == heading);
        if (s is null) m.Passthrough.Add(s = new RawSection { Heading = heading, After = after });
        return s;
    }

    /// <summary>The rebar sizes: name, area and diameter.</summary>
    public static List<(string Name, double Area, double Diameter)> RebarSizes(StructuralModel m) =>
        (m.Passthrough.Find(x => x.Heading == "REBAR DEFINITIONS")?.Lines ?? []).Select(E2kText.Tokenize)
            .Where(t => t.Count > 1 && t[0] == "REBARDEFINITION")
            .Select(t => (E2kText.Unquote(t[1]), Num(E2kText.Value(t, "AREA")), Num(E2kText.Value(t, "DIA")))).ToList();

    private static double Num(string? v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>Adds or changes a rebar size, or deletes it (area null).</summary>
    public static void SetRebarSize(StructuralModel m, string name, double? area, double? diameter)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the bar's name.");
        if (area is not null && (area <= 0 || diameter is null or <= 0)) throw new ArgumentException("A bar has an area and a diameter, both more than 0.");
        var s = Section(m, "REBAR DEFINITIONS", "MATERIAL PROPERTIES");
        s.Lines.RemoveAll(l => E2kText.Tokenize(l) is { Count: > 1 } t && t[0] == "REBARDEFINITION" && E2kText.Unquote(t[1]) == name);
        if (area is { } a) s.Lines.Add(E2kText.Line(["REBARDEFINITION", E2kText.Quote(name)], ["AREA", E2kText.Number(a), "DIA", E2kText.Number(diameter!.Value)]));
    }

    /// <summary>The keywords a concrete section's rebar line may carry, by its TYPE.</summary>
    public static readonly string[] ColumnRebarKeys = ["LONGBARMATERIAL", "CONFINEBARMATERIAL", "PATTERN", "TRANSREINF", "DESIGNCHECK", "COVER", "LONGBARAREA", "CONFINEBARAREA", "CONFINEBARSPACING", "NUMCONFINEBARS3", "NUMCONFINEBARS2"];
    public static readonly string[] BeamRebarKeys = ["LONGBARMATERIAL", "CONFINEBARMATERIAL", "COVERTOP", "COVERBOTTOM", "ATI", "ABI", "ATJ", "ABJ"];
    private static readonly string[] TextKeys = ["LONGBARMATERIAL", "CONFINEBARMATERIAL", "PATTERN", "TRANSREINF", "DESIGNCHECK", "TYPE"];

    /// <summary>A concrete section's rebar as keyword and value (TYPE first), or null when it has none.</summary>
    public static Dictionary<string, string>? Rebar(StructuralModel m, string section)
    {
        var line = (m.Passthrough.Find(x => x.Heading == "CONCRETE SECTIONS")?.Lines ?? []).Select(E2kText.Tokenize)
            .FirstOrDefault(t => t.Count > 1 && t[0] == "CONCRETESECTION" && E2kText.Unquote(t[1]) == section);
        if (line is null) return null;
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i + 1 < line.Count; i += 2) d[line[i]] = E2kText.Unquote(line[i + 1]);
        return d;
    }

    /// <summary>Changes a concrete section's rebar: TYPE Column or Beam, and that type's keywords (only those given change).</summary>
    public static void SetRebar(StructuralModel m, string section, IReadOnlyDictionary<string, string> values)
    {
        var fs = m.FrameSections.Find(x => x.Name == section) ?? throw new ArgumentException($"There is no frame section '{section}'.");
        if (m.Materials.Find(x => x.Name == fs.Material) is not { IsConcrete: true }) throw new ArgumentException($"{section} is not a concrete section; only concrete sections have rebar.");
        var now = Rebar(m, section) ?? new(StringComparer.Ordinal) { ["TYPE"] = "Column" };
        foreach (var (k, v) in values) now[k.ToUpperInvariant()] = v;
        var type = now.GetValueOrDefault("TYPE") ?? "Column";
        if (type is not ("Column" or "Beam")) throw new ArgumentException("TYPE is Column or Beam.");
        var keys = type == "Beam" ? BeamRebarKeys : ColumnRebarKeys;
        foreach (var k in values.Keys.Select(x => x.ToUpperInvariant()))
            if (k != "TYPE" && !keys.Contains(k)) throw new ArgumentException($"A {type.ToLowerInvariant()}'s rebar has no '{k}'. Its keywords: {string.Join(", ", keys)}.");
        foreach (var k in keys.Where(k => !TextKeys.Contains(k) && now.ContainsKey(k)))
            if (Num(now[k]) < 0 || !double.TryParse(now[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new ArgumentException($"{k} is a number, 0 or more.");
        foreach (var mat in new[] { "LONGBARMATERIAL", "CONFINEBARMATERIAL" })
            if (now.TryGetValue(mat, out var name) && !m.Materials.Exists(x => x.Name == name)) throw new ArgumentException($"There is no material '{name}'.");
        if (now.TryGetValue("PATTERN", out var pat) && !System.Text.RegularExpressions.Regex.IsMatch(pat, @"^(R-\d+-\d+|C-\d+)$")) throw new ArgumentException("PATTERN is R-n3-n2 (bars along 3 and 2) or C-n (bars in a circle).");
        // ETABS's own order: the bar materials, then TYPE, then the rest.
        var tokens = new List<string>();
        foreach (var k in keys.Take(2).Where(now.ContainsKey)) tokens.AddRange([k, E2kText.Quote(now[k])]);
        tokens.AddRange(["TYPE", E2kText.Quote(type)]);
        foreach (var k in keys.Skip(2).Where(now.ContainsKey)) tokens.AddRange([k, TextKeys.Contains(k) ? E2kText.Quote(now[k]) : now[k]]);
        var s = Section(m, "CONCRETE SECTIONS", "FRAME SECTIONS");
        s.Lines.RemoveAll(l => E2kText.Tokenize(l) is { Count: > 1 } t && t[0] == "CONCRETESECTION" && E2kText.Unquote(t[1]) == section);
        s.Lines.Add(E2kText.Line(["CONCRETESECTION", E2kText.Quote(section)], tokens));
    }
}
