using CsiProMax.Model;

namespace CsiProMax.Checks.Stability;

/// <summary>
/// The model as the analysis sees it, the same for ETABS, SAP2000, SAFE and the Modeler: joints in 3D with their
/// supports, frames and shells between joints, the sections and materials they use, and what the analysis is asked to
/// run. The stability checker reads only this, so one checker serves every program. Each program's adapter fills it
/// (FromStructural for the Modeler and .e2k files; the assistant's live reader for a model open in a CSI program) and
/// leaves a value null when the program did not say: a check that needs it then says it could not look.
/// </summary>
public sealed class AnalysisModel
{
    /// <summary>"etabs", "sap2000", "safe" or "modeler": which rules apply (ETABS joins members where they cross; SAP2000 does not).</summary>
    public string Program { get; set; } = "etabs";
    /// <summary>The model's length unit as the program names it (m, mm, cm, in, ft), for the tolerances.</summary>
    public string LengthUnit { get; set; } = "m";
    public string ForceUnit { get; set; } = "kN";

    public List<Joint> Joints { get; } = [];
    public List<Frame> Frames { get; } = [];
    public List<Shell> Shells { get; } = [];
    /// <summary>Links and constraints that tie joints together (or one joint to the ground), as the program joins them.</summary>
    public List<Link> Links { get; } = [];
    public Dictionary<string, FrameProp> FrameProps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ShellProp> ShellProps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MaterialProp> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Rigid diaphragms by name. A joint or shell names one; ETABS makes one constraint per story of it.</summary>
    public HashSet<string> RigidDiaphragms { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Pattern> Patterns { get; } = [];
    public List<Case> Cases { get; } = [];
    public List<Combo> Combos { get; } = [];
    /// <summary>The functions defined (spectra, time histories) by name; null when the program did not say.</summary>
    public HashSet<string>? Functions { get; set; }
    /// <summary>The mass source, when the program said; null when it could not be read.</summary>
    public MassSource? Mass { get; set; }
    /// <summary>
    /// True when the program joins a member to the joints and members it passes through (ETABS's auto mesh at
    /// intersections, on by default) and meshes floors and walls to the joints inside them. SAP2000 joins only at the
    /// ends unless the member is meshed.
    /// </summary>
    public bool JoinsAtIntersections { get; set; } = true;
    /// <summary>
    /// The degrees of freedom the analysis solves for (Analyze > Set Analysis Options > Active Degrees of Freedom):
    /// UX UY UZ RX RY RZ. A plane frame (UX UZ RY) has nothing to resist the others and needs nothing to: the program
    /// leaves them out. Null when the program did not say (all six).
    /// </summary>
    public bool[]? ActiveDof { get; set; }
    /// <summary>Only the out-of-plane degrees of freedom count (SAFE: UZ, RX, RY); the program holds the others.</summary>
    public bool PlateOnly { get; set; }
    /// <summary>What the adapter could not read, said with the answer.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>The tolerance for "the same place" and "too short", in the model's length unit: about 1 mm.</summary>
    public double Tolerance => LengthUnit.ToLowerInvariant() switch
    {
        "mm" => 1, "cm" => 0.1, "in" or "inch" => 0.04, "ft" or "feet" => 0.0033, "micron" => 1000, _ => 0.001,
    };
}

/// <param name="Name">The joint's name as the program shows it. For ETABS, the point label and its story.</param>
/// <param name="Label">ETABS's point label (the same on every story); the name otherwise.</param>
/// <param name="Restraint">UX UY UZ RX RY RZ.</param>
/// <param name="Spring">A point spring is assigned (it supports the joint in some directions).</param>
/// <param name="Diaphragm">The rigid diaphragm the joint is in, null when none.</param>
public sealed record Joint(string Name, string Label, string? Story, double X, double Y, double Z, bool[] Restraint, bool Spring = false, string? Diaphragm = null, bool Loaded = false)
{
    public Xyz At => new(X, Y, Z);
    public bool Supported => Spring || Restraint.Any(r => r);
}

/// <param name="Releases">P V2 V3 T M2 M3 at end I, then the same at end J (12 values), as the CSI programs list them.</param>
/// <param name="Meshed">SAP2000: the frame is auto meshed at the joints and members it meets.</param>
public sealed record Frame(string Name, string Label, string? Story, string Kind, string I, string J, string? Section, bool[] Releases, bool Meshed = false, bool Spring = false);

/// <summary>
/// A link object, or a constraint (SAP2000's body, weld, equal...), between joint I and joint J; a link with one joint
/// (J null) ties that joint to the ground. Its stiffness is not judged: what it joins counts as joined, so a part held only
/// through a link (an isolator, a gap, a rigid link) is not called floating.
/// </summary>
public sealed record Link(string Name, string I, string? J, string Kind = "link");

/// <param name="Kind">floor, wall or shell.</param>
public sealed record Shell(string Name, string Label, string? Story, string Kind, IReadOnlyList<string> Joints, string? Section, bool Spring = false, string? Diaphragm = null, bool MeshNone = false);

/// <summary>A frame section: its material and the stiffness numbers the program gave (null when not known).</summary>
public sealed record FrameProp(string Name, string? Material, double? Area, double? I22, double? I33, double? Torsion, string Shape = "", double Modifier = 1);

/// <param name="Type">Shell (thin or thick: bending and membrane), Membrane (in-plane only) or Plate (bending only).</param>
public sealed record ShellProp(string Name, string? Material, double? Thickness, string Type = "Shell", string Kind = "Slab");

public sealed record MaterialProp(string Name, double? E, double? Weight, double? Mass = null, string Type = "");

public sealed record Pattern(string Name, string Type, double SelfWeight, bool HasLoads);

/// <param name="Type">The program's type: Linear Static, Modal, Response Spectrum, ...</param>
/// <param name="Patterns">The load patterns it applies (static cases).</param>
/// <param name="Uses">Other cases it builds on: a response spectrum's modal case, a staged or nonlinear case's start.</param>
/// <param name="Functions">The functions it names (spectrum, time history).</param>
public sealed record Case(string Name, string Type, IReadOnlyList<string> Patterns, IReadOnlyList<string> Uses, IReadOnlyList<string> Functions)
{
    public bool NeedsMass => Type.Contains("Modal", StringComparison.OrdinalIgnoreCase) || Type.Contains("Spectrum", StringComparison.OrdinalIgnoreCase)
        || Type.Contains("History", StringComparison.OrdinalIgnoreCase) && !Type.Contains("Static", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Items">The cases and combinations it adds, by name.</param>
public sealed record Combo(string Name, IReadOnlyList<string> Items);

/// <param name="Loads">The load patterns whose loads count as mass, with their factors.</param>
public sealed record MassSource(string Name, bool FromElements, bool FromAddedMass, IReadOnlyList<(string Pattern, double Factor)> Loads);

/// <summary>How bad a stability finding is. Error: the analysis stops or its results are meaningless. Warning: deserves a look.</summary>
public enum Level { Error, Warning, Note }

/// <summary>One object a finding is about, so the 3D view can show it.</summary>
/// <param name="Kind">joint, frame or shell.</param>
public sealed record ObjectRef(string Kind, string Name, string? Story = null);

/// <summary>
/// One problem that stops the analysis from completing, or makes it unstable: what it is, the objects it is about,
/// where (for the 3D view to mark), and what to do about it.
/// </summary>
public sealed record Instability(Level Level, string Check, string Message, string Fix, IReadOnlyList<ObjectRef> Objects, IReadOnlyList<Xyz> At);
