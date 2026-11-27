namespace CsiProMax.Model;

/// <summary>
/// SAP2000 26.3's own per-object assignment and load tables that the Modeler has no native field for (Assign > Joint,
/// Frame and Area forms, and their load forms): the table, the kind of object it is for, whether an object has several
/// rows (one per load pattern, face, edge or spring), and every field after the object's own name, exactly as SAP2000's
/// "Table and Field Keys" give them (project files field/table-keys/sap2000-26.3-field-names.txt). An object keeps its
/// rows in <see cref="Assign.Tables"/> by the table name; S2kReader reads them there, S2kWriter writes them back with the
/// object's name, so they follow the object through edits, renames and deletes. SAP2000 only: no other program has them.
/// </summary>
public static class SapObjectTables
{
    public static readonly IReadOnlyDictionary<string, (string Kind, bool Many, string[] Fields)> Tables = new Dictionary<string, (string, bool, string[])>(StringComparer.OrdinalIgnoreCase)
    {
        // Also read by the writer's own model (springs from spring properties, mass as ETABS's X-Y and Z): SAP2000's form keeps its own row.
        ["Joint Added Mass Assignments"] = ("joint", false, ["CoordSys", "Mass1", "Mass2", "Mass3", "MMI1", "MMI2", "MMI3", "AutoBridge", "BridgeObj"]),
        ["Joint Spring Assignments 1 - Uncoupled"] = ("joint", false, ["CoordSys", "U1", "U2", "U3", "R1", "R2", "R3"]),
        ["Joint Local Axes Assignments 1 - Typical"] = ("joint", false, ["AngleA", "AngleB", "AngleC", "AdvanceAxes"]),
        ["Joint Local Axes Assignments 2 - Advanced"] = ("joint", false, ["LocalPlane", "AxOption1", "AxCoordSys", "AxCoordDir", "AxVecJt1", "AxVecJt2", "PlOption1", "PlCoordSys", "CoordDir1", "CoordDir2", "PlVecJt1", "PlVecJt2", "AxVecX", "AxVecY", "AxVecZ", "PlVecX", "PlVecY", "PlVecZ"]),
        ["Joint Added Mass By Weight Assignments"] = ("joint", false, ["CoordSys", "Weight1", "Weight2", "Weight3", "WMI1", "WMI2", "WMI3", "AutoBridge", "BridgeObj"]),
        ["Joint Added Mass By Volume Assignments"] = ("joint", false, ["CoordSys", "Material", "Vol1", "Vol2", "Vol3", "VMI1", "VMI2", "VMI3", "AutoBridge", "BridgeObj"]),
        ["Joint Merge Number Assignments"] = ("joint", false, ["MergeNumber"]),
        ["Joint Pattern Assignments"] = ("joint", true, ["Pattern", "Value"]),
        ["Joint Spring Assignments 2 - Coupled"] = ("joint", true, ["CoordSys", "U1", "U1U2", "U2", "U1U3", "U2U3", "U3", "U1R1", "U2R1", "U3R1", "R1", "U1R2", "U2R2", "U3R2", "R1R2", "R2", "U1R3", "U2R3", "U3R3", "R1R3", "R2R3", "R3"]),
        ["Joint Punching Load Assignments"] = ("joint", false, ["JtActive", "Frame", "FrmActive", "UserFType", "FType", "UserSection", "Section", "Class", "Gap", "UserAngle", "Angle", "QqAxial", "QqMomIn", "QqMomOut", "QuAxial", "QuMomIn", "QuMomOut", "QfAxial", "QfMomIn", "QfMomOut", "PhiJAxial", "PhiJMomIn", "PhiJMomOut", "WeldThick", "Vwa", "PhiWeld", "L1OverL", "L2"]),
        ["Frame Auto Mesh Assignments"] = ("frame", false, ["AutoMesh", "AtJoints", "AtFrames", "NumSegments", "MaxLength", "MaxDegrees"]),
        ["Frame End Skew Angle Assignments"] = ("frame", false, ["SkewI", "SkewJ"]),
        ["Frame Fireproofing Assignments"] = ("frame", true, ["Type", "Thickness", "UserPerim", "TopFlange", "Density", "WtPerLen", "SelfWt", "GravLoad", "LoadPat"]),
        ["Frame Load Transfer Options"] = ("frame", false, ["Transfer"]),
        ["Frame Local Axes Assignments 2 - Advanced"] = ("frame", false, ["LocalPlane", "PlOption1", "PlCoordSys", "CoordDir1", "CoordDir2", "PlVecJt1", "PlVecJt2", "PlVecX", "PlVecY", "PlVecZ"]),
        ["Frame Output Station Assignments"] = ("frame", false, ["Cable", "StationType", "MinNumSta", "MaxStaSpcg", "AddAtElmInt", "AddAtPtLoad"]),
        ["Frame P-Delta Force Assignments"] = ("frame", true, ["CoordSys", "Direction", "Force"]),
        ["Frame Rebar Ratio For Creep Analysis Assignments"] = ("frame", false, ["IsUser", "UserRatio"]),
        ["Frame Spring Assignments"] = ("frame", true, ["Type", "Stiffness", "SimpleType", "LinkProp", "Dir1Type", "Dir", "Axis1Angle", "CoordSys", "VecX", "VecY", "VecZ", "Axis2Angle", "AutoBridge", "BridgeObj"]),
        ["Frame Material Temperatures"] = ("frame", false, ["Cable", "Tendon", "Temp", "JtPattern"]),
        ["Frame Offset Along Length Assignments"] = ("frame", false, ["Type", "LengthI", "LengthJ", "RigidFactor"]),
        ["Frame Hinge Assigns 00 - Hinge Distribution Type"] = ("frame", false, ["HgeDistrTyp", "IsHgeLenRel", "HgeLenRel", "HgeLenAbs", "IntgrType", "SpaceType", "MaxSpace", "NumHgeSpace", "HngAtEnds"]),
        ["Frame Hinge Assigns 02 - User Defined Properties"] = ("frame", true, ["HgeDistrTyp", "AssignProp", "GenHinge", "SaveFibResp", "DistType", "RelDist", "AbsDist", "ActualRDist", "ActualDist", "LenOWType", "LenOWRel", "LenOWAbs"]),
        ["Frame Hinge Assigns 09 - Hinge Overwrites"] = ("frame", false, ["AutoDivide", "RelLength", "UseHngLenOW", "NoLoadDrop", "LimNegStiff"]),
        ["Frame Loads - Gravity"] = ("frame", true, ["Cable", "Tendon", "LoadPat", "CoordSys", "MultiplierX", "MultiplierY", "MultiplierZ"]),
        ["Frame Loads - Deformation"] = ("frame", true, ["Cable", "Tendon", "LoadPat", "IncludeU1", "IncludeU2", "IncludeU3", "IncludeR1", "IncludeR2", "IncludeR3", "U1", "U2", "U3", "R1", "R2", "R3"]),
        ["Frame Loads - Target Force"] = ("frame", true, ["Cable", "Tendon", "LoadPat", "IncludeP", "IncludeV2", "IncludeV3", "IncludeT", "IncludeM2", "IncludeM3", "P", "V2", "V3", "T", "M2", "M3", "RelLocP", "RelLocV2", "RelLocV3", "RelLocT", "RelLocM2", "RelLocM3"]),
        ["Frame Loads - Open Structure Wind"] = ("frame", true, ["Cable", "LoadPat", "CoordSys", "Type", "Dir", "DistType", "RelDistA", "RelDistB", "AbsDistA", "AbsDistB", "FOverLA", "FOverLB"]),
        ["Area Auto Mesh Assignments"] = ("area", false, ["MeshType", "MeshGroup", "Number1", "Number2", "Max1", "Max2", "PtsFromLine", "PtsFromPt", "RotAngle", "GenMaxSize", "LocalEdge", "LocalFace", "SuppEdge", "SuppFace", "SubMesh", "SubMeshSize"]),
        ["Area Edge Constraint Assignments"] = ("area", true, ["Edge", "Constrained"]),
        ["Area Local Axes Assignments 2 - Advanced"] = ("area", false, ["LocalPlane", "PlOption1", "PlCoordSys", "CoordDir1", "CoordDir2", "PlVecJt1", "PlVecJt2", "PlVecX", "PlVecY", "PlVecZ"]),
        ["Area Material Temperatures"] = ("area", false, ["JtPattern", "Temp"]),
        ["Area Overwrites - Thickness"] = ("area", false, ["ThickOver", "ThickPat", "ThickPatSF", "Thick1", "Thick2", "Thick3", "Thick4"]),
        ["Area Overwrites - Joint Offsets"] = ("area", false, ["OffsetOver", "OffsetPat", "OffsetPatSF", "Offset1", "Offset2", "Offset3", "Offset4"]),
        ["Area Rebar Ratio For Creep Analysis Assignments"] = ("area", false, ["UserRatio"]),
        ["Area Spring Assignments"] = ("area", true, ["Type", "Stiffness", "SimpleType", "LinkProp", "Face", "Dir1Type", "Dir", "NormalDir", "CoordSys", "VecX", "VecY", "VecZ", "Axis2Angle"]),
        ["Area Loads - Gravity"] = ("area", true, ["LoadPat", "CoordSys", "MultiplierX", "MultiplierY", "MultiplierZ"]),
        ["Area Loads - Pore Pressure"] = ("area", true, ["LoadPat", "JtPattern", "Pressure"]),
        ["Area Loads - Rotate"] = ("area", true, ["LoadPat", "AngularVel"]),
        ["Area Loads - Surface Pressure"] = ("area", true, ["LoadPat", "Face", "JtPattern", "Pressure"]),
        ["Area Loads - Uniform To Frame"] = ("area", true, ["LoadPat", "CoordSys", "Dir", "UnifLoad", "DistType"]),
        ["Area Loads - Wind Pressure Coefficients"] = ("area", true, ["LoadPat", "Windward", "Cp", "DistType", "XComponent", "YComponent", "ZComponent"]),
    };

    /// <summary>The field that names the object in each kind's tables.</summary>
    public static string KeyField(string kind) => kind switch { "frame" => "Frame", "area" => "Area", _ => "Joint" };

    /// <summary>Tables the Modeler's own model also carries (a coarser way): S2kReader keeps SAP2000's row on the object too,
    /// and S2kWriter writes that row in place of the one it makes from the model.</summary>
    public static readonly string[] AlsoNative = ["Joint Added Mass Assignments", "Joint Spring Assignments 1 - Uncoupled", "Frame Offset Along Length Assignments",
        "Frame Hinge Assigns 00 - Hinge Distribution Type", "Frame Hinge Assigns 02 - User Defined Properties", "Frame Hinge Assigns 09 - Hinge Overwrites"];

    public static bool Has(string table) => Tables.ContainsKey(table);

    /// <summary>The table's name as the catalogue spells it.</summary>
    public static string Name(string table) => Tables.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));
}
