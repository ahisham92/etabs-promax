namespace CsiProMax.Model;

/// <summary>
/// What a model can hold that ETABS's .e2k has no place for, so that nothing the CSI programs take as input is out of
/// reach: SAP2000's joint constraints and moving loads (lanes, vehicles, vehicle classes, moving load cases) and SAFE's
/// user-drawn design strips. Kept in CSI ProMax's own .cpm file, written into the .s2k and the .f2k, and never into
/// the .e2k (ETABS has no equal for them; its diaphragms are the Define > Diaphragms item).
/// </summary>
public sealed class ProgramExtras
{
    public List<JointConstraint> Constraints { get; set; } = [];
    public List<Lane> Lanes { get; set; } = [];
    public List<Vehicle> Vehicles { get; set; } = [];
    public List<VehicleClass> VehicleClasses { get; set; } = [];
    public List<MovingCase> MovingCases { get; set; } = [];
    public List<DesignStrip> Strips { get; set; } = [];

    public bool IsEmpty => Constraints.Count + Lanes.Count + Vehicles.Count + VehicleClasses.Count + MovingCases.Count + Strips.Count == 0;
}

/// <summary>A joint constraint (SAP2000 Define > Joint Constraints) and the joints (point@story) it ties together.</summary>
public sealed class JointConstraint
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Body";
    /// <summary>Diaphragm, plate, rod and beam: the axis (X, Y, Z or Auto).</summary>
    public string Axis { get; set; } = "Z";
    /// <summary>Body, equal, local and weld: the degrees of freedom tied (UX..RZ; U1..R3 for local).</summary>
    public List<string> Dofs { get; set; } = [];
    /// <summary>Weld: how close joints must be to be welded.</summary>
    public double Tolerance { get; set; }
    public List<string> Joints { get; set; } = [];
}

/// <summary>A lane (Define > Moving Loads > Lanes) along frames (label@story), at an offset from them, of a width.</summary>
public sealed class Lane
{
    public string Name { get; set; } = "";
    public List<string> Frames { get; set; } = [];
    public double Offset { get; set; }
    public double Width { get; set; }
}

/// <summary>A vehicle: a standard one by name (HL-93K and the like), or a general one by its axles.</summary>
public sealed class Vehicle
{
    public string Name { get; set; } = "";
    /// <summary>The standard vehicle it is (one of <see cref="ExtrasDefs.StandardVehicles"/>), or "" for a general vehicle.</summary>
    public string Standard { get; set; } = "";
    public double Scale { get; set; } = 1;
    /// <summary>A general vehicle's axles, front to back: each its load and its distance from the axle before it.</summary>
    public List<Axle> Axles { get; set; } = [];
    /// <summary>A general vehicle's uniform load along its length (force per length), 0 for none.</summary>
    public double Uniform { get; set; }
}

public sealed class Axle
{
    public double Load { get; set; }
    public double Spacing { get; set; }
}

/// <summary>A vehicle class: the vehicles a moving load case moves, each with its scale factor.</summary>
public sealed class VehicleClass
{
    public string Name { get; set; } = "";
    public List<ClassVehicle> Vehicles { get; set; } = [];
}

public sealed class ClassVehicle
{
    public string Vehicle { get; set; } = "";
    public double Scale { get; set; } = 1;
}

/// <summary>A moving load case: each assignment moves a vehicle class over lanes.</summary>
public sealed class MovingCase
{
    public string Name { get; set; } = "";
    public List<MovingAssign> Assignments { get; set; } = [];
}

public sealed class MovingAssign
{
    public string Class { get; set; } = "";
    public List<string> Lanes { get; set; } = [];
    public double Scale { get; set; } = 1;
    public int MinLoaded { get; set; } = 1;
    public int MaxLoaded { get; set; }
}

/// <summary>A design strip drawn by the user (SAFE Draw > Draw Design Strips): layer A or B, through points on the
/// slab, with its width to the left and right of its line.</summary>
public sealed class DesignStrip
{
    public string Name { get; set; } = "";
    public string Layer { get; set; } = "A";
    public List<double[]> Points { get; set; } = [];
    public double WidthLeft { get; set; }
    public double WidthRight { get; set; }
}

/// <summary>The checks and edits behind the Define and Assign items for <see cref="ProgramExtras"/>.</summary>
public static class ExtrasDefs
{
    public static readonly string[] ConstraintTypes = ["Body", "Diaphragm", "Plate", "Rod", "Beam", "Equal", "Local", "Weld"];
    public static readonly string[] Axes = ["X", "Y", "Z", "Auto"];
    public static readonly string[] GlobalDofs = ["UX", "UY", "UZ", "RX", "RY", "RZ"];
    public static readonly string[] LocalDofs = ["U1", "U2", "U3", "R1", "R2", "R3"];
    public static readonly string[] StandardVehicles = ["HL-93K", "HL-93M", "HL-93S", "HS20-44", "HS15-44", "H20-44", "H15-44", "P5", "P7", "P9", "P11", "P13"];

    public static ProgramExtras Of(StructuralModel m) => m.Extras ??= new ProgramExtras();

    /// <summary>Drops an empty <see cref="StructuralModel.Extras"/>, so a model without any writes as before.</summary>
    public static void Tidy(StructuralModel m) { if (m.Extras is { IsEmpty: true }) m.Extras = null; }

    public static bool HasAxis(string type) => type is "Diaphragm" or "Plate" or "Rod" or "Beam";
    public static bool HasDofs(string type) => type is "Body" or "Equal" or "Local" or "Weld";

    private static void Name(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"Give the {what} a name.");
        if (name.Contains('"')) throw new ArgumentException("A name cannot have a double quote in it.");
    }

    private static string Pick(string[] list, string value, string what) =>
        list.FirstOrDefault(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"The {what} is one of {string.Join(", ", list)}, not '{value}'.");

    private static (string Label, string Story) At(string o, string what)
    {
        var at = o.LastIndexOf('@');
        if (at <= 0 || at == o.Length - 1) throw new ArgumentException($"'{o}' is not {what}@story.");
        return (o[..at], o[(at + 1)..]);
    }

    private static void Replace<T>(List<T> list, Func<T, string> name, T item)
    {
        var i = list.FindIndex(x => name(x) == name(item));
        if (i < 0) list.Add(item); else list[i] = item;
    }

    private static void Positive(double v, string what, bool zero = true)
    {
        if (!double.IsFinite(v) || v < 0 || !zero && v == 0) throw new ArgumentException($"The {what} is {(zero ? "0 or more" : "more than 0")}.");
    }

    // ---- joint constraints ----

    public static JointConstraint SetConstraint(StructuralModel m, string name, string type, string? axis, IReadOnlyList<string>? dofs, double tolerance)
    {
        Name(name, "constraint");
        type = Pick(ConstraintTypes, type, "constraint type");
        var c = new JointConstraint { Name = name, Type = type };
        if (HasAxis(type)) c.Axis = Pick(Axes, axis is { Length: > 0 } ? axis : type == "Diaphragm" || type == "Plate" ? "Z" : "Auto", "axis");
        else c.Axis = "";
        if (HasDofs(type))
        {
            var allowed = type == "Local" ? LocalDofs : GlobalDofs;
            var given = (dofs is { Count: > 0 } ? dofs : allowed).Select(d => Pick(allowed, d, "degree of freedom")).Distinct().ToList();
            c.Dofs = allowed.Where(given.Contains).ToList();
        }
        if (type == "Weld") { Positive(tolerance, "weld tolerance", false); c.Tolerance = tolerance; }
        var x = Of(m);
        c.Joints = x.Constraints.Find(o => o.Name == name)?.Joints ?? [];
        Replace(x.Constraints, o => o.Name, c);
        return c;
    }

    public static void DeleteConstraint(StructuralModel m, string name)
    {
        if (m.Extras?.Constraints.RemoveAll(c => c.Name == name) is not > 0) throw new ArgumentException($"There is no constraint '{name}'.");
        Tidy(m);
    }

    /// <summary>Puts joints (point@story) in a constraint, or takes them out of every constraint with "".</summary>
    public static int AssignConstraint(StructuralModel m, IReadOnlyList<string> joints, string constraint)
    {
        foreach (var j in joints)
        {
            var (p, s) = At(j, "point");
            if (m.Point(p) is null) throw new ArgumentException($"There is no point '{p}'.");
            if (m.StoryIndex(s) < 0) throw new ArgumentException($"There is no story '{s}'.");
        }
        var keys = joints.Select(j => { var (p, s) = At(j, "point"); return p + "@" + m.Stories[m.StoryIndex(s)].Name; }).Distinct().ToList();
        if (constraint.Length > 0)
        {
            var c = m.Extras?.Constraints.Find(o => o.Name == constraint) ?? throw new ArgumentException($"There is no constraint '{constraint}'. Define it first.");
            foreach (var k in keys) if (!c.Joints.Contains(k)) c.Joints.Add(k);
            return keys.Count;
        }
        foreach (var c in m.Extras?.Constraints ?? []) c.Joints.RemoveAll(keys.Contains);
        return keys.Count;
    }

    // ---- moving loads ----

    public static Lane SetLane(StructuralModel m, string name, IReadOnlyList<string> frames, double offset, double width)
    {
        Name(name, "lane");
        if (frames.Count == 0) throw new ArgumentException("A lane runs along frames: name them, as label@story, in order.");
        var named = new List<string>();
        foreach (var f in frames)
        {
            var (l, s) = At(f, "frame");
            var x = m.LineAssigns.Find(x => x.Label == l && string.Equals(x.Story, s, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"There is no frame {f}.");
            named.Add(x.Label + "@" + x.Story);
        }
        if (!double.IsFinite(offset)) throw new ArgumentException("The lane's offset is a number.");
        Positive(width, "lane width");
        var lane = new Lane { Name = name, Frames = named, Offset = offset, Width = width };
        Replace(Of(m).Lanes, x => x.Name, lane);
        return lane;
    }

    public static Vehicle SetVehicle(StructuralModel m, string name, string standard, double scale, IReadOnlyList<Axle> axles, double uniform)
    {
        Name(name, "vehicle");
        var v = new Vehicle { Name = name, Scale = scale };
        Positive(scale, "vehicle's scale factor", false);
        if (standard.Length > 0) v.Standard = Pick(StandardVehicles, standard, "standard vehicle");
        else
        {
            if (axles.Count == 0 && uniform <= 0) throw new ArgumentException("A general vehicle needs axles (each a load and its distance from the axle before it) or a uniform load.");
            for (var i = 0; i < axles.Count; i++)
            {
                Positive(axles[i].Load, "axle load");
                Positive(axles[i].Spacing, "axle spacing", i == 0);
            }
            Positive(uniform, "uniform load");
            v.Axles = axles.Select((a, i) => new Axle { Load = a.Load, Spacing = i == 0 ? 0 : a.Spacing }).ToList();
            v.Uniform = uniform;
        }
        Replace(Of(m).Vehicles, x => x.Name, v);
        return v;
    }

    public static VehicleClass SetVehicleClass(StructuralModel m, string name, IReadOnlyList<ClassVehicle> vehicles)
    {
        Name(name, "vehicle class");
        if (vehicles.Count == 0) throw new ArgumentException("A vehicle class has at least one vehicle.");
        foreach (var v in vehicles)
        {
            if (m.Extras?.Vehicles.Exists(x => x.Name == v.Vehicle) != true) throw new ArgumentException($"There is no vehicle '{v.Vehicle}'. Define it first.");
            Positive(v.Scale, "scale factor", false);
        }
        var c = new VehicleClass { Name = name, Vehicles = vehicles.Select(v => new ClassVehicle { Vehicle = v.Vehicle, Scale = v.Scale }).ToList() };
        Replace(Of(m).VehicleClasses, x => x.Name, c);
        return c;
    }

    public static MovingCase SetMovingCase(StructuralModel m, string name, IReadOnlyList<MovingAssign> assignments)
    {
        Name(name, "moving load case");
        if (m.LoadCases.Exists(c => c.Name == name)) throw new ArgumentException($"There is already a load case '{name}'.");
        if (assignments.Count == 0) throw new ArgumentException("A moving load case moves at least one vehicle class over lanes.");
        foreach (var a in assignments)
        {
            if (m.Extras?.VehicleClasses.Exists(x => x.Name == a.Class) != true) throw new ArgumentException($"There is no vehicle class '{a.Class}'. Define it first.");
            if (a.Lanes.Count == 0) throw new ArgumentException($"Name the lanes {a.Class} moves over.");
            foreach (var l in a.Lanes) if (m.Extras.Lanes.Exists(x => x.Name == l) != true) throw new ArgumentException($"There is no lane '{l}'. Define it first.");
            Positive(a.Scale, "scale factor", false);
            var most = a.MaxLoaded == 0 ? a.Lanes.Count : a.MaxLoaded;
            if (a.MinLoaded < 0 || most < a.MinLoaded || most > a.Lanes.Count) throw new ArgumentException($"The lanes loaded at once are from {a.MinLoaded} to {most}, of {a.Lanes.Count}: that cannot be.");
        }
        var c = new MovingCase { Name = name, Assignments = assignments.Select(a => new MovingAssign { Class = a.Class, Lanes = [.. a.Lanes], Scale = a.Scale, MinLoaded = a.MinLoaded, MaxLoaded = a.MaxLoaded }).ToList() };
        Replace(Of(m).MovingCases, x => x.Name, c);
        return c;
    }

    /// <summary>Deletes a lane, vehicle or vehicle class, refusing while something uses it.</summary>
    public static void DeleteMoving(StructuralModel m, string kind, string name)
    {
        var x = m.Extras ?? throw new ArgumentException($"There is no {kind} '{name}'.");
        string? user = kind switch
        {
            "lane" => x.MovingCases.FirstOrDefault(c => c.Assignments.Any(a => a.Lanes.Contains(name)))?.Name,
            "vehicle" => x.VehicleClasses.FirstOrDefault(c => c.Vehicles.Any(v => v.Vehicle == name))?.Name,
            "vehicle class" => x.MovingCases.FirstOrDefault(c => c.Assignments.Any(a => a.Class == name))?.Name,
            _ => null,
        };
        if (user is not null) throw new ArgumentException($"The {kind} '{name}' is used by {user}; change that first.");
        var removed = kind switch
        {
            "lane" => x.Lanes.RemoveAll(o => o.Name == name),
            "vehicle" => x.Vehicles.RemoveAll(o => o.Name == name),
            "vehicle class" => x.VehicleClasses.RemoveAll(o => o.Name == name),
            "moving load case" => x.MovingCases.RemoveAll(o => o.Name == name),
            _ => throw new ArgumentException($"Nothing of the kind '{kind}'."),
        };
        if (removed == 0) throw new ArgumentException($"There is no {kind} '{name}'.");
        Tidy(m);
    }

    // ---- design strips ----

    public static DesignStrip SetStrip(StructuralModel m, string name, string layer, IReadOnlyList<double[]> points, double left, double right)
    {
        Name(name, "strip");
        layer = Pick(["A", "B"], layer, "strip layer");
        if (points.Count < 2) throw new ArgumentException("A strip runs through at least two points (x, y).");
        if (points.Any(p => p.Length != 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) throw new ArgumentException("Each strip point is [x, y].");
        for (var i = 1; i < points.Count; i++)
            if (Math.Abs(points[i][0] - points[i - 1][0]) + Math.Abs(points[i][1] - points[i - 1][1]) < 1e-9) throw new ArgumentException("Two strip points in a row are the same point.");
        Positive(left, "strip's width to the left"); Positive(right, "strip's width to the right");
        if (left + right <= 0) throw new ArgumentException("A strip has a width.");
        var s = new DesignStrip { Name = name, Layer = layer, Points = points.Select(p => new[] { p[0], p[1] }).ToList(), WidthLeft = left, WidthRight = right };
        Replace(Of(m).Strips, x => x.Name, s);
        return s;
    }

    public static void DeleteStrip(StructuralModel m, string name)
    {
        if (m.Extras?.Strips.RemoveAll(s => s.Name == name) is not > 0) throw new ArgumentException($"There is no strip '{name}'.");
        Tidy(m);
    }
}
