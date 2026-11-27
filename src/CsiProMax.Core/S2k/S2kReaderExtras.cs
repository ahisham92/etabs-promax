using System.Globalization;
using CsiProMax.Model;
using static CsiProMax.E2k.E2kText;

namespace CsiProMax.S2k;

/// <summary>
/// The part of a SAP2000 file beyond members, sections and loads: groups, joint constraints of every type, joint
/// springs, link properties, auto hinges, the mass source and moving loads (lanes, vehicles, vehicle classes, moving
/// load cases). Each goes where the Modeler keeps it, so a file SAP2000 wrote opens with all of it.
/// </summary>
public static partial class S2kReader
{
    private static void Extras(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, List<Dictionary<string, string>>> t,
        Dictionary<string, Xyz> joints, Func<string, string> jointAt, Dictionary<string, (string Label, string Story)> frameAt,
        Dictionary<string, (string Label, string Story)> areaAt, List<string> stories, List<string> notes)
    {
        MassSourceOf(m, use("MASS SOURCE"));
        Groups(m, use("GROUPS 1 - DEFINITIONS"), use("GROUPS 2 - ASSIGNMENTS"), joints, jointAt, frameAt, areaAt, stories);
        Constraints(m, use, t, joints, jointAt, notes);
        Springs(m, use("JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED"), joints, jointAt, notes);
        Links(m, use, t, notes);
        Hinges(m, use, t, frameAt, notes);
        Moving(m, use, frameAt, notes);
        ExtrasDefs.Tidy(m);
    }

    private static void MassSourceOf(StructuralModel m, List<Dictionary<string, string>> rows)
    {
        if (rows.Count == 0) return;
        var name = rows.FirstOrDefault(r => Yes(r, "IsDefault")) is { } d ? S(d, "MassSource") : S(rows[0], "MassSource");
        var mine = rows.Where(r => S(r, "MassSource") == name).ToList();
        var loads = mine.Where(r => Yes(r, "Loads") && S(r, "LoadPat").Length > 0).Select(r => (S(r, "LoadPat"), D(r, "Multiplier", 1))).ToList();
        new Lateral.MassSource(name.Length > 0 ? name : "MsSrc1", !mine.Exists(r => S(r, "Elements").Equals("No", StringComparison.OrdinalIgnoreCase)),
            !mine.Exists(r => S(r, "Masses").Equals("No", StringComparison.OrdinalIgnoreCase)), loads).WriteTo(m);
    }

    private static readonly string[] GroupColors = ["Red", "Green", "Blue", "Yellow", "Cyan", "Magenta", "Gray", "White", "Black", "Orange"];

    private static void Groups(StructuralModel m, List<Dictionary<string, string>> defs, List<Dictionary<string, string>> members, Dictionary<string, Xyz> joints,
        Func<string, string> jointAt, Dictionary<string, (string Label, string Story)> frameAt, Dictionary<string, (string Label, string Story)> areaAt, List<string> stories)
    {
        // "ALL" is SAP2000's own; a group named for a story is the one CSI ProMax writes for it.
        bool Skip(string g) => g.Length == 0 || g.Equals("ALL", StringComparison.OrdinalIgnoreCase) || stories.Exists(s => s.Equals(g, StringComparison.OrdinalIgnoreCase));
        var lines = new List<string>();
        var names = defs.Select(r => S(r, "GroupName")).Concat(members.Select(r => S(r, "GroupName"))).Where(g => !Skip(g)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var g in names)
        {
            var c = defs.Find(r => S(r, "GroupName") == g) is { } r0 ? S(r0, "Color") : "";
            lines.Add("  " + Line(["GROUP", Quote(g), "COLOR", Quote(GroupColors.FirstOrDefault(x => x.Equals(c, StringComparison.OrdinalIgnoreCase)) ?? "Red")]));
        }
        foreach (var r in members)
        {
            var g = S(r, "GroupName");
            if (Skip(g)) continue;
            var label = S(r, "ObjectLabel");
            var type = S(r, "ObjectType").ToUpperInvariant();
            (string, string, string)? member = type switch
            {
                "JOINT" when joints.ContainsKey(label) => jointAt(label).Split('@') is var j ? ("POINT", j[0], j[1]) : null,
                "FRAME" when frameAt.TryGetValue(label, out var f) => ("LINE", f.Label, f.Story),
                "AREA" when areaAt.TryGetValue(label, out var a) => ("AREA", a.Label, a.Story),
                _ => null,
            };
            if (member is var (k, l, s)) lines.Add("  " + Line(["GROUP", Quote(g), k, Quote(l), Quote(s)]));
        }
        if (lines.Count > 0) m.Passthrough.Add(new RawSection { Heading = GroupDefs.Heading, After = "AREA ASSIGNS", Lines = lines.Distinct().ToList() });
    }

    private static void Constraints(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, List<Dictionary<string, string>>> t,
        Dictionary<string, Xyz> joints, Func<string, string> jointAt, List<string> notes)
    {
        // Diaphragms are read with the floors; every other type is a constraint of its own.
        var assigned = use("JOINT CONSTRAINT ASSIGNMENTS").Where(r => !S(r, "Type").Equals("Diaphragm", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var type in ExtrasDefs.ConstraintTypes.Where(x => x != "Diaphragm"))
        {
            foreach (var r in use("CONSTRAINT DEFINITIONS - " + type.ToUpperInvariant()))
            {
                var name = S(r, "Name");
                var dofs = (type == "Local" ? ExtrasDefs.LocalDofs : ExtrasDefs.GlobalDofs).Where(d => Yes(r, d)).ToList();
                try
                {
                    ExtrasDefs.SetConstraint(m, name, type, S(r, "Axis"), ExtrasDefs.HasDofs(type) && dofs.Count == 0 ? null : dofs, type == "Weld" ? Math.Max(D(r, "Tolerance"), 1e-9) : 0);
                    var mine = assigned.Where(a => S(a, "Constraint") == name && joints.ContainsKey(S(a, "Joint"))).Select(a => jointAt(S(a, "Joint"))).ToList();
                    if (mine.Count > 0) ExtrasDefs.AssignConstraint(m, mine, name);
                }
                catch (ArgumentException ex) { notes.Add($"Constraint {name}: {ex.Message}"); }
            }
        }
    }

    private static void Springs(StructuralModel m, List<Dictionary<string, string>> rows, Dictionary<string, Xyz> joints, Func<string, string> jointAt, List<string> notes)
    {
        // SAP2000 keeps a joint's stiffness on the joint; the Modeler keeps a named point spring, one for each set of values.
        var byValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var j = S(r, "Joint");
            if (!joints.ContainsKey(j)) continue;
            var values = new[] { ("UX", "U1"), ("UY", "U2"), ("UZ", "U3"), ("RX", "R1"), ("RY", "R2"), ("RZ", "R3") }.ToDictionary(x => x.Item1, x => D(r, x.Item2));
            if (!values.Values.Any(v => v > 0)) continue;
            var key = string.Join(",", values.Values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            if (!byValues.TryGetValue(key, out var name))
            {
                name = "PS" + (byValues.Count + 1).ToString(CultureInfo.InvariantCulture);
                try { PropertyDefs.SetSpring(m, "point", name, values); } catch (ArgumentException ex) { notes.Add($"Spring at joint {j}: {ex.Message}"); continue; }
                byValues[key] = name;
            }
            var at = jointAt(j).Split('@');
            var pa = m.PointAssigns.Find(p => p.Point == at[0] && p.Story == at[1]);
            if (pa is null) m.PointAssigns.Add(pa = new PointAssign { Point = at[0], Story = at[1] });
            PropertyDefs.Put(pa.Extra, "SPRINGPROP", Quote(name));
        }
    }

    private static void Links(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, List<Dictionary<string, string>>> t, List<string> notes)
    {
        var general = use("LINK PROPERTY DEFINITIONS 01 - GENERAL");
        if (general.Count == 0) return;
        // SAP2000's own link rows are kept as they are (every type's every field), for Link/Support Property Data.
        foreach (var table in DefineTables.SapHinges.Keys.Where(k => k.StartsWith("Link Property Definitions ", StringComparison.OrdinalIgnoreCase) && char.IsDigit(k["Link Property Definitions ".Length])))
        {
            var fields = DefineTables.SapHinges[table];
            var kept = use(table.ToUpperInvariant()).Select(r => r.Where(kv => kv.Value.Length > 0 && fields.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kv => fields.First(f => f.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), kv => kv.Value, StringComparer.Ordinal)).Where(r => r.Count > 0).ToList();
            if (kept.Count == 0) continue;
            m.DefinitionTables ??= new(StringComparer.OrdinalIgnoreCase);
            m.DefinitionTables[table] = kept;
        }
        // Each direction's effective stiffness and damping, from whichever of SAP2000's link tables holds it.
        var dofRows = t.Keys.Where(k => k.StartsWith("LINK PROPERTY DEFINITIONS", StringComparison.OrdinalIgnoreCase) && k != "LINK PROPERTY DEFINITIONS 01 - GENERAL")
            .SelectMany(k => use(k)).Where(r => S(r, "DOF").Length > 0).ToList();
        foreach (var r in general)
        {
            var name = S(r, "Link");
            var type = S(r, "LinkType");
            var dofs = new Dictionary<string, (double, double)>();
            foreach (var d in dofRows.Where(x => S(x, "Link") == name))
            {
                var dof = S(d, "DOF").ToUpperInvariant();
                if (!PropertyDefs.LinkDofs.Contains(dof) || dofs.ContainsKey(dof)) continue;
                dofs[dof] = (Math.Max(0, D(d, "TransKE", D(d, "RotKE"))), Math.Max(0, D(d, "TransCE", D(d, "RotCE"))));
            }
            if (dofs.Count == 0) dofs["U1"] = (0, 0);
            try { PropertyDefs.SetLink(m, name, PropertyDefs.EngineLinkType(type), dofs); }
            catch (ArgumentException ex) { notes.Add($"Link property {name}: {ex.Message}"); }
        }
    }

    private static void Hinges(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, List<Dictionary<string, string>>> t,
        Dictionary<string, (string Label, string Story)> frameAt, List<string> notes)
    {
        var rows = t.Keys.Where(k => k.StartsWith("FRAME HINGE ASSIGNS", StringComparison.OrdinalIgnoreCase)).SelectMany(k => use(k))
            .Where(r => S(r, "DOF").Length > 0 && frameAt.ContainsKey(S(r, "Frame"))).ToList();
        foreach (var g in rows.GroupBy(r => S(r, "Frame")))
        {
            var (label, story) = frameAt[g.Key];
            var hinges = g.Select(r => new PropertyDefs.HingeDef(D(r, "RelDist"), PropertyDefs.HingeDofs.FirstOrDefault(h => h.Replace("-", "") == S(r, "DOF").Replace("-", "").ToUpperInvariant()) ?? S(r, "DOF")))
                .GroupBy(h => (Math.Round(h.At, 6), h.Dof)).Select(x => x.First()).ToList();
            try { PropertyDefs.SetHinges(m, label, story, hinges); }
            catch (ArgumentException ex) { notes.Add($"Hinges on frame {g.Key}: {ex.Message}"); }
        }
    }

    private static void Moving(StructuralModel m, Func<string, List<Dictionary<string, string>>> use, Dictionary<string, (string Label, string Story)> frameAt, List<string> notes)
    {
        void Try(string what, Action a) { try { a(); } catch (ArgumentException ex) { notes.Add($"{what}: {ex.Message}"); } }
        foreach (var lane in use("LANE DEFINITION DATA").GroupBy(r => S(r, "Lane")))
        {
            var frames = lane.Where(r => frameAt.ContainsKey(S(r, "Frame"))).Select(r => frameAt[S(r, "Frame")]).Select(f => f.Label + "@" + f.Story).ToList();
            var first = lane.First();
            if (frames.Count > 0) Try("Lane " + lane.Key, () => ExtrasDefs.SetLane(m, lane.Key, frames, D(first, "Offset"), D(first, "Width")));
        }
        foreach (var r in use("VEHICLES 1 - STANDARD VEHICLES"))
            Try("Vehicle " + S(r, "VehName"), () => ExtrasDefs.SetVehicle(m, S(r, "VehName"), S(r, "Type"), D(r, "ScaleFactor", 1), [], 0));
        use("VEHICLES 2 - GENERAL VEHICLES 1 - GENERAL");
        foreach (var v in use("VEHICLES 3 - GENERAL VEHICLES 2 - LOADS").GroupBy(r => S(r, "VehName")))
        {
            var axles = new List<Axle>();
            var uniform = 0.0;
            foreach (var r in v)
            {
                var kind = S(r, "LoadType");
                if (kind.StartsWith("Leading", StringComparison.OrdinalIgnoreCase)) axles.Add(new Axle { Load = D(r, "InterAxle"), Spacing = 0 });
                else if (kind.StartsWith("Trailing", StringComparison.OrdinalIgnoreCase)) continue;
                else { axles.Add(new Axle { Load = D(r, "InterAxle"), Spacing = D(r, "InterMinD", D(r, "InterMaxD")) }); uniform = Math.Max(uniform, D(r, "InterUnif")); }
            }
            Try("Vehicle " + v.Key, () => ExtrasDefs.SetVehicle(m, v.Key, "", 1, axles, uniform));
        }
        foreach (var c in use("VEHICLES 4 - VEHICLE CLASSES").GroupBy(r => S(r, "VehClass")))
            Try("Vehicle class " + c.Key, () => ExtrasDefs.SetVehicleClass(m, c.Key, c.Select(r => new ClassVehicle { Vehicle = S(r, "VehName"), Scale = D(r, "ScaleFactor", 1) }).ToList()));
        var lanesLoaded = use("CASE - MOVING LOAD 2 - LANES LOADED").ToLookup(r => (S(r, "Case"), S(r, "AssignNum")));
        foreach (var c in use("CASE - MOVING LOAD 1 - LANE ASSIGNMENTS").GroupBy(r => S(r, "Case")))
        {
            var assigns = c.Select(r => new MovingAssign
            {
                Class = S(r, "VehClass"), Scale = D(r, "ScaleFactor", 1), MinLoaded = (int)D(r, "MinLoaded", 1), MaxLoaded = (int)D(r, "MaxLoaded"),
                Lanes = lanesLoaded[(c.Key, S(r, "AssignNum"))].Select(x => S(x, "Lane")).ToList(),
            }).ToList();
            // MaxLoaded equal to the number of lanes is the default (0: all of them).
            foreach (var a in assigns) if (a.MaxLoaded == a.Lanes.Count) a.MaxLoaded = 0;
            Try("Moving load case " + c.Key, () => ExtrasDefs.SetMovingCase(m, c.Key, assigns));
        }
    }
}
